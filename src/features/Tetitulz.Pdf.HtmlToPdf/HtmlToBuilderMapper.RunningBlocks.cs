// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= header / footer templates

        /// <summary>
        /// Parses a header/footer template into paragraphs, resolving the substitution classes
        /// Playwright defines so the two engines agree on what a template means.
        ///
        /// <para><c>pageNumber</c> and <c>totalPages</c> become the exporter's placeholder tokens
        /// rather than literal numbers: the page count is not known until pagination finishes, and
        /// the page number differs per page. <c>PdfExporter</c> resolves the first at draw time
        /// and back-fills the second once the final count is known.</para>
        /// </summary>
        private List<M.Paragraph> BuildRunningBlock(string? template, IDocument sourceDom, Style rootStyle)
        {
            var result = new List<M.Paragraph>();
            if (string.IsNullOrWhiteSpace(template)) return result;

            var fragment = new HtmlParser().ParseDocument(template!);
            var fragBody = fragment.Body;
            if (fragBody == null) return result;

            ApplyStylesheets(fragment);
            ResolveCssVariables(fragment);
            SubstituteRunningFields(fragBody, sourceDom);

            // A running block is drawn at a fixed offset from the page edge and never paginates,
            // so it gets its own section-less walk into a throwaway sink.
            var scratch = new M.Section();
            var sink = new List<M.IDocElement>();
            var style = DeriveStyle(rootStyle, fragBody, RunningBlockFontSizePt, false);
            WalkBlocks(fragBody, scratch, sink, 0, 0, style, ResolveAlign(fragBody));

            foreach (var el in sink)
                if (el is M.Paragraph para) result.Add(para);
            return result;
        }

        /// <summary>Default running-header/footer size — Chromium's own template default.</summary>
        private const double RunningBlockFontSizePt = 9;

        /// <summary>
        /// Minimum page margin when a running header/footer is present, in points.
        ///
        /// <para>The exporter draws a running block 0.5&#160;in from the page edge, so the content
        /// margin has to clear that plus the block's own line. 0.75&#160;in does, and only ever
        /// <i>grows</i> a caller's margin — an explicit larger one is left alone.</para>
        /// </summary>
        private const double RunningBlockReservePt = 0.75 * PdfUnits.PointsPerInch;

        /// <summary>
        /// Replaces the substitution spans Playwright's templates use
        /// (<c>class="pageNumber"</c>, <c>totalPages</c>, <c>title</c>, <c>date</c>, <c>url</c>)
        /// with their values, so a template written for the browser path renders the same here.
        /// </summary>
        private void SubstituteRunningFields(IElement root, IDocument sourceDom)
        {
            foreach (var el in root.QuerySelectorAll("span,div,p"))
            {
                string? cls = el.GetAttribute("class");
                if (string.IsNullOrEmpty(cls)) continue;

                string? value = null;
                foreach (string token in cls!.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Eq(token, "pageNumber")) { value = PdfExporter.PagePlaceholder; break; }
                    if (Eq(token, "totalPages")) { value = PdfExporter.NumPagesPlaceholder; break; }
                    if (Eq(token, "title")) { value = sourceDom?.Title ?? ""; break; }
                    if (Eq(token, "date")) { value = _options.RunningDate ?? ""; break; }
                    // `url` has no meaning without a source URL, and inventing one would be worse
                    // than an empty span: it would look authoritative.
                    if (Eq(token, "url")) { value = ""; break; }
                }
                if (value != null) el.TextContent = value;
            }
        }
    }
}
