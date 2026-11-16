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
    /// A percentage <c>border-radius</c> at one corner among lengths, or in a longhand (CSS
    /// Backgrounds 3 §5.1, batch 108): each corner takes its own share of the box — a horizontal
    /// percentage of the width, a vertical one of the height — where before batch 108 only one
    /// percentage for all four corners was read and any other read as 0, reported. A box's corner is
    /// read off the curve that draws it: its extent across and down.
    /// </summary>
    public class PerCornerPercentTests
    {
        private const string Box = "background-color:#0000ff;padding:10px;";

        private static List<(double Dx, double Dy)> Curves(string style)
        {
            string c = DecodeStreams(ToPdf($"<html><body><div style='{Box}{style}'>A</div></body></html>"));
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

        /// <summary><c>50% 10px</c>: two corners half the full-width box across and half its height down, two at 7.5pt each way.</summary>
        [Fact]
        public void APercentAmongLengths_RoundsItsCornersByTheirShare()
        {
            var e = Curves("border-radius:50% 10px");

            Assert.Equal(4, e.Count);
            var wide = e.Where(x => x.Dx > 100).ToList();
            var small = e.Where(x => x.Dx <= 100).ToList();
            Assert.Equal(2, wide.Count);
            Assert.All(wide, x => Assert.True(x.Dy < x.Dx / 4, $"a 50% corner {x.Dx} × {x.Dy} is not half the height down: its vertical radius is of the width"));
            Assert.Equal(2, small.Count);
            Assert.All(small, x => { Assert.Equal(7.5, x.Dx, 2); Assert.Equal(7.5, x.Dy, 2); });
        }

        /// <summary>A longhand percentage rounds its corner alone: one curve, half the width across; the other three corners are square.</summary>
        [Fact]
        public void ALonghandPercent_RoundsItsCornerAlone()
        {
            var e = Curves("border-top-left-radius:50%");

            var corner = Assert.Single(e);
            Assert.True(corner.Dx > 100, $"the top-left corner {corner.Dx} across is not half the width");
            Assert.True(corner.Dy < corner.Dx / 4, $"the top-left corner {corner.Dy} down is not half the height");
        }

        /// <summary><c>50% / 10px</c>: every corner half the width across and 7.5pt down — a percentage horizontal radius with a length vertical one.</summary>
        [Fact]
        public void APercentAcross_WithALengthDown()
        {
            var e = Curves("border-radius:50% / 10px");

            Assert.Equal(4, e.Count);
            Assert.All(e, x => { Assert.True(x.Dx > 100, $"{x.Dx} across is not half the width"); Assert.Equal(7.5, x.Dy, 2); });
        }

        /// <summary><c>10px / 50%</c>: every corner 7.5pt across and half the height down — the percentage vertical radius is of the height, not the width.</summary>
        [Fact]
        public void APercentDown_IsOfTheHeight()
        {
            var e = Curves("border-radius:10px / 50%");
            var tall = Curves("border-radius:10px / 50%;padding:40px");

            Assert.Equal(4, e.Count);
            Assert.All(e, x => Assert.Equal(7.5, x.Dx, 2));
            // Taller box, taller corner: the vertical radius grew with the height, and is nowhere near half the width.
            Assert.True(tall[0].Dy > e[0].Dy + 20, $"the vertical radius did not grow with the box: {e[0].Dy} then {tall[0].Dy}");
            Assert.True(tall[0].Dy < 100, $"the vertical radius {tall[0].Dy} is of the width, not the height");
        }

        /// <summary>A longhand length beside the shorthand's percentage overrides its corner: three corners half the width, the top-left 7.5pt.</summary>
        [Fact]
        public void ALonghandLength_OverridesTheShorthandsPercentAtItsCorner()
        {
            var e = Curves("border-radius:50%;border-top-left-radius:10px");

            Assert.Equal(4, e.Count);
            Assert.Equal(3, e.Count(x => x.Dx > 100));
            var small = Assert.Single(e.Where(x => x.Dx <= 100));
            Assert.Equal(7.5, small.Dx, 2);
        }

        /// <summary>A box of blocks takes per-corner percentages too: two wide corners and two 7.5pt ones round a two-paragraph box.</summary>
        [Fact]
        public void ABoxOfBlocks_TakesPerCornerPercents()
        {
            string c = DecodeStreams(ToPdf($"<html><body><div style='{Box}border-radius:50% 10px'><p>A</p><p>B</p></div></body></html>"));
            var e = new PerCornerPercentTests().CurvesOf(c);

            Assert.Equal(4, e.Count);
            Assert.Equal(2, e.Count(x => x.Dx > 100));
            Assert.Equal(2, e.Count(x => Math.Abs(x.Dx - 7.5) < 0.05));
        }

        /// <summary>A paragraph whose box moves round a lifted float keeps its per-corner percentages.</summary>
        [Fact]
        public void AMovedBox_KeepsItsPerCornerPercents()
        {
            string c = DecodeStreams(ToPdf($"<html><body><div style='{Box}border-radius:50% 10px'>A <span style='float:right'>N</span></div></body></html>"));
            var e = new PerCornerPercentTests().CurvesOf(c);

            Assert.Equal(4, e.Count);
            Assert.Equal(2, e.Count(x => x.Dx > 100));
        }

        private List<(double Dx, double Dy)> CurvesOf(string c)
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
    }
}
