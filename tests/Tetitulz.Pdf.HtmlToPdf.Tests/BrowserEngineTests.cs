// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    public class BrowserEngineTests
    {
        [Theory]
        [InlineData(BrowserEngine.Chromium)]
        [InlineData(BrowserEngine.Firefox)]
        [InlineData(BrowserEngine.WebKit)]
        public void BrowserEngine_AllValuesAreDefined(BrowserEngine engine)
        {
            Assert.True(Enum.IsDefined(typeof(BrowserEngine), engine));
        }

        [Fact]
        public void BrowserEngine_DefaultIsChromium()
        {
            Assert.Equal(0, (int)BrowserEngine.Chromium);
        }

        [Fact]
        public void BrowserEngine_HasThreeValues()
        {
            var values = Enum.GetValues(typeof(BrowserEngine));
            Assert.Equal(3, values.Length);
        }
    }
}
