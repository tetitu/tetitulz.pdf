// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using F = Tetitulz.Pdf.TestHelpers.KerningFont;
using S = Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// CSS <c>font-kerning</c>: text is kerned as a browser kerns it — <c>auto</c>, the initial value,
    /// kerns — and <c>none</c> turns it off, inherited. With <see cref="F"/>, "To" at 20 points kerned
    /// is <c>[&lt;0003&gt; 110 &lt;0004&gt;] TJ</c>, unkerned <c>&lt;00030004&gt; Tj</c>.
    /// </summary>
    public class HtmlFontKerningTests
    {
        private const string Kerned = "[<0003> 110 <0004>] TJ";
        private const string Plain = "<00030004> Tj";

        [Fact]
        public void TextIsKernedByDefault()
        {
            string content = Content("<p>To</p>");
            Assert.Contains(Kerned, content);
            Assert.DoesNotContain(Plain, content);
        }

        [Fact]
        public void FontKerningNone_TurnsItOff()
        {
            string content = Content("<p style=\"font-kerning:none\">To</p>");
            Assert.Contains(Plain, content);
            Assert.DoesNotContain(Kerned, content);
        }

        /// <summary><c>none</c> is inherited, and a descendant's <c>auto</c> or <c>normal</c> turns kerning back on.</summary>
        [Theory]
        [InlineData("none", Plain)]
        [InlineData("auto", Kerned)]
        [InlineData("normal", Kerned)]
        public void FontKerning_IsInherited_AndOverridden(string inner, string expected)
        {
            string content = Content($"<div style=\"font-kerning:none\"><p style=\"font-kerning:{inner}\">To</p></div>");
            Assert.Contains(expected, content);
            Assert.DoesNotContain(expected == Plain ? Kerned : Plain, content);
        }

        private static string Content(string body)
        {
            var o = S.Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => F.Build();
            return S.DecodeStreams(S.ToPdf(
                $"<html><body style=\"font-family:KerningFixture; font-size:20pt\">{body}</body></html>", o));
        }
    }
}
