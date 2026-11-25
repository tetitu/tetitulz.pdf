// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Legacy <c>&lt;font size&gt;</c>: a leading <c>+</c> or <c>-</c> is relative to the default level 3,
    /// not an absolute level. With the sign stripped, <c>size="+1"</c> would read as the absolute level 1
    /// (8 pt) instead of level 4 (14 pt), and every relative size would come out inverted and tiny.
    /// </summary>
    public class FontTagSizeTests
    {
        private static double[] TfSizes(string html)
        {
            string content = PdfContentProbe.DecodeStreams(HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed()));
            return Regex.Matches(content, @"(-?[\d.]+)\s+Tf")
                        .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                        .ToArray();
        }

        [Theory]
        [InlineData("+1", 14.0)]   // relative: level 3 + 1 = 4 → 14 pt
        [InlineData("-1", 10.0)]   // relative: level 3 - 1 = 2 → 10 pt
        [InlineData("5", 18.0)]    // absolute: level 5 → 18 pt (control: absolute sizes are unchanged)
        public void FontTagSize_RelativeIsOffsetFromLevelThree(string size, double pt)
        {
            double[] sizes = TfSizes("<p><font size=\"" + size + "\">HelloFont</font></p>");
            Assert.Contains(sizes, s => Math.Abs(s - pt) < 0.01);
        }
    }
}
