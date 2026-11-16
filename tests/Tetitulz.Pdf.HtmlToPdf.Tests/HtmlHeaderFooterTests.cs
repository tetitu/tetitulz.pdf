// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Header and footer templates in the managed engine.
    ///
    /// <para><c>HeaderTemplate</c>/<c>FooterTemplate</c>/<c>DisplayHeaderFooter</c> are documented
    /// options that were Playwright-only and silently did nothing here — even though
    /// <c>Section.Header</c>/<c>Footer</c> exist in the model and the exporter renders them. The
    /// substitution classes Playwright defines are resolved, so a template written for the browser
    /// path means the same thing in both engines.</para>
    /// </summary>
    public class HtmlHeaderFooterTests
    {
        private static HtmlToPdfOptions WithRunning(string header = null, string footer = null,
            bool display = true)
        {
            var o = Managed();
            o.DisplayHeaderFooter = display;
            o.HeaderTemplate = header;
            o.FooterTemplate = footer;
            return o;
        }

        private static string Body(int paragraphs = 1)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < paragraphs; i++) sb.Append($"<p>Paragraph {i}</p>");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ the option now works

        [Fact]
        public void HeaderTemplate_IsRendered()
        {
            string dec = DecodeStreams(ToPdf(Body(), WithRunning(header: "<div>RUNNINGHEAD</div>")));
            Assert.Contains("RUNNINGHEAD", dec);
        }

        [Fact]
        public void FooterTemplate_IsRendered()
        {
            string dec = DecodeStreams(ToPdf(Body(), WithRunning(footer: "<div>RUNNINGFOOT</div>")));
            Assert.Contains("RUNNINGFOOT", dec);
        }

        [Fact]
        public void DisplayHeaderFooterFalse_SuppressesBoth()
        {
            string dec = DecodeStreams(ToPdf(Body(),
                WithRunning("<div>RUNNINGHEAD</div>", "<div>RUNNINGFOOT</div>", display: false)));

            Assert.DoesNotContain("RUNNINGHEAD", dec);
            Assert.DoesNotContain("RUNNINGFOOT", dec);
        }

        [Fact]
        public void HeaderIsAboveTheBody_AndFooterBelowIt()
        {
            string dec = DecodeStreams(ToPdf(Body(),
                WithRunning("<div>RUNNINGHEAD</div>", "<div>RUNNINGFOOT</div>")));

            double head = YOf(dec, "RUNNINGHEAD");
            double body = YOf(dec, "Paragraph");
            double foot = YOf(dec, "RUNNINGFOOT");

            Assert.False(double.IsNaN(head) || double.IsNaN(foot), "both running blocks must draw");
            // Larger y is higher up the page.
            Assert.True(head > body, $"header should sit above the body ({head} vs {body})");
            Assert.True(foot < body, $"footer should sit below the body ({foot} vs {body})");
        }

        // ------------------------------------------------------------------ substitutions

        /// <summary>
        /// <c>pageNumber</c> becomes the exporter's placeholder, resolved per page at draw time —
        /// so a two-page document shows 1 then 2, not the same number twice.
        /// </summary>
        [Fact]
        public void PageNumber_DiffersPerPage()
        {
            byte[] pdf = ToPdf(Body(150),
                WithRunning(footer: "<div>Page <span class=\"pageNumber\"></span></div>"));

            Assert.True(CountPages(pdf) >= 2, "fixture must paginate");

            string dec = DecodeStreams(pdf);
            var footers = DrawnTexts(dec).Where(d => d.Text == "1" || d.Text == "2").ToList();
            Assert.Contains(footers, d => d.Text == "1");
            Assert.Contains(footers, d => d.Text == "2");
        }

        /// <summary>
        /// <c>totalPages</c> cannot be known until pagination finishes, so it is emitted as a
        /// placeholder and back-filled. Every page must show the same, final count.
        /// </summary>
        [Fact]
        public void TotalPages_IsTheFinalCount_OnEveryPage()
        {
            byte[] pdf = ToPdf(Body(150),
                WithRunning(footer: "<div>of <span class=\"totalPages\"></span></div>"));

            int pages = CountPages(pdf);
            Assert.True(pages >= 2, "fixture must paginate");

            string dec = DecodeStreams(pdf);
            int shown = DrawnTexts(dec).Count(d => d.Text == pages.ToString());
            Assert.True(shown >= pages, $"expected the count {pages} on each of {pages} pages, saw {shown}");

            // The placeholder token itself must never survive into the output.
            Assert.DoesNotContain("{{NUMPAGES}}", dec);
            Assert.DoesNotContain("{{PAGE}}", dec);
        }

        [Fact]
        public void Title_ComesFromTheDocument()
        {
            string dec = DecodeStreams(ToPdf(
                "<html><head><title>MYTITLE</title></head><body><p>Body</p></body></html>",
                WithRunning(header: "<div><span class=\"title\"></span></div>")));

            Assert.Contains("MYTITLE", dec);
        }

        /// <summary>
        /// The date is caller-supplied, not <c>DateTime.Now</c>: a built-in clock would make the
        /// same input produce different bytes on every run, breaking reproducible output and any
        /// comparison test a caller writes.
        /// </summary>
        [Fact]
        public void Date_IsSubstitutedFromTheOption()
        {
            var o = WithRunning(header: "<div><span class=\"date\"></span></div>");
            o.RunningDate = "2026-08-10";

            Assert.Contains("2026-08-10", DecodeStreams(ToPdf(Body(), o)));
        }

        [Fact]
        public void Date_WithNoOption_SubstitutesNothing()
        {
            string dec = DecodeStreams(ToPdf(Body(),
                WithRunning(header: "<div>[<span class=\"date\"></span>]</div>")));

            // The brackets prove the span was reached and emptied rather than skipped.
            Assert.Contains("[", dec);
            Assert.Contains("]", dec);
        }

        // ------------------------------------------------------------------ formatting

        [Fact]
        public void TemplateMarkup_IsStyled_NotEmittedLiterally()
        {
            string dec = DecodeStreams(ToPdf(Body(),
                WithRunning(header: "<div style=\"color:#ff0000\">REDHEAD</div>")));

            Assert.Contains("REDHEAD", dec);
            Assert.Contains("1 0 0 rg", dec);
            Assert.DoesNotContain("<div", dec);
        }

        [Fact]
        public void EmptyTemplate_AddsNoRunningBlock()
        {
            string withEmpty = DecodeStreams(ToPdf(Body(), WithRunning(header: "   ")));
            string without = DecodeStreams(ToPdf(Body(), WithRunning()));

            Assert.Equal(without, withEmpty);
        }

        // ------------------------------------------------------------------ tagging

        [Fact]
        public void RunningBlocks_AreArtifacts_NotDocumentContent()
        {
            // A running head is page furniture. Left as untagged ink inside a tagged page it would
            // itself be a conformance violation; tagged as a paragraph it would pollute the
            // reading order with a repeat of the same text on every page.
            var o = WithRunning(header: "<div>RUNNINGHEAD</div>");
            o.Tagged = true;
            string dec = DecodeStreams(ToPdf(Body(), o));

            int headAt = dec.IndexOf("RUNNINGHEAD", StringComparison.Ordinal);
            Assert.True(headAt >= 0, "the header must be drawn");

            // The nearest BDC before the header text must be an /Artifact one.
            int bdcAt = dec.LastIndexOf("BDC", headAt, StringComparison.Ordinal);
            Assert.True(bdcAt >= 0, "the header ink must be inside a marked-content sequence");
            string opener = dec.Substring(Math.Max(0, bdcAt - 80), bdcAt - Math.Max(0, bdcAt - 80));
            Assert.Contains("/Artifact", opener);
        }
    }
}
