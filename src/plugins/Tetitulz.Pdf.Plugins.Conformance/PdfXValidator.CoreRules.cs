// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The core rules the PDF Association's <i>PDF/X in a Nutshell</i> (CC BY 4.0,
    /// <c>docs/iso</c>) lists as applying "in all parts and conformance levels of PDF/X" — X14 to
    /// X19. ISO 15930 itself is not available; GAPS.md §12b records the owner's rule for building
    /// a check from public sources, and which of the brochure's rules stay open and why.
    /// </summary>
    public static partial class PdfXValidator
    {
        // ----------------------------------------------------------------
        //  X14 — no transfer curves
        // ----------------------------------------------------------------

        /// <summary>
        /// "No transfer curves (since they modify appearance of colors)": an <c>/ExtGState</c>
        /// reachable from a print element that sets <c>/TR</c> or <c>/TR2</c>.
        /// </summary>
        /// <remarks>
        /// What counts is <see cref="TransferFunctionEntries"/>'s answer, shared with PDF 2.0's
        /// deprecation report. A halftone's own <c>/TransferFunction</c> is not checked: a type 5
        /// halftone <i>requires</i> one for a non-primary component (ISO 32000-1 Table 130), so the
        /// brochure's one-liner cannot settle it (GAPS.md §12b).
        /// </remarks>
        private static void CheckTransferFunctions(PdfDocument doc, List<PdfXViolation> v,
            List<PdfXViolation> info, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            long documentBytes = 0;
            var seen = new HashSet<PdfDictionary>();   // a dictionary pages share is judged once

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: false, printElementsOnly: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X14 transfer-function check did not run in full — {scan.Incomplete}."));

                foreach (var res in scan.Resources)
                {
                    if (!seen.Add(res)) continue;
                    var egsDict = Resolve<PdfDictionary>(doc, res[PdfName.ExtGState]);
                    if (egsDict == null) continue;

                    foreach (var kvp in egsDict)
                    {
                        var gs = Resolve<PdfDictionary>(doc, kvp.Value);
                        if (gs == null) continue;

                        foreach (var key in TransferFunctionEntries.Declared(doc, gs))
                        {
                            v.Add(new PdfXViolation("X14",
                                $"PDF/X does not allow transfer curves; /ExtGState /{kvp.Key.Value} on " +
                                $"page {p + 1} sets /{key.Value}."));
                            return;
                        }
                    }
                }
            }
        }

        // ----------------------------------------------------------------
        //  X15 — no LZW compression; X16 — no embedded files
        // ----------------------------------------------------------------

        /// <summary>
        /// "No use of LZW compression" (X15) and "No embedded files" (X16). Both are about the
        /// file, not about what prints, so both read every object the cross-reference table lists.
        /// </summary>
        /// <remarks>
        /// <para><b>X15</b> reads each stream's <c>/Filter</c>, and each inline image's
        /// <c>/F</c> or <c>/Filter</c> in every content stream a page reaches, non-printing
        /// annotations' included.</para>
        /// <para><b>X16</b> reports the catalog's <c>/Names /EmbeddedFiles</c> key, a
        /// <c>/FileAttachment</c> annotation, and a stream naming itself <c>/Type /EmbeddedFile</c>.
        /// The last catches a file that the first two do not reach.</para>
        /// <para>Two passes share one object walk. An object the parser cannot read is skipped and
        /// counted in an X99, because neither rule can say what it holds. The LZW reading is
        /// <see cref="LzwUse"/>'s, which PDF/A's A30 asks too.</para>
        /// </remarks>
        private static void CheckLzwAndEmbeddedFiles(PdfDictionary catalog, PdfDocument doc,
            List<PdfXViolation> v, List<PdfXViolation> info,
            int maxPageContentBytes, long maxDocumentContentBytes)
        {
            string lzw = null;
            string embeddedStream = null;

            int unreadable = LzwUse.EachStream(doc, (number, stream) =>
            {
                if (lzw == null && LzwUse.NamesLzw(stream[PdfName.Filter]))
                    lzw = $"stream object {number}";
                if (embeddedStream == null && ResolveName(doc, stream, PdfName.Type) == PdfName.EmbeddedFile.Value)
                    embeddedStream = $"an embedded file stream (object {number})";
            });

            if (unreadable > 0)
                info.Add(new PdfXViolation("X99",
                    $"The X15 and X16 checks skipped {unreadable} object(s) the parser could not read."));

            // ---- X16 ----
            var found = new List<string>();
            var names = Resolve<PdfDictionary>(doc, catalog[PdfName.Names]);
            if (names?[PdfName.EmbeddedFiles] != null)
                found.Add("the catalog's /Names /EmbeddedFiles");

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                if (HasAnnotation(doc, doc.Pages[p], PdfAnnotationSubtypes.FileAttachment))
                {
                    found.Add($"a /FileAttachment annotation on page {p + 1}");
                    break;
                }
            }
            if (embeddedStream != null) found.Add(embeddedStream);

            if (found.Count > 0)
                v.Add(new PdfXViolation("X16",
                    "PDF/X does not allow embedded files; found " + string.Join("; ", found) + "."));

            // ---- X15, inline images ----
            if (lzw == null)
            {
                long documentBytes = 0;
                for (int p = 0; p < doc.Pages.Count && lzw == null; p++)
                {
                    var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                        ref documentBytes, maxDocumentContentBytes, collectContent: true);

                    if (scan.Incomplete != null)
                        info.Add(new PdfXViolation("X99",
                            $"Page {p + 1}: the X15 inline-image LZW check did not run in full — {scan.Incomplete}."));

                    foreach (byte[] content in scan.ContentStreams)
                    {
                        if (LzwUse.InlineImageUsesLzw(content))
                        {
                            lzw = $"an inline image on page {p + 1}";
                            break;
                        }
                    }
                }
            }

            if (lzw != null)
                v.Add(new PdfXViolation("X15",
                    $"PDF/X does not allow LZW compression; {lzw} uses /LZWDecode."));
        }

        // ----------------------------------------------------------------
        //  X17 — a BleedBox encloses the TrimBox
        // ----------------------------------------------------------------

        /// <summary>
        /// Containment slack, in points: an edge that coincides once written and read back must
        /// not fail. A thousandth of a point is far below any prepress tolerance.
        /// </summary>
        private const double BoxTolerance = 1e-3;

        /// <summary>
        /// "If the bleed zone is defined, the BleedBox must be outside the printable area (the
        /// TrimBox)": a page that declares both boxes must have its trim inside its bleed.
        /// </summary>
        /// <remarks>
        /// Presence is asked of the page <b>dictionary</b>: <c>PdfPage.BleedBox</c> and
        /// <c>TrimBox</c> default to the crop box (§14.11.2), so they answer for every page. The
        /// brochure names the TrimBox only, so a page using an <c>/ArtBox</c> instead is not
        /// judged.
        /// </remarks>
        private static void CheckBleedBox(PdfDocument doc, List<PdfXViolation> v)
        {
            var pages = new List<int>();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var pd = doc.Pages[i].Dictionary;
                if (!TryReadBox(doc, pd[PdfName.BleedBox], out var bleed)) continue;
                if (!TryReadBox(doc, pd[PdfName.TrimBox], out var trim)) continue;

                if (trim.X0 < bleed.X0 - BoxTolerance || trim.Y0 < bleed.Y0 - BoxTolerance ||
                    trim.X1 > bleed.X1 + BoxTolerance || trim.Y1 > bleed.Y1 + BoxTolerance)
                    pages.Add(i + 1);
            }

            if (pages.Count > 0)
                v.Add(new PdfXViolation("X17",
                    "PDF/X requires a /BleedBox, where one is given, to enclose the /TrimBox. " +
                    $"It does not on page(s): {string.Join(", ", pages)}."));
        }

        /// <summary>A four-number box, each number resolved, normalised per §7.9.5.</summary>
        private static bool TryReadBox(PdfDocument doc, PdfObject value,
            out (double X0, double Y0, double X1, double Y1) box)
        {
            box = default;
            var arr = Resolve<PdfArray>(doc, value);
            if (arr == null || arr.Count != 4) return false;

            var n = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!(Resolve<PdfNumber>(doc, arr[i]) is PdfNumber num)) return false;
                n[i] = num.DoubleValue;
                if (double.IsNaN(n[i]) || double.IsInfinity(n[i])) return false;
            }
            box = (Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
            return true;
        }

        // ----------------------------------------------------------------
        //  X18 — one alternate per spot colour
        // ----------------------------------------------------------------

        /// <summary>Object nodes the X18 comparisons may examine across one document.</summary>
        private const int MaxSpotComparisonNodes = 100_000;

        /// <summary>Decoded bytes one tint-transform stream may take when compared.</summary>
        private const long MaxSpotStreamBytes = 16L * 1024 * 1024;

        /// <summary>Decoded bytes the X18 comparisons may compare across one document, every pair of streams charged.</summary>
        private const long MaxSpotComparisonBytes = 256L * 1024 * 1024;

        /// <summary>
        /// "Spot colors may only be used if they have an alternate color, and this alternate color
        /// must be the same for all occurrences of the respective spot color": every
        /// <c>/Separation</c> space with one colorant name must have the same alternate space and
        /// the same tint transform.
        /// </summary>
        /// <remarks>
        /// <para><b>The objects are compared, not their results</b>, the way ISO 19005-2 words the
        /// same rule for PDF/A-2 (6.2.4.4: "the PDF objects shall be compared, rather than the
        /// computational result of the use of those PDF objects. Compression and whether or not an
        /// object is direct or indirect shall be ignored"). <see cref="PdfObjectEquivalence"/> does
        /// that.</para>
        /// <para>A separation is found wherever <see cref="ColourSpaceSites"/> reaches a colour
        /// space, inside an <c>/Indexed</c> base, and in a DeviceN's <c>/Colorants</c>.
        /// <c>/All</c> and <c>/None</c> are not spot colours and are skipped.</para>
        /// </remarks>
        private static void CheckSpotColourAlternates(PdfDocument doc, List<PdfXViolation> v,
            List<PdfXViolation> info, int maxPageContentBytes, long maxDocumentContentBytes)
        {
            var first = new Dictionary<string, (PdfArray Space, int Page)>(StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var inconsistent = new List<string>();
            int budget = MaxSpotComparisonNodes;
            long byteBudget = MaxSpotComparisonBytes;
            bool undecided = false;
            long documentBytes = 0;
            var seen = new HashSet<PdfDictionary>();   // a dictionary pages share holds the same spaces on each

            // A verdict is remembered per pair of objects: a file naming one tint transform ten
            // thousand times against the first compares the pair once, not once a site at the
            // stream's full decoded length; the byte budget prices what is compared.
            var verdicts = new Dictionary<PdfObject, Dictionary<PdfObject, PdfObjectEquivalence.Result>>(PdfReferenceEquality.Instance);
            PdfObjectEquivalence.Result Compare(PdfObject a, PdfObject b)
            {
                a = doc.ResolveObject(a) ?? PdfNull.Instance;
                b = doc.ResolveObject(b) ?? PdfNull.Instance;
                if (!verdicts.TryGetValue(a, out var row))
                    verdicts[a] = row = new Dictionary<PdfObject, PdfObjectEquivalence.Result>(PdfReferenceEquality.Instance);
                if (row.TryGetValue(b, out var known)) return known;
                var verdict = PdfObjectEquivalence.Compare(doc, a, b, ref budget, MaxSpotStreamBytes, ref byteBudget);
                row[b] = verdict;
                return verdict;
            }

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var scan = ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                    ref documentBytes, maxDocumentContentBytes, collectContent: false, printElementsOnly: true);

                if (scan.Incomplete != null)
                    info.Add(new PdfXViolation("X99",
                        $"Page {p + 1}: the X18 spot-colour check did not run in full — {scan.Incomplete}."));

                var separations = new List<PdfArray>();
                foreach (var site in ColourSpaceSites(doc, doc.Pages[p], scan, seen))
                    CollectSeparations(doc, site.Space, 0, separations);

                foreach (var sep in separations)
                {
                    string colorant = Resolve<PdfName>(doc, sep[1])?.Value;
                    if (colorant == null || colorant == "All" || colorant == "None") continue;

                    if (!first.TryGetValue(colorant, out var earlier))
                    {
                        first[colorant] = (sep, p + 1);
                        continue;
                    }
                    if (ReferenceEquals(earlier.Space, sep) || reported.Contains(colorant)) continue;

                    var alternate = Compare(earlier.Space[2], sep[2]);
                    var tint = alternate == PdfObjectEquivalence.Result.Different
                        ? alternate
                        : Compare(earlier.Space[3], sep[3]);

                    if (alternate == PdfObjectEquivalence.Result.Different
                        || tint == PdfObjectEquivalence.Result.Different)
                    {
                        reported.Add(colorant);
                        inconsistent.Add(earlier.Page == p + 1
                            ? $"/{colorant} (page {earlier.Page})"
                            : $"/{colorant} (pages {earlier.Page} and {p + 1})");
                    }
                    else if (alternate == PdfObjectEquivalence.Result.Undecided
                             || tint == PdfObjectEquivalence.Result.Undecided)
                    {
                        undecided = true;
                    }
                }
            }

            if (inconsistent.Count > 0)
                v.Add(new PdfXViolation("X18",
                    "PDF/X requires a spot colour to have the same alternate wherever it is used; " +
                    "these are defined with different alternate spaces or tint transforms: " +
                    string.Join(", ", inconsistent) + "."));

            if (undecided)
                info.Add(new PdfXViolation("X99",
                    "The X18 spot-colour check could not compare every definition — a tint " +
                    "transform would not decode, nested too deeply, or the comparison budget ran out."));
        }

        /// <summary>
        /// Adds every <c>/Separation</c> array <paramref name="csObj"/> holds: itself, an
        /// <c>/Indexed</c> base, or a DeviceN's <c>/Colorants</c>. Depth-guarded.
        /// </summary>
        private static void CollectSeparations(PdfDocument doc, PdfObject csObj, int depth,
            List<PdfArray> into)
        {
            if (csObj == null || depth > PdfLimits.MaxResourceDepth) return;
            var arr = Resolve<PdfArray>(doc, csObj);
            if (arr == null || arr.Count == 0) return;

            switch (Resolve<PdfName>(doc, arr[0])?.Value)
            {
                case PdfColorSpaceNames.Separation:
                    if (arr.Count >= 4) into.Add(arr);
                    break;
                case PdfColorSpaceNames.Indexed:
                    if (arr.Count > 1) CollectSeparations(doc, arr[1], depth + 1, into);
                    break;
                case PdfColorSpaceNames.Pattern:
                    // [/Pattern base]: an uncoloured pattern's spot ink is its base space.
                    if (arr.Count > 1) CollectSeparations(doc, arr[1], depth + 1, into);
                    break;
                case PdfColorSpaceNames.DeviceN:
                {
                    var attributes = arr.Count > 4 ? Resolve<PdfDictionary>(doc, arr[4]) : null;
                    var colorants = Resolve<PdfDictionary>(doc, attributes?[PdfName.Colorants]);
                    if (colorants == null) break;
                    foreach (var kvp in colorants)
                        CollectSeparations(doc, kvp.Value, depth + 1, into);
                    break;
                }
            }
        }

        // ----------------------------------------------------------------
        //  X19 — no audio, video or 3-D annotations
        // ----------------------------------------------------------------

        /// <summary>
        /// The annotation types that play media or show a 3-D model: Sound, Movie, Screen (a
        /// rendition's surface), RichMedia and 3D.
        /// </summary>
        private static readonly HashSet<string> MediaAnnotationSubtypes = new HashSet<string>(
            StringComparer.Ordinal)
        {
            PdfAnnotationSubtypes.Sound, PdfAnnotationSubtypes.Movie, PdfAnnotationSubtypes.Screen,
            PdfAnnotationSubtypes.RichMedia, PdfAnnotationSubtypes.ThreeD,
        };

        /// <summary>
        /// "No audio, video or 3D annotations" — and, in the brochure's principle, nothing may
        /// appear on a page "that is either not printable at all (such as video or 3D)". Every
        /// annotation counts, printing or not: the rule is about the type.
        /// </summary>
        private static void CheckNonPrintableAnnotations(PdfDocument doc, List<PdfXViolation> v)
        {
            var found = new List<string>();
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var annots = Resolve<PdfArray>(doc, doc.Pages[p].Dictionary[PdfName.Annots]);
                if (!ScanReuse.AdmitAnnotations(annots)) continue;
                for (int a = 0; a < annots.Count; a++)
                {
                    string subtype = ResolveName(doc, Resolve<PdfDictionary>(doc, annots[a]), PdfName.Subtype);
                    if (subtype != null && MediaAnnotationSubtypes.Contains(subtype))
                    {
                        found.Add($"/{subtype} on page {p + 1}");
                        break;
                    }
                }
            }

            if (found.Count > 0)
                v.Add(new PdfXViolation("X19",
                    "PDF/X does not allow audio, video or 3-D annotations; found " +
                    string.Join(", ", found) + "."));
        }

        // ----------------------------------------------------------------
        //  X24 — no annotations in the print area
        // ----------------------------------------------------------------

        /// <summary>Annotations X24 reports at most, so a form of a thousand fields names a few.</summary>
        private const int MaxReportedPrintAreaAnnotations = 8;

        /// <summary>
        /// "No annotations in the print area" (<i>PDF/X in a Nutshell</i>, core principles), with
        /// the box the standards committee's own notes name: "The PDF/X standards state that most
        /// types of annotations must fall entirely outside the BleedBox. PrinterMark annotations may
        /// fall inside the BleedBox, but must still be entirely outside the TrimBox or ArtBox"
        /// (CGATS/NPES, <i>Application Notes for PDF/X Standards</i> v4, 2006, §2.28 — and §3.1.1:
        /// "PDF/X-1:2001 annotations must be located outside the TrimBox (or ArtBox) and BleedBox";
        /// <c>docs/iso/pdfx-application-notes-v4.txt</c>). The print area is therefore the
        /// <c>/BleedBox</c>, or the <c>/TrimBox</c> (<c>/ArtBox</c>) where a page has none; a
        /// <c>/PrinterMark</c> is held to the TrimBox/ArtBox alone.
        /// </summary>
        /// <remarks>
        /// <para><b>A widget is an annotation</b>, so a form field drawn in the print area is reported
        /// here, named by its field. The brochure's separate "no form fields" has no public source
        /// beyond this, and the notes name none, so a field outside the print area stays conforming
        /// (<c>AcroFormWithNoActions_DoesNotReportX07</c>) — the stricter reading would be a rule
        /// written from recollection, which §12b's stopping rule forbids.</para>
        /// <para><b>Two subtypes are not judged, each for a stated reason.</b> A <c>/TrapNet</c>
        /// annotation defines "the trapping characteristics for a page" (ISO 32000-2 §12.5.6.21) and
        /// so lies over the page by construction — the "most types" the notes except. A
        /// <c>/Popup</c> "shall have no appearance stream" and is "associated with a markup annotation,
        /// its parent" (§12.5.6.14): it draws nothing of its own, and its parent is judged.</para>
        /// <para><b>Every other annotation counts, printing or not</b> — the 2001 revision of the
        /// standard moved from "non-printing annotations may be anywhere" to "located outside"
        /// (§3.1.1), so a flag does not exempt one. <b>Touching is not overlapping</b>: the test is
        /// <see cref="Tetitulz.Pdf.Base.Operations.Extraction.PdfSpatialQuery.RectIntersects"/>, strict
        /// on every edge, so an annotation whose edge lies on the box's edge is outside it.</para>
        /// </remarks>
        private static void CheckAnnotationsInThePrintArea(PdfDocument doc, List<PdfXViolation> v)
        {
            var found = new List<string>();
            int total = 0;
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var pd = doc.Pages[p].Dictionary;
                var annots = Resolve<PdfArray>(doc, pd[PdfName.Annots]);
                if (!ScanReuse.AdmitAnnotations(annots)) continue;

                bool hasBleed = TryReadBox(doc, pd[PdfName.BleedBox], out var bleed);
                bool hasTrim = TryReadBox(doc, pd[PdfName.TrimBox], out var trim)
                               || TryReadBox(doc, pd[PdfName.ArtBox], out trim);
                if (!hasBleed && !hasTrim) continue;   // X05 reports the missing box

                for (int a = 0; a < annots.Count; a++)
                {
                    var annot = Resolve<PdfDictionary>(doc, annots[a]);
                    if (annot == null) continue;
                    string subtype = ResolveName(doc, annot, PdfName.Subtype);
                    if (subtype == PdfAnnotationSubtypes.TrapNet || subtype == PdfAnnotationSubtypes.Popup) continue;
                    if (!TryReadBox(doc, annot[PdfName.Rect], out var rect)) continue;

                    bool printerMark = subtype == PdfAnnotationSubtypes.PrinterMark;
                    if (printerMark && !hasTrim) continue;
                    var box = printerMark || !hasBleed ? trim : bleed;
                    string boxName = printerMark || !hasBleed ? (pd[PdfName.TrimBox] != null ? "TrimBox" : "ArtBox") : "BleedBox";

                    if (!Overlaps(rect, box)) continue;
                    total++;
                    if (found.Count < MaxReportedPrintAreaAnnotations)
                        found.Add($"/{subtype ?? "?"}{FieldNameSuffix(doc, annot, subtype)} at " +
                                  $"[{F(rect.X0)} {F(rect.Y0)} {F(rect.X1)} {F(rect.Y1)}] on page {p + 1} (overlaps the {boxName})");
                }
            }

            if (total > 0)
                v.Add(new PdfXViolation("X24",
                    "PDF/X requires every annotation to lie entirely outside the print area — the " +
                    "/BleedBox, or the /TrimBox (/ArtBox) where there is none; a /PrinterMark may lie " +
                    "inside the BleedBox but not the TrimBox/ArtBox (Application Notes for PDF/X v4, " +
                    "§2.28). Found " + total + ": " + string.Join(", ", found) +
                    (total > found.Count ? ", …" : "") + "."));

            static string F(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Strict overlap of two normalised boxes — the one rule, in Base.</summary>
        private static bool Overlaps((double X0, double Y0, double X1, double Y1) a,
                                     (double X0, double Y0, double X1, double Y1) b)
            => Tetitulz.Pdf.Base.Operations.Extraction.PdfSpatialQuery.RectIntersects(
                (float)a.X0, (float)a.Y0, (float)(a.X1 - a.X0), (float)(a.Y1 - a.Y0),
                (float)b.X0, (float)b.Y0, (float)(b.X1 - b.X0), (float)(b.Y1 - b.Y0));

        /// <summary>For a widget, " (field 'name')" from its own or its parent's <c>/T</c>; else empty.</summary>
        private static string FieldNameSuffix(PdfDocument doc, PdfDictionary annot, string subtype)
        {
            if (subtype != PdfAnnotationSubtypes.Widget) return "";
            var t = Resolve<PdfString>(doc, annot[PdfName.T])
                    ?? Resolve<PdfString>(doc, Resolve<PdfDictionary>(doc, annot[PdfName.Parent])?[PdfName.T]);
            return t == null ? "" : $" (field '{t.GetText()}')";
        }

        /// <summary>Whether <paramref name="page"/> carries an annotation of <paramref name="subtype"/>.</summary>
        private static bool HasAnnotation(PdfDocument doc, PdfPage page, string subtype)
        {
            var annots = Resolve<PdfArray>(doc, page.Dictionary[PdfName.Annots]);
            if (!ScanReuse.AdmitAnnotations(annots)) return false;
            for (int a = 0; a < annots.Count; a++)
                if (ResolveName(doc, Resolve<PdfDictionary>(doc, annots[a]), PdfName.Subtype) == subtype)
                    return true;
            return false;
        }
    }
}
