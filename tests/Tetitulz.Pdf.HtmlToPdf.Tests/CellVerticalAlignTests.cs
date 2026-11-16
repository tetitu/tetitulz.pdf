// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A table cell is centred vertically unless something says otherwise — the UA stylesheet's
    /// <c>tr, tbody { vertical-align: middle }</c>, which cells inherit. Found against Chrome on a
    /// report: a rowspan cell's text sat at the top, where a browser centres it. Its own
    /// <c>vertical-align</c>, its row's and its row group's — by CSS or <c>valign</c> — still decide.
    /// </summary>
    public class CellVerticalAlignTests
    {
        private static string Content(string table) => DecodeStreams(ToPdf("<html><body>" + table + "</body></html>"));

        private static string RowSpan(string tbody = "<tbody>", string tr = "<tr>", string td = "<td rowspan='2'>")
            => "<table>" + tbody + tr + td + "AAA</td><td>B1</td></tr><tr><td>B2</td></tr></tbody></table>";

        [Fact]
        public void ARowspanCellIsCentredByDefault()
        {
            string c = Content(RowSpan());
            Assert.Equal((YOf(c, "B1") + YOf(c, "B2")) / 2, YOf(c, "AAA"), 1);
        }

        [Fact]
        public void BesideATallerCellAShortOneIsCentred()
        {
            string c = Content("<table><tr><td>AAA</td><td>L1<br>L2<br>L3</td></tr></table>");
            Assert.Equal(YOf(c, "L2"), YOf(c, "AAA"), 1);
        }

        /// <summary>The cell's own <c>vertical-align: top</c> — the control that the others still apply.</summary>
        [Fact]
        public void ItsOwnTopIsTop()
        {
            string c = Content(RowSpan(td: "<td rowspan='2' style='vertical-align:top'>"));
            Assert.Equal(YOf(c, "B1"), YOf(c, "AAA"), 1);
        }

        [Fact]
        public void ItsRowsValignDecides()
        {
            string c = Content(RowSpan(tr: "<tr valign='top'>"));
            Assert.Equal(YOf(c, "B1"), YOf(c, "AAA"), 1);
        }

        [Fact]
        public void ItsRowGroupsVerticalAlignDecides()
        {
            string c = Content(RowSpan(tbody: "<tbody style='vertical-align:bottom'>"));
            Assert.Equal(YOf(c, "B2"), YOf(c, "AAA"), 1);
        }

        /// <summary>The cell's own <c>middle</c> wins over its row's <c>top</c>.</summary>
        [Fact]
        public void ItsOwnMiddleWinsOverItsRow()
        {
            string c = Content(RowSpan(tr: "<tr valign='top'>", td: "<td rowspan='2' style='vertical-align:middle'>"));
            Assert.Equal((YOf(c, "B1") + YOf(c, "B2")) / 2, YOf(c, "AAA"), 1);
        }

        /// <summary>
        /// Nothing outside the table decides: <c>vertical-align</c> does not inherit, so the cell
        /// of an OUTER table with <c>valign="top"</c> leaves a nested table's cells centred.
        /// </summary>
        [Fact]
        public void AnEnclosingCellDoesNotDecide()
        {
            string c = Content("<table><tr><td valign='top'>" + RowSpan() + "</td></tr></table>");
            Assert.Equal((YOf(c, "B1") + YOf(c, "B2")) / 2, YOf(c, "AAA"), 1);
        }

        /// <summary><c>inherit</c> on the cell passes the question up to its row.</summary>
        [Fact]
        public void InheritAsksTheRow()
        {
            string c = Content(RowSpan(tr: "<tr style='vertical-align:bottom'>", td: "<td rowspan='2' style='vertical-align:inherit'>"));
            Assert.Equal(YOf(c, "B2"), YOf(c, "AAA"), 1);
        }
    }
}
