// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using F = Tetitulz.Pdf.TestHelpers.RcltFont;
using S = Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Contextual alternates (<c>GSUB</c> <c>calt</c>) are drawn as a browser draws them — on unless
    /// <c>font-variant-ligatures</c> says <c>none</c> or <c>no-contextual</c>, inherited. With
    /// <see cref="F"/>'s <c>calt</c> font, beh then yeh is <c>&lt;00020003&gt; Tj</c> with the
    /// alternate, <c>&lt;00020001&gt; Tj</c> without.
    /// </summary>
    public class HtmlContextualAlternatesTests
    {
        private const string Alternate = "<00020003> Tj";
        private const string Plain = "<00020001> Tj";

        [Fact]
        public void ContextualAlternatesAreOnByDefault()
        {
            string content = Content("<p>بي</p>");
            Assert.Contains(Alternate, content);
            Assert.DoesNotContain(Plain, content);
        }

        /// <summary><c>none</c> and <c>no-contextual</c> turn them off; another ligature value leaves them on.</summary>
        [Theory]
        [InlineData("none", Plain)]
        [InlineData("no-contextual", Plain)]
        [InlineData("no-common-ligatures no-contextual", Plain)]
        [InlineData("no-common-ligatures", Alternate)]
        [InlineData("contextual", Alternate)]
        [InlineData("normal", Alternate)]
        public void FontVariantLigatures_Decides(string value, string expected)
        {
            string content = Content($"<p style=\"font-variant-ligatures:{value}\">بي</p>");
            Assert.Contains(expected, content);
            Assert.DoesNotContain(expected == Plain ? Alternate : Plain, content);
        }

        /// <summary>The setting is inherited, and a descendant's <c>normal</c> turns them back on.</summary>
        [Theory]
        [InlineData("none", Plain)]
        [InlineData("normal", Alternate)]
        public void FontVariantLigatures_IsInherited_AndOverridden(string inner, string expected)
        {
            string content = Content($"<div style=\"font-variant-ligatures:none\"><p style=\"font-variant-ligatures:{inner}\">بي</p></div>");
            Assert.Contains(expected, content);
            Assert.DoesNotContain(expected == Plain ? Alternate : Plain, content);
        }

        private static string Content(string body)
        {
            var o = S.Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => F.Build(feature: "calt");
            return S.DecodeStreams(S.ToPdf(
                $"<html><body style=\"font-family:RcltFixture; font-size:20pt\">{body}</body></html>", o));
        }
    }
}
