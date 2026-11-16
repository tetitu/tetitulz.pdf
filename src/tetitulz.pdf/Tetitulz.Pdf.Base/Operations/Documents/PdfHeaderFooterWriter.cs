// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Stamps header and/or footer text on every page of a PDF.
    /// </summary>
    /// <remarks>
    /// <b>Each stamp is a pagination artifact (&#167;14.8.2.2), marked separately.</b> Unmarked, a
    /// screen reader announces a running head in the middle of the page's text. Header and footer
    /// get their own sequence rather than one wrapping both, because the &#167;14.8.2.2 subtype
    /// differs between them and a single sequence would have to be wrong about one of the two.
    ///
    /// <para><b>Nothing in the suite can see this by reading the output as a document.</b>
    /// Extraction returns the text whether or not it is marked, and neither renderer draws marked
    /// content differently, so the tests read the appended content stream itself.</para>
    /// </remarks>
    public static class PdfHeaderFooterWriter
    {
        /// <summary>
        /// Adds a header and/or footer to all pages.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="options">Header/footer options.</param>
        public static void AddHeaderFooter(Stream input, Stream output, HeaderFooterOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (options == null) throw new ArgumentNullException(nameof(options));
            // A NaN font size wrote `0 Tf` — a header no reader shows — and a NaN margin put it at
            // the origin; both came back as a successful write.
            NumericArguments.RequirePositive(options.FontSize, nameof(options), nameof(options.FontSize));
            NumericArguments.RequireNonNegative(options.MarginPt, nameof(options), nameof(options.MarginPt));

            PdfPageContentAppender.AppendContentToAllPages(
                input, output,
                (builder, page) => BuildHeaderFooter(builder, page, options),
                compressStream: false);
        }

        private static void BuildHeaderFooter(
            PdfContentStreamBuilder builder,
            PdfPageInfo page,
            HeaderFooterOptions opts)
        {
            string fontRes = builder.AddStandardFont(opts.FontName);

            builder.SaveState();
            builder.SetFillColorRgb((float)opts.ColorR, (float)opts.ColorG, (float)opts.ColorB);

            // Measured on the page as shown: a header at the shown top, the margin from the shown
            // edges, and the line read left to right as shown (PositionAsShown), so a cropped page
            // or a turned one carries its header where a reader looks for it.
            if (!string.IsNullOrEmpty(opts.HeaderText))
            {
                string text = FormatText(opts.HeaderText, page);
                double x = GetAlignedX(opts.HorizontalAlignment, opts.FontName, opts.MarginPt, page.ShownWidth, opts.FontSize, text);
                double y = page.ShownHeight - opts.MarginPt - opts.FontSize;

                builder.BeginArtifact(PdfArtifactTypes.Pagination, PdfArtifactSubtypes.Header);
                builder.BeginText();
                builder.SetFont(fontRes, (float)opts.FontSize);
                PositionAsShown(builder, page, x, y);
                builder.ShowText(text);
                builder.EndText();
                builder.EndMarkedContent();
            }

            if (!string.IsNullOrEmpty(opts.FooterText))
            {
                string text = FormatText(opts.FooterText, page);
                double x = GetAlignedX(opts.HorizontalAlignment, opts.FontName, opts.MarginPt, page.ShownWidth, opts.FontSize, text);
                double y = opts.MarginPt;

                builder.BeginArtifact(PdfArtifactTypes.Pagination, PdfArtifactSubtypes.Footer);
                builder.BeginText();
                builder.SetFont(fontRes, (float)opts.FontSize);
                PositionAsShown(builder, page, x, y);
                builder.ShowText(text);
                builder.EndText();
                builder.EndMarkedContent();
            }

            builder.RestoreState();
        }

        /// <summary>
        /// Starts a line at (<paramref name="shownX"/>, <paramref name="shownY"/>) of the page as
        /// shown, reading left to right as shown (<see cref="PdfPageInfo.TextMatrixAsShown"/>): a
        /// move on an unturned page, a text matrix carrying the turn otherwise. The Bates stamp
        /// and the header/footer writer place their lines through this alone.
        /// </summary>
        internal static void PositionAsShown(PdfContentStreamBuilder builder, PdfPageInfo page, double shownX, double shownY)
        {
            var (a, b, c, d, e, f) = page.TextMatrixAsShown(shownX, shownY);
            if (page.Rotation == 0) builder.SetTextPosition(e, f);
            else builder.SetTextMatrix(a, b, c, d, e, f);
        }

        private static string FormatText(string template, PdfPageInfo page)
        {
            // Replace placeholders: {page} = 1-based page number
            return template
                .Replace("{page}", (page.PageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{Page}", (page.PageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Where a line of <paramref name="text"/> starts for the alignment — measured by
        /// <see cref="TextWidth"/>. Bates stamps place their number through this too: the two
        /// writers carried one copy each of the same half-em estimate.
        /// </summary>
        internal static double GetAlignedX(
            HorizontalAlignment align,
            string fontName,
            double margin,
            double pageWidth,
            double fontSize,
            string text)
        {
            double approxTextWidth = TextWidth(fontName, fontSize, text);

            switch (align)
            {
                case HorizontalAlignment.Center:
                    return (pageWidth - approxTextWidth) / 2.0;
                case HorizontalAlignment.Right:
                    return pageWidth - margin - approxTextWidth;
                default: // Left
                    return margin;
            }
        }

        /// <summary>
        /// The text's width in points: the standard font's own AFM advances, the table the font
        /// is drawn with. A font with no such table (Symbol, ZapfDingbats, a name that is not one of
        /// the fourteen) falls back to half an em a character — what every font was measured by
        /// here before, which set right-aligned and centred lines visibly off where their glyphs'
        /// widths differ from that average (a run of capitals, or of digits).
        /// </summary>
        internal static double TextWidth(string fontName, double fontSize, string text)
        {
            if (!Core.Fonts.Standard14Metrics.TryGetWidthTable(fontName, out var widths, out double missing))
                return text.Length * fontSize * 0.5;

            double units = 0;
            foreach (char c in text) units += widths.TryGetValue(c, out double w) ? w : missing;
            return units * fontSize / 1000.0;
        }
    }

    /// <summary>
    /// Options for adding headers and footers.
    /// </summary>
    public sealed class HeaderFooterOptions
    {
        /// <summary>
        /// Header text template. Use <c>{page}</c> for the 1-based page number.
        /// Null or empty means no header is added.
        /// </summary>
        public string HeaderText { get; set; }

        /// <summary>
        /// Footer text template. Use <c>{page}</c> for the 1-based page number.
        /// Null or empty means no footer is added.
        /// </summary>
        public string FooterText { get; set; }

        /// <summary>PDF standard font name (default Helvetica).</summary>
        public string FontName { get; set; } = StandardFonts.Helvetica;

        /// <summary>Font size in points (default 10).</summary>
        public double FontSize { get; set; } = 10;

        /// <summary>Distance from page edges in points (default 36 = 0.5 inch).</summary>
        public double MarginPt { get; set; } = 36;

        /// <summary>Horizontal text alignment (default Left).</summary>
        public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Left;

        /// <summary>Red channel 0.0–1.0 (default 0).</summary>
        public double ColorR { get; set; } = 0;
        /// <summary>Green channel 0.0–1.0 (default 0).</summary>
        public double ColorG { get; set; } = 0;
        /// <summary>Blue channel 0.0–1.0 (default 0).</summary>
        public double ColorB { get; set; } = 0;
    }

    public enum HorizontalAlignment { Left, Center, Right }
}
