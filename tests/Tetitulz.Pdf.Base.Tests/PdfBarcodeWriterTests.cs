// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests
{
    public class PdfBarcodeWriterTests
    {
        /// <summary>
        /// A barcode PDF must actually contain bars, and those bars must depend on the encoded
        /// data. Both halves matter: a blank page is a valid non-empty PDF, and a generator that
        /// ignored its input would still paint <i>something</i>.
        /// </summary>
        [Fact]
        public void GeneratePdf_Code128_PaintsBarsThatDependOnTheData()
        {
            using var hello = PixelAssert.Render(
                PdfBarcodeWriter.GeneratePdf("HELLO", BarcodeType.Code128));

            int ink = PixelAssert.CountInk(hello);
            Assert.True(ink > 100, $"a barcode should paint bars, got {ink} inked pixels");

            // Bars are wider than they are tall for this payload — a sanity check that the ink is
            // a barcode and not, say, an error message rendered as text.
            var box = PixelAssert.InkBounds(hello);
            Assert.NotNull(box);
            Assert.True(box!.Value.right - box.Value.left > box.Value.bottom - box.Value.top);

            // Different payload, different bars.
            using var world = PixelAssert.Render(
                PdfBarcodeWriter.GeneratePdf("WORLD!", BarcodeType.Code128));
            Assert.NotEqual(ink, PixelAssert.CountInk(world));
        }

        [Fact]
        public void GeneratePdf_Code128_OutputStartsWithPdf()
        {
            byte[] pdf = PdfBarcodeWriter.GeneratePdf("TEST", BarcodeType.Code128);
            string header = Encoding.ASCII.GetString(pdf, 0, Math.Min(8, pdf.Length));
            Assert.StartsWith("%PDF-", header);
        }

        [Fact]
        public void GenerateContentStream_Code128_ReturnsNonEmptyString()
        {
            string cs = PdfBarcodeWriter.GenerateContentStream("ABC", BarcodeType.Code128, 10f, 400f);
            Assert.False(string.IsNullOrWhiteSpace(cs));
        }

        [Fact]
        public void GenerateContentStream_Code128_ContainsDrawCommands()
        {
            string cs = PdfBarcodeWriter.GenerateContentStream("12345", BarcodeType.Code128, 10f, 400f);
            // Should contain path fill operator
            Assert.Contains("f", cs);
        }

        /// <summary>
        /// EAN-13 given only 12 digits must <b>compute</b> the 13th, and lay the result out as the
        /// 95-module symbol the standard defines. "It did not throw" is equally true of a generator
        /// that dropped the check digit or drew nothing.
        /// </summary>
        [Fact]
        public void GeneratePdf_Ean13_TwelveDigits_AppendsTheCheckDigitAndSpansNinetyFiveModules()
        {
            byte[] pdf = PdfBarcodeWriter.GeneratePdf("123456789012", BarcodeType.Ean13);
            string cs  = ContentStreamOf(pdf);

            // 1·1+2·3+3·1+4·3+5·1+6·3+7·1+8·3+9·1+0·3+1·1+2·3 = 92 → check digit (10-2)%10 = 8.
            Assert.Contains("(1234567890128) Tj", cs, StringComparison.Ordinal);

            var bars = RectsOf(cs);
            float mw = new BarcodeOptions().ModuleWidth;

            // Guard 101 … centre 01010 … guard 101: the first and last modules of an EAN-13
            // symbol are both bars, so the painted span is exactly 94 module pitches wide.
            Assert.Equal(94, (int)MathF.Round((bars[bars.Count - 1].X - bars[0].X) / mw));

            // Two guard bars + centre + 12 seven-module digit patterns — a real symbol, and
            // fewer bars than modules because the light modules are not painted.
            Assert.InRange(bars.Count, 30, 94);
            Assert.All(bars, r => Assert.Equal(mw, r.W, 3));
        }

        /// <summary>
        /// The QR path must emit a real module grid at the requested size, and the grid must
        /// depend on the payload. Two same-length payloads pick the same QR version, so a
        /// difference in module positions can only come from the encoder actually encoding.
        /// </summary>
        [Fact]
        public void GeneratePdf_QrCode_PaintsASquareModuleGridThatDependsOnThePayload()
        {
            byte[] pdf = PdfBarcodeWriter.GeneratePdf("https://example.com", BarcodeType.QrCode);
            string cs  = ContentStreamOf(pdf);

            var label = Regex.Match(cs, @"% QR Code \((\d+)x(\d+)\)");
            Assert.True(label.Success, "content stream carries no QR matrix label:\n" + Head(cs));

            int rows = int.Parse(label.Groups[1].Value, CultureInfo.InvariantCulture);
            int cols = int.Parse(label.Groups[2].Value, CultureInfo.InvariantCulture);
            Assert.Equal(rows, cols);
            Assert.True(rows >= 21, $"QR version 1 is 21 modules; got {rows}");
            Assert.Equal(1, rows % 4);                 // every QR version is 17 + 4v modules

            var dark = RectsOf(cs);
            float size = new BarcodeOptions().Size;
            Assert.All(dark, r => Assert.Equal(size / rows, r.W, 3));
            Assert.InRange(dark.Count, rows * 2, rows * cols);

            // Same length → same version → same grid. Different bytes must move modules.
            string other = ContentStreamOf(
                PdfBarcodeWriter.GeneratePdf("https://example.net", BarcodeType.QrCode));
            Assert.Contains($"({rows}x{cols})", other, StringComparison.Ordinal);
            Assert.NotEqual(ModulePositions(cs), ModulePositions(other));

            // Rendered, the symbol occupies the square it was asked for.
            using var bmp = PixelAssert.Render(pdf);
            AssertInkFillsSquare(bmp, size);
        }

        [Fact]
        public void GenerateContentStream_NullData_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PdfBarcodeWriter.GenerateContentStream(null, BarcodeType.Code128, 0, 0));
        }

        [Fact]
        public void BarcodeOptions_Defaults_AreReasonable()
        {
            var opts = new BarcodeOptions();
            Assert.True(opts.ModuleWidth > 0);
            Assert.True(opts.Height > 0);
            Assert.True(opts.QuietZone >= 0);
            Assert.True(opts.Size > 0);
            Assert.True(opts.ShowText);
        }

        /// <summary>
        /// A long payload must be encoded in full, one 11-module Code 128 symbol per character.
        /// Removing one character has to shorten the painted symbol by exactly one 11-module
        /// pitch — an oracle that does not depend on the code table, and that a generator which
        /// silently truncated or clamped the payload cannot satisfy.
        /// </summary>
        /// <remarks>
        /// <b>The total span moved by two modules when Stop was corrected, and the per-character
        /// pitch did not.</b> Stop is 13 modules, not 11: it carries the bar that terminates the
        /// symbol in dark, and it was shipping without it. The relative assertion below — one
        /// character in, one 11-module pitch out — is untouched by that fix and is this test's
        /// actual claim, which is why the name still holds. The absolute span is derived from the
        /// layout rather than pinned as a number, so it cannot silently absorb the next such change.
        /// </remarks>
        [Fact]
        public void GeneratePdf_Code128_LongString_EncodesEachCharacterAsAnElevenModuleSymbol()
        {
            const string data = "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789";   // 37 characters
            float mw = new BarcodeOptions().ModuleWidth;

            string longCs  = ContentStreamOf(PdfBarcodeWriter.GeneratePdf(data, BarcodeType.Code128));
            string shortCs = ContentStreamOf(
                PdfBarcodeWriter.GeneratePdf(data.Substring(0, data.Length - 1), BarcodeType.Code128));

            var longBars  = RectsOf(longCs);
            var shortBars = RectsOf(shortCs);

            // Start-B is always the first symbol and Stop always the last, so the leading and
            // trailing bar sit at fixed offsets and the span difference is the dropped symbol.
            float longSpan  = longBars[longBars.Count - 1].X   - longBars[0].X;
            float shortSpan = shortBars[shortBars.Count - 1].X - shortBars[0].X;
            Assert.Equal(11 * mw, longSpan - shortSpan, 3);

            // Start + 37 data + check = 39 characters at 11 modules each, then Stop at 13. The
            // symbol's very last module is Stop's terminating bar, so the left edge of the last
            // rectangle sits one module short of the end.
            const int Characters = 1 + 37 + 1;
            int lastBarOffset = (Characters * Code128Symbols.CharacterModules)
                              + Code128Symbols.StopModules - 1;
            Assert.Equal(lastBarOffset, (int)MathF.Round(longSpan / mw));
            Assert.Contains("(" + data + ") Tj", longCs, StringComparison.Ordinal);
        }

        [Fact]
        public void GenerateContentStream_Code128_ContainsRectangles()
        {
            string cs = PdfBarcodeWriter.GenerateContentStream("HELLO", BarcodeType.Code128, 10f, 400f);
            // PDF path rectangle operator
            Assert.Contains(" re", cs);
        }

        /// <summary>
        /// DataMatrix must emit an ECC-200 square module grid at the requested size whose modules
        /// depend on the payload. Two same-length payloads select the same symbol size, so any
        /// difference in module positions comes from the encoding, not from the geometry.
        /// </summary>
        [Fact]
        public void GeneratePdf_DataMatrix_PaintsASquareModuleGridThatDependsOnThePayload()
        {
            byte[] pdf = PdfBarcodeWriter.GeneratePdf("TEST123", BarcodeType.DataMatrix);
            string cs  = ContentStreamOf(pdf);

            var label = Regex.Match(cs, @"% DataMatrix \((\d+)x(\d+)\)");
            Assert.True(label.Success, "content stream carries no DataMatrix label:\n" + Head(cs));

            int rows = int.Parse(label.Groups[1].Value, CultureInfo.InvariantCulture);
            int cols = int.Parse(label.Groups[2].Value, CultureInfo.InvariantCulture);
            Assert.Equal(rows, cols);
            Assert.True(rows >= 10, $"the smallest ECC-200 square symbol is 10 modules; got {rows}");
            Assert.Equal(0, rows % 2);                  // ECC-200 square symbols are even-sided

            var dark = RectsOf(cs);
            float size = new BarcodeOptions().Size;
            Assert.All(dark, r => Assert.Equal(size / rows, r.W, 3));
            Assert.InRange(dark.Count, rows, rows * cols);

            string other = ContentStreamOf(
                PdfBarcodeWriter.GeneratePdf("TEST124", BarcodeType.DataMatrix));
            Assert.Contains($"({rows}x{cols})", other, StringComparison.Ordinal);
            Assert.NotEqual(ModulePositions(cs), ModulePositions(other));

            using var bmp = PixelAssert.Render(pdf);
            AssertInkFillsSquare(bmp, size);
        }

        [Fact]
        public void GenerateContentStream_Ean13_Contains95Modules()
        {
            // 12 digits → auto-compute check digit; result must not throw
            string cs = PdfBarcodeWriter.GenerateContentStream("590123412345", BarcodeType.Ean13, 10f, 400f);
            Assert.False(string.IsNullOrWhiteSpace(cs));
        }

        [Fact]
        public void GenerateContentStream_QrCode_EmitsRealQrModules()
        {
            string cs = PdfBarcodeWriter.GenerateContentStream("QRTEST", BarcodeType.QrCode, 10f, 400f);
            Assert.Contains("QR Code", cs, StringComparison.Ordinal);
            // Real QR matrices are rendered as a grid of filled rects; the finder patterns
            // alone guarantee dozens of ` re` tokens. Count occurrences of the literal.
            int rectCount = 0, idx = 0;
            while ((idx = cs.IndexOf(" re", idx, StringComparison.Ordinal)) >= 0) { rectCount++; idx += 3; }
            Assert.True(rectCount > 20, $"Expected many `re` ops for real QR; got {rectCount}.");
        }

        /// <summary>
        /// Every field of <see cref="BarcodeOptions"/> that the caller sets has to reach the
        /// output: each bar is <c>ModuleWidth</c> wide and <c>Height</c> tall, and
        /// <c>ShowText = false</c> means there is no text object at all. A generator that ignored
        /// the options object entirely still returns a PDF and still throws nothing.
        /// </summary>
        [Fact]
        public void GeneratePdf_Code128_WithOptions_HonoursModuleWidthHeightAndShowText()
        {
            var opts = new BarcodeOptions { ModuleWidth = 3f, Height = 80f, ShowText = false };
            byte[] pdf = PdfBarcodeWriter.GeneratePdf("OPTS", BarcodeType.Code128, opts);
            string cs  = ContentStreamOf(pdf);

            var bars = RectsOf(cs);
            Assert.All(bars, r =>
            {
                Assert.Equal(3f,  r.W, 3);
                Assert.Equal(80f, r.H, 3);
            });

            Assert.DoesNotContain("BT", cs, StringComparison.Ordinal);
            Assert.DoesNotContain("Tj", cs, StringComparison.Ordinal);

            // The same payload at the default 2pt module is exactly 2/3 as wide.
            var atDefault = RectsOf(ContentStreamOf(
                PdfBarcodeWriter.GeneratePdf("OPTS", BarcodeType.Code128)));
            float wideSpan  = bars[bars.Count - 1].X - bars[0].X;
            float defaultSpan = atDefault[atDefault.Count - 1].X - atDefault[0].X;
            Assert.Equal(2f / 3f, defaultSpan / wideSpan, 3);

            // …and the rendered bars really are 80pt tall, with nothing printed beneath them.
            using var bmp = PixelAssert.Render(pdf);
            var box = PixelAssert.InkBounds(bmp);
            Assert.NotNull(box);
            Assert.InRange(box!.Value.bottom - box.Value.top + 1, 79, 82);
        }

        [Fact]
        public void GeneratePdf_NullData_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => PdfBarcodeWriter.GeneratePdf(null, BarcodeType.Code128));
        }

        /// <summary>
        /// The <c>PdfOperations</c> entry point is a facade over <see cref="PdfBarcodeWriter"/>,
        /// so it must produce byte-identical output. Comparing the two is what would catch the
        /// facade drifting to different defaults; "returns bytes" would not.
        /// </summary>
        [Fact]
        public void PdfOperations_GenerateBarcodePdf_MatchesTheDirectWriter()
        {
            byte[] viaFacade = PdfOperations.GenerateBarcodePdf("HELLO", BarcodeType.Code128);
            byte[] viaDirect = PdfBarcodeWriter.GeneratePdf("HELLO", BarcodeType.Code128);

            Assert.Equal(viaDirect, viaFacade);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(viaFacade, 0, 5));
        }

        [Fact]
        public void PdfOperations_GenerateBarcodeContentStream_ReturnsString()
        {
            string cs = PdfOperations.GenerateBarcodeContentStream("HELLO", BarcodeType.Code128, 10f, 400f);
            Assert.False(string.IsNullOrWhiteSpace(cs));
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        //
        // GeneratePdf writes one page with a single uncompressed content stream, so the drawing
        // operators the writer produced can be read straight back out of the file. Asserting on
        // those — or on decoded pixels — is what distinguishes "a barcode was drawn" from
        // "a PDF came back".

        private static string ContentStreamOf(byte[] pdf)
        {
            string all = Encoding.Latin1.GetString(pdf);
            int start = all.IndexOf("stream\n", StringComparison.Ordinal);
            Assert.True(start >= 0, "generated PDF has no content stream");
            start += "stream\n".Length;
            int end = all.IndexOf("\nendstream", start, StringComparison.Ordinal);
            Assert.True(end > start, "generated PDF has an unterminated content stream");
            return all.Substring(start, end - start);
        }

        private static readonly Regex RectOp =
            new(@"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");

        /// <summary>Every <c>x y w h re</c> rectangle in a content stream, in emission order.</summary>
        private static List<(float X, float Y, float W, float H)> RectsOf(string cs)
        {
            var rects = new List<(float, float, float, float)>();
            foreach (Match m in RectOp.Matches(cs))
                rects.Add((F(m.Groups[1].Value), F(m.Groups[2].Value),
                           F(m.Groups[3].Value), F(m.Groups[4].Value)));
            Assert.True(rects.Count > 0, "no bars were painted:\n" + Head(cs));
            return rects;

            static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>The dark-module coordinates of a 2-D symbol, as a comparable string.</summary>
        private static string ModulePositions(string cs) =>
            string.Join(";", RectsOf(cs).Select(r =>
                r.X.ToString("F2", CultureInfo.InvariantCulture) + "," +
                r.Y.ToString("F2", CultureInfo.InvariantCulture)));

        /// <summary>
        /// A 2-D symbol is drawn into the square side it was given. The lower bound is loose by
        /// one module because the outermost row/column is not guaranteed to be dark.
        /// </summary>
        private static void AssertInkFillsSquare(SkiaSharp.SKBitmap bmp, float sidePoints)
        {
            var box = PixelAssert.InkBounds(bmp);
            Assert.NotNull(box);

            int w = box!.Value.right - box.Value.left + 1;
            int h = box.Value.bottom - box.Value.top + 1;
            int lo = (int)(sidePoints * 0.85f), hi = (int)(sidePoints * 1.03f);

            Assert.InRange(w, lo, hi);
            Assert.InRange(h, lo, hi);
        }

        private static string Head(string s) => s.Length <= 200 ? s : s.Substring(0, 200);

        /// <summary>
        /// The page's Helvetica resource must declare <c>/WinAnsiEncoding</c>, because the
        /// content-stream bytes are written as Latin-1.
        /// </summary>
        /// <remarks>
        /// It declared no <c>/Encoding</c> at all, so a conforming reader falls back to the
        /// font's BUILT-IN encoding (ISO 32000-1 §9.6.6.1) -- StandardEncoding for Helvetica,
        /// which puts <c>quoteright</c> at 0x27 and <c>quoteleft</c> at 0x60 where Latin-1 has the
        /// straight <c>quotesingle</c> and <c>grave</c>. Code 128 subset B accepts every byte from
        /// 32 to 127, so a payload containing an apostrophe produced a caption whose apostrophe is
        /// a curly closing quote above bars encoding a straight one. The sibling generator,
        /// <c>SvgToPdfConverter</c>, emitted the identical page skeleton WITH the entry.
        ///
        /// <para><b>Asserted on the emitted dictionary, and that is a deliberate retreat.</b> The
        /// first version of this test extracted the caption text and a second one compared
        /// rendered pixels; BOTH survived the mutation, because neither of this library's own
        /// consumers distinguishes the two encodings -- the extractor falls back to Latin-1 and
        /// the renderer draws byte-identical glyphs (measured: 0 differing pixels for both 0x27
        /// and 0x60). The defect is real for readers that follow §9.6.6.1, and invisible to us.
        /// That leniency is exactly why nothing caught it, and it is worth knowing that a future
        /// encoding defect in generated output cannot be caught by round-tripping through this
        /// library either.</para>
        /// </remarks>
        [Fact]
        public void GeneratePdf_DeclaresWinAnsiEncodingForTheCaptionFont()
        {
            string pdf = Encoding.Latin1.GetString(
                PdfBarcodeWriter.GeneratePdf("O'BRIEN", BarcodeType.Code128));

            int f1 = pdf.IndexOf("/F1", StringComparison.Ordinal);
            Assert.True(f1 >= 0, "no /F1 font resource was written");
            string fontDict = pdf.Substring(f1, Math.Min(200, pdf.Length - f1));

            Assert.Contains("/BaseFont /Helvetica", fontDict);
            Assert.Contains("/Encoding /WinAnsiEncoding", fontDict);
        }

        /// <summary>
        /// The two generators that emit this page skeleton must agree about it. They are one
        /// implementation now (<c>Base.Operations.SinglePageDocumentWriter</c>), and this is what
        /// makes that a claim rather than a coincidence: the encoding parameter has no default,
        /// so a caller cannot inherit one by omission the way this pair did.
        /// </summary>
        [Fact]
        public void TheBarcodeAndSvgGeneratorsDeclareTheSameFontEncoding()
        {
            string barcode = Encoding.Latin1.GetString(
                PdfBarcodeWriter.GeneratePdf("HELLO", BarcodeType.Code128));

            string svg = Encoding.Latin1.GetString(SvgToPdfConverter.Convert(
                "<svg xmlns='http://www.w3.org/2000/svg'><text x='10' y='20'>hi</text></svg>"));

            Assert.Contains("/BaseFont /Helvetica /Encoding /WinAnsiEncoding", barcode);
            Assert.Contains("/BaseFont /Helvetica /Encoding /WinAnsiEncoding", svg);
        }
    }
}
