// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A list item's own vertical margins — found against Chrome on a report's
    /// <c>ol li { margin-bottom: 4px }</c>, which moved nothing: every item had none. Adjoining
    /// margins collapse, an item's bottom margin comes after its nested list, and the outer items'
    /// collapse into the list's. Each is measured against the same list without it.
    /// </summary>
    public class ListItemMarginTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private static double Drop(string c, string from, string to) => YOf(c, from) - YOf(c, to);

        private static string TwoItems(string first, string second = "")
            => "<ul style='margin:0'><li" + first + ">AAA</li><li" + second + ">BBB</li></ul>";

        /// <summary>8px after the first item is 6pt more between the two.</summary>
        [Fact]
        public void ABottomMarginSeparatesTheItems()
        {
            double plain = Drop(Content(TwoItems("")), "AAA", "BBB");
            Assert.Equal(plain + 6, Drop(Content(TwoItems(" style='margin-bottom:8px'")), "AAA", "BBB"), 1);
        }

        /// <summary>The first's 8px bottom and the second's 4px top collapse to 8px, not 12.</summary>
        [Fact]
        public void AdjoiningMarginsCollapse()
        {
            double plain = Drop(Content(TwoItems("")), "AAA", "BBB");
            Assert.Equal(plain + 6, Drop(Content(TwoItems(" style='margin-bottom:8px'", " style='margin-top:4px'")), "AAA", "BBB"), 1);
            Assert.Equal(plain + 9, Drop(Content(TwoItems("", " style='margin-top:12px'")), "AAA", "BBB"), 1);
        }

        /// <summary>
        /// An item's bottom margin is after its nested list: nothing between the item's text and
        /// its sublist, the 6pt between the sublist and the next item.
        /// </summary>
        [Fact]
        public void ABottomMarginIsAfterTheNestedList()
        {
            const string Plain = "<ul style='margin:0'><li>AAA<ul><li>SUB</li></ul></li><li>BBB</li></ul>";
            const string Spaced = "<ul style='margin:0'><li style='margin-bottom:8px'>AAA<ul><li>SUB</li></ul></li><li>BBB</li></ul>";
            string plain = Content(Plain), spaced = Content(Spaced);
            Assert.Equal(Drop(plain, "AAA", "SUB"), Drop(spaced, "AAA", "SUB"), 1);
            Assert.Equal(Drop(plain, "SUB", "BBB") + 6, Drop(spaced, "SUB", "BBB"), 1);
        }

        /// <summary>The first item's top margin collapses into the list's: 20px above it is 15pt more above the list.</summary>
        [Fact]
        public void TheFirstItemsTopMarginIsTheLists()
        {
            double plain = Drop(Content("<p style='margin:0'>P</p>" + TwoItems("")), "P", "AAA");
            Assert.Equal(plain + 15, Drop(Content("<p style='margin:0'>P</p>" + TwoItems(" style='margin-top:20px'")), "P", "AAA"), 1);

            // Collapsed with the list's own, not added to it: 30px and 20px are 30px (22.5pt).
            string withList = "<p style='margin:0'>P</p><ul style='margin:30px 0 0'><li style='margin-top:20px'>AAA</li></ul>";
            Assert.Equal(plain + 22.5, Drop(Content(withList), "P", "AAA"), 1);
        }

        /// <summary>The last item's bottom margin collapses into the list's: 20px under it is 15pt more before the next block.</summary>
        [Fact]
        public void TheLastItemsBottomMarginIsTheLists()
        {
            double plain = Drop(Content(TwoItems("") + "<p style='margin:0'>P</p>"), "BBB", "P");
            Assert.Equal(plain + 15, Drop(Content(TwoItems("", " style='margin-bottom:20px'") + "<p style='margin:0'>P</p>"), "BBB", "P"), 1);
        }
    }
}
