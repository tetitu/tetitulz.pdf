// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A flex item, a grid item and a column of a multi-column block add no padding of their own:
    /// their text starts where a block's does, at the container's edge. The tables they are laid
    /// out as kept the renderer's 3pt a side, so every item's text was 3pt in, and two items 6pt
    /// apart with no gap between them.
    /// </summary>
    public class LayoutCellPaddingTests
    {
        private static double Offset(string container)
        {
            string c = DecodeStreams(ToPdf("<html><body><p style='margin:0'>REF</p>" + container + "</body></html>"));
            return XOf(c, "ITEM") - XOf(c, "REF");
        }

        [Fact]
        public void AFlexItemsTextStartsAtTheContainersEdge()
            => Assert.Equal(0, Offset("<div style='display:flex'><div>ITEM</div><div>OTHER</div></div>"), 1);

        [Fact]
        public void AGridItemsTextStartsAtTheContainersEdge()
            => Assert.Equal(0, Offset("<div style='display:grid;grid-template-columns:1fr 1fr'><div>ITEM</div><div>OTHER</div></div>"), 1);

        [Fact]
        public void AColumnsTextStartsAtTheContainersEdge()
            => Assert.Equal(0, Offset("<div style='column-count:2'><p style='margin:0'>ITEM</p><p style='margin:0'>A</p><p style='margin:0'>B</p><p style='margin:0'>C</p></div>"), 1);

        /// <summary>
        /// Three columns 60pt apart are equal columns of text: the second column's text is as far
        /// from the first's as the third's is from the second's — each cell's width carries its
        /// own margins and nothing else, so an outer column (one margin) and a middle one (two)
        /// give equal content.
        /// </summary>
        [Fact]
        public void ThreeColumnsSpaceTheirTextEqually()
        {
            string c = DecodeStreams(ToPdf("<html><body><div style='column-count:3;column-gap:60pt'>" +
                "<p style='margin:0'>ONE</p><p style='margin:0'>TWO</p><p style='margin:0'>THREE</p></div></body></html>"));
            double one = XOf(c, "ONE"), two = XOf(c, "TWO"), three = XOf(c, "THREE");
            Assert.Equal(two - one, three - two, 1);
        }
    }
}
