// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using F = Tetitulz.Pdf.TestHelpers.LigatureFont;
using S = Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The common ligatures (<c>GSUB</c> <c>liga</c>) are formed as a browser forms them — on unless
    /// <c>font-variant-ligatures</c> says <c>none</c> or <c>no-common-ligatures</c>, inherited. With
    /// <see cref="F"/>, f then i is <c>&lt;0004&gt; Tj</c> as the fi ligature and
    /// <c>&lt;00010002&gt; Tj</c> as the letters; the ligature is written back in <c>/ToUnicode</c>
    /// as its letters.
    /// </summary>
    public class HtmlLigaturesTests
    {
        private const string Ligature = "<0004> Tj";
        private const string Plain = "<00010002> Tj";

        [Fact]
        public void LigaturesAreOnByDefault()
        {
            string content = Content("<p>fi</p>");
            Assert.Contains(Ligature, content);
            Assert.DoesNotContain(Plain, content);
            Assert.Contains("<0004> <00660069>", content);
        }

        /// <summary><c>none</c> and <c>no-common-ligatures</c> turn them off; another value leaves them on.</summary>
        [Theory]
        [InlineData("none", Plain)]
        [InlineData("no-common-ligatures", Plain)]
        [InlineData("no-common-ligatures no-contextual", Plain)]
        [InlineData("no-contextual", Ligature)]
        [InlineData("common-ligatures", Ligature)]
        [InlineData("normal", Ligature)]
        public void FontVariantLigatures_Decides(string value, string expected)
        {
            string content = Content($"<p style=\"font-variant-ligatures:{value}\">fi</p>");
            Assert.Contains(expected, content);
            Assert.DoesNotContain(expected == Plain ? Ligature : Plain, content);
        }

        /// <summary>The setting is inherited, and a descendant's <c>normal</c> turns them back on.</summary>
        [Theory]
        [InlineData("none", Plain)]
        [InlineData("normal", Ligature)]
        public void FontVariantLigatures_IsInherited_AndOverridden(string inner, string expected)
        {
            string content = Content($"<div style=\"font-variant-ligatures:none\"><p style=\"font-variant-ligatures:{inner}\">fi</p></div>");
            Assert.Contains(expected, content);
            Assert.DoesNotContain(expected == Plain ? Ligature : Plain, content);
        }

        /// <summary>
        /// The optional kinds are off unless named (CSS Fonts 4 §6.4): <c>discretionary-ligatures</c>
        /// forms the font's <c>dlig</c>, <c>historical-ligatures</c> its <c>hlig</c>; <c>normal</c> forms
        /// neither, and naming one does not form the other.
        /// </summary>
        [Theory]
        [InlineData("dlig", "discretionary-ligatures", Ligature)]
        [InlineData("dlig", "normal", Plain)]
        [InlineData("dlig", "historical-ligatures", Plain)]
        [InlineData("hlig", "historical-ligatures", Ligature)]
        [InlineData("hlig", "common-ligatures discretionary-ligatures", Plain)]
        [InlineData("hlig", "normal", Plain)]
        public void TheOptionalKinds_AreFormedOnlyWhereNamed(string feature, string value, string expected)
        {
            string content = Content($"<p style=\"font-variant-ligatures:{value}\">fi</p>", feature);
            Assert.Contains(expected, content);
            Assert.DoesNotContain(expected == Plain ? Ligature : Plain, content);
        }

        private static string Content(string body, string feature = "liga")
        {
            var o = S.Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => F.Build(feature: feature);
            return S.DecodeStreams(S.ToPdf(
                $"<html><body style=\"font-family:LigatureFixture; font-size:20pt\">{body}</body></html>", o));
        }
    }
}
