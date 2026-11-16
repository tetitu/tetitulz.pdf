// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A narrow public façade for comparing a simple font's declared width (<c>/Widths</c>) against
    /// its embedded program's own advance — built for ISO 19005-1:2005 Technical
    /// Corrigendum 1's §6.3.6 ("the glyph width information stored in the Widths entry ... and in
    /// the embedded font program shall be consistent"), for a caller that cannot see Core's font
    /// classes directly: <see cref="PdfFont"/>/<see cref="PdfSimpleFont"/>/<see cref="PdfTrueTypeFont"/>
    /// are <c>internal</c> and Core's <c>InternalsVisibleTo</c> deliberately names no plugin. Reuses
    /// the existing, tested logic entirely (<see cref="PdfFont.Create"/>,
    /// <c>PdfSimpleFont.GetWidth</c>/<c>HasExplicitWidth</c>, <c>PdfTrueTypeFont.TryGetGlyphId</c>,
    /// <see cref="TrueTypeFontProgram.GetAdvance"/>, <c>CffFont.TryGetAdvance</c>,
    /// <c>Type1Font.TryGetAdvance</c>) rather than re-deriving any of it — the same
    /// "narrow public leaf over internal machinery" shape <see cref="TrueTypeFontProgram"/> itself
    /// already has.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately narrower than "every embedded font."</b> For TrueType,
    /// <see cref="TryGetEmbeddedAdvance"/> can only answer for a character code
    /// <c>PdfTrueTypeFont.TryGetGlyphId</c>'s §9.6.6.4 ladder resolves without ambiguity: a symbolic
    /// font with no <c>/Encoding</c> (its codes address the embedded program's own built-in cmap
    /// directly), or a <c>/Differences</c> name resolved through the font's <c>post</c> table or a
    /// <c>gNN</c>/<c>glyphNN</c>/<c>cidNN</c> subset-name convention. The common case — a plain
    /// nonsymbolic Latin font with a standard base encoding and no <c>/Differences</c> — is
    /// deliberately <b>not</b> covered: the only way to a Unicode code point for that case is
    /// <c>PdfFont.Decode</c>, which prioritises a producer-supplied <c>/ToUnicode</c> mapping ahead
    /// of the encoding table, and <c>/ToUnicode</c> is a semantic assertion of what the text
    /// <i>means</i>, not necessarily which glyph is drawn (a ligature is the standard example).
    /// Composing that path with a Unicode cmap lookup risks silently comparing the wrong glyph's
    /// width — the "confidently wrong" failure mode CLAUDE.md's rendering section warns is worse
    /// than an obviously missing one — so this probe declines rather than guesses.</para>
    /// <para><b>Type 1 and MMType1 fonts</b> (<see cref="IsType1"/>) answer through
    /// <see cref="TryGetEmbeddedWidth"/> for a code whose glyph is named by <c>/Differences</c> — a
    /// name selects the glyph unambiguously — in a <c>/FontFile3</c> CFF program whose charset is read
    /// exactly, or a <c>/FontFile</c> Type 1 program. A code left to the base or built-in encoding is
    /// declined for the same reason as the TrueType case.</para>
    /// <para><b>Type 3 fonts</b> (<see cref="IsType3"/>) answer for a code <c>/Differences</c> names:
    /// the glyph procedure <c>/CharProcs</c> holds under that name, whose first operator — <c>d0</c>
    /// or <c>d1</c>, §9.6.5 — states its width in glyph space, the space a Type 3 font's
    /// <c>/Widths</c> is in. Only the procedure's first <see cref="Type3WidthBytesRead"/> bytes are
    /// read: the operator must come first.</para>
    /// </remarks>
    public sealed class SimpleFontWidthProbe
    {
        private readonly PdfSimpleFont _simple;
        private readonly PdfTrueTypeFont _trueType;
        private readonly bool _isType1;
        private readonly bool _isType3;
        private readonly PdfDictionary _fontDict;
        private readonly PdfDocument _document;

        private bool _programProbed;
        private TrueTypeFontProgram _program;

        private bool _type1ProgramProbed;
        private CffFont _cff;
        private Type1Font _type1Program;

        public SimpleFontWidthProbe(PdfDictionary fontDict, PdfDocument document)
        {
            _fontDict = fontDict;
            _document = document;
            var font = PdfFont.Create(fontDict, document);
            _simple = font as PdfSimpleFont;
            _trueType = font as PdfTrueTypeFont;
            string subtype = (Resolve(document, fontDict?[PdfName.Subtype]) as PdfName)?.Value;
            _isType1 = _simple != null && _trueType == null && (subtype == "Type1" || subtype == "MMType1");
            _isType3 = _simple != null && subtype == "Type3";
        }

        /// <summary>How much of a Type 3 glyph procedure is read for its leading <c>d0</c>/<c>d1</c>.</summary>
        public const int Type3WidthBytesRead = 512;

        /// <summary>True when this font dictionary produced a simple (Type1 or TrueType) font —
        /// the only kind this probe can answer anything about.</summary>
        public bool IsSimpleFont => _simple != null;

        /// <summary>True when the font is a TrueType simple font specifically — required for
        /// <see cref="TryGetEmbeddedAdvance"/> to ever succeed.</summary>
        public bool IsTrueType => _trueType != null;

        /// <summary>True when the font is a <c>/Type1</c> or <c>/MMType1</c> simple font (not
        /// <c>/Type3</c>), whose embedded advances <see cref="TryGetEmbeddedWidth"/> can read.</summary>
        public bool IsType1 => _isType1;

        /// <summary>True when the font is a <c>/Type3</c> font, whose glyph procedures' widths
        /// <see cref="TryGetEmbeddedWidth"/> can read.</summary>
        public bool IsType3 => _isType3;

        /// <summary>
        /// The PDF's own declared advance for <paramref name="charCode"/> — its <c>/Widths</c>
        /// entry, a Standard-14 fallback, or <c>/MissingWidth</c>. See <see cref="HasExplicitWidth"/>
        /// to tell a real <c>/Widths</c> entry from a fallback.
        /// </summary>
        public double GetWidth(int charCode) => _simple?.GetWidth(charCode) ?? 0;

        /// <summary>True when <paramref name="charCode"/> falls within the font's <c>/Widths</c>
        /// array, as opposed to a Standard-14 or <c>/MissingWidth</c> fallback.</summary>
        public bool HasExplicitWidth(int charCode) => _simple != null && _simple.HasExplicitWidth(charCode);

        /// <summary>
        /// The embedded TrueType program's own advance for the glyph <paramref name="charCode"/>
        /// resolves to, in 1000-unit PDF glyph space — or <c>false</c> when this isn't a TrueType
        /// font, the code has no unambiguous glyph id (see the class remarks), or the embedded
        /// program could not be read.
        /// </summary>
        public bool TryGetEmbeddedAdvance(int charCode, out int advance)
        {
            advance = 0;
            if (_trueType == null) return false;
            if (!_trueType.TryGetGlyphId(charCode, out ushort glyphId)) return false;

            EnsureProgram();
            // GetAdvance falls back to glyph 0 (or a constant) for an out-of-range id rather than
            // signalling failure — fine for its own write-side purpose, but trusting that fallback
            // here would compare the PDF's declared width against a glyph that was never selected.
            if (_program == null || glyphId >= _program.GlyphCount) return false;

            advance = _program.GetAdvance(glyphId);
            return true;
        }

        /// <summary>
        /// The embedded program's own advance for the glyph <paramref name="charCode"/> selects, in
        /// 1000-unit PDF glyph space and unrounded — for a TrueType font exactly what
        /// <see cref="TryGetEmbeddedAdvance"/> answers; for a Type 1 font (<see cref="IsType1"/>) the
        /// CFF or Type 1 program's advance for the glyph its <c>/Differences</c> entry names. False
        /// wherever the glyph or its advance cannot be known (see the class remarks).
        /// </summary>
        public bool TryGetEmbeddedWidth(int charCode, out double width)
        {
            width = 0;
            if (_trueType != null)
            {
                if (!TryGetEmbeddedAdvance(charCode, out int advance)) return false;
                width = advance;
                return true;
            }

            string name = DifferenceName(charCode);
            if (name == null) return false;
            if (_isType3) return TryGetType3Width(name, out width);

            EnsureType1Program();
            if (_cff != null)
                return _cff.HasExactGlyphNames && _cff.GlyphNameToGid.TryGetValue(name, out int gid)
                       && _cff.TryGetAdvance(gid, out width);
            return _type1Program != null && _type1Program.TryGetAdvance(name, out width);
        }

        /// <summary>
        /// Whether the embedded Type 1 or CFF program defines a glyph named
        /// <paramref name="glyphName"/> — null when this is not a Type 1 font, it embeds neither
        /// program, or the program's glyph names were not read completely enough for an absence to
        /// mean anything.
        /// </summary>
        internal bool? EmbeddedProgramDefines(string glyphName)
        {
            if (!_isType1 || glyphName == null) return null;
            EnsureType1Program();
            if (_cff != null) return _cff.HasExactGlyphNames ? _cff.GlyphNameToGid.ContainsKey(glyphName) : (bool?)null;
            if (_type1Program != null && _type1Program.CharStringsComplete) return _type1Program.CharStrings.ContainsKey(glyphName);
            return null;
        }

        private string DifferenceName(int charCode)
            => (_isType1 || _isType3) && _simple.DifferenceGlyphNames.TryGetValue(charCode, out string name) ? name : null;

        /// <summary>The width a Type 3 glyph procedure's leading <c>d0</c> or <c>d1</c> states.</summary>
        private bool TryGetType3Width(string glyphName, out double width)
        {
            width = 0;
            var procs = Resolve(_document, _fontDict?[PdfName.CharProcs]) as PdfDictionary;
            if (!(Resolve(_document, procs?[new PdfName(glyphName)]) is PdfStream proc)) return false;
            byte[] data;
            try
            {
                data = proc.GetDecodedData();
            }
            catch (System.Exception)
            {
                return false;   // a procedure that will not decode states no width
            }
            if (data == null || data.Length == 0) return false;
            if (data.Length > Type3WidthBytesRead) System.Array.Resize(ref data, Type3WidthBytesRead);

            var ops = Text.ContentStreamParser.Parse(data);
            if (ops.Count == 0 || (ops[0].Operator != Constants.PdfOperators.SetCharWidth && ops[0].Operator != Constants.PdfOperators.SetCacheDevice)
                || ops[0].Operands.Length == 0 || !(ops[0].Operands[0] is PdfNumber wx))
                return false;
            width = wx.DoubleValue;
            return true;
        }

        private void EnsureProgram()
        {
            if (_programProbed) return;
            _programProbed = true;

            var fd = Resolve(_document, _fontDict?[PdfName.FontDescriptor]) as PdfDictionary;
            var ffObj = fd?[PdfName.FontFile2];
            if (Resolve(_document, ffObj) is not PdfStream ffStream) return;

            byte[] data = FontStreamBytes.Program(ffStream);
            if (data == null || data.Length == 0) return;

            _program = TrueTypeFontProgram.Parse(data);
        }

        private void EnsureType1Program()
        {
            if (_type1ProgramProbed) return;
            _type1ProgramProbed = true;

            _cff = EmbeddedFontProgram.ReadCff(_fontDict, _document);
            if (_cff == null) _type1Program = EmbeddedFontProgram.ReadType1(_fontDict, _document);
        }

        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            try { return doc?.ResolveObject(obj) ?? obj; }
            catch { return obj; }
        }
    }
}
