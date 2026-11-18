// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading.Tasks;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Net;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Converts HTML content or URLs to PDF through one of two engines, selected by
    /// <see cref="HtmlToPdfOptions.RenderEngine"/>:
    /// <list type="bullet">
    /// <item><see cref="HtmlRenderEngine.Browser"/> — Microsoft Playwright driving
    /// headless Chromium, the one browser Playwright prints a PDF from. Full CSS fidelity; needs
    /// the browser installed.</item>
    /// <item><see cref="HtmlRenderEngine.Managed"/> — pure-managed, in-process, no browser and no
    /// network I/O. Honoured by every HTML- and file-input entry point, synchronous and
    /// asynchronous alike; the URL entry points refuse it rather than silently launching a
    /// browser.</item>
    /// </list>
    /// </summary>
    public static class HtmlToPdfConverter
    {
        #region Synchronous API (file paths)

        /// <summary>
        /// Converts an HTML file to PDF.
        /// </summary>
        public static void ConvertFile(string inputHtmlPath, string outputPdfPath,
            HtmlToPdfOptions? options = null)
        {
            if (inputHtmlPath == null) throw new ArgumentNullException(nameof(inputHtmlPath));
            if (outputPdfPath == null) throw new ArgumentNullException(nameof(outputPdfPath));

            var bytes = ConvertFileToBytes(inputHtmlPath, options);
            File.WriteAllBytes(outputPdfPath, bytes);
        }

        /// <summary>
        /// Converts an HTML string to a PDF file.
        /// </summary>
        public static void ConvertHtml(string htmlContent, string outputPdfPath,
            HtmlToPdfOptions? options = null)
        {
            if (htmlContent == null) throw new ArgumentNullException(nameof(htmlContent));
            if (outputPdfPath == null) throw new ArgumentNullException(nameof(outputPdfPath));

            var bytes = ConvertHtmlToBytes(htmlContent, options);
            File.WriteAllBytes(outputPdfPath, bytes);
        }

        /// <summary>
        /// Converts a URL to a PDF file.
        /// </summary>
        public static void ConvertUrl(string url, string outputPdfPath,
            HtmlToPdfOptions? options = null)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            if (outputPdfPath == null) throw new ArgumentNullException(nameof(outputPdfPath));

            var bytes = ConvertUrlToBytes(url, options);
            File.WriteAllBytes(outputPdfPath, bytes);
        }

        #endregion

        #region Synchronous API (byte arrays)

        /// <summary>
        /// Converts an HTML file to a PDF byte array.
        /// </summary>
        public static byte[] ConvertFileToBytes(string inputHtmlPath,
            HtmlToPdfOptions? options = null)
        {
            if (inputHtmlPath == null) throw new ArgumentNullException(nameof(inputHtmlPath));
            options = options ?? HtmlToPdfOptions.Default;
            if (options.RenderEngine == HtmlRenderEngine.Managed)
                return ConvertFileManaged(inputHtmlPath, options);
            return Task.Run(() => ConvertFileToBytesAsync(inputHtmlPath, options)).GetAwaiter().GetResult();
        }

        // The managed engine's file path, shared by the sync and async entry points so the two
        // cannot disagree. The input file's directory becomes the base for relative subresources
        // when the caller set no explicit BasePath — matching what the browser path already does
        // for file:// subresources (see ConvertFileToBytesAsync). The caller's options instance is
        // never mutated; the directory travels as an argument.
        private static byte[] ConvertFileManaged(string inputHtmlPath, HtmlToPdfOptions options)
        {
            if (!File.Exists(inputHtmlPath))
                throw new FileNotFoundException("HTML input file not found.", inputHtmlPath);
            string fullPath = Path.GetFullPath(inputHtmlPath);
            return HtmlToBuilderMapper.ConvertHtml(
                File.ReadAllText(fullPath), options, Path.GetDirectoryName(fullPath));
        }

        // ConvertUrl* has no managed implementation by design: the managed engine performs no
        // network I/O at all. Falling through to Playwright is what this guard exists to stop —
        // that silent fallback is how RenderEngine.Managed came to be ignored on every async
        // entry point, and therefore through IHtmlToPdfConverter.
        private static void RefuseManagedUrlConversion(HtmlToPdfOptions options)
        {
            if (options.RenderEngine != HtmlRenderEngine.Managed) return;
            throw new HtmlToPdfException(
                "RenderEngine.Managed cannot convert a URL: the managed engine performs no network I/O. " +
                "Use ConvertHtml/ConvertFile with the managed engine, or set RenderEngine.Browser.");
        }

        /// <summary>
        /// Converts an HTML string to a PDF byte array.
        /// </summary>
        public static byte[] ConvertHtmlToBytes(string htmlContent,
            HtmlToPdfOptions? options = null)
        {
            if (htmlContent == null) throw new ArgumentNullException(nameof(htmlContent));
            options = options ?? HtmlToPdfOptions.Default;
            if (options.RenderEngine == HtmlRenderEngine.Managed)
                return HtmlToBuilderMapper.ConvertHtml(htmlContent, options);
            return Task.Run(() => ConvertHtmlToBytesAsync(htmlContent, options)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Converts an HTML string with the managed engine, mapping its form controls to form-field
        /// placeholders, and reports where each one landed.
        /// </summary>
        /// <remarks>
        /// <para><b>The returned bytes contain the placeholders, not the fields.</b> This assembly
        /// references only Core and Builder, and the one field author —
        /// <c>Base.Operations.Forms.PdfFormFieldBuilder.AddFields</c>, with its appearance
        /// generation — is in Base. Hand <see cref="HtmlFormConversionResult.FormFields"/> to it, or
        /// call <c>Tetitulz.Pdf.Plugins.Html.HtmlFillableForms.ConvertHtmlToFillablePdf</c>, which
        /// does both steps.</para>
        ///
        /// <para><b>Mapped:</b> text-like <c>&lt;input&gt;</c> types, <c>password</c> (never with a
        /// value, ISO 32000-2 Table 229), <c>checkbox</c>, <c>&lt;textarea&gt;</c> (a multi-line
        /// field one line tall), <c>&lt;select&gt;</c> as a combo box, or a list box when it
        /// declares <c>multiple</c> or a <c>size</c> above one, and <c>radio</c> — the buttons that
        /// share a <c>name</c> placed as one group, each with its own export value. <b>Left static,
        /// and reported:</b> buttons, <c>file</c> and <c>image</c> inputs, which carry no value to
        /// edit.</para>
        ///
        /// <para>The ordinary entry points keep drawing controls as graphics, which is what a browser
        /// prints — so the two engines agree unless a caller asks for fields.</para>
        /// </remarks>
        /// <exception cref="HtmlToPdfException">
        /// <see cref="HtmlToPdfOptions.RenderEngine"/> is not <see cref="HtmlRenderEngine.Managed"/>:
        /// only the managed layout knows where a control landed.
        /// </exception>
        public static HtmlFormConversionResult ConvertHtmlWithFormFields(string htmlContent,
            HtmlToPdfOptions? options = null)
        {
            if (htmlContent == null) throw new ArgumentNullException(nameof(htmlContent));
            options = options ?? HtmlToPdfOptions.Default;
            if (options.RenderEngine != HtmlRenderEngine.Managed)
                throw new HtmlToPdfException(
                    "ConvertHtmlWithFormFields needs RenderEngine.Managed: a browser prints form controls " +
                    "as graphics and reports nothing about where they landed.");
            var (pdf, fields) = HtmlToBuilderMapper.ConvertHtmlWithFormFields(htmlContent, options, null);
            return new HtmlFormConversionResult(pdf, fields);
        }

        /// <summary>
        /// Converts a URL to a PDF byte array.
        /// </summary>
        public static byte[] ConvertUrlToBytes(string url,
            HtmlToPdfOptions? options = null)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            RefuseManagedUrlConversion(options ?? HtmlToPdfOptions.Default);
            return Task.Run(() => ConvertUrlToBytesAsync(url, options)).GetAwaiter().GetResult();
        }

        #endregion

        #region Synchronous API (streams)

        /// <summary>
        /// Converts an HTML string to PDF and writes to a stream.
        /// </summary>
        public static void ConvertHtml(string htmlContent, Stream output,
            HtmlToPdfOptions? options = null)
        {
            if (htmlContent == null) throw new ArgumentNullException(nameof(htmlContent));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var bytes = ConvertHtmlToBytes(htmlContent, options);
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Converts a URL to PDF and writes to a stream.
        /// </summary>
        public static void ConvertUrl(string url, Stream output,
            HtmlToPdfOptions? options = null)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var bytes = ConvertUrlToBytes(url, options);
            output.Write(bytes, 0, bytes.Length);
        }

        #endregion

        #region Async API

        /// <summary>
        /// Asynchronously converts an HTML file to a PDF byte array.
        /// </summary>
        public static async Task<byte[]> ConvertFileToBytesAsync(string inputHtmlPath,
            HtmlToPdfOptions? options = null)
        {
            if (inputHtmlPath == null) throw new ArgumentNullException(nameof(inputHtmlPath));
            options = options ?? HtmlToPdfOptions.Default;
            // The managed engine is synchronous and launches nothing; returning its completed
            // result keeps the async surface honest rather than silently using Playwright.
            if (options.RenderEngine == HtmlRenderEngine.Managed)
                return ConvertFileManaged(inputHtmlPath, options);
            if (!File.Exists(inputHtmlPath))
                throw new FileNotFoundException("HTML input file not found.", inputHtmlPath);
            string fullPath = Path.GetFullPath(inputHtmlPath);
            var fileUri = new Uri(fullPath).AbsoluteUri;
            // Permit file:// for this conversion, but confine file:// SUBRESOURCES
            // (<img src="file:///etc/passwd">, etc.) to the input file's own directory so a
            // crafted local HTML file cannot exfiltrate arbitrary files into the output PDF.
            string? baseDir = Path.GetDirectoryName(fullPath);
            return await ConvertUrlCoreAsync(fileUri, options ?? HtmlToPdfOptions.Default,
                    allowFileScheme: true, fileBaseDirectory: baseDir)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously converts an HTML string to a PDF byte array.
        /// </summary>
        public static async Task<byte[]> ConvertHtmlToBytesAsync(string htmlContent,
            HtmlToPdfOptions? options = null)
        {
            if (htmlContent == null) throw new ArgumentNullException(nameof(htmlContent));
            options = options ?? HtmlToPdfOptions.Default;
            if (options.RenderEngine == HtmlRenderEngine.Managed)
                return HtmlToBuilderMapper.ConvertHtml(htmlContent, options);

            return await PluginBrowserRenderer
                .RenderAsync(HtmlBrowserRequest.ForHtml(htmlContent, options))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously converts a URL to a PDF byte array.
        /// </summary>
        public static Task<byte[]> ConvertUrlToBytesAsync(string url,
            HtmlToPdfOptions? options = null)
        {
            options = options ?? HtmlToPdfOptions.Default;
            RefuseManagedUrlConversion(options);
            return ConvertUrlCoreAsync(url, options,
                allowFileScheme: false, fileBaseDirectory: null);
        }


        private static async Task<byte[]> ConvertUrlCoreAsync(string url,
            HtmlToPdfOptions options, bool allowFileScheme, string? fileBaseDirectory)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            options = options ?? HtmlToPdfOptions.Default;

            return await PluginBrowserRenderer
                .RenderAsync(HtmlBrowserRequest.ForUrl(url, options, allowFileScheme, fileBaseDirectory))
                .ConfigureAwait(false);
        }

        #endregion

        #region Private helpers






        #endregion

        #region Browser installation helper

        /// <summary>
        /// <b>Obsolete.</b> Browser installation moved out with the browser engine.
        /// </summary>
        /// <remarks>
        /// The Playwright binaries are installed by the plugin that drives them:
        /// <c>Tetitulz.Pdf.Plugins.HtmlBrowser.PlaywrightHtmlBrowserPlugin.InstallBrowsers()</c>.
        /// Kept as a throwing stub rather than deleted so a caller upgrading gets a message naming
        /// the replacement instead of a compile error with no explanation.
        /// </remarks>
        [Obsolete("Browser installation moved to Tetitulz.Pdf.Plugins.HtmlBrowser. Call " +
                  "PlaywrightHtmlBrowserPlugin.InstallBrowsers() instead.", error: false)]
        public static void InstallBrowsers(BrowserEngine? engine = null)
            => throw new HtmlToPdfException(
                "Browser installation moved out of Tetitulz.Pdf.HtmlToPdf along with the browser " +
                "engine. Reference Tetitulz.Pdf.Plugins.HtmlBrowser and call " +
                "PlaywrightHtmlBrowserPlugin.InstallBrowsers(engine).");

        #endregion
    }
}
