// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>What a caller wants a font for.</summary>
    public sealed class FontResolutionRequest
    {
        /// <summary>
        /// The family to prefer — a PDF <c>/BaseFont</c> name, a CSS family, "David", "Arial".
        /// May be null, in which case only the script fallbacks are tried.
        /// </summary>
        public string FamilyHint { get; set; }

        /// <summary>Prefer a bold face.</summary>
        public bool Bold { get; set; }

        /// <summary>Prefer an italic face.</summary>
        public bool Italic { get; set; }

        /// <summary>
        /// The code points the resolved font must have glyphs for. A face that matches the family
        /// by name but cannot draw these is <b>not</b> a match — that distinction is the whole
        /// reason this type exists, because filename matching cannot see a <c>cmap</c>.
        /// </summary>
        public IReadOnlyCollection<int> RequiredCodePoints { get; set; }

        /// <summary>
        /// Permit falling back to a font installed on this machine when
        /// <see cref="SystemFontResolver.Resolve"/>'s provider yields nothing usable.
        ///
        /// <para><b>Defaults to false, and that is a licensing decision, not a technical one.</b>
        /// Embedding a font redistributes it. This library ships no fonts and licenses none, so the
        /// caller has to say that redistributing whatever is installed on the host is acceptable.
        /// Same name and same default as <c>FormFillOptions.AllowSystemFontSubstitution</c> and
        /// <c>Accessibility.FontEmbeddingOptions.AllowSystemFontSubstitution</c>; a third spelling
        /// of one concept would be a documentation bug.</para>
        ///
        /// <para>Note this is orthogonal to <c>TETITULZ_PDF_FONTDIR</c>, which controls <i>where</i>
        /// to look. Pointing that at fonts the deployment owns and then setting this true carries no
        /// licensing risk, and is the recommended server configuration.</para>
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// Accept CFF/OpenType (<c>.otf</c>) outlines as well as TrueType <c>glyf</c>.
        /// Defaults to true: several Hebrew families that a Linux host is likely to have — Alef,
        /// Rubik, Heebo, some Noto Hebrew builds — ship only as <c>.otf</c>, and
        /// <c>Type0FontBuilder</c> has emitted <c>CIDFontType0</c> + <c>FontFile3</c> for them for
        /// some time. See <see cref="FontResolution.WillEmbedFullProgram"/> for the cost.
        /// </summary>
        public bool AllowCffOutlines { get; set; } = true;

        /// <summary>
        /// Ceiling on font files parsed during the unnamed fallback scan. A font server can expose
        /// thousands, and parsing each one is not free.
        /// </summary>
        public int MaxScannedFiles { get; set; } = PdfLimits.MaxScannedFontFiles;

        /// <summary>
        /// Consider a host font whose <c>OS/2</c> <c>fsType</c> forbids embedding its outlines.
        /// Defaults to <b>false</b>, so such a font is skipped and the search continues.
        ///
        /// <para><b>This applies to the system search only, never to
        /// <see cref="SystemFontResolver.Resolve"/>'s <c>fontProvider</c>.</b> A caller who hands
        /// over a font program has made a licensing decision this code cannot see and may hold a
        /// licence the bits know nothing about; overruling them would be wrong. A font the library
        /// finds on the machine by itself is the opposite case — nobody decided anything, and the
        /// only statement available is the one in the file. See <see cref="FontEmbeddingLicense"/>.</para>
        ///
        /// <para>Set it true when the deployment owns the fonts it installed — the same situation
        /// <c>TETITULZ_PDF_FONTDIR</c> exists for.</para>
        /// </summary>
        public bool AllowRestrictedSystemFonts { get; set; }

        /// <summary>
        /// The code points of <paramref name="text"/>, as <see cref="RequiredCodePoints"/> wants
        /// them — <b>surrogate pairs combined into the one code point they encode.</b>
        /// </summary>
        /// <param name="text">The text that will be drawn. Null yields an empty set.</param>
        /// <remarks>
        /// <para><b>Combining surrogates is the whole job, and the reason is that a
        /// <c>foreach (char …)</c> loop looks like it already does this.</b> It does not: over an
        /// astral character — an emoji, a rarer CJK ideograph, an ancient script — it yields the two
        /// halves of the pair, U+D83D and U+DE42 rather than U+1F642. No <c>cmap</c> maps a lone
        /// surrogate, so the resolution fails with "no font on this host covers U+D83D" for text
        /// that half the fonts on the machine can draw. Two of the three call sites had their own
        /// correct loop and the third was the naive one.</para>
        ///
        /// <para><b>Whitespace</b> is deliberately <i>not</i> filtered here:
        /// <see cref="SystemFontResolver.Resolve"/> already drops space, tab, CR and LF from
        /// whatever it is given, so doing it twice would put that rule in two places and invite
        /// them to drift — which is the defect this helper exists to end, not to reproduce.
        /// <b>Duplicates are</b> filtered, because the set is built with a <c>HashSet</c> and there
        /// is no way to hand back one that keeps them. That is not the same hazard: <c>Resolve</c>
        /// de-duplicates whatever it is handed regardless, so doing it twice is idempotent and the
        /// two can never disagree about anything, where dropping whitespace twice would still be
        /// one rule living in two places. Duplicates are filtered;
        /// <c>CodePointsOfTests.CodePointsOf_DeduplicatesAndAcceptsBmpText</c> pins it.</para>
        /// </remarks>
        public static IReadOnlyCollection<int> CodePointsOf(string text)
        {
            var seen = new HashSet<int>();
            if (string.IsNullOrEmpty(text)) return seen;

            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length &&
                    char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }

                seen.Add(cp);
            }

            return seen;
        }
    }

    /// <summary>The outcome of <see cref="SystemFontResolver.Resolve"/>.</summary>
    public sealed class FontResolution
    {
        internal FontResolution(TrueTypeFontProgram program, byte[] fontData, string sourcePath,
                                bool fromSystem, IReadOnlyList<int> uncovered, string failureReason)
        {
            Program = program;
            FontData = fontData;
            SourcePath = sourcePath;
            FromSystem = fromSystem;
            UncoveredCodePoints = uncovered ?? Array.Empty<int>();
            FailureReason = failureReason;
            WillEmbedFullProgram = program != null && program.HasCff;
        }

        /// <summary>The parsed font, or null when nothing usable was found.</summary>
        public TrueTypeFontProgram Program { get; }

        /// <summary>The bytes the program was parsed from — so the caller need not re-read the file.</summary>
        public byte[] FontData { get; }

        /// <summary>Where it came from, or null when it came from the caller's provider.</summary>
        public string SourcePath { get; }

        /// <summary>True when this came from a font installed on the host rather than the provider.</summary>
        public bool FromSystem { get; }

        /// <summary>
        /// True when embedding this font will carry the <b>whole</b> program rather than a subset:
        /// <c>TrueTypeSubsetter</c> handles <c>glyf</c> outlines only, so a CFF/OpenType face is
        /// embedded entire. Surfaced rather than silently preferred-against, because for a document
        /// with one Hebrew line that is a few hundred KB the caller may not want.
        /// </summary>
        public bool WillEmbedFullProgram { get; private set; }

        /// <summary>
        /// The requested code points no candidate could draw. Empty on success. Populated on
        /// failure so the caller can name them — <c>U+05D0</c> is actionable, "no font found" is not.
        /// </summary>
        public IReadOnlyList<int> UncoveredCodePoints { get; }

        /// <summary>Why resolution failed, or null on success.</summary>
        public string FailureReason { get; }

        /// <summary>
        /// What the resolved font's <c>OS/2</c> <c>fsType</c> permits.
        /// <see cref="FontEmbeddingLicense.Undeclared"/> on failure, and for a program with no
        /// <c>OS/2</c> table.
        /// </summary>
        /// <remarks>
        /// Surfaced even when the resolution came from the caller's own provider — <b>especially</b>
        /// then, since that is the case the resolver deliberately does not police. A caller building
        /// a font-licensing report reads this; a caller who simply wants the search to avoid
        /// restricted host fonts sets <c>FontResolutionRequest.AllowRestrictedSystemFonts</c> false,
        /// which is the default.
        /// </remarks>
        public FontEmbeddingLicense EmbeddingLicense =>
            Program?.EmbeddingLicense ?? FontEmbeddingLicense.Undeclared;

        /// <summary>A usable font was found.</summary>
        public bool Success => Program != null;
    }

    /// <summary>
    /// Finds a font that can actually draw a given set of code points.
    ///
    /// <para><b>Why this is public and in Core.</b> <c>Base.Operations.FormFontSource</c>,
    /// <c>Builder.Export.BuilderFontResolver</c> and <c>DocxToPdf.Rendering.FontResolver</c> all
    /// resolve through it, over the filename matching in <see cref="FontFileLocator"/>, so they agree
    /// on whether CFF is acceptable, whether the scan is bounded, and whether coverage is checked.
    /// A plugin reaches it too: Core's <c>InternalsVisibleTo</c> names no plugin, so an internal
    /// resolver would leave a plugin writing its own copy.</para>
    ///
    /// <para><b>Coverage is checked before the font is returned, not after.</b> Handing back a face
    /// that parses but has no glyphs for the text pushes the failure to the point of drawing, where
    /// the usual outcome is a silently substituted space — a Hebrew name that quietly loses a
    /// letter. Here it is a failed resolution naming the code point.</para>
    /// </summary>
    public static class SystemFontResolver
    {
        [ThreadStatic] private static string _lastResolutionError;

        /// <summary>
        /// Why the most recent <see cref="Resolve"/> <b>on this thread</b> failed — null when it
        /// succeeded, and null on a thread that has not resolved anything. Cleared at the start of
        /// every call on every path, so reading it straight after one describes that call.
        /// </summary>
        public static string LastResolutionError => _lastResolutionError;

        /// <summary>
        /// Resolves a font covering <see cref="FontResolutionRequest.RequiredCodePoints"/>.
        /// </summary>
        /// <param name="request">What is needed. Null is treated as an empty request.</param>
        /// <param name="fontProvider">
        /// The caller's own source of font bytes, tried first and consulted with the family hint.
        /// May be null. Returning null or unusable bytes falls through to the system search only
        /// when <see cref="FontResolutionRequest.AllowSystemFontSubstitution"/> is set.
        /// </param>
        public static FontResolution Resolve(FontResolutionRequest request,
                                             Func<string, byte[]> fontProvider)
        {
            _lastResolutionError = null;
            request = request ?? new FontResolutionRequest();

            var required = Distinct(request.RequiredCodePoints);

            // Files the licence gate rejected, so the failure reason below can name them.
            var licenceSkips = new List<string>();

            // What the caller's own font could not draw, kept so the refusal below can name it.
            // Null means the provider was absent, threw, or returned bytes that would not parse —
            // which is a different sentence from "your font is missing a glyph".
            List<int> providerGaps = null;

            // Why the caller's provider produced nothing, when it threw. A LOCAL, not
            // `_lastResolutionError`: that slot is cleared by Success() and overwritten by Fail(),
            // so every exit from this method destroyed the message before any caller could reach it
            // through LastResolutionError — recorded, never reportable, for the whole life of the
            // line that wrote it. It is folded into the failure reasons below instead, which is the
            // one place a caller can act on it. On success it stays unsaid, deliberately: a
            // resolution that worked must leave no diagnostic behind (see Success()).
            string providerFailure = null;

            // 1. The caller's provider, coverage-tested like anything else.
            if (fontProvider != null)
            {
                byte[] bytes = null;
                try { bytes = fontProvider(request.FamilyHint); }
                catch (Exception ex)
                {
                    // A provider that throws is the caller's bug, but it must not take the
                    // operation down: record it and continue to the fallbacks it may have allowed.
                    providerFailure = "the font provider threw " + ex.GetType().Name + ": " + ex.Message;
                }

                var program = Parse(bytes, request.AllowCffOutlines);
                if (program != null && Covers(program, required, out providerGaps))
                    return Success(program, bytes, null, fromSystem: false);
            }

            // 2. The opt-in gate. Without it, a caller who supplied no font — or whose font did not
            //    cover the text — gets a clear failure rather than a redistributed system face.
            if (!request.AllowSystemFontSubstitution)
            {
                if (providerGaps != null)
                    return Fail(providerGaps,
                        "the supplied font does not cover " + Describe(providerGaps) +
                        ", and AllowSystemFontSubstitution is false");

                // No usable font arrived at all. Saying "the supplied font does not cover …" here
                // would describe a font
                // that was never supplied, and sends the caller looking at a font program instead of
                // at the provider that returned nothing. A provider that THREW gets its own
                // sentence for the same reason: "returned nothing usable" describes one that
                // declined, and the remedy for a broken provider is not the remedy for one that
                // simply had no font to give.
                return Fail(required,
                    fontProvider == null
                        ? "no font was supplied and AllowSystemFontSubstitution is false"
                        : providerFailure != null
                            ? providerFailure + ", and AllowSystemFontSubstitution is false"
                            : "the font provider returned nothing usable and AllowSystemFontSubstitution " +
                              "is false");
            }

            // 3. Named families, best-ranked first, each coverage-tested.
            foreach (string family in FamilyCandidates(request.FamilyHint, required))
            {
                foreach (string path in FontFileLocator.FindFamilyFiles(
                             family, request.Bold, request.Italic, MaxCandidatesPerFamily))
                {
                    if (FontCoverageProbe.Covers(path, required) == false) continue;

                    var hit = TryAccept(path, request, required, licenceSkips);
                    if (hit != null) return hit;
                }
            }

            // 3b. The family a file does not admit to in its NAME. Step 3 matches FILENAMES —
            //     "Liberation Sans" finds LiberationSans-Regular.ttf — which is most of the time
            //     the whole answer and costs no reads at all. It cannot answer for a face whose
            //     file is named nothing like its family, and there falling straight through to
            //     step 4 would return the first font on the machine that happens to cover the
            //     text: the RIGHT GLYPHS IN THE WRONG TYPEFACE, silently.
            //
            //     FontCoverageProbe.FamilyName reads the sfnt directory and the `name` table only
            //     — one table, not the file — and answers through the same
            //     TrueTypeFontProgram.FamilyName the full read would, so this is a cheaper route
            //     to the same verdict rather than a second implementation of it.
            //
            //     A null probe ("cannot tell cheaply") is SKIPPED here rather than escalated to a
            //     full read. That is the opposite of what DocxToPdf's resolver does with the same
            //     probe, and deliberately: there the family pass is the only pass, so a deferral
            //     must be followed up or the font stops being a candidate. Here step 4 below is an
            //     exhaustive walk of the same files, so anything skipped is still reached — this
            //     pass only changes which of two acceptable answers wins, and paying a full read
            //     for every unnameable file would give back the saving that makes it affordable.
            if (!string.IsNullOrEmpty(request.FamilyHint))
            {
                int probed = 0;
                foreach (string path in FontFileLocator.EnumerateFontFiles(request.AllowCffOutlines))
                {
                    if (path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++probed > request.MaxScannedFiles) break;

                    string family = FontCoverageProbe.FamilyName(path);
                    if (!FontFileLocator.IsFamilyMatch(family, request.FamilyHint)) continue;
                    if (FontCoverageProbe.Covers(path, required) == false) continue;

                    var hit = TryAccept(path, request, required, licenceSkips);
                    if (hit != null) return hit;
                }
            }

            // 4. Bounded scan. Last resort: the family hint told us nothing and no script fallback
            //    matched, so walk what is installed until something covers the text.
            int scanned = 0;
            foreach (string path in FontFileLocator.EnumerateFontFiles(request.AllowCffOutlines))
            {
                // .ttc collections cannot be parsed by TrueTypeFontProgram, and on macOS they are
                // most of the system fonts — each one would burn a slot of the budget on a file
                // that can never succeed.
                if (path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) continue;
                if (++scanned > request.MaxScannedFiles) break;

                // Cheap rejection: the sfnt directory and cmap rather than the whole file, answered
                // through the same TrueTypeFontProgram.Covers this loop uses (Core.Fonts.FontCoverageProbe).
                //
                // Deliberately AFTER the budget increment. Not charging a rejected file would let the
                // scan reach further and so find a different font — better output, perhaps, but a
                // different answer, and this is meant to be purely cheaper. The same set
                // of files is examined and the same one wins; only the reads disappear.
                if (FontCoverageProbe.Covers(path, required) == false) continue;

                var hit = TryAccept(path, request, required, licenceSkips);
                if (hit != null) return hit;
            }

            return Fail(required, WithProviderFailure(WithLicenceSkips(
                required.Count == 0
                    ? "no usable font was found on this host"
                    : "no font on this host covers " + Describe(required),
                licenceSkips), providerFailure));
        }

        /// <summary>
        /// Whether a font found <b>on this host</b> may be embedded — the licence gate, applied at
        /// all three system-search sites and at none of them twice.
        /// </summary>
        /// <param name="program">The parsed candidate.</param>
        /// <param name="request">Carries <see cref="FontResolutionRequest.AllowRestrictedSystemFonts"/>.</param>
        /// <param name="path">The file it came from, named in the diagnostic.</param>
        /// <param name="skipped">Collects the rejected files, for the failure reason.</param>
        /// <remarks>
        /// <para><b>Deliberately not applied to the caller's provider</b> — see
        /// <see cref="FontEmbeddingLicense"/> for why the two cases differ. It is one method rather
        /// than three inline tests because the three sites are the same decision, and a rule with copies is
        /// changed at one of them.</para>
        ///
        /// <para><b>A skip is recorded, not swallowed.</b> Without it the search simply reports "no
        /// font on this host covers U+05D0" while a font that does cover it sits on disk — a true
        /// sentence pointing at the wrong problem, and the caller would go looking for a Hebrew font
        /// they already have.</para>
        /// </remarks>
        private static bool Embeddable(TrueTypeFontProgram program, FontResolutionRequest request,
                                       string path, List<string> skipped)
        {
            if (request.AllowRestrictedSystemFonts) return true;
            if (program.EmbeddingLicense.PermitsOutlineEmbedding) return true;

            // Collected rather than written to _lastResolutionError, because Fail() overwrites that
            // slot with the generic "no font covers …" reason — which would have made this
            // diagnostic dead on the only path where it matters.
            if (skipped.Count < MaxReportedLicenceSkips)
                skipped.Add(path + " (" + program.EmbeddingLicense.Describe() + ")");
            return false;
        }

        /// <summary>
        /// How many licence-skipped files a failure reason names before it stops listing them. A
        /// host with a restricted font family installed has one per weight and style, and a reason
        /// string is read by a human.
        /// </summary>
        private const int MaxReportedLicenceSkips = 3;

        /// <summary>
        /// Appends what the licence gate rejected to a failure reason, so "no font on this host
        /// covers U+05D0" cannot be reported while a font that does cover it sits on disk — a true
        /// sentence pointing at the wrong problem, which would send the caller looking for a font
        /// they already have.
        /// </summary>
        private static string WithLicenceSkips(string reason, List<string> skipped)
            => skipped.Count == 0
                ? reason
                : reason + "; " + skipped.Count + " candidate" + (skipped.Count == 1 ? " was" : "s were")
                  + " skipped because embedding is licence-restricted (" + string.Join(", ", skipped)
                  + ") — set FontResolutionRequest.AllowRestrictedSystemFonts if this deployment "
                  + "holds a licence for them";

        /// <summary>
        /// Appends a provider exception to a failure reason, so "no font on this host covers
        /// U+05D0" cannot be the whole story when the caller's own provider — which may well have
        /// held a font that covers it — threw before it could be asked.
        /// </summary>
        /// <remarks>
        /// Same shape and same reason as <see cref="WithLicenceSkips"/>: a true sentence pointing at
        /// the wrong problem is worse than a longer one. Deliberately <b>not</b> applied on the
        /// success path — a resolution that succeeded must leave no diagnostic behind, which is
        /// what <c>AThrowingProviderLeavesNoDiagnosticBehindWhenTheResolutionThenSucceeds</c> pins.
        /// </remarks>
        private static string WithProviderFailure(string reason, string providerFailure)
            => providerFailure == null ? reason : reason + "; " + providerFailure;

        /// <summary>Drops <see cref="FontFileLocator"/>'s cached scans. For tests, and for hosts
        /// that install fonts after startup.</summary>
        public static void ResetCache() => FontFileLocator.ResetCache();

        // ── internals ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// How many files to try per family before moving to the next. A family whose first few
        /// faces all fail the coverage test is the wrong family, not a deeper list.
        /// </summary>
        private const int MaxCandidatesPerFamily = 8;

        private static IEnumerable<string> FamilyCandidates(string hint, List<int> required)
        {
            if (!string.IsNullOrEmpty(hint)) yield return hint;

            // Script fallbacks, chosen from what the text actually needs rather than from a fixed
            // list, so a Latin-only request never pays for the Hebrew probe.
            if (NeedsHebrew(required)) yield return "Hebrew";
            yield return "Arial";
            yield return "Times New Roman";
        }

        private static bool NeedsHebrew(List<int> required)
        {
            foreach (int cp in required)
                if (cp >= 0x0590 && cp <= 0x05FF) return true;
            return false;
        }

        private static List<int> Distinct(IReadOnlyCollection<int> source)
        {
            var list = new List<int>();
            if (source == null) return list;
            var seen = new HashSet<int>();
            foreach (int cp in source)
            {
                // Whitespace is not evidence of coverage: nearly every font has a space, so
                // requiring it tells us nothing and excluding it costs nothing.
                if (cp == ' ' || cp == '\t' || cp == '\r' || cp == '\n') continue;
                if (seen.Add(cp)) list.Add(cp);
            }
            return list;
        }

        /// <summary>
        /// Whether <paramref name="program"/> can draw every one of <paramref name="required"/>,
        /// reporting <paramref name="missing"/> — <b>all</b> the code points it cannot, or null on
        /// success.
        /// </summary>
        /// <remarks>
        /// It collects rather than short-circuiting because the caller's own font is the one case
        /// where naming the gap is actionable: "your font does not cover U+0378" tells a caller
        /// what to change, and the whole requested set — which is what the failure reported before,
        /// truncated to its first four entries — hides the offending character behind the ones that
        /// were fine. The system-search loops pass <c>out _</c>; there the answer that matters is
        /// which candidate won, not how each one lost.
        /// </remarks>
        private static bool Covers(TrueTypeFontProgram program, List<int> required, out List<int> missing)
        {
            missing = null;
            foreach (int cp in required)
                if (!program.CoversShaped(cp)) (missing ??= new List<int>()).Add(cp);

            return missing == null;
        }

        private static TrueTypeFontProgram Parse(byte[] bytes, bool allowCff)
        {
            if (bytes == null || bytes.Length == 0) return null;
            TrueTypeFontProgram program;
            try { program = TrueTypeFontProgram.Parse(bytes); }
            catch (Exception) { return null; }

            if (program == null) return null;
            if (!allowCff && program.HasCff) return null;
            return program;
        }

        private static byte[] ReadAllBytes(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The tail every system-search pass shares: read the file, parse it, apply the licence
        /// gate and the real coverage test, and answer with the resolution when all four hold.
        /// <c>null</c> means "not this one" — the caller keeps looking.
        /// </summary>
        /// <remarks>
        /// <para><b>Three passes carried these five lines each.</b> Named families (step 3), the
        /// family a file does not admit to in its <c>name</c> table (step 3b) and the bounded scan
        /// (step 4) differ entirely in <i>which</i> files they offer and not at all in what they do
        /// with one — so the acceptance rule was written three times while the decision it makes is
        /// one decision. GAPS.md §7 put it as: three copies of a licence gate is where one pass
        /// comes to accept what another refuses.</para>
        ///
        /// <para><b>The licence gate runs before the coverage test, and a mutation swapping the two
        /// survives — which is worth knowing rather than hiding.</b> The intent is that a font this
        /// host may not embed is recorded as a licence skip rather than counted as "did not cover
        /// the text", which is what <c>WithLicenceSkips</c> reports. But every caller runs
        /// <see cref="FontCoverageProbe"/> <i>first</i> and skips the file on a definite <c>false</c>,
        /// so by the time execution arrives here a non-covering font has almost always already been
        /// dropped. The order is therefore observable only for a font the probe <b>abstained</b> on
        /// — and it abstains for a WOFF container (which the locator does not enumerate), a
        /// collection (skipped by name), a file the full parse also fails, or a <c>cmap</c> over
        /// 4 MiB. Keep the order as the intent says; do not describe it as load-bearing.</para>
        ///
        /// <para>For the same reason the <c>Covers</c> call below is <b>defence in depth behind the
        /// probe</b> rather than a live decision: the probe may only ever reject, and where it
        /// answers <c>true</c> it agrees with the full parse by construction — both reach the same
        /// <c>TrueTypeFontProgram.Covers</c>, which <c>FontCoverageProbeParityTests</c> asserts
        /// against every font on the host.</para>
        ///
        /// <para><b>The cheap probe is deliberately NOT here.</b> <c>FontCoverageProbe</c> is called
        /// by each pass before this, and the passes disagree about what a null probe means — step 3b
        /// skips an unnameable file because step 4 will reach it anyway, where a resolver whose
        /// family pass is the only pass must escalate instead. That difference is the caller's, so
        /// folding the probe in would have flattened a real distinction into a shared default.</para>
        /// </remarks>
        private static FontResolution TryAccept(string path, FontResolutionRequest request,
                                                List<int> required, List<string> licenceSkips)
        {
            byte[] bytes = ReadAllBytes(path);
            var program = Parse(bytes, request.AllowCffOutlines);
            if (program == null) return null;
            if (!Embeddable(program, request, path, licenceSkips)) return null;
            if (!Covers(program, required, out _)) return null;

            return Success(program, bytes, path, fromSystem: true);
        }

        private static FontResolution Success(TrueTypeFontProgram program, byte[] bytes,
                                              string path, bool fromSystem)
        {
            // Cleared on the way out, not only on the way in. Resetting at entry is not enough:
            // a provider that throws records its reason mid-call, and if the search then succeeds
            // from a system font the slot would still hold that reason — so a caller reading it
            // after a successful resolution sees a failure that did not happen. Same shape as the
            // LastPermsValidation defect CLAUDE.md records: per-thread storage is not per-outcome
            // scope, and every path that finishes the operation has to say what the outcome was.
            _lastResolutionError = null;
            return new FontResolution(program, bytes, path, fromSystem, null, null);
        }

        private static FontResolution Fail(List<int> uncovered, string reason)
        {
            _lastResolutionError = reason;
            return new FontResolution(null, null, null, false, uncovered, reason);
        }

        private static string Describe(List<int> codePoints)
        {
            if (codePoints.Count == 0) return "the requested text";
            var sb = new System.Text.StringBuilder();
            int shown = System.Math.Min(codePoints.Count, 4);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("U+").Append(codePoints[i].ToString("X4"));
            }
            if (codePoints.Count > shown) sb.Append(" (and ").Append(codePoints.Count - shown).Append(" more)");
            return sb.ToString();
        }
    }
}
