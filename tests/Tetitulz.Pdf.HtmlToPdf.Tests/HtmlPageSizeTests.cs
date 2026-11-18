// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <see cref="HtmlToPdfOptions.PageSize"/>: a common size written to <see cref="HtmlToPdfOptions.PageWidth"/>
    /// and <see cref="HtmlToPdfOptions.PageHeight"/>, read back from them, and a custom size left as the
    /// two strings say.
    /// </summary>
    public class HtmlPageSizeTests
    {
        private static double[] MediaBoxOf(HtmlToPdfOptions options)
        {
            options.RenderEngine = HtmlRenderEngine.Managed;
            byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes("<p>Hello</p>", options);
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            PdfArray box = doc.Pages[0].MediaBox;
            var values = new double[box.Count];
            for (int i = 0; i < box.Count; i++) values[i] = ((PdfNumber)box[i]).DoubleValue;
            return values;
        }

        public static TheoryData<HtmlPageSize> EverySize()
        {
            var data = new TheoryData<HtmlPageSize>();
            foreach (HtmlPageSize size in Enum.GetValues(typeof(HtmlPageSize))) data.Add(size);
            return data;
        }

        [Theory]
        [MemberData(nameof(EverySize))]
        public void EverySize_ReadsBackAsItself_InUnitsBothEnginesRead(HtmlPageSize size)
        {
            var options = new HtmlToPdfOptions { PageSize = size };

            Assert.Equal(size, options.PageSize);
            // Chromium's page size takes px, in, cm and mm; pt and pc are the managed engine's alone.
            foreach (string side in new[] { options.PageWidth, options.PageHeight })
                Assert.True(side.EndsWith("mm", StringComparison.Ordinal) || side.EndsWith("in", StringComparison.Ordinal), side);
        }

        [Fact]
        public void TheDefault_ReadsAsLetter()
            => Assert.Equal(HtmlPageSize.Letter, new HtmlToPdfOptions().PageSize);

        [Fact]
        public void A4_IsTheA4MediaBox_AndLandscapeTurnsIt()
        {
            double[] portrait = MediaBoxOf(new HtmlToPdfOptions { PageSize = HtmlPageSize.A4 });
            Assert.Equal(PdfPageSizes.A4Width, portrait[2], 1);
            Assert.Equal(PdfPageSizes.A4Height, portrait[3], 1);

            double[] landscape = MediaBoxOf(new HtmlToPdfOptions { PageSize = HtmlPageSize.A4, Landscape = true });
            Assert.Equal(PdfPageSizes.A4Height, landscape[2], 1);
            Assert.Equal(PdfPageSizes.A4Width, landscape[3], 1);
        }

        [Fact]
        public void A3_IsTheA3MediaBox()
        {
            double[] box = MediaBoxOf(new HtmlToPdfOptions { PageSize = HtmlPageSize.A3 });
            Assert.Equal(PdfPageSizes.A3Width, box[2], 1);
            Assert.Equal(PdfPageSizes.A3Height, box[3], 1);
        }

        [Fact]
        public void ACustomSize_SetAfterAPreset_Wins_AndReadsAsNull()
        {
            var options = new HtmlToPdfOptions { PageSize = HtmlPageSize.A4, PageWidth = "100mm", PageHeight = "150mm" };

            Assert.Null(options.PageSize);
            double[] box = MediaBoxOf(options);
            Assert.Equal(100 * 72 / 25.4, box[2], 1);
            Assert.Equal(150 * 72 / 25.4, box[3], 1);
        }

        [Fact]
        public void APresetSetAfterACustomSize_Wins()
        {
            var options = new HtmlToPdfOptions { PageWidth = "100mm", PageHeight = "150mm", PageSize = HtmlPageSize.Legal };

            Assert.Equal("8.5in", options.PageWidth);
            Assert.Equal("14in", options.PageHeight);
        }

        [Fact]
        public void SettingNull_LeavesTheSizeAlone()
        {
            var options = new HtmlToPdfOptions { PageWidth = "100mm", PageHeight = "150mm" };
            options.PageSize = null;

            Assert.Equal("100mm", options.PageWidth);
            Assert.Equal("150mm", options.PageHeight);
        }

        [Fact]
        public void AValueOutsideTheEnum_IsRefused_AndTheSizeIsKept()
        {
            var options = new HtmlToPdfOptions();
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.PageSize = (HtmlPageSize)999);
            Assert.Equal("value", ex.ParamName);
            Assert.Equal(HtmlPageSize.Letter, options.PageSize);
        }
    }
}
