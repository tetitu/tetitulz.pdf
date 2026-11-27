// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Resolves an embeddable <see cref="TrueTypeFontProgram"/> covering the code points a run
    /// needs. Order: the caller's <see cref="PdfExportOptions.FontProvider"/>, then — only when
    /// <see cref="PdfExportOptions.AllowSystemFontSubstitution"/> is set — files matching the
    /// requested family name, then a list of broad-coverage families, then any font on the system
    /// that covers the code points. Returns <c>null</c> when nothing does, and reports why.
    ///
    /// <para><b>An instance, not a static.</b> A static resolution cache keyed only by
    /// <c>bold|italic|codepoint-range</c> would be wrong: with a per-export
    /// <see cref="PdfExportOptions.FontProvider"/> that key cannot distinguish one caller's
    /// supplied font from another's, so one document's provider font would be served to the next
    /// document. The file cache below stays static: it is keyed by path, it holds host state
    /// rather than caller state, and it is where the cost actually is.</para>
    /// </summary>
    internal sealed class BuilderFontResolver
    {
        // Host-level and safe to share: keyed by path, holding what is on disk.
        private static readonly object FileGate = new object();
        private static readonly Dictionary<string, TrueTypeFontProgram?> FileCache =
            new Dictionary<string, TrueTypeFontProgram?>(StringComparer.OrdinalIgnoreCase);
        private static List<string>? _fontFiles;
        private static long _chargedBytes;

        /// <summary>
        /// Ceiling on the font bytes <see cref="FileCache"/> retains, in the shape
        /// <c>ContentOperationCache.MaxBytes</c> established: once full the cache stops accepting
        /// entries — no eviction, no LRU.
        ///
        /// <para>Step 4 of <see cref="ResolveUncached"/> walks every font on the machine when
        /// nothing covers the run's text, and unbounded, every one it touched would be retained
        /// forever: <see cref="TrueTypeFontProgram.Parse"/> keeps the whole file as <c>FontData</c>
        /// because embedding needs it, and this dictionary is <c>static</c> and never evicted.
        /// Measured on a host with 441 font files, that is <b>425 MiB</b> of large-object arrays held
        /// for the process lifetime after a single export. <c>FontScanBenchmarks</c> has the
        /// numbers; <c>DocxToPdf</c>'s <c>FontResolver</c> carries the same bound, and the
        /// two are deliberately independent constants in independent assemblies rather than a shared
        /// one, matching how <c>ContentOperationCache</c> keeps its own.</para>
        ///
        /// <para>Refusing an entry never changes what <see cref="LoadFile"/> returns, so a full
        /// cache costs a re-read on a future miss and nothing else. A re-read is a new instance,
        /// and the exporter embeds a font once per instance, so an export keeps the host programs it
        /// accepted itself (<c>_acceptedHostPrograms</c>).</para>
        /// </summary>
        internal const long MaxCachedFontBytes = 32L * 1024 * 1024;

        /// <summary>Bytes <see cref="FileCache"/> is holding; for the bound's regression test.</summary>
        internal static long ChargedBytes { get { lock (FileGate) return _chargedBytes; } }

        // Per-export, because a provider result belongs to one caller.
        private readonly PdfExportOptions _options;
        private readonly Dictionary<string, TrueTypeFontProgram?> _resolveCache =
            new Dictionary<string, TrueTypeFontProgram?>(StringComparer.Ordinal);
        private readonly Dictionary<string, TrueTypeFontProgram?> _providerCache =
            new Dictionary<string, TrueTypeFontProgram?>(StringComparer.Ordinal);

        // The host programs this export accepted, by file. The exporter embeds a font once per
        // program instance (EmbeddedByProgram), and _resolveCache is keyed by code-point set, so each
        // new set resolves again; FileCache may never hold a program (MaxCachedFontBytes), and a
        // re-read is a second instance — the same face embedded once per set. Only accepted programs
        // are kept: every candidate a scan touches would be the retention the ceiling exists to stop.
        private readonly Dictionary<string, TrueTypeFontProgram> _acceptedHostPrograms =
            new Dictionary<string, TrueTypeFontProgram>(StringComparer.OrdinalIgnoreCase);

        // Code points no installed font has a glyph for, learned on a failed host scan. A set that
        // contains one can never resolve (a covering font must cover every point), so a later set
        // sharing it skips the scan. Each point is probed at most once (_probedUncoverable guards).
        // Only populated and consulted when the host is the only source — see Resolve.
        private HashSet<int>? _uncoverablePoints;
        private HashSet<int>? _probedUncoverable;

        /// <summary>
        /// How many resolutions were answered <c>null</c> from <see cref="_uncoverablePoints"/>
        /// without a host scan — the liveness signal for the short-circuit, since its answer is the
        /// same <c>null</c> the full scan would give (Builder-1:1 / 2:4).
        /// </summary>
        internal int UncoverableShortCircuits { get; private set; }

        private IReadOnlyList<string>? _hostFontFilesForTest;

        /// <summary>
        /// The host font files this resolver may consider, in place of the machine's own. Null —
        /// the only value production ever uses — means the real OS font directories.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a seam rather than an end-to-end test.</b> The licence gate in
        /// <see cref="HostCandidate"/> only fires on a font found on the host, and the host search
        /// is memoised for the process lifetime in <c>_fontFiles</c> and <see cref="FileCache"/> —
        /// so a test steering it through <c>TETITULZ_PDF_FONTDIR</c> would pass or fail depending on
        /// which test scanned first, and would mutate a process-wide environment variable while
        /// other test classes resolve fonts in parallel. This is per-instance state on a per-export
        /// object, so it races with nothing.</para>
        /// <para>Setting it also skips steps 2 and 3 of <see cref="ResolveUncached"/>, which read
        /// the machine's directories directly: the point is a search that sees exactly the files the
        /// test named and no others. All three steps reach the gate through the one
        /// <see cref="HostCandidate"/>, so exercising step 4 exercises the decision rather than a
        /// copy of it.</para>
        /// </remarks>
        internal IReadOnlyList<string>? HostFontFilesForTest
        {
            get => _hostFontFilesForTest;
            set => _hostFontFilesForTest = value;
        }

        internal BuilderFontResolver(PdfExportOptions options) => _options = options ?? PdfExportOptions.Default;

        /// <summary>
        /// A program covering every code point in <paramref name="requiredCodePoints"/>, or
        /// <c>null</c>. Never throws: a provider that throws is treated as having declined, and
        /// the reason is reported.
        /// </summary>
        /// <param name="providerOnly">
        /// Take the caller's <c>FontProvider</c> and nothing else — no system scan, even where
        /// <c>AllowSystemFontSubstitution</c> permits one.
        ///
        /// <para><b>Why that distinction has to exist.</b> The two callers are asking different
        /// questions. Text above U+00FF has no fallback worth having: without a covering font it is
        /// drawn as <c>'?'</c>, so any face that covers it — installed or supplied — is better than
        /// none, and a system scan is right. A run that merely NAMES a Latin family already has a
        /// perfectly good fallback in the standard-14 faces, and resolving it from the host would
        /// make the output depend on what fonts the machine happens to have: the same HTML would
        /// embed Georgia on a developer's Windows box and fall back to Times on a Linux CI runner.
        /// A provider is the caller's own deterministic choice, so it is the only source that may
        /// change a Latin run's face.</para>
        /// </param>
        /// <param name="familyName">The run's requested family, or null for the default.</param>
        /// <param name="bold">Whether a bold face is wanted.</param>
        /// <param name="italic">Whether an italic face is wanted.</param>
        /// <param name="requiredCodePoints">
        /// The code points the run actually draws. A candidate face is accepted only if its cmap
        /// covers all of them, so a resolution can never silently produce blank boxes.
        /// </param>
        internal TrueTypeFontProgram? Resolve(string? familyName, bool bold, bool italic,
            IReadOnlyCollection<int> requiredCodePoints, bool providerOnly = false)
        {
            int[] required = (requiredCodePoints ?? Array.Empty<int>()).Distinct().OrderBy(c => c).ToArray();
            // Nothing to cover means decline: Covers() is vacuously true for an empty set, so
            // without this the FIRST candidate face is accepted for a run that needed no font.
            //
            // It is the net under RtlText's invariant, not an independently reachable branch: the
            // only way to arrive here empty is for NeedsUnicode and CollectCodePoints to disagree
            // about the threshold, which WinAnsiThresholdTests pins. A mutation of this line
            // survives the whole suite, as expected: with the invariant held, no input reaches it.
            if (required.Length == 0) return null;

            string key = $"{familyName}|{bold}|{italic}|{providerOnly}|{string.Join(",", required)}";
            if (_resolveCache.TryGetValue(key, out var cached)) return cached;

            // This resolution scans the host only when it is NOT provider-only and host substitution
            // is on with no provider. Only then is "no installed font has this code point" the whole
            // story, so only then may the uncoverable-point cache short-circuit or be filled — a
            // provider is an opaque source that could cover anything for some family.
            bool hostScan = !providerOnly && _options.FontProvider == null && _options.AllowSystemFontSubstitution;
            if (hostScan && _uncoverablePoints != null)
                foreach (int cp in required)
                    if (_uncoverablePoints.Contains(cp))
                    {
                        // A set containing a point no installed font has can never resolve; skip the
                        // scan rather than re-walk every font file for the same certain failure.
                        UncoverableShortCircuits++;
                        _resolveCache[key] = null;
                        return null;
                    }

            var result = providerOnly
                ? FromProvider(familyName, bold, italic, required)
                : ResolveUncached(familyName, bold, italic, required);
            _resolveCache[key] = result;

            // A host scan that failed: remember which of its points no installed font has at all, so
            // a later set sharing one short-circuits above. Each point is probed at most once.
            if (result == null && hostScan) RememberUncoverablePoints(required);

            return result;
        }

        /// <summary>
        /// Records the code points in <paramref name="required"/> that no installed font has a glyph
        /// for. Coverage is necessary for a resolution, so such a point makes any set holding it fail;
        /// probing it is a cmap-only <see cref="FontCoverageProbe"/> pass, done once per point.
        /// </summary>
        private void RememberUncoverablePoints(int[] required)
        {
            _probedUncoverable ??= new HashSet<int>();
            int[] one = new int[1];
            foreach (int cp in required)
            {
                if (!_probedUncoverable.Add(cp)) continue;   // already classified
                one[0] = cp;
                bool anyFontMightHaveIt = false;
                foreach (string path in _hostFontFilesForTest ?? AllFontFiles())
                {
                    // `false` is the only sound "no"; `true` and `null` (cannot decide) both mean a
                    // font might cover it, so the point is not certainly uncoverable.
                    if (FontCoverageProbe.Covers(path, one) != false) { anyFontMightHaveIt = true; break; }
                }
                if (!anyFontMightHaveIt) (_uncoverablePoints ??= new HashSet<int>()).Add(cp);
            }
        }

        private TrueTypeFontProgram? ResolveUncached(string? familyName, bool bold, bool italic, int[] required)
        {
            // 1. The caller's provider — deterministic, and the only path where the caller
            //    knowingly chooses what gets redistributed.
            var supplied = FromProvider(familyName, bold, italic, required);
            if (supplied != null) return supplied;

            if (!_options.AllowSystemFontSubstitution) return null;

            // Steps 2 and 3 read the machine's font directories, so a test that wants to drive the
            // host branch deterministically supplies its own file list and they are skipped: see
            // HostFontFilesForTest for why the seam exists at all.
            if (_hostFontFilesForTest == null)
            {
                // 2. Files whose name matches the family the run actually asked for. Without this the
                //    family is only ever a sans/serif/mono bucket, so a document that asks for David
                //    or Noto Sans Hebrew gets whichever installed font happens to cover Hebrew first.
                if (!string.IsNullOrWhiteSpace(familyName))
                {
                    foreach (string path in FontFileLocator.FindFamilyFiles(familyName!, bold, italic, 8))
                    {
                        var prog = HostCandidate(path, required);
                        if (prog != null && IsEmbeddable(prog, path, required)) return prog;
                    }
                }

                // 3. Preferred broad-coverage families in the requested style.
                foreach (string stem in PreferredStems(bold, italic))
                    foreach (string dir in FontFileLocator.Directories)
                        foreach (string ext in FontExtensions)
                        {
                            string p = Path.Combine(dir, stem + ext);
                            var prog = HostCandidate(p, required);
                            if (prog != null && IsEmbeddable(prog, p, required)) return prog;
                        }
            }

            // 4. Any font on the system that covers the code points. glyf-outline programs are
            //    still tried before CFF ones. The original reason ("only glyf can be subsetted")
            //    stopped being true in plan 11 §5e slice 2, but the preference is kept for a
            //    different one: a glyf subset REBUILDS the font and so shrinks the file, while CFF
            //    blanking leaves the length identical and pays off only after Flate. Where both
            //    cover the text, the one that shrinks the uncompressed program is still better.
            var cffCandidates = new List<(TrueTypeFontProgram prog, string path)>();
            foreach (string path in _hostFontFilesForTest ?? AllFontFiles())
            {
                // Cheap, sound rejection before the full read: FontCoverageProbe reads the sfnt
                // directory and cmap only, and reaches its verdict through the same Covers this loop
                // uses. It may ONLY reject — "cannot decide" and "covers" both fall through, so the
                // choice is always made by a real program. 395 ms/461 MB down to 34 ms/3.6 MB across
                // the scan; parity is asserted by Core.Tests' FontCoverageProbeParityTests.
                if (FontCoverageProbe.Covers(path, required) == false) continue;

                var prog = HostCandidate(path, required);
                if (prog == null) continue;
                if (!prog.HasCff) { if (IsEmbeddable(prog, path, required)) return prog; }
                else cffCandidates.Add((prog, path));
            }
            foreach (var (prog, path) in cffCandidates)
                if (IsEmbeddable(prog, path, required)) return prog;

            return null;
        }

        /// <summary>
        /// A font found <b>on this host</b> that may be used for the run: parsed, covering
        /// <paramref name="required"/>, and permitted to be embedded by its own <c>OS/2</c>
        /// <c>fsType</c>. <c>null</c> otherwise.
        ///
        /// <para><b>This is the one licence gate, and all three host steps above reach it.</b>
        /// Embedding a font redistributes it, and <c>fsType</c> is where the foundry records what it
        /// permits — so a face that says "Restricted License embedding" or "bitmaps only" must not
        /// be picked up off the machine and shipped inside a PDF. With
        /// <see cref="PdfExportOptions.AllowSystemFontSubstitution"/> defaulting to <b>true</b>, a
        /// resolver that never read the field would let every Builder export — and everything
        /// layered on it, <c>HtmlToPdf</c> and the Templating plugin included — embed such a font by
        /// default.</para>
        ///
        /// <para><b>It is one method rather than three inline tests</b> for the reason
        /// <c>SystemFontResolver.Embeddable</c> gives for the same decision: three copies of a rule
        /// are three places for a correction to reach only one. Note the mirrored asymmetry — the
        /// gate is applied here and deliberately <b>not</b> in <see cref="FromProvider"/>. A caller
        /// who hands over a font program has made a licensing decision this code cannot see and may
        /// hold a licence the bits know nothing about; a font the library picks up by itself is the
        /// opposite case, because nobody decided anything and the file's own statement is all there
        /// is. <c>PdfExportOptions.FontProvider</c> is therefore the escape hatch for a deployment
        /// that does hold the licence, in the way
        /// <c>FontResolutionRequest.AllowRestrictedSystemFonts</c> is for Core's resolver.</para>
        ///
        /// <para>A rejection is <b>reported, not swallowed</b>: without it the export reports only
        /// "no font on this host covers U+05D0" while a font that covers it sits on disk — a true
        /// sentence pointing at the wrong problem.</para>
        /// </summary>
        /// <param name="path">The file the candidate came from; named in the diagnostic.</param>
        /// <param name="required">The code points the run draws.</param>
        private TrueTypeFontProgram? HostCandidate(string path, int[] required)
        {
            var prog = _acceptedHostPrograms.TryGetValue(path, out var accepted) ? accepted : LoadFile(path);
            if (prog == null || !Covers(prog, required)) return null;

            var licence = prog.EmbeddingLicense;
            if (licence.PermitsOutlineEmbedding)
            {
                _acceptedHostPrograms[path] = prog;
                return prog;
            }

            ReportRejected(
                $"the host font '{path}' was not embedded: {licence.Describe()}. Supply it through " +
                "PdfExportOptions.FontProvider if this deployment holds a licence permitting it");
            return null;
        }

        private TrueTypeFontProgram? FromProvider(string? familyName, bool bold, bool italic, int[] required)
        {
            if (_options.FontProvider == null) return null;

            string key = $"{familyName}|{bold}|{italic}|{string.Join(",", required)}";
            if (_providerCache.TryGetValue(key, out var cached)) return cached;

            TrueTypeFontProgram? prog = null;
            try
            {
                byte[]? bytes = _options.FontProvider(
                    new BuilderFontRequest(familyName ?? "", bold, italic, required));
                if (bytes != null && bytes.Length > 0)
                {
                    prog = TrueTypeFontProgram.Parse(bytes);
                    if (prog == null)
                        ReportRejected($"the font program supplied for '{familyName}' could not be parsed " +
                                       "(expected sfnt TrueType or OpenType)");
                    else if (!Covers(prog, required))
                    {
                        ReportRejected($"the font program supplied for '{familyName}' does not cover " +
                                       $"{DescribeMissing(prog, required)}");
                        prog = null;
                    }
                    else if (!IsEmbeddable(prog, "<provided>", required))
                        prog = null;
                }
            }
            catch (Exception ex)
            {
                // A provider is caller code; a throw from it must not take the export down.
                ReportRejected($"the font provider threw for '{familyName}': {ex.Message}", ex);
                prog = null;
            }

            _providerCache[key] = prog;
            return prog;
        }

        /// <summary>
        /// Whether this program may be embedded. CFF/OpenType programs ARE embeddable — Core's
        /// <c>Type0FontBuilder</c> emits them as CIDFontType0 + <c>/FontFile3 /OpenType</c> — and
        /// since plan 11 §5e slice 2 they are also <b>subsetted</b>, by charstring blanking.
        /// </summary>
        /// <remarks>
        /// <para><b>The ceiling applies only where its own name says it does.</b>
        /// <see cref="PdfExportOptions.MaxUnsubsettableFontBytes"/> exists to stop one character of a
        /// CJK <c>.otf</c> adding 10–20 MB to a document, the cost of embedding a CFF font whole. A
        /// font <c>CffSubsetter</c> can subset does not cost that, so measuring it against a ceiling
        /// for unsubsettable fonts would refuse a font that renders — and the fallback is worse
        /// output, not a smaller file. CFF2 and malformed programs cannot be subsetted, and the
        /// ceiling applies to those.</para>
        ///
        /// <para><b>What this deliberately does not do is predict the final size.</b> The glyph set
        /// is not known here — this resolver sees one run's code points, while the embedded subset
        /// covers the whole document — so any size this method computed would be a floor quoted as
        /// a total. The subsettability probe is a fact about the font; a size estimate would be a
        /// guess about the content, and GAPS §16 item 4 is what quoting the wrong regime costs.</para>
        /// </remarks>
        private bool IsEmbeddable(TrueTypeFontProgram prog, string origin, int[] required)
        {
            if (!prog.HasCff) return true;
            int size = prog.FontData?.Length ?? 0;
            if (size <= 0 || size <= _options.MaxUnsubsettableFontBytes) return true;

            if (CffSubsetter.CanSubset(prog.FontData, RequiredGids(prog, required), out string why))
                return true;

            ReportRejected(
                $"{origin} is a CFF/OpenType program of {size:N0} bytes that cannot be subsetted " +
                $"({why}); embedding it whole would exceed PdfExportOptions.MaxUnsubsettableFontBytes " +
                $"({_options.MaxUnsubsettableFontBytes:N0})");
            return false;
        }

        /// <summary>
        /// The run's code points as glyph ids, for the subsettability probe. An unmapped code point
        /// contributes nothing: the probe asks whether the font's <i>shape</i> permits subsetting,
        /// and a missing glyph is a coverage question <see cref="Covers"/> has already answered.
        /// </summary>
        private static List<int> RequiredGids(TrueTypeFontProgram prog, int[] required)
        {
            var gids = new List<int>((required?.Length ?? 0) + 1) { 0 };
            if (required != null)
                foreach (int codePoint in required)
                    if (prog.TryGetGlyph(codePoint, out ushort gid)) gids.Add(gid);
            return gids;
        }

        private void ReportRejected(string detail, Exception? ex = null) =>
            ReportTo(BuilderDiagnosticKind.FontFormatRejected, detail, ex);

        internal void ReportUnresolved(string detail) =>
            ReportTo(BuilderDiagnosticKind.FontNotResolved, detail);

        internal void ReportMissingGlyph(string detail) =>
            ReportTo(BuilderDiagnosticKind.GlyphMissing, detail);

        private void ReportTo(BuilderDiagnosticKind kind, string detail, Exception? ex = null)
        {
            BuilderDiagnostics.Report(kind, detail, ex);
            var handler = _options.OnDiagnostic;
            // Report already reached it when it is the running export's handler.
            if (handler == null || ReferenceEquals(handler, BuilderDiagnostics.ExportHandler)) return;
            try { handler(new BuilderDiagnostic(kind, detail, ex)); } catch { /* caller telemetry */ }
        }

        /// <summary>The first few code points <paramref name="prog"/> cannot draw, for a message a reader can act on.</summary>
        internal static string DescribeMissing(TrueTypeFontProgram? prog, IReadOnlyCollection<int> required)
        {
            var missing = required.Where(cp => prog == null || !prog.CoversShaped(cp)).ToList();
            if (missing.Count == 0) return "no code points";
            string head = string.Join(", ", missing.Take(4).Select(cp => $"U+{cp:X4}"));
            return missing.Count <= 4 ? head : $"{head} (and {missing.Count - 4} more)";
        }

        // Style-suffixed stems for broad-coverage families that ship Hebrew/Arabic/Latin
        // (Windows / common Linux / macOS filenames). Regular stem last as a fallback.
        private static IEnumerable<string> PreferredStems(bool bold, bool italic)
        {
            string sfx = (bold ? "bd" : "") + (italic ? "i" : "");
            string[] families =
            {
                "arialuni", "arial", "tahoma", "segoeui", "times", "david",
                "NotoSansHebrew-Regular", "NotoSansArabic-Regular", "NotoSans-Regular",
                "DejaVuSans", "FreeSans",
            };
            foreach (var fam in families)
            {
                if (sfx.Length > 0) yield return fam + sfx;
                if (bold && italic) yield return fam + "bi";
                if (bold) yield return fam + "bd";
                yield return fam;
            }
        }

        private static readonly string[] FontExtensions = { ".ttf", ".ttc", ".otf" };

        private static bool Covers(TrueTypeFontProgram prog, int[] required)
        {
            foreach (int cp in required)
                if (!prog.CoversShaped(cp)) return false;
            return true;
        }

        // The OS font-directory list and the recursive scan live in Core.Fonts.FontFileLocator —
        // this file, DocxToPdf's FontResolver and Base's FontCache all need the same answer, and
        // three private copies had already started to drift.
        //
        // includeOpenType: TRUE — CFF programs are embeddable (see IsEmbeddable). This was false,
        // which silently excluded every .otf on the host, so a container shipping only Noto
        // Hebrew as OpenType rendered Hebrew as '?'.
        private static List<string> AllFontFiles()
        {
            lock (FileGate)
            {
                if (_fontFiles != null) return _fontFiles;
                _fontFiles = new List<string>(FontFileLocator.EnumerateFontFiles(includeOpenType: true));
                return _fontFiles;
            }
        }

        /// <summary>
        /// <see cref="LoadFile"/>, reachable by a test.
        /// </summary>
        /// <remarks>
        /// The ordinary route to it is a scan of the OS font directories, and both that scan and
        /// the file cache below are memoised for the process lifetime — so an end-to-end test for
        /// this reporting path would pass or fail depending on which test ran first, which is the
        /// flakiness class this repository has already been bitten by. Driving the loader with a
        /// path of the test's own choosing asserts the same behaviour deterministically.
        /// </remarks>
        internal TrueTypeFontProgram? LoadFontFileForTest(string path) => LoadFile(path);

        /// <summary>
        /// The same program with the tables a PDF reader never consults removed, or <c>null</c> when
        /// pruning cannot help and the original should be used unchanged.
        ///
        /// <para>Re-parsed rather than patched, because <see cref="TrueTypeFontProgram.FontData"/> is
        /// what actually gets embedded and only <c>Parse</c> can set it. A prune whose output the
        /// parser then rejects counts as no prune at all: the original font is known to work, and a
        /// smaller broken one is not an improvement.</para>
        /// </summary>
        private TrueTypeFontProgram? Pruned(TrueTypeFontProgram program)
        {
            byte[]? original = program.FontData;
            if (original == null || original.Length == 0) return null;

            byte[]? pruned;
            try { pruned = SfntTablePruner.Prune(original, out _, out _); }
            catch { return null; }
            // A prune that did not shrink the program is discarded. The effect is output SIZE
            // only — both byte arrays are valid fonts — which is why a mutation removing the
            // length clause survives the suite: nothing asserting the exported document's content
            // can see it, and that is CLAUDE.md's second blind spot (an optimisation whose fast
            // and slow paths agree cannot be pinned by asserting the answer). Pinning it would
            // need a liveness probe on the pruner rather than an assertion about the output.
            if (pruned == null || pruned.Length >= original.Length) return null;

            TrueTypeFontProgram? reparsed;
            try { reparsed = TrueTypeFontProgram.Parse(pruned); }
            catch { return null; }
            return reparsed;
        }

        /// <summary>
        /// Loads and parses a font file, memoising the result — including a failure — by path.
        /// </summary>
        /// <remarks>
        /// <para><b>A file that is present but unreadable is not the same as an absent one</b>, and
        /// the bare <c>catch</c> here made them identical: every other rejection path in this class
        /// reports, and this one — the one that fires when a font is installed and broken — did not.
        /// On a container where DejaVuSans.ttf exists but was truncated by a partial layer copy, or
        /// is unreadable by permissions, every Hebrew run fell back to <c>'?'</c> and the only
        /// diagnostic named the code points, never the file that was tried.</para>
        /// <para>The report happens on the cache MISS and outside the lock: once per path per
        /// process, matching the memoised null, and without invoking a host's handler while holding
        /// <see cref="FileGate"/>.</para>
        /// </remarks>
        private TrueTypeFontProgram? LoadFile(string path)
        {
            string? failure = null;
            TrueTypeFontProgram? prog;
            lock (FileGate)
            {
                if (FileCache.TryGetValue(path, out var cached)) return cached;
                prog = null;
                try
                {
                    if (File.Exists(path)) prog = TrueTypeFontProgram.Parse(File.ReadAllBytes(path));
                }
                catch (Exception ex)
                {
                    prog = null;
                    failure = ex.Message;
                }

                // A failed parse holds no bytes and is always memoised — remembering that a file is
                // unreadable is what stops the next scan re-reading it, and it is what the reporting
                // path above is written around.
                // A CFF/OpenType program carries its layout tables into every PDF that uses one.
                // Pruning them (Core.Fonts.SfntTablePruner) is the cheap half of the size problem;
                // blanking unused charstrings at the embed site is the other half
                // (Core.Writing.Type0FontBuilder). The two compose — the pruner drops whole
                // tables, the blanker empties charstrings — and neither can undo the other.
                //
                // Applied here rather than at the embed site so EVERY downstream decision sees the
                // real cost. Coverage still works (the pruner keeps cmap by design), and the file
                // cache holds the smaller program.
                //
                // Pruning only ever drops tables a PDF reader does not consult, and returns null
                // rather than the input when it cannot help, so the original is used unchanged.
                if (prog != null && prog.HasCff) prog = Pruned(prog) ?? prog;

                long bytes = prog?.FontData?.LongLength ?? 0;
                if (bytes == 0 || _chargedBytes + bytes <= MaxCachedFontBytes)
                {
                    FileCache[path] = prog;
                    _chargedBytes += bytes;
                }
            }

            if (failure != null)
                ReportRejected($"the font file '{path}' is present but could not be read or parsed: {failure}");
            return prog;
        }
    }
}
