// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Draws the text a shape carries — a Word text box (<c>wps:txbx/w:txbxContent</c>, whose
    /// contents are WordprocessingML paragraphs) or DrawingML shape text
    /// (<c>a:txBody</c> / <c>dsp:txBody</c>, whose contents are <c>a:p</c>/<c>a:r</c>/<c>a:t</c>).
    /// Both are read here because a shape can hold either and the layout is identical once the
    /// runs are extracted.
    ///
    /// <para>
    /// <b>Text must set a text matrix, not a text position.</b> The drawing frame has a negative
    /// vertical scale (see <see cref="DrawingTransform"/>), so glyphs placed with <c>Td</c> come
    /// out mirrored. <c>1 0 0 -1 tx ty Tm</c> re-flips them, and <c>Td</c> cannot express that.
    /// A substring assertion on the drawn string would not notice the difference, which is why
    /// the test for this asserts the matrix.
    /// </para>
    /// </summary>
    internal static class ShapeTextRenderer
    {
        // a:bodyPr insets, in EMU, per ECMA-376 §21.1.2.1.1 defaults.
        private const long DefaultLeftInsetEmu = 91440;   // 0.1"
        private const long DefaultTopInsetEmu = 45720;    // 0.05"
        private const long DefaultRightInsetEmu = 91440;
        private const long DefaultBottomInsetEmu = 45720;

        /// <summary>The DrawingML default run size (§21.1.2.3.9: <c>sz</c> defaults to 1800).</summary>
        private const double DefaultSizePt = 18.0;

        /// <summary>Line height as a multiple of the font size, matching the body renderer.</summary>
        private const double LineFactor = 1.2;

        private sealed class Line
        {
            internal string Text;
            internal double Width;
            internal double Size;
            internal string FontName;
            internal Rgb Color;
            internal int Align;    // 0 left, 1 centre, 2 right

            /// <summary>A line outside WinAnsi, shaped in an embedded font: its glyphs and the font's entry; null for a standard-face line.</summary>
            internal UnicodeText.GlyphRun Glyphs;
            internal DocxRenderContext.EmbeddedFontEntry Font;
        }

        /// <summary>A paragraph's text as read: its lines (a <c>w:br</c> or <c>w:cr</c> ends one) and the style of its first run.</summary>
        private sealed class ParagraphText
        {
            internal readonly List<string> Lines = new List<string>();
            internal double Size;
            internal string FontName;
            internal string Family;
            internal bool Bold, Italic;
            internal Rgb Color;
            internal int Align;
        }

        /// <summary>
        /// Whether the shape carries any text at all. Checked before the geometry is painted,
        /// because a text box commonly has no fill and no outline and would otherwise be
        /// discarded along with its text.
        /// </summary>
        internal static bool HasText(OpenXmlElement shape)
        {
            var body = FindTextBody(shape);
            return body != null && Paragraphs(body).Any(p => InlineRuns(p).Any(r => !string.IsNullOrWhiteSpace(r.InnerText)));
        }

        /// <summary>
        /// Emits the shape's text into <paramref name="ops"/>, laid out in the shape's own
        /// top-down box <c>(0,0)–(w,h)</c>. Returns false when the shape has no text.
        /// </summary>
        internal static bool Render(OpenXmlElement shape, double w, double h,
            StringBuilder ops, DocxRenderContext context, DrawingColorResolver colors)
        {
            var body = FindTextBody(shape);
            if (body == null) return false;

            var bodyPr = body.ChildElements.FirstOrDefault(e => e.LocalName == "bodyPr");
            double lIns = Emu(Attr(bodyPr, "lIns"), DefaultLeftInsetEmu);
            double tIns = Emu(Attr(bodyPr, "tIns"), DefaultTopInsetEmu);
            double rIns = Emu(Attr(bodyPr, "rIns"), DefaultRightInsetEmu);
            double bIns = Emu(Attr(bodyPr, "bIns"), DefaultBottomInsetEmu);

            double boxW = w - lIns - rIns;
            double boxH = h - tIns - bIns;
            if (boxW <= 0 || boxH <= 0) return false;

            // normAutofit shrinks the text rather than the box; Word states the factor it computed.
            double fontScale = 1.0;
            var autofit = bodyPr?.ChildElements.FirstOrDefault(e => e.LocalName == "normAutofit");
            if (autofit != null)
            {
                string fs = Attr(autofit, "fontScale");
                if (double.TryParse(fs, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) && pct > 0)
                    fontScale = pct / 100000.0;
            }

            var lines = LayoutLines(body, boxW, fontScale, context, colors);
            if (lines.Count == 0) return false;

            double totalHeight = lines.Sum(l => l.Size * LineFactor);
            double y = tIns + VerticalOffset(Attr(bodyPr, "anchor"), boxH, totalHeight);

            foreach (var line in lines)
            {
                double lineHeight = line.Size * LineFactor;
                double baseline = y + line.Size;          // top-down: the baseline sits below the top
                double x = lIns + line.Align switch
                {
                    1 => (boxW - line.Width) / 2.0,
                    2 => boxW - line.Width,
                    _ => 0.0,
                };

                ops.Append("BT\n");
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} rg\n",
                    line.Color.R, line.Color.G, line.Color.B);
                ops.Append(DrawingTransform.TextMatrix(x, baseline));
                if (line.Glyphs != null)
                {
                    // A line outside WinAnsi, drawn as the body draws one: shaped glyph ids in the
                    // embedded font, the glyphs recorded for the font's /ToUnicode.
                    line.Glyphs.RecordInto(line.Font.UsedGidToUnicode, line.Font.UsedGidToText);
                    context.UseEmbeddedFontOnCurrentPage(line.Font.ResName);
                    ops.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", line.Font.ResName, line.Size);
                    ops.Append(UnicodeText.ShowText(line.Glyphs, line.Size)).Append('\n');
                }
                else
                {
                    ops.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n",
                        context.RegisterFont(line.FontName), line.Size);
                    ops.AppendFormat("({0}) Tj\n", RunRenderer.EncodeWinAnsiLiteral(line.Text));
                }
                ops.Append("ET\n");

                y += lineHeight;
            }

            if (totalHeight > boxH)
                DocxDiagnostics.Report("ShapeTextRenderer.Render",
                    "the shape's text is taller than the shape; it was drawn overflowing rather than "
                        + "clipped, which is what Word does");

            return true;
        }

        private static double VerticalOffset(string anchor, double boxH, double textH)
            => anchor switch
            {
                "ctr" => Math.Max(0, (boxH - textH) / 2.0),
                "b" => Math.Max(0, boxH - textH),
                _ => 0.0,     // "t" and unspecified
            };

        // ------------------------------------------------------------------ line layout

        private static List<Line> LayoutLines(OpenXmlElement body, double boxW, double fontScale,
            DocxRenderContext context, DrawingColorResolver colors)
        {
            var lines = new List<Line>();

            foreach (var para in Paragraphs(body))
            {
                var text = ReadParagraph(para, fontScale, context, colors);
                foreach (string lineText in text.Lines)
                {
                    if (string.IsNullOrWhiteSpace(lineText)) continue;

                    if (UnicodeText.NeedsUnicode(lineText))
                    {
                        // Shaped in an embedded font, as a body run outside WinAnsi is: the DOCX's own
                        // font first, then the machine's. Drawn as WinAnsi bytes it would be mojibake,
                        // so with no covering font the line is left out and that is said.
                        var cps = UnicodeText.CodePointsToCover(lineText);
                        var program = context.ResolveEmbeddedFont(text.Family, text.Bold, text.Italic, cps)
                                      ?? FontResolver.Resolve(text.Family, text.Bold, text.Italic, cps, context.Options.AllowRestrictedSystemFonts);
                        if (program == null)
                        {
                            RunRenderer.ReportNoCoveringFont("ShapeTextRenderer.Render", text.Family, lineText);
                            continue;
                        }
                        var entry = context.RegisterEmbeddedFont(program);
                        foreach (string piece in Wrap(lineText, boxW, candidate => UnicodeText.MeasureWidth(candidate, program, text.Size)))
                        {
                            var glyphs = UnicodeText.Shape(piece, program, text.Size);
                            lines.Add(new Line
                            {
                                Text = piece, Glyphs = glyphs, Font = entry, Width = glyphs.WidthPts,
                                Size = text.Size, FontName = text.FontName, Color = text.Color, Align = text.Align,
                            });
                        }
                        continue;
                    }

                    foreach (string piece in Wrap(lineText, boxW, candidate => context.StyleResolver.MeasureTextWidth(candidate, text.FontName, text.Size)))
                        lines.Add(new Line
                        {
                            Text = piece,
                            Width = context.StyleResolver.MeasureTextWidth(piece, text.FontName, text.Size),
                            Size = text.Size,
                            FontName = text.FontName,
                            Color = text.Color,
                            Align = text.Align,
                        });
                }
            }

            return lines;
        }

        private static IEnumerable<string> Wrap(string text, double maxWidth, Func<string, double> measure)
        {
            var words = text.Split(' ');
            var current = new StringBuilder();

            foreach (string word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && measure(candidate) > maxWidth)
                {
                    yield return current.ToString();
                    current.Clear();
                    current.Append(word);
                }
                else
                {
                    current.Clear();
                    current.Append(candidate);
                }
            }

            if (current.Length > 0) yield return current.ToString();
        }

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// A shape paragraph's text and the style of its first run. The runs are read through what
        /// holds them — a hyperlink, a simple field, a content control, an insertion, custom XML, a
        /// smart tag — as the body's walk reads them; a <c>w:tab</c> is a space, and a <c>w:br</c> or
        /// <c>w:cr</c> ends the line. A WordprocessingML run's size comes through the style resolver
        /// (its own, its style's, the document's default), a DrawingML run's from its <c>sz</c>.
        /// </summary>
        private static ParagraphText ReadParagraph(OpenXmlElement para, double fontScale,
            DocxRenderContext context, DrawingColorResolver colors)
        {
            var text = new ParagraphText { Color = new Rgb(0, 0, 0) };
            var sb = new StringBuilder();
            double sizePt = 0;
            bool bold = false, italic = false;
            string typeface = null;
            var typedParagraph = para as DocumentFormat.OpenXml.Wordprocessing.Paragraph;

            void ReadRun(OpenXmlElement run)
            {
                // a:rPr uses sz in hundredths of a point; w:rPr uses w:sz in half-points, and a
                // WordprocessingML run without one takes its style's or the document's.
                var rPr = run.ChildElements.FirstOrDefault(e => e.LocalName == "rPr");
                if (rPr != null)
                {
                    if (sizePt <= 0)
                    {
                        string sz = Attr(rPr, "sz");
                        if (double.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out double hundredths)
                            && hundredths > 0)
                            sizePt = hundredths / 100.0;
                        else
                            sizePt = HalfPointSize(rPr);
                    }
                    bold |= IsOn(rPr, "b");
                    italic |= IsOn(rPr, "i");
                    typeface ??= Attr(rPr.ChildElements.FirstOrDefault(e => e.LocalName == "latin"), "typeface");

                    var solid = rPr.ChildElements.FirstOrDefault(e => e.LocalName == "solidFill");
                    if (solid != null && colors.TryResolve(solid, null, out var rgba)) text.Color = rgba.ToRgb();
                }
                if (sizePt <= 0 && typedParagraph != null && run is DocumentFormat.OpenXml.Wordprocessing.Run typedRun)
                {
                    double styled = context.StyleResolver.ResolveFontSizeInPoints(typedRun, typedParagraph, 0);
                    if (styled > 0) sizePt = styled;
                }

                foreach (var child in run.ChildElements)
                {
                    switch (child.LocalName)
                    {
                        case "t": sb.Append(child.InnerText); break;
                        case "tab": case "ptab": sb.Append(' '); break;
                        case "br": case "cr": text.Lines.Add(sb.ToString()); sb.Clear(); break;
                        case "noBreakHyphen": sb.Append('-'); break;
                    }
                }
            }

            foreach (var child in InlineRuns(para))
                ReadRun(child);
            text.Lines.Add(sb.ToString());

            if (sizePt <= 0 && typedParagraph != null)
                sizePt = context.StyleResolver.ResolveFontSizeInPoints(null, typedParagraph, 0);
            if (sizePt <= 0) sizePt = DefaultSizePt;
            text.Size = sizePt * fontScale;

            text.Align = (Attr(para.ChildElements.FirstOrDefault(e => e.LocalName == "pPr"), "algn")
                          ?? ParagraphJustification(para)) switch
            {
                "ctr" or "center" => 1,
                "r" or "right" or "end" => 2,
                _ => 0,
            };

            text.Bold = bold;
            text.Italic = italic;
            text.Family = typeface
                          ?? (typedParagraph != null ? context.StyleResolver.ResolveComplexScriptFontName(null, typedParagraph) : null);
            text.FontName = context.StyleResolver.MapToPdfFontName(typeface, bold, italic);
            return text;
        }

        /// <summary>
        /// The runs of a shape paragraph in order, through the elements that hold runs without being
        /// one: a hyperlink, a simple field's cached result, a content control's content, an
        /// insertion, custom XML, a smart tag, a bidirectional wrapper. By local name, since a text
        /// box's content is typed WordprocessingML and a DrawingML body is not.
        /// </summary>
        private static IEnumerable<OpenXmlElement> InlineRuns(OpenXmlElement holder)
        {
            foreach (var child in holder.ChildElements)
            {
                switch (child.LocalName)
                {
                    case "r":
                        yield return child;
                        break;
                    case "hyperlink": case "fldSimple": case "ins": case "moveTo": case "customXml":
                    case "smartTag": case "dir": case "bdo": case "sdtContent":
                        foreach (var run in InlineRuns(child)) yield return run;
                        break;
                    case "sdt":
                        var content = child.ChildElements.FirstOrDefault(e => e.LocalName == "sdtContent");
                        if (content != null) foreach (var run in InlineRuns(content)) yield return run;
                        break;
                }
            }
        }

        // w:rPr/w:sz@w:val is in half-points; a WordprocessingML text box uses that spelling.
        private static double HalfPointSize(OpenXmlElement rPr)
        {
            var sz = rPr.ChildElements.FirstOrDefault(e => e.LocalName == "sz");
            string v = Attr(sz, "val");
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double half) && half > 0
                ? PdfUnits.HalfPointsToPoints(half)
                : 0;
        }

        private static string ParagraphJustification(OpenXmlElement para)
            => Attr(para.ChildElements.FirstOrDefault(e => e.LocalName == "pPr")?
                        .ChildElements.FirstOrDefault(e => e.LocalName == "jc"), "val");

        private static bool IsOn(OpenXmlElement rPr, string name)
        {
            // DrawingML spells it as an attribute (b="1"); WordprocessingML as a child (<w:b/>).
            string attr = Attr(rPr, name);
            if (attr != null) return OoxmlOnOff.IsTrue(attr);

            return OoxmlOnOff.ElementIsOn(rPr.ChildElements.FirstOrDefault(e => e.LocalName == name));
        }

        private static IEnumerable<OpenXmlElement> Paragraphs(OpenXmlElement body)
            => body.ChildElements.Where(e => e.LocalName == "p");

        /// <summary>
        /// The shape's text body, whichever of the two spellings it uses. A Word text box nests
        /// <c>wps:txbx/w:txbxContent</c>; DrawingML and SmartArt use <c>a:txBody</c> /
        /// <c>dsp:txBody</c> directly.
        /// </summary>
        private static OpenXmlElement FindTextBody(OpenXmlElement shape)
        {
            var txbx = shape.ChildElements.FirstOrDefault(e => e.LocalName == "txbx");
            var content = txbx?.ChildElements.FirstOrDefault(e => e.LocalName == "txbxContent");
            if (content != null) return content;

            return shape.ChildElements.FirstOrDefault(e => e.LocalName == "txBody");
        }

        private static double Emu(string v, long fallback)
            => (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu) ? emu : fallback)
               / PdfUnits.EmuPerPoint;

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
