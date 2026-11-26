// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Resolves a DOCX font family (+ bold/italic + the code points it must cover)
    /// to a <see cref="TrueTypeFontProgram"/>. Hybrid strategy:
    ///   1. the OS font matching the requested family/style,
    ///   2. any OS font that covers the required code points (broad-coverage fallback,
    ///      so Hebrew/Arabic still render even when the declared family lacks them),
    ///   3. a bundled Unicode font shipped as an assembly resource (if present),
    /// returning null only when nothing covers the code points (caller keeps the
    /// standard-14 path). Results are cached.
    ///
    /// <para><b>The <c>OS/2</c> <c>fsType</c> gate is applied to host fonts.</b> This said "an
    /// embeddable program" and did not check, so a face the foundry marks <i>Restricted License
    /// embedding</i> was picked up off the machine and shipped inside the PDF, while
    /// <c>Builder.Export.BuilderFontResolver.HostCandidate</c> refused the same file — the same
    /// font on the same machine, opposite answers from the two converters.</para>
    ///
    /// <para><b>The gate has an escape hatch.</b> Refusing changes layout (the run falls back to
    /// standard-14), so a gate with no way through would leave a deployment that <i>does</i> hold the
    /// licence stuck. <c>DocxToPdfOptions</c> carries
    /// <c>AllowRestrictedSystemFonts</c>, the counterpart of Builder's
    /// <c>PdfExportOptions.FontProvider</c> and Core's
    /// <c>FontResolutionRequest.AllowRestrictedSystemFonts</c>; the flag reaches this static class
    /// as a parameter and is part of the cache key, so a permissive conversion cannot answer a
    /// later strict one. A refusal is reported, never silent.</para>
    ///
    /// <para><b>A font embedded in the DOCX is not gated</b>, deliberately: both call sites try it
    /// first, and its author may hold a licence the bits know nothing about. That is
    /// <c>Core.Fonts.FontEmbeddingLicense</c>'s rule — report for a caller-supplied program, skip
    /// for a system-substituted one.</para>
    /// </summary>
    internal static class FontResolver
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// The file each resolution answered with (null for none), by family, style and code-point
        /// set. A path, never the program: the programs live in <see cref="FileCache"/>, under its
        /// ceiling, and a resolution memoised as a program would pin a copy of every face the ceiling
        /// refused for the life of the process, one a distinct run.
        /// </summary>
        private static readonly Dictionary<string, string> ResolveCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, TrueTypeFontProgram> FileCache =
            new Dictionary<string, TrueTypeFontProgram>(StringComparer.OrdinalIgnoreCase);
        private static List<string> _fontFiles; // lazily-scanned OS font paths

        /// <summary>
        /// A host font covering <paramref name="requiredCodePoints"/>, or null.
        /// </summary>
        /// <param name="family">The requested family.</param>
        /// <param name="bold">Whether a bold face is wanted.</param>
        /// <param name="italic">Whether an italic face is wanted.</param>
        /// <param name="requiredCodePoints">The code points the run draws.</param>
        /// <param name="allowRestricted">
        /// <c>DocxToPdfOptions.AllowRestrictedSystemFonts</c>: embed a face whose <c>OS/2</c>
        /// <c>fsType</c> forbids it. Default false, and the flag is part of the cache key — a
        /// permissive call must not answer a later strict one.
        /// </param>
        /// <remarks>
        /// <b>The licence gate is here and not at the call sites</b>, because this is the only
        /// place that reads a font off the machine. A font embedded in the DOCX is resolved before
        /// this is reached and is deliberately not policed: its author put it there and may hold a
        /// licence the bits know nothing about, which is the distinction
        /// <c>Core.Fonts.FontEmbeddingLicense</c> states — report for a caller-supplied program,
        /// skip for a system-substituted one.
        /// </remarks>
        /// <param name="documentPrograms">
        /// The programs one document has taken, by file (<c>DocxRenderContext.ResolveHostFont</c>
        /// passes its own): a file the document already holds is answered with that program, and a
        /// new one is added. A document deduplicates its embedded fonts by program identity
        /// (<c>DocxRenderContext.RegisterEmbeddedFont</c>), and <see cref="FileCache"/> past its
        /// ceiling hands back a new program for every load, so without the map one face would be
        /// embedded once a run. Null answers from the file cache alone.
        /// </param>
        internal static TrueTypeFontProgram Resolve(string family, bool bold, bool italic,
            IEnumerable<int> requiredCodePoints, bool allowRestricted = false,
            IDictionary<string, TrueTypeFontProgram> documentPrograms = null)
        {
            int[] required = RequiredCodePoints(requiredCodePoints);
            // The flag is in the key for the reason the caches exist at all: they are static and
            // process-wide, so one conversion that allows restricted fonts would otherwise hand its
            // answer to the next conversion that does not.
            string key = CacheKey(family, bold, italic, required) + (allowRestricted ? "|R" : "");

            lock (Gate)
            {
                if (ResolveCache.TryGetValue(key, out string cachedPath))
                    return cachedPath == null ? null : TheDocumentsProgram(cachedPath, null, documentPrograms);
                var result = ResolveUncached(family, bold, italic, required, allowRestricted, out string path);
                ResolveCache[key] = path;
                return path == null ? result : TheDocumentsProgram(path, result, documentPrograms);
            }
        }

        /// <summary>
        /// The program <paramref name="documentPrograms"/> holds for <paramref name="path"/>; else
        /// <paramref name="loaded"/>, or the file read through <see cref="FileCache"/>, kept there
        /// for the document's next request.
        /// </summary>
        private static TrueTypeFontProgram TheDocumentsProgram(
            string path, TrueTypeFontProgram loaded, IDictionary<string, TrueTypeFontProgram> documentPrograms)
        {
            if (documentPrograms != null && documentPrograms.TryGetValue(path, out var held)) return held;
            var program = loaded ?? LoadFile(path);
            if (documentPrograms != null && program != null) documentPrograms[path] = program;
            return program;
        }

        /// <summary>Drops the programs <see cref="FileCache"/> holds and their charge, the resolutions kept: a test's way to see that a resolution holds no program of its own.</summary>
        internal static void ClearFileCacheForTest()
        {
            lock (FileCache)
            {
                FileCache.Clear();
                _chargedBytes = 0;
            }
        }

        /// <summary>
        /// The code points a candidate font must genuinely have a glyph for: distinct, and with
        /// space, tab, CR and LF removed. <b>One implementation</b> — <see cref="Resolve"/>,
        /// <c>DocxRenderContext.ResolveEmbeddedFont</c> and <c>MathMeasure.Embedded</c> all ask this
        /// question and must not answer it three ways.
        /// </summary>
        /// <remarks>
        /// <para><b>Why whitespace is removed.</b> <c>RunRenderer</c> puts whitespace into the very
        /// same string as the text, because <c>&lt;w:br/&gt;</c> becomes <c>'\n'</c> and
        /// <c>&lt;w:tab/&gt;</c> becomes <c>'\t'</c> (RunRenderer.Render). Most faces map neither
        /// U+0009 nor U+000A at all, so demanding a <c>cmap</c> entry for every member of a bare
        /// <c>.Distinct()</c> would make a Hebrew or CJK run holding a tab or a line break ask for
        /// coverage no font can give: <see cref="Covers"/> would return false for every candidate,
        /// <see cref="Resolve"/> null, and the run fall through to the standard-14 path where
        /// <c>RunRenderer.EncodeWinAnsiLiteral</c> maps every non-WinAnsi character to a space — a
        /// page of correctly-sized BLANK lines, for text a font on the machine could draw.</para>
        ///
        /// <para><b>Such a failure is indistinguishable from the one it imitates.</b> Losing the run
        /// is exactly what happens when nothing on the host covers Hebrew, which on a bare container
        /// is genuinely the case, and <c>ReportNoCoveringFont</c> filters the reported code points
        /// through <c>IsWinAnsiCodePoint</c> — so the diagnostic would name the Hebrew letters and
        /// never mention the tab. Whitespace is invisible in the output, so no rendering test could
        /// see the difference either.</para>
        ///
        /// <para><b>Why excluding it is free.</b> Nearly every font has a space, so requiring one
        /// never discriminates between candidates, and <c>UnicodeText.Shape</c> already falls back to
        /// the space glyph for anything the cmap misses. This is the same four-member rule, for the
        /// same stated reason, as <c>Core.Fonts.SystemFontResolver.Distinct</c>.</para>
        /// </remarks>
        internal static int[] RequiredCodePoints(IEnumerable<int> codePoints)
        {
            if (codePoints == null) return Array.Empty<int>();

            var seen = new HashSet<int>();
            var kept = new List<int>();
            foreach (int cp in codePoints)
            {
                if (cp == ' ' || cp == '\t' || cp == '\r' || cp == '\n') continue;
                if (seen.Add(cp)) kept.Add(cp);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// A key over the <b>full</b> required code-point set, not a summary of it.
        ///
        /// <para>
        /// A summary (the count and the lowest member, say) would let two runs whose distinct
        /// code-point sets merely match in it share one entry, and the cached program would be
        /// handed back without re-checking <see cref="Covers"/>: "אב" (U+05D0, U+05D1) resolves to a
        /// Hebrew-only face, and "א€" (U+05D0, U+20AC) would then be drawn in it, <c>UnicodeText.Shape</c>
        /// substituting the space glyph for the euro sign with nothing reported, while a font covering
        /// it sits on the machine.
        /// </para>
        ///
        /// <para>Sorted, so the key does not depend on the order the characters appear in.</para>
        /// </summary>
        internal static string CacheKey(string family, bool bold, bool italic, int[] required)
        {
            Array.Sort(required);
            var sb = new System.Text.StringBuilder((family?.Length ?? 0) + required.Length * 6 + 8);
            sb.Append(family).Append('|').Append(bold ? '1' : '0').Append(italic ? '1' : '0');
            foreach (int cp in required)
                sb.Append('|').Append(cp.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>The program for a run, and in <paramref name="path"/> the file it came from (null for none), which the resolution remembers.</summary>
        private static TrueTypeFontProgram ResolveUncached(
            string family, bool bold, bool italic, int[] required, bool allowRestricted, out string path)
        {
            // 1. Try the requested family from the OS by known filename candidates. Skipped when a
            //    test has supplied its own font list, so the machine's real fonts cannot answer —
            //    see UseHostFontFilesForTest for why that seam exists.
            if (_hostFontFilesForTest == null)
            {
                foreach (var candidate in CandidateFiles(family, bold, italic))
                {
                    var prog = LoadFile(candidate);
                    if (prog != null && Covers(prog, required) && Embeddable(prog, candidate, allowRestricted))
                    {
                        path = candidate;
                        return prog;
                    }
                }
            }

            // 2. Any OS font that covers the required code points (prefer broad Unicode
            //    fonts like Arial / Arial Unicode), so the script renders regardless.
            var byCoverage = FindAnyCovering(required, bold, italic, allowRestricted, out path);
            if (byCoverage != null) return byCoverage;

            // No font ships in this package (Plugins.Fonts registers its own through Core.Fonts.FontSearchPath,
            // which the two searches above already read): a lookup of an embedded fallback here was dead.
            path = null;
            return null;
        }

        /// <summary>
        /// Whether <paramref name="prog"/>, picked up off the host, may be embedded — its
        /// <c>OS/2</c> <c>fsType</c> per <see cref="FontEmbeddingLicense"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Refusing changes layout, so it is reported rather than silent.</b> The run
        /// falls through to the standard-14 path, which is a visible difference with no exception
        /// attached — exactly the shape a caller cannot diagnose unless told. The message names the
        /// file and the licence, and points at the option that lets a deployment holding the
        /// licence through.</para>
        ///
        /// <para>Builder made this decision at <c>BuilderFontResolver.HostCandidate</c> and this
        /// engine did not, so the same machine and the same font gave opposite answers depending on
        /// which converter the caller used.</para>
        /// </remarks>
        private static bool Embeddable(TrueTypeFontProgram prog, string path, bool allowRestricted)
        {
            if (allowRestricted) return true;

            var licence = prog.EmbeddingLicense;
            if (licence.PermitsOutlineEmbedding) return true;

            DocxDiagnostics.Report("FontResolver.RestrictedLicence",
                $"the host font '{path}' was not embedded: {licence.Describe()}. Set " +
                "DocxToPdfOptions.AllowRestrictedSystemFonts if this deployment holds a licence " +
                "permitting it; the text falls back to a standard-14 face.");
            return false;
        }

        private static bool Covers(TrueTypeFontProgram prog, int[] required)
        {
            if (required.Length == 0) return true;
            foreach (int cp in required)
                if (!prog.CoversShaped(cp)) return false;
            return true;
        }

        // ---- OS font directories / candidate filenames ----

        // Shared with Builder's resolver and Base's FontCache — see Core.Fonts.FontFileLocator.
        private static IEnumerable<string> FontDirectories()
            => FontFileLocator.Directories;

        // includeOpenType: true — CFF/OTF embeds via Type0FontBuilder (CIDFontType0 + FontFile3).
        private static List<string> AllFontFiles()
        {
            if (_hostFontFilesForTest != null) return _hostFontFilesForTest;
            if (_fontFiles != null) return _fontFiles;
            _fontFiles = new List<string>(FontFileLocator.EnumerateFontFiles(includeOpenType: true));
            return _fontFiles;
        }

        [ThreadStatic] private static List<string> _hostFontFilesForTest;

        /// <summary>
        /// Replaces the machine's font list for the duration of a test, and clears the caches on
        /// the way in and out.
        /// </summary>
        /// <remarks>
        /// <para><b>The seam exists because the alternative is an assertion about the machine.</b>
        /// The <c>fsType</c> gate can only be observed by resolving a font that declares a
        /// restrictive licence, and no CI container is guaranteed to have one — a test that hunts
        /// for such a font on the host either skips silently or asserts whatever happens to be
        /// installed. <c>BuilderFontResolver.HostFontFilesForTest</c> exists for the same reason.</para>
        ///
        /// <para><b>The caches must be cleared, and they are static.</b> They key on family and
        /// coverage, so an entry resolved from the real machine would otherwise answer a test that
        /// has replaced the font list — and a test's entry would outlive it. <c>[ThreadStatic]</c>
        /// on the list itself keeps a parallel run from reading another test's fonts, which is the
        /// concurrency rule every <c>Last*</c> slot in this repository follows.</para>
        /// </remarks>
        internal static IDisposable UseHostFontFilesForTest(IEnumerable<string> paths)
            => new HostFontFileScope(paths);

        private sealed class HostFontFileScope : IDisposable
        {
            internal HostFontFileScope(IEnumerable<string> paths)
            {
                ClearCaches();
                _hostFontFilesForTest = paths == null ? new List<string>() : new List<string>(paths);
            }

            public void Dispose()
            {
                _hostFontFilesForTest = null;
                ClearCaches();
            }

            // Each cache under the lock its readers take: LoadFile reads and writes the file cache under
            // its own lock, so a clear under Gate could meet another thread's write; and the charge goes
            // with the entries, or a few scopes would spend the budget on entries no longer held and
            // nothing more would be cached.
            private static void ClearCaches()
            {
                lock (Gate) ResolveCache.Clear();
                lock (FileCache)
                {
                    FileCache.Clear();
                    _chargedBytes = 0;
                }
            }
        }

        // Common DOCX families → likely OS filenames (Windows naming).
        private static readonly Dictionary<string, string> KnownFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["arial"] = "arial", ["helvetica"] = "arial", ["liberation sans"] = "arial",
                ["times new roman"] = "times", ["times"] = "times", ["liberation serif"] = "times",
                ["courier new"] = "cour", ["courier"] = "cour", ["consolas"] = "consola",
                ["calibri"] = "calibri", ["cambria"] = "cambria", ["tahoma"] = "tahoma",
                ["segoe ui"] = "segoeui", ["verdana"] = "verdana", ["georgia"] = "georgia",
                ["david"] = "david", ["frank ruehl"] = "frank", ["narkisim"] = "nrkis",
                ["arial unicode ms"] = "arialuni",
            };

        private static IEnumerable<string> CandidateFiles(string family, bool bold, bool italic)
        {
            string baseName = null;
            if (!string.IsNullOrEmpty(family) && KnownFiles.TryGetValue(family.Trim(), out var b))
                baseName = b;

            var stems = new List<string>();
            if (baseName != null)
            {
                string suffix = (bold ? "bd" : "") + (italic ? "i" : "");
                if (suffix.Length > 0) stems.Add(baseName + suffix);
                if (bold && italic) stems.Add(baseName + "bi");
                stems.Add(baseName);
            }

            foreach (var dir in FontDirectories())
                foreach (var stem in stems)
                    foreach (var ext in new[] { ".ttf", ".otf", ".ttc" })
                    {
                        string p = Path.Combine(dir, stem + ext);
                        if (File.Exists(p)) yield return p;
                    }

            // Match by the font's actual family name when the filename map missed.
            //
            // This is the scan that actually costs. KnownFiles has 17 entries, so any family outside
            // it — Aptos, Calibri Light, anything a modern Word installs — lands here, and a LoadFile
            // of every installed font would be a full read and parse of the machine's whole font
            // collection, measured at 210 MiB for a one-paragraph document. "Nothing covers the text"
            // is the rare case; this is the common one.
            //
            // FontCoverageProbe.FamilyName reads the sfnt directory and the `name` table only, and
            // gets the answer from the same TrueTypeFontProgram.FamilyName the full read would. A
            // null means "could not tell cheaply", which must fall through to the full read rather
            // than be treated as a non-match — otherwise a font that only the full parser can name
            // would silently stop being a candidate.
            if (!string.IsNullOrEmpty(family))
            {
                string norm = Normalize(family);
                foreach (var path in AllFontFiles())
                {
                    string probed = FontCoverageProbe.FamilyName(path);
                    if (probed != null)
                    {
                        System.Threading.Interlocked.Increment(ref _probeFamilyNames);
                        if (Normalize(probed) == norm) yield return path;
                        continue;
                    }

                    var prog = LoadFile(path);
                    if (prog?.FamilyName != null && Normalize(prog.FamilyName) == norm)
                        yield return path;
                }
            }
        }

        /// <summary>
        /// A cheap, sound rejection: reads the sfnt table directory and the <c>cmap</c> instead of the
        /// whole file, and reaches its verdict through the very same
        /// <see cref="TrueTypeFontProgram.Covers"/> the full read would.
        ///
        /// <para><b>It may only ever reject.</b> "Cannot decide" and "covers" both fall through to
        /// <see cref="LoadFile"/>, so the shipping choice is always made by a real program — the probe
        /// removes reads, never decisions. Measured across this scan: 395 ms and 461 MB down to 34 ms
        /// and 3.6 MB (GAPS §16 item 4a); parity with a full parse over every installed font is
        /// asserted by <c>FontCoverageProbeParityTests</c>.</para>
        /// </summary>
        private static bool CannotCover(string path, int[] required)
        {
            if (FontCoverageProbe.Covers(path, required) != false) return false;
            System.Threading.Interlocked.Increment(ref _probeRejections);
            return true;
        }

        private static long _probeRejections;

        /// <summary>
        /// How many candidates the cheap probe has rejected without a full read.
        ///
        /// <para>Exposed because it is the only <b>order-independent</b> evidence that the probe is
        /// wired in. The obvious measure — how many bytes a conversion added to
        /// <see cref="ChargedBytes"/> — is saturable: a sibling test that fills the cache to
        /// <see cref="MaxCachedFontBytes"/> first makes every later conversion appear to read nothing,
        /// so the assertion silently stops being about the probe. That is the process-wide-state trap
        /// recorded in GAPS §14, and it caught this file's first draft. A rejection is counted whether
        /// or not the file was already cached, so the count does not depend on what ran before.</para>
        /// </summary>
        internal static long ProbeRejections => System.Threading.Interlocked.Read(ref _probeRejections);

        private static long _probeFamilyNames;

        /// <summary>
        /// How many candidates the family-name match answered from the <c>name</c> table alone.
        ///
        /// <para>Its own counter rather than a share of <see cref="ProbeRejections"/>, because the two
        /// wire into different loops and a single number cannot fail for one of them. Without it, a
        /// change that stopped consulting <c>FamilyName</c> would be caught by nothing. Counted
        /// whether or not the path was already in
        /// <see cref="FileCache"/>, so it does not depend on what ran before.</para>
        /// </summary>
        internal static long ProbeFamilyNames => System.Threading.Interlocked.Read(ref _probeFamilyNames);

        private static TrueTypeFontProgram FindAnyCovering(int[] required, bool bold, bool italic, bool allowRestricted, out string path)
        {
            path = null;
            if (required.Length == 0) return null;
            // Prefer well-known broad-coverage fonts first.
            foreach (var preferred in new[] { "arialuni", "arial", "tahoma", "segoeui", "DejaVuSans", "NotoSans" })
                foreach (var candidate in AllFontFiles())
                    if (Path.GetFileNameWithoutExtension(candidate).IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (CannotCover(candidate, required)) continue;
                        var prog = LoadFile(candidate);
                        if (prog != null && Covers(prog, required) && Embeddable(prog, candidate, allowRestricted))
                        {
                            path = candidate;
                            return prog;
                        }
                    }

            foreach (var candidate in AllFontFiles())
            {
                if (CannotCover(candidate, required)) continue;
                var prog = LoadFile(candidate);
                if (prog != null && Covers(prog, required) && Embeddable(prog, candidate, allowRestricted))
                {
                    path = candidate;
                    return prog;
                }
            }
            return null;
        }

        /// <summary>
        /// Total <see cref="TrueTypeFontProgram.FontData"/> bytes <see cref="FileCache"/> is holding.
        /// Exposed for the bound's regression test, mirroring
        /// <c>ContentOperationCache.ChargedBytes</c>.
        /// </summary>
        internal static long ChargedBytes { get { lock (FileCache) return _chargedBytes; } }

        private static long _chargedBytes;

        /// <summary>
        /// Ceiling on retained font bytes, in the shape <c>ContentOperationCache.MaxBytes</c>
        /// established: once full the cache simply stops accepting entries — no eviction, no LRU.
        ///
        /// <para><b>Why a ceiling was needed at all.</b> <see cref="FindAnyCovering"/> walks every
        /// font on the machine when nothing covers the run's text, and every one it touched used to
        /// be cached forever: <see cref="TrueTypeFontProgram.Parse"/> retains the whole file as
        /// <c>FontData</c> because <c>/FontFile2</c> embedding needs it, and this dictionary is
        /// <c>static</c> and never evicted. Measured on a host with 441 font files, a
        /// <b>one-paragraph</b> DOCX ended up holding <b>425 MiB</b> of large-object arrays for the
        /// process lifetime. GAPS §16 item 4 and <c>FontScanBenchmarks</c> have the numbers.</para>
        ///
        /// <para>32 MiB rather than 16: a single broad-coverage face is large (Arial Unicode is
        /// ~23 MiB) and the fonts worth keeping hot are the ones a document actually draws.</para>
        ///
        /// <para><b>Refusing to cache never changes what a document embeds.</b> The program is handed
        /// back either way, but a refused file is read again on the next load, as a new program:
        /// <see cref="ResolveCache"/> memoises a resolution as the file it answered with, never the
        /// program. A document keeps the program it first took for each file (the
        /// <c>documentPrograms</c> map <see cref="Resolve"/> takes), so its runs share one program
        /// and one embedded font, and a full cache costs one read of a file per document.</para>
        /// </summary>
        internal const long MaxCachedFontBytes = 32L * 1024 * 1024;

        /// <summary>
        /// Empties <see cref="FileCache"/> and charges it to its ceiling until disposed, so every load
        /// meanwhile is refused and read afresh: a test's way to reach a full cache without 32 MiB of
        /// fonts. Disposing empties it again and clears the charge.
        /// </summary>
        internal static IDisposable FullFileCacheForTest() => new FullFileCacheScope();

        private sealed class FullFileCacheScope : IDisposable
        {
            internal FullFileCacheScope()
            {
                lock (FileCache)
                {
                    FileCache.Clear();
                    _chargedBytes = MaxCachedFontBytes;
                }
            }

            public void Dispose() => ClearFileCacheForTest();
        }

        /// <summary>
        /// <see cref="LoadFile"/>, reachable by a test — the same hook
        /// <c>BuilderFontResolver.LoadFontFileForTest</c> exposes, and for the same reason.
        /// </summary>
        /// <remarks>
        /// The ordinary route here is a scan of the OS font directories, and both that scan and the
        /// file cache are memoised for the process lifetime, so an end-to-end test of the loader's
        /// behaviour would depend on which test ran first. Driving it with a path of the test's own
        /// choosing asserts the same thing deterministically.
        /// </remarks>
        internal static TrueTypeFontProgram LoadFontFileForTest(string path) => LoadFile(path);

        /// <summary>
        /// The same program with the tables a PDF reader never consults removed, or <c>null</c> when
        /// pruning cannot help and the original should be used unchanged.
        /// </summary>
        /// <remarks>
        /// Re-parsed rather than patched, because <see cref="TrueTypeFontProgram.FontData"/> is what
        /// actually gets embedded and only <c>Parse</c> can set it. A prune whose output the parser
        /// then rejects counts as no prune at all: the original font is known to work, and a smaller
        /// broken one is not an improvement. Deliberately a copy of Builder's, not a shared helper —
        /// the two resolvers keep their own caches and bounds as independent constants in independent
        /// assemblies, and this is four lines of policy rather than an algorithm.
        /// </remarks>
        private static TrueTypeFontProgram Pruned(TrueTypeFontProgram program)
        {
            byte[] original = program.FontData;
            if (original == null || original.Length == 0) return null;

            byte[] pruned;
            try { pruned = SfntTablePruner.Prune(original, out _, out _); }
            catch { return null; }
            if (pruned == null || pruned.Length >= original.Length) return null;

            try { return TrueTypeFontProgram.Parse(pruned); }
            catch { return null; }
        }

        private static TrueTypeFontProgram LoadFile(string path)
        {
            lock (FileCache)
            {
                if (FileCache.TryGetValue(path, out var cached)) return cached;
                TrueTypeFontProgram prog = null;
                try
                {
                    if (File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        prog = TrueTypeFontProgram.Parse(bytes);
                        // CFF/OTF fonts embed via Type0FontBuilder (CIDFontType0 + FontFile3), which
                        // also SUBSETS them by charstring blanking. Pruning here is
                        // the other half of the size problem and composes with it: the pruner drops
                        // whole tables, the blanker empties charstrings, and neither can undo the
                        // other. Applied at load so the file cache holds the smaller program and
                        // every downstream decision sees the real cost — the same placement, and the
                        // same reasoning, as Builder's BuilderFontResolver.LoadFile.
                        if (prog != null && prog.HasCff) prog = Pruned(prog) ?? prog;
                    }
                }
                catch { prog = null; }

                // A failed parse is cached unconditionally: it holds no bytes, and remembering that
                // a file is unreadable is what stops the next scan re-reading it.
                long bytes2 = prog?.FontData?.LongLength ?? 0;
                if (bytes2 == 0 || _chargedBytes + bytes2 <= MaxCachedFontBytes)
                {
                    FileCache[path] = prog;
                    _chargedBytes += bytes2;
                }
                return prog;
            }
        }

        private static string Normalize(string s)
            => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
    }
}
