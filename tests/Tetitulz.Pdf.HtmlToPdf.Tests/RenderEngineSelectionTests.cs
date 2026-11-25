// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <see cref="HtmlRenderEngine.Managed"/> must be honoured on every entry point that can
    /// serve it, not only the two synchronous byte methods.
    ///
    /// <para>Checked at <c>ConvertFileToBytes</c> and <c>ConvertHtmlToBytes</c> alone, every
    /// <c>…Async</c> overload would launch Playwright regardless. Because
    /// <see cref="HtmlToPdfConverterService"/> — and therefore <see cref="IHtmlToPdfConverter"/>,
    /// the DI surface — delegates exclusively to the async methods, selecting the managed engine
    /// through dependency injection would do nothing at all. These tests run with no browser
    /// installed, which is what makes them falsifiable: an async case that ignored the selection
    /// would either throw (no Playwright) or produce browser output.</para>
    /// </summary>
    public class RenderEngineSelectionTests
    {
        // ------------------------------------------------------------------ async parity

        [Fact]
        public async Task Managed_SyncAndAsync_ProduceIdenticalBytes()
        {
            const string html = "<html><body><h1>Title</h1><p>Hello <b>world</b>.</p></body></html>";

            byte[] sync = HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed());
            byte[] async = await HtmlToPdfConverter.ConvertHtmlToBytesAsync(html, Managed());

            // Byte-identity modulo the random trailer /ID is the strongest available statement
            // that the same engine ran. A weaker "both are non-empty PDFs" would pass with the
            // async path on Chromium.
            Assert.Equal(NormalizedRaw(sync), NormalizedRaw(async));
        }

        [Fact]
        public async Task Managed_ThroughDiInterface_UsesTheManagedEngine()
        {
            const string html = "<html><body><p>Injected</p></body></html>";
            IHtmlToPdfConverter svc = new HtmlToPdfConverterService();

            byte[] viaDi = await svc.ConvertHtmlToBytesAsync(html, Managed());
            byte[] direct = HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed());

            Assert.Equal(NormalizedRaw(direct), NormalizedRaw(viaDi));
        }

        [Fact]
        public async Task Managed_ConvertFileAsync_MatchesSync()
        {
            using var dir = NewScratchDir();
            string htmlPath = dir.Write("doc.html", "<html><body><p>From a file</p></body></html>");

            byte[] sync = HtmlToPdfConverter.ConvertFileToBytes(htmlPath, Managed());
            byte[] async = await HtmlToPdfConverter.ConvertFileToBytesAsync(htmlPath, Managed());

            Assert.Equal(NormalizedRaw(sync), NormalizedRaw(async));
        }

        // ------------------------------------------------------------------ implicit base path

        [Fact]
        public void Managed_ConvertFile_LoadsSiblingImage_WithoutExplicitBasePath()
        {
            using var dir = NewScratchDir();
            dir.Write("logo.png", MakeRgbPng(2, 2));
            string htmlPath = dir.Write("doc.html",
                "<html><body><p>Logo:</p><img src=\"logo.png\" width=\"20\" height=\"20\"></body></html>");

            string raw = Raw(HtmlToPdfConverter.ConvertFileToBytes(htmlPath, Managed()));

            // The image really reached the PDF: an XObject with the source's own dimensions.
            Assert.Contains("/Subtype /Image", raw);
            Assert.Contains("/Width 2", raw);
            Assert.Contains("/Height 2", raw);
        }

        [Fact]
        public void Managed_ConvertHtml_WithNoBasePath_StillRefusesLocalImages()
        {
            // The implicit base path is a property of converting a FILE. Converting a string has
            // no directory to imply, so a relative src must stay unreadable — otherwise the
            // convenience would silently widen the trust boundary for untrusted markup.
            using var dir = NewScratchDir();
            dir.Write("logo.png", MakeRgbPng(2, 2));

            string raw = Raw(ToPdf("<p>x</p><img src=\"logo.png\" width=\"20\" height=\"20\">"));

            Assert.DoesNotContain("/Subtype /Image", raw);
        }

        [Fact]
        public void Managed_ConvertFile_ImageOutsideTheInputDirectory_IsRefused()
        {
            using var outer = NewScratchDir();
            outer.Write("secret.png", MakeRgbPng(2, 2));
            string inner = Path.Combine(outer.Path, "sub");
            Directory.CreateDirectory(inner);
            string htmlPath = Path.Combine(inner, "doc.html");
            File.WriteAllText(htmlPath,
                "<html><body><img src=\"../secret.png\" width=\"20\" height=\"20\"></body></html>");

            string raw = Raw(HtmlToPdfConverter.ConvertFileToBytes(htmlPath, Managed()));

            Assert.DoesNotContain("/Subtype /Image", raw);
        }

        [Fact]
        public void Managed_ExplicitBasePath_WinsOverTheInputDirectory()
        {
            using var htmlDir = NewScratchDir();
            using var assetDir = NewScratchDir();
            htmlDir.Write("logo.png", MakeRgbPng(2, 2));      // 2×2 beside the HTML
            assetDir.Write("logo.png", MakeRgbPng(4, 4));     // 4×4 in the explicit base
            string htmlPath = htmlDir.Write("doc.html",
                "<html><body><img src=\"logo.png\" width=\"20\" height=\"20\"></body></html>");

            var options = Managed();
            options.BasePath = assetDir.Path;
            string raw = Raw(HtmlToPdfConverter.ConvertFileToBytes(htmlPath, options));

            Assert.Contains("/Width 4", raw);
            Assert.DoesNotContain("/Width 2", raw);
        }

        [Fact]
        public void Managed_ConvertFile_DoesNotMutateTheCallersOptions()
        {
            // A reused options instance must not silently acquire file-read permission from an
            // earlier ConvertFile call — a later ConvertHtml on untrusted markup would inherit it.
            using var dir = NewScratchDir();
            dir.Write("logo.png", MakeRgbPng(2, 2));
            string htmlPath = dir.Write("doc.html", "<html><body><p>x</p></body></html>");

            var options = Managed();
            HtmlToPdfConverter.ConvertFileToBytes(htmlPath, options);

            Assert.Null(options.BasePath);

            string raw = Raw(HtmlToPdfConverter.ConvertHtmlToBytes(
                "<img src=\"logo.png\" width=\"20\" height=\"20\">", options));
            Assert.DoesNotContain("/Subtype /Image", raw);
        }

        // ------------------------------------------------------------------ URL refusal

        [Fact]
        public void Managed_ConvertUrl_ThrowsNamingTheEngine()
        {
            var ex = Assert.Throws<HtmlToPdfException>(() =>
                HtmlToPdfConverter.ConvertUrlToBytes("https://example.com", Managed()));
            Assert.Contains("Managed", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Managed_ConvertUrlAsync_ThrowsNamingTheEngine()
        {
            var ex = await Assert.ThrowsAsync<HtmlToPdfException>(() =>
                HtmlToPdfConverter.ConvertUrlToBytesAsync("https://example.com", Managed()));
            Assert.Contains("Managed", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Managed_ConvertUrlAsync_ThroughDiInterface_Throws()
        {
            IHtmlToPdfConverter svc = new HtmlToPdfConverterService();
            await Assert.ThrowsAsync<HtmlToPdfException>(() =>
                svc.ConvertUrlToBytesAsync("https://example.com", Managed()));
        }

        [Fact]
        public void Managed_ConvertFile_MissingInput_ThrowsFileNotFound()
        {
            Assert.Throws<FileNotFoundException>(() =>
                HtmlToPdfConverter.ConvertFileToBytes(
                    Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N") + ".html"),
                    Managed()));
        }
    }
}
