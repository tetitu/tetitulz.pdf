// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// Maps a PostScript font name onto one of the PDF standard 14, and measures strings with the
    /// real AFM metrics.
    ///
    /// <para><b>This is the fallback path, not the only one.</b> A document that ships its own Type 1
    /// program gets that program embedded verbatim instead (<c>PsEmbeddedFont</c> →
    /// <c>PdfResourceCollector.GetOrAddEmbeddedFont</c>), and <c>PsTextOps.Paint</c> prefers it —
    /// so this type handles the fonts the document expects the <i>reader</i> to already have. The
    /// standard 14 are exactly those, and <c>Core.Fonts.Standard14Metrics</c> supplies their widths,
    /// which is why that type was made public: <c>stringwidth</c> has no answer without them, and a
    /// plugin cannot see Core's internals.</para>
    ///
    /// <para><b>An unknown name still returns a font.</b> Raising <c>undefined</c> from
    /// <c>findfont</c> would kill the program at line 1 of its prologue, where returning the nearest
    /// standard face and reporting it loses only the typeface.</para>
    /// </summary>
    internal static class PsFontMapper
    {
        // The private family table and its StripStyleWords pass are gone: Standard14Names.Canonical
        // answers this now, and it knows the names this table did not ("TimesNewRomanPSMT", the
        // AcroForm short names "Cour" / "TiRo" / "ZaDb"). Deleting rather than leaving it unread
        // is the point — a table nothing consults is free to drift back out of agreement.

        /// <summary>
        /// The standard-14 base font for <paramref name="postScriptName"/>.
        /// </summary>
        /// <param name="postScriptName">
        /// The name as the PostScript program wrote it — <c>/Helvetica-Bold</c>, <c>/Arial,Bold</c>,
        /// or a subset-prefixed <c>/ABCDEF+Times-Roman</c>.
        /// </param>
        /// <param name="exact">
        /// True when the name mapped to a real family; false when it fell back to Helvetica. The
        /// caller reports the fallback so the output is honest about the substitution.
        /// </param>
        public static string Resolve(string postScriptName, out bool exact)
        {
            exact = false;
            if (string.IsNullOrEmpty(postScriptName)) return StandardFonts.Helvetica;

            // The standard 14 spelled exactly — pass straight through.
            if (Standard14Metrics.IsStandard14(postScriptName)) { exact = true; return postScriptName; }

            // Symbol and ZapfDingbats have no style variants and no metric-compatible substitute.
            if (postScriptName.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0)
            { exact = true; return StandardFonts.Symbol; }
            if (postScriptName.IndexOf("Dingbat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                postScriptName.IndexOf("ZapfD", StringComparison.OrdinalIgnoreCase) >= 0)
            { exact = true; return StandardFonts.ZapfDingbats; }

            // The ONE canonicalisation rule, in Core beside the AFM metrics the answer selects.
            //
            // A private family table plus a StripStyleWords pass would disagree with it on ordinary
            // input: "TimesNewRomanPSMT" would miss the table, be cut at "roman" to "timesnew", miss
            // again and come back Helvetica with exact:false — Times text emitted as /BaseFont
            // /Helvetica and MEASURED against the Helvetica AFM table, at the wrong advances, with a
            // false "no metric-compatible substitute is known". The AcroForm short names ("Cour",
            // "TiRo") are the shared rule's too.
            string canonical = Standard14Names.Canonical(postScriptName);
            if (canonical != null)
            {
                exact = true;
                return canonical;
            }

            return StandardFonts.Helvetica;   // exact stays false: this is a substitution
        }

        /// <summary>
        /// The advance width of <paramref name="bytes"/> in the named standard-14 font, in
        /// <b>em fractions</b> — multiply by the font size for user-space units.
        ///
        /// <para>Bytes are read as WinAnsi, which is the encoding the emitted font dictionaries
        /// declare. The two disagree only above 0x7F, and StandardEncoding is not what a PDF reader
        /// will apply to what we emit — so measuring in the encoding we write is the consistent
        /// choice.</para>
        /// </summary>
        public static double MeasureEmWidth(string baseFont, byte[] bytes, int offset, int length)
            => MeasureEmWidth(baseFont, bytes, offset, length, out _);

        /// <summary>
        /// The advance width of <paramref name="bytes"/> in em fractions, also reporting how many
        /// of them the font cannot draw.
        /// </summary>
        /// <param name="baseFont">One of the standard-14 base font names.</param>
        /// <param name="bytes">The shown string, as bytes.</param>
        /// <param name="offset">Index of the first byte to measure.</param>
        /// <param name="length">How many bytes to measure.</param>
        /// <param name="unrepresentable">
        /// How many bytes the mapped font has no glyph for. Non-zero means the reader will draw
        /// something other than what the program asked for — see <see cref="CountUnrepresentable"/>.
        /// </param>
        public static double MeasureEmWidth(string baseFont, byte[] bytes, int offset, int length,
                                            out int unrepresentable)
            => MeasureEmWidth(baseFont, bytes, offset, length, null, out unrepresentable);

        /// <summary>
        /// The advance width under an optional custom encoding.
        /// </summary>
        /// <param name="baseFont">One of the standard-14 base font names.</param>
        /// <param name="bytes">The shown string, as bytes.</param>
        /// <param name="offset">Index of the first byte to measure.</param>
        /// <param name="length">How many bytes to measure.</param>
        /// <param name="differences">
        /// Character code to Adobe glyph name, from a re-encoded font, or null for WinAnsi. A code
        /// listed here is resolved through its glyph name rather than through WinAnsi — which is
        /// the whole point of re-encoding, and without it a Greek or Hebrew string would be measured
        /// with the widths of the Latin letters at the same code points.
        /// </param>
        /// <param name="unrepresentable">How many bytes the font has no glyph for.</param>
        public static double MeasureEmWidth(string baseFont, byte[] bytes, int offset, int length,
                                            IReadOnlyDictionary<int, string> differences,
                                            out int unrepresentable)
        {
            unrepresentable = 0;
            if (bytes == null || length <= 0) return 0;

            double total = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = bytes[offset + i];
                double w = GlyphWidth(baseFont, b, differences, out bool isWhitespace);

                if (w <= 0)
                {
                    // A glyph absent from the table contributes the font's own space width rather
                    // than zero, so an unmappable byte still advances the pen and text after it is
                    // not overprinted on top of what came before.
                    w = GlyphWidth(baseFont, (byte)' ', null, out _);
                    if (!isWhitespace) unrepresentable++;
                }

                total += w;
            }
            return total / 1000.0;
        }

        /// <summary>
        /// The advance of <paramref name="bytes"/> in em fractions by an embedded program's own glyphs —
        /// each byte's glyph through <see cref="PsEmbeddedFont.GlyphNameFor"/>, a glyph the program
        /// lacks drawn as its <c>.notdef</c> — or false when the program cannot be read, for the caller
        /// to measure against the standard face instead.
        /// </summary>
        public static bool TryMeasureEmbeddedEm(PsEmbeddedFont font, byte[] bytes, int offset, int length,
                                                IReadOnlyDictionary<int, string> encoding, out double em)
        {
            em = 0;
            if (font == null || !font.CanMeasure || bytes == null) return false;
            double total = 0;
            for (int k = 0; k < length; k++)
                total += font.GlyphAdvance(font.GlyphNameFor(bytes[offset + k], encoding));
            em = total / 1000.0;   // thousandths, as the font matrix takes them (PsTextOps.DefineFont)
            return true;
        }

        /// <summary>
        /// The WinAnsi code that draws <paramref name="glyphName"/>, or -1 when none does.
        ///
        /// <para>The inverse of <see cref="WinAnsi.ToChar"/>, and what lets <c>glyphshow</c> reach a
        /// glyph the way <c>show</c> would. A font with no custom encoding has no map to search —
        /// its encoding is WinAnsi by construction (see
        /// <c>PdfResourceCollector.GetOrAddFont</c>) — so without this every <c>/A glyphshow</c>
        /// would synthesise a one-entry <c>/Differences</c> font for a glyph already sitting at
        /// 0x41, minting resources and diverging from <c>show</c> for no reason.</para>
        /// </summary>
        public static int WinAnsiCodeFor(string glyphName)
        {
            if (string.IsNullOrEmpty(glyphName)) return -1;
            if (!PdfGlyphNames.TryGetUnicode(glyphName, out char c) || c == '\0') return -1;

            // 224 comparisons over a table that is mostly identity. Below 0x80 it IS identity, so
            // the common case is one test; above it the 0x80-0x9F block is the only real work.
            if (c < 0x80) return c;

            for (int b = 0x80; b <= 0xFF; b++)
                if (WinAnsi.ToChar((byte)b) == c) return b;

            return -1;
        }

        /// <summary>
        /// The Unicode a byte denotes: through the custom encoding's glyph name when one covers it,
        /// otherwise through WinAnsi.
        /// </summary>
        internal static char ResolveChar(byte b, IReadOnlyDictionary<int, string> differences)
        {
            if (differences != null && differences.TryGetValue(b, out string glyphName)
                && PdfGlyphNames.TryGetUnicode(glyphName, out char fromName))
                return fromName;

            return WinAnsi.ToChar(b);
        }

        /// <summary>
        /// The AFM width (1/1000 em) of the glyph a byte selects, or 0 for none. Symbol and
        /// ZapfDingbats are measured through their <b>built-in</b> encoding (ISO 32000-1 §9.6.6.4
        /// — a reader draws them through it whatever <c>/Encoding</c> says), a <c>/Differences</c>
        /// entry by its glyph name; every other face through WinAnsi, which is what the collector
        /// declares for it. Through WinAnsi, <c>(abg) show</c> in Symbol would advance the pen by
        /// nothing and report three bytes with "no glyph" — while a reader draws αβγ.
        /// </summary>
        private static double GlyphWidth(string baseFont, byte b,
                                         IReadOnlyDictionary<int, string> differences,
                                         out bool isWhitespace)
        {
            if (Standard14Metrics.HasBuiltInEncoding(baseFont))
            {
                isWhitespace = false;
                if (differences != null && differences.TryGetValue(b, out string named))
                    return Standard14Metrics.TryGetWidthByGlyphName(baseFont, named, out double byName)
                        ? byName : 0;
                if (!Standard14Metrics.TryGetBuiltInGlyph(baseFont, b, out string builtIn, out double w))
                    return 0;
                isWhitespace = builtIn == "space";
                return w;
            }

            char c = ResolveChar(b, differences);
            isWhitespace = IsWhitespace(c);
            return Standard14Metrics.GetWidthByUnicode(baseFont, c);
        }

        /// <summary>
        /// The byte that shows <paramref name="glyphName"/> through the encoding the collector
        /// declares for <paramref name="baseFont"/> — Symbol's and ZapfDingbats' own, WinAnsi for
        /// the rest — or −1 when the glyph is outside it.
        /// </summary>
        public static int DefaultCodeFor(string baseFont, string glyphName)
            => Standard14Metrics.HasBuiltInEncoding(baseFont)
                ? Standard14Metrics.BuiltInCodeOf(baseFont, glyphName)
                : WinAnsiCodeFor(glyphName);

        /// <summary>
        /// How many of <paramref name="bytes"/> the mapped font cannot represent.
        ///
        /// <para><b>Why this is worth reporting rather than silently tolerating.</b> A font that
        /// reaches this type is emitted with <c>WinAnsiEncoding</c>, so its bytes are handed to the
        /// reader as WinAnsi code points. A legacy Hebrew, Cyrillic or Greek document encodes its
        /// text in a single-byte codepage — ISO-8859-8, CP1251, ISO-8859-7 — and expects a font that
        /// matches it. <c>definefont</c>'s <c>/Encoding</c> array is carried across as a
        /// <c>/Differences</c>, which fixes Greek because Symbol has the glyphs; Hebrew and Cyrillic
        /// have no glyphs in <i>any</i> standard-14 face, so unless the document also embedded its
        /// own font program the reader draws the <i>WinAnsi</i> glyphs at those code points and
        /// <c>שלום</c> comes out as <c>éìåí</c>. That last case is what this counts.</para>
        ///
        /// <para>That is a wrong drawing, not a missing one, and nothing else in the pipeline can
        /// notice it — which is exactly the case that must not report a clean conversion.</para>
        /// </summary>
        public static int CountUnrepresentable(string baseFont, byte[] bytes,
                                               IReadOnlyDictionary<int, string> differences = null)
        {
            if (bytes == null || bytes.Length == 0) return 0;
            MeasureEmWidth(baseFont, bytes, 0, bytes.Length, differences, out int unrepresentable);
            return unrepresentable;
        }

        /// <summary>
        /// Whitespace is never counted as unrepresentable: the standard-14 tables carry no width for
        /// a tab or a newline, but a program that shows one is not asking for a glyph.
        ///
        /// <para><b>NUL is deliberately excluded from this list.</b> <see cref="WinAnsi.ToChar"/>
        /// returns NUL for the five code points CP1252 leaves undefined, so treating it as
        /// whitespace would exempt exactly the bytes this count exists to find.</para>
        /// </summary>
        private static bool IsWhitespace(char c) =>
            c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
    }

    /// <summary>
    /// The WinAnsi (CP1252) byte-to-Unicode mapping, for measuring what will be emitted.
    ///
    /// <para>Only the 0x80–0x9F block differs from Latin-1; the rest is identity. Spelling out just
    /// that block is smaller and clearer than carrying a 256-entry table that is 224 identity
    /// entries.</para>
    /// </summary>
    internal static class WinAnsi
    {
        /// <summary>
        /// 0x80-0x9F as explicit code points rather than pasted glyphs. Several are invisible or
        /// easily mangled by an editor, and <b>five are undefined in CP1252</b> (0x81, 0x8D, 0x8F,
        /// 0x90, 0x9D). Those five map to NUL, which is what lets
        /// <see cref="PsFontMapper.CountUnrepresentable"/> see that no glyph exists for them.
        /// </summary>
        private static readonly char[] High =
        {
            '€', '\0',     '‚', 'ƒ',   // 80 EUR  81 --   82 ,     83 f
            '„', '…', '†', '‡',   // 84 ,,   85 ...  86 dag   87 ddag
            'ˆ', '‰', 'Š', '‹',   // 88 ^    89 %o   8A S     8B <
            'Œ', '\0',     'Ž', '\0',       // 8C OE   8D --   8E Z     8F --
            '\0',     '‘', '’', '“',   // 90 --   91 '    92 '     93 "
            '”', '•', '–', '—',   // 94 "    95 *    96 -     97 --
            '˜', '™', 'š', '›',   // 98 ~    99 TM   9A s     9B >
            'œ', '\0',     'ž', 'Ÿ'    // 9C oe   9D --   9E z     9F Y
        };

        /// <summary>
        /// The Unicode code point a byte denotes under WinAnsi, or NUL for the five undefined
        /// slots.
        /// </summary>
        public static char ToChar(byte b) => b >= 0x80 && b <= 0x9F ? High[b - 0x80] : (char)b;
    }
}
