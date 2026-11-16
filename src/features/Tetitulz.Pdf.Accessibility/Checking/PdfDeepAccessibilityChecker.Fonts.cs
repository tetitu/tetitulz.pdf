// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    /// <summary>
    /// Extended PDF/UA-1 accessibility checker that supplements the base <c>PdfUaChecker</c>
    /// (which owns UA00–UA09) with deeper structural, content, font and annotation checks.
    ///
    /// <list type="bullet">
    ///   <item>UA06 — The security handler of an <i>encrypted</i> document must permit content
    ///     extraction for accessibility (ISO 32000-1 Table 22, /P bit 10). Encryption itself is
    ///     not a PDF/UA-1 failure.</item>
    ///   <item>UA10 — Heading hierarchy, for both the numbered (H1…H6) and the "strong"
    ///     (&lt;H&gt; nested in &lt;Sect&gt;) PDF/UA models, including the mixed-model failure.</item>
    ///   <item>UA11 — List structure: L → LI → Lbl + LBody, in that order (PDF/UA-1 7.6).</item>
    ///   <item>UA12 — Table structure: Table &gt; TR &gt; TD/TH nesting, TH addressability
    ///     (/Scope or /Headers), and table <i>regularity</i> once /ColSpan and /RowSpan are
    ///     resolved (Matterhorn 15-003).</item>
    ///   <item>UA13 — Role mapping: every element's /S must be standard or resolve — through
    ///     role-map chains — to a standard type, and standard types must not be remapped.</item>
    ///   <item>UA14 — ParentTree coverage <i>and the mapping it encodes</i>: every page's
    ///     /StructParents and every annotation's /StructParent must be present in the ParentTree
    ///     and resolve to the right kind of object — and, beyond the shape of those pointers,
    ///     every MCID a structure element names must really occur on its page, the page's
    ///     /ParentTree array must name that element at that index, and every marked-content
    ///     sequence the page draws must be claimed by some element
    ///     (see <see cref="CheckMarkedContentMapping"/>).</item>
    ///   <item>UA15 — Figure alt-text quality: empty or placeholder /Alt.</item>
    ///   <item>UA16 — <i>Leaf</i> structure elements that reference no content at all.</item>
    ///   <item>UA17 — Pages that draw text belonging to no marked-content sequence.</item>
    ///   <item>UA18 — Pages that carry structural content but no /StructParents, in a document
    ///     that claims to be tagged.</item>
    ///   <item>UA19 — /MarkInfo /Suspects true.</item>
    ///   <item>UA20 — Text that can produce no Unicode (no /ToUnicode, no usable encoding).</item>
    ///   <item>UA21 — Fonts that are not embedded.</item>
    ///   <item>UA22 — Link annotations with no alternate description.</item>
    ///   <item>UA23 — Annotations that are not reachable from the structure tree.</item>
    ///   <item>UA24 — Image-only (likely scanned) pages.</item>
    ///   <item>UA30 — Structure <i>subtrees</i> whose every element, transitively, reaches no
    ///     content: the aggregate finding UA16 cannot make because UA16 is per-leaf.</item>
    ///   <item>UA99 — A check could not be completed; its findings are missing (Info).</item>
    /// </list>
    ///
    /// <para><b>Why UA16–UA19 exist.</b> Before they were added the audit could not tell a
    /// properly tagged document from one whose structure tree points at nothing: a file with a
    /// handful of empty structure elements, no MCIDs and no ParentTree produced a single
    /// Info-level remark and therefore looked very nearly conformant. Those four checks, plus
    /// raising the missing-/ParentTree finding from Info to Error, are what make hollow tagging
    /// fail the audit.</para>
    ///
    /// <para><b>Why UA30 exists on top of them.</b> UA16 asks a purely <i>local</i> question — is
    /// <i>this</i> element a leaf that points at nothing — so a tree in which every element has an
    /// element child slips past it even when the tree as a whole reaches zero content. That shape
    /// is the original hollow-tagging defect wearing a disguise, and only UA17 caught it, by
    /// accident, and only when a page happened to draw unmarked text. UA30 asks the
    /// <i>transitive</i> question instead and reports the highest element of each hollow subtree,
    /// so a remediator gets one actionable entry per broken branch rather than one per element.</para>
    ///
    /// <para><b>Reporting policy.</b> Up to <see cref="IndividualReportLimit"/> occurrences of a
    /// finding are reported one issue each, fully located (page, object number, structure path).
    /// Beyond that they collapse into a single issue carrying
    /// <see cref="AccessibilityIssue.InstanceCount"/> and the first few object numbers in its
    /// message, so a pathological document cannot flood the report while
    /// <see cref="AccessibilityAuditReport.TotalInstanceCount"/> still reflects reality.</para>
    ///
    /// <para><b>Robustness.</b> Every individual check runs inside
    /// <see cref="RunCheck"/>: a failure in one can never take down the audit. It is surfaced as
    /// a UA99 Info issue and through <see cref="AccessibilityDiagnostics"/> — never swallowed.</para>
    ///
    /// <para><b>Encrypted documents are audited, not refused.</b> PDF/UA-1 permits encryption as
    /// long as the security handler grants accessibility extraction, and dictionary structure —
    /// element roles, /K linkage, the /ParentTree, /StructParents, table geometry — is never
    /// ciphertext, so it remains fully checkable. One UA06 Error and a stop would fail a conformant
    /// file, give a non-conformant one half an audit, and contradict the sibling
    /// <c>PdfUaChecker</c>, which reads /P and keeps auditing, in one combined report. The two
    /// halves apply the same rule (see <see cref="CheckEncryption"/>) and the audit continues either way.
    /// What genuinely cannot be read — text and content streams — is named once, exactly, by
    /// <see cref="ReportEncryptionCoverage"/>; no check is silently omitted.</para>
    ///
    /// <para><b>Nor is a page whose content is merely broken.</b> The same rule applies with no
    /// encryption in sight: a stray delimiter, a PostScript calculator brace or a corrupt Flate
    /// payload leaves a page unscannable, and the four checks that need decoded content
    /// (UA14's marked-content mapping, UA17, UA18, UA24) must then skip it. Each such skip is
    /// recorded and named per page by <see cref="ReportContentCoverage"/>, so a report can never
    /// again be byte-identical to one in which those checks ran and found nothing.</para>
    ///
    /// <para><b>Nor is a page that was only half-read.</b> A page whose own <c>/Contents</c> decodes
    /// perfectly can still invoke a Form XObject that will not resolve or decode, or name a property
    /// list that is not in <c>/Properties</c>. Its counts are then lower bounds while
    /// <c>ContentUsable</c> is still true, so <see cref="ReportContentCoverage"/> — which is gated on
    /// that flag — says nothing about it. <see cref="ReportPartialContentCoverage"/> covers exactly
    /// that gap, for the same reason and at the same severity.</para>
    /// </summary>
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA20 / UA21 — Fonts
        // ================================================================

        private sealed class FontUse
        {
            internal PdfDictionary Dict;
            internal int ObjectNumber = -1;
            internal int PageNumber;
            internal string ResourceName;
        }

        private static void CheckFonts(CheckContext ctx)
        {
            var fonts = CollectFonts(ctx);
            if (fonts.Count == 0) return;

            var noUnicode      = new List<Occurrence>();
            var unverifiable   = new List<Occurrence>();
            var notEmbedded    = new List<Occurrence>();

            // UA21 is a dictionary-level fact — a /FontDescriptor either carries a /FontFile* or it
            // does not — and holds on an encrypted document. UA20 is not: deciding whether a font
            // can produce Unicode means parsing its /ToUnicode CMap stream, which is ciphertext
            // here, so every font in the file would be condemned for a mapping that is present and
            // simply unreadable.
            bool checkUnicode = !ctx.Encrypted;
            if (!checkUnicode)
            {
                NoteEncryptionLimit(ctx,
                    "UA20 (text that maps to no Unicode) was not attempted: the verdict rests on a " +
                    "font's /ToUnicode CMap, which is an encrypted stream, so running it would report " +
                    "every font in the document as producing no Unicode");

                // UA21 still runs, but which fonts ISO 14289-1 §7.21.4.1 asks about — the ones some
                // content paints with — is established from the page content streams, which were not
                // read. Say so rather than let the narrowing quietly stop applying.
                NoteEncryptionLimit(ctx,
                    "UA21 (font embedding) ran, but which fonts the content paints with could not be " +
                    "established — that is read from the encrypted page content streams — so every " +
                    "font without an embedded program is reported, including one an OCR text layer " +
                    "draws only invisibly and one nothing draws with at all");
            }

            // The fonts the document paints with: one answer, shared with the plugin's UA25.
            ICollection<PdfDictionary> painted = PdfUaFontUse.PaintedFonts(ctx.Doc);
            if (PdfUaFontUse.LastError != null)
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CheckFonts", PdfUaFontUse.LastError);

            foreach (FontUse font in fonts)
            {
                UnicodeCapability capability = checkUnicode
                    ? ClassifyUnicodeCapability(ctx, font)
                    : UnicodeCapability.Ok();

                if (capability.Verdict != UnicodeVerdict.Mappable)
                {
                    var occurrence = Occurrence.ForFont(font);
                    occurrence.Detail = capability.Reason;
                    (capability.Verdict == UnicodeVerdict.NotMappable ? noUnicode : unverifiable).Add(occurrence);
                }

                if (!IsEmbedded(ctx, font, out string embeddingDetail) && IsUsedForRendering(painted, font))
                {
                    var occurrence = Occurrence.ForFont(font);
                    occurrence.Detail = embeddingDetail;
                    notEmbedded.Add(occurrence);
                }
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA20", IssueCategory.Fonts, WcagLevel.A,
                "09-004", "1.1.1", noUnicode,
                o => "Font " + o.Detail + " can produce no Unicode, so any text drawn with it cannot be " +
                     "read aloud, searched, copied or extracted.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " font(s) can produce no Unicode (e.g. " +
                    objects + "), so text drawn with them cannot be read aloud, searched, copied or extracted.",
                "Embed a /ToUnicode CMap in each font dictionary mapping every character code the document " +
                "uses to its Unicode value. For simple fonts a standard /Encoding (WinAnsiEncoding, " +
                "MacRomanEncoding, StandardEncoding) is also sufficient.");

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA20", IssueCategory.Fonts, WcagLevel.A,
                "09-004", "1.1.1", unverifiable,
                o => "Font " + o.Detail + " — this checker cannot confirm the text maps to Unicode.",
                (count, objects) =>
                    "This checker cannot confirm that " + count.ToString(CultureInfo.InvariantCulture) +
                    " font(s) map their text to Unicode (e.g. " + objects + ").",
                "Add an explicit /ToUnicode CMap so the mapping is unambiguous and does not depend on the " +
                "consumer having the referenced CMap or glyph-name tables available.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA21", IssueCategory.Fonts, WcagLevel.None,
                null, null, notEmbedded,
                o => "Font " + o.Detail + " is not embedded. PDF/UA requires every font to be embedded — " +
                     "including the standard 14, for which it grants no exemption — so the document renders " +
                     "and reflows identically everywhere.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " font(s) are not embedded (e.g. " + objects +
                    "). PDF/UA requires every font to be embedded, including the standard 14, for which it " +
                    "grants no exemption.",
                "Embed each font as /FontFile (Type 1), /FontFile2 (TrueType) or /FontFile3 (CFF/OpenType) " +
                "on its /FontDescriptor. Subsetting is fine as long as every glyph used is present.");
        }

        /// <summary>
        /// Gathers every distinct font dictionary reachable from the page resources, following
        /// Form XObject resources too (a great deal of real content is drawn inside them). The
        /// same font instance reached from several pages is reported once, against the first page
        /// it was seen on.
        /// </summary>
        private static List<FontUse> CollectFonts(CheckContext ctx)
        {
            var seenFonts = new Dictionary<PdfObject, FontUse>(PdfObjectIndex.ReferenceIdentityComparer);
            var order = new List<FontUse>();

            foreach (PageInfo page in ctx.Pages)
            {
                if (page.Resources == null) continue;
                var visitedResources = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);
                CollectFontsFrom(ctx, page.Resources, page.PageNumber, seenFonts, order, visitedResources, 0);
            }

            return order;
        }

        private static void CollectFontsFrom(
            CheckContext ctx,
            PdfDictionary resources,
            int pageNumber,
            Dictionary<PdfObject, FontUse> seenFonts,
            List<FontUse> order,
            HashSet<PdfObject> visitedResources,
            int depth)
        {
            if (resources == null || depth > MaxResourceDepth) return;
            if (!visitedResources.Add(resources)) return;   // resource dictionaries can be shared cyclically

            PdfDictionary fonts = ResolveDictionary(ctx.Doc, resources[PdfName.Font]);
            if (fonts != null)
            {
                foreach (var kvp in fonts)
                {
                    PdfDictionary fontDict = ResolveDictionary(ctx.Doc, kvp.Value);
                    if (fontDict == null) continue;
                    if (seenFonts.ContainsKey(fontDict)) continue;

                    var use = new FontUse
                    {
                        Dict = fontDict,
                        ObjectNumber = GetObjectNumber(ctx, kvp.Value, fontDict),
                        PageNumber = pageNumber,
                        ResourceName = kvp.Key.Value,
                    };
                    seenFonts[fontDict] = use;
                    order.Add(use);
                }
            }

            PdfDictionary xobjects = ResolveDictionary(ctx.Doc, resources[PdfName.XObject]);
            if (xobjects != null)
            {
                foreach (var kvp in xobjects)
                {
                    PdfDictionary xo = ResolveDictionary(ctx.Doc, kvp.Value);
                    if (xo == null) continue;
                    if (ResolvedName(ctx, xo, PdfName.Subtype) != "Form") continue;

                    PdfDictionary nested = ResolveDictionary(ctx.Doc, xo[PdfName.Resources]);
                    CollectFontsFrom(ctx, nested, pageNumber, seenFonts, order, visitedResources, depth + 1);
                }
            }

            // Tiling patterns carry their own /Resources, and a font used only there is still a
            // font the document renders with — UA21 grants no exemption based on where a font is
            // drawn from.
            //
            // Patterns and Type 3 /CharProcs too, not form XObjects only: a font living solely in a
            // tiling pattern would otherwise produce "(no issues at all)" from the whole deep checker
            // while `Fonts.PdfFontEmbeddingRemediator` — the *fixer* for this very violation — walks
            // patterns and Type 3 /CharProcs and embeds it, the checker/fixer drift
            // `AcroFormFieldWalk` exists to prevent elsewhere.
            PdfDictionary patterns = ResolveDictionary(ctx.Doc, resources[PdfName.Pattern]);
            if (patterns != null)
            {
                foreach (var kvp in patterns)
                {
                    PdfDictionary pattern = ResolveDictionary(ctx.Doc, kvp.Value);
                    if (pattern == null) continue;
                    // Only a tiling pattern (/PatternType 1) has content and resources of its own;
                    // a shading pattern paints a gradient and can draw no text.
                    if (GetIntegerValue(ctx, pattern[PdfName.PatternType]) != 1) continue;

                    PdfDictionary nested = ResolveDictionary(ctx.Doc, pattern[PdfName.Resources]);
                    CollectFontsFrom(ctx, nested, pageNumber, seenFonts, order, visitedResources, depth + 1);
                }
            }

            // A Type 3 font's glyph procedures are content streams painted with the /Resources of
            // the Type 3 font itself, which may name further fonts.
            PdfDictionary fontsForType3 = ResolveDictionary(ctx.Doc, resources[PdfName.Font]);
            if (fontsForType3 != null)
            {
                foreach (var kvp in fontsForType3)
                {
                    PdfDictionary fontDict = ResolveDictionary(ctx.Doc, kvp.Value);
                    if (fontDict == null) continue;
                    if (ResolvedName(ctx, fontDict, PdfName.Subtype) != "Type3") continue;

                    PdfDictionary nested = ResolveDictionary(ctx.Doc, fontDict[PdfName.Resources]);
                    CollectFontsFrom(ctx, nested, pageNumber, seenFonts, order, visitedResources, depth + 1);
                }
            }
        }

        /// <summary>Integer value of a possibly-indirect number, or 0.</summary>
        private static int GetIntegerValue(CheckContext ctx, PdfObject obj)
            => ResolveObject(ctx.Doc, obj) is PdfNumber n ? n.IntValue : 0;

        private enum UnicodeVerdict { Mappable, NotMappable, Unverifiable }

        private readonly struct UnicodeCapability
        {
            internal UnicodeVerdict Verdict { get; }
            internal string Reason { get; }

            internal UnicodeCapability(UnicodeVerdict verdict, string reason)
            {
                Verdict = verdict;
                Reason  = reason;
            }

            internal static UnicodeCapability Ok() => new UnicodeCapability(UnicodeVerdict.Mappable, null);
            internal static UnicodeCapability No(string reason) => new UnicodeCapability(UnicodeVerdict.NotMappable, reason);
            internal static UnicodeCapability Unsure(string reason) => new UnicodeCapability(UnicodeVerdict.Unverifiable, reason);
        }

        /// <summary>
        /// Decides whether a font can yield Unicode at all.
        ///
        /// <para>This reuses Core's font machinery rather than re-implementing it: the /ToUnicode
        /// stream is parsed with <see cref="CMapParser"/> (the same parser
        /// <see cref="PdfFont.Create"/> uses), predefined CMap availability is asked of
        /// <see cref="CMapParser.IsPredefinedCMapRegistered"/>, and glyph names in /Differences are
        /// resolved with <see cref="StandardEncodings.TryGetGlyphUnicode"/>.</para>
        /// </summary>
        private static UnicodeCapability ClassifyUnicodeCapability(CheckContext ctx, FontUse font)
        {
            string describe = DescribeFont(ctx, font);
            string subtype = ResolvedName(ctx, font.Dict, PdfName.Subtype);

            if (HasUsableToUnicode(ctx, font.Dict, out string toUnicodeProblem)) return UnicodeCapability.Ok();

            // A broken /ToUnicode does not by itself condemn the font: a simple font with
            // WinAnsiEncoding still maps to Unicode perfectly well. Only when the encoding route
            // also fails does the broken CMap become part of the finding.
            UnicodeCapability fallback = subtype == "Type0"
                ? ClassifyType0(ctx, font, describe)
                : ClassifySimpleFont(ctx, font, describe, subtype);

            if (fallback.Verdict == UnicodeVerdict.Mappable || toUnicodeProblem == null) return fallback;

            return new UnicodeCapability(fallback.Verdict,
                fallback.Reason + "; its /ToUnicode CMap is also unusable (" + toUnicodeProblem + ")");
        }

        private static UnicodeCapability ClassifyType0(CheckContext ctx, FontUse font, string describe)
        {
            // ISO 14289-1 7.21.7 exempts a Type 0 font over a published Adobe collection, whatever its
            // CMap — the plugin's UA26 reads the same function (test 7.21.7-t01-pass-a: Identity-H
            // over Adobe-Japan1, no /ToUnicode).
            if (FontUnicodeMapping.UsesPublishedCharacterCollection(ctx.Doc, font.Dict)) return UnicodeCapability.Ok();

            PdfObject encoding = font.Dict[PdfName.Encoding];
            if (encoding is PdfReference) encoding = ResolveObject(ctx.Doc, encoding);

            if (encoding is PdfName cmapName)
            {
                // Identity-H / Identity-V map a code to a CID and stop there; without /ToUnicode
                // there is no path from a CID to a character. This is the single most common
                // real-world PDF/UA failure.
                if (cmapName.Value == "Identity-H" || cmapName.Value == "Identity-V")
                {
                    string ordering = GetCidOrdering(ctx, font.Dict);
                    return UnicodeCapability.No(describe + " uses the " + cmapName.Value +
                        " CMap with no /ToUnicode" +
                        (ordering != null ? " (CIDSystemInfo ordering " + ordering + ")" : string.Empty) +
                        "; that maps character codes to CIDs only, never to characters");
                }

                if (CMapParser.IsPredefinedCMapRegistered(cmapName.Value)) return UnicodeCapability.Ok();

                return UnicodeCapability.Unsure(describe + " uses predefined CMap /" + cmapName.Value +
                    ", which is not available to this library, and has no /ToUnicode");
            }

            if (encoding is PdfStream)
            {
                return UnicodeCapability.No(describe +
                    " uses an embedded CMap stream with no /ToUnicode; an embedded CMap defines code → CID " +
                    "only, so no Unicode can be recovered");
            }

            return UnicodeCapability.No(describe + " is a Type0 font with neither /ToUnicode nor a usable /Encoding CMap");
        }

        private static UnicodeCapability ClassifySimpleFont(CheckContext ctx, FontUse font, string describe, string subtype)
        {
            PdfObject encoding = font.Dict[PdfName.Encoding];
            if (encoding is PdfReference) encoding = ResolveObject(ctx.Doc, encoding);

            if (encoding is PdfName baseName)
            {
                return UnicodeCapableBaseEncodings.Contains(baseName.Value)
                    ? UnicodeCapability.Ok()
                    : UnicodeCapability.Unsure(describe + " uses the non-standard base encoding /" +
                        baseName.Value + " and has no /ToUnicode");
            }

            if (encoding is PdfDictionary encodingDict)
            {
                string baseEncoding = ResolvedName(ctx, encodingDict, PdfName.BaseEncoding);
                bool baseIsUnicodeCapable = baseEncoding != null && UnicodeCapableBaseEncodings.Contains(baseEncoding);

                var unmapped = new List<string>();
                int differencesCount = CountUnmappableDifferences(ctx, encodingDict, unmapped);

                if (differencesCount < 0)
                {
                    // No /Differences at all: the mapping is whatever the base encoding says.
                    return baseIsUnicodeCapable
                        ? UnicodeCapability.Ok()
                        : UnicodeCapability.No(describe + " has an /Encoding dictionary with neither a " +
                            "standard /BaseEncoding nor /Differences, and no /ToUnicode; the mapping " +
                            "depends entirely on the font program's built-in encoding");
                }

                if (unmapped.Count == 0)
                    return baseIsUnicodeCapable || differencesCount > 0
                        ? UnicodeCapability.Ok()
                        : UnicodeCapability.Unsure(describe + " relies on /Differences glyph names with no " +
                            "standard /BaseEncoding and no /ToUnicode");

                return UnicodeCapability.No(describe + " has " +
                    unmapped.Count.ToString(CultureInfo.InvariantCulture) +
                    " /Differences glyph name(s) that map to no Unicode (" + JoinCapped(unmapped) +
                    ") and no /ToUnicode");
            }

            // No /Encoding at all — the font program's built-in encoding decides.
            string baseFont = SubsetTag.Strip(ResolvedName(ctx, font.Dict, PdfName.BaseFont));

            if (baseFont != null && Standard14SymbolicFonts.Contains(baseFont))
                return UnicodeCapability.No(describe + " is the symbolic standard-14 face " + baseFont +
                    ", whose built-in encoding maps to private-use glyph names rather than Unicode, and it " +
                    "has no /ToUnicode");

            if (baseFont != null && Standard14TextFonts.Contains(baseFont))
                return UnicodeCapability.Ok();   // built-in StandardEncoding does map to Unicode

            if (subtype == "Type3")
                return UnicodeCapability.No(describe + " is a Type3 font with no /Encoding /Differences and " +
                    "no /ToUnicode; Type3 glyph procedures carry no character semantics at all");

            if (IsSymbolic(ctx, font.Dict))
                return UnicodeCapability.No(describe + " is flagged symbolic and has neither /Encoding nor " +
                    "/ToUnicode, so its codes map to font-specific glyphs with no character meaning");

            // Non-symbolic with no /Encoding: ISO 32000-1 §9.6.6.2 says the built-in encoding of a
            // non-symbolic simple font is StandardEncoding, whose glyph names are AGL-mappable —
            // route (a) of §9.10.2, which ISO 14289-1 §7.21.7 incorporates by reference. Reporting
            // an Error here contradicts the sibling PdfUaChecker, which accepts the same font.
            return UnicodeCapability.Ok();
        }

        private static bool HasUsableToUnicode(CheckContext ctx, PdfDictionary fontDict, out string problem)
        {
            problem = null;

            PdfObject toUnicodeObj = fontDict[PdfName.ToUnicode];
            if (toUnicodeObj == null) return false;

            if (ResolveObject(ctx.Doc, toUnicodeObj) is not PdfStream stream)
            {
                problem = "/ToUnicode is not a stream";
                return false;
            }

            byte[] data;
            try
            {
                data = FontStreamBytes.CMap(stream);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.HasUsableToUnicode",
                    "a /ToUnicode CMap could not be decoded; the font is reported as having no Unicode mapping", ex);
                problem = "the CMap stream could not be decoded: " + ex.Message;
                return false;
            }

            if (data == null || data.Length == 0) { problem = "the CMap stream is empty"; return false; }

            try
            {
                Dictionary<int, string> map = CMapParser.Parse(data, out _);
                if (map != null && map.Count > 0) return true;
                problem = "the CMap defines no code ranges";
                return false;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.HasUsableToUnicode",
                    "a /ToUnicode CMap could not be parsed; the font is reported as having no Unicode mapping", ex);
                problem = "the CMap could not be parsed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Counts glyph names in /Differences that resolve to no Unicode. Returns -1 when the
        /// encoding dictionary has no /Differences array at all.
        /// </summary>
        private static int CountUnmappableDifferences(CheckContext ctx, PdfDictionary encodingDict, List<string> unmapped)
        {
            if (ResolveObject(ctx.Doc, encodingDict[PdfName.Differences]) is not PdfArray differences) return -1;

            int named = 0;
            for (int i = 0; i < differences.Count; i++)
            {
                if (differences[i] is not PdfName glyph) continue;   // the interleaved code numbers
                named++;

                // ".notdef" is the deliberate "no glyph" name; it is not a mapping failure.
                if (glyph.Value == ".notdef") continue;
                if (StandardEncodings.TryGetGlyphUnicode(glyph.Value, out _)) continue;
                if (unmapped.Count < 20) unmapped.Add(glyph.Value);
            }

            return named;
        }

        /// <summary>
        /// Whether ISO 14289-1 §7.21.4.1 asks for the font to be embedded: whether some content of
        /// the document paints text with it.
        /// </summary>
        /// <remarks>
        /// <para>The clause requires "the font programs for all fonts used for rendering" and says
        /// what used means — "at least one of its glyphs is referenced from a content stream" —
        /// with NOTE 2 exempting a font referenced only in render mode 3, which paints nothing. So
        /// a font an OCR text layer draws invisibly is not asked about, and neither is one a
        /// resource dictionary names and nothing draws with. Exempting only the invisible font would
        /// read "never painted visibly" and "never painted" as different, forgiving only the first;
        /// the clause's own definition of use settles it the other way, and the plugin's UA25 — which
        /// answers the same question — answers it the same way, from the same <see cref="PdfUaFontUse"/>.</para>
        ///
        /// <para>That shared answer reads every stream the page draws — including tiling patterns,
        /// Type 3 glyph procedures and annotation appearances, which this checker's own page scan
        /// did not — and is asked of the font <i>object</i>, across the whole document: one visible
        /// painting anywhere makes a font used. When it cannot be established (<paramref name="painted"/>
        /// is null) every font is treated as used.</para>
        /// </remarks>
        private static bool IsUsedForRendering(ICollection<PdfDictionary> painted, FontUse font)
            => painted == null || font.Dict == null || painted.Contains(font.Dict);

        private static bool IsEmbedded(CheckContext ctx, FontUse font, out string detail)
        {
            string describe = DescribeFont(ctx, font);
            string subtype = ResolvedName(ctx, font.Dict, PdfName.Subtype);

            // A Type3 font's glyphs ARE content streams inside the PDF; there is nothing to embed.
            if (subtype == "Type3") { detail = null; return true; }

            PdfDictionary descriptorOwner = font.Dict;

            if (subtype == "Type0")
            {
                PdfDictionary descendant = null;
                if (ResolveObject(ctx.Doc, font.Dict[PdfName.DescendantFonts]) is PdfArray descendants && descendants.Count > 0)
                    descendant = ResolveDictionary(ctx.Doc, descendants[0]);

                if (descendant == null)
                {
                    detail = describe + " (Type0 font with no readable /DescendantFonts entry)";
                    return false;
                }
                descriptorOwner = descendant;
            }

            PdfDictionary descriptor = ResolveDictionary(ctx.Doc, descriptorOwner[PdfName.FontDescriptor]);
            if (descriptor == null)
            {
                detail = describe + " (no /FontDescriptor)";
                return false;
            }

            if (descriptor[PdfName.FontFile] != null
                || descriptor[PdfName.FontFile2] != null
                || descriptor[PdfName.FontFile3] != null)
            {
                detail = null;
                return true;
            }

            detail = describe + " (its /FontDescriptor carries no /FontFile, /FontFile2 or /FontFile3)";
            return false;
        }

        // One implementation, in Core.Fonts.FontFlags — shared with Core's own simple-font encoding
        // decision and with PdfUaChecker, which cannot reference this assembly.
        //
        // This copy had bit 3 right and no descriptor-less fallback at all. The exact names /Symbol
        // and /ZapfDingbats are caught earlier, by the Standard14SymbolicFonts test above, so the
        // gap here is narrower than it looks: it is a descriptor-less font whose stripped /BaseFont
        // STARTS WITH one of those — "SymbolMT", "Symbol+Bold" — which the exact-match set misses
        // and which PdfUaChecker has always reported. Routing through FontFlags makes the two
        // checkers agree on those; it does not change the exact-name case, which was never wrong.
        private static bool IsSymbolic(CheckContext ctx, PdfDictionary fontDict)
            => Core.Fonts.FontFlags.IsSymbolic(fontDict, ctx.Doc);

        private static string GetCidOrdering(CheckContext ctx, PdfDictionary type0)
        {
            if (ResolveObject(ctx.Doc, type0[PdfName.DescendantFonts]) is not PdfArray descendants
                || descendants.Count == 0) return null;

            PdfDictionary descendant = ResolveDictionary(ctx.Doc, descendants[0]);
            PdfDictionary info = ResolveDictionary(ctx.Doc, descendant?[PdfName.CIDSystemInfo]);
            if (info == null) return null;

            string registry = (ResolveObject(ctx.Doc, info[PdfName.Registry]) as PdfString)?.GetText();
            string ordering = (ResolveObject(ctx.Doc, info[PdfName.Ordering]) as PdfString)?.GetText();
            if (string.IsNullOrEmpty(ordering)) return null;

            return string.IsNullOrEmpty(registry) ? ordering : registry + "-" + ordering;
        }

        private static string DescribeFont(CheckContext ctx, FontUse font)
        {
            string baseFont = ResolvedName(ctx, font.Dict, PdfName.BaseFont);
            string subtype  = ResolvedName(ctx, font.Dict, PdfName.Subtype);

            var sb = new StringBuilder(48);
            sb.Append('/').Append(font.ResourceName ?? "?");
            if (!string.IsNullOrEmpty(baseFont)) sb.Append(" (").Append(baseFont).Append(')');
            if (!string.IsNullOrEmpty(subtype))  sb.Append(" [").Append(subtype).Append(']');
            return sb.ToString();
        }

        // "ABCDEF+Helvetica" → "Helvetica" (ISO 32000-1 §9.6.4): Core.Fonts.SubsetTag, once, for
        // the side that CHECKS a subset tag and the side that WRITES one, so the two cannot hold
        // different ideas of what a tag is (whether a bare "ABCDEF+" counts, say).
    }
}
