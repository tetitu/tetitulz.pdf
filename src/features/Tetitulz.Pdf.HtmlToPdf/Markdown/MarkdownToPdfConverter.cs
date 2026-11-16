// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.HtmlToPdf.Markdown
{
    /// <summary>
    /// Converts Markdown to PDF by rendering it as HTML through the existing pipeline.
    /// </summary>
    /// <remarks>
    /// <para><b>A front-end, not an engine.</b> Markdown becomes HTML
    /// (<see cref="MarkdownToHtml"/>) and everything after that is
    /// <see cref="HtmlToPdfConverter"/> — so a Markdown document inherits the tagged output, the RTL
    /// and bidi handling, the deterministic font resolution and the pagination that pipeline already
    /// has, and keeps inheriting them.</para>
    ///
    /// <para><b>It lives in this assembly rather than its own, and that is forced.</b> A separate
    /// <c>Tetitulz.Pdf.MarkdownToPdf</c> feature would need a <c>ProjectReference</c> on
    /// <c>Tetitulz.Pdf.HtmlToPdf</c> — a feature→feature edge CLAUDE.md bans outright (Builder is the
    /// only exception) and which <c>AssemblyLayeringTests</c> fails the build on.</para>
    ///
    /// <para>The supported Markdown subset, and what is deliberately outside it, is documented on
    /// <see cref="MarkdownToHtml"/>.</para>
    /// </remarks>
    public static class MarkdownToPdfConverter
    {
        /// <summary>Renders Markdown to a PDF byte array.</summary>
        /// <param name="markdown">The Markdown source.</param>
        /// <param name="options">Conversion options, or null for the defaults.</param>
        /// <param name="title">Document title, or null for none.</param>
        /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
        public static byte[] ConvertMarkdownToBytes(
            string markdown, HtmlToPdfOptions? options = null, string? title = null)
        {
            if (markdown == null) throw new ArgumentNullException(nameof(markdown));
            return HtmlToPdfConverter.ConvertHtmlToBytes(MarkdownToHtml.Convert(markdown, title), options);
        }

        /// <summary>Renders Markdown to a PDF written to <paramref name="output"/>.</summary>
        /// <param name="markdown">The Markdown source.</param>
        /// <param name="output">Receives the PDF. Not disposed.</param>
        /// <param name="options">Conversion options, or null for the defaults.</param>
        /// <param name="title">Document title, or null for none.</param>
        public static void ConvertMarkdown(
            string markdown, Stream output, HtmlToPdfOptions? options = null, string? title = null)
        {
            if (markdown == null) throw new ArgumentNullException(nameof(markdown));
            if (output == null) throw new ArgumentNullException(nameof(output));

            byte[] pdf = ConvertMarkdownToBytes(markdown, options, title);
            output.Write(pdf, 0, pdf.Length);
        }

        /// <summary>Renders the Markdown file at <paramref name="inputPath"/> to a PDF file.</summary>
        /// <param name="inputPath">Path of the Markdown file.</param>
        /// <param name="outputPdfPath">Path of the PDF to write.</param>
        /// <param name="options">Conversion options, or null for the defaults.</param>
        public static void ConvertFile(string inputPath, string outputPdfPath, HtmlToPdfOptions? options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPdfPath == null) throw new ArgumentNullException(nameof(outputPdfPath));

            byte[] pdf = ConvertMarkdownToBytes(File.ReadAllText(inputPath), options);
            File.WriteAllBytes(outputPdfPath, pdf);
        }

        /// <summary>
        /// The HTML the Markdown becomes, without rendering it.
        /// </summary>
        /// <param name="markdown">The Markdown source.</param>
        /// <param name="title">Document title, or null.</param>
        /// <remarks>
        /// Exposed because a caller debugging an unexpected layout needs to see what the converter
        /// actually produced, and because it is the boundary the two halves meet at.
        /// </remarks>
        public static string ToHtml(string markdown, string? title = null)
        {
            if (markdown == null) throw new ArgumentNullException(nameof(markdown));
            return MarkdownToHtml.Convert(markdown, title);
        }
    }
}
