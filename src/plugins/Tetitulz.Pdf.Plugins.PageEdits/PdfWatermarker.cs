// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// Stamps a diagonal text watermark on every page of a PDF.
    /// </summary>
    public static class PdfWatermarker
    {
        /// <summary>
        /// Adds a diagonal text watermark to all pages.
        /// </summary>
        /// <exception cref="PdfEncryptedDocumentException">
        /// The input is encrypted. See the remarks — the operation cannot mark such a document and
        /// must not report success for one.
        /// </exception>
        /// <remarks>
        /// <para><b>Fails closed on an encrypted document</b>, as every other whole-file operation in
        /// this repository does (<c>PdfMerger</c>, <c>PdfSanitizer</c>, <c>PdfRedactionEngine</c>).
        /// <see cref="PdfDocument"/> loads neither the catalog nor the page tree of an encrypted file,
        /// so <c>AppendContentToAllPages</c> would iterate <b>zero</b> pages and the call would return
        /// normally — handing a DMS a valid, completely <i>unwatermarked</i> copy of a document it
        /// believed it had stamped CONFIDENTIAL.</para>
        /// <para>Route an encrypted input through
        /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run</c>, which decrypts, runs an
        /// <c>Action&lt;Stream, Stream&gt;</c> — this method — and re-encrypts. This plugin cannot do
        /// that itself: the ring model forbids it a reference to the Encryption assembly.</para>
        /// </remarks>
        public static void AddWatermark(Stream input, Stream output, WatermarkOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.Text))
                throw new ArgumentException("Watermark text must not be empty.", nameof(options));

            RejectUnrepresentableText(options.Text);
            ValidateOptions(options);

            // Seek-normalised once, here, so the encryption probe cannot consume a forward-only
            // stream out from under the appender (which normalises again, harmlessly).
            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            try
            {
                List<PageBox> boxes;
                using (var probe = PdfDocument.Open(seekable))
                {
                    if (probe.IsEncrypted)
                        throw new PdfEncryptedDocumentException(
                            "The document is encrypted. Watermarking appends content to every page, and " +
                            "an encrypted document exposes no pages to append to — the output would be " +
                            "an unmarked copy of the input. Decrypt it first " +
                            "(Tetitulz.Pdf.Encryption.PdfDecryptor), or run this through " +
                            "PdfEncryptedRoundTrip.Run, which decrypts, watermarks and re-encrypts.");

                    boxes = ReadPageBoxes(probe);
                }
                seekable.Position = 0;

                PdfPageContentAppender.AppendContentToAllPages(
                    seekable, output,
                    (builder, page) => BuildWatermark(builder, page, options, boxes),
                    compressStream: false);
            }
            finally
            {
                // Release the buffer EnsureSeekable allocates for a forward-only input — including
                // on the encrypted-refusal path, which returns before the appender runs. Only the
                // copy is ours to close: a caller's own seekable stream is returned as-is by
                // EnsureSeekable and must survive the call.
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// Refuses option values the PDF operators they become cannot legally carry.
        /// </summary>
        /// <remarks>
        /// Every one of these reached the file verbatim. §11.6.4.4 requires <c>/ca</c> and
        /// <c>/CA</c> in 0..1 and §8.6.4.3 requires the <c>rg</c> components in the same range, so a
        /// host binding <c>Opacity</c> to a percentage control and passing 3.5 emitted
        /// <c>&lt;&lt; /ca 3.5 /CA 3.5 &gt;&gt;</c>: strict readers reject it, lenient ones clamp
        /// differently, and the same file then looks different in two viewers — none of the three
        /// outcomes being what the caller asked for. An unknown <c>FontName</c> becomes a
        /// <c>/BaseFont</c> no viewer can resolve.
        /// <para>Refused rather than clamped, because a 350% opacity is a caller bug and silently
        /// turning it into 100% hides it — the same reasoning the empty-text check already applies
        /// one line up.</para>
        /// </remarks>
        private static void ValidateOptions(WatermarkOptions options)
        {
            RequireUnitInterval(options.Opacity, nameof(options.Opacity));
            RequireUnitInterval(options.ColorR, nameof(options.ColorR));
            RequireUnitInterval(options.ColorG, nameof(options.ColorG));
            RequireUnitInterval(options.ColorB, nameof(options.ColorB));

            if (!(options.FontSize > 0) || double.IsInfinity(options.FontSize))
                throw new ArgumentOutOfRangeException(nameof(options.FontSize), options.FontSize,
                    "The watermark font size must be a finite number greater than zero.");

            // A NaN or infinite angle made the cosine and sine NaN, every page's text matrix
            // `0 0 0 0 cx cy Tm`, and the stamp invisible — a copy that looks marked to the caller and
            // is not.
            Core.Util.NumericArguments.RequireFinite(options.AngleDegrees, nameof(options.AngleDegrees));

            // One implementation, Core.Fonts.Standard14Metrics.IsStandard14: a private copy of the
            // set could drift from it even with the same fourteen constants.
            if (!Core.Fonts.Standard14Metrics.IsStandard14(options.FontName))
                throw new ArgumentException(
                    $"'{options.FontName}' is not one of the standard 14 fonts, and this watermarker " +
                    "embeds nothing — the /BaseFont would name a font no viewer can resolve. Use a " +
                    "name from Tetitulz.Pdf.Core.Constants.StandardFonts.",
                    nameof(options.FontName));
        }

        private static void RequireUnitInterval(double value, string name)
        {
            if (double.IsNaN(value) || value < 0 || value > 1)
                throw new ArgumentOutOfRangeException(name, value,
                    "The value must be between 0 and 1 inclusive: it is written straight into a PDF " +
                    "operator whose range ISO 32000-1 fixes (§11.6.4.4 for /ca and /CA, §8.6.4.3 for " +
                    "rg components).");
        }

        /// <summary>
        /// Refuses watermark text the standard-14 <c>/WinAnsiEncoding</c> path cannot represent.
        /// </summary>
        /// <remarks>
        /// <see cref="PdfContentStreamBuilder.ShowText"/> encodes one byte per character and
        /// substitutes <c>'?'</c> for anything above U+00FF, so <c>"סודי"</c> stamped every page
        /// with <c>"????"</c> — a watermark that says nothing, from a call that reported success.
        /// Bytes 0x80–0x9F are rejected too: Latin-1 puts C1 control codes there and WinAnsi puts
        /// typographic characters (the em dash, the curly quotes), so the same byte means different
        /// things to the encoder and to the font, and the glyph drawn is not the one asked for.
        /// <para>Refusing loudly is the honest half of a limit this plugin cannot lift on its own:
        /// the general fix is an embedded font through <c>Core.Text.TextRunComposer</c>, which needs
        /// a font source the caller has not supplied.</para>
        /// </remarks>
        private static void RejectUnrepresentableText(string text)
        {
            // The rule is Core.Text.TextRunComposer.NeedsCompositeFont's, one character at a time so
            // the message can name the first offender.
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!Core.Text.TextRunComposer.NeedsCompositeFont(c.ToString())) continue;

                throw new ArgumentException(
                    $"The watermark text cannot be drawn with a standard-14 /WinAnsiEncoding font: " +
                    $"character U+{(int)c:X4} at index {i} would be written as '?'. Use text within " +
                    "ASCII or Latin-1 (excluding U+0080–U+009F, which WinAnsi maps differently), or " +
                    "stamp the mark yourself with an embedded font.",
                    nameof(text));
            }
        }

        /// <summary>
        /// One page's visible box, in the order <c>doc.Pages</c> reports the pages, and the page's
        /// display rotation (<c>/Rotate</c>, inherited, normalised to 0, 90, 180 or 270).
        /// </summary>
        private readonly struct PageBox
        {
            internal readonly double X, Y, Width, Height;
            internal readonly int Rotation;
            internal PageBox(double x, double y, double width, double height, int rotation)
            { X = x; Y = y; Width = width; Height = height; Rotation = rotation; }
        }

        /// <summary>
        /// Reads each page's <c>/CropBox</c> (falling back to <c>/MediaBox</c>) including its
        /// <b>origin</b>.
        /// </summary>
        /// <remarks>
        /// <see cref="PdfPageInfo"/> carries width and height only — <c>x2−x1</c> and <c>y2−y1</c> —
        /// so placing at <c>Width/2, Height/2</c> discards the origin. For a page whose box does not
        /// start at (0,0), which is ordinary in imposed, cropped and n-up output, the stamp landed
        /// outside the box and rendered nowhere while the call reported success. The <c>/CropBox</c>
        /// is preferred because that is the region a viewer displays.
        /// </remarks>
        private static List<PageBox> ReadPageBoxes(PdfDocument doc)
        {
            var boxes = new List<PageBox>();
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var page = doc.Pages[i];
                int rotation = PdfPageRotation.Normalize(page.Rotate, out _);
                var box = page.CropBox ?? page.MediaBox;
                if (box == null || box.Count < 4) { boxes.Add(new PageBox(0, 0, 0, 0, rotation)); continue; }

                double x1 = Coordinate(doc, box[0]), y1 = Coordinate(doc, box[1]);
                double x2 = Coordinate(doc, box[2]), y2 = Coordinate(doc, box[3]);
                boxes.Add(new PageBox(
                    Math.Min(x1, x2), Math.Min(y1, y2),
                    Math.Abs(x2 - x1), Math.Abs(y2 - y1), rotation));
            }
            return boxes;
        }

        private static double Coordinate(PdfDocument doc, PdfObject value)
            => (doc.ResolveObject(value) as PdfNumber)?.DoubleValue ?? 0;

        private static void BuildWatermark(
            PdfContentStreamBuilder builder,
            PdfPageInfo page,
            WatermarkOptions opts,
            List<PageBox> boxes)
        {
            // The box origin, when it is known. Falling back to the callback's width and height
            // reproduces the historical placement for a document whose boxes could not be read.
            double cx, cy;
            int rotation = 0;
            bool boxKnown = page.PageIndex >= 0 && page.PageIndex < boxes.Count;
            if (boxKnown) rotation = boxes[page.PageIndex].Rotation;
            if (boxKnown && boxes[page.PageIndex].Width > 0)
            {
                var box = boxes[page.PageIndex];
                cx = box.X + box.Width / 2.0;
                cy = box.Y + box.Height / 2.0;
            }
            else
            {
                cx = page.Width / 2.0;
                cy = page.Height / 2.0;
            }

            // The angle is measured as the page is displayed: a /Rotate 90 page is shown turned a
            // quarter turn clockwise, so a baseline that reads left to right on the screen runs up the
            // page's own +y axis — the same turn PdfPageTextWriter gives text it keeps upright. Without
            // it a mark at 0° on a /Rotate 180 page reads upside down.
            double angleDeg = opts.AngleDegrees + rotation;
            double angleRad = angleDeg * Math.PI / 180.0;

            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);

            string fontRes = builder.AddStandardFont(opts.FontName);
            string gsRes = builder.AddExtGState((float)opts.Opacity, (float)opts.Opacity);

            builder.SaveState();
            builder.SetGraphicsState(gsRes);
            builder.SetFillColorRgb((float)opts.ColorR, (float)opts.ColorG, (float)opts.ColorB);
            builder.BeginText();
            builder.SetFont(fontRes, (float)opts.FontSize);
            builder.SetTextMatrix(cos, sin, -sin, cos, cx, cy);
            builder.ShowText(opts.Text);
            builder.EndText();
            builder.RestoreState();
        }
    }

    /// <summary>
    /// Options for text watermarking.
    /// </summary>
    public sealed class WatermarkOptions
    {
        public string Text { get; set; } = "CONFIDENTIAL";
        public string FontName { get; set; } = StandardFonts.Helvetica;
        public double FontSize { get; set; } = 48;
        /// <summary>
        /// The mark's angle in degrees, counter-clockwise from the horizontal <b>as the page is
        /// displayed</b>: a page's <c>/Rotate</c> is added before the text matrix is written, so 0
        /// reads left to right on every page, whatever its rotation.
        /// </summary>
        public double AngleDegrees { get; set; } = 45;
        public double Opacity { get; set; } = 0.3;
        public double ColorR { get; set; } = 0.7;
        public double ColorG { get; set; } = 0.7;
        public double ColorB { get; set; } = 0.7;
    }
}
