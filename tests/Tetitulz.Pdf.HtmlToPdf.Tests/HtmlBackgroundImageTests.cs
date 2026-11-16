// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// CSS <c>background-image</c> in the managed engine: what it paints, what it refuses to
    /// fetch, and what <c>PrintBackground:false</c> does to it.
    ///
    /// <para><b>Why the two refusal tests are here and not in <c>SsrfGuardTests</c>.</b> That file
    /// guards <c>ConvertUrlToBytesAsync</c>'s scheme check, which runs before a browser is
    /// launched and has nothing to do with the managed engine. A background url is a fresh
    /// SSRF / traversal surface on the <i>managed</i> side, and it is refused by the same
    /// <c>ResolveLocalPath</c> boundary every other local read uses — so the test belongs beside
    /// the feature that introduced the surface.</para>
    /// </summary>
    public class HtmlBackgroundImageTests
    {
        /// <summary>An image XObject invocation, <c>/Name Do</c> — one per painted tile.</summary>
        private static readonly Regex XObjectInvocation = new(@"/\w+\s+Do\b", RegexOptions.Compiled);

        private static int TilesPainted(byte[] pdf) =>
            XObjectInvocation.Matches(DecodeStreams(pdf)).Count;

        private static string PngDataUri() =>
            "data:image/png;base64," + System.Convert.ToBase64String(MakeRgbPng(8, 8));

        private static (byte[] pdf, List<BuilderDiagnostic> diagnostics) Convert(
            string html, Action<HtmlToPdfOptions> configure = null)
        {
            var seen = new List<BuilderDiagnostic>();
            var o = Managed();
            o.OnDiagnostic = seen.Add;
            configure?.Invoke(o);
            return (ToPdf(html, o), seen);
        }

        // ------------------------------------------------------------------ painting

        /// <summary>
        /// The feature. Counting the invocations rather than searching the file for "/Image" is
        /// what makes this about the paint: an XObject can be registered in the page resources and
        /// never drawn.
        /// </summary>
        [Fact]
        public void BackgroundImage_FromADataUri_IsPainted()
        {
            var (pdf, _) = Convert($"<div style=\"background-image:url({PngDataUri()})\">content</div>");

            Assert.True(TilesPainted(pdf) > 0, "the background image was never invoked");
        }

        /// <summary>
        /// The control. Without it the assertion above is satisfied by any image the engine
        /// happens to draw, and passes with the whole feature deleted.
        /// </summary>
        [Fact]
        public void PlainDiv_PaintsNoBackgroundImage()
        {
            var (pdf, _) = Convert("<div>content</div>");

            Assert.Equal(0, TilesPainted(pdf));
        }

        /// <summary>
        /// The <c>background</c> shorthand carries a colour <b>and</b> an image, and the two are
        /// read by different code. Painting only one of them is the shape of defect this asserts
        /// against — the colour path already existed, so an image-only assertion would not notice
        /// the colour being lost.
        /// </summary>
        [Fact]
        public void BackgroundShorthand_PaintsBothTheColourAndTheImage()
        {
            var (pdf, _) = Convert(
                $"<div style=\"background:#eeeeee url({PngDataUri()}) no-repeat\">content</div>");

            string dec = DecodeStreams(pdf);
            Assert.True(PdfContentProbe.RectangleFillCount(dec) > 0, "the shorthand's colour was not painted");
            Assert.Single(XObjectInvocation.Matches(dec));      // no-repeat → exactly one tile
        }

        /// <summary>
        /// <c>repeat</c> is the CSS initial value, so an 8×8 image (6&#160;pt) behind a full-width
        /// block must tile. A single draw means the repeat arm never ran.
        /// </summary>
        [Fact]
        public void BackgroundImage_DefaultsToRepeat_AndTiles()
        {
            var (pdf, _) = Convert($"<div style=\"background-image:url({PngDataUri()})\">content</div>");

            Assert.True(TilesPainted(pdf) > 1, "a background with no background-repeat did not tile");
        }

        // ------------------------------------------------------------------ the trust boundary

        /// <summary>
        /// A remote background is never fetched. The managed engine has no network at all, and a
        /// background url would otherwise be a fresh SSRF surface reached from a stylesheet.
        /// </summary>
        [Fact]
        public void BackgroundImage_WithARemoteUrl_IsNeverFetched()
        {
            var (pdf, _) = Convert(
                "<div style=\"background-image:url(https://example.com/bg.png)\">content</div>");

            Assert.Equal(0, TilesPainted(pdf));
        }

        /// <summary>
        /// A read outside <c>BasePath</c> is refused by the same <c>ResolveLocalPath</c> boundary
        /// every other local read uses — no second path guard, which would be a second thing to
        /// get wrong.
        ///
        /// <para>The url is the secret file's <b>absolute</b> path, deliberately. Written as
        /// <c>url(../secret.png)</c> this test passed against a mutation that bypassed
        /// <c>ResolveLocalPath</c> entirely and read the raw url — because a relative traversal
        /// resolves against the process working directory and finds nothing there either. An
        /// absolute path is the input that only the boundary refuses.</para>
        /// </summary>
        [Fact]
        public void BackgroundImage_OutsideBasePath_IsRefused()
        {
            using var outer = NewScratchDir();
            string secret = outer.Write("secret.png", MakeRgbPng(8, 8));
            string inner = Path.Combine(outer.Path, "sub");
            Directory.CreateDirectory(inner);

            var (pdf, _) = Convert(
                $"<div style=\"background-image:url({secret.Replace('\\', '/')})\">content</div>",
                o => o.BasePath = inner);

            Assert.Equal(0, TilesPainted(pdf));
        }

        /// <summary>The same file <i>inside</i> the base path must paint — otherwise the two
        /// refusals above would be satisfied by the feature never working at all.</summary>
        [Fact]
        public void BackgroundImage_InsideBasePath_IsPainted()
        {
            using var dir = NewScratchDir();
            dir.Write("bg.png", MakeRgbPng(8, 8));

            var (pdf, _) = Convert("<div style=\"background-image:url(bg.png)\">content</div>",
                                   o => o.BasePath = dir.Path);

            Assert.True(TilesPainted(pdf) > 0, "a background image inside BasePath was not painted");
        }

        // ------------------------------------------------------------------ honest failure

        /// <summary>
        /// Bytes that are recognisable but not decodable are reported where the url is still
        /// known. The exporter sees only bytes, so its message could name a byte count and nothing
        /// else — the whole reason the decision is made in the mapper.
        /// </summary>
        [Fact]
        public void BackgroundImage_WithUndecodableBytes_IsReported()
        {
            string tiff = "data:image/tiff;base64," +
                          System.Convert.ToBase64String(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0, 0, 0, 0, 0 });

            var (pdf, diagnostics) = Convert($"<div style=\"background-image:url({tiff})\">content</div>");

            Assert.Equal(0, TilesPainted(pdf));
            Assert.Contains(diagnostics, d => d.Kind == BuilderDiagnosticKind.ImageDecodeFailed
                                           && d.Detail.Contains("background-image", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ PrintBackground

        /// <summary>
        /// A background image is a background: Chromium drops it under
        /// <c>printBackground:false</c> exactly as it drops the colour, and so does this. The
        /// model scrub is the single place that decides, which is why it catches a background
        /// source added long after it was written.
        /// </summary>
        [Fact]
        public void PrintBackgroundFalse_ScrubsTheBackgroundImage()
        {
            string markup = $"<div style=\"background-image:url({PngDataUri()})\">content</div>";

            var (on, _) = Convert(markup, o => o.PrintBackground = true);
            var (off, _) = Convert(markup, o => o.PrintBackground = false);

            Assert.True(TilesPainted(on) > 0, "the fixture must paint something with backgrounds on");
            Assert.Equal(0, TilesPainted(off));
        }
    }
}
