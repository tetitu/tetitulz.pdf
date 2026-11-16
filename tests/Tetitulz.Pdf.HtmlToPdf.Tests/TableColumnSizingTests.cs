// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An HTML table's columns are sized by their content (<c>table-layout: auto</c>, CSS's default)
    /// — a form's label column as narrow as its labels, as Chrome draws it — and split evenly under
    /// <c>table-layout: fixed</c>.
    /// </summary>
    public class TableColumnSizingTests
    {
        private static double FirstColumnWidth(string tableStyle)
        {
            string html = "<html><body><table style='width:100%;" + tableStyle + "'>" +
                          "<tr><td>Name</td><td>second column value</td></tr>" +
                          "</table></body></html>";
            string content = DecodeStreams(ToPdf(html));
            double a = XOf(content, "Name");
            double b = XOf(content, "second");   // each word is its own run
            Assert.False(double.IsNaN(a) || double.IsNaN(b), "a cell's text was not found");
            return b - a;
        }

        /// <summary>
        /// By default the label column takes far less than half — the even split
        /// <c>table-layout: fixed</c> keeps (the control).
        /// </summary>
        [Fact]
        public void AnAutoTableSizesItsColumnsByContentAndAFixedOneSplitsEvenly()
        {
            double fixedWidth = FirstColumnWidth("table-layout:fixed");
            double autoWidth = FirstColumnWidth("");

            Assert.True(autoWidth < fixedWidth / 2, $"auto label column {autoWidth}pt, fixed {fixedWidth}pt");
        }
    }
}
