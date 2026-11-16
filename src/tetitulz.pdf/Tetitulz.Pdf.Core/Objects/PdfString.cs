// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfString : PdfObject
    {
        // Latin-1 maps bytes 0x00-0xFF 1:1 to code points, so it round-trips raw string bytes
        // losslessly. There is no Encoding lookup to cache: Text.Latin1Text.Encode is a loop, so
        // there is nothing to cache and
        // no type initializer on a type the parser touches on its first string.
        /// <summary>
        /// The string's bytes as the file holds them (after decryption). Read, never written: the
        /// text <see cref="GetText"/> decodes from them is kept.
        /// </summary>
        public byte[] RawBytes { get; }
        public bool IsHex { get; }

        public PdfString(byte[] bytes, bool isHex = false)
        {
            RawBytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
            IsHex = isHex;
        }

        /// <summary>
        /// Creates a PDF text string (§7.9.2.2), choosing the encoding the content requires.
        /// </summary>
        /// <remarks>
        /// <para>Text that fits in a single byte per character is written as before, byte for
        /// byte. Anything above U+00FF is written as <b>UTF-16BE with the required
        /// <c>FE FF</c> byte-order mark</b>, which <see cref="GetText"/> already reads back.</para>
        /// <para><b>Not everything through Latin-1</b>, whose encoder fallback substitutes <c>?</c>:
        /// every character outside Latin-1 would be silently destroyed at the point of writing — a
        /// Hebrew, Arabic, Greek, Cyrillic or CJK document title, alt text, tooltip or annotation
        /// <c>/Contents</c> a row of question marks. The writing half matches the reading half,
        /// which handles UTF-16BE.</para>
        /// <para>Existing output is unaffected: a string that Latin-1 could represent produces
        /// exactly the same bytes it did before, so only the cases that were already broken
        /// change.</para>
        /// </remarks>
        public PdfString(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            RawBytes = RequiresUtf16(text)
                ? EncodeUtf16BigEndianWithBom(text)
                : Text.Latin1Text.Encode(text);
            IsHex = false;
        }

        /// <summary>
        /// True when writing <paramref name="text"/> one byte per character would not survive
        /// <see cref="GetText"/> — so the string must go out as UTF-16BE instead.
        ///
        /// <para><b>Two clauses, and the second one is the whole point.</b> A character above
        /// U+00FF plainly has no single byte. But a character <i>at or below</i> U+00FF can be
        /// unrepresentable too, because the two halves of this class do not use the same
        /// single-byte encoding: the constructor wrote <b>Latin-1</b> and
        /// <see cref="GetText"/> reads <b>PDFDocEncoding</b> (§7.9.2.2), and those differ at
        /// 0x18-0x1F and 0x80-0xA0. So <c>new PdfString(" ")</c> - a NO-BREAK SPACE - wrote
        /// the byte <c>A0</c> and <see cref="GetText"/> read it back as the Euro sign, and every
        /// character in those two ranges came back as a <i>different</i> character. <b>The class
        /// was not its own inverse.</b></para>
        ///
        /// <para>The second clause is written as a lookup into <see cref="_pdfDocMap"/> — the very
        /// table <see cref="DecodePdfDocEncoding"/> reads — rather than as a hand-written range
        /// test, so the two can never drift: a byte the decoder translates is by construction a
        /// byte the encoder must not emit. A range test spelled <c>0x18..0x1F || 0x80..0xA0</c>
        /// would already be subtly wrong, because <c>0x9F</c> is undefined in PDFDocEncoding and
        /// the decoder correctly passes it through.</para>
        /// </summary>
        private static bool RequiresUtf16(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c > 'ÿ') return true;
                if (_pdfDocMap[c] != '\0') return true;
            }
            return false;
        }

        /// <summary>
        /// UTF-16BE preceded by <c>FE FF</c>, the marker §7.9.2.2 requires so a reader can tell
        /// the string from a PDFDocEncoded one.
        /// </summary>
        private static byte[] EncodeUtf16BigEndianWithBom(string text)
        {
            byte[] body = Encoding.BigEndianUnicode.GetBytes(text);
            var bytes = new byte[body.Length + 2];
            bytes[0] = 0xFE;
            bytes[1] = 0xFF;
            Buffer.BlockCopy(body, 0, bytes, 2, body.Length);
            return bytes;
        }

        /// <summary>
        /// Creates a PDF 2.0 text string using the native UTF-8 encoding (§7.9.2.2, ISO 32000-2),
        /// prefixed by the <c>EF BB BF</c> byte-order mark <see cref="GetText"/> already
        /// recognises on read.
        /// </summary>
        /// <remarks>
        /// <b>Opt-in only.</b> The <see cref="PdfString(string)"/> constructor keeps writing
        /// PDFDocEncoding/UTF-16BE regardless of content, which every PDF 1.x reader already
        /// understands — switching its output automatically based on the string's own content, or
        /// on a document's PDF version the object model does not carry at construction time, would
        /// silently change what every existing caller produces. A caller targeting PDF 2.0 chooses
        /// this explicitly instead.
        /// </remarks>
        public static PdfString CreateUtf8(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            byte[] body = Encoding.UTF8.GetBytes(text);
            var bytes = new byte[body.Length + 3];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);
            return new PdfString(bytes);
        }

        /// <summary>
        /// The string as text (§7.9.2.2): UTF-16BE or UTF-8 behind a byte-order mark, a UTF-16LE one
        /// tolerated, PDFDocEncoding otherwise — decoded once and kept.
        /// </summary>
        /// <remarks>
        /// <b>Once, because a file can name one string from every node of a tree</b> — every
        /// widget's <c>/MK</c> caption, every field's <c>/V</c>, every <c>/Info</c> entry, every
        /// annotation's <c>/Contents</c> — and a reader decoding it at each name holds the string's
        /// size times its references: a form of three hundred fields sharing one string of a quarter
        /// of a megabyte cost three quarters of a gigabyte to read. The bytes are the string's own
        /// and are not written after it is made (the decryptor makes a new string), so the text kept
        /// stays theirs.
        /// </remarks>
        public string GetText() => _text ??= Decode();

        private string _text;

        private string Decode()
        {
            // Check for BOM (UTF-16 BE) — PDF spec §7.9.2.2
            if (RawBytes.Length >= 2 && RawBytes[0] == 0xFE && RawBytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(RawBytes, 2, RawBytes.Length - 2);
            // Check for UTF-16 LE BOM — not in the PDF spec but common in practice
            if (RawBytes.Length >= 2 && RawBytes[0] == 0xFF && RawBytes[1] == 0xFE)
                return Encoding.Unicode.GetString(RawBytes, 2, RawBytes.Length - 2);
            // Check for UTF-8 BOM (PDF 2.0)
            if (RawBytes.Length >= 3 && RawBytes[0] == 0xEF && RawBytes[1] == 0xBB && RawBytes[2] == 0xBF)
                return Encoding.UTF8.GetString(RawBytes, 3, RawBytes.Length - 3);
            // Default: PDFDocEncoding — differs from ISO-8859-1 at bytes 0x18-0x1F and 0x80-0xA0.
            return DecodePdfDocEncoding(RawBytes);
        }

        // PDFDocEncoding's translation table (ISO 32000-2 Annex D).
        // Non-zero entries override the byte value; zero means use (char)byte as-is.
        // Key differences vs ISO-8859-1:
        //   0x18-0x1F → typographic accent marks (˘ ˇ ˆ ˙ ˝ ˛ ˚ ˜)
        //   0x80-0x9E → typographic chars (• † ‡ … — – ƒ ⁄ ‹ › − ‰ „ " " ' ' ‚ ™ ﬁ ﬂ Ł Œ Š Ÿ Ž ı ł œ š ž)
        //   0xA0      → Euro sign (€) instead of NO-BREAK SPACE
        private static readonly char[] _pdfDocMap = BuildPdfDocMap();

        /// <summary>
        /// Size of the PDFDocEncoding translation table: one entry per possible byte value, so
        /// <see cref="DecodePdfDocEncoding"/> can index it with an unchecked <c>byte</c>.
        /// </summary>
        private const int PdfDocEncodingTableSize = 256;

        private static char[] BuildPdfDocMap()
        {
            var m = new char[PdfDocEncodingTableSize];
            m[0x18] = '\u02D8'; m[0x19] = '\u02C7'; m[0x1A] = '\u02C6'; m[0x1B] = '\u02D9';
            m[0x1C] = '\u02DD'; m[0x1D] = '\u02DB'; m[0x1E] = '\u02DA'; m[0x1F] = '\u02DC';
            m[0x80] = '\u2022'; m[0x81] = '\u2020'; m[0x82] = '\u2021'; m[0x83] = '\u2026';
            m[0x84] = '\u2014'; m[0x85] = '\u2013'; m[0x86] = '\u0192'; m[0x87] = '\u2044';
            m[0x88] = '\u2039'; m[0x89] = '\u203A'; m[0x8A] = '\u2212'; m[0x8B] = '\u2030';
            m[0x8C] = '\u201E'; m[0x8D] = '\u201C'; m[0x8E] = '\u201D'; m[0x8F] = '\u2018';
            m[0x90] = '\u2019'; m[0x91] = '\u201A'; m[0x92] = '\u2122'; m[0x93] = '\uFB01';
            m[0x94] = '\uFB02'; m[0x95] = '\u0141'; m[0x96] = '\u0152'; m[0x97] = '\u0160';
            m[0x98] = '\u0178'; m[0x99] = '\u017D'; m[0x9A] = '\u0131'; m[0x9B] = '\u0142';
            m[0x9C] = '\u0153'; m[0x9D] = '\u0161'; m[0x9E] = '\u017E';
            // 0x9F: undefined in PDFDocEncoding — leave '\0', falls back to (char)0x9F
            m[0xA0] = '\u20AC'; // Euro sign
            // 0xA1-0xFF: same as ISO-8859-1, leave as '\0' (use (char)b)
            return m;
        }

        private static string DecodePdfDocEncoding(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
            {
                char mapped = _pdfDocMap[b];
                sb.Append(mapped != '\0' ? mapped : (char)b);
            }
            return sb.ToString();
        }

        public override string ToString() => GetText();
    }
}
