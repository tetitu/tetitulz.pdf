// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Turns DrawingML placement (<c>a:xfrm</c>) into PDF <c>cm</c> matrices.
    ///
    /// <para>
    /// <b>The one y-flip.</b> PDF's y axis grows upward; DrawingML's grows downward. Doing that
    /// conversion once, at the drawing level, is what lets everything inside — preset outlines,
    /// child offsets, group frames, shape text — be written exactly the way ECMA-376 describes it.
    /// The frame is opened with <see cref="OpenDrawingFrame"/>:
    /// </para>
    /// <code>
    /// q  1 0 0 -1 X YT cm      % X = left edge, YT = TOP edge, in PDF user space
    ///    … everything below is top-down, origin at the drawing's top-left, in points …
    /// Q
    /// </code>
    ///
    /// <para>
    /// <b>Consequence for text.</b> Glyphs drawn with <c>Td</c> inside this frame come out
    /// mirrored — the frame has a negative vertical scale. Shape text must set a text matrix that
    /// re-flips (<c>1 0 0 -1 tx ty Tm</c>); <c>Td</c> cannot express it. See
    /// <see cref="TextMatrix"/>.
    /// </para>
    /// </summary>
    internal static class DrawingTransform
    {
        /// <summary>
        /// A 2-D affine map in PDF's operand order: <c>x' = a·x + c·y + e</c>,
        /// <c>y' = b·x + d·y + f</c>.
        /// </summary>
        internal readonly struct Matrix
        {
            internal Matrix(double a, double b, double c, double d, double e, double f)
            {
                A = a; B = b; C = c; D = d; E = e; F = f;
            }

            internal double A { get; }
            internal double B { get; }
            internal double C { get; }
            internal double D { get; }
            internal double E { get; }
            internal double F { get; }

            internal static Matrix Identity => new Matrix(1, 0, 0, 1, 0, 0);

            /// <summary>Applying <c>this</c> and then <paramref name="outer"/>.</summary>
            internal Matrix Then(Matrix outer) => new Matrix(
                A * outer.A + B * outer.C,
                A * outer.B + B * outer.D,
                C * outer.A + D * outer.C,
                C * outer.B + D * outer.D,
                E * outer.A + F * outer.C + outer.E,
                E * outer.B + F * outer.D + outer.F);

            /// <summary>
            /// The uniform scale factor this matrix applies, as <c>sqrt(|det|)</c>. Used to keep a
            /// stroke's on-page width the same under a group frame that scales anisotropically:
            /// the pen would otherwise become elliptical, which Word never does. An approximation,
            /// exact only when the scale is uniform.
            /// </summary>
            internal double UniformScale
            {
                get
                {
                    double det = Math.Abs(A * D - B * C);
                    return det <= 0 ? 1.0 : Math.Sqrt(det);
                }
            }

            internal string ToCm()
                => string.Format(CultureInfo.InvariantCulture,
                    "{0:F4} {1:F4} {2:F4} {3:F4} {4:F2} {5:F2} cm\n", A, B, C, D, E, F);
        }

        /// <summary>
        /// Opens the drawing's coordinate frame: a <c>q</c> and the single y-flip that puts the
        /// origin at the drawing's top-left with y running downward, in points.
        /// </summary>
        internal static string OpenDrawingFrame(double leftX, double topY)
            => string.Format(CultureInfo.InvariantCulture, "q\n1 0 0 -1 {0:F2} {1:F2} cm\n", leftX, topY);

        internal const string CloseFrame = "Q\n";

        /// <summary>
        /// A shape's own placement from <c>a:xfrm</c>: offset, rotation about the shape's centre,
        /// and the two flips.
        ///
        /// <para>
        /// <b>The scale is deliberately absent.</b> A preset outline is built at its final size in
        /// points, so baking <c>a:ext</c> into the matrix would scale the pen with it and give
        /// anisotropic stroke widths. Only group frames scale, because there it is what Word does.
        /// </para>
        ///
        /// <para>
        /// <b>Rotation sign.</b> <c>a:xfrm/@rot</c> is clockwise-on-screen, in 60000ths of a
        /// degree. Inside the y-down frame this file establishes, clockwise-on-screen *is* the
        /// mathematically positive direction — so <c>θ</c> is used as-is, with no inversion. This
        /// is the line a reviewer should check.
        /// </para>
        /// </summary>
        internal static Matrix ShapeMatrix(OpenXmlElement xfrm, double x, double y, double w, double h)
        {
            double theta = Rotation(xfrm);
            bool flipH = Flag(xfrm, "flipH");
            bool flipV = Flag(xfrm, "flipV");

            double sx = flipH ? -1.0 : 1.0;
            double sy = flipV ? -1.0 : 1.0;
            double cos = Math.Cos(theta), sin = Math.Sin(theta);

            double a = sx * cos, b = sx * sin, c = -sy * sin, d = sy * cos;
            double e = x + w / 2 - (a * w / 2 + c * h / 2);
            double f = y + h / 2 - (b * w / 2 + d * h / 2);
            return new Matrix(a, b, c, d, e, f);
        }

        /// <summary>
        /// A group's frame: its children are authored in the group's <i>child</i> coordinate space
        /// (<c>a:chOff</c> / <c>a:chExt</c>) and must be mapped onto the space the group itself
        /// occupies (<c>a:off</c> / <c>a:ext</c>). Unlike a shape, this scale is real — Word does
        /// stretch group content — so strokes inside are compensated by
        /// <see cref="Matrix.UniformScale"/>.
        /// </summary>
        internal static Matrix GroupMatrix(OpenXmlElement xfrm)
        {
            if (xfrm == null) return Matrix.Identity;

            if (!TryPoint(xfrm, "off", out double ox, out double oy)) { ox = 0; oy = 0; }
            if (!TryPoint(xfrm, "ext", out double ew, out double eh)) return Matrix.Identity;
            if (!TryPoint(xfrm, "chOff", out double cox, out double coy)) { cox = 0; coy = 0; }
            if (!TryPoint(xfrm, "chExt", out double cew, out double ceh) || cew <= 0 || ceh <= 0)
                return new Matrix(1, 0, 0, 1, ox, oy);

            double sx = ew / cew, sy = eh / ceh;
            return new Matrix(sx, 0, 0, sy, ox - cox * sx, oy - coy * sy);
        }

        /// <summary>
        /// The text matrix for a glyph run inside the y-down drawing frame: the vertical re-flip
        /// that makes text read the right way up, positioned at <paramref name="x"/> /
        /// <paramref name="baselineY"/> in the frame's own coordinates.
        /// </summary>
        internal static string TextMatrix(double x, double baselineY)
            => string.Format(CultureInfo.InvariantCulture, "1 0 0 -1 {0:F2} {1:F2} Tm\n", x, baselineY);

        /// <summary>
        /// Reads a shape's <c>a:xfrm</c> box (offset + extent) in points, if it has one.
        /// A <c>dsp:sp</c> in a persisted SmartArt drawing always does, and its offsets are
        /// absolute within the diagram.
        /// </summary>
        internal static bool TryReadBox(OpenXmlElement xfrm,
            out double x, out double y, out double w, out double h)
        {
            x = y = w = h = 0;
            if (xfrm == null) return false;
            if (!TryPoint(xfrm, "ext", out w, out h) || w <= 0 || h <= 0) return false;
            if (!TryPoint(xfrm, "off", out x, out y)) { x = 0; y = 0; }
            return true;
        }

        /// <summary>The <c>a:xfrm</c> child of a shape-properties element, if present.</summary>
        internal static OpenXmlElement FindXfrm(OpenXmlElement spPr)
            => spPr?.ChildElements.FirstOrDefault(e => e.LocalName == "xfrm");

        internal static double Rotation(OpenXmlElement xfrm)
        {
            string rot = Attr(xfrm, "rot");
            if (string.IsNullOrEmpty(rot)) return 0;
            if (!double.TryParse(rot, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return 0;
            // 60000ths of a degree.
            return v / 60000.0 * Math.PI / 180.0;
        }

        private static bool TryPoint(OpenXmlElement xfrm, string childLocalName, out double a, out double b)
        {
            a = b = 0;
            var el = xfrm?.ChildElements.FirstOrDefault(e => e.LocalName == childLocalName);
            if (el == null) return false;

            // a:off uses x/y; a:ext uses cx/cy; a:chOff and a:chExt mirror them.
            string first = Attr(el, "x") ?? Attr(el, "cx");
            string second = Attr(el, "y") ?? Attr(el, "cy");
            if (!long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ea)) return false;
            if (!long.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out long eb)) return false;

            a = ea / PdfUnits.EmuPerPoint;
            b = eb / PdfUnits.EmuPerPoint;
            return true;
        }

        private static bool Flag(OpenXmlElement e, string name)
        {
            return OoxmlOnOff.IsTrue(Attr(e, name));
        }

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
