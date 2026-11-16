// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The cascade's specificity (batch 91) is Selectors Level 4 §17's triple, not a count of
    /// characters: pinned first against the specification's own example table, then on the
    /// selectors the character count got wrong, and last through the rendered colour a competing
    /// pair of rules produces — the only place a wrong weight shows.
    /// </summary>
    public class CssSpecificityTests
    {
        private static int S(int a, int b, int c) => a * 1_000_000 + b * 1000 + c;

        /// <summary>Selectors Level 4 §17's example table, with the additions the specification lists under it.</summary>
        [Theory]
        [InlineData("*", 0, 0, 0)]
        [InlineData("LI", 0, 0, 1)]
        [InlineData("UL LI", 0, 0, 2)]
        [InlineData("UL OL+LI", 0, 0, 3)]
        [InlineData("H1 + *[REL=up]", 0, 1, 1)]
        [InlineData("UL OL LI.red", 0, 1, 3)]
        [InlineData("LI.red.level", 0, 2, 1)]
        [InlineData("#x34y", 1, 0, 0)]
        [InlineData("#s12:not(FOO)", 1, 0, 1)]
        [InlineData(".foo :is(.bar, #baz)", 1, 1, 0)]
        [InlineData(":where(.a.b, #c) p", 0, 0, 1)]
        [InlineData("li:nth-child(2n+1 of .important, #x)", 1, 1, 1)]
        [InlineData("p::first-line", 0, 0, 2)]
        [InlineData("p:first-line", 0, 0, 2)]
        [InlineData("a:hover::before", 0, 1, 2)]
        [InlineData("svg|rect", 0, 0, 1)]
        [InlineData("div > p ~ span", 0, 0, 3)]
        public void TheSpecificationsExamplesWeighAsTheTableSays(string selector, int a, int b, int c)
        {
            Assert.Equal(S(a, b, c), HtmlToBuilderMapper.Specificity(selector));
        }

        /// <summary>
        /// What the character count got wrong: a dot, hash or colon inside an attribute value or a
        /// functional argument is not a selector; <c>:not()</c> counts its argument, not itself; a
        /// value token is not a type; <c>:where()</c> counts nothing; a class count never outweighs one id.
        /// </summary>
        [Theory]
        [InlineData("a[href=\"http://x.y/#z\"]", 0, 1, 1)]
        [InlineData("input[type=text]", 0, 1, 1)]
        [InlineData("p:not(.x)", 0, 1, 1)]
        [InlineData("p:not(#x)", 1, 0, 1)]
        [InlineData("li:nth-child(odd)", 0, 1, 1)]
        [InlineData("p:where(.a.b.c)", 0, 0, 1)]
        [InlineData("p:has(> img.big)", 0, 1, 2)]
        public void WhatTheCharacterCountGotWrong(string selector, int a, int b, int c)
        {
            Assert.Equal(S(a, b, c), HtmlToBuilderMapper.Specificity(selector));
        }

        /// <summary>
        /// <c>:is()</c>/<c>:not()</c>/<c>:has()</c> weigh their <b>most specific</b> argument, not
        /// their last one — every fixture above happens to have the two agree, because the more
        /// specific alternative was always written last.
        /// </summary>
        [Theory]
        [InlineData(":is(#a, .b)", 1, 0, 0)]
        [InlineData(":not(.b, #a)", 1, 0, 0)]
        public void MostSpecificPicksTheHighestArgumentNotTheLastOne(string selector, int a, int b, int c)
        {
            Assert.Equal(S(a, b, c), HtmlToBuilderMapper.Specificity(selector));
        }

        /// <summary>
        /// A bracket or parenthesis inside a quoted value does not end the attribute selector or
        /// functional argument early — <c>SkipBalanced</c>'s own quote tracking, shared by
        /// <c>[...]</c> and <c>(...)</c> alike.
        /// </summary>
        [Theory]
        [InlineData("a[href=\"a]b\"]", 0, 1, 1)]
        [InlineData("p:lang(\"a)b\")", 0, 1, 1)]
        public void AQuotedBracketInsideAnAttributeOrFunctionalArgumentDoesNotEndItEarly(
            string selector, int a, int b, int c)
        {
            Assert.Equal(S(a, b, c), HtmlToBuilderMapper.Specificity(selector));
        }

        /// <summary>
        /// The selector list after <c>of</c> is read when it is written across lines — a pattern
        /// ending <c>.+$</c> could not cross the break and weighed the pseudo-class alone.
        /// </summary>
        [Fact]
        public void AnOfListWrittenAcrossLinesIsWeighed()
        {
            Assert.Equal(S(1, 1, 1), HtmlToBuilderMapper.Specificity("li:nth-child(2n+1 of .important,\n#x)"));
        }

        /// <summary><c>of</c> is the clause only as a word with white space after it — not inside one, not run into the list.</summary>
        [Theory]
        [InlineData("li:nth-child(2 proof .x)")]
        [InlineData("li:nth-child(2 of.x)")]
        public void AnOfThatIsNotAWordIsNotTheClause(string selector)
        {
            Assert.Equal(S(0, 1, 1), HtmlToBuilderMapper.Specificity(selector));
            Assert.Equal(S(0, 2, 1), HtmlToBuilderMapper.Specificity("li:nth-child(2 of .x)"));   // control: the clause weighs
        }

        /// <summary>
        /// <b>Adversarial: sixty thousand <c>of</c> before a line break.</b> The pattern tried each
        /// <c>of</c> and scanned on to the break from it — quadratic, minutes at this size; the scan
        /// takes the first and weighs the list after it: the attribute selector.
        /// </summary>
        [Fact]
        public void AdversarialOfWordsBeforeALineBreakAreReadOnce()
        {
            string junk = string.Concat(System.Linq.Enumerable.Repeat(" of", 60_000));
            string selector = "li:nth-child(2 of [title=\"" + junk + "\nx\"])";

            Assert.Equal(S(0, 2, 1), HtmlToBuilderMapper.Specificity(selector));
        }

        /// <summary>Twelve classes stay under one id: the packing keeps the triple's order, which a base-100 weight did not.</summary>
        [Fact]
        public void AnIdOutweighsAnyNumberOfClasses()
        {
            string twelve = ".a.b.c.d.e.f.g.h.i.j.k.l";
            Assert.True(HtmlToBuilderMapper.Specificity("#x") > HtmlToBuilderMapper.Specificity(twelve));
            Assert.True(HtmlToBuilderMapper.Specificity(twelve) > HtmlToBuilderMapper.Specificity("div p span"));
        }

        /// <summary>
        /// Through the cascade: two rules of equal specificity resolve by source order, so the later
        /// one wins where the character count had made the first heavier. Each pair's first rule
        /// is the one the count over-weighed; the later, correctly equal or heavier, must win.
        /// </summary>
        [Theory]
        [InlineData("p:not(.x){color:#ff0000} p.y{color:#0000ff}", "<p class='y'>X</p>")]
        [InlineData("p[data-kind=text]{color:#ff0000} p.a{color:#0000ff}", "<p data-kind='text' class='a'>X</p>")]
        [InlineData("p[title='a.b:c']{color:#ff0000} p.c{color:#0000ff}", "<p class='c' title='a.b:c'>X</p>")]
        [InlineData("p:where(.a.b){color:#ff0000} p.c{color:#0000ff}", "<p class='a b c'>X</p>")]
        public void TheLaterRuleOfEqualSpecificityWins_WhereTheCountOverweighedTheFirst(string css, string html)
        {
            string dec = DecodeStreams(ToPdf("<style>" + css + "</style>" + html));

            Assert.Contains("0 0 1 rg", dec, StringComparison.Ordinal);
            Assert.DoesNotContain("1 0 0 rg", dec, StringComparison.Ordinal);
        }
    }
}
