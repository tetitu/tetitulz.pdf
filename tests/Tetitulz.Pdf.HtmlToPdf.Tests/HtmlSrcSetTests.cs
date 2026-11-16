// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>srcset</c> and <c>&lt;picture&gt;</c>.
    ///
    /// <para>Candidates are distinguished by their <b>pixel dimensions</b>, which travel into the
    /// PDF as <c>/Width</c> — so a test can name exactly which file was chosen. Asserting merely
    /// that "an image was embedded" would pass whichever candidate won, and choosing between them
    /// is the entire feature.</para>
    /// </summary>
    public class HtmlSrcSetTests
    {
        private static (byte[] pdf, List<BuilderDiagnostic> diagnostics) Convert(string html, string basePath)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.BasePath = basePath;
            o.OnDiagnostic = seen.Add;
            return (ToPdf(html, o), seen);
        }

        [Fact]
        public void SrcSet_PicksTheHighestResolutionCandidate()
        {
            using var dir = NewScratchDir();
            dir.Write("small.png", MakeRgbPng(2, 2));
            dir.Write("large.png", MakeRgbPng(9, 9));

            var (pdf, _) = Convert(
                "<img src=\"small.png\" srcset=\"small.png 100w, large.png 900w\" width=\"20\">", dir.Path);

            // Print has no viewport, so the largest candidate is the right one.
            Assert.Contains("/Width 9", Raw(pdf));
            Assert.DoesNotContain("/Width 2", Raw(pdf));
        }

        [Fact]
        public void SrcSet_HonoursDensityDescriptors()
        {
            using var dir = NewScratchDir();
            dir.Write("one.png", MakeRgbPng(3, 3));
            dir.Write("two.png", MakeRgbPng(7, 7));

            var (pdf, _) = Convert(
                "<img src=\"one.png\" srcset=\"one.png 1x, two.png 2x\" width=\"20\">", dir.Path);

            Assert.Contains("/Width 7", Raw(pdf));
        }

        [Fact]
        public void SrcSet_WithNoDescriptors_TreatsEntriesAsOneX()
        {
            using var dir = NewScratchDir();
            dir.Write("only.png", MakeRgbPng(5, 5));

            var (pdf, _) = Convert("<img src=\"missing.png\" srcset=\"only.png\" width=\"20\">", dir.Path);

            Assert.Contains("/Width 5", Raw(pdf));
        }

        [Fact]
        public void NoSrcSet_StillUsesSrc()
        {
            // The control: srcset support must not disturb an ordinary <img>.
            using var dir = NewScratchDir();
            dir.Write("plain.png", MakeRgbPng(6, 6));

            var (pdf, _) = Convert("<img src=\"plain.png\" width=\"20\">", dir.Path);

            Assert.Contains("/Width 6", Raw(pdf));
        }

        // ------------------------------------------------------------------ <picture>

        [Fact]
        public void Picture_UsesTheFirstApplicableSource()
        {
            using var dir = NewScratchDir();
            dir.Write("chosen.png", MakeRgbPng(4, 4));
            dir.Write("fallback.png", MakeRgbPng(8, 8));

            var (pdf, _) = Convert(
                "<picture><source srcset=\"chosen.png\">" +
                "<img src=\"fallback.png\" width=\"20\"></picture>", dir.Path);

            Assert.Contains("/Width 4", Raw(pdf));
        }

        /// <summary>
        /// A <c>&lt;source media="screen"&gt;</c> is skipped for the same reason a screen-only
        /// <c>@media</c> block is: the output is print. The evaluation is shared with the cascade,
        /// so the two cannot disagree about what "print" means.
        /// </summary>
        [Fact]
        public void Picture_SkipsAScreenOnlySource()
        {
            using var dir = NewScratchDir();
            dir.Write("screen.png", MakeRgbPng(4, 4));
            dir.Write("print.png", MakeRgbPng(8, 8));

            var (pdf, _) = Convert(
                "<picture><source media=\"screen\" srcset=\"screen.png\">" +
                "<source media=\"print\" srcset=\"print.png\">" +
                "<img src=\"screen.png\" width=\"20\"></picture>", dir.Path);

            Assert.Contains("/Width 8", Raw(pdf));
            Assert.DoesNotContain("/Width 4", Raw(pdf));
        }

        /// <summary>
        /// The common modern pattern: a WebP first with a PNG fallback. Declining the format here
        /// — rather than selecting it and failing to decode later — is what makes the fallback
        /// reachable at all.
        /// </summary>
        [Fact]
        public void Picture_SkipsASourceWhoseTypeCannotBeDecoded()
        {
            using var dir = NewScratchDir();
            dir.Write("modern.webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 });
            dir.Write("safe.png", MakeRgbPng(7, 7));

            var (pdf, diagnostics) = Convert(
                "<picture><source type=\"image/webp\" srcset=\"modern.webp\">" +
                "<source type=\"image/png\" srcset=\"safe.png\">" +
                "<img src=\"safe.png\" width=\"20\"></picture>", dir.Path);

            Assert.Contains("/Width 7", Raw(pdf));
            Assert.DoesNotContain(diagnostics, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed);
        }

        [Fact]
        public void Picture_FallsBackToTheImgWhenNoSourceApplies()
        {
            using var dir = NewScratchDir();
            dir.Write("fallback.png", MakeRgbPng(5, 5));

            var (pdf, _) = Convert(
                "<picture><source media=\"screen\" srcset=\"nothing.png\">" +
                "<img src=\"fallback.png\" width=\"20\"></picture>", dir.Path);

            Assert.Contains("/Width 5", Raw(pdf));
        }

        [Fact]
        public void SrcSet_WithOnlyUnresolvableCandidates_ReportsRatherThanDrawingAHole()
        {
            using var dir = NewScratchDir();

            var (pdf, _) = Convert(
                "<p>ABOVE</p><img srcset=\"gone.png 100w\" width=\"40\" height=\"40\"><p>BELOW</p>", dir.Path);

            Assert.DoesNotContain("/Subtype /Image", Raw(pdf));
        }
    }
}
