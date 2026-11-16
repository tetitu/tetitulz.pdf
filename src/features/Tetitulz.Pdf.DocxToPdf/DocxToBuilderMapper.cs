// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.DocxToPdf
{
    /// <summary>
    /// Maps an OOXML WordprocessingML document to the centralized
    /// <see cref="M.Document"/> model so it can be rendered by the shared Builder PDF
    /// exporter (and exported to DOCX/HTML/ODT). All <c>w:*</c> property resolution is done
    /// up-front via <see cref="StyleResolver"/>, so the model carries fully-resolved values
    /// and the renderer stays format-agnostic.
    ///
    /// This is the converter's "non-legacy" path (<see cref="DocxToPdfOptions.UseLegacyRenderer"/>
    /// = false). It covers multi-section page sizing, headers/footers (default/first-page/
    /// even-odd variants, inherited across sections through <see cref="HeaderFooterReferences"/>),
    /// page borders, paragraphs (alignment, direction, spacing, indent, line height, rich run
    /// formatting), inline images (PNG/JPEG), tables (gridSpan, vMerge, cell shading / vAlign, RTL
    /// columns, nested tables), lists (a cell's too, numbered with the body's), hyperlinks (external
    /// and to a bookmark), footnotes and endnotes (<see cref="MapNote"/>), and the PAGE and NUMPAGES
    /// fields, simple and complex alike, as live placeholders. A drawing that is not a picture is
    /// reported once, and an inline it has no case for is reported when it held text
    /// (<see cref="MapUnhandledInline"/>).
    /// </summary>
    internal sealed class DocxToBuilderMapper
    {
        private readonly WordprocessingDocument _wordDoc;
        private readonly MainDocumentPart _main;
        private readonly StyleResolver _sr;
        private readonly double _defSize;
        private readonly bool _evenOdd;
        private readonly HeaderFooterReferences _bands;

        /// <summary>The list counters of the whole conversion, shared by every section's and every cell's grouping.</summary>
        private readonly Dictionary<int, int> _listCounters = new Dictionary<int, int>();

        /// <summary>
        /// Left indent added per list nesting level (0.25&#160;inch), for the simple-list fallback
        /// this path uses in place of the legacy renderer's full <c>w:numbering</c> resolution.
        /// </summary>
        private const int ListLevelIndentPt = 18;

        private DocxToBuilderMapper(WordprocessingDocument wordDoc, DocxToPdfOptions options)
        {
            _wordDoc = wordDoc;
            _main = wordDoc.MainDocumentPart;
            _sr = new StyleResolver(wordDoc);
            _defSize = options.DefaultFontSize;
            _evenOdd = _main?.DocumentSettingsPart?.Settings?.GetFirstChild<W.EvenAndOddHeaders>() != null;
            _bands = new HeaderFooterReferences(_main);
        }

        private HashSet<string> _linkedBookmarks;

        /// <summary>
        /// The bookmark a paragraph is the target of (<see cref="M.Paragraph.AnchorName"/>): of the
        /// bookmarks it holds, the first some hyperlink's <c>w:anchor</c> names, else the first, so an
        /// internal link to a bookmark has something to land on. The model takes one name a paragraph; a second bookmark in it that a link
        /// also names is not a target on this path.
        /// </summary>
        private string BookmarkOf(W.Paragraph wp)
        {
            string first = null;
            foreach (var start in wp.Descendants<W.BookmarkStart>())
            {
                string name = start.Name?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                _linkedBookmarks ??= new HashSet<string>(
                    (_main?.Document?.Body?.Descendants<W.Hyperlink>() ?? Enumerable.Empty<W.Hyperlink>())
                        .Select(h => h.Anchor?.Value).Where(a => !string.IsNullOrEmpty(a)),
                    System.StringComparer.Ordinal);
                if (_linkedBookmarks.Contains(name)) return name;
                first ??= name;
            }
            return first;
        }

        public static M.Document Map(WordprocessingDocument wordDoc, DocxToPdfOptions options)
            => new DocxToBuilderMapper(wordDoc, options).Build(options);

        private M.Document Build(DocxToPdfOptions options)
        {
            var doc = new M.Document();
            var props = _wordDoc.PackageProperties;
            if (props != null)
            {
                doc.Title = props.Title ?? "";
                doc.Author = props.Creator ?? "";
                doc.Subject = props.Subject ?? "";
            }

            var body = _main?.Document?.Body;
            if (body == null)
            {
                var only = new M.Section();
                ApplyPageSetup(only, null, options);
                doc.Sections.Add(only);
                return doc;
            }

            foreach (var (content, sectPr) in SplitSections(body))
            {
                var section = new M.Section();
                ApplyPageSetup(section, sectPr, options);
                _bands.EnterSection(sectPr);
                MapHeadersFooters(section, sectPr);
                MapPageBorders(section, sectPr);

                // OOXML has no list element: a list is a run of paragraphs sharing a w:numId.
                // The grouping is reconstructed here rather than in MapParagraph, because it spans
                // paragraphs and a per-paragraph mapper cannot see where one ends.
                var lists = new DocxListGrouping(_wordDoc, _sr, _listCounters);

                foreach (var el in Rendering.MarkupWrappers.Blocks(content))
                {
                    switch (el)
                    {
                        case W.Paragraph p:
                            AddParagraph(section.Children, lists, p);
                            break;
                        case W.Table t:
                            lists.Flush(section.Children);
                            section.Children.Add(MapTable(t));
                            break;
                    }
                }

                // A section ending on a list item would otherwise drop the whole list.
                lists.Flush(section.Children);

                doc.Sections.Add(section);
            }

            if (doc.Sections.Count == 0)
            {
                var s = new M.Section();
                ApplyPageSetup(s, null, options);
                doc.Sections.Add(s);
            }
            return doc;
        }

        private static List<(List<OpenXmlElement> content, W.SectionProperties sectPr)> SplitSections(W.Body body)
        {
            var sections = new List<(List<OpenXmlElement>, W.SectionProperties)>();
            var current = new List<OpenXmlElement>();
            foreach (var el in body.ChildElements)
            {
                if (el is W.SectionProperties bodySect)
                {
                    sections.Add((current, bodySect));
                    current = new List<OpenXmlElement>();
                    continue;
                }
                current.Add(el);
                if (el is W.Paragraph p &&
                    p.ParagraphProperties?.GetFirstChild<W.SectionProperties>() is W.SectionProperties ps)
                {
                    sections.Add((current, ps));
                    current = new List<OpenXmlElement>();
                }
            }
            if (current.Count > 0 || sections.Count == 0)
                sections.Add((current, null));
            return sections;
        }

        // ------------------------------------------------------------------ page setup

        private void ApplyPageSetup(M.Section section, W.SectionProperties sectPr, DocxToPdfOptions options)
        {
            section.PageWidthPt = options.PageWidth;
            section.PageHeightPt = options.PageHeight;
            section.MarginTop = options.MarginTop;
            section.MarginBottom = options.MarginBottom;
            section.MarginLeft = options.MarginLeft;
            section.MarginRight = options.MarginRight;
            if (sectPr == null) return;

            double pageWidth = section.PageWidthPt, pageHeight = section.PageHeightPt;
            double top = section.MarginTop, bottom = section.MarginBottom, left = section.MarginLeft, right = section.MarginRight;
            Rendering.SectionPageSize.Resolve(sectPr.Elements<W.PageSize>().FirstOrDefault(),
                sectPr.Elements<W.PageMargin>().FirstOrDefault(), options.UseDocumentMargins,
                ref pageWidth, ref pageHeight, ref top, ref bottom, ref left, ref right);
            section.PageWidthPt = pageWidth;
            section.PageHeightPt = pageHeight;
            section.MarginTop = top; section.MarginBottom = bottom; section.MarginLeft = left; section.MarginRight = right;

            // A section's w:bidi is not mapped onto Section.Direction, which is the direction of the
            // section's CONTENT — its paragraphs', lists' and tables' default. ECMA-376-1 §17.6.1: the
            // property "only affects section-level properties, and does not affect the layout of
            // text within the contents of this section"; §17.4.1: a table without w:bidiVisual "shall
            // not be presented right to left". Mapped, every table of a Hebrew section was mirrored
            // and every paragraph without w:bidi read right to left, unlike the legacy renderer and
            // Word. A paragraph's own w:bidi still sets its direction (MapParagraph). It is kept as
            // the section's layout, so a DOCX export writes it back.
            var sectionBidi = sectPr.GetFirstChild<W.BiDi>();
            section.RightToLeftLayout = sectionBidi != null && (sectionBidi.Val == null || sectionBidi.Val.Value);

            // w:pgNumType: the PAGE field's format — Roman, letters, Hebrew — and the number the
            // section starts at. The legacy renderer read the format; this path read neither.
            var pageNumbers = sectPr.GetFirstChild<W.PageNumberType>();
            section.PageNumberFormat = Rendering.DocxRenderContext.MapPageNumberFormat(pageNumbers?.Format);
            if (pageNumbers?.Start?.Value is int start) section.PageNumberStart = start;
        }

        // ------------------------------------------------------------------ headers / footers

        private void MapHeadersFooters(M.Section section, W.SectionProperties sectPr)
        {
            if (sectPr == null) return;
            section.TitlePage = sectPr.GetFirstChild<W.TitlePage>() != null;
            section.EvenAndOddHeaders = _evenOdd;

            section.Header = MapHeader(_bands.Header(W.HeaderFooterValues.Default));
            section.Footer = MapFooter(_bands.Footer(W.HeaderFooterValues.Default));

            // An empty band rather than none where the file names no first or even band: the
            // exporter draws the default band on a page whose own band is null, and a titlePg cover
            // or an even page under evenAndOddHeaders shows none (HeaderFooterReferences).
            if (section.TitlePage)
            {
                section.FirstPageHeader = MapHeader(_bands.Header(W.HeaderFooterValues.First)) ?? new M.Header();
                section.FirstPageFooter = MapFooter(_bands.Footer(W.HeaderFooterValues.First)) ?? new M.Footer();
            }
            if (_evenOdd)
            {
                section.EvenPageHeader = MapHeader(_bands.Header(W.HeaderFooterValues.Even)) ?? new M.Header();
                section.EvenPageFooter = MapFooter(_bands.Footer(W.HeaderFooterValues.Even)) ?? new M.Footer();
            }
        }

        private M.Header MapHeader(OpenXmlElement part)
        {
            if (part == null) return null;
            var h = new M.Header();
            MapBand(part, h.Paragraphs, h.Blocks);
            return h.Content().Any() ? h : null;
        }

        private M.Footer MapFooter(OpenXmlElement part)
        {
            if (part == null) return null;
            var f = new M.Footer();
            MapBand(part, f.Paragraphs, f.Blocks);
            return f.Content().Any() ? f : null;
        }

        /// <summary>
        /// A header's or footer's paragraphs and tables, in order, through content controls and custom XML around
        /// them: paragraphs alone go to <paramref name="paragraphs"/>; a band with a table — a letterhead's usual
        /// layout — goes whole to <paramref name="blocks"/>, which the exporter draws as a cell's content.
        /// </summary>
        private void MapBand(OpenXmlElement part, List<M.Paragraph> paragraphs, List<M.IDocElement> blocks)
        {
            bool anyTable = false;
            foreach (var block in Rendering.MarkupWrappers.Blocks(part.ChildElements))
            {
                if (block is W.Paragraph p) blocks.Add(MapParagraph(p));
                else if (block is W.Table t) { blocks.Add(MapTable(t)); anyTable = true; }
            }
            if (anyTable) return;
            foreach (var block in blocks) paragraphs.Add((M.Paragraph)block);
            blocks.Clear();
        }

        // ------------------------------------------------------------------ page borders

        private static void MapPageBorders(M.Section section, W.SectionProperties sectPr)
        {
            var pb = sectPr?.GetFirstChild<W.PageBorders>();
            if (pb == null) return;
            bool fromPage = pb.OffsetFrom?.Value == W.PageBorderOffsetValues.Page;
            var mpb = new M.PageBorders { OffsetFromPage = fromPage };
            mpb.Top = MapBorder(pb.TopBorder);
            mpb.Bottom = MapBorder(pb.BottomBorder);
            mpb.Left = MapBorder(pb.LeftBorder);
            mpb.Right = MapBorder(pb.RightBorder);
            double DefSpace() => fromPage ? DocxLayoutMetrics.PageBorderPageOffsetPt : 0.0;
            mpb.SpaceTop = (double?)pb.TopBorder?.Space?.Value ?? DefSpace();
            mpb.SpaceBottom = (double?)pb.BottomBorder?.Space?.Value ?? DefSpace();
            mpb.SpaceLeft = (double?)pb.LeftBorder?.Space?.Value ?? DefSpace();
            mpb.SpaceRight = (double?)pb.RightBorder?.Space?.Value ?? DefSpace();
            section.PageBorders = mpb;
        }

        private static M.Border MapBorder(W.BorderType b)
        {
            if (b == null) return null;
            var val = b.Val?.Value;
            if (val == W.BorderValues.None || val == W.BorderValues.Nil) return new M.Border { None = true };
            var border = new M.Border { Width = (b.Size?.Value ?? 4) / PdfUnits.BorderEighthsPerPoint, None = false };
            string color = b.Color?.Value;
            if (!string.IsNullOrEmpty(color) && color != "auto") border.Color = "#" + color;
            return border;
        }

        // ------------------------------------------------------------------ paragraphs

        /// <summary>
        /// Adds a paragraph to the section, as a list item when it continues a numbered run.
        /// </summary>
        private void AddParagraph(
            List<M.IDocElement> sink, DocxListGrouping lists, W.Paragraph wp)
        {
            M.Paragraph mapped = MapParagraph(wp);
            if (lists.TryTake(wp, mapped, sink)) return;

            lists.Flush(sink);
            sink.Add(mapped);
        }

        private M.Paragraph MapParagraph(W.Paragraph wp)
        {
            var mp = new M.Paragraph { Alignment = MapAlign(_sr.ResolveDisplayAlignment(wp)), AnchorName = BookmarkOf(wp) };
            if (_sr.ResolveParagraphRtl(wp)) mp.Direction = M.TextDirection.RightToLeft;

            var (before, after) = _sr.ResolveParagraphSpacing(wp);
            mp.SpacingBefore = before;
            mp.SpacingAfter = after;

            var (left, right, firstLine) = _sr.ResolveIndentation(wp);
            mp.IndentLeft = left;
            mp.IndentRight = right;
            mp.FirstLineIndent = firstLine;

            double lh = _sr.ResolveLineHeight(wp, _defSize);
            if (lh > 0) mp.LineSpacingPt = lh;

            // Paragraph shading + borders (w:shd / w:pBdr).
            var pPr = wp.ParagraphProperties;
            string shd = pPr?.Shading?.Fill?.Value;
            if (!string.IsNullOrEmpty(shd) && shd != "auto") mp.Shading = "#" + shd;
            var pbd = pPr?.ParagraphBorders;
            if (pbd != null)
            {
                mp.BorderTop = MapBorder(pbd.TopBorder);
                mp.BorderBottom = MapBorder(pbd.BottomBorder);
                mp.BorderLeft = MapBorder(pbd.LeftBorder);
                mp.BorderRight = MapBorder(pbd.RightBorder);
            }

            // The marker is NOT written here. A numbered paragraph becomes a ListItem (see
            // DocxListGrouping) whose marker comes from numbering.xml through the same reader the
            // rendering path uses -- a hard-coded bullet run would turn an ordered list into a
            // bulleted one and lose both numbers of a multi-level "1.1" scheme.
            //
            // The indent stays: it applies whether or not the grouping takes the paragraph, and one
            // that falls through (a numbering id the definitions do not describe) is still drawn
            // indented by Word.
            if (_sr.ResolveNumbering(wp) is W.NumberingProperties numbering)
            {
                int ilvl = (int)(numbering.NumberingLevelReference?.Val?.Value ?? 0);
                mp.IndentLeft += (ilvl + 1) * ListLevelIndentPt;
            }

            // The fields resolved first (Rendering.FieldWalk, the legacy renderer's walk too): a PAGE or
            // NUMPAGES field, simple or complex, becomes its live placeholder in the cached result's formatting.
            foreach (var child in FieldWalk.ResolveFields(wp.ChildElements, FieldPlaceholder))
                MapInline(mp, child, wp, null, null);

            if (mp.Runs.Count == 0) mp.Runs.Add(new M.TextRun { Text = "", FontSize = _defSize });
            return mp;
        }

        /// <summary>
        /// The exporter's placeholder for a field this path evaluates — PAGE and NUMPAGES — or null for a
        /// field it does not, whose cached result then stands. The field's name, through the one parser
        /// the legacy renderer uses, not a substring of the instruction: <c>PAGEREF</c> is not <c>PAGE</c>.
        /// </summary>
        private static string FieldPlaceholder(string instruction)
        {
            switch (Rendering.ParagraphRenderer.FieldName(instruction))
            {
                case "NUMPAGES": return Tetitulz.Pdf.Builder.Export.PdfExporter.NumPagesPlaceholder;
                case "PAGE": return Tetitulz.Pdf.Builder.Export.PdfExporter.PagePlaceholder;
                default: return null;
            }
        }

        private void MapInline(M.Paragraph mp, OpenXmlElement child, W.Paragraph wp, string linkUri, string linkAnchor)
        {
            switch (child)
            {
                case W.Run run:
                    MapRun(mp, run, wp, linkUri, linkAnchor);
                    break;
                case W.Hyperlink hl:
                    // Its relationship's target, or its bookmark (Rendering.HyperlinkTargets); the exporter decides
                    // whether a target may be a link.
                    string uri = Rendering.HyperlinkTargets.Of(hl, _wordDoc.MainDocumentPart);
                    string anchor = uri != null && uri.StartsWith("#") ? hl.Anchor?.Value : null;
                    foreach (var c in hl.ChildElements) MapInline(mp, c, wp, uri, anchor);
                    break;
                case W.SimpleField sf:
                    // A simple field the walk did not compute (one inside a hyperlink too): its cached result.
                    string placeholder = FieldPlaceholder(sf.Instruction?.Value);
                    if (placeholder != null) mp.Runs.Add(new M.TextRun { Text = placeholder, FontSize = _defSize });
                    else foreach (var c in sf.ChildElements) MapInline(mp, c, wp, linkUri, linkAnchor);
                    break;
                default:
                    // Content controls, insertions, smart tags, custom XML, moves, bidi wrappers: their runs;
                    // a deletion or a move's source: nothing (Rendering.MarkupWrappers).
                    var content = Rendering.MarkupWrappers.InlineContent(child);
                    if (content == null) { MapUnhandledInline(mp, child); break; }
                    int first = mp.Runs.Count;
                    foreach (var c in content) MapInline(mp, c, wp, linkUri, linkAnchor);
                    var (open, close) = Rendering.MarkupWrappers.DirectionControls(child);
                    if (open != '\0') WrapInControls(mp.Runs, first, open, close);
                    break;
            }
        }

        /// <summary>
        /// A <c>w:dir</c> or <c>w:bdo</c>'s runs, <c>mp.Runs[first..]</c>, between the controls it stands for, which the
        /// exporter's bidi resolves across the paragraph as it does HTML's <c>&lt;bdo&gt;</c>. Each control is a run of its
        /// own in the face and size of the text it bounds, so it adds nothing to the line's height; a wrapper with no
        /// text is left alone.
        /// </summary>
        private static void WrapInControls(List<M.IInlineElement> runs, int first, char open, char close)
        {
            M.TextRun start = null, end = null;
            for (int i = first; i < runs.Count; i++)
                if (runs[i] is M.TextRun tr && tr.Box == null && tr.FormField == null)
                {
                    start ??= tr;
                    end = tr;
                }
            if (start == null) return;
            runs.Insert(first, Control(open, start));
            runs.Add(Control(close, end));
        }

        private static M.TextRun Control(char control, M.TextRun like) => new M.TextRun
        {
            Text = control.ToString(), FontName = like.FontName, FontSize = like.FontSize,
            IsBold = like.IsBold, IsItalic = like.IsItalic, VerticalAlign = like.VerticalAlign,
        };

        /// <summary>
        /// Office Math, and anything else this mapper has no case for.
        /// </summary>
        /// <remarks>
        /// <para><b>The switch above had no <c>default</c>, so anything it did not name vanished
        /// without a word.</b> Measured on a DOCX carrying one <c>m:oMath</c>: the legacy renderer
        /// produced the equation, this path produced nothing at all, and
        /// <c>DocxToPdfConverter.LastDiagnostic</c> stayed <c>null</c> — so an equation the source
        /// contained and a source that never had one are the same output. That is the failure
        /// <see cref="Rendering.DocxDiagnostics"/> exists to make impossible.</para>
        /// <para><b>Math is linearised rather than merely reported</b>, through the
        /// <see cref="Rendering.MathLinearizer"/> the legacy path already owns — whose own summary
        /// says it exists "so equations are preserved as text instead of being dropped". Fractions
        /// become <c>(a)/(b)</c>, radicals <c>√(x)</c>, and the symbols come straight from the
        /// <c>m:t</c> runs. It is an approximation of the legacy path's 2-D typesetting, so it is
        /// reported as one.</para>
        /// <para><b>Everything else is reported only when it would have contributed text.</b> A
        /// paragraph carries plenty of inert markup — <c>w:bookmarkStart</c>, <c>w:proofErr</c>,
        /// <c>w:commentRangeEnd</c>, <c>w:lastRenderedPageBreak</c> — and none of it loses anything
        /// visible. Reporting those would fire on every ordinary document, which is how a
        /// diagnostic channel trains its reader to ignore it.</para>
        /// </remarks>
        private void MapUnhandledInline(M.Paragraph mp, OpenXmlElement child)
        {
            // Matched by local name, not by SDK type: which wrapper class models m:oMath differs
            // between SDK versions, and MathLinearizer walks by name for the same reason.
            if (child.LocalName == "oMath" || child.LocalName == "oMathPara")
            {
                string linear = Rendering.MathLinearizer.Linearize(child);
                if (!string.IsNullOrWhiteSpace(linear))
                {
                    mp.Runs.Add(new M.TextRun { Text = linear, FontSize = _defSize });
                    Rendering.DocxDiagnostics.Report("DocxToBuilderMapper.Math",
                        "an Office Math equation was linearised to text (" + Excerpt(linear) +
                        ") because the Builder path has no 2-D math typesetter; " +
                        "DocxToPdfOptions.UseLegacyRenderer = true typesets it.");
                    return;
                }
            }

            string text = child.InnerText;
            if (string.IsNullOrWhiteSpace(text)) return;   // inert markup: nothing was lost

            Rendering.DocxDiagnostics.Report("DocxToBuilderMapper.UnmappedInline",
                "<" + child.LocalName + "> is not mapped by the Builder path and its text (" +
                Excerpt(text) + ") is absent from the output; " +
                "DocxToPdfOptions.UseLegacyRenderer = true handles it.");
        }

        /// <summary>A short, single-line excerpt for a diagnostic message.</summary>
        private static string Excerpt(string value)
        {
            string s = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= 40 ? "\"" + s + "\"" : "\"" + s.Substring(0, 40) + "…\"";
        }

        private void MapRun(M.Paragraph mp, W.Run run, W.Paragraph wp, string linkUri, string linkAnchor)
        {
            // Hidden text (w:vanish) is left out of print and PDF, its drawing with it.
            if (_sr.ResolveVanish(run, wp)) return;

            // A run may carry a drawing (inline/anchored image). This path maps pictures alone: a shape, a
            // text box, a chart or a diagram is not drawn, and that is said once per conversion.
            var drawing = run.GetFirstChild<W.Drawing>();
            if (drawing != null)
            {
                var img = ExtractImage(drawing);
                if (img != null) mp.Runs.Add(img);
                else if (!_unmappedDrawingReported)
                {
                    _unmappedDrawingReported = true;
                    Rendering.DocxDiagnostics.Report("DocxToBuilderMapper.Drawing",
                        "a drawing that is not a picture (a shape, a text box, a chart or a diagram) is not drawn by the " +
                        "Builder path; DocxToPdfOptions.UseLegacyRenderer = true draws it. Reported once per conversion.");
                }
            }

            var tpl = new M.TextRun
            {
                FontSize = _sr.ResolveFontSizeInPoints(run, wp, _defSize),
                IsBold = _sr.ResolveBoldNullable(run, wp) ?? (_cellFormat.Bold ?? false),
                IsItalic = _sr.ResolveIsItalic(run, wp),
                IsUnderline = _sr.ResolveUnderline(run, wp),
                IsStrikethrough = _sr.ResolveStrike(run, wp),
                IsAllCaps = _sr.ResolveCaps(run, wp),
                IsSmallCaps = _sr.ResolveSmallCaps(run, wp),
                // The Builder's CharSpacing is PDF's Tc, scaled by its HorizontalScale; Word's pitch is not.
                CharSpacing = RunRenderer.EmittedCharSpacing(_sr.ResolveCharacterSpacing(run, wp), _sr.ResolveTextScale(run, wp)),
                HorizontalScale = _sr.ResolveTextScale(run, wp),
                BaselineShift = _sr.ResolveTextPosition(run, wp),
                LinkUri = linkUri,
                LinkAnchor = linkAnchor,
            };
            tpl.Kerning = _sr.ResolveKerning(run, wp, tpl.FontSize);
            tpl.ContextualAlternates = _sr.ResolveContextualAlternates(run, wp);
            tpl.Ligatures = _sr.ResolveLigatures(run, wp);
            tpl.DiscretionaryLigatures = _sr.ResolveDiscretionaryLigatures(run, wp);
            tpl.HistoricalLigatures = _sr.ResolveHistoricalLigatures(run, wp);
            string col = _sr.ResolveColor(run, wp) ?? _cellFormat.ColorHex;
            if (col != null) tpl.Color = "#" + col;
            string hl = _sr.ResolveHighlight(run, wp);
            if (hl != null) tpl.Highlight = "#" + hl;
            int va = _sr.ResolveVertAlign(run, wp);
            tpl.VerticalAlign = va == 1 ? M.VerticalAlign.Superscript
                : va == -1 ? M.VerticalAlign.Subscript : M.VerticalAlign.Baseline;
            string font = _sr.ResolveComplexScriptFontName(run, wp) ?? _sr.ResolveFontName(run, wp);
            if (!string.IsNullOrEmpty(font)) tpl.FontName = font;

            foreach (var c in run.ChildElements)
            {
                switch (c)
                {
                    case W.Text t:
                        var r = tpl.Clone();
                        r.Text = t.Text ?? "";
                        mp.Runs.Add(r);
                        break;
                    case W.TabChar:
                        mp.Runs.Add(new M.TabRun());
                        break;
                    case W.Break br:
                        if (br.Type?.Value == W.BreakValues.Page) mp.Runs.Add(new M.PageBreak());
                        else mp.Runs.Add(new M.LineBreak());
                        break;
                    case W.FootnoteReference footnote:
                        mp.Runs.Add(MapNote(footnote.Id?.Value ?? -1, isEndnote: false, wp));
                        break;
                    case W.EndnoteReference endnote:
                        mp.Runs.Add(MapNote(endnote.Id?.Value ?? -1, isEndnote: true, wp));
                        break;
                    default:
                        // The one reading of a run's other content (Rendering.RunContent), the legacy renderer's too.
                        switch (RunContent.Classify(c, out string stands))
                        {
                            case RunContent.Kind.Text:
                                var h = tpl.Clone(); h.Text = stands; mp.Runs.Add(h);
                                break;
                            case RunContent.Kind.LineBreak:
                                mp.Runs.Add(new M.LineBreak());
                                break;
                            case RunContent.Kind.Tab:
                                mp.Runs.Add(new M.TabRun());
                                break;
                        }
                        break;
                }
            }
        }

        private bool _unmappedDrawingReported;

        /// <summary>
        /// A footnote or endnote reference as the model's <see cref="M.Footnote"/>: the exporter numbers
        /// its mark and draws its content at the page's foot or among the endnotes. The content is the
        /// note's paragraphs, mapped as body runs, a line break between them; the mark inside the note
        /// (<c>w:footnoteRef</c>) is no content and is passed over. A reference to no note is empty.
        /// </summary>
        private M.Footnote MapNote(long id, bool isEndnote, W.Paragraph wp)
        {
            var note = new M.Footnote { Placement = isEndnote ? M.NotePlacement.DocumentEnd : M.NotePlacement.PageFoot };
            OpenXmlElement source = isEndnote
                ? _main?.EndnotesPart?.Endnotes?.Elements<W.Endnote>().FirstOrDefault(e => e.Id?.Value == id)
                : _main?.FootnotesPart?.Footnotes?.Elements<W.Footnote>().FirstOrDefault(f => f.Id?.Value == id);
            if (source == null) return note;

            bool first = true;
            foreach (var block in Rendering.MarkupWrappers.Blocks(source.ChildElements))
            {
                if (block is not W.Paragraph paragraph) continue;
                if (!first) note.Runs.Add(new M.LineBreak());
                first = false;
                var mapped = new M.Paragraph();
                foreach (var child in FieldWalk.ResolveFields(paragraph.ChildElements, FieldPlaceholder))
                    MapInline(mapped, child, paragraph, null, null);
                note.Runs.AddRange(mapped.Runs);
            }
            return note;
        }

        // Fallback size, in points, for a drawing whose wp:extent is missing or unreadable.
        private const double DefaultImageSizePt = 96;

        private M.ImageRun ExtractImage(W.Drawing drawing)
        {
            var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
            string embed = blip?.Embed?.Value;
            if (embed == null || _main == null) return null;

            // The relationship belongs to the part the drawing lives in — the main document for a
            // body picture, but the header, footer, footnote or text-box part for one there. Ids
            // are numbered PER PART, so resolving against MainDocumentPart alone had all three of
            // the failure modes OpenXmlPartLookup was written to close, live on this path only:
            // GetPartById THREW for an id the main document does not own (and this call sits
            // outside the try below, so a valid document became a DocxToPdfException), the id
            // named a part of the wrong type and the picture vanished, or it named a DIFFERENT
            // valid image and that one was painted. The legacy ImageRenderer resolved it correctly
            // the whole time, so the two converters disagreed about the same file.
            if (OpenXmlPartLookup.Resolve(drawing, embed, _main) is not ImagePart ip) return null;

            // Read once a part: every drawing of it shares the bytes, which the exporter then decodes once.
            if (!_imageBytes.TryGetValue(ip, out byte[] bytes))
                _imageBytes[ip] = bytes = ReadImagePart(ip);
            if (bytes == null) return null;

            var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
            double wPt = extent?.Cx != null ? PdfUnits.EmuToPoints(extent.Cx.Value) : DefaultImageSizePt;
            double hPt = extent?.Cy != null ? PdfUnits.EmuToPoints(extent.Cy.Value) : DefaultImageSizePt;
            return new M.ImageRun { Data = bytes, WidthPt = wPt, HeightPt = hPt };
        }

        private readonly Dictionary<ImagePart, byte[]> _imageBytes = new Dictionary<ImagePart, byte[]>();

        /// <summary>An image part's bytes, or null when it is empty, unreadable or past the decompression cap.</summary>
        private static byte[] ReadImagePart(ImagePart ip)
        {
            byte[] bytes;
            try
            {
                // A DOCX is a ZIP; an image part can be a small deflated blob that expands to many
                // GB (zip bomb). Cap the decompressed read at 256 MB (mirror the §69 legacy
                // ImageRenderer cap, which this parallel Builder-path reader never received).
                long MaxImageBytes = PdfLimits.MaxDocxPartBytes;
                using var s = ip.GetStream();
                using var ms = new MemoryStream();
                byte[] cbuf = new byte[81920];
                long total = 0;
                int rn;
                while ((rn = s.Read(cbuf, 0, cbuf.Length)) > 0)
                {
                    total += rn;
                    if (total > MaxImageBytes) return null;
                    ms.Write(cbuf, 0, rn);
                }
                bytes = ms.ToArray();
            }
            catch { return null; }
            return bytes.Length == 0 ? null : bytes;
        }

        // ------------------------------------------------------------------ tables

        // Tables can nest (a w:tbl inside a w:tc). Deeply-nested input would recurse
        // MapTable→MapCell→MapTable to a StackOverflow during model construction; cap the depth
        // at the shared limit the legacy renderer's TableRenderer also uses. Beyond the cap the
        // inner table is dropped — acceptable for pathological input.
        private static int MaxTableNesting => PdfLimits.MaxTableNestingDepth;

        private M.Table MapTable(W.Table wt, int depth = 0)
        {
            var mt = new M.Table();
            if (depth > MaxTableNesting) return mt;
            var tblPr = wt.GetFirstChild<W.TableProperties>();
            if (Rendering.TableRenderer.MirrorsColumns(tblPr)) mt.RtlColumns = true;

            var top = tblPr?.TableBorders?.TopBorder;
            if (top?.Val?.Value == W.BorderValues.None || top?.Val?.Value == W.BorderValues.Nil)
                mt.BorderWidth = 0;

            // w:jc: where a table narrower than its band sits in it (§17.4.29).
            switch (tblPr?.TableJustification?.Val?.InnerText)
            {
                case "center": mt.Alignment = M.TextAlignment.Center; break;
                case "right": mt.Alignment = M.TextAlignment.Right; break;
                case "end": mt.Alignment = M.TextAlignment.End; break;
            }

            // The table style's conditional formatting (w:tblStylePr: header row, banding, first column, corners)
            // for each cell by its place in the grid, as the legacy renderer resolves it — this path applied none
            // of it, so a styled table lost its header shading and bold.
            var rows = Rendering.MarkupWrappers.Rows(wt).ToList();
            int colCount = wt.GetFirstChild<W.TableGrid>()?.Elements<W.GridColumn>().Count() ?? 0;
            if (colCount == 0)
                foreach (var wr in rows)
                    colCount = System.Math.Max(colCount, Rendering.MarkupWrappers.Cells(wr).Sum(SpanOf));

            for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
            {
                var wr = rows[rowIdx];
                var mr = new M.TableRow();
                var trPr = wr.GetFirstChild<W.TableRowProperties>();
                if (trPr?.GetFirstChild<W.TableHeader>() != null) mr.IsHeader = true;

                // w:gridBefore (§17.4.14): the row's cells start that many grid columns in, the columns
                // before them empty, so a vertical merge pairs with the cell above at the same column.
                int gridBefore = System.Math.Min(System.Math.Max(0, trPr?.GetFirstChild<W.GridBefore>()?.Val?.Value ?? 0), colCount);
                for (int i = 0; i < gridBefore; i++)
                {
                    var empty = new M.TableCell();
                    empty.Blocks.Add(new M.Paragraph());
                    mr.Cells.Add(empty);
                }

                int colIdx = gridBefore;
                foreach (var wc in Rendering.MarkupWrappers.Cells(wr))
                {
                    int span = SpanOf(wc);
                    var styleFmt = _sr.ResolveTableStyleFormat(wt, rowIdx, rows.Count, colIdx, span, colCount);
                    var mc = MapCell(wc, depth, styleFmt);
                    // Each side from the cell's tcBorders, the table's tblBorders, the style's and its conditional
                    // ones, by the cell's place — the legacy renderer's resolution. This path read none of them, so
                    // every table drew the full default grid whatever its borders said.
                    var borders = _sr.ResolveCellBorders(wc, wt, rowIdx, rows.Count, colIdx, span, colCount);
                    // Resolved for the cell's logical place; a mirrored table draws it at the other end.
                    if (mt.RtlColumns) (borders.Left, borders.Right) = (borders.Right, borders.Left);
                    if (borders.AnyDefined)
                    {
                        mc.BorderTop = ToModelBorder(borders.Top);
                        mc.BorderBottom = ToModelBorder(borders.Bottom);
                        mc.BorderLeft = ToModelBorder(borders.Left);
                        mc.BorderRight = ToModelBorder(borders.Right);
                    }
                    mr.Cells.Add(mc);
                    colIdx += span;
                }
                mt.Rows.Add(mr);
            }
            if (mt.Rows.Any(r => r.IsHeader)) mt.RepeatHeaderRow = true;
            return mt;
        }

        /// <summary>
        /// A resolved cell side as the Builder draws it: a visible side as its width and colour, any other — none,
        /// nil, no width, or no source naming it while another side is named — as no border, as the legacy
        /// renderer leaves it undrawn.
        /// </summary>
        private static M.Border ToModelBorder(Rendering.StyleResolver.BorderSide side)
        {
            if (!side.Visible || !(side.WidthPts > 0)) return new M.Border { None = true };
            var border = new M.Border { Width = side.WidthPts, None = false };
            if (!string.IsNullOrEmpty(side.HexColor) && side.HexColor != "auto") border.Color = "#" + side.HexColor;
            return border;
        }

        private static int SpanOf(W.TableCell wc) => System.Math.Max(1, wc.TableCellProperties?.GridSpan?.Val?.Value ?? 1);

        /// <summary>The conditional formatting of the cell whose content is being mapped; default outside a cell.</summary>
        private Rendering.StyleResolver.TableStyleCellFormat _cellFormat;

        private M.TableCell MapCell(W.TableCell wc, int depth, Rendering.StyleResolver.TableStyleCellFormat styleFmt)
        {
            var mc = new M.TableCell();
            var tcPr = wc.TableCellProperties;

            if (tcPr?.GridSpan?.Val != null) mc.GridSpan = tcPr.GridSpan.Val.Value;

            var vm = tcPr?.VerticalMerge;
            if (vm != null)
                mc.VMerge = vm.Val?.Value == W.MergedCellValues.Restart
                    ? M.VerticalMerge.Restart : M.VerticalMerge.Continue;

            // The cell's own shading wins over its table style's, as on the legacy path.
            var shadeFill = tcPr?.Shading?.Fill?.Value;
            if (!string.IsNullOrEmpty(shadeFill) && shadeFill != "auto")
                mc.BackgroundColor = "#" + shadeFill;
            else if (styleFmt.ShadingHex != null)
                mc.BackgroundColor = "#" + styleFmt.ShadingHex;

            var va = tcPr?.TableCellVerticalAlignment?.Val?.Value;
            mc.VAlign = va == W.TableVerticalAlignmentValues.Center ? M.CellVerticalAlign.Center
                : va == W.TableVerticalAlignmentValues.Bottom ? M.CellVerticalAlign.Bottom
                : M.CellVerticalAlign.Top;

            // The cell's runs take the style's conditional bold and colour where they set none themselves; a nested
            // table's cells set their own.
            var outer = _cellFormat;
            _cellFormat = styleFmt;
            try
            {
                // A cell's numbered paragraphs are a list as the body's are, counted with the body's.
                var lists = new DocxListGrouping(_wordDoc, _sr, _listCounters);
                foreach (var c in Rendering.MarkupWrappers.Blocks(wc.ChildElements))
                {
                    if (c is W.Paragraph p) AddParagraph(mc.Blocks, lists, p);
                    else if (c is W.Table t) { lists.Flush(mc.Blocks); mc.Blocks.Add(MapTable(t, depth + 1)); }
                }
                lists.Flush(mc.Blocks);
            }
            finally { _cellFormat = outer; }
            if (mc.Blocks.Count == 0) mc.Blocks.Add(new M.Paragraph());
            return mc;
        }

        // ------------------------------------------------------------------ helpers

        private static M.TextAlignment MapAlign(W.JustificationValues j)
        {
            if (j == W.JustificationValues.Center) return M.TextAlignment.Center;
            if (j == W.JustificationValues.Right) return M.TextAlignment.Right;
            if (j == W.JustificationValues.Both) return M.TextAlignment.Justify;
            return M.TextAlignment.Left;
        }
    }
}
