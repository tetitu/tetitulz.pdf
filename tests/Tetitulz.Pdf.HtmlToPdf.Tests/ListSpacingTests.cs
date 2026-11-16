// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A list's items take the line height they inherit and no gap between them, and the list
    /// takes its margins — CSS's, else the UA stylesheet's 1em at the top level and none nested —
    /// collapsing with its neighbours'. Items had the exporter's own line and 2pt after each, and
    /// a list no margin at all.
    /// </summary>
    public class ListSpacingTests
    {
        private static double Gap(string css, string body, string first, string second)
        {
            string c = DecodeStreams(ToPdf($"<html><head><style>{css}</style></head><body>{body}</body></html>"));
            double a = YOf(c, first), b = YOf(c, second);
            Assert.False(double.IsNaN(a) || double.IsNaN(b), "a text was not drawn");
            return a - b;
        }

        private const string Fixed = "body{font-size:10pt;line-height:15pt}";

        /// <summary>Two items are one inherited line apart: 15pt, no 2pt gap, no automatic line.</summary>
        [Fact]
        public void ItemsAreOneInheritedLineApart()
            => Assert.Equal(15, Gap(Fixed, "<ul style='margin:0'><li>A</li><li>B</li></ul>", "A", "B"), 2);

        /// <summary>
        /// A top-level list is 1em from its neighbours, as a paragraph is — and its margin collapses
        /// with a paragraph's (12pt, not 24); with <c>margin: 0</c>, the line alone (the control).
        /// </summary>
        [Fact]
        public void AListHasTheBrowsersMarginAndItCollapses()
        {
            double flush = Gap(Fixed, "<div>P</div><ul style='margin:0'><li>A</li></ul>", "P", "A");
            Assert.Equal(15, flush, 2);
            Assert.Equal(15 + 10, Gap(Fixed, "<div>P</div><ul><li>A</li></ul>", "P", "A"), 2);
            Assert.Equal(15 + 10, Gap(Fixed, "<p>P</p><ul><li>A</li></ul>", "P", "A"), 2);
            Assert.Equal(15 + 10, Gap(Fixed, "<ul><li>A</li></ul><div>Q</div>", "A", "Q"), 2);
        }

        /// <summary>A nested list has no margins: its first item is one line under its parent's.</summary>
        [Fact]
        public void ANestedListHasNoMargins()
        {
            string body = "<ul><li>A<ul><li>B</li></ul></li><li>C</li></ul>";
            Assert.Equal(15, Gap(Fixed, body, "A", "B"), 2);
            Assert.Equal(15, Gap(Fixed, body, "B", "C"), 2);
        }

        /// <summary>An ordered item's marker shares its text's baseline under a set line height.</summary>
        [Fact]
        public void AMarkerSharesItsItemsBaseline()
        {
            string c = DecodeStreams(ToPdf("<html><head><style>body{line-height:30pt}</style></head>" +
                                           "<body><ol><li>A</li></ol></body></html>"));
            Assert.Equal(YOf(c, "A"), YOf(c, "1."), 2);
        }

        /// <summary>
        /// A list in a table cell takes its margins inside the cell, measured and drawn alike: the
        /// next row is 2 × 1em further down than under a list with none.
        /// </summary>
        [Fact]
        public void AListInACellTakesItsMargins()
        {
            static string Table(string list) =>
                $"<table><tr><td style='padding:0'>{list}</td></tr><tr><td style='padding:0'>B</td></tr></table>";
            double flush = Gap(Fixed, Table("<ul style='margin:0'><li>A</li></ul>"), "A", "B");
            double spaced = Gap(Fixed, Table("<ul><li>A</li></ul>"), "A", "B");
            Assert.Equal(flush + 10, spaced, 2);
        }

        /// <summary>In a table cell too, two items are one inherited line apart, with no gap.</summary>
        [Fact]
        public void ItemsInACellAreOneInheritedLineApart()
            => Assert.Equal(15, Gap(Fixed,
                   "<table><tr><td style='padding:0'><ul style='margin:0'><li>A</li><li>B</li></ul></td></tr></table>", "A", "B"), 2);

        /// <summary>A list's bottom margin comes before what follows it in the same cell: 1em.</summary>
        [Fact]
        public void AListsBottomMarginSpacesTheNextBlockInItsCell()
            => Assert.Equal(15 + 10, Gap(Fixed,
                   "<table><tr><td style='padding:0'><ul><li>A</li></ul><div>B</div></td></tr></table>", "A", "B"), 2);

        /// <summary>A list's bottom margin collapses with the next paragraph's top: 1em, not 2.</summary>
        [Fact]
        public void AListsBottomMarginCollapsesWithTheNextParagraphs()
            => Assert.Equal(15 + 10, Gap(Fixed, "<ul><li>A</li></ul><p>Q</p>", "A", "Q"), 2);
    }
}
