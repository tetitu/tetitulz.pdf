// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>Barcode format selector.</summary>
    public enum BarcodeType
    {
        /// <summary>Code 128 Subset B (ASCII 32-127).</summary>
        Code128,
        /// <summary>QR Code (ISO/IEC 18004), byte-mode, error-correction level M.
        /// Supports input up to ~213 bytes (version 10, 57×57 modules).</summary>
        QrCode,
        /// <summary>EAN-13 linear barcode (12 digits + check digit).</summary>
        Ean13,
        /// <summary>DataMatrix 2-D barcode (ECC-200) rendered via
        /// <see cref="DataMatrixEncoder"/>. Supports ASCII input.</summary>
        DataMatrix,
    }

    /// <summary>Options controlling barcode rendering.</summary>
    public sealed class BarcodeOptions
    {
        /// <summary>Points per module for linear barcodes (Code 128, EAN-13).</summary>
        public float ModuleWidth { get; set; } = 2f;

        /// <summary>Height in points for linear barcodes.</summary>
        public float Height { get; set; } = 50f;

        /// <summary>Quiet zone in points around the barcode.</summary>
        public float QuietZone { get; set; } = 10f;

        /// <summary>Side length in points for 2-D barcodes (QR Code, DataMatrix).</summary>
        public float Size { get; set; } = 100f;

        /// <summary>Whether to render human-readable text below the barcode.</summary>
        public bool ShowText { get; set; } = true;

        /// <summary>
        /// Whether a DataMatrix takes one of ECC-200's rectangular sizes (8×18 to 16×48, at most 49
        /// data codewords) rather than a square one. <see cref="Size"/> is then its width.
        /// </summary>
        public bool DataMatrixRectangular { get; set; }
    }

    /// <summary>
    /// Pure-managed barcode generator: PDF content-stream snippets and complete single-page PDF
    /// files for Code 128 B, EAN-13, QR Code and Data Matrix.
    /// </summary>
    /// <remarks>
    /// A snippet with <see cref="BarcodeOptions.ShowText"/> set draws its caption in font resource
    /// <c>/F1</c>, which the page it is placed on must supply; <see cref="GeneratePdf"/> does.
    /// </remarks>
    public static class PdfBarcodeWriter
    {
        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a PDF content-stream snippet that draws the barcode at (x, y).
        /// </summary>
        public static string GenerateContentStream(string data, BarcodeType type,
            float x, float y, BarcodeOptions options = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            options ??= new BarcodeOptions();
            // The origin and every option reach the content stream as numbers; a NaN among them
            // was written as 0, and a bar of module width 0 is no bar — a code no scanner reads,
            // returned as a success.
            NumericArguments.RequireFinite(x, nameof(x));
            NumericArguments.RequireFinite(y, nameof(y));
            NumericArguments.RequirePositive(options.ModuleWidth, nameof(options), nameof(options.ModuleWidth));
            NumericArguments.RequirePositive(options.Height, nameof(options), nameof(options.Height));
            NumericArguments.RequireNonNegative(options.QuietZone, nameof(options), nameof(options.QuietZone));
            NumericArguments.RequirePositive(options.Size, nameof(options), nameof(options.Size));

            return type switch
            {
                BarcodeType.Code128   => Code128ContentStream(data, x, y, options),
                BarcodeType.Ean13     => Ean13ContentStream(data, x, y, options),
                BarcodeType.QrCode    => MatrixContentStream(QrCodeEncoder.Encode(data), x, y, options, "QR Code"),
                BarcodeType.DataMatrix => MatrixContentStream(
                    DataMatrixEncoder.Encode(data, options.DataMatrixRectangular), x, y, options, "DataMatrix"),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }

        /// <summary>Returns a complete single-page PDF file containing the barcode.</summary>
        public static byte[] GeneratePdf(string data, BarcodeType type, BarcodeOptions options = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            options ??= new BarcodeOptions();

            float pageW = PdfPageSizes.A4Width, pageH = PdfPageSizes.A4Height;
            float x = options.QuietZone;
            float y = pageH / 2f;

            string cs = GenerateContentStream(data, type, x, y, options);
            return BuildPdf(cs, pageW, pageH);
        }

        // ── Code 128 B ────────────────────────────────────────────────────────


        private static string Code128ContentStream(string data, float x, float y, BarcodeOptions opt)
        {
            // Encode in Code 128 Subset B
            // StartB value = 104; each char value = ASCII - 32; check digit; stop = 106
            var values = new List<int> { Code128Symbols.StartB };

            int checksum = Code128Symbols.StartB;
            for (int i = 0; i < data.Length; i++)
            {
                int charVal = data[i] - Code128Symbols.SubsetBFirstAscii;
                if (charVal < 0 || charVal > 95)
                    throw new ArgumentException($"Code 128 Subset B: character '{data[i]}' (ASCII {(int)data[i]}) is not supported. Only ASCII {Code128Symbols.SubsetBFirstAscii}-{Code128Symbols.SubsetBLastAscii} are allowed.");
                values.Add(charVal);
                checksum += (i + 1) * charVal;
            }

            values.Add(checksum % Code128Symbols.ChecksumModulus);
            values.Add(Code128Symbols.Stop);

            // Expanded to modules rather than drawn per pattern, because Stop is 13 modules and
            // every other character is 11 -- a loop over a fixed eleven bits silently dropped its
            // terminating bar, and the symbol ended in white.
            var modules = new List<bool>();
            foreach (int value in values)
            {
                int pattern = Code128Symbols.Pattern(value);
                for (int bit = Code128Symbols.ModulesOf(value) - 1; bit >= 0; bit--)
                    modules.Add(((pattern >> bit) & 1) == 1);
            }

            var sb = new StringBuilder();
            sb.AppendLine("% Code 128 B barcode");
            sb.AppendLine("q");
            sb.AppendLine("0 0 0 rg"); // black fill

            float curX = x + opt.QuietZone;

            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i])
                {
                    // Draw filled rectangle for bar
                    sb.AppendLine(Rect(curX, y, opt.ModuleWidth, opt.Height));
                    sb.AppendLine("f");
                }
                curX += opt.ModuleWidth;
            }

            if (opt.ShowText)
            {
                float textX = x + opt.QuietZone;
                float textY = y - 12f;
                sb.AppendLine("BT");
                sb.AppendLine("/F1 8 Tf");
                sb.AppendLine(Pt(textX) + " " + Pt(textY) + " Td");
                sb.AppendLine("(" + EscapePdf(data) + ") Tj");
                sb.AppendLine("ET");
            }

            sb.AppendLine("Q");
            return sb.ToString();
        }

        // ── EAN-13 ────────────────────────────────────────────────────────────





        private static string Ean13ContentStream(string data, float x, float y, BarcodeOptions opt)
        {
            // Accept 12 or 13 ASCII digits; separators such as '-' and ' ' are dropped. ASCII
            // only: char.IsDigit took Arabic-Indic and full-width digits, whose `c - '0'` then
            // indexed the symbol tables in the thousands.
            string digits = new string(Array.FindAll(data.ToCharArray(), AsciiDigits.Is));
            if (digits.Length == 12)
            {
                digits += EanSymbols.CheckDigit(digits).ToString(CultureInfo.InvariantCulture);
            }
            else if (digits.Length == 13)
            {
                // A supplied check digit is checked, not trusted. Encoded as given, a wrong one made
                // a symbol every scanner refuses — this library's own reader included — from a call
                // that returned normally.
                int expected = EanSymbols.CheckDigit(digits);
                if (digits[12] - '0' != expected)
                    throw new ArgumentException(
                        $"EAN-13 check digit {digits[12]} is wrong for {digits.Substring(0, 12)}; it should be " +
                        $"{expected}. Pass the first twelve digits to have it computed.",
                        nameof(data));
            }
            if (digits.Length != 13)
                throw new ArgumentException("EAN-13 requires exactly 12 or 13 digits.");

            int[] d = new int[13];
            for (int i = 0; i < 13; i++) d[i] = digits[i] - '0';

            int firstDigit = d[0];
            int parityPattern = EanSymbols.FirstDigitParity(firstDigit);

            // Build module list: quiet(9) + guard(3) + 6*7 + centre(5) + 6*7 + guard(3) + quiet(9) = 95 modules
            var modules = new List<bool>();
            // Left quiet zone (not drawn as bars, just space — we advance curX)
            // Guard bars: 101
            modules.Add(true); modules.Add(false); modules.Add(true);
            // Left 6 digits
            for (int i = 1; i <= 6; i++)
            {
                bool useG = ((parityPattern >> (6 - i)) & 1) == 1;
                int pattern = useG ? EanSymbols.LeftEven(d[i]) : EanSymbols.LeftOdd(d[i]);
                for (int bit = EanSymbols.DigitModules - 1; bit >= 0; bit--)
                    modules.Add(((pattern >> bit) & 1) == 1);
            }
            // Centre guard: 01010
            modules.Add(false); modules.Add(true); modules.Add(false); modules.Add(true); modules.Add(false);
            // Right 6 digits
            for (int i = 7; i <= 12; i++)
            {
                int pattern = EanSymbols.Right(d[i]);
                for (int bit = EanSymbols.DigitModules - 1; bit >= 0; bit--)
                    modules.Add(((pattern >> bit) & 1) == 1);
            }
            // Right guard: 101
            modules.Add(true); modules.Add(false); modules.Add(true);

            var sb = new StringBuilder();
            sb.AppendLine("% EAN-13 barcode");
            sb.AppendLine("q");
            sb.AppendLine("0 0 0 rg");

            float curX = x + opt.QuietZone;
            float mw = opt.ModuleWidth;

            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i])
                {
                    sb.AppendLine(Rect(curX, y, mw, opt.Height));
                    sb.AppendLine("f");
                }
                curX += mw;
            }

            if (opt.ShowText)
            {
                sb.AppendLine("BT");
                sb.AppendLine("/F1 8 Tf");
                sb.AppendLine(Pt(x + opt.QuietZone) + " " + Pt(y - 12f) + " Td");
                sb.AppendLine("(" + digits + ") Tj");
                sb.AppendLine("ET");
            }

            sb.AppendLine("Q");
            return sb.ToString();
        }

        // ── 2-D matrix rendering ────────────────────────────────────────────

        /// <summary>Renders a boolean matrix of modules as PDF content ops.</summary>
        private static string MatrixContentStream(bool[,] matrix, float x, float y,
            BarcodeOptions opt, string label)
        {
            int rows = matrix.GetLength(0);
            int cols = matrix.GetLength(1);
            float mod = opt.Size / Math.Max(rows, cols);

            // The quiet zone the linear symbols leave before their first bar, applied here too, or a
            // QR at GeneratePdf's x = QuietZone would meet the page edge two modules out, where
            // ISO/IEC 18004 asks for four.
            x += opt.QuietZone;

            var sb = new StringBuilder();
            sb.AppendLine($"% {label} ({rows}x{cols})");
            sb.AppendLine("q");
            sb.AppendLine("0 0 0 rg"); // black fill for dark modules
            for (int ry = 0; ry < rows; ry++)
            {
                for (int cx = 0; cx < cols; cx++)
                {
                    if (!matrix[ry, cx]) continue;
                    float mx = x + cx * mod;
                    // PDF y grows upward; rows start at top of matrix
                    float my = y + (rows - 1 - ry) * mod;
                    sb.AppendLine(Rect(mx, my, mod, mod));
                    sb.AppendLine("f");
                }
            }
            sb.AppendLine("Q");
            return sb.ToString();
        }

        // ── PDF builder ───────────────────────────────────────────────────────

        /// <summary>
        /// The page skeleton, through the one implementation in
        /// <see cref="SinglePageDocumentWriter"/>.
        /// </summary>
        /// <remarks>
        /// <b>/WinAnsiEncoding is declared on purpose.</b> With no /Encoding the caption would
        /// resolve through Helvetica's built-in StandardEncoding while the bytes are written as
        /// Latin-1. The two agree over most of ASCII but NOT at 0x27 and
        /// 0x60: StandardEncoding puts quoteright and quoteleft there, WinAnsi the straight
        /// quotesingle and grave. Code 128 subset B accepts every byte from 32 to 127, so a
        /// caption containing an apostrophe would print a curly closing quote underneath a barcode
        /// that encodes a straight one -- the human-readable line disagreeing with the machine-
        /// readable data, which is the one thing a barcode caption exists to prevent.
        /// </remarks>
        private static byte[] BuildPdf(string contentStream, float pageW, float pageH)
            => SinglePageDocumentWriter.Build(
                contentStream,
                $"[0 0 {Pt(pageW)} {Pt(pageH)}]",
                fontEncoding: "/WinAnsiEncoding");

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Rect(float x, float y, float w, float h) =>
            $"{Pt(x)} {Pt(y)} {Pt(w)} {Pt(h)} re";

        private static string Pt(float v) =>
            // Non-finite goes to "0" instead of the bare keyword `NaN` / `Infinity`, which
            // is not a §7.3.3 number and can cost a strict reader the whole content stream.
            // The PRECISION here is unchanged and deliberate; only the finiteness rule is shared.
            PdfRealFormat.Format(v, "F4");

        /// <summary>
        /// §7.3.4.2 literal-string escaping, through the single implementation in Core.
        /// </summary>
        /// <remarks>
        /// Besides leaving control bytes raw, the three-<c>Replace</c> chain this replaces threw
        /// <see cref="System.NullReferenceException"/> on a null caption; the shared helper returns
        /// the empty string, which is the contract the other copies already had.
        /// </remarks>
        private static string EscapePdf(string s) => Core.Objects.PdfStringEscape.EscapeLiteral(s);

    }
}
