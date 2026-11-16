// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    internal static partial class StandardEncodings
    {
        // WinAnsiEncoding: maps character codes 0-255 to Unicode code points
        private static readonly Dictionary<int, char> _winAnsi = BuildWinAnsiEncoding();
        private static readonly Dictionary<int, char> _macRoman = BuildMacRomanEncoding();
        private static readonly Dictionary<int, char> _cp1255 = BuildCp1255Encoding();
        private static readonly Dictionary<int, char> _iso8859_8 = BuildIso8859_8Encoding();
        private static readonly Dictionary<int, char> _adobeStandard = BuildAdobeStandardEncoding();
        private static readonly Dictionary<int, char> _symbol = BuildSymbolEncoding();
        private static readonly Dictionary<int, char> _zapfDingbats = BuildZapfDingbatsEncoding();
        private static readonly Dictionary<int, char> _macExpert = BuildMacExpertEncoding();

        public static Dictionary<int, char> WinAnsiEncoding => _winAnsi;
        public static Dictionary<int, char> MacRomanEncoding => _macRoman;
        public static Dictionary<int, char> Cp1255Encoding => _cp1255;
        public static Dictionary<int, char> Iso8859_8Encoding => _iso8859_8;
        public static Dictionary<int, char> AdobeStandardEncoding => _adobeStandard;
        public static Dictionary<int, char> SymbolEncoding => _symbol;
        public static Dictionary<int, char> ZapfDingbatsEncoding => _zapfDingbats;
        public static Dictionary<int, char> MacExpertEncoding => _macExpert;

        public static char GetWinAnsiChar(int code)
        {
            if (_winAnsi.TryGetValue(code, out char c)) return c;
            return code < 256 ? (char)code : '\uFFFD';
        }

        private static Dictionary<char, byte> _winAnsiCodes;

        /// <summary>
        /// The WinAnsiEncoding code for <paramref name="c"/>, the inverse of
        /// <see cref="WinAnsiEncoding"/>; <c>false</c> for a character the encoding does not hold.
        /// </summary>
        /// <remarks>
        /// <b>Not Latin-1.</b> WinAnsi agrees with Latin-1 from 0xA0 up but puts the euro sign, the
        /// curly quotes and the dashes at 0x80-0x9F, where Latin-1 has control codes. Writing text for
        /// a <c>/WinAnsiEncoding</c> font through a Latin-1 encoder turns those into '?' and lets
        /// U+0080-U+009F through as the glyphs WinAnsi puts there.
        /// </remarks>
        public static bool TryGetWinAnsiCode(char c, out byte code)
        {
            var map = _winAnsiCodes;
            if (map == null)
            {
                map = new Dictionary<char, byte>();
                for (int i = 0; i < 256; i++)
                    if (_winAnsi.TryGetValue(i, out char u) && !map.ContainsKey(u)) map[u] = (byte)i;
                _winAnsiCodes = map;
            }
            return map.TryGetValue(c, out code);
        }

        /// <summary>
        /// Resolves an encoding name to its character map.
        /// The one resolver, for PdfType1Font and PdfTrueTypeFont alike.
        /// </summary>
        public static Dictionary<int, char> GetEncodingByName(string name)
        {
            return name switch
            {
                "WinAnsiEncoding" => WinAnsiEncoding,
                "MacRomanEncoding" => MacRomanEncoding,
                "MacExpertEncoding" => MacExpertEncoding,
                "StandardEncoding" => AdobeStandardEncoding,
                "PDFDocEncoding" => WinAnsiEncoding,
                "CP1255" or "Windows-1255" or "Win1255Encoding" or "WinHebrewEncoding" => Cp1255Encoding,
                "ISOLatin8Encoding" or "ISO-8859-8" or "ISO88598Encoding" => Iso8859_8Encoding,
                _ => WinAnsiEncoding,
            };
        }

        public static char GetMacRomanChar(int code)
        {
            if (_macRoman.TryGetValue(code, out char c)) return c;
            return code < 256 ? (char)code : '\uFFFD';
        }

        private static Dictionary<int, char> BuildWinAnsiEncoding()
        {
            var map = new Dictionary<int, char>();
            // Standard ASCII range maps directly
            for (int i = 0; i < 128; i++)
                map[i] = (char)i;

            // WinAnsi differences from Latin-1 in 0x80-0x9F range
            map[0x80] = '\u20AC'; // Euro sign
            map[0x82] = '\u201A'; // Single low-9 quotation mark
            map[0x83] = '\u0192'; // Latin small letter f with hook
            map[0x84] = '\u201E'; // Double low-9 quotation mark
            map[0x85] = '\u2026'; // Horizontal ellipsis
            map[0x86] = '\u2020'; // Dagger
            map[0x87] = '\u2021'; // Double dagger
            map[0x88] = '\u02C6'; // Modifier letter circumflex accent
            map[0x89] = '\u2030'; // Per mille sign
            map[0x8A] = '\u0160'; // Latin capital letter S with caron
            map[0x8B] = '\u2039'; // Single left-pointing angle quotation mark
            map[0x8C] = '\u0152'; // Latin capital ligature OE
            map[0x8E] = '\u017D'; // Latin capital letter Z with caron
            map[0x91] = '\u2018'; // Left single quotation mark
            map[0x92] = '\u2019'; // Right single quotation mark
            map[0x93] = '\u201C'; // Left double quotation mark
            map[0x94] = '\u201D'; // Right double quotation mark
            map[0x95] = '\u2022'; // Bullet
            map[0x96] = '\u2013'; // En dash
            map[0x97] = '\u2014'; // Em dash
            map[0x98] = '\u02DC'; // Small tilde
            map[0x99] = '\u2122'; // Trade mark sign
            map[0x9A] = '\u0161'; // Latin small letter s with caron
            map[0x9B] = '\u203A'; // Single right-pointing angle quotation mark
            map[0x9C] = '\u0153'; // Latin small ligature oe
            map[0x9E] = '\u017E'; // Latin small letter z with caron
            map[0x9F] = '\u0178'; // Latin capital letter Y with diaeresis

            // 0xA0-0xFF map to Unicode Latin-1 Supplement directly
            for (int i = 0xA0; i <= 0xFF; i++)
                map[i] = (char)i;

            return map;
        }

        private static Dictionary<int, char> BuildMacRomanEncoding()
        {
            var map = new Dictionary<int, char>();
            // Standard ASCII range
            for (int i = 0; i < 128; i++)
                map[i] = (char)i;

            // MacRoman high byte mappings
            map[0x80] = '\u00C4'; map[0x81] = '\u00C5'; map[0x82] = '\u00C7'; map[0x83] = '\u00C9';
            map[0x84] = '\u00D1'; map[0x85] = '\u00D6'; map[0x86] = '\u00DC'; map[0x87] = '\u00E1';
            map[0x88] = '\u00E0'; map[0x89] = '\u00E2'; map[0x8A] = '\u00E4'; map[0x8B] = '\u00E3';
            map[0x8C] = '\u00E5'; map[0x8D] = '\u00E7'; map[0x8E] = '\u00E9'; map[0x8F] = '\u00E8';
            map[0x90] = '\u00EA'; map[0x91] = '\u00EB'; map[0x92] = '\u00ED'; map[0x93] = '\u00EC';
            map[0x94] = '\u00EE'; map[0x95] = '\u00EF'; map[0x96] = '\u00F1'; map[0x97] = '\u00F3';
            map[0x98] = '\u00F2'; map[0x99] = '\u00F4'; map[0x9A] = '\u00F6'; map[0x9B] = '\u00F5';
            map[0x9C] = '\u00FA'; map[0x9D] = '\u00F9'; map[0x9E] = '\u00FB'; map[0x9F] = '\u00FC';
            map[0xA0] = '\u2020'; map[0xA1] = '\u00B0'; map[0xA2] = '\u00A2'; map[0xA3] = '\u00A3';
            map[0xA4] = '\u00A7'; map[0xA5] = '\u2022'; map[0xA6] = '\u00B6'; map[0xA7] = '\u00DF';
            map[0xA8] = '\u00AE'; map[0xA9] = '\u00A9'; map[0xAA] = '\u2122'; map[0xAB] = '\u00B4';
            map[0xAC] = '\u00A8'; map[0xAD] = '\u2260'; map[0xAE] = '\u00C6'; map[0xAF] = '\u00D8';
            map[0xB0] = '\u221E'; map[0xB1] = '\u00B1'; map[0xB2] = '\u2264'; map[0xB3] = '\u2265';
            map[0xB4] = '\u00A5'; map[0xB5] = '\u00B5'; map[0xB6] = '\u2202'; map[0xB7] = '\u2211';
            map[0xB8] = '\u220F'; map[0xB9] = '\u03C0'; map[0xBA] = '\u222B'; map[0xBB] = '\u00AA';
            map[0xBC] = '\u00BA'; map[0xBD] = '\u03A9'; map[0xBE] = '\u00E6'; map[0xBF] = '\u00F8';
            map[0xC0] = '\u00BF'; map[0xC1] = '\u00A1'; map[0xC2] = '\u00AC'; map[0xC3] = '\u221A';
            map[0xC4] = '\u0192'; map[0xC5] = '\u2248'; map[0xC6] = '\u2206'; map[0xC7] = '\u00AB';
            map[0xC8] = '\u00BB'; map[0xC9] = '\u2026'; map[0xCA] = '\u00A0'; map[0xCB] = '\u00C0';
            map[0xCC] = '\u00C3'; map[0xCD] = '\u00D5'; map[0xCE] = '\u0152'; map[0xCF] = '\u0153';
            map[0xD0] = '\u2013'; map[0xD1] = '\u2014'; map[0xD2] = '\u201C'; map[0xD3] = '\u201D';
            map[0xD4] = '\u2018'; map[0xD5] = '\u2019'; map[0xD6] = '\u00F7'; map[0xD7] = '\u25CA';
            map[0xD8] = '\u00FF'; map[0xD9] = '\u0178'; map[0xDA] = '\u2044'; map[0xDB] = '\u20AC';
            map[0xDC] = '\u2039'; map[0xDD] = '\u203A'; map[0xDE] = '\uFB01'; map[0xDF] = '\uFB02';
            map[0xE0] = '\u2021'; map[0xE1] = '\u00B7'; map[0xE2] = '\u201A'; map[0xE3] = '\u201E';
            map[0xE4] = '\u2030'; map[0xE5] = '\u00C2'; map[0xE6] = '\u00CA'; map[0xE7] = '\u00C1';
            map[0xE8] = '\u00CB'; map[0xE9] = '\u00C8'; map[0xEA] = '\u00CD'; map[0xEB] = '\u00CE';
            map[0xEC] = '\u00CF'; map[0xED] = '\u00CC'; map[0xEE] = '\u00D3'; map[0xEF] = '\u00D4';
            map[0xF0] = '\uF8FF'; map[0xF1] = '\u00D2'; map[0xF2] = '\u00DA'; map[0xF3] = '\u00DB';
            map[0xF4] = '\u00D9'; map[0xF5] = '\u0131'; map[0xF6] = '\u02C6'; map[0xF7] = '\u02DC';
            map[0xF8] = '\u00AF'; map[0xF9] = '\u02D8'; map[0xFA] = '\u02D9'; map[0xFB] = '\u02DA';
            map[0xFC] = '\u00B8'; map[0xFD] = '\u02DD'; map[0xFE] = '\u02DB'; map[0xFF] = '\u02C7';

            return map;
        }

        // Adobe Glyph Name to Unicode mapping (most common glyphs)
        private static readonly Dictionary<string, char> _glyphToUnicode = BuildGlyphMap();

        /// <summary>
        /// The Adobe Glyph List <b>numeric</b> glyph-name forms — <c>uniXXXX</c> and
        /// <c>uXXXX</c>..<c>uXXXXXX</c> — as a full Unicode code point, which may be outside the BMP.
        /// </summary>
        /// <remarks>
        /// <para><b>The single rule for "is this an AGL numeric name, and which code point is it".</b>
        /// <see cref="TryGetGlyphUnicode"/> and <c>FontCache.BuildUnicodeToGidFromNames</c> both ask
        /// it, and they are not independent — <c>FontCache.ApplyPdfEncodingOverride</c> builds a reverse index with one
        /// and looks a <c>/Differences</c> name up with the other, then joins both answers on a
        /// single <c>int</c> key — so a name one of them calls AGL and the other does not is a
        /// lookup that silently misses.</para>
        ///
        /// <para><b>Deliberately separate from the standard-name table.</b> Callers that want
        /// <c>"A"</c> to mean U+0041 go through <see cref="TryGetGlyphUnicode"/>; the reverse-index
        /// builder must <i>not</i>, because a font naming a glyph <c>"A"</c> is describing its own
        /// glyph inventory rather than declaring a Unicode mapping.</para>
        ///
        /// <para><b><c>uniXXXXYYYY</c> is refused, not truncated.</b> AGL gives that form to a code
        /// point <i>sequence</i>. Taking the first four digits — which one copy did — maps U+0041
        /// to a glyph that draws "AB", so a page asking for "A" rendered "AB". A name we cannot
        /// represent as one code point is not a name we know.</para>
        /// </remarks>
        internal static bool TryGetAglCodePoint(string glyphName, out int codePoint)
        {
            codePoint = 0;
            if (glyphName == null || glyphName.Length < 5 || glyphName[0] != 'u') return false;

            // "uniXXXX": exactly four hex digits. More digits is a SEQUENCE, not a code point.
            if (glyphName.StartsWith("uni", System.StringComparison.Ordinal))
                return glyphName.Length == 3 + 4 && TryParseHex(glyphName, 3, 4, out codePoint);

            // "uXXXX" through "uXXXXXX". The second character cannot be 'n' — that is the `uni`
            // form above — which is what keeps "union" from parsing as u + "nion".
            if (glyphName[1] == 'n') return false;

            int hexLen = glyphName.Length - 1;
            return hexLen >= 4 && hexLen <= 6
                && TryParseHex(glyphName, 1, hexLen, out codePoint)
                && codePoint <= MaxCodePoint;
        }

        /// <summary>The largest Unicode code point (U+10FFFF).</summary>
        private const int MaxCodePoint = 0x10FFFF;

        /// <summary>
        /// Hex digits at a fixed position, without allocating a substring and without
        /// <c>NumberStyles.HexNumber</c>'s tolerance for leading and trailing white space.
        /// </summary>
        private static bool TryParseHex(string s, int start, int len, out int value)
        {
            value = 0;
            if (start + len > s.Length) return false;
            for (int i = 0; i < len; i++)
            {
                char c = s[start + i];
                int digit;
                if      (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else return false;
                value = (value << 4) | digit;
            }
            return true;
        }

        /// <summary>
        /// The glyph name's code point when it fits in a single UTF-16 code unit: the standard AGL
        /// table first, then the numeric forms.
        /// </summary>
        /// <remarks>
        /// The BMP ceiling is a constraint of the <c>out char</c> return type, not a parsing rule —
        /// which is why it is applied here and not in <see cref="TryGetAglCodePoint"/>. A glyph
        /// named <c>u1F600</c> is a perfectly good AGL name; it just cannot come back through a
        /// <see cref="char"/>.
        /// </remarks>
        public static bool TryGetGlyphUnicode(string glyphName, out char unicode)
        {
            if (glyphName != null && _glyphToUnicode.TryGetValue(glyphName, out unicode))
                return true;

            if (TryGetAglCodePoint(glyphName, out int codePoint) && codePoint <= 0xFFFF)
            {
                unicode = (char)codePoint;
                return true;
            }

            unicode = default;
            return false;
        }

        private static Dictionary<int, char> BuildCp1255Encoding()
        {
            var map = new Dictionary<int, char>();
            // ASCII range
            for (int i = 0; i < 128; i++) map[i] = (char)i;
            // CP1255 high bytes (Windows Hebrew)
            map[0x80] = '\u20AC'; map[0x82] = '\u201A'; map[0x83] = '\u0192';
            map[0x84] = '\u201E'; map[0x85] = '\u2026'; map[0x86] = '\u2020';
            map[0x87] = '\u2021'; map[0x88] = '\u02C6'; map[0x89] = '\u2030';
            map[0x8B] = '\u2039'; map[0x91] = '\u2018'; map[0x92] = '\u2019';
            map[0x93] = '\u201C'; map[0x94] = '\u201D'; map[0x95] = '\u2022';
            map[0x96] = '\u2013'; map[0x97] = '\u2014'; map[0x98] = '\u02DC';
            map[0x99] = '\u2122'; map[0x9B] = '\u203A'; map[0xA0] = '\u00A0';
            map[0xA1] = '\u00A1'; map[0xA2] = '\u00A2'; map[0xA3] = '\u00A3';
            map[0xA4] = '\u20AA'; // New Shekel Sign
            map[0xA5] = '\u00A5'; map[0xA6] = '\u00A6'; map[0xA7] = '\u00A7';
            map[0xA8] = '\u00A8'; map[0xA9] = '\u00A9'; map[0xAA] = '\u00D7';
            map[0xAB] = '\u00AB'; map[0xAC] = '\u00AC'; map[0xAD] = '\u00AD';
            map[0xAE] = '\u00AE'; map[0xAF] = '\u00AF'; map[0xB0] = '\u00B0';
            map[0xB1] = '\u00B1'; map[0xB2] = '\u00B2'; map[0xB3] = '\u00B3';
            map[0xB4] = '\u00B4'; map[0xB5] = '\u00B5'; map[0xB6] = '\u00B6';
            map[0xB7] = '\u00B7'; map[0xB8] = '\u00B8'; map[0xB9] = '\u00B9';
            map[0xBA] = '\u00F7'; map[0xBB] = '\u00BB'; map[0xBC] = '\u00BC';
            map[0xBD] = '\u00BD'; map[0xBE] = '\u00BE'; map[0xBF] = '\u00BF';
            // Hebrew vowel points U+05B0-U+05C4 at 0xC0-0xD6
            map[0xC0] = '\u05B0'; map[0xC1] = '\u05B1'; map[0xC2] = '\u05B2';
            map[0xC3] = '\u05B3'; map[0xC4] = '\u05B4'; map[0xC5] = '\u05B5';
            map[0xC6] = '\u05B6'; map[0xC7] = '\u05B7'; map[0xC8] = '\u05B8';
            map[0xC9] = '\u05B9'; map[0xCB] = '\u05BB'; map[0xCC] = '\u05BC';
            map[0xCD] = '\u05BD'; map[0xCE] = '\u05BE'; map[0xCF] = '\u05BF';
            map[0xD0] = '\u05C0'; map[0xD1] = '\u05C1'; map[0xD2] = '\u05C2';
            map[0xD3] = '\u05C3'; map[0xD4] = '\u05F0'; map[0xD5] = '\u05F1';
            map[0xD6] = '\u05F2'; map[0xD7] = '\u05F3'; map[0xD8] = '\u05F4';
            // Hebrew letters U+05D0-U+05EA at 0xE0-0xFA
            map[0xE0] = '\u05D0'; // alef
            map[0xE1] = '\u05D1'; // bet
            map[0xE2] = '\u05D2'; // gimel
            map[0xE3] = '\u05D3'; // dalet
            map[0xE4] = '\u05D4'; // he
            map[0xE5] = '\u05D5'; // vav
            map[0xE6] = '\u05D6'; // zayin
            map[0xE7] = '\u05D7'; // het
            map[0xE8] = '\u05D8'; // tet
            map[0xE9] = '\u05D9'; // yod
            map[0xEA] = '\u05DA'; // final kaf
            map[0xEB] = '\u05DB'; // kaf
            map[0xEC] = '\u05DC'; // lamed
            map[0xED] = '\u05DD'; // final mem
            map[0xEE] = '\u05DE'; // mem
            map[0xEF] = '\u05DF'; // final nun
            map[0xF0] = '\u05E0'; // nun
            map[0xF1] = '\u05E1'; // samekh
            map[0xF2] = '\u05E2'; // ayin
            map[0xF3] = '\u05E3'; // final pe
            map[0xF4] = '\u05E4'; // pe
            map[0xF5] = '\u05E5'; // final tsadi
            map[0xF6] = '\u05E6'; // tsadi
            map[0xF7] = '\u05E7'; // qof
            map[0xF8] = '\u05E8'; // resh
            map[0xF9] = '\u05E9'; // shin
            map[0xFA] = '\u05EA'; // tav
            return map;
        }

        private static Dictionary<int, char> BuildIso8859_8Encoding()
        {
            var map = new Dictionary<int, char>();
            for (int i = 0; i < 128; i++) map[i] = (char)i;
            map[0xA0] = '\u00A0'; map[0xA2] = '\u00A2'; map[0xA3] = '\u00A3';
            map[0xA4] = '\u00A4'; map[0xA5] = '\u00A5'; map[0xA6] = '\u00A6';
            map[0xA7] = '\u00A7'; map[0xA8] = '\u00A8'; map[0xA9] = '\u00A9';
            map[0xAA] = '\u00D7'; map[0xAB] = '\u00AB'; map[0xAC] = '\u00AC';
            map[0xAD] = '\u00AD'; map[0xAE] = '\u00AE'; map[0xAF] = '\u00AF';
            map[0xB0] = '\u00B0'; map[0xB1] = '\u00B1'; map[0xB2] = '\u00B2';
            map[0xB3] = '\u00B3'; map[0xB4] = '\u00B4'; map[0xB5] = '\u00B5';
            map[0xB6] = '\u00B6'; map[0xB7] = '\u00B7'; map[0xB8] = '\u00B8';
            map[0xB9] = '\u00B9'; map[0xBA] = '\u00F7'; map[0xBB] = '\u00BB';
            map[0xBC] = '\u00BC'; map[0xBD] = '\u00BD'; map[0xBE] = '\u00BE';
            // Hebrew letters at 0xE0-0xFA
            map[0xE0] = '\u05D0'; map[0xE1] = '\u05D1'; map[0xE2] = '\u05D2';
            map[0xE3] = '\u05D3'; map[0xE4] = '\u05D4'; map[0xE5] = '\u05D5';
            map[0xE6] = '\u05D6'; map[0xE7] = '\u05D7'; map[0xE8] = '\u05D8';
            map[0xE9] = '\u05D9'; map[0xEA] = '\u05DA'; map[0xEB] = '\u05DB';
            map[0xEC] = '\u05DC'; map[0xED] = '\u05DD'; map[0xEE] = '\u05DE';
            map[0xEF] = '\u05DF'; map[0xF0] = '\u05E0'; map[0xF1] = '\u05E1';
            map[0xF2] = '\u05E2'; map[0xF3] = '\u05E3'; map[0xF4] = '\u05E4';
            map[0xF5] = '\u05E5'; map[0xF6] = '\u05E6'; map[0xF7] = '\u05E7';
            map[0xF8] = '\u05E8'; map[0xF9] = '\u05E9'; map[0xFA] = '\u05EA';
            return map;
        }

        // Adobe Standard Encoding (ISO 32000-1, Annex D.1).
        private static Dictionary<int, char> BuildAdobeStandardEncoding()
        {
            var map = new Dictionary<int, char>();
            // ASCII range — same as ASCII except 0x27 and 0x60
            for (int i = 0x20; i <= 0x7E; i++) map[i] = (char)i;
            map[0x27] = '\u2019'; // quoteright
            map[0x60] = '\u2018'; // quoteleft
            // High bytes (selected; undefined positions are omitted → fallback to (char)b)
            map[0xA1] = '\u00A1'; map[0xA2] = '\u00A2'; map[0xA3] = '\u00A3';
            map[0xA4] = '\u2044'; // fraction (not currency)
            map[0xA5] = '\u00A5'; map[0xA6] = '\u0192'; // florin
            map[0xA7] = '\u00A7'; map[0xA8] = '\u00A4'; // currency
            map[0xA9] = '\u0027'; // quotesingle (apostrophe)
            map[0xAA] = '\u201C'; map[0xAB] = '\u00AB';
            map[0xAC] = '\u2039'; map[0xAD] = '\u203A';
            map[0xAE] = '\uFB01'; map[0xAF] = '\uFB02'; // fi, fl ligatures
            map[0xB1] = '\u2013'; // endash
            map[0xB2] = '\u2020'; map[0xB3] = '\u2021'; // dagger, daggerdbl
            map[0xB4] = '\u00B7'; // periodcentered
            map[0xB6] = '\u00B6'; map[0xB7] = '\u2022'; // paragraph, bullet
            map[0xB8] = '\u201A'; map[0xB9] = '\u201E'; // quotesinglbase, quotedblbase
            map[0xBA] = '\u201D'; map[0xBB] = '\u00BB';
            map[0xBC] = '\u2026'; // ellipsis
            map[0xBD] = '\u2030'; // perthousand
            map[0xBF] = '\u00BF'; // questiondown
            map[0xC1] = '\u0060'; map[0xC2] = '\u00B4'; // grave, acute
            map[0xC3] = '\u02C6'; map[0xC4] = '\u02DC'; // circumflex, tilde
            map[0xC5] = '\u00AF'; map[0xC6] = '\u02D8'; // macron, breve
            map[0xC7] = '\u02D9'; map[0xC8] = '\u00A8'; // dotaccent, dieresis
            map[0xCA] = '\u02DA'; map[0xCB] = '\u00B8'; // ring, cedilla
            map[0xCD] = '\u02DD'; map[0xCE] = '\u02DB'; // hungarumlaut, ogonek
            map[0xCF] = '\u02C7'; // caron
            map[0xD0] = '\u2014'; // emdash
            map[0xE1] = '\u00C6'; map[0xE3] = '\u00AA'; // AE, ordfeminine
            map[0xE8] = '\u0141'; map[0xE9] = '\u00D8'; // Lslash, Oslash
            map[0xEA] = '\u0152'; map[0xEB] = '\u00BA'; // OE, ordmasculine
            map[0xF1] = '\u00E6'; map[0xF5] = '\u0131'; // ae, dotlessi
            map[0xF8] = '\u0142'; map[0xF9] = '\u00F8'; // lslash, oslash
            map[0xFA] = '\u0153'; map[0xFB] = '\u00DF'; // oe, germandbls
            return map;
        }

        // Symbol font encoding (Adobe Symbol Set).
        private static Dictionary<int, char> BuildSymbolEncoding()
        {
            var map = new Dictionary<int, char>();
            map[0x20] = ' ';
            map[0x21] = '!'; map[0x22] = '\u2200'; map[0x23] = '#'; map[0x24] = '\u2203';
            map[0x25] = '%'; map[0x26] = '&'; map[0x27] = '\u220B'; map[0x28] = '(';
            map[0x29] = ')'; map[0x2A] = '\u2217'; map[0x2B] = '+'; map[0x2C] = ',';
            map[0x2D] = '\u2212'; map[0x2E] = '.'; map[0x2F] = '/';
            for (int i = 0x30; i <= 0x39; i++) map[i] = (char)i; // 0-9
            map[0x3A] = ':'; map[0x3B] = ';'; map[0x3C] = '<'; map[0x3D] = '=';
            map[0x3E] = '>'; map[0x3F] = '?';
            map[0x40] = '\u2245'; // congruent
            map[0x41] = '\u0391'; map[0x42] = '\u0392'; map[0x43] = '\u03A7'; map[0x44] = '\u0394';
            map[0x45] = '\u0395'; map[0x46] = '\u03A6'; map[0x47] = '\u0393'; map[0x48] = '\u0397';
            map[0x49] = '\u0399'; map[0x4A] = '\u03D1'; map[0x4B] = '\u039A'; map[0x4C] = '\u039B';
            map[0x4D] = '\u039C'; map[0x4E] = '\u039D'; map[0x4F] = '\u039F'; map[0x50] = '\u03A0';
            map[0x51] = '\u0398'; map[0x52] = '\u03A1'; map[0x53] = '\u03A3'; map[0x54] = '\u03A4';
            map[0x55] = '\u03A5'; map[0x56] = '\u03C2'; map[0x57] = '\u03A9'; map[0x58] = '\u039E';
            map[0x59] = '\u03A8'; map[0x5A] = '\u0396';
            map[0x5B] = '['; map[0x5C] = '\u2234'; map[0x5D] = ']'; map[0x5E] = '\u22A5';
            map[0x5F] = '_'; map[0x60] = '\u203E'; // overline
            map[0x61] = '\u03B1'; map[0x62] = '\u03B2'; map[0x63] = '\u03C7'; map[0x64] = '\u03B4';
            map[0x65] = '\u03B5'; map[0x66] = '\u03C6'; map[0x67] = '\u03B3'; map[0x68] = '\u03B7';
            map[0x69] = '\u03B9'; map[0x6A] = '\u03D5'; map[0x6B] = '\u03BA'; map[0x6C] = '\u03BB';
            map[0x6D] = '\u03BC'; map[0x6E] = '\u03BD'; map[0x6F] = '\u03BF'; map[0x70] = '\u03C0';
            map[0x71] = '\u03B8'; map[0x72] = '\u03C1'; map[0x73] = '\u03C3'; map[0x74] = '\u03C4';
            map[0x75] = '\u03C5'; map[0x76] = '\u03D6'; map[0x77] = '\u03C9'; map[0x78] = '\u03BE';
            map[0x79] = '\u03C8'; map[0x7A] = '\u03B6';
            map[0x7B] = '{'; map[0x7C] = '|'; map[0x7D] = '}'; map[0x7E] = '\u223C';
            // 0x7F-0xA0: undefined
            map[0xA1] = '\u03D2'; map[0xA2] = '\u2032'; map[0xA3] = '\u2264'; map[0xA4] = '\u2044';
            map[0xA5] = '\u221E'; map[0xA6] = '\u0192'; map[0xA7] = '\u2663'; map[0xA8] = '\u2666';
            map[0xA9] = '\u2665'; map[0xAA] = '\u2660'; map[0xAB] = '\u2194'; map[0xAC] = '\u2190';
            map[0xAD] = '\u2191'; map[0xAE] = '\u2192'; map[0xAF] = '\u2193';
            map[0xB0] = '\u00B0'; map[0xB1] = '\u00B1'; map[0xB2] = '\u2033'; map[0xB3] = '\u2265';
            map[0xB4] = '\u00D7'; map[0xB5] = '\u221D'; map[0xB6] = '\u2202'; map[0xB7] = '\u2022';
            map[0xB8] = '\u00F7'; map[0xB9] = '\u2260'; map[0xBA] = '\u2261'; map[0xBB] = '\u2248';
            map[0xBC] = '\u2026'; map[0xBF] = '\u21B5';
            map[0xC0] = '\u2135'; map[0xC1] = '\u2111'; map[0xC2] = '\u211C'; map[0xC3] = '\u2118';
            map[0xC4] = '\u2297'; map[0xC5] = '\u2295'; map[0xC6] = '\u2205'; map[0xC7] = '\u2229';
            map[0xC8] = '\u222A'; map[0xC9] = '\u2283'; map[0xCA] = '\u2287'; map[0xCB] = '\u2284';
            map[0xCC] = '\u2282'; map[0xCD] = '\u2286'; map[0xCE] = '\u2208'; map[0xCF] = '\u2209';
            map[0xD0] = '\u2220'; map[0xD1] = '\u2207'; map[0xD2] = '\u00AE'; map[0xD3] = '\u00A9';
            map[0xD4] = '\u2122'; map[0xD5] = '\u220F'; map[0xD6] = '\u221A'; map[0xD7] = '\u22C5';
            map[0xD8] = '\u00AC'; map[0xD9] = '\u2227'; map[0xDA] = '\u2228'; map[0xDB] = '\u21D4';
            map[0xDC] = '\u21D0'; map[0xDD] = '\u21D1'; map[0xDE] = '\u21D2'; map[0xDF] = '\u21D3';
            map[0xE0] = '\u25CA'; map[0xE1] = '\u2329'; map[0xE2] = '\u00AE'; map[0xE3] = '\u00A9';
            map[0xE4] = '\u2122'; map[0xE5] = '\u2211';
            map[0xF1] = '\u232A'; map[0xF2] = '\u222B'; map[0xF3] = '\u2320'; map[0xF5] = '\u2321';
            return map;
        }

        // ZapfDingbats font encoding.
        private static Dictionary<int, char> BuildZapfDingbatsEncoding()
        {
            var map = new Dictionary<int, char>();
            map[0x20] = ' ';
            map[0x21] = '\u2701'; map[0x22] = '\u2702'; map[0x23] = '\u2703'; map[0x24] = '\u2704';
            map[0x25] = '\u260E'; map[0x26] = '\u2706'; map[0x27] = '\u2707'; map[0x28] = '\u2708';
            map[0x29] = '\u2709'; map[0x2A] = '\u261B'; map[0x2B] = '\u261E'; map[0x2C] = '\u270C';
            map[0x2D] = '\u270D'; map[0x2E] = '\u270E'; map[0x2F] = '\u270F'; map[0x30] = '\u2710';
            map[0x31] = '\u2711'; map[0x32] = '\u2712'; map[0x33] = '\u2713'; map[0x34] = '\u2714';
            map[0x35] = '\u2715'; map[0x36] = '\u2716'; map[0x37] = '\u2717'; map[0x38] = '\u2718';
            map[0x39] = '\u2719'; map[0x3A] = '\u271A'; map[0x3B] = '\u271B'; map[0x3C] = '\u271C';
            map[0x3D] = '\u271D'; map[0x3E] = '\u271E'; map[0x3F] = '\u271F'; map[0x40] = '\u2720';
            map[0x41] = '\u2721'; map[0x42] = '\u2722'; map[0x43] = '\u2723'; map[0x44] = '\u2724';
            map[0x45] = '\u2725'; map[0x46] = '\u2726'; map[0x47] = '\u2727'; map[0x48] = '\u2605';
            map[0x49] = '\u2729'; map[0x4A] = '\u272A'; map[0x4B] = '\u272B'; map[0x4C] = '\u272C';
            map[0x4D] = '\u272D'; map[0x4E] = '\u272E'; map[0x4F] = '\u272F'; map[0x50] = '\u2730';
            map[0x51] = '\u2731'; map[0x52] = '\u2732'; map[0x53] = '\u2733'; map[0x54] = '\u2734';
            map[0x55] = '\u2735'; map[0x56] = '\u2736'; map[0x57] = '\u2737'; map[0x58] = '\u2738';
            map[0x59] = '\u2739'; map[0x5A] = '\u273A'; map[0x5B] = '\u273B'; map[0x5C] = '\u273C';
            map[0x5D] = '\u273D'; map[0x5E] = '\u273E'; map[0x5F] = '\u273F'; map[0x60] = '\u2740';
            map[0x61] = '\u2741'; map[0x62] = '\u2742'; map[0x63] = '\u2743'; map[0x64] = '\u2744';
            map[0x65] = '\u2745'; map[0x66] = '\u2746'; map[0x67] = '\u2747'; map[0x68] = '\u2748';
            map[0x69] = '\u2749'; map[0x6A] = '\u274A'; map[0x6B] = '\u274B'; map[0x6C] = '\u25CF';
            map[0x6D] = '\u274D'; map[0x6E] = '\u25A0'; map[0x6F] = '\u274F'; map[0x70] = '\u2750';
            map[0x71] = '\u2751'; map[0x72] = '\u2752'; map[0x73] = '\u25B2'; map[0x74] = '\u25BC';
            map[0x75] = '\u25C6'; map[0x76] = '\u2756'; map[0x77] = '\u25D7'; map[0x78] = '\u2758';
            map[0x79] = '\u2759'; map[0x7A] = '\u275A'; map[0x7B] = '\u275B'; map[0x7C] = '\u275C';
            map[0x7D] = '\u275D'; map[0x7E] = '\u275E';
            // 0x7F-0xA0: undefined
            map[0xA1] = '\u2761'; map[0xA2] = '\u2762'; map[0xA3] = '\u2763'; map[0xA4] = '\u2764';
            map[0xA5] = '\u2765'; map[0xA6] = '\u2766'; map[0xA7] = '\u2767'; map[0xA8] = '\u2663';
            map[0xA9] = '\u2666'; map[0xAA] = '\u2665'; map[0xAB] = '\u2660'; map[0xAC] = '\u2460';
            map[0xAD] = '\u2461'; map[0xAE] = '\u2462'; map[0xAF] = '\u2463'; map[0xB0] = '\u2464';
            map[0xB1] = '\u2465'; map[0xB2] = '\u2466'; map[0xB3] = '\u2467'; map[0xB4] = '\u2468';
            map[0xB5] = '\u2469'; map[0xB6] = '\u2776'; map[0xB7] = '\u2777'; map[0xB8] = '\u2778';
            map[0xB9] = '\u2779'; map[0xBA] = '\u277A'; map[0xBB] = '\u277B'; map[0xBC] = '\u277C';
            map[0xBD] = '\u277D'; map[0xBE] = '\u277E'; map[0xBF] = '\u277F'; map[0xC0] = '\u2780';
            map[0xC1] = '\u2781'; map[0xC2] = '\u2782'; map[0xC3] = '\u2783'; map[0xC4] = '\u2784';
            map[0xC5] = '\u2785'; map[0xC6] = '\u2786'; map[0xC7] = '\u2787'; map[0xC8] = '\u2788';
            map[0xC9] = '\u2789'; map[0xCA] = '\u278A'; map[0xCB] = '\u278B'; map[0xCC] = '\u278C';
            map[0xCD] = '\u278D'; map[0xCE] = '\u278E'; map[0xCF] = '\u278F'; map[0xD0] = '\u2790';
            map[0xD1] = '\u2791'; map[0xD2] = '\u2792'; map[0xD3] = '\u2793'; map[0xD4] = '\u2794';
            map[0xD5] = '\u2192'; map[0xD6] = '\u2194'; map[0xD7] = '\u2195'; map[0xD8] = '\u2798';
            map[0xD9] = '\u2799'; map[0xDA] = '\u279A'; map[0xDB] = '\u279B'; map[0xDC] = '\u279C';
            map[0xDD] = '\u279D'; map[0xDE] = '\u279E'; map[0xDF] = '\u279F'; map[0xE0] = '\u27A0';
            map[0xE1] = '\u27A1'; map[0xE2] = '\u27A2'; map[0xE3] = '\u27A3'; map[0xE4] = '\u27A4';
            map[0xE5] = '\u27A5'; map[0xE6] = '\u27A6'; map[0xE7] = '\u27A7'; map[0xE8] = '\u27A8';
            map[0xE9] = '\u27A9'; map[0xEA] = '\u27AA'; map[0xEB] = '\u27AB'; map[0xEC] = '\u27AC';
            map[0xED] = '\u27AD'; map[0xEE] = '\u27AE'; map[0xF0] = '\u27AF'; map[0xF1] = '\u27B0';
            map[0xF3] = '\u27B2'; map[0xF4] = '\u27B3'; map[0xF5] = '\u27B5'; map[0xF7] = '\u27B6';
            map[0xF8] = '\u27B7'; map[0xF9] = '\u27B8'; map[0xFA] = '\u27B9'; map[0xFB] = '\u27BA';
            map[0xFC] = '\u27BB'; map[0xFD] = '\u27BC'; map[0xFE] = '\u27BD';
            return map;
        }

        /// <summary>
        /// MacExpertEncoding — used by some expert-set Type 1 fonts.
        /// Maps selected byte codes to Unicode old-style figures, fractions, and ligatures.
        /// Based on Adobe Technical Note #5014.
        /// </summary>
        private static Dictionary<int, char> BuildMacExpertEncoding()
        {
            var map = new Dictionary<int, char>();
            // Only the most commonly used mappings; gaps are intentional.
            map[0x20] = ' ';
            // Old-style figures
            map[0x30] = '\u2070'; // superscript 0  (approximation for expert zero)
            map[0xDC] = '\uF6DE'; // ff ligature (PUA)
            map[0xDF] = '\uF6DF'; // ffi ligature (PUA)
            // Fractions
            map[0xAD] = '\u00BD'; // one half
            map[0xAE] = '\u00BC'; // one quarter
            map[0xAF] = '\u00BE'; // three quarters
            map[0xB0] = '\u2153'; // one third
            map[0xB1] = '\u2154'; // two thirds
            // Small caps (mapped to lowercase as approximation)
            for (int i = 0x41; i <= 0x5A; i++)
                map[i] = (char)(i + 0x20); // A-Z → a-z
            // ASCII digits (expert set old-style figures mapped to digits)
            for (int i = 0x30; i <= 0x39; i++)
                map[i] = (char)i;
            return map;
        }
    }
}

