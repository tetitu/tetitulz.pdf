// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Adobe Font Metrics (AFM) advance widths for the PDF Standard-14 fonts,
    /// in thousandths of a text-space unit (1000 = 1 em).
    ///
    /// Per ISO 32000-1 §9.6.2.2, viewers MUST use these built-in metrics when a
    /// Standard-14 font dictionary omits the /Widths array.  Each table is indexed
    /// by Unicode code point.
    ///
    /// <para><b>Public because a plugin needs it.</b> PostScript's <c>stringwidth</c> has no
    /// answer without real AFM metrics, and a plugin assembly cannot see Core's internals.
    /// The alternatives were an <c>InternalsVisibleTo</c> for one plugin (which would establish
    /// "plugins reach into Core internals") or a second copy of the tables (a DRY violation of
    /// exactly the kind this repo has been bitten by). This is spec-mandated reference data with
    /// a two-method surface, so publishing it is the honest option.</para>
    ///
    /// <para><b>These tables are keyed by Unicode, and an AFM file is keyed by StandardEncoding
    /// code. Those two disagree at exactly two places.</b> StandardEncoding puts <c>quoteright</c> at code 39 and <c>quoteleft</c> at
    /// code 96 (see <c>StandardEncodings</c>), where ASCII has <c>quotesingle</c> (U+0027) and
    /// <c>grave</c> (U+0060). Transcribing an AFM by its encoded code and filing each width under
    /// that code's ASCII character therefore mis-assigns those two — and only those two, because
    /// every other printable ASCII code names the same glyph in both.</para>
    ///
    /// <para>The Helvetica pair carried <c>quoteright</c>/<c>quoteleft</c>'s widths at U+0027 and
    /// U+0060 (222/222 and 278/278) instead of 191/333 and 238/333. <b>It survived because it is
    /// invisible in four of the six fonts</b>: in the Times family <c>quotesingle</c> and
    /// <c>quoteright</c> happen to be close or equal, so the same transcription error produces the
    /// right number. The Times tables here were in fact keyed correctly, which is what made the
    /// Helvetica entries provably wrong rather than merely suspect — they equalled this class's own
    /// U+2019/U+2018 values exactly, which is the signature of the mis-keying.</para>
    ///
    /// <para><b>When adding a font, check U+0027 and U+0060 against the AFM's <i>glyph names</i>,
    /// not its codes.</b> <c>Standard14QuoteAndGraveTests</c> pins the pair.</para>
    /// </summary>
    public static partial class Standard14Metrics
    {
        // ── Canonical base-font names ──────────────────────────────────────────
        private static readonly HashSet<string> _standard14Names = new()
        {
            StandardFonts.Courier, StandardFonts.CourierBold,
            StandardFonts.CourierOblique, StandardFonts.CourierBoldOblique,
            StandardFonts.Helvetica, StandardFonts.HelveticaBold,
            StandardFonts.HelveticaOblique, StandardFonts.HelveticaBoldOblique,
            StandardFonts.TimesRoman, StandardFonts.TimesBold,
            StandardFonts.TimesItalic, StandardFonts.TimesBoldItalic,
            StandardFonts.Symbol, StandardFonts.ZapfDingbats,
        };

        public static bool IsStandard14(string baseFontName) =>
            baseFontName != null && _standard14Names.Contains(baseFontName);

        /// <summary>
        /// Returns the AFM advance width (in 1/1000 em) for <paramref name="unicode"/>
        /// in the named Standard-14 font.  Returns 0 when the font is not Standard-14
        /// or the glyph is not in the table.
        /// </summary>
        public static double GetWidthByUnicode(string baseFontName, char unicode)
            => TryGetWidthTable(baseFontName, out var table, out double missing)
                 ? (table.TryGetValue(unicode, out double w) ? w : missing)
                 // Symbol and ZapfDingbats: no Unicode table of their own, but their AFM names
                 // reach Unicode through the glyph list (Standard14Metrics.Symbolic.cs).
                 : SymbolicWidthByUnicode(baseFontName, unicode);

        /// <summary>
        /// The Unicode-keyed AFM width table for a Standard-14 font, and the width to use for a
        /// code point the table does not carry. Returns <c>false</c> for a font that has no such
        /// table — a non-Standard-14 name, or <c>Symbol</c>/<c>ZapfDingbats</c>, which are reached
        /// through their own encodings instead.
        /// </summary>
        /// <remarks>
        /// <b>Exists so a caller measuring a string selects the table once rather than per
        /// character.</b> <see cref="GetWidthByUnicode"/> resolves the font with an if-chain of up
        /// to eleven <see cref="string"/> comparisons, and <c>PdfExporter.AdvanceWidth</c> called it
        /// inside its per-character loop having already resolved the base font immediately above —
        /// so measuring an <i>n</i>-character run cost up to 11<i>n</i> string comparisons to reach
        /// the same table every time. <see cref="GetWidthByUnicode"/> is now a wrapper over this, so
        /// there is still one implementation of the selection.
        /// </remarks>
        public static bool TryGetWidthTable(
            string baseFontName, out IReadOnlyDictionary<char, double> table, out double missingWidth)
        {
            missingWidth = 0;
            table = null;
            if (baseFontName == null) return false;

            // Courier family: monospaced, so a code point outside the table still advances 600.
            if (baseFontName == StandardFonts.Courier || baseFontName == StandardFonts.CourierBold ||
                baseFontName == StandardFonts.CourierOblique || baseFontName == StandardFonts.CourierBoldOblique)
            {
                table = _courierWidth;
                missingWidth = CourierAdvanceWidth;
                return true;
            }

            if (baseFontName == StandardFonts.Helvetica || baseFontName == StandardFonts.HelveticaOblique)
                table = _helveticaWidths;
            else if (baseFontName == StandardFonts.HelveticaBold || baseFontName == StandardFonts.HelveticaBoldOblique)
                table = _helveticaBoldWidths;
            else if (baseFontName == StandardFonts.TimesRoman)      table = _timesRomanWidths;
            else if (baseFontName == StandardFonts.TimesBold)       table = _timesBoldWidths;
            else if (baseFontName == StandardFonts.TimesItalic)     table = _timesItalicWidths;
            else if (baseFontName == StandardFonts.TimesBoldItalic) table = _timesBoldItalicWidths;

            // Symbol and ZapfDingbats are handled separately via their own encodings;
            // we don't provide Unicode-keyed tables for them here.
            return table != null;
        }

        /// <summary>
        /// The single advance width, in glyph-space units, of every glyph in the Courier family —
        /// it is monospaced, so any code point not in the table still advances by this much.
        /// </summary>
        private const double CourierAdvanceWidth = 600;

        /// <summary>
        /// The AFM vertical metrics of a Standard-14 font, in 1/1000 em — ascender, descender
        /// (negative, below the baseline), cap height and x-height.
        ///
        /// <para>
        /// <b>Why this is here and not alongside the widths callers already use.</b> Advance widths
        /// answer "how far does the pen move"; stacking one box above another needs "how tall is it
        /// and how deep does it hang", and nothing in the repository could answer that for a
        /// Standard-14 font. A fraction cannot be laid out from widths alone.
        /// </para>
        ///
        /// <para>
        /// The twelve Latin faces' values are the published Adobe AFM figures (<c>Ascender</c>,
        /// <c>Descender</c>, <c>CapHeight</c>, <c>XHeight</c>). <b>Symbol and ZapfDingbats are not</b>:
        /// their AFMs carry none of those four keys, so their ascender and descender here are the
        /// <c>FontBBox</c>'s top and bottom, and their cap height and x-height (700, 520) are
        /// placeholders with no AFM source. Returns <c>false</c> for anything that is not one of
        /// the Standard 14, leaving the caller to use a font program's own <c>head</c> /
        /// <c>hhea</c> / <c>OS/2</c> metrics instead.
        /// </para>
        /// </summary>
        public static bool TryGetVerticalMetrics(string baseFontName,
            out double ascender, out double descender, out double capHeight, out double xHeight)
        {
            ascender = descender = capHeight = xHeight = 0;
            if (baseFontName == null) return false;

            switch (baseFontName)
            {
                case StandardFonts.Courier:
                case StandardFonts.CourierOblique:
                    ascender = 629; descender = -157; capHeight = 562; xHeight = 426; return true;
                case StandardFonts.CourierBold:
                case StandardFonts.CourierBoldOblique:
                    ascender = 629; descender = -157; capHeight = 562; xHeight = 439; return true;

                case StandardFonts.Helvetica:
                case StandardFonts.HelveticaOblique:
                    ascender = 718; descender = -207; capHeight = 718; xHeight = 523; return true;
                case StandardFonts.HelveticaBold:
                case StandardFonts.HelveticaBoldOblique:
                    ascender = 718; descender = -207; capHeight = 718; xHeight = 532; return true;

                case StandardFonts.TimesRoman:
                    ascender = 683; descender = -217; capHeight = 662; xHeight = 450; return true;
                case StandardFonts.TimesBold:
                    ascender = 683; descender = -217; capHeight = 676; xHeight = 461; return true;
                case StandardFonts.TimesItalic:
                    ascender = 683; descender = -217; capHeight = 653; xHeight = 441; return true;
                case StandardFonts.TimesBoldItalic:
                    ascender = 683; descender = -217; capHeight = 669; xHeight = 462; return true;

                case StandardFonts.Symbol:
                    ascender = 1010; descender = -293; capHeight = 700; xHeight = 520; return true;
                case StandardFonts.ZapfDingbats:
                    ascender = 820; descender = -143; capHeight = 700; xHeight = 520; return true;

                default:
                    return false;
            }
        }

        // ── Courier ─────────────────────────────────────────────────────────────
        // All printable characters are 600; special control codes are 0.
        private static readonly Dictionary<char, double> _courierWidth = new()
        {
            {' ', 600}, {'!', 600}, {'"', 600}, {'#', 600}, {'$', 600}, {'%', 600},
            {'&', 600}, {'\'',600}, {'(', 600}, {')', 600}, {'*', 600}, {'+', 600},
            {',', 600}, {'-', 600}, {'.', 600}, {'/', 600}, {'0', 600}, {'1', 600},
            {'2', 600}, {'3', 600}, {'4', 600}, {'5', 600}, {'6', 600}, {'7', 600},
            {'8', 600}, {'9', 600}, {':', 600}, {';', 600}, {'<', 600}, {'=', 600},
            {'>', 600}, {'?', 600}, {'@', 600},
            {'A', 600}, {'B', 600}, {'C', 600}, {'D', 600}, {'E', 600}, {'F', 600},
            {'G', 600}, {'H', 600}, {'I', 600}, {'J', 600}, {'K', 600}, {'L', 600},
            {'M', 600}, {'N', 600}, {'O', 600}, {'P', 600}, {'Q', 600}, {'R', 600},
            {'S', 600}, {'T', 600}, {'U', 600}, {'V', 600}, {'W', 600}, {'X', 600},
            {'Y', 600}, {'Z', 600},
            {'[', 600}, {'\\',600}, {']', 600}, {'^', 600}, {'_', 600}, {'`', 600},
            {'a', 600}, {'b', 600}, {'c', 600}, {'d', 600}, {'e', 600}, {'f', 600},
            {'g', 600}, {'h', 600}, {'i', 600}, {'j', 600}, {'k', 600}, {'l', 600},
            {'m', 600}, {'n', 600}, {'o', 600}, {'p', 600}, {'q', 600}, {'r', 600},
            {'s', 600}, {'t', 600}, {'u', 600}, {'v', 600}, {'w', 600}, {'x', 600},
            {'y', 600}, {'z', 600},
            {'{', 600}, {'|', 600}, {'}', 600}, {'~', 600},
        };

        // ── Helvetica (regular + oblique share metrics) ───────────────────────
        private static readonly Dictionary<char, double> _helveticaWidths = new()
        {
            {' ', 278}, {'!', 278}, {'"', 355}, {'#', 556}, {'$', 556}, {'%', 889},
            // U+0027 is quotesingle (191), NOT quoteright (222) — see the remarks on this class.
            {'&', 667}, {'\'',191}, {'(', 333}, {')', 333}, {'*', 389}, {'+', 584},
            {',', 278}, {'-', 333}, {'.', 278}, {'/', 278},
            {'0', 556}, {'1', 556}, {'2', 556}, {'3', 556}, {'4', 556},
            {'5', 556}, {'6', 556}, {'7', 556}, {'8', 556}, {'9', 556},
            {':', 278}, {';', 278}, {'<', 584}, {'=', 584}, {'>', 584},
            {'?', 556}, {'@', 1015},
            {'A', 667}, {'B', 667}, {'C', 722}, {'D', 722}, {'E', 667}, {'F', 611},
            {'G', 778}, {'H', 722}, {'I', 278}, {'J', 500}, {'K', 667}, {'L', 556},
            {'M', 833}, {'N', 722}, {'O', 778}, {'P', 667}, {'Q', 778}, {'R', 722},
            {'S', 667}, {'T', 611}, {'U', 722}, {'V', 667}, {'W', 944}, {'X', 667},
            {'Y', 667}, {'Z', 611},
            // U+0060 is grave (333), NOT quoteleft (222).
            {'[', 278}, {'\\',278}, {']', 278}, {'^', 469}, {'_', 556}, {'`', 333},
            {'a', 556}, {'b', 556}, {'c', 500}, {'d', 556}, {'e', 556}, {'f', 278},
            {'g', 556}, {'h', 556}, {'i', 222}, {'j', 222}, {'k', 500}, {'l', 222},
            {'m', 833}, {'n', 556}, {'o', 556}, {'p', 556}, {'q', 556}, {'r', 333},
            {'s', 500}, {'t', 278}, {'u', 556}, {'v', 500}, {'w', 722}, {'x', 500},
            {'y', 500}, {'z', 500},
            {'{', 334}, {'|', 260}, {'}', 334}, {'~', 584},
            // WinAnsi extended (common accented Latin)
            {' ', 278}, {'¡', 333}, {'¢', 556}, {'£', 556},
            {'¤', 556}, {'¥', 556}, {'¦', 260}, {'§', 556},
            {'¨', 333}, {'©', 737}, {'ª', 370}, {'«', 556},
            {'¬', 584}, {'­', 333}, {'®', 737}, {'¯', 333},
            {'°', 400}, {'±', 584}, {'²', 333}, {'³', 333},
            {'´', 278}, {'µ', 556}, {'¶', 537}, {'·', 278},
            {'¸', 278}, {'¹', 333}, {'º', 365}, {'»', 556},
            {'¼', 834}, {'½', 834}, {'¾', 834}, {'¿', 611},
            {'À', 667}, {'Á', 667}, {'Â', 667}, {'Ã', 667},
            {'Ä', 667}, {'Å', 667}, {'Æ', 1000},{'Ç', 722},
            {'È', 667}, {'É', 667}, {'Ê', 667}, {'Ë', 667},
            {'Ì', 278}, {'Í', 278}, {'Î', 278}, {'Ï', 278},
            {'Ð', 722}, {'Ñ', 722}, {'Ò', 778}, {'Ó', 778},
            {'Ô', 778}, {'Õ', 778}, {'Ö', 778}, {'×', 584},
            {'Ø', 778}, {'Ù', 722}, {'Ú', 722}, {'Û', 722},
            {'Ü', 722}, {'Ý', 667}, {'Þ', 667}, {'ß', 611},
            {'à', 556}, {'á', 556}, {'â', 556}, {'ã', 556},
            {'ä', 556}, {'å', 556}, {'æ', 889}, {'ç', 500},
            {'è', 556}, {'é', 556}, {'ê', 556}, {'ë', 556},
            {'ì', 278}, {'í', 278}, {'î', 278}, {'ï', 278},
            {'ð', 556}, {'ñ', 556}, {'ò', 556}, {'ó', 556},
            {'ô', 556}, {'õ', 556}, {'ö', 556}, {'÷', 584},
            {'ø', 611}, {'ù', 556}, {'ú', 556}, {'û', 556},
            {'ü', 556}, {'ý', 500}, {'þ', 556}, {'ÿ', 500},
            // CP1252 range (0x80–0x9F → Unicode)
            {'€', 556}, {'‚', 222}, {'ƒ', 556}, {'„', 333},
            {'…', 1000},{'†', 556}, {'‡', 556}, {'ˆ', 333},
            {'‰', 1000},{'Š', 667}, {'‹', 333}, {'Œ', 1000},
            {'Ž', 611}, {'‘', 222}, {'’', 222}, {'“', 333},
            {'”', 333}, {'•', 350}, {'–', 556}, {'—', 1000},
            {'˜', 333}, {'™', 1000},{'š', 500}, {'›', 333},
            {'œ', 944}, {'ž', 500}, {'Ÿ', 667},
        };

        // ── Helvetica-Bold (+ BoldOblique) ────────────────────────────────────
        private static readonly Dictionary<char, double> _helveticaBoldWidths = new()
        {
            {' ', 278}, {'!', 333}, {'"', 474}, {'#', 556}, {'$', 556}, {'%', 889},
            // U+0027 is quotesingle (238), NOT quoteright (278) — see the remarks on this class.
            {'&', 722}, {'\'',238}, {'(', 333}, {')', 333}, {'*', 389}, {'+', 584},
            {',', 278}, {'-', 333}, {'.', 278}, {'/', 278},
            {'0', 556}, {'1', 556}, {'2', 556}, {'3', 556}, {'4', 556},
            {'5', 556}, {'6', 556}, {'7', 556}, {'8', 556}, {'9', 556},
            {':', 333}, {';', 333}, {'<', 584}, {'=', 584}, {'>', 584},
            {'?', 611}, {'@', 975},
            {'A', 722}, {'B', 722}, {'C', 722}, {'D', 722}, {'E', 667}, {'F', 611},
            {'G', 778}, {'H', 722}, {'I', 278}, {'J', 556}, {'K', 722}, {'L', 611},
            {'M', 833}, {'N', 722}, {'O', 778}, {'P', 667}, {'Q', 778}, {'R', 722},
            {'S', 667}, {'T', 611}, {'U', 722}, {'V', 667}, {'W', 944}, {'X', 667},
            {'Y', 667}, {'Z', 611},
            // U+0060 is grave (333), NOT quoteleft (278).
            {'[', 333}, {'\\',278}, {']', 333}, {'^', 584}, {'_', 556}, {'`', 333},
            {'a', 556}, {'b', 611}, {'c', 556}, {'d', 611}, {'e', 556}, {'f', 333},
            {'g', 611}, {'h', 611}, {'i', 278}, {'j', 278}, {'k', 556}, {'l', 278},
            {'m', 889}, {'n', 611}, {'o', 611}, {'p', 611}, {'q', 611}, {'r', 389},
            {'s', 556}, {'t', 333}, {'u', 611}, {'v', 556}, {'w', 778}, {'x', 556},
            {'y', 556}, {'z', 500},
            {'{', 389}, {'|', 280}, {'}', 389}, {'~', 584},
            {' ', 278}, {'¡', 333}, {'¢', 556}, {'£', 556},
            {'¤', 556}, {'¥', 556}, {'¦', 280}, {'§', 556},
            {'¨', 333}, {'©', 737}, {'ª', 400}, {'«', 556},
            {'¬', 584}, {'­', 333}, {'®', 737}, {'¯', 333},
            {'°', 400}, {'±', 584}, {'²', 333}, {'³', 333},
            {'´', 278}, {'µ', 611}, {'¶', 556}, {'·', 278},
            {'¸', 278}, {'¹', 333}, {'º', 400}, {'»', 556},
            {'¼', 834}, {'½', 834}, {'¾', 834}, {'¿', 611},
            {'À', 722}, {'Á', 722}, {'Â', 722}, {'Ã', 722},
            {'Ä', 722}, {'Å', 722}, {'Æ', 1000},{'Ç', 722},
            {'È', 667}, {'É', 667}, {'Ê', 667}, {'Ë', 667},
            {'Ì', 278}, {'Í', 278}, {'Î', 278}, {'Ï', 278},
            {'Ð', 722}, {'Ñ', 722}, {'Ò', 778}, {'Ó', 778},
            {'Ô', 778}, {'Õ', 778}, {'Ö', 778}, {'×', 584},
            {'Ø', 778}, {'Ù', 722}, {'Ú', 722}, {'Û', 722},
            {'Ü', 722}, {'Ý', 667}, {'Þ', 667}, {'ß', 611},
            {'à', 556}, {'á', 556}, {'â', 556}, {'ã', 556},
            {'ä', 556}, {'å', 556}, {'æ', 889}, {'ç', 556},
            {'è', 556}, {'é', 556}, {'ê', 556}, {'ë', 556},
            {'ì', 278}, {'í', 278}, {'î', 278}, {'ï', 278},
            {'ð', 611}, {'ñ', 611}, {'ò', 611}, {'ó', 611},
            {'ô', 611}, {'õ', 611}, {'ö', 611}, {'÷', 584},
            {'ø', 611}, {'ù', 611}, {'ú', 611}, {'û', 611},
            {'ü', 611}, {'ý', 556}, {'þ', 611}, {'ÿ', 556},
            {'€', 556}, {'‚', 278}, {'ƒ', 556}, {'„', 500},
            {'…', 1000},{'†', 556}, {'‡', 556}, {'ˆ', 333},
            {'‰', 1000},{'Š', 667}, {'‹', 333}, {'Œ', 1000},
            {'Ž', 611}, {'‘', 278}, {'’', 278}, {'“', 500},
            {'”', 500}, {'•', 350}, {'–', 556}, {'—', 1000},
            {'˜', 333}, {'™', 1000},{'š', 556}, {'›', 333},
            {'œ', 944}, {'ž', 500}, {'Ÿ', 667},
        };

        // ── Times-Roman ──────────────────────────────────────────────────────
        private static readonly Dictionary<char, double> _timesRomanWidths = new()
        {
            {' ', 250}, {'!', 333}, {'"', 408}, {'#', 500}, {'$', 500}, {'%', 833},
            {'&', 778}, {'\'',180}, {'(', 333}, {')', 333}, {'*', 500}, {'+', 564},
            {',', 250}, {'-', 333}, {'.', 250}, {'/', 278},
            {'0', 500}, {'1', 500}, {'2', 500}, {'3', 500}, {'4', 500},
            {'5', 500}, {'6', 500}, {'7', 500}, {'8', 500}, {'9', 500},
            {':', 278}, {';', 278}, {'<', 564}, {'=', 564}, {'>', 564},
            {'?', 444}, {'@', 921},
            {'A', 722}, {'B', 667}, {'C', 667}, {'D', 722}, {'E', 611}, {'F', 556},
            {'G', 722}, {'H', 722}, {'I', 333}, {'J', 389}, {'K', 722}, {'L', 611},
            {'M', 889}, {'N', 722}, {'O', 722}, {'P', 556}, {'Q', 722}, {'R', 667},
            {'S', 556}, {'T', 611}, {'U', 722}, {'V', 722}, {'W', 944}, {'X', 722},
            {'Y', 722}, {'Z', 611},
            {'[', 333}, {'\\',278}, {']', 333}, {'^', 469}, {'_', 500}, {'`', 333},
            {'a', 444}, {'b', 500}, {'c', 444}, {'d', 500}, {'e', 444}, {'f', 333},
            {'g', 500}, {'h', 500}, {'i', 278}, {'j', 278}, {'k', 500}, {'l', 278},
            {'m', 778}, {'n', 500}, {'o', 500}, {'p', 500}, {'q', 500}, {'r', 333},
            {'s', 389}, {'t', 278}, {'u', 500}, {'v', 500}, {'w', 722}, {'x', 500},
            {'y', 500}, {'z', 444},
            {'{', 480}, {'|', 200}, {'}', 480}, {'~', 541},
            {' ', 250}, {'¡', 333}, {'¢', 500}, {'£', 500},
            {'¤', 500}, {'¥', 500}, {'¦', 200}, {'§', 500},
            {'¨', 333}, {'©', 760}, {'ª', 276}, {'«', 500},
            {'¬', 564}, {'­', 333}, {'®', 760}, {'¯', 333},
            {'°', 400}, {'±', 564}, {'²', 300}, {'³', 300},
            {'´', 333}, {'µ', 500}, {'¶', 453}, {'·', 250},
            {'¸', 333}, {'¹', 300}, {'º', 310}, {'»', 500},
            {'¼', 750}, {'½', 750}, {'¾', 750}, {'¿', 444},
            {'À', 722}, {'Á', 722}, {'Â', 722}, {'Ã', 722},
            {'Ä', 722}, {'Å', 722}, {'Æ', 889}, {'Ç', 667},
            {'È', 611}, {'É', 611}, {'Ê', 611}, {'Ë', 611},
            {'Ì', 333}, {'Í', 333}, {'Î', 333}, {'Ï', 333},
            {'Ð', 722}, {'Ñ', 722}, {'Ò', 722}, {'Ó', 722},
            {'Ô', 722}, {'Õ', 722}, {'Ö', 722}, {'×', 564},
            {'Ø', 722}, {'Ù', 722}, {'Ú', 722}, {'Û', 722},
            {'Ü', 722}, {'Ý', 722}, {'Þ', 556}, {'ß', 500},
            {'à', 444}, {'á', 444}, {'â', 444}, {'ã', 444},
            {'ä', 444}, {'å', 444}, {'æ', 667}, {'ç', 444},
            {'è', 444}, {'é', 444}, {'ê', 444}, {'ë', 444},
            {'ì', 278}, {'í', 278}, {'î', 278}, {'ï', 278},
            {'ð', 500}, {'ñ', 500}, {'ò', 500}, {'ó', 500},
            {'ô', 500}, {'õ', 500}, {'ö', 500}, {'÷', 564},
            {'ø', 500}, {'ù', 500}, {'ú', 500}, {'û', 500},
            {'ü', 500}, {'ý', 500}, {'þ', 500}, {'ÿ', 500},
            {'€', 500}, {'‘', 333}, {'’', 333},
            {'“', 444}, {'”', 444}, {'–', 500}, {'—', 1000},
            {'™', 980}, {'Š', 556}, {'š', 389}, {'Œ', 722},
            {'œ', 500}, {'Ÿ', 722}, {'Ž', 611}, {'ž', 444},
        };

        // ── Times-Bold ───────────────────────────────────────────────────────
        private static readonly Dictionary<char, double> _timesBoldWidths = new()
        {
            {' ', 250}, {'!', 333}, {'"', 555}, {'#', 500}, {'$', 500}, {'%', 1000},
            {'&', 833}, {'\'',278}, {'(', 333}, {')', 333}, {'*', 500}, {'+', 570},
            {',', 250}, {'-', 333}, {'.', 250}, {'/', 278},
            {'0', 500}, {'1', 500}, {'2', 500}, {'3', 500}, {'4', 500},
            {'5', 500}, {'6', 500}, {'7', 500}, {'8', 500}, {'9', 500},
            {':', 333}, {';', 333}, {'<', 570}, {'=', 570}, {'>', 570},
            {'?', 500}, {'@', 930},
            {'A', 722}, {'B', 667}, {'C', 722}, {'D', 722}, {'E', 667}, {'F', 611},
            {'G', 778}, {'H', 778}, {'I', 389}, {'J', 500}, {'K', 778}, {'L', 667},
            {'M', 944}, {'N', 722}, {'O', 778}, {'P', 611}, {'Q', 778}, {'R', 722},
            {'S', 556}, {'T', 667}, {'U', 722}, {'V', 722}, {'W', 1000},{'X', 722},
            {'Y', 722}, {'Z', 667},
            {'[', 333}, {'\\',278}, {']', 333}, {'^', 581}, {'_', 500}, {'`', 333},
            {'a', 500}, {'b', 556}, {'c', 444}, {'d', 556}, {'e', 444}, {'f', 333},
            {'g', 500}, {'h', 556}, {'i', 278}, {'j', 333}, {'k', 556}, {'l', 278},
            {'m', 833}, {'n', 556}, {'o', 500}, {'p', 556}, {'q', 556}, {'r', 444},
            {'s', 389}, {'t', 333}, {'u', 556}, {'v', 500}, {'w', 722}, {'x', 500},
            {'y', 500}, {'z', 444},
            {'{', 394}, {'|', 220}, {'}', 394}, {'~', 520},
            {' ', 250}, {'¡', 333}, {'¢', 500}, {'£', 500},
            {'¤', 500}, {'¥', 500}, {'¦', 220}, {'§', 500},
            {'¨', 333}, {'©', 747}, {'ª', 300}, {'«', 500},
            {'¬', 570}, {'­', 333}, {'®', 747}, {'¯', 333},
            {'°', 400}, {'±', 570}, {'²', 300}, {'³', 300},
            {'´', 333}, {'µ', 556}, {'¶', 540}, {'·', 250},
            {'¸', 333}, {'¹', 300}, {'º', 330}, {'»', 500},
            {'¼', 750}, {'½', 750}, {'¾', 750}, {'¿', 500},
            {'À', 722}, {'Á', 722}, {'Â', 722}, {'Ã', 722},
            {'Ä', 722}, {'Å', 722}, {'Æ', 1000},{'Ç', 722},
            {'È', 667}, {'É', 667}, {'Ê', 667}, {'Ë', 667},
            {'Ì', 389}, {'Í', 389}, {'Î', 389}, {'Ï', 389},
            {'Ð', 722}, {'Ñ', 722}, {'Ò', 778}, {'Ó', 778},
            {'Ô', 778}, {'Õ', 778}, {'Ö', 778}, {'×', 570},
            {'Ø', 778}, {'Ù', 722}, {'Ú', 722}, {'Û', 722},
            {'Ü', 722}, {'Ý', 722}, {'Þ', 611}, {'ß', 556},
            {'à', 500}, {'á', 500}, {'â', 500}, {'ã', 500},
            {'ä', 500}, {'å', 500}, {'æ', 722}, {'ç', 444},
            {'è', 444}, {'é', 444}, {'ê', 444}, {'ë', 444},
            {'ì', 278}, {'í', 278}, {'î', 278}, {'ï', 278},
            {'ð', 500}, {'ñ', 556}, {'ò', 500}, {'ó', 500},
            {'ô', 500}, {'õ', 500}, {'ö', 500}, {'÷', 570},
            {'ø', 500}, {'ù', 556}, {'ú', 556}, {'û', 556},
            {'ü', 556}, {'ý', 500}, {'þ', 556}, {'ÿ', 500},
            {'€', 500}, {'‘', 333}, {'’', 333},
            {'“', 500}, {'”', 500}, {'–', 500}, {'—', 1000},
            {'™', 1000},{'Š', 556}, {'š', 389}, {'Œ', 722},
            {'œ', 556}, {'Ÿ', 722}, {'Ž', 667}, {'ž', 444},
        };

        // ── Times-Italic ─────────────────────────────────────────────────────
        private static readonly Dictionary<char, double> _timesItalicWidths = new()
        {
            {' ', 250}, {'!', 333}, {'"', 420}, {'#', 500}, {'$', 500}, {'%', 833},
            {'&', 778}, {'\'',214}, {'(', 333}, {')', 333}, {'*', 500}, {'+', 675},
            {',', 250}, {'-', 333}, {'.', 250}, {'/', 278},
            {'0', 500}, {'1', 500}, {'2', 500}, {'3', 500}, {'4', 500},
            {'5', 500}, {'6', 500}, {'7', 500}, {'8', 500}, {'9', 500},
            {':', 333}, {';', 333}, {'<', 675}, {'=', 675}, {'>', 675},
            {'?', 500}, {'@', 920},
            {'A', 611}, {'B', 611}, {'C', 667}, {'D', 722}, {'E', 611}, {'F', 611},
            {'G', 722}, {'H', 722}, {'I', 333}, {'J', 444}, {'K', 667}, {'L', 556},
            {'M', 833}, {'N', 667}, {'O', 722}, {'P', 611}, {'Q', 722}, {'R', 611},
            {'S', 500}, {'T', 556}, {'U', 722}, {'V', 611}, {'W', 833}, {'X', 611},
            {'Y', 556}, {'Z', 556},
            {'[', 389}, {'\\',278}, {']', 389}, {'^', 422}, {'_', 500}, {'`', 333},
            {'a', 500}, {'b', 500}, {'c', 444}, {'d', 500}, {'e', 444}, {'f', 278},
            {'g', 500}, {'h', 500}, {'i', 278}, {'j', 278}, {'k', 444}, {'l', 278},
            {'m', 722}, {'n', 500}, {'o', 500}, {'p', 500}, {'q', 500}, {'r', 389},
            {'s', 389}, {'t', 278}, {'u', 500}, {'v', 444}, {'w', 667}, {'x', 444},
            {'y', 444}, {'z', 389},
            {'{', 400}, {'|', 275}, {'}', 400}, {'~', 541},
            {' ', 250}, {'¡', 389}, {'¢', 500}, {'£', 500},
            {'¤', 500}, {'¥', 500}, {'¦', 275}, {'§', 500},
            {'¨', 333}, {'©', 760}, {'ª', 276}, {'«', 500},
            {'¬', 675}, {'­', 333}, {'®', 760}, {'¯', 333},
            {'°', 400}, {'±', 675}, {'²', 300}, {'³', 300},
            {'´', 333}, {'µ', 500}, {'¶', 523}, {'·', 250},
            {'¸', 333}, {'¹', 300}, {'º', 310}, {'»', 500},
            {'¼', 750}, {'½', 750}, {'¾', 750}, {'¿', 500},
            {'À', 611}, {'Á', 611}, {'Â', 611}, {'Ã', 611},
            {'Ä', 611}, {'Å', 611}, {'Æ', 889}, {'Ç', 667},
            {'È', 611}, {'É', 611}, {'Ê', 611}, {'Ë', 611},
            {'Ì', 333}, {'Í', 333}, {'Î', 333}, {'Ï', 333},
            {'Ð', 722}, {'Ñ', 667}, {'Ò', 722}, {'Ó', 722},
            {'Ô', 722}, {'Õ', 722}, {'Ö', 722}, {'×', 675},
            {'Ø', 722}, {'Ù', 722}, {'Ú', 722}, {'Û', 722},
            {'Ü', 722}, {'Ý', 556}, {'Þ', 611}, {'ß', 500},
            {'à', 500}, {'á', 500}, {'â', 500}, {'ã', 500},
            {'ä', 500}, {'å', 500}, {'æ', 667}, {'ç', 444},
            {'è', 444}, {'é', 444}, {'ê', 444}, {'ë', 444},
            {'ì', 278}, {'í', 278}, {'î', 278}, {'ï', 278},
            {'ð', 500}, {'ñ', 500}, {'ò', 500}, {'ó', 500},
            {'ô', 500}, {'õ', 500}, {'ö', 500}, {'÷', 675},
            {'ø', 500}, {'ù', 500}, {'ú', 500}, {'û', 500},
            {'ü', 500}, {'ý', 444}, {'þ', 500}, {'ÿ', 444},
            {'€', 500}, {'‘', 333}, {'’', 333},
            {'“', 556}, {'”', 556}, {'–', 500}, {'—', 889},
            {'™', 980}, {'Š', 500}, {'š', 389}, {'Œ', 944},
            {'œ', 556}, {'Ÿ', 556}, {'Ž', 556}, {'ž', 389},
        };

        // ── Times-BoldItalic ─────────────────────────────────────────────────
        private static readonly Dictionary<char, double> _timesBoldItalicWidths = new()
        {
            {' ', 250}, {'!', 389}, {'"', 555}, {'#', 500}, {'$', 500}, {'%', 833},
            {'&', 778}, {'\'',278}, {'(', 333}, {')', 333}, {'*', 500}, {'+', 570},
            {',', 250}, {'-', 333}, {'.', 250}, {'/', 278},
            {'0', 500}, {'1', 500}, {'2', 500}, {'3', 500}, {'4', 500},
            {'5', 500}, {'6', 500}, {'7', 500}, {'8', 500}, {'9', 500},
            {':', 333}, {';', 333}, {'<', 570}, {'=', 570}, {'>', 570},
            {'?', 500}, {'@', 832},
            {'A', 667}, {'B', 667}, {'C', 667}, {'D', 722}, {'E', 667}, {'F', 667},
            {'G', 722}, {'H', 778}, {'I', 389}, {'J', 500}, {'K', 667}, {'L', 611},
            {'M', 889}, {'N', 722}, {'O', 722}, {'P', 611}, {'Q', 722}, {'R', 667},
            {'S', 556}, {'T', 611}, {'U', 722}, {'V', 667}, {'W', 889}, {'X', 667},
            {'Y', 611}, {'Z', 611},
            {'[', 333}, {'\\',278}, {']', 333}, {'^', 570}, {'_', 500}, {'`', 333},
            {'a', 500}, {'b', 500}, {'c', 444}, {'d', 500}, {'e', 444}, {'f', 333},
            {'g', 500}, {'h', 556}, {'i', 278}, {'j', 278}, {'k', 500}, {'l', 278},
            {'m', 778}, {'n', 556}, {'o', 500}, {'p', 500}, {'q', 500}, {'r', 389},
            {'s', 389}, {'t', 278}, {'u', 556}, {'v', 444}, {'w', 667}, {'x', 500},
            {'y', 444}, {'z', 389},
            {'{', 348}, {'|', 220}, {'}', 348}, {'~', 570},
            {' ', 250}, {'¡', 389}, {'¢', 500}, {'£', 500},
            {'¤', 500}, {'¥', 500}, {'¦', 220}, {'§', 500},
            {'¨', 333}, {'©', 747}, {'ª', 266}, {'«', 500},
            {'¬', 606}, {'­', 333}, {'®', 747}, {'¯', 333},
            {'°', 400}, {'±', 570}, {'²', 300}, {'³', 300},
            {'´', 333}, {'µ', 556}, {'¶', 500}, {'·', 250},
            {'¸', 333}, {'¹', 300}, {'º', 300}, {'»', 500},
            {'¼', 750}, {'½', 750}, {'¾', 750}, {'¿', 500},
            {'À', 667}, {'Á', 667}, {'Â', 667}, {'Ã', 667},
            {'Ä', 667}, {'Å', 667}, {'Æ', 944}, {'Ç', 667},
            {'È', 667}, {'É', 667}, {'Ê', 667}, {'Ë', 667},
            {'Ì', 389}, {'Í', 389}, {'Î', 389}, {'Ï', 389},
            {'Ð', 722}, {'Ñ', 722}, {'Ò', 722}, {'Ó', 722},
            {'Ô', 722}, {'Õ', 722}, {'Ö', 722}, {'×', 570},
            {'Ø', 722}, {'Ù', 722}, {'Ú', 722}, {'Û', 722},
            {'Ü', 722}, {'Ý', 611}, {'Þ', 611}, {'ß', 500},
            {'à', 500}, {'á', 500}, {'â', 500}, {'ã', 500},
            {'ä', 500}, {'å', 500}, {'æ', 722}, {'ç', 444},
            {'è', 444}, {'é', 444}, {'ê', 444}, {'ë', 444},
            {'ì', 278}, {'í', 278}, {'î', 278}, {'ï', 278},
            {'ð', 500}, {'ñ', 556}, {'ò', 500}, {'ó', 500},
            {'ô', 500}, {'õ', 500}, {'ö', 500}, {'÷', 570},
            {'ø', 500}, {'ù', 556}, {'ú', 556}, {'û', 556},
            {'ü', 556}, {'ý', 444}, {'þ', 500}, {'ÿ', 444},
            {'€', 500}, {'‘', 333}, {'’', 333},
            {'“', 500}, {'”', 500}, {'–', 500}, {'—', 1000},
            {'™', 1000},{'Š', 556}, {'š', 389}, {'Œ', 944},
            {'œ', 500}, {'Ÿ', 611}, {'Ž', 611}, {'ž', 389},
        };
    }
}
