// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// CSS <c>hyphens</c> (Text 3 §5.1): <c>auto</c> hyphenates where the language has a resource —
    /// the library's is English, so an English <c>lang</c> — inherited, <c>manual</c> by default and
    /// on request; <c>-webkit-hyphens</c> alike. Read from the drawing: a head ending in a hyphen.
    /// </summary>
    public class HyphensMappingTests
    {
        private static string Filler(int n) => string.Join(" ", Enumerable.Repeat("word", n)) + " hyphenation";

        private static bool Hyphenated(string html, string word = "hyphenation") =>
            Hyphenated(html, Managed(), word);

        private static bool Hyphenated(string html, HtmlToPdfOptions options, string word = "hyphenation") =>
            DrawnTexts(DecodeStreams(ToPdf(html, options))).Any(t => t.Text.EndsWith("-") && word.StartsWith(t.Text.TrimEnd('-')));

        /// <summary>The filler count at which <paramref name="wrap"/> around the words hyphenates.</summary>
        private static int CountThatHyphenates(System.Func<string, string> wrap)
        {
            for (int n = 1; n < 250; n++)
                if (Hyphenated(wrap(Filler(n)))) return n;
            throw new System.InvalidOperationException("never hyphenated");
        }

        [Fact]
        public void HyphensAutoUnderAnEnglishLang_Hyphenates_AnotherLangOrNoneDoesNot()
        {
            int n = CountThatHyphenates(w => $"<p lang='en' style='hyphens:auto'>{w}</p>");
            string words = Filler(n);

            Assert.True(Hyphenated($"<p lang='en-GB' style='hyphens:auto'>{words}</p>"));
            Assert.True(Hyphenated($"<html lang='en-US'><body><p style='hyphens:auto'>{words}</p></body></html>"));
            Assert.False(Hyphenated($"<p lang='en' style='hyphens:manual'>{words}</p>"));
            Assert.False(Hyphenated($"<p lang='en'>{words}</p>"));                        // manual by default
            Assert.False(Hyphenated($"<p lang='de' style='hyphens:auto'>{words}</p>"));    // no German resource
            Assert.False(Hyphenated($"<p lang='eng' style='hyphens:auto'>{words}</p>"));   // not English's subtag
            Assert.False(Hyphenated($"<p style='hyphens:auto'>{words}</p>"));              // no language at all
        }

        /// <summary>The options' default language, when the markup declares none, is the language in force.</summary>
        [Fact]
        public void TheOptionsDefaultLanguage_IsTheLanguageInForce()
        {
            int n = CountThatHyphenates(w => $"<p lang='en' style='hyphens:auto'>{w}</p>");
            string html = $"<p style='hyphens:auto'>{Filler(n)}</p>";
            var english = Managed();
            english.DefaultLanguage = "en";
            var german = Managed();
            german.DefaultLanguage = "de";
            Assert.True(Hyphenated(html, english));
            Assert.False(Hyphenated(html, german));
        }

        [Fact]
        public void HyphensIsInherited_AndAChildTurnsItOff()
        {
            int n = CountThatHyphenates(w => $"<div lang='en' style='-webkit-hyphens:auto'><p>{w}</p></div>");
            string words = Filler(n);
            Assert.False(Hyphenated($"<div lang='en' style='-webkit-hyphens:auto'><p style='hyphens:none'>{words}</p></div>"));
            Assert.False(Hyphenated($"<div lang='en' style='hyphens:auto'><p lang='he'>{words}</p></div>"));
            Assert.False(Hyphenated($"<div lang='en'><p>{words}</p></div>"));
        }

        /// <summary>A float lifted out of a hyphenating paragraph hyphenates too: its paragraph takes the host's setting.</summary>
        [Fact]
        public void AFloatLiftedFromAHyphenatingParagraph_Hyphenates()
        {
            const string Float = "<span style='float:right;width:40pt'>a characteristic</span>";
            Assert.True(Hyphenated($"<p lang='en' style='hyphens:auto'>Start {Float} rest</p>", "characteristic"));
            Assert.False(Hyphenated($"<p lang='en'>Start {Float} rest</p>", "characteristic"));
        }

        /// <summary>
        /// The lines after a break that a float follows become a paragraph of their own, which keeps the
        /// host's setting.
        /// </summary>
        [Fact]
        public void TheLinesAfterABreakBeforeAFloat_KeepTheSetting()
        {
            static string Html(string style, string words) =>
                $"<p lang='en' style='{style}'>Top<br>{words} <span style='float:right'>F</span></p>";
            int n = CountThatHyphenates(w => Html("hyphens:auto", w));
            Assert.False(Hyphenated(Html("", Filler(n))));
        }

        [Fact]
        public void ATableCellHyphenates_UnderTheSameRule()
        {
            int n = CountThatHyphenates(w => $"<table style='width:100%'><tr><td lang='en' style='hyphens:auto'>{w}</td></tr></table>");
            Assert.False(Hyphenated($"<table style='width:100%'><tr><td lang='en'>{Filler(n)}</td></tr></table>"));
        }
    }
}
