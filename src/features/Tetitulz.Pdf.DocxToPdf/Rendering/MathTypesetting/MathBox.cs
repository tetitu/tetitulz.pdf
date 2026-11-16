// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting
{
    /// <summary>
    /// A laid-out piece of a formula, measured as <b>width, height above the baseline and depth
    /// below it</b> — the three numbers stacking needs and that advance widths alone cannot give.
    ///
    /// <para>
    /// Boxes draw in the body renderer's own space (PDF user space, y upward) at a given baseline,
    /// so a formula sits on the text baseline like any other inline content.
    /// </para>
    /// </summary>
    internal abstract class MathBox
    {
        internal double Width { get; set; }
        internal double Height { get; set; }
        internal double Depth { get; set; }

        /// <summary>Emits this box at <paramref name="x"/> on the baseline <paramref name="baselineY"/>.</summary>
        internal abstract void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx);
    }

    /// <summary>A run of glyphs in one style — the leaf of every formula.</summary>
    internal sealed class MathGlyphBox : MathBox
    {
        internal string Text;
        internal MathStyle Style;

        /// <summary>Set when the run goes through the embedded Identity-H path instead of WinAnsi.</summary>
        internal UnicodeText.GlyphRun Glyphs;
        internal string EmbeddedResName;

        /// <summary>
        /// The standard-14 face the run was measured in (<c>MathMeasure</c>), drawn in the same — not the
        /// style's family name: drawn by that, an equation measured in Times would be drawn in whatever
        /// face the family's raw name registered as.
        /// </summary>
        internal string PdfFontName;

        /// <summary>
        /// The vertical stretch a fence is drawn at (1 for none); <c>Height</c> and <c>Depth</c>
        /// are the stretched ones. A stretched fence is this box drawn through a text matrix, so its face,
        /// colour and encoding are the box's own: drawn apart, it was always written as WinAnsi text with
        /// no colour set — a fence from an embedded face came out as a space, in the previous colour.
        /// </summary>
        internal double VerticalScale = 1;

        /// <summary>This box stretched vertically by <paramref name="scale"/>.</summary>
        internal MathGlyphBox Stretched(double scale)
            => new MathGlyphBox
            {
                Text = Text, Style = Style, Glyphs = Glyphs, EmbeddedResName = EmbeddedResName,
                PdfFontName = PdfFontName, VerticalScale = VerticalScale * scale,
                Width = Width, Height = Height * scale, Depth = Depth * scale,
            };

        internal override void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx)
        {
            if (string.IsNullOrEmpty(Text) && Glyphs == null) return;

            ops.Append("BT\n");
            ctx.AppendFill(ops, Style.ColorHex);

            string font;
            if (Glyphs != null)
            {
                ctx.UseEmbeddedFont(EmbeddedResName);
                font = EmbeddedResName;
            }
            else font = ctx.RegisterFont(PdfFontName ?? Style.FontName);

            ops.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", font, Style.SizePt);
            if (VerticalScale != 1)
                ops.AppendFormat(CultureInfo.InvariantCulture, "1 0 0 {0:F4} {1:F2} {2:F2} Tm\n", VerticalScale, x, baselineY);
            else
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} Td\n", x, baselineY);

            if (Glyphs != null) ops.Append(UnicodeText.ShowText(Glyphs, Style.SizePt)).Append('\n');
            else ops.AppendFormat("({0}) Tj\n", RunRenderer.EncodeWinAnsiLiteral(Text));

            ops.Append("ET\n");
        }
    }

    /// <summary>
    /// A filled rectangle: a fraction bar, a radical's vinculum, an <c>m:bar</c>.
    ///
    /// <para>
    /// Drawing rules as paths rather than glyphs is what keeps 2-D setting possible with no maths
    /// font at all — the single most useful decision in this design, because Cambria Math is
    /// unreachable on every platform (it ships as a <c>.ttc</c>, which the font reader rejects).
    /// </para>
    /// </summary>
    internal sealed class MathRuleBox : MathBox
    {
        internal double Thickness;

        /// <summary>Distance from the baseline to the rule's bottom edge; may be negative.</summary>
        internal double RaiseToBottom;

        internal string ColorHex = "000000";

        internal override void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx)
        {
            if (Width <= 0 || Thickness <= 0) return;

            ops.Append("q\n");
            ctx.AppendFill(ops, ColorHex);
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2} re\nf\nQ\n",
                x, baselineY + RaiseToBottom, Width, Thickness);
        }
    }

    /// <summary>An arbitrary stroked outline, parameterised by its own box — the surd hook.</summary>
    internal sealed class MathPathBox : MathBox
    {
        /// <summary>Given (x, baselineY) writes the operators for this shape.</summary>
        internal Action<StringBuilder, double, double> Emit;

        internal override void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx)
            => Emit?.Invoke(ops, x, baselineY);
    }

    /// <summary>Children laid left to right on one baseline.</summary>
    internal sealed class MathHBox : MathBox
    {
        internal readonly List<MathBox> Children = new List<MathBox>();

        /// <summary>Recomputes the box's own dimensions from its children.</summary>
        internal MathHBox Pack()
        {
            double w = 0, h = 0, d = 0;
            foreach (var child in Children)
            {
                w += child.Width;
                h = Math.Max(h, child.Height);
                d = Math.Max(d, child.Depth);
            }
            Width = w; Height = h; Depth = d;
            return this;
        }

        /// <summary>An empty box of a given size — the vehicle for reserved-but-undrawn space.</summary>
        internal static MathHBox Phantom(double width, double height, double depth)
            => new MathHBox { Width = width, Height = height, Depth = depth };

        internal override void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx)
        {
            double cursor = x;
            foreach (var child in Children)
            {
                child.Draw(ops, cursor, baselineY, ctx);
                cursor += child.Width;
            }
        }
    }

    /// <summary>One child, drawn on a baseline shifted up (positive) or down (negative).</summary>
    internal sealed class MathShiftBox : MathBox
    {
        internal MathBox Child;
        internal double Shift;

        internal MathShiftBox Pack()
        {
            Width = Child?.Width ?? 0;
            Height = Math.Max(0, (Child?.Height ?? 0) + Shift);
            Depth = Math.Max(0, (Child?.Depth ?? 0) - Shift);
            return this;
        }

        internal override void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx)
            => Child?.Draw(ops, x, baselineY + Shift, ctx);
    }

    internal enum MathAlign { Left, Centre, Right }

    /// <summary>
    /// Children sharing one origin, each already shifted — how a fraction stacks, how a sub and a
    /// super sit above one another, and how an accent sits over its base.
    /// </summary>
    internal sealed class MathOverlayBox : MathBox
    {
        internal readonly List<MathBox> Children = new List<MathBox>();
        internal MathAlign Align = MathAlign.Centre;

        internal MathOverlayBox Pack()
        {
            double w = 0, h = 0, d = 0;
            foreach (var child in Children)
            {
                w = Math.Max(w, child.Width);
                h = Math.Max(h, child.Height);
                d = Math.Max(d, child.Depth);
            }
            Width = w; Height = h; Depth = d;
            return this;
        }

        internal override void Draw(StringBuilder ops, double x, double baselineY, MathDrawContext ctx)
        {
            foreach (var child in Children)
            {
                double offset = Align switch
                {
                    MathAlign.Centre => (Width - child.Width) / 2,
                    MathAlign.Right => Width - child.Width,
                    _ => 0,
                };
                child.Draw(ops, x + offset, baselineY, ctx);
            }
        }
    }
}


