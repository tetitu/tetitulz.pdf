// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A side-by-side band — a flex row, a grid, a column set, a float band — is as tall as its
    /// items, as a browser has it. Each maps onto a table, which carried the Builder's 16pt row
    /// floor and 4pt gap below: an invoice's flex header drew its rule 7px lower than Chrome, and a
    /// one-line flex row was 12px taller. The next block's text now sits one line below the band's,
    /// as it does below a plain block's (the control).
    /// </summary>
    public class BandHeightTests
    {
        private const string Next = "<div>NEXT</div>";

        /// <summary>How far below <c>AAA</c>'s baseline <c>NEXT</c>'s is.</summary>
        private static double Drop(string band, int px = 10)
        {
            string c = DecodeStreams(ToPdf($"<html><body style='font-size:{px}px'>" + band + Next + "</body></html>"));
            return YOf(c, "AAA") - YOf(c, "NEXT");
        }

        private static double PlainDrop => Drop("<div><div>AAA</div></div>");

        private const string FloatLine = "<div><div style='float:left;width:40%'>AAA</div><div style='float:left;width:40%'>BBB</div></div><div style='clear:both'></div>";
        private const string SpacedFlexRow = "<div style='display:flex;justify-content:space-between'><div style='width:30%'>AAA</div><div style='width:30%'>BBB</div></div>";
        private const string GridWithAnEmptyTrack = "<div style='display:grid;grid-template-columns:1fr 1fr 1fr'><div>AAA</div><div>BBB</div></div>";

        /// <summary>The control's drop is one 10px line at <c>normal</c>, 1.15 of the size: 8.625pt.</summary>
        [Fact]
        public void BelowAPlainBlockTheNextLineIsOneLineDown() => Assert.Equal(8.625, PlainDrop, 2);

        [Fact]
        public void AFlexRowIsAsTallAsItsItems()
            => Assert.Equal(PlainDrop, Drop("<div style='display:flex'><div>AAA</div><div>BBB</div></div>"), 2);

        [Fact]
        public void AGridIsAsTallAsItsItems()
            => Assert.Equal(PlainDrop, Drop("<div style='display:grid;grid-template-columns:1fr 1fr'><div>AAA</div><div>BBB</div></div>"), 2);

        [Fact]
        public void AColumnSetIsAsTallAsItsItems()
            => Assert.Equal(PlainDrop, Drop("<div style='column-count:2'><div>AAA</div><div>BBB</div></div>"), 2);

        [Fact]
        public void AFloatBandIsAsTallAsItsItems()
            => Assert.Equal(PlainDrop, Drop("<div><div style='float:left;width:40%'>AAA</div><div>BBB</div></div>"), 2);

        // ── the filler cells: free space and empty tracks hold a width and no height ──────────────

        /// <summary>A line of floats that does not fill the measure: the rest is a filler cell.</summary>
        [Fact]
        public void AFloatLinesFreeSpaceAddsNoHeight()
            => Assert.Equal(PlainDrop, Drop(FloatLine), 2);

        /// <summary>A flex row of fixed items spaced apart: the space between is a filler cell.</summary>
        [Fact]
        public void AFlexRowsFreeSpaceAddsNoHeight()
            => Assert.Equal(PlainDrop, Drop(SpacedFlexRow), 2);

        /// <summary>A grid track no item lands in is an empty cell.</summary>
        [Fact]
        public void AnEmptyGridTrackAddsNoHeight()
            => Assert.Equal(PlainDrop, Drop(GridWithAnEmptyTrack), 2);

        /// <summary>
        /// Nor any padding: a filler cell's 3pt default, 6pt in all, is hidden by a 10px line and
        /// not by a 4px one (3.45pt) — each filler case in small print against a plain block's.
        /// </summary>
        [Theory]
        [InlineData(FloatLine)]
        [InlineData(SpacedFlexRow)]
        [InlineData(GridWithAnEmptyTrack)]
        public void AFillerCellHasNoPadding(string band)
        {
            double plain = Drop("<div><div>AAA</div></div>", px: 4);
            Assert.Equal(3.45, plain, 2);
            Assert.Equal(plain, Drop(band, px: 4), 2);
        }
    }
}
