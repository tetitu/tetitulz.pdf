// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An inline box's corners are read one by one since batch 124 — <c>border-radius</c>'s four
    /// values, the <c>x / y</c> elliptical form, a longhand, a percentage among lengths — as a
    /// paragraph's box's are. Before, an inline box took its largest corner's radius for all four
    /// (so a tab rounded at the top only was a pill) or one percentage, and an image's clip likewise.
    /// </summary>
    /// <remarks>
    /// Each rounded corner of a box path is one <c>c</c> operator; a square corner is none. A curve's
    /// extent along x is its corner's horizontal radius and along y its vertical one. A pixel is 0.75pt.
    /// </remarks>
    public class InlineBoxCornerRadiiTests
    {
        private static readonly string Png = Convert.ToBase64String(MakeRgbPng(8, 8));

        private static List<(double Dx, double Dy)> CurveExtents(string c)
        {
            var extents = new List<(double, double)>();
            double px = 0, py = 0;
            foreach (Match m in Regex.Matches(c, @"((?:[-\d.]+ )+)(m|l|c)\n"))
            {
                double[] v = m.Groups[1].Value.Trim().Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                double ex = v[v.Length - 2], ey = v[v.Length - 1];
                if (m.Groups[2].Value == "c") extents.Add((Math.Abs(ex - px), Math.Abs(ey - py)));
                px = ex; py = ey;
            }
            return extents;
        }

        private static string Span(string style) => DecodeStreams(ToPdf(
            $"<html><body><p>A <span style='background-color:#0000ff;padding:2px 12px;{style}'>BBBBBBBB</span></p></body></html>"));

        /// <summary>
        /// <c>border-radius: 8px 0 0 0</c> rounds the top-left corner alone: one curve, 6pt each way,
        /// where the box used to take 6pt at all four. The control, <c>8px</c>, rounds four.
        /// </summary>
        [Fact]
        public void OneRoundedCorner_IsOneCurve_TheOthersSquare()
        {
            var e = CurveExtents(Span("border-radius:8px 0 0 0"));
            var curve = Assert.Single(e);
            Assert.Equal(6.0, curve.Dx, 2);
            Assert.Equal(6.0, curve.Dy, 2);

            Assert.Equal(4, CurveExtents(Span("border-radius:8px")).Count);
        }

        /// <summary>
        /// <c>border-radius: 8px / 4px</c> makes each corner a quarter ellipse, 6pt across and 3pt down —
        /// read as circular 6pt corners before.
        /// </summary>
        [Fact]
        public void AnEllipticalRadius_GivesEachCornerItsVerticalRadius()
        {
            var e = CurveExtents(Span("border-radius:8px / 4px"));
            Assert.Equal(4, e.Count);
            Assert.All(e, x => { Assert.Equal(6.0, x.Dx, 2); Assert.Equal(3.0, x.Dy, 2); });
        }

        /// <summary>
        /// A percentage at one corner — <c>border-radius: 0 0 50% 0</c> — rounds that corner by half the
        /// box's width across and half its height down, so on a wide box the one curve is wider than
        /// tall; such a corner used to read as 0 and leave the box square.
        /// </summary>
        [Fact]
        public void APercentAtOneCorner_IsThatShareOfTheBoxThere()
        {
            var e = CurveExtents(Span("border-radius:0 0 50% 0"));
            var curve = Assert.Single(e);
            Assert.True(curve.Dx > 1.5 * curve.Dy, $"the corner {curve.Dx:F2} × {curve.Dy:F2} is not half a wide box's width by half its height");
            Assert.True(curve.Dy > 3.0, $"the corner's vertical radius {curve.Dy:F2} is not half the box's height");
        }

        /// <summary>
        /// A longhand beside the shorthand overrides its corner — <c>border-radius: 8px</c> with
        /// <c>border-bottom-right-radius: 0</c> rounds three.
        /// </summary>
        [Fact]
        public void ALonghand_OverridesItsCorner()
        {
            Assert.Equal(3, CurveExtents(Span("border-radius:8px;border-bottom-right-radius:0")).Count);
        }

        /// <summary>
        /// An image is clipped to its own corners: <c>border-radius: 8px 0 0 0</c> clips the top-left
        /// corner alone, one curve of 6pt, where the clip used to round all four.
        /// </summary>
        [Fact]
        public void AnImage_IsClippedToItsOwnCorners()
        {
            string content = DecodeStreams(ToPdf(
                $"<html><body><img src='data:image/png;base64,{Png}' width='40' height='20' style='border-radius:8px 0 0 0'></body></html>"));
            int image = Regex.Match(content, @"cm\s*/\w+\s+Do\b").Index;
            int clip = content.LastIndexOf("W", image, StringComparison.Ordinal);
            Assert.True(clip > 0 && Regex.IsMatch(content.Substring(clip, image - clip), @"^W\s*n\b"), "the image is not clipped");
            int save = content.LastIndexOf("q\n", clip, StringComparison.Ordinal);
            var e = CurveExtents(content.Substring(save, clip - save));
            var curve = Assert.Single(e);
            Assert.Equal(6.0, curve.Dx, 2);
        }
    }
}
