// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The state and the three code-indexed lookups every <b>simple font</b> (§9.6) shares:
    /// a single-byte code addresses one <c>/Widths</c> entry and one character.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this class exists.</b> <see cref="PdfType1Font"/> and
    /// <see cref="PdfTrueTypeFont"/> share <see cref="HasExplicitWidth"/>, <see cref="Decode(byte[])"/>
    /// and the <c>/Widths</c> lookup over one set of fields: a rule such as §9.6.6.4's implicit base
    /// encoding changed in one copy only would extract the same bytes as U+2019 through one class
    /// and U+0027 through the other.</para>
    ///
    /// <para><b>Why not on <see cref="PdfFont"/> itself.</b> <see cref="PdfType0Font"/> also
    /// derives from it and declares its own <c>_toUnicode</c> and <c>_defaultWidth</c> with
    /// <i>different</i> meanings — a composite font's code is not a byte and its default width is
    /// <c>/DW</c>, which defaults to 1000 rather than to 0. Hoisting these fields all the way up
    /// would shadow those, which is a silent wrong-value hazard rather than a compile error at the
    /// use sites. The shared home is therefore the layer that actually shares the semantics.</para>
    ///
    /// <para><b>The encoding ladder is here too.</b> <see cref="ParseDifferences"/> fills
    /// <see cref="_differenceGlyphNames"/> for <i>every</i> simple subtype, so two copies of the
    /// decision would only drift (the implicit base with no <c>/Encoding</c>, the implicit base with
    /// no <c>/BaseEncoding</c>, an <c>/Encoding</c> that is neither a name nor a dictionary).
    /// <see cref="ReadEncoding"/> is the one ladder; <see cref="ReadFontDescriptor"/> and
    /// <see cref="ImplicitBaseEncoding"/> are what it decides from, and the constructors share the
    /// Symbolic-flag test there. The Standard-14 width tier is here too:
    /// <see cref="PdfType1Font"/> does not override <see cref="GetWidth"/>.</para>
    /// </remarks>
    internal abstract class PdfSimpleFont : PdfFont
    {
        /// <summary>Code → character from the font's base encoding (§9.6.6). Never null once a
        /// subclass constructor has run: every branch of the /Encoding ladder assigns it.</summary>
        private protected Dictionary<int, char> _encoding;

        /// <summary>The parsed <c>/ToUnicode</c> CMap, or <c>null</c> when the font has none (or
        /// when it is a built-in-encoded font whose /ToUnicode is not to be trusted, §9.6.6.4).</summary>
        private protected Dictionary<int, string> _toUnicode;

        /// <summary>Code → character named by <c>/Encoding /Differences</c>. Populated by
        /// <see cref="ParseDifferences"/>, alongside <see cref="_differenceGlyphNames"/>.</summary>
        private protected readonly Dictionary<int, char> _differences = new Dictionary<int, char>();

        /// <summary>
        /// A simple font addresses glyphs with single-byte codes (§9.6.6.1), so no code above 255
        /// is reachable. Bounds the per-code side tables built from <c>/Differences</c>, whose array
        /// length is otherwise attacker-controlled.
        /// </summary>
        private protected const int MaxSimpleFontCode = 255;

        /// <summary>
        /// Code → the raw glyph name <c>/Encoding /Differences</c> assigns, whether or not the Adobe
        /// Glyph List knows it. Populated by <see cref="ParseDifferences"/> for every simple subtype.
        /// </summary>
        /// <remarks>
        /// Two readers need the name rather than the character: §9.6.6.4 resolves a TrueType
        /// <c>/Differences</c> name against the program's <c>post</c> table before the glyph list
        /// (<see cref="PdfTrueTypeFont"/>), and PDF/UA-2 §8.4.5.8 asks whether the names of the glyphs
        /// a Type 1 or Type 3 font draws are all in the glyph list.
        /// </remarks>
        private protected readonly Dictionary<int, string> _differenceGlyphNames = new Dictionary<int, string>();

        /// <summary>See <see cref="_differenceGlyphNames"/>. Codes are bounded to 0–255.</summary>
        internal IReadOnlyDictionary<int, string> DifferenceGlyphNames => _differenceGlyphNames;

        /// <summary><c>/FirstChar</c> — the origin of <see cref="_widths"/>, not a neutral
        /// sentinel: it is subtracted from every code before indexing.</summary>
        private protected int _firstChar;

        /// <summary><c>/Widths</c>, indexed by <c>code - _firstChar</c>; <c>null</c> when the font
        /// dictionary has no <c>/Widths</c> at all.</summary>
        private protected double[] _widths;

        /// <summary><c>/MissingWidth</c> from the <c>/FontDescriptor</c>, or 0 when absent
        /// (§9.6.1, Table 122's default).</summary>
        private protected double _defaultWidth;

        /// <summary>
        /// <c>/FontDescriptor /Flags</c> bit 3 (value 4) — <b>Symbolic</b>, ISO 32000-1 Table 123.
        /// Bit 6 (value 32) is <b>Nonsymbolic</b>.
        /// </summary>
        private const long SymbolicFlag = 1 << 2;

        /// <summary>
        /// The three things both simple-font subtypes read the same way before they diverge:
        /// <c>/BaseFont</c>, whether the font is built-in encoded (which decides if
        /// <c>/ToUnicode</c> is honoured), and the <c>/FirstChar</c> + <c>/Widths</c> pair.
        /// Returns the <c>/BaseFont</c> value for the caller to assign.
        /// </summary>
        /// <remarks>
        /// <para>Extracted for the same reason as <see cref="ReadFontDescriptor"/> directly below,
        /// and the comment beside its old call sites had already named the principle — "one
        /// implementation for both simple subtypes, in <c>PdfSimpleFont</c>" — while this block
        /// above it stayed copied. The two copies still agreed; the point is that nothing was
        /// keeping them that way, and the descriptor read they sit beside is a worked example of
        /// what happens when that goes on long enough (an indirect <c>/Flags</c> measured
        /// nonsymbolic through one subtype and symbolic through the other).</para>
        ///
        /// <para><b>The <c>/Widths</c> read looks redundant and is not.</b>
        /// <see cref="PdfDictionary.GetArray"/> does not follow an indirect reference, so a
        /// perfectly legal <c>/Widths 9 0 R</c> reads as <i>absent</i> — indistinguishable from a
        /// font that supplies none, which then measures every glyph at the default width. The
        /// second lookup resolves it. Do not "simplify" the pair away.</para>
        ///
        /// <para><b><c>/BaseFont</c> and each <c>/Widths</c> element resolve too</b> (§7.3.10 covers
        /// array elements), as <c>/FirstChar</c> and the descriptor do. It is not cosmetic: the
        /// Standard-14 AFM fallback in <see cref="GetWidth"/> is keyed on the name, so a raw read of
        /// <c>/BaseFont 9 0 R</c> with no <c>/Widths</c> would measure every glyph at
        /// <c>/MissingWidth</c>, and the SVG exporter, which names its font family from
        /// <c>BaseFont</c>, would name none.</para>
        ///
        /// <para><b>§9.6.6.4: Symbol and ZapfDingbats always use their built-in encoding</b>, so a
        /// <c>/ToUnicode</c> CMap for one is discarded rather than trusted. Some producers write an
        /// incorrect one for these fonts, and honouring it maps the glyphs to the wrong characters.
        /// <c>/LastChar</c> is deliberately not read: it is redundant with the array's own length,
        /// which is what the width lookup indexes against.</para>
        /// </remarks>
        private protected string ReadBaseFontAndWidths(
            PdfDictionary fontDict, PdfDocument document, Dictionary<int, string> toUnicode)
        {
            string baseFont = ResolvedName(fontDict, PdfName.BaseFont, document)?.Value;

            bool isBuiltInEncoded = Standard14Metrics.HasBuiltInEncoding(baseFont);
            _toUnicode = isBuiltInEncoded ? null : toUnicode;

            _firstChar = ResolvedInt(fontDict, PdfName.FirstChar, document) ?? 0;

            var widthsArr = fontDict.GetArray(PdfName.Widths);
            if (widthsArr == null && document != null)
            {
                var wo = fontDict[PdfName.Widths];
                if (wo != null) widthsArr = document.ResolveObject(wo) as PdfArray;
            }
            if (widthsArr != null && widthsArr.Count > 0)
            {
                _widths = new double[widthsArr.Count];
                for (int i = 0; i < widthsArr.Count; i++)
                    _widths[i] = ((document?.ResolveObject(widthsArr[i]) ?? widthsArr[i]) as PdfNumber)?.DoubleValue ?? 0;
            }

            return baseFont;
        }

        /// <summary>
        /// Reads the two <c>/FontDescriptor</c> entries both simple subtypes need — <c>/MissingWidth</c>
        /// into <see cref="_defaultWidth"/>, and §9.6.6.4's <b>Symbolic</b> flag, which is returned.
        /// Both reads follow an indirect reference. A font with no descriptor keeps
        /// <c>/MissingWidth</c>'s default of 0 and is not symbolic.
        /// </summary>
        /// <remarks>
        /// <para><b>One symbolic test for both subtypes, and it is Table 123's bit 3 alone</b> —
        /// not "bit 3 set <i>and</i> bit 6 clear" for one subtype and bit 3 for the other.</para>
        ///
        /// <para><b>Table 123 makes the extra clause dead code in every conforming file</b>, which
        /// is what settles it: Symbolic and Nonsymbolic shall not both be set nor both be clear, so
        /// bit 3 alone and "bit 3 and not bit 6" differ only for a <b>malformed</b> descriptor that
        /// sets both. The two-clause form is therefore not a spec rule but a policy about invalid
        /// input — and nothing in §9.6.6.4 makes such a policy depend on the subtype. Bit 3 alone is
        /// what §9.6.6.4's own prose keys on, what every other reader does, and what three of the
        /// five sites in this tree already did. It is also the safer direction to unify in: on the
        /// TrueType side this bit gates <see cref="PdfTrueTypeFont.UsesBuiltInEncoding"/> and hence
        /// real glyph selection, so adopting the stricter test there would have changed what a
        /// symbol font <i>draws</i> — whereas adopting bit 3 on the Type 1 side changes only the
        /// implicit base encoding, and only for a descriptor that contradicts itself.</para>
        ///
        /// <para><b><c>/Flags</c> is read resolving.</b> The Type 1 copy read it straight off the
        /// dictionary, so a perfectly legal <c>/Flags 12 0 R</c> read as <b>absent</b> — i.e. 0,
        /// i.e. nonsymbolic — while its TrueType sibling resolved the same reference and answered
        /// the opposite. Same for <c>/MissingWidth</c>: an indirect one measured 0 through Type 1
        /// and its real value through TrueType.</para>
        /// </remarks>
        private protected bool ReadFontDescriptor(PdfDictionary fontDict, PdfDocument document)
        {
            var descriptor = Resolved(fontDict, PdfName.FontDescriptor, document) as PdfDictionary;
            if (descriptor == null) return false;

            double? missingWidth = ResolvedDouble(descriptor, PdfName.MissingWidth, document);
            if (missingWidth.HasValue) _defaultWidth = missingWidth.Value;

            long flags = (Resolved(descriptor, PdfName.Flags, document) as PdfNumber)?.LongValue ?? 0;
            return (flags & SymbolicFlag) != 0;
        }

        /// <summary>
        /// Walks an <c>/Encoding /Differences</c> array (§9.6.6.1): a number resets the code, a name
        /// assigns the glyph at that code and advances it. Each name that the Adobe glyph list knows
        /// contributes a character to <see cref="_differences"/>, and every name, known or not, is
        /// kept in <see cref="_differenceGlyphNames"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>One walk for both simple subtypes</b>, beside <see cref="ReadFontDescriptor"/>
        /// and <see cref="ReadBaseFontAndWidths"/>. The raw names are kept here, for every subtype,
        /// since a second reader needs them for Type 1 and Type 3.</para>
        ///
        /// <para><b>An unrecognised name is not skipped, it is stepped over.</b> <c>currentCode</c>
        /// advances for every name whether or not the glyph list knows it, because §9.6.6.1 numbers
        /// the names positionally — treating an unknown name as absent would shift every later
        /// glyph in the run by one.</para>
        /// </remarks>
        private protected void ParseDifferences(PdfArray diffs)
        {
            int currentCode = 0;
            for (int i = 0; i < diffs.Count; i++)
            {
                if (diffs[i] is PdfNumber num)
                {
                    currentCode = num.IntValue;
                }
                else if (diffs[i] is PdfName name)
                {
                    if (StandardEncodings.TryGetGlyphUnicode(name.Value, out char unicode))
                        _differences[currentCode] = unicode;

                    if (currentCode >= 0 && currentCode <= MaxSimpleFontCode)
                        _differenceGlyphNames[currentCode] = name.Value;
                    currentCode++;
                }
            }
        }

        /// <summary>
        /// §9.6.6.4's base encoding for a font that names none — neither an <c>/Encoding</c> entry,
        /// nor a <c>/BaseEncoding</c> inside one: <b>StandardEncoding</b>, not WinAnsiEncoding.
        /// </summary>
        /// <remarks>
        /// A symbolic font keeps WinAnsi for the <i>text-extraction</i> view only: its codes select
        /// glyphs from the font program's own built-in table rather than naming characters, so there
        /// is no right answer, and <c>/ToUnicode</c> — which wins over this in
        /// <see cref="Decode(byte[])"/> — is the producer's way to say what the text was.
        ///
        /// <para>This is called at <b>three</b> sites per subtype: no <c>/Encoding</c>, an
        /// <c>/Encoding</c> dictionary with no <c>/BaseEncoding</c>, and an <c>/Encoding</c> whose
        /// value is neither a name nor a dictionary. An unusable value (a number, a reference that does
        /// not resolve) is the same state as an absent one; both reach the implicit base, for every
        /// subtype — hardcoding WinAnsi at the third site would give a <b>nonsymbolic</b> font
        /// WinAnsi from one subtype and StandardEncoding from the other.</para>
        /// </remarks>
        private protected static Dictionary<int, char> ImplicitBaseEncoding(bool symbolic)
            => symbolic
                ? StandardEncodings.WinAnsiEncoding
                : StandardEncodings.AdobeStandardEncoding;

        /// <summary>
        /// §9.6.6 in full, for both simple subtypes: assigns <see cref="_encoding"/> and, where the
        /// font carries an <c>/Encoding</c> dictionary, walks its <c>/Differences</c>. Returns
        /// whether the font dictionary named an <c>/Encoding</c> at all — §9.6.6.4 needs that to
        /// decide whether a symbolic font's codes address the program's built-in cmap, and
        /// <see cref="PdfTrueTypeFont.UsesBuiltInEncoding"/> is the only caller that asks.
        /// </summary>
        /// <remarks>
        /// <para><b>One ladder, because two copies of it drifted three times.</b> Symbol and
        /// ZapfDingbats always use their own built-in tables whatever <c>/Encoding</c> says; a name
        /// selects a standard table; a dictionary takes <c>/BaseEncoding</c> if it has one and the
        /// implicit base if not, then applies <c>/Differences</c>; anything else — absent, a number,
        /// a reference that does not resolve — is the implicit base. Each of the last three sites is
        /// a place the two copies had already disagreed: byte <c>0x27</c> extracted as U+0027
        /// through <see cref="PdfType1Font"/> and U+2019 through <see cref="PdfTrueTypeFont"/> for
        /// months, and the "neither a name nor a dictionary" branch hardcoded WinAnsi on the
        /// TrueType side alone. Keeping them apart is what made that possible; each fix landing in
        /// one copy is what made it last.</para>
        ///
        /// <para><b><c>/BaseEncoding</c> and <c>/Differences</c> are read resolving</b> (§7.3.10):
        /// <see cref="PdfDictionary.GetArray"/> and its siblings do not follow an indirect
        /// reference, so <c>/Differences 9 0 R</c> reads as absent through the raw accessors — a
        /// font whose glyph re-mapping silently does nothing.</para>
        ///
        /// <para><b><see cref="PdfFont.BaseFont"/> must already be assigned</b> when this is called: the
        /// Symbol / ZapfDingbats test keys on it. Both subtypes call
        /// <see cref="ReadBaseFontAndWidths"/> and then <see cref="ReadFontDescriptor"/> first, and
        /// the <paramref name="symbolic"/> flag is the latter's answer.</para>
        /// </remarks>
        private protected bool ReadEncoding(PdfDictionary fontDict, PdfDocument document, bool symbolic)
        {
            PdfObject encodingObj = fontDict[PdfName.Encoding];
            bool hasEncodingEntry = encodingObj != null;

            // §9.6.6.4: Symbol and ZapfDingbats always use their own built-in encodings regardless
            // of any /Encoding entry in the font dictionary. "ZaDb" is the form-field alias for
            // ZapfDingbats as viewers use it.
            if (BaseFont == StandardFonts.Symbol)
            {
                _encoding = StandardEncodings.SymbolEncoding;
                return hasEncodingEntry;
            }
            if (BaseFont == StandardFonts.ZapfDingbats || BaseFont == StandardFonts.ZapfDingbatsAbbrev)
            {
                _encoding = StandardEncodings.ZapfDingbatsEncoding;
                return hasEncodingEntry;
            }

            Dictionary<int, char> implicitBase = ImplicitBaseEncoding(symbolic);

            // Deliberately NOT `document?.` — both copies dereferenced it, so guarding here would be
            // an untestable behaviour change riding along with a consolidation. PdfFont.Create is
            // the only construction path and it dereferences `document` itself.
            PdfObject resolved = encodingObj == null ? null : document.ResolveObject(encodingObj);
            if (resolved is PdfName encodingName)
            {
                _encoding = StandardEncodings.GetEncodingByName(encodingName.Value);
            }
            else if (resolved is PdfDictionary encodingDict)
            {
                PdfName baseEncoding = ResolvedName(encodingDict, PdfName.BaseEncoding, document);
                _encoding = baseEncoding != null
                    ? StandardEncodings.GetEncodingByName(baseEncoding.Value)
                    : implicitBase;

                PdfArray diffsArray = ResolvedArray(encodingDict, PdfName.Differences, document);
                if (diffsArray != null)
                    ParseDifferences(diffsArray);
            }
            else
            {
                _encoding = implicitBase;
            }

            return hasEncodingEntry;
        }

        /// <summary>
        /// The advance for one character code: its <c>/Widths</c> entry when the array covers it,
        /// else the Standard-14 built-in metric when the font has no <c>/Widths</c> at all
        /// (§9.6.2.2), else <c>/MissingWidth</c> (§9.6.1).
        /// </summary>
        /// <remarks>
        /// <para><b>The Standard-14 tier belongs to both simple subtypes, not just Type 1.</b>
        /// It lived on <see cref="PdfType1Font"/> alone, so a
        /// <c>/Subtype /TrueType /BaseFont /Arial</c> dictionary with no <c>/Widths</c> —
        /// which §9.6.3 Table 113 expressly permits, and which real producers emit — measured
        /// <c>/MissingWidth</c>, defaulting to <b>zero advance for every glyph</b>. Every glyph
        /// then piles up at one x position. The asymmetry was recorded as "genuinely
        /// per-subtype" in one place and as a live defect in another; Table 113 settles it, since
        /// it carries the same "Required except for the standard 14 fonts" note as Table 111.</para>
        ///
        /// <para><b>The name is canonicalised rather than matched exactly.</b> The Type 1 copy
        /// gated on <c>Standard14Metrics.IsStandard14</c>, which is an exact-name set — so
        /// <c>ArialMT</c>, <c>Arial,Bold</c>, <c>TimesNewRomanPSMT</c> and any subset-tagged
        /// <c>ABCDEF+Helvetica</c> all missed the tier that exists for them and fell through to
        /// zero. <see cref="Standard14Names.Canonical"/> is the alias resolver Core already has
        /// for exactly this question, and it returns <c>null</c> for a font that names none of
        /// the fourteen, so the fallback stays off for everything else.</para>
        ///
        /// <para><b><see cref="HasExplicitWidth"/> is deliberately NOT extended to match.</b> A
        /// width recovered from the AFM tables is not an explicit <c>/Widths</c> entry, and the
        /// callers that ask — space detection, combining-mark placement — need to tell the two
        /// apart. This note travelled here from the Type 1 override with the code.</para>
        /// </remarks>
        public override double GetWidth(int charCode)
        {
            if (_widths != null)
            {
                int idx = charCode - _firstChar;
                if (idx >= 0 && idx < _widths.Length)
                    return _widths[idx];

                // /Widths is present but does not cover this code: §9.6.1 says /MissingWidth.
                // The Standard-14 tier below is for a font with NO /Widths at all.
                return _defaultWidth;
            }

            string standard14 = Standard14Name();
            if (standard14 != null)
            {
                // Symbol and ZapfDingbats: by code through the built-in encoding (§9.6.6.4), unless
                // /Differences re-assigned this code — then by the character it resolved to, below.
                // Without these widths a /Symbol font with no /Widths would measure /MissingWidth —
                // every glyph on one point.
                if (!_differences.ContainsKey(charCode)
                    && Standard14Metrics.TryGetBuiltInGlyph(standard14, charCode, out _, out double builtIn))
                    return builtIn;

                char unicode = '\0';
                if (!_differences.TryGetValue(charCode, out unicode))
                    _encoding.TryGetValue(charCode, out unicode);
                if (unicode != '\0')
                {
                    double w = Standard14Metrics.GetWidthByUnicode(standard14, unicode);
                    if (w > 0) return w;
                }
            }

            return _defaultWidth;
        }

        /// <summary>
        /// <see cref="Standard14Names.Canonical"/> of <see cref="PdfFont.BaseFont"/>, worked out once —
        /// both subtype constructors set the name first, before anything can ask for a width.
        /// </summary>
        /// <remarks>
        /// <see cref="GetWidth"/> asked it for every glyph of a font with no <c>/Widths</c> — the
        /// standard-14 case — and the answer is string surgery (a lower-cased copy, substrings, two
        /// arrays): on a hundred-thousand-show document that was most of plain extraction's
        /// allocation, for a name that does not change. A holder, so a font that is none of the
        /// fourteen remembers its <c>null</c> too.
        /// </remarks>
        private string Standard14Name()
        {
            System.Tuple<string> known = _standard14;
            if (known == null)
            {
                _standard14 = known = System.Tuple.Create(Standard14Names.Canonical(BaseFont));
                Standard14NameComputations++;
            }
            return known.Item1;
        }

        private System.Tuple<string> _standard14;

        /// <summary>How many times <see cref="Standard14Name"/> worked the name out — the cache's evidence.</summary>
        internal int Standard14NameComputations { get; private set; }

        /// <inheritdoc/>
        public override bool HasExplicitWidth(int charCode)
        {
            if (_widths != null)
            {
                int idx = charCode - _firstChar;
                return idx >= 0 && idx < _widths.Length;
            }
            return false;
        }

        /// <summary>
        /// Maps show-text bytes to characters. Priority is <c>/ToUnicode</c>, then
        /// <c>/Differences</c>, then the base encoding, then the raw byte as a code point — the
        /// producer's own statement of what the text was wins over any encoding table.
        /// </summary>
        public override string Decode(byte[] bytes)
        {
            if (bytes.Length == 1) return DecodeByte(bytes[0]);

            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes) sb.Append(DecodeByte(b));
            return sb.ToString();
        }

        /// <summary>
        /// The character the font's own encoding gives <paramref name="code"/> — <c>/Differences</c>,
        /// then the base encoding — whatever <c>/ToUnicode</c> says. §9.6.6.4 selects a simple font's
        /// glyph through this character; <c>/ToUnicode</c> says what the text means, for extraction.
        /// </summary>
        internal bool TryGetEncodedCharacter(int code, out char character)
            => _differences.TryGetValue(code, out character) || _encoding.TryGetValue(code, out character);

        /// <summary>A one-byte code's text, decoded once per code (see <see cref="PdfFont.Decode(CMapCodec.Code)"/>).</summary>
        public override string Decode(CMapCodec.Code code)
            => code.ByteLength == 1 && code.Value >= 0 && code.Value <= 255
                ? DecodeByte((byte)code.Value)
                : base.Decode(code);

        /// <summary>
        /// One string per code, decoded on first use. The three tables it reads are filled by the
        /// constructors and never after, so a decoded code stays right; the slot is written at most
        /// once with the same value, so two threads sharing the font race benignly.
        /// </summary>
        private string[] _decodedByCode;

        private string DecodeByte(byte b)
        {
            string[] cache = _decodedByCode ??= new string[256];
            return cache[b] ??= DecodeByteUncached(b);
        }

        private string DecodeByteUncached(byte b)
        {
            if (_toUnicode != null && _toUnicode.TryGetValue(b, out string uStr)) return uStr;
            if (_differences.TryGetValue(b, out char diffChar)) return diffChar.ToString();
            if (_encoding.TryGetValue(b, out char encChar)) return encChar.ToString();
            return ((char)b).ToString();
        }
    }
}
