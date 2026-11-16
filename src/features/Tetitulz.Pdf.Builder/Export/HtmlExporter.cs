// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>Exports a Document to semantic HTML5 with inline CSS.</summary>
    public sealed class HtmlExporter : IDocumentExporter
    {
        public void Export(Document document, Stream output)
        {
            // Cleared per export, on every path — see DocxExporter.Export. This exporter reports
            // nothing today, which is precisely why the clear matters: without it a caller reading
            // BuilderDiagnostics.LastError after an HTML export would see the previous export's
            // failure and conclude this one had it too.
            BuilderDiagnostics.Clear();
            ModelMeasurements.Validate(document);

            var sb = new StringBuilder();
            // The PDF rule for /Lang, verbatim: write the document's language when it declared one
            // and NOTHING when it did not. A hardcoded lang="en" made a document that declared
            // Hebrew export as English — which is worse than declaring nothing, because a screen
            // reader then pronounces it with English phonemes while a conformance check reports a
            // clean language declaration.
            sb.Append("<!DOCTYPE html>\n<html");
            if (!string.IsNullOrEmpty(document.Language))
                sb.AppendFormat(" lang=\"{0}\"", Enc(document.Language));
            sb.Append(">\n<head>\n<meta charset=\"utf-8\">\n");
            sb.AppendFormat("<title>{0}</title>\n", Enc(document.Title));
            sb.Append("<style>\n");
            sb.Append("body { font-family: 'Segoe UI', Helvetica, Arial, sans-serif; max-width: 800px; margin: 40px auto; line-height: 1.6; color: #333; }\n");
            sb.Append("table { border-collapse: collapse; width: 100%; margin: 12px 0; }\n");
            sb.Append("th, td { border: 1px solid #ccc; padding: 8px; text-align: left; }\n");
            sb.Append("th { background: #f5f5f5; font-weight: bold; }\n");
            sb.Append(".page-break { page-break-after: always; }\n");
            sb.Append("header, footer { color: #888; font-size: 0.85em; border-bottom: 1px solid #eee; padding: 8px 0; margin-bottom: 16px; }\n");
            sb.Append("footer { border-bottom: none; border-top: 1px solid #eee; margin-top: 16px; padding-top: 8px; }\n");
            sb.Append("</style>\n</head>\n<body>\n");

            // The same numbering the PDF export uses, so one document exported both ways numbers
            // its notes identically.
            _footnotes = FootnoteNumbering.For(document);
            _footnoteOrder.Clear();
            _endnoteOrder.Clear();
            _noteIds.Clear();

            foreach (var section in document.Sections)
            {
                bool sectionRtl = ModelRules.IsRightToLeft(section);
                if (section.Header != null)
                {
                    sb.Append("<header>");
                    RenderBlocks(sb, section.Header.Content(), section, sectionRtl, document);
                    sb.Append("</header>\n");
                }

                RenderBlocks(sb, ModelRules.InFlow(section.Children), section, sectionRtl, document);

                if (section.Footer != null)
                {
                    sb.Append("<footer>");
                    RenderBlocks(sb, section.Footer.Content(), section, sectionRtl, document);
                    sb.Append("</footer>\n");
                }
            }

            AppendFootnoteSection(sb);
            sb.Append("</body>\n</html>");
            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>A run of blocks — the section's flow, or a header's or footer's content.</summary>
        private void RenderBlocks(StringBuilder sb, IEnumerable<IDocElement> blocks, Section section, bool sectionRtl, Document document)
        {
            foreach (var child in blocks)
            {
                if (child is Paragraph para) RenderParagraph(sb, para, section);
                else if (child is Table table) RenderTable(sb, table, sectionRtl, 0);
                else if (child is ListItem li) RenderListItem(sb, li, sectionRtl);
                else if (child is ListBlock lb) RenderList(sb, lb, sectionRtl, 0);
                else if (child is TocBlock toc) RenderToc(sb, toc, document);
            }
        }

        private void RenderParagraph(StringBuilder sb, Paragraph para, Section section)
            => RenderParagraphCore(sb, para, ModelRules.Declared(para, section),
                                   ModelRules.IsRightToLeft(para, section));

        /// <summary>
        /// Writes a paragraph as a <c>&lt;p&gt;</c> with its direction and alignment, given the
        /// direction already resolved. The section path and the table-cell path both reach it: a
        /// cell has no <see cref="Section"/>, and emitting its paragraphs through <c>RenderInline</c>
        /// instead — the original cell loop — ran them together with no <c>&lt;p&gt;</c>, no
        /// separation and no dir or alignment, so two paragraphs in a cell became one run of text
        /// aligned the default way.
        /// </summary>
        private void RenderParagraphCore(StringBuilder sb, Paragraph para, TextDirection declared, bool rtl)
        {
            // HTML is the one format that can express "detect it from the content" verbatim; an
            // `== RightToLeft` test would make TextDirection.Auto mean left-to-right here while the
            // PDF resolves it per UAX #9.
            string dir = declared switch
            {
                TextDirection.RightToLeft => " dir=\"rtl\"",
                TextDirection.Auto => " dir=\"auto\"",
                _ => "",
            };
            // Left, Start and End all collapsed to no text-align at all, which a browser renders
            // as `start` — so End was always wrong and an explicit physical Left was lost inside
            // dir="rtl", the exact distinction Start was added to preserve. All four values below
            // are valid CSS.
            string align = ModelRules.ResolvePhysicalAlignment(para.Alignment, rtl) switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "right",
                TextAlignment.Justify => "justify",
                TextAlignment.Left => "left",
                _ => "",
            };
            string css = !string.IsNullOrEmpty(align) ? $"text-align:{align};" : "";
            css += BackgroundImageCss(para.BackgroundImage);
            css += OpacityCss(para.Opacity);
            css += FloatCss(para);
            if (para.Hyphenate) css += "hyphens:auto;";
            string style = css.Length > 0 ? $" style=\"{css}\"" : "";
            // AnchorName is HTML's `id`, which is what an internal link's fragment resolves
            // against. Encoded rather than emitted raw: the name comes from a document, and an
            // unescaped quote in it would close the attribute and let the rest be read as markup.
            string id = !string.IsNullOrEmpty(para.AnchorName)
                ? $" id=\"{Enc(para.AnchorName)}\"" : "";
            sb.AppendFormat("<p{0}{1}{2}>", dir, style, id);
            RenderInline(sb, para.Runs);
            sb.Append("</p>\n");
        }

        /// <summary>
        /// Re-emits an opacity as the CSS property it came from — the round trip, and the one
        /// format of the four that expresses it exactly.
        ///
        /// <para>Emits nothing at full opacity, so no existing document's markup changes. The
        /// value is clamped by <see cref="PdfExporter.Alpha01"/> so a caller's out-of-range or
        /// <c>NaN</c> value cannot produce invalid CSS that a browser silently discards.</para>
        /// </summary>
        private static string OpacityCss(double opacity)
        {
            double a = PdfExporter.Alpha01(opacity);
            return a >= 1 ? ""
                : "opacity:" + a.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ";";
        }

        /// <summary>
        /// <c>float</c> and <c>clear</c> for a floated paragraph, which HTML expresses natively.
        /// </summary>
        /// <remarks>
        /// <para><b>The one export where this needs no approximation.</b> The PDF exporter has to
        /// compute per-line exclusion rectangles to get text to flow round a float; here the format
        /// already has the concept, so the model's intent is written down and the reader does the
        /// layout. A width is emitted alongside because <c>Paragraph.FloatWidth</c> is the measure
        /// the model reserved, and a float without one shrink-to-fits to its content — a different
        /// layout from the one every other exporter produced.</para>
        ///
        /// <para><b>Emits nothing for an unfloated paragraph</b>, which is all of them in every
        /// document written before floats existed, so no existing markup changes.</para>
        /// </remarks>
        private static string FloatCss(Paragraph para)
        {
            var css = new StringBuilder();

            if (para.Float != FloatSide.None && para.FloatWidth > 0)
            {
                css.Append("float:")
                   .Append(para.Float == FloatSide.Left ? "left" : "right")
                   .Append(";width:")
                   .Append(para.FloatWidth.ToString("0.##", CultureInfo.InvariantCulture))
                   .Append("pt;");
            }

            // Independent of Float: a paragraph may clear without floating, which is the ordinary
            // way an author closes a floated section off.
            if (para.ClearFloats) css.Append("clear:both;");

            return css.ToString();
        }

        /// <summary>
        /// Re-emits a paragraph background image as the CSS that produced it, with the bytes
        /// inlined as a <c>data:</c> URI.
        ///
        /// <para>The same base64 + <see cref="DetectImageMime"/> pair an <c>ImageRun</c> uses,
        /// deliberately: an exporter that sniffed the media type differently for backgrounds than
        /// for images would eventually disagree with itself about the same bytes.</para>
        /// </summary>
        private static string BackgroundImageCss(BackgroundImage? bg)
        {
            if (bg?.Data == null || bg.Data.Length == 0) return "";

            string repeat = bg.Repeat switch
            {
                BackgroundRepeat.NoRepeat => "no-repeat",
                BackgroundRepeat.RepeatX => "repeat-x",
                BackgroundRepeat.RepeatY => "repeat-y",
                _ => "repeat",
            };
            string size = bg.Size switch
            {
                BackgroundSize.Cover => "background-size:cover;",
                BackgroundSize.Contain => "background-size:contain;",
                _ => "",
            };
            return $"background-image:url(data:{DetectImageMime(bg.Data)};base64,{Convert.ToBase64String(bg.Data)});" +
                   $"background-repeat:{repeat};" + size;
        }

        private void RenderInline(StringBuilder sb, IEnumerable<IInlineElement> runs)
        {
            foreach (var run in runs)
            {
                if (run is TextRun tr)
                {
                    string css = "";
                    // InvariantCulture: a comma-decimal locale would emit "12,5pt"
                    // (invalid CSS, silently dropped by browsers).
                    if (tr.FontSize != DocDefaults.FontSizePt)
                        css += "font-size:" + tr.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "pt;";
                    if (tr.IsInvisible) css += "color:transparent;";
                    else if (tr.Color != DocDefaults.TextColorHex)
                    {
                        string? safe = SafeColor(tr.Color);
                        if (safe != null) css += $"color:{safe};";
                    }
                    if (tr.IsBold) css += "font-weight:bold;";
                    if (tr.IsItalic) css += "font-style:italic;";
                    var deco = "";
                    if (tr.IsUnderline) deco += "underline ";
                    if (tr.IsStrikethrough || tr.IsDoubleStrike) deco += "line-through ";
                    if (deco.Length > 0) css += "text-decoration:" + deco.Trim() + ";";
                    string? safeHighlight = SafeColor(tr.Highlight);
                    if (safeHighlight != null) css += $"background-color:{safeHighlight};";
                    if (tr.IsAllCaps || tr.IsSmallCaps) css += "text-transform:uppercase;";
                    css += OpacityCss(tr.Opacity);

                    string inner = Enc(tr.Text);
                    if (tr.VerticalAlign == VerticalAlign.Superscript) inner = "<sup>" + inner + "</sup>";
                    else if (tr.VerticalAlign == VerticalAlign.Subscript) inner = "<sub>" + inner + "</sub>";
                    if (!string.IsNullOrEmpty(css)) inner = $"<span style=\"{css}\">{inner}</span>";
                    // LinkAnchor is the internal half, and it reaches HTML as the fragment it
                    // already is. Handling only LinkUri here was the same omission the PDF
                    // exporter had: a converted DOCX's cross-references arrived as plain text in
                    // every output format, so the field looked unused rather than unimplemented.
                    string? href = !string.IsNullOrEmpty(tr.LinkUri) ? tr.LinkUri
                                 : !string.IsNullOrEmpty(tr.LinkAnchor) ? "#" + tr.LinkAnchor
                                 : null;
                    // A rejected target drops the anchor and leaves the text (LinkTargets), as the PDF path does.
            if (LinkTargets.IsSafe(href)) inner = $"<a href=\"{Enc(href)}\">{inner}</a>";
                    sb.Append(inner);
                }
                else if (run is ImageRun { Drawing: { } drawing } vector) AppendVector(sb, vector, drawing);
                else if (run is ImageRun { Data.Length: 0 } blank)
                {
                    // A blank box (ImageRun.Box with no pixels): the room it takes, nothing more — an
                    // empty data: URI would be a broken-image icon.
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "<span style=\"display:inline-block;width:{0}pt;height:{1}pt\"></span>",
                        SaturatingCast.ToInt(blank.WidthPt), SaturatingCast.ToInt(blank.HeightPt));
                }
                else if (run is ImageRun img)
                {
                    string b64 = Convert.ToBase64String(img.Data);
                    sb.AppendFormat("<img src=\"data:{0};base64,{1}\" width=\"{2}\" height=\"{3}\" />",
                        DetectImageMime(img.Data), b64, SaturatingCast.ToInt(img.WidthPt), SaturatingCast.ToInt(img.HeightPt));
                }
                else if (run is Footnote note) AppendFootnoteMark(sb, note);
                else if (run is LineBreak) sb.Append("<br/>");
                else if (run is TabRun) sb.Append("&emsp;");
                else if (run is PageBreak) sb.Append("</p>\n<div class=\"page-break\"></div>\n<p>");
            }
        }


        /// <summary>
        /// Writes an <see cref="ImageRun.Drawing"/> as an inline <c>&lt;svg&gt;</c> of its own size: one
        /// <c>&lt;path&gt;</c>, whose fill closes an open path for itself as the PDF's does. Decorative,
        /// it is hidden from assistive technology; otherwise it carries the image's alternative text.
        /// </summary>
        private static void AppendVector(StringBuilder sb, ImageRun img, VectorDrawing drawing)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.###", ci);
            var d = new StringBuilder();
            foreach (PathCommand c in drawing.Path)
            {
                switch (c.Verb)
                {
                    case PathVerb.MoveTo: d.Append("M").Append(N(c.X)).Append(' ').Append(N(c.Y)).Append(' '); break;
                    case PathVerb.LineTo: d.Append("L").Append(N(c.X)).Append(' ').Append(N(c.Y)).Append(' '); break;
                    case PathVerb.CurveTo:
                        d.Append("C").Append(N(c.C1X)).Append(' ').Append(N(c.C1Y)).Append(' ').Append(N(c.C2X)).Append(' ')
                         .Append(N(c.C2Y)).Append(' ').Append(N(c.X)).Append(' ').Append(N(c.Y)).Append(' ');
                        break;
                    case PathVerb.Close: d.Append("Z "); break;
                }
            }
            string label = img.IsDecorative ? " aria-hidden=\"true\"" : string.IsNullOrEmpty(img.Alt) ? " role=\"img\"" : $" role=\"img\" aria-label=\"{Enc(img.Alt)}\"";
            string stroke = drawing.StrokeColor != null && drawing.StrokeWidthPt > 0
                ? $"stroke=\"{Enc(drawing.StrokeColor)}\" stroke-width=\"{N(drawing.StrokeWidthPt)}\""
                : "stroke=\"none\"";
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(img.WidthPt)}pt\" height=\"{N(img.HeightPt)}pt\" " +
                      $"viewBox=\"0 0 {N(img.WidthPt)} {N(img.HeightPt)}\" style=\"overflow:visible\"{label}>" +
                      $"<path d=\"{d.ToString().TrimEnd()}\" fill=\"{(drawing.FillColor != null ? Enc(drawing.FillColor) : "none")}\" {stroke}/></svg>");
        }

        /// <summary>
        /// Writes a footnote's reference mark as a superscript link to the note at the end.
        /// </summary>
        /// <remarks>
        /// <para><b>A link, not just a superscript number.</b> HTML has no page, so the note cannot
        /// sit at the foot of one — it goes in a section at the end of the document, and
        /// without an anchor a reader who meets the mark has no way to reach it. The pair of ids
        /// also lets the note link back.</para>
        ///
        /// <para>The mark comes from the same <c>FootnoteNumbering</c> the PDF export uses, so one
        /// document exported both ways numbers its notes identically.</para>
        /// </remarks>
        private void AppendFootnoteMark(StringBuilder sb, Footnote note)
        {
            string mark = _footnotes.MarkOf(note);
            if (mark.Length == 0) return;

            // Endnotes have their own section and their own ids (en…), so a footnote 1 and an
            // endnote 1 — two sequences, as the exporters number them — do not share an anchor.
            string prefix = IsEndnote(note) ? "en" : "fn";
            string id = NoteId(note);
            sb.Append("<sup id=\"").Append(prefix).Append("ref").Append(id).Append("\">")
              .Append("<a href=\"#").Append(prefix).Append(id).Append("\">").Append(Enc(mark)).Append("</a></sup>");
        }

        /// <summary>The notes collected so far, as lists at the end of the document — the footnotes, then the endnotes.</summary>
        /// <remarks>
        /// <b>Written even when empty is wrong, so it is not.</b> A document with no footnotes gets
        /// no section at all — an empty <c>&lt;ol&gt;</c> under a "Notes" heading is furniture
        /// a screen reader announces and a reader has to skip past. The same for the endnotes.
        /// </remarks>
        private void AppendFootnoteSection(StringBuilder sb)
        {
            AppendNoteSection(sb, _footnoteOrder, "footnotes", "fn");
            AppendNoteSection(sb, _endnoteOrder, "endnotes", "en");
        }

        private void AppendNoteSection(StringBuilder sb, List<Footnote> order, string cssClass, string prefix)
        {
            if (order.Count == 0) return;

            sb.Append("<section class=\"").Append(cssClass).Append("\">\n<hr/>\n<ol>\n");
            foreach (Footnote note in order)
            {
                string id = NoteId(note);
                sb.Append("<li id=\"").Append(prefix).Append(id).Append("\"><p>");
                RenderInline(sb, note.Runs);
                sb.Append(" <a href=\"#").Append(prefix).Append("ref").Append(id).Append("\">\u21a9</a></p></li>\n");
            }
            sb.Append("</ol>\n</section>\n");
        }

        private static bool IsEndnote(Footnote note) => note.Placement == NotePlacement.DocumentEnd;

        /// <summary>A stable id for a note, from the order it was first seen among its kind.</summary>
        /// <remarks>
        /// Looked up in <see cref="_noteIds"/>, not searched for in the order: an <c>IndexOf</c> per note made an
        /// export of n notes n²/2 comparisons — 0.2 s at 8 000 notes, growing twelvefold for each fourfold.
        /// </remarks>
        private string NoteId(Footnote note)
        {
            if (!_noteIds.TryGetValue(note, out int at))
            {
                List<Footnote> order = IsEndnote(note) ? _endnoteOrder : _footnoteOrder;
                order.Add(note);
                at = order.Count;
                _noteIds[note] = at;
            }
            return at.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private FootnoteNumbering _footnotes = new FootnoteNumbering();
        private readonly List<Footnote> _footnoteOrder = new List<Footnote>();
        private readonly List<Footnote> _endnoteOrder = new List<Footnote>();
        private readonly Dictionary<Footnote, int> _noteIds =
            new Dictionary<Footnote, int>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);

        /// <summary>
        /// Writes a table of contents as a navigation list of links to the headings.
        /// </summary>
        /// <remarks>
        /// <b>Links, not page numbers.</b> HTML has no page, so a number here would describe a
        /// layout the reader is not looking at — and a link is the more useful thing in a
        /// document one scrolls. <c>&lt;nav&gt;</c> is what lets a screen reader announce it as
        /// navigation and skip it, which is the whole point of a contents list nobody is reading
        /// line by line.
        /// </remarks>
        private void RenderToc(StringBuilder sb, TocBlock toc, Document document)
        {
            List<TocEntries.Entry> entries = TocEntries.Collect(document, toc.MaxLevel);
            if (entries.Count == 0) return;

            sb.Append("<nav class=\"toc\">\n");
            if (!string.IsNullOrEmpty(toc.Title))
                sb.Append("<h2>").Append(Enc(toc.Title)).Append("</h2>\n");

            sb.Append("<ul>\n");
            foreach (TocEntries.Entry entry in entries)
            {
                // margin-inline-start, not margin-left: the contents list indents each level on the
                // start side, which is the right side of an RTL document. A physical left margin put
                // the whole list's indentation on the wrong side there.
                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "<li style=\"margin-inline-start:{0}px\"><a href=\"#{1}\">{2}</a></li>\n",
                    SaturatingCast.ToInt((entry.Level - 1) * toc.LevelIndentPt),
                    Enc(entry.Anchor), Enc(entry.Text));
            }
            sb.Append("</ul>\n</nav>\n");
        }

        /// <summary>Writes one list item — its marker, its content, indented by level.</summary>
        /// <remarks>
        /// Extracted so a list item inside a <b>table cell</b> reaches it too. The cell loop
        /// handled only paragraphs and nested tables, so <c>CellBuilder.AddListItem</c> — public,
        /// in the API snapshot, and dropped in silence by all four exporters — produced nothing at
        /// all here. A branch that exists in one loop and not its sibling is the shape that costs
        /// content without a diagnostic.
        /// </remarks>

        /// <summary>Writes a list as a real <c>&lt;ul&gt;</c> or <c>&lt;ol&gt;</c>.</summary>
        /// <remarks>
        /// <para><b>Not a <c>&lt;p&gt;</c> with the marker as literal text and a pixel indent.</b>
        /// That looks right and carries none of the meaning: a screen reader announces no list, the
        /// items are not items, and the numbers are characters a copy picks up. The container is what makes the semantic element available — a flat item has
        /// no siblings to be an item among.</para>
        ///
        /// <para><b>An explicit marker survives in the only two ways HTML offers.</b> A numbered
        /// item whose marker parses as an integer becomes <c>&lt;li value&gt;</c>, which is exactly
        /// what it means; anything else — <c>"א."</c>, <c>"1.2."</c>, a custom bullet — is written
        /// as text with the automatic marker turned off, because renumbering a producer that had
        /// already resolved its own sequence would silently change the document.</para>
        /// </remarks>
        private void RenderList(StringBuilder sb, ListBlock list, bool containerRtl, int depth)
        {
            if (list == null || depth > ModelRules.MaxListDepth) return;

            bool rtl = ModelRules.IsRightToLeft(
                list.Direction ?? (containerRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                string.Empty);

            string tag = list.IsOrdered ? "ol" : "ul";
            sb.Append('<').Append(tag);
            if (rtl) sb.Append(" dir=\"rtl\"");
            if (list.IsOrdered && list.Start != 1)
                sb.AppendFormat(CultureInfo.InvariantCulture, " start=\"{0}\"", list.Start);
            sb.Append(">\n");

            for (int i = 0; i < list.Items.Count; i++)
            {
                ListItem li = list.Items[i];
                if (li == null) continue;

                bool numericMarker = list.IsOrdered && li.Marker != null
                    && int.TryParse(li.Marker.Trim().TrimEnd('.'), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out int value);

                sb.Append("<li");
                if (numericMarker)
                    sb.AppendFormat(CultureInfo.InvariantCulture, " value=\"{0}\"",
                        int.Parse(li.Marker!.Trim().TrimEnd('.'), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture));
                else if (li.Marker != null)
                    sb.Append(" style=\"list-style-type:none\"");
                sb.Append('>');

                if (li.Marker != null && !numericMarker) sb.Append(Enc(li.Marker));

                RenderInline(sb, ModelRules.ListRuns(li));

                if (li.Sublist != null)
                {
                    sb.Append('\n');
                    RenderList(sb, li.Sublist, rtl, depth + 1);
                }

                sb.Append("</li>\n");
            }

            sb.Append("</").Append(tag).Append(">\n");
        }
        private void RenderListItem(StringBuilder sb, ListItem li, bool containerRtl)
        {
            bool liRtl = ModelRules.IsRightToLeft(
                li.Direction ?? (containerRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                li.Text);
            sb.AppendFormat("<p{0} style=\"margin-{1}:{2}px\">{3}",
                liRtl ? " dir=\"rtl\"" : "", liRtl ? "right" : "left",
                li.Level * 20, Enc(ModelRules.ListMarker(li)));
            RenderInline(sb, ModelRules.ListRuns(li));
            sb.Append("</p>\n");
        }

        private void RenderTable(StringBuilder sb, Table table, bool rtl, int depth)
        {
            // A cell may hold its own table; each re-entry carries depth + 1, so a self-reference and a
            // long distinct chain are both refused at the ceiling, before the uncatchable stack overflow.
            if (depth > PdfLimits.MaxTableNestingDepth)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    "Tables nested more than " + PdfLimits.MaxTableNestingDepth + " deep were not written.");
                return;
            }
            sb.Append((rtl || table.RtlColumns) ? "<table dir=\"rtl\">\n" : "<table>\n");
            bool firstRow = true;
            for (int r = 0; r < table.Rows.Count; r++)
            {
                TableRow row = table.Rows[r];
                sb.Append("<tr>");
                bool header = row.IsHeader || firstRow;
                string tag = header ? "th" : "td";
                for (int ci = 0; ci < row.Cells.Count; ci++)
                {
                    TableCell cell = row.Cells[ci];
                    if (cell.VMerge == VerticalMerge.Continue) continue; // folded into the Restart cell above
                    string attrs = "";
                    if (cell.GridSpan > 1) attrs += $" colspan=\"{cell.GridSpan}\"";
                    // A Restart cell spans itself plus every Continue cell below it in the same grid
                    // column, written as a rowspan — dropping those Continue cells without one would
                    // leave each merged row one cell short and the whole table misaligned.
                    int rowSpan = RowSpanOf(table, r, ci);
                    if (rowSpan > 1) attrs += $" rowspan=\"{rowSpan}\"";
                    string css = "";
                    string? safeCellBg = SafeColor(cell.BackgroundColor);
                    if (safeCellBg != null) css += $"background-color:{safeCellBg};";
                    if (cell.VAlign == CellVerticalAlign.Center) css += "vertical-align:middle;";
                    else if (cell.VAlign == CellVerticalAlign.Bottom) css += "vertical-align:bottom;";
                    else if (cell.VAlign == CellVerticalAlign.Baseline) css += "vertical-align:baseline;";
                    if (css.Length > 0) attrs += $" style=\"{css}\"";

                    sb.AppendFormat("<{0}{1}>", tag, attrs);
                    foreach (var block in ModelRules.InFlow(cell.Content()))
                    {
                        if (block is Paragraph p)
                            // A cell paragraph gets the same <p>, direction and alignment the body
                            // path gives one, with the cell's own direction as the fallback for a
                            // paragraph that states none.
                            RenderParagraphCore(sb, p,
                                p.Direction ?? (rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                                ModelRules.IsRightToLeft(
                                    p.Direction ?? (rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                                    ModelRules.SampleText(p)));
                        else if (block is Table nt) RenderTable(sb, nt, rtl, depth + 1);
                        else if (block is ListItem li) RenderListItem(sb, li, rtl);
                        else if (block is ListBlock lb) RenderList(sb, lb, rtl, 0);
                    }
                    sb.AppendFormat("</{0}>", tag);
                }
                sb.Append("</tr>\n");
                firstRow = false;
            }
            sb.Append("</table>\n");
        }

        /// <summary>
        /// How many rows a cell spans vertically: itself plus each consecutive row below whose cell
        /// at the same grid position is <see cref="VerticalMerge.Continue"/>. The model keeps a cell
        /// per grid column in every row — the Continue placeholders the Restart cell absorbs — so
        /// the column position is the list index.
        /// </summary>
        private static int RowSpanOf(Table table, int rowIndex, int cellIndex)
        {
            int span = 1;
            for (int r = rowIndex + 1; r < table.Rows.Count; r++)
            {
                IReadOnlyList<TableCell> cells = table.Rows[r].Cells;
                if (cellIndex >= cells.Count) break;
                if (cells[cellIndex]?.VMerge != VerticalMerge.Continue) break;
                span++;
            }
            return span;
        }

        /// <summary>
        /// Escapes text for both element content and a quoted attribute value.
        /// </summary>
        /// <remarks>
        /// <para><b>The quotes are not decorative.</b> This escaped <c>&amp;</c>, <c>&lt;</c> and
        /// <c>&gt;</c> only, while being interpolated into <c>href="…"</c> — so a link target of
        /// <c>x" onmouseover="alert(1)</c>, which <c>HtmlToBuilderMapper</c> copies verbatim out of
        /// a source document, closed the attribute and opened an event handler in whatever page
        /// displays the export.</para>
        /// <para><c>&amp;</c> must be replaced first or the replacements escape each other's
        /// ampersands.</para>
        /// </remarks>
        private static string Enc(string? s) =>
            s?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
              .Replace("\"", "&quot;").Replace("'", "&#39;") ?? "";


        /// <summary>
        /// A CSS colour value that is safe to interpolate into a <c>style</c> attribute, or
        /// <c>null</c> when the model string is not one.
        /// </summary>
        /// <remarks>
        /// Escaping is not enough for a CSS context: <c>&amp;quot;</c> is decoded back to a quote
        /// before the CSS parser sees it, so an attribute value can still be closed from inside a
        /// stylesheet declaration. These values come from file-derived text (the DOCX mapper copies
        /// <c>w:color</c>), so they are validated instead — and rejected rather than repaired,
        /// which is what the PDF path already does with anything <see cref="Export.PdfExporter"/>'s
        /// <c>ParseColorD</c> cannot read.
        /// </remarks>
        private static string? SafeColor(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string v = value!.Trim();
            int start = v.Length > 0 && v[0] == '#' ? 1 : 0;
            int digits = v.Length - start;
            // A CSS hex colour is #RGB, #RGBA, #RRGGBB or #RRGGBBAA — 3, 4, 6 or 8 digits. The old
            // 3..8 range also let a 5- or 7-digit string through: hex, but not a CSS colour, so the
            // browser dropped the whole declaration.
            if (digits != 3 && digits != 4 && digits != 6 && digits != 8) return null;
            for (int i = start; i < v.Length; i++)
            {
                char c = v[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            // Always emit the leading '#'. A file-derived value is bare hex (the DOCX mapper copies
            // w:color as "FF0000"), which the PDF exporter's ParseColorD reads but CSS does not:
            // `color:FF0000` is invalid and silently ignored, so the HTML lost every colour the PDF
            // kept. A value that already had the '#' is written unchanged.
            return "#" + v.Substring(start);
        }

        // Detect the image media type from magic bytes rather than always claiming
        // image/jpeg (standards-mode consumers may reject a mislabeled data URI).
        private static string DetectImageMime(byte[] data)
        {
            if (data == null || data.Length < 4) return "application/octet-stream";
            if (RasterImageInspector.IsJpeg(data)) return "image/jpeg";
            if (RasterImageInspector.IsPng(data)) return "image/png";
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
            if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return "image/webp";
            return "application/octet-stream";
        }
    }
}
