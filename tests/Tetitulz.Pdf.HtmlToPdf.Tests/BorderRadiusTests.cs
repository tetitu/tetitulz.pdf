// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>border-radius</c> read per corner — the shorthand's one to four values in CSS's order and
    /// the four longhands — where the first value was applied to all four: a Bootstrap panel
    /// heading, rounded at its top only, drew rounded bottom corners over the panel's straight edge.
    /// </summary>
    public class BorderRadiusTests
    {
        /// <summary>How many rounded corners a shaded block draws: the curves in the page.</summary>
        private static int RoundedCorners(string style) =>
            Regex.Matches(DecodeStreams(ToPdf($"<html><body><div style='background-color:#0000ff;{style}'>A</div></body></html>")),
                @"\bc\n").Count;

        /// <summary>One value rounds all four; none rounds nothing (the control).</summary>
        [Fact]
        public void OneValueRoundsAllFour()
        {
            Assert.Equal(4, RoundedCorners("border-radius:6px"));
            Assert.Equal(0, RoundedCorners(""));
        }

        /// <summary>Four values are the four corners: <c>6px 6px 0 0</c> rounds the top two.</summary>
        [Fact]
        public void FourValuesAreTheFourCorners() => Assert.Equal(2, RoundedCorners("border-radius:6px 6px 0 0"));

        /// <summary>Two values are the diagonals: <c>6px 0</c> rounds top-left and bottom-right.</summary>
        [Fact]
        public void TwoValuesAreTheDiagonals() => Assert.Equal(2, RoundedCorners("border-radius:6px 0"));

        /// <summary>A longhand rounds its corner alone, and overrides the shorthand's value for it.</summary>
        [Fact]
        public void ALonghandRoundsItsCorner()
        {
            Assert.Equal(1, RoundedCorners("border-top-left-radius:6px"));
            Assert.Equal(3, RoundedCorners("border-radius:6px;border-bottom-left-radius:0"));
        }

        /// <summary>
        /// An elliptical radius is drawn, not reported as approximated — no more than a circular one
        /// is (the control); its four corners are drawn.
        /// </summary>
        [Fact]
        public void AnEllipticalRadiusIsNotReported()
        {
            static List<string> Reports(string style)
            {
                var seen = new List<string>();
                var o = Managed();
                o.OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) seen.Add(d.Detail); };
                ToPdf($"<html><body><div style='background-color:#0000ff;{style}'>A</div></body></html>", o);
                return seen;
            }
            Assert.Empty(Reports("border-radius:6px / 3px"));
            Assert.Empty(Reports("border-radius:6px"));
            Assert.Equal(4, RoundedCorners("border-radius:6px / 3px"));
        }

        /// <summary>Each curve's horizontal and vertical extent, from the point before it to its end.</summary>
        private static List<(double Dx, double Dy)> CurveExtentsOf(string c)
        {
            var extents = new List<(double, double)>();
            double px = 0, py = 0;
            foreach (Match m in Regex.Matches(c, @"((?:[-\d.]+ )+)(m|l|c)\n"))
            {
                double[] v = m.Groups[1].Value.Trim().Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                double ex = v[v.Length - 2], ey = v[v.Length - 1];
                if (m.Groups[2].Value == "c") extents.Add((System.Math.Abs(ex - px), System.Math.Abs(ey - py)));
                px = ex; py = ey;
            }
            return extents;
        }

        private static List<(double Dx, double Dy)> CurveExtents(string style) =>
            CurveExtentsOf(DecodeStreams(ToPdf($"<html><body><div style='background-color:#0000ff;{style}'>A<br>B<br>C</div></body></html>")));

        /// <summary>
        /// The shorthand's <c>x / y</c> makes each corner a quarter ellipse: 16px across (12pt) and
        /// 4px down (3pt); the list after the slash expands as the one before it does, so
        /// <c>16px / 4px 8px</c> gives the diagonals 3pt and 6pt.
        /// </summary>
        [Fact]
        public void TheShorthandsSlashGivesTheVerticalRadii()
        {
            var e = CurveExtents("border-radius:16px / 4px");
            Assert.Equal(4, e.Count);
            Assert.All(e, x => { Assert.Equal(12, x.Dx, 2); Assert.Equal(3, x.Dy, 2); });

            var two = CurveExtents("border-radius:16px / 4px 8px").Select(x => System.Math.Round(x.Dy, 2)).OrderBy(d => d).ToList();
            Assert.Equal(new[] { 3.0, 3, 6, 6 }, two);
        }

        /// <summary>A longhand's two lengths are its corner's horizontal and vertical radii; one length is circular (the control).</summary>
        [Fact]
        public void ALonghandsTwoLengthsAreItsRadii()
        {
            var e = Assert.Single(CurveExtents("border-top-left-radius:16px 4px"));
            Assert.Equal(12, e.Dx, 2);
            Assert.Equal(3, e.Dy, 2);
            var c = Assert.Single(CurveExtents("border-top-left-radius:16px"));
            Assert.Equal(12, c.Dy, 2);
        }

        /// <summary>A box of blocks takes the elliptical corners too.</summary>
        [Fact]
        public void ABoxOfBlocksTakesEllipticalCorners()
        {
            var e = CurveExtentsOf(DecodeStreams(ToPdf("<html><body><div style='background-color:#0000ff;border-radius:16px / 4px'><p>A</p><p>B</p></div></body></html>")));
            Assert.Equal(4, e.Count);
            Assert.All(e, x => { Assert.Equal(12, x.Dx, 2); Assert.Equal(3, x.Dy, 2); });
        }

        /// <summary>A paragraph whose box moves round a lifted float keeps its elliptical corners.</summary>
        [Fact]
        public void AMovedBoxKeepsItsEllipticalCorners()
        {
            var e = CurveExtentsOf(DecodeStreams(ToPdf("<html><body><div style='background-color:#0000ff;border-radius:16px / 4px'>A <span style='float:right'>N</span></div></body></html>")));
            Assert.Equal(4, e.Count);
            Assert.All(e, x => Assert.Equal(3, x.Dy, 2));
        }

        private static int Curves(string body) =>
            Regex.Matches(DecodeStreams(ToPdf("<html><body>" + body + "</body></html>")), @"\bc\n").Count;

        /// <summary>A box of blocks takes its corners too: a panel of two paragraphs rounded at its top draws two.</summary>
        [Fact]
        public void ABoxOfBlocksTakesItsCorners()
            => Assert.Equal(2, Curves("<div style='background-color:#0000ff;border-radius:6px 6px 0 0'><p>A</p><p>B</p></div>"));

        /// <summary>
        /// A rounded paragraph whose box moves round a lifted float keeps its corners on the moved
        /// box: still the two top ones.
        /// </summary>
        [Fact]
        public void AMovedBoxKeepsItsCorners()
            => Assert.Equal(2, Curves("<div style='background-color:#0000ff;border-radius:6px 6px 0 0'>A <span style='float:right'>N</span></div>"));

        /// <summary>
        /// An inline box rounds each corner by its own radius since batch 124: a badge rounded only on
        /// its right side draws its two right corners and leaves the left ones square. Before, it
        /// took its largest corner's radius for all four (and before that the first value — 0 — drew
        /// it square). <c>InlineBoxCornerRadiiTests</c> has the rest.
        /// </summary>
        [Fact]
        public void AnInlineBoxRoundsEachCornerByItsOwnRadius()
            => Assert.Equal(2, Curves("<p>A <span style='background-color:#0000ff;padding:2px;border-radius:0 10px 10px 0'>B</span></p>"));
    }
}

