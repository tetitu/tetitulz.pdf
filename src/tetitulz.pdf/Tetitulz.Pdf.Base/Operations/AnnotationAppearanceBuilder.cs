// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Builds the <c>/AP /N</c> appearance stream an annotation carries — the form a viewer
    /// actually draws (§12.5.5).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Every annotation this library wrote was created without an
    /// appearance, and that looked fine here: <c>Base.Rendering.AnnotationFallbackModel</c>
    /// synthesises one at draw time, so the annotations rendered correctly in this library's own
    /// viewer and in the SVG exporter. §12.5.5 says a viewer <i>should</i> generate an appearance
    /// for an annotation without one, but only for the subtypes it chooses to; a produced document
    /// that carries no <c>/AP</c> is at the mercy of whatever opens it. Writing the stream removes
    /// the question.</para>
    /// <para><b>Two subtypes deliberately get no appearance, and that is correct rather than
    /// missing.</b> A <c>Text</c> (sticky note) annotation's icon is chosen by the viewer from its
    /// <c>/Name</c> (§12.5.6.4) and every reader draws its own — writing one here would replace a
    /// familiar icon with ours in every reader at once. A <c>Link</c> has nothing to draw: its
    /// border is described by <c>/Border</c> and <c>/BS</c>, and the ones this library writes are
    /// explicitly width 0.</para>
    /// <para>The appearance is drawn in a box the size of the annotation's <c>/Rect</c> with its
    /// origin at (0,0), so <see cref="AnnotationAppearanceStamp.BuildPlacement"/> maps it back onto
    /// the rectangle with a pure translation. Quad points, which §12.5.6.10 gives in <b>page</b>
    /// space, are translated into that box.</para>
    /// </remarks>
    internal static class AnnotationAppearanceBuilder
    {
        /// <summary>Resource name for the multiply blend state a highlight needs.</summary>
        private const string MultiplyStateName = "GsMul";

        /// <summary>Resource name of the standard-14 font a text-bearing appearance draws with.</summary>
        private const string FontResourceName = "F1";

        /// <summary>Default border width when the annotation declares none (§12.5.4, Table 168).</summary>
        private const double DefaultBorderWidth = 1;

        // The free-text size and placement, and the stamp's caption, colour and line width, are the
        // renderers' fallback's (Rendering.AnnotationFallbackModel), so the two cannot differ.

        /// <summary>
        /// Builds the appearance form for <paramref name="annot"/>, or returns <c>null</c> when the
        /// subtype has none to draw.
        /// </summary>
        /// <param name="annot">The annotation dictionary, fully populated except for <c>/AP</c>.</param>
        /// <param name="width">The annotation rectangle's width, in points.</param>
        /// <param name="height">The annotation rectangle's height, in points.</param>
        /// <param name="originX">The rectangle's lower-left x, for translating page-space quads.</param>
        /// <param name="originY">The rectangle's lower-left y, for translating page-space quads.</param>
        /// <param name="reason">
        /// Why no appearance was built, when the result is <c>null</c>. "This subtype has none by
        /// design" and "this text could not be drawn" are different facts about the output.
        /// </param>
        internal static PdfStream Build(
            PdfDictionary annot, double width, double height,
            double originX, double originY, out string reason)
        {
            reason = null;
            if (annot == null) throw new ArgumentNullException(nameof(annot));

            string subtype = (annot[PdfName.Subtype] as PdfName)?.Value;
            if (subtype == null) { reason = "the annotation has no /Subtype"; return null; }

            if (width <= 0 || height <= 0)
            {
                reason = "the annotation rectangle is empty, so there is nowhere to draw";
                return null;
            }

            var resources = new PdfDictionary();
            string content;

            switch (subtype)
            {
                case PdfAnnotationSubtypes.Square:
                    content = BuildSquare(annot, width, height);
                    break;

                case PdfAnnotationSubtypes.Highlight:
                    content = BuildHighlight(annot, width, height, originX, originY, resources);
                    break;

                case PdfAnnotationSubtypes.Underline:
                case PdfAnnotationSubtypes.StrikeOut:
                    content = BuildRule(annot, subtype, width, height, originX, originY);
                    break;

                case PdfAnnotationSubtypes.FreeText:
                    content = BuildFreeText(annot, width, height, resources, out reason);
                    break;

                case PdfAnnotationSubtypes.Stamp:
                    content = BuildStamp(annot, width, height, resources);
                    break;

                case PdfAnnotationSubtypes.Ink:
                case PdfAnnotationSubtypes.PolyLine:
                case PdfAnnotationSubtypes.Polygon:
                case PdfAnnotationSubtypes.Line:
                    content = BuildPath(annot, subtype, originX, originY, out reason);
                    break;

                case PdfAnnotationSubtypes.Text:
                    reason = "a Text annotation's icon is chosen by the viewer from its /Name " +
                             "(§12.5.6.4), so writing one here would replace the reader's own";
                    return null;

                case PdfAnnotationSubtypes.Link:
                    reason = "a Link annotation draws nothing of its own; its border comes from " +
                             "/Border and /BS";
                    return null;

                default:
                    reason = $"no appearance is generated for /{subtype}";
                    return null;
            }

            if (content == null) return null;

            byte[] bytes = Latin1Bytes(content);
            var form = new PdfStream();
            form.Set(PdfName.Type, PdfName.XObject);
            form.Set(PdfName.Subtype, PdfName.Form);
            form.Set(PdfName.FormType, new PdfNumber(1));

            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(0)); bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(width)); bbox.Add(new PdfNumber(height));
            form.Set(PdfName.BBox, bbox);
            form.Set(PdfName.Resources, resources);
            form.RawData = bytes;
            form.Set(PdfName.Length, new PdfNumber(bytes.Length));
            return form;
        }

        // ----------------------------------------------------------------
        //  Subtypes
        // ----------------------------------------------------------------

        /// <summary>
        /// A stroked rectangle, optionally filled from <c>/IC</c>. Inset by half the border width so
        /// the stroke lands inside the rectangle rather than straddling its edge — a stroke is
        /// centred on its path, so a full-extent rectangle loses half its border to the /BBox clip.
        /// </summary>
        private static string BuildSquare(PdfDictionary annot, double width, double height)
        {
            double bw = BorderWidth(annot);
            double inset = bw / 2.0;
            double w = Math.Max(width - bw, 0);
            double h = Math.Max(height - bw, 0);

            var sb = new StringBuilder();
            sb.Append("q\n");

            string interior = ColorOperator(annot, PdfName.IC, stroke: false);
            if (interior != null) sb.Append(interior);

            string border = ColorOperator(annot, PdfName.C, stroke: true) ?? "0 0 0 RG\n";
            sb.Append(border);
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} w\n", Fmt(bw));
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2} {3} re\n",
                Fmt(inset), Fmt(inset), Fmt(w), Fmt(h));
            // A zero-width border means "no border" (§12.5.4), so the shape is filled or nothing.
            sb.Append(bw <= 0 ? (interior != null ? "f\n" : "n\n")
                              : (interior != null ? "B\n" : "S\n"));
            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>
        /// The quads filled in the annotation's colour, through a <b>multiply</b> blend so the text
        /// underneath stays legible.
        /// </summary>
        /// <remarks>
        /// Without the blend the fill is opaque and a highlight <i>hides</i> the words it marks —
        /// which is not a subtle difference, and is what a naive appearance produces. The blend is
        /// an <c>/ExtGState</c> on the form's own resources, so it cannot leak into the page.
        /// </remarks>
        private static string BuildHighlight(
            PdfDictionary annot, double width, double height,
            double originX, double originY, PdfDictionary resources)
        {
            var gs = new PdfDictionary();
            gs.Set(PdfName.Type, PdfName.ExtGState);
            gs.Set(PdfName.BM, new PdfName(PdfBlendModes.Multiply));
            var states = new PdfDictionary();
            states.Set(new PdfName(MultiplyStateName), gs);
            resources.Set(PdfName.ExtGState, states);

            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.AppendFormat("/{0} gs\n", MultiplyStateName);
            sb.Append(ColorOperator(annot, PdfName.C, stroke: false) ?? "1 1 0 rg\n");

            foreach (var q in Quads(annot, width, height, originX, originY))
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2} {3} re\n",
                    Fmt(q.X), Fmt(q.Y), Fmt(q.W), Fmt(q.H));

            sb.Append("f\n");
            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>
        /// A horizontal rule per quad: along the bottom for Underline, across the middle for
        /// StrikeOut (§12.5.6.10).
        /// </summary>
        private static string BuildRule(
            PdfDictionary annot, string subtype, double width, double height,
            double originX, double originY)
        {
            bool strike = subtype == PdfAnnotationSubtypes.StrikeOut;

            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.Append(ColorOperator(annot, PdfName.C, stroke: true) ?? "0 0 0 RG\n");

            foreach (var q in Quads(annot, width, height, originX, originY))
            {
                // Proportional to the quad's own height, so the rule under 8 pt text is thinner
                // than the rule under 24 pt text rather than a fixed hairline under both.
                double thickness = Math.Max(q.H * RuleThicknessFraction, MinimumRuleThickness);
                double y = strike ? q.Y + q.H / 2.0 : q.Y + thickness;

                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} w\n", Fmt(thickness));
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} m {2} {3} l S\n",
                    Fmt(q.X), Fmt(y), Fmt(q.X + q.W), Fmt(y));
            }

            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>
        /// The strokes the renderers draw for these subtypes without an <c>/AP</c>: an Ink's
        /// <c>/InkList</c> (§12.5.6.13), a PolyLine's or Polygon's <c>/Vertices</c> — a Polygon closed and
        /// filled from <c>/IC</c> (§12.5.6.9) — and a Line's <c>/L</c> (§12.5.6.7), in <c>/C</c> at the
        /// border width, dashed when <c>/BS</c> says so, the page-space points moved into the box.
        /// </summary>
        /// <remarks>
        /// <b>Refused where the annotation asks for drawing the fallback does not do</b> — line endings
        /// (<c>/LE</c> other than <c>/None</c>), a cloudy border (<c>/BE /S /C</c>), an intent
        /// (<c>/IT</c>: dimension, cloud, arrow), a Line's leader lines (<c>/LL</c>, <c>/LLE</c>,
        /// <c>/LLO</c>) or caption (<c>/Cap</c>): an appearance is authoritative, so one that left those
        /// out would replace a viewer's correct drawing with a wrong one.
        /// </remarks>
        private static string BuildPath(PdfDictionary annot, string subtype, double originX, double originY, out string reason)
        {
            reason = WhyThePathIsNotDrawnHere(annot, subtype);
            if (reason != null) return null;

            var paths = new List<double[]>();
            switch (subtype)
            {
                case PdfAnnotationSubtypes.Ink:
                    if (annot[PdfName.InkList] is PdfArray strokes)
                        foreach (PdfObject stroke in strokes)
                            if (Points(stroke as PdfArray) is double[] pts) paths.Add(pts);
                    break;
                case PdfAnnotationSubtypes.Line:
                    if (Points(annot[PdfName.L] as PdfArray) is double[] line && line.Length == 4) paths.Add(line);
                    break;
                default:
                    if (Points(annot[PdfName.Vertices] as PdfArray) is double[] vertices) paths.Add(vertices);
                    break;
            }
            if (paths.Count == 0)
            {
                reason = $"the /{subtype} annotation has no points to draw";
                return null;
            }

            bool closed = subtype == PdfAnnotationSubtypes.Polygon;
            string interior = closed ? ColorOperator(annot, PdfName.IC, stroke: false) : null;
            double bw = BorderWidth(annot);

            var sb = new StringBuilder();
            sb.Append("q\n");
            if (interior != null) sb.Append(interior);
            sb.Append(ColorOperator(annot, PdfName.C, stroke: true) ?? "0 0 0 RG\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} w\n", Fmt(bw));
            if (DashArray(annot) is string dash) sb.Append(dash);
            foreach (double[] pts in paths)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} m\n", Fmt(pts[0] - originX), Fmt(pts[1] - originY));
                for (int i = 2; i + 1 < pts.Length; i += 2)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} l\n", Fmt(pts[i] - originX), Fmt(pts[i + 1] - originY));
                if (closed) sb.Append("h\n");
                // A zero-width border draws no stroke (§12.5.4); a Polygon is then its fill or nothing.
                sb.Append(bw <= 0 ? (interior != null ? "f\n" : "n\n") : (interior != null ? "B\n" : "S\n"));
            }
            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>Why a path annotation asks for more than its points and border, or null when it does not.</summary>
        private static string WhyThePathIsNotDrawnHere(PdfDictionary annot, string subtype)
        {
            if (annot[PdfName.LE] is PdfObject endings)
            {
                IEnumerable<PdfObject> names = endings is PdfArray a ? (IEnumerable<PdfObject>)a : new[] { endings };
                foreach (PdfObject e in names)
                    if (!(e is PdfName n) || n.Value != "None")
                        return $"the /{subtype} annotation has line endings (/LE), which the viewer draws";
            }
            if (annot[PdfName.BE] is PdfDictionary effect && (effect[PdfName.S] as PdfName)?.Value == "C")
                return $"the /{subtype} annotation has a cloudy border (/BE), which the viewer draws";
            if (annot[PdfName.IT] is PdfName intent)
                return $"the /{subtype} annotation has the intent /{intent.Value} (/IT), which the viewer draws";
            if (subtype == PdfAnnotationSubtypes.Line)
            {
                if (annot[PdfName.LL] is PdfNumber ll && ll.DoubleValue != 0 || annot[PdfName.LLE] != null || annot[PdfName.LLO] != null)
                    return "the Line annotation has leader lines (/LL), which the viewer draws";
                if (annot[PdfName.Cap] is PdfBoolean cap && cap.Value)
                    return "the Line annotation has a caption (/Cap), which the viewer draws";
            }
            return null;
        }

        /// <summary>An even run of at least two finite points, or null.</summary>
        private static double[] Points(PdfArray array)
        {
            if (array == null || array.Count < 4 || array.Count % 2 != 0) return null;
            var pts = new double[array.Count];
            for (int i = 0; i < pts.Length; i++)
            {
                if (!(array[i] is PdfNumber n)) return null;
                double v = n.DoubleValue;
                if (double.IsNaN(v) || double.IsInfinity(v)) return null;
                pts[i] = v;
            }
            return pts;
        }

        /// <summary>The <c>d</c> operator for a dashed <c>/BS</c> (<c>/S /D</c>, its <c>/D</c> array or [3]), or null.</summary>
        private static string DashArray(PdfDictionary annot)
        {
            if (!(annot[PdfName.BS] is PdfDictionary bs) || (bs[PdfName.S] as PdfName)?.Value != "D") return null;
            var sb = new StringBuilder("[");
            if (bs[PdfName.D] is PdfArray d && d.Count > 0)
            {
                foreach (PdfObject o in d)
                    if (o is PdfNumber n && n.DoubleValue >= 0 && !double.IsInfinity(n.DoubleValue)) sb.Append(Fmt(n.DoubleValue)).Append(' ');
            }
            else sb.Append("3 ");
            return sb.ToString().TrimEnd() + "] 0 d\n";
        }

        /// <summary>Rule thickness as a fraction of the marked text's height.</summary>
        private const double RuleThicknessFraction = 0.06;

        /// <summary>Floor on rule thickness, so a rule is never thinner than a hairline.</summary>
        private const double MinimumRuleThickness = 0.5;

        /// <summary>
        /// The annotation's text, drawn from its <c>/DA</c> string.
        /// </summary>
        /// <remarks>
        /// <b>Refused for text outside WinAnsi.</b> The <c>/DA</c> names a standard-14 font, which
        /// cannot carry Hebrew, Arabic, Greek, Cyrillic or CJK — and unlike the invisible OCR layer,
        /// these glyphs are <i>seen</i>, so a non-embedded composite font would draw blank boxes.
        /// Embedding needs a font program the caller has to license, which this entry point has no
        /// way to ask for; until it does, no <c>/AP</c> is written and the viewer's own appearance
        /// generation — which today is what draws every one of these — is left to do the job.
        /// </remarks>
        private static string BuildFreeText(
            PdfDictionary annot, double width, double height,
            PdfDictionary resources, out string reason)
        {
            reason = null;
            // The text the renderers' fallback shows: /Contents, else /RC stripped of its markup — one
            // reading (AnnotationFallbackModel.FreeTextContents), so an annotation carrying only rich
            // text is not given an appearance drawing nothing. The dictionary is direct here.
            string text = Rendering.AnnotationFallbackModel.FreeTextContents(annot, null) ?? string.Empty;

            if (Core.Text.TextRunComposer.NeedsCompositeFont(text))
            {
                reason = "the text needs a composite font, which an appearance built from a /DA " +
                         "string cannot embed; no /AP was written and the viewer will draw its own";
                return null;
            }

            // The /DA reading, the default size and the placement are all the renderers' fallback's
            // (Base.Operations.Forms.DefaultAppearanceString, Rendering.AnnotationFallbackModel). The comment
            // here said the two agreed while this defaulted to 12 pt where the fallback used 10,
            // inset the text 4 pt where the fallback used 2, and put the baseline a full size below
            // the top where the fallback put it 1.1 sizes down. A document the library annotated
            // therefore looked different once its appearance was written.
            var da = Base.Operations.Forms.DefaultAppearanceString.Parse((annot[PdfName.DA] as PdfString)?.GetText());
            double size = da.FontSize is float s && s > 0 ? s : Base.Operations.Forms.DefaultAppearanceString.DefaultFontSize;
            string fill = (da.FillOperator() ?? "0 g") + "\n";

            AddStandard14(resources, StandardFonts.Helvetica);

            Rendering.AnnotationFallbackModel.FreeTextBaseline(
                0f, (float)height, (float)size, out float baselineX, out float baselineY);

            // Each line of the text on its own baseline (TL, T*), a line's height below the one
            // before: a CR or LF drawn inside one Tj is an undefined WinAnsi code, not a line break.
            string[] lines = text.Replace("\r\n", "\n").Split('\n', '\r');

            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.Append("BT\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1} Tf\n", FontResourceName, Fmt(size));
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} TL\n", Fmt(size * Rendering.AnnotationFallbackModel.FreeTextLineHeightFactor));
            sb.Append(fill);
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} Td\n",
                Fmt(baselineX), Fmt(Math.Max(baselineY, 0)));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append("T*\n");
                sb.AppendFormat("({0}) Tj\n", PdfStringEscape.EscapeLiteral(lines[i]));
            }
            sb.Append("ET\n");
            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>
        /// A bordered box carrying the stamp's name — the shape the renderer's fallback draws, so a
        /// stamped document looks the same before and after the appearance is written.
        /// </summary>
        /// <remarks>
        /// The name, the colour, the line width and the caption's size and place are the renderers'
        /// fallback's (<see cref="Rendering.AnnotationFallbackModel"/>). The summary above has always
        /// said so. It was not true: an "Approved" stamp was outlined in red here, where the fallback
        /// chooses green by name, at half the fallback's minimum width, with its caption placed by a
        /// different estimate. A stamp with no <c>/Name</c> took its label from <c>/Contents</c> here
        /// and read "Draft" in the fallback. Table 181 makes Draft the default.
        /// </remarks>
        private static string BuildStamp(
            PdfDictionary annot, double width, double height, PdfDictionary resources)
        {
            string label = Rendering.AnnotationFallbackModel.StampName(annot, null);

            // A stamp name is a PDF name (§12.5.6.12, Table 181), but a name need not be ASCII, and
            // the font here is a standard-14 one.
            if (Core.Text.TextRunComposer.NeedsCompositeFont(label)) label = string.Empty;

            SkiaSharp.SKColor declared = Rendering.AnnotationFallbackModel.AnnotationColor(
                annot, null, PdfName.C, SkiaSharp.SKColors.Black);
            SkiaSharp.SKColor color = Rendering.AnnotationFallbackModel.StampColor(label, declared);
            double lineWidth = Math.Max(BorderWidth(annot), Rendering.AnnotationFallbackModel.StampMinLineWidth);

            AddStandard14(resources, StandardFonts.HelveticaBold);

            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.Append(RgbOperator(color, stroke: true));
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} w\n", Fmt(lineWidth));
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} {2} {3} re S\n",
                Fmt(lineWidth / 2), Fmt(lineWidth / 2),
                Fmt(Math.Max(width - lineWidth, 0)),
                Fmt(Math.Max(height - lineWidth, 0)));

            if (label.Length > 0 && Rendering.AnnotationFallbackModel.StampCaption(
                    label, 0f, 0f, (float)width, (float)height,
                    out float size, out float baselineX, out float baselineY))
            {
                sb.Append("BT\n");
                sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1} Tf\n", FontResourceName, Fmt(size));
                sb.Append(RgbOperator(color, stroke: false));
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} Td\n", Fmt(baselineX), Fmt(baselineY));
                sb.AppendFormat("({0}) Tj\n", PdfStringEscape.EscapeLiteral(label));
                sb.Append("ET\n");
            }

            sb.Append("Q\n");
            return sb.ToString();
        }

        /// <summary>
        /// A resolved colour as an RGB colour operator, at the precision its 8-bit channels carry.
        /// </summary>
        private static string RgbOperator(SkiaSharp.SKColor c, bool stroke)
            => string.Join(" ", Fmt(c.Red / 255.0), Fmt(c.Green / 255.0), Fmt(c.Blue / 255.0))
               + (stroke ? " RG\n" : " rg\n");

        // ----------------------------------------------------------------
        //  Shared reading
        // ----------------------------------------------------------------

        /// <summary>A quad rectangle, translated into the appearance box's coordinates.</summary>
        private readonly struct Quad
        {
            internal Quad(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
            internal double X { get; }
            internal double Y { get; }
            internal double W { get; }
            internal double H { get; }
        }

        /// <summary>
        /// The annotation's <c>/QuadPoints</c> as rectangles in appearance space, or one rectangle
        /// covering the whole box when it declares none.
        /// </summary>
        /// <remarks>
        /// <b>The quads are in page space and the appearance box has its origin at (0,0)</b>, so
        /// every quad is translated by the rectangle's origin. Emitting them untranslated draws the
        /// marks at the page's own coordinates inside a box that is then placed at the rectangle —
        /// so a highlight halfway down the page lands off its own appearance entirely and is
        /// clipped to nothing. The fallback to the whole box matches §12.5.6.10's rule that a
        /// missing <c>/QuadPoints</c> means the <c>/Rect</c>.
        /// </remarks>
        private static IEnumerable<Quad> Quads(
            PdfDictionary annot, double width, double height, double originX, double originY)
        {
            var qp = annot[PdfName.QuadPoints] as PdfArray;
            if (qp == null || qp.Count < 8)
            {
                yield return new Quad(0, 0, width, height);
                yield break;
            }

            for (int i = 0; i + 8 <= qp.Count; i += 8)
            {
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                for (int c = 0; c < 8; c += 2)
                {
                    double x = (qp[i + c] as PdfNumber)?.DoubleValue ?? 0;
                    double y = (qp[i + c + 1] as PdfNumber)?.DoubleValue ?? 0;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
                yield return new Quad(minX - originX, minY - originY, maxX - minX, maxY - minY);
            }
        }

        /// <summary>
        /// The annotation's border width: <c>/BS /W</c> first, then <c>/Border</c>'s third element
        /// (§12.5.4), then the default of 1.
        /// </summary>
        private static double BorderWidth(PdfDictionary annot)
        {
            if (annot[PdfName.BS] is PdfDictionary bs && bs[PdfName.W] is PdfNumber w)
                return Math.Max(w.DoubleValue, 0);

            if (annot[PdfName.Border] is PdfArray border && border.Count >= 3
                && border[2] is PdfNumber bw)
                return Math.Max(bw.DoubleValue, 0);

            return DefaultBorderWidth;
        }

        /// <summary>
        /// The colour operator for an annotation colour array, or <c>null</c> when the key is
        /// absent — which for <c>/IC</c> means "no interior colour", not "black" (§12.5.6.8).
        /// </summary>
        /// <remarks>
        /// §12.5.2 gives a colour 1, 3 or 4 components: grey, RGB or CMYK. An <b>empty</b> array is
        /// legal and means "no colour", which is why the count is switched on rather than assumed
        /// to be 3.
        /// </remarks>
        private static string ColorOperator(PdfDictionary annot, PdfName key, bool stroke)
        {
            if (!(annot[key] is PdfArray c) || c.Count == 0) return null;

            var parts = new string[c.Count];
            for (int i = 0; i < c.Count; i++)
                parts[i] = Fmt((c[i] as PdfNumber)?.DoubleValue ?? 0);

            switch (c.Count)
            {
                case 1: return string.Join(" ", parts) + (stroke ? " G\n" : " g\n");
                case 3: return string.Join(" ", parts) + (stroke ? " RG\n" : " rg\n");
                case 4: return string.Join(" ", parts) + (stroke ? " K\n" : " k\n");
                default: return null;
            }
        }


        /// <summary>Registers a standard-14 font in the form's own resources.</summary>
        private static void AddStandard14(PdfDictionary resources, string baseFont)
        {
            var font = new PdfDictionary();
            font.Set(PdfName.Type, PdfName.Font);
            font.Set(PdfName.Subtype, PdfName.Type1);
            font.Set(PdfName.BaseFont, new PdfName(baseFont));
            font.Set(PdfName.Encoding, PdfName.WinAnsiEncoding);

            var fonts = new PdfDictionary();
            fonts.Set(new PdfName(FontResourceName), font);
            resources.Set(PdfName.Font, fonts);
        }

        private static byte[] Latin1Bytes(string s) => Core.Text.Latin1Text.Encode(s);

        private static string Fmt(double v) => PdfRealFormat.Format(v, "F4");
    }
}
