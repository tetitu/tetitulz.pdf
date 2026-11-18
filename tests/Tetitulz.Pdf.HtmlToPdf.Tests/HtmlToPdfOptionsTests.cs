// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    public class HtmlToPdfOptionsTests
    {
        [Fact]
        public void Default_HasExpectedValues()
        {
            var options = HtmlToPdfOptions.Default;

            Assert.Equal(BrowserEngine.Chromium, options.Engine);
            Assert.Equal("8.5in", options.PageWidth);
            Assert.Equal("11in", options.PageHeight);
            Assert.Equal("0.4in", options.MarginTop);
            Assert.Equal("0.4in", options.MarginBottom);
            Assert.Equal("0.4in", options.MarginLeft);
            Assert.Equal("0.4in", options.MarginRight);
            Assert.False(options.Landscape);
            Assert.True(options.PrintBackground);
            Assert.Equal(1.0, options.Scale);
            Assert.Null(options.HeaderTemplate);
            Assert.Null(options.FooterTemplate);
            Assert.False(options.DisplayHeaderFooter);
            Assert.Equal(30000, options.NavigationTimeoutMs);
            Assert.True(options.WaitForNetworkIdle);
        }

        [Fact]
        public void Properties_CanBeModified()
        {
            var options = new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Browser,
                PageWidth = "210mm",
                PageHeight = "297mm",
                MarginTop = "1in",
                MarginBottom = "1in",
                MarginLeft = "1in",
                MarginRight = "1in",
                Landscape = true,
                PrintBackground = false,
                Scale = 0.8,
                HeaderTemplate = "<div>Header</div>",
                FooterTemplate = "<div>Footer</div>",
                DisplayHeaderFooter = true,
                NavigationTimeoutMs = 60000,
                WaitForNetworkIdle = false
            };

            Assert.Equal(HtmlRenderEngine.Browser, options.RenderEngine);
            Assert.Equal("210mm", options.PageWidth);
            Assert.Equal("297mm", options.PageHeight);
            Assert.True(options.Landscape);
            Assert.False(options.PrintBackground);
            Assert.Equal(0.8, options.Scale);
            Assert.Equal("<div>Header</div>", options.HeaderTemplate);
            Assert.True(options.DisplayHeaderFooter);
            Assert.Equal(60000, options.NavigationTimeoutMs);
            Assert.False(options.WaitForNetworkIdle);
        }
    }
}
