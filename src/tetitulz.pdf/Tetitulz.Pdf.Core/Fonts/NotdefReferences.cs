// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>Why a shown code references the <c>.notdef</c> glyph.</summary>
    public enum NotdefCause
    {
        /// <summary>A composite font's code that its CMap maps to CID 0, the CIDFont's <c>.notdef</c> (ISO 32000-2 §9.7.4.2).</summary>
        Cid0,
        /// <summary>A composite font's bytes that match no codespace range of its CMap: an invalid code, given a substitute glyph (§9.7.6.3).</summary>
        NoCodespace,
        /// <summary>A composite font's valid code that no <c>cidchar</c> or <c>cidrange</c> maps: a notdef mapping or CID 0 is selected (§9.7.6.3).</summary>
        NoCidMapping,
        /// <summary>A simple font's code whose <c>/Differences</c> entry names <c>.notdef</c> itself.</summary>
        NamedNotdef,
        /// <summary>A simple TrueType font's code whose <c>/Differences</c> name resolves to glyph 0 (§9.6.6.4).</summary>
        GlyphZero,
        /// <summary>A Type 1 font's code whose <c>/Differences</c> name the embedded program does not define, so <c>.notdef</c> is substituted (§9.6.5.2).</summary>
        NameNotInProgram,
        /// <summary>A simple font's code that its encoding, known in full, names no glyph for — WinAnsi's code 0, a Type 3 code outside its <c>/Differences</c>.</summary>
        UnnamedCode,
    }

    /// <summary>One code of a shown string that references the <c>.notdef</c> glyph, and why.</summary>
    public readonly struct NotdefReference
    {
        internal NotdefReference(int code, NotdefCause cause, string glyphName)
        {
            Code = code; Cause = cause; GlyphName = glyphName;
        }

        /// <summary>The character code as shown: a simple font's byte, a composite font's code value.</summary>
        public int Code { get; }

        /// <summary>Why the code references <c>.notdef</c>.</summary>
        public NotdefCause Cause { get; }

        /// <summary>The <c>/Differences</c> name for <see cref="NotdefCause.NameNotInProgram"/> and <see cref="NotdefCause.GlyphZero"/>; otherwise <c>null</c>.</summary>
        public string GlyphName { get; }
    }

    /// <summary>
    /// Which codes of a font's shown strings reference the <c>.notdef</c> glyph — ISO 32000-2 §9.7.6.3
    /// for a composite font, §9.6.5.2 and §9.6.6.4 for a simple one — the question ISO 14289-2
    /// §8.4.5.9, ISO 19005-2/3 6.2.11.8 and ISO 19005-4 6.2.10.9 ask ("shall not contain a reference to
    /// the .notdef glyph from any of the text showing operators, regardless of text rendering mode").
    /// One reader per font dictionary; the PDF/UA-2 checker and the PDF/A validator both ask it.
    /// </summary>
    /// <remarks>
    /// <para><b>A composite font</b>: a code no codespace range admits, a valid code no mapping covers,
    /// or one mapped to CID 0 — each of §9.7.6.3's routes to the notdef machinery. The codec keeps an
    /// unmapped code's own value as its CID for rendering (<see cref="CMapCodec.Code.CidMapped"/>); the
    /// reference is judged by the specification, not by what the renderer falls back to.</para>
    /// <para><b>A simple font</b>: a <c>/Differences</c> name that is <c>.notdef</c>; for TrueType a
    /// <c>/Differences</c> name the embedded <c>post</c> table or a glyph-index name resolves to glyph 0
    /// (<see cref="PdfTrueTypeFont.DifferenceResolvesToNotdef"/> — never the symbol/Mac <c>cmap</c> half
    /// of the ladder, whose "no entry" is bitwise an explicit glyph 0); for Type 1 a <c>/Differences</c>
    /// name the program does not define, where its names were read completely. And a code the encoding
    /// names no glyph for, where the encoding is known in full — a named base (directly or as
    /// <c>/BaseEncoding</c>), a Type 3 font's encoding dictionary, a Type 1 program's built-in encoding —
    /// as the PDF/A profiles read a glyph's name through the encoding, <c>.notdef</c> for none. Not for a symbolic
    /// TrueType font, whose codes reach its <c>cmap</c> directly (§9.6.6.4), nor where the base is left
    /// to a program this reader does not have.</para>
    /// <para>What could not be read is said in <see cref="Unreadable"/> and not judged: the answer can
    /// miss a reference, never invent one.</para>
    /// </remarks>
    public sealed class NotdefReferences
    {
        private readonly PdfFont _font;
        private readonly string _subtype;
        private readonly SimpleFontWidthProbe _probe;
        private readonly IReadOnlyDictionary<int, string> _names;

        private NotdefReferences(PdfFont font, string subtype, SimpleFontWidthProbe probe,
            IReadOnlyDictionary<int, string> names, string unreadable)
        {
            _font = font; _subtype = subtype; _probe = probe; _names = names; Unreadable = unreadable;
        }

        /// <summary>
        /// Why part of this font's answer was not judged — the font dictionary did not read, its program's
        /// glyph names did not — or <c>null</c>. The first reason only.
        /// </summary>
        public string Unreadable { get; private set; }

        /// <summary>The reader for <paramref name="fontDict"/>.</summary>
        public static NotdefReferences For(PdfDictionary fontDict, PdfDocument document)
        {
            if (fontDict == null) throw new ArgumentNullException(nameof(fontDict));
            string subtype = (document?.ResolveObject(fontDict[PdfName.Subtype]) ?? fontDict[PdfName.Subtype]) is PdfName name ? name.Value : null;
            PdfFont font;
            try { font = PdfFont.Create(fontDict, document); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return new NotdefReferences(null, subtype, null, null, "the font dictionary could not be read as a font (" + ex.GetType().Name + ")");
            }

            SimpleFontWidthProbe probe = null;
            string unreadable = null;
            if (subtype == "Type1" || subtype == "MMType1")
            {
                try { probe = new SimpleFontWidthProbe(fontDict, document); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    unreadable = "the font's glyph names could not be read (" + ex.GetType().Name + ")";
                }
            }
            IReadOnlyDictionary<int, string> names = null;
            if (font is PdfSimpleFont)
            {
                try { names = CompleteEncodingNames(fontDict, subtype, document, out string namesUnread); unreadable = unreadable ?? namesUnread; }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    unreadable = unreadable ?? "the font's encoding could not be read (" + ex.GetType().Name + ")";
                }
            }
            return new NotdefReferences(font, subtype, probe, names, unreadable);
        }

        /// <summary>
        /// The glyph name every code selects, where the encoding is known in full — a named base, a Type 3
        /// encoding dictionary, an embedded Type 1 program's built-in encoding under an implicit base — or
        /// <c>null</c>: a symbolic TrueType font, or a base left to a program this reader does not have.
        /// </summary>
        private static IReadOnlyDictionary<int, string> CompleteEncodingNames(PdfDictionary fontDict, string subtype,
            PdfDocument document, out string unreadable)
        {
            unreadable = null;
            Func<PdfObject, PdfObject> resolve = o => document != null ? document.ResolveObject(o) : o;
            PdfObject encoding = resolve(fontDict[PdfName.Encoding]);
            var descriptor = resolve(fontDict[PdfName.FontDescriptor]) as PdfDictionary;
            if (subtype == "TrueType"
                && resolve(descriptor?[PdfName.Flags]) is PdfNumber flags && flags.IsInteger && (flags.LongValue & 4) != 0)
                return null;
            bool namedBase = (encoding is PdfName named && SimpleFontEncodingNames.Named(named.Value) != null)
                || (encoding is PdfDictionary dictionary && resolve(dictionary[PdfName.BaseEncoding]) is PdfName baseName
                    && SimpleFontEncodingNames.Named(baseName.Value) != null);
            if (namedBase) return SimpleFontEncodingNames.ByCode(fontDict, resolve);
            if (subtype == "Type3") return encoding is PdfDictionary ? SimpleFontEncodingNames.ByCode(fontDict, resolve) : null;
            if ((subtype == "Type1" || subtype == "MMType1")
                && (descriptor?[PdfName.FontFile] != null || descriptor?[PdfName.FontFile3] != null))
                return EmbeddedGlyphSets.GlyphNamesByCode(fontDict, resolve, out unreadable);
            return null;
        }

        /// <summary>The codes of one shown string that reference <c>.notdef</c>, in the order shown.</summary>
        public IReadOnlyList<NotdefReference> In(byte[] shown)
        {
            var found = new List<NotdefReference>();
            if (_font == null || shown == null || shown.Length == 0) return found;

            if (_subtype == "Type0")
            {
                IReadOnlyList<CMapCodec.Code> codes;
                try { codes = _font.SplitCodes(shown); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    Unreadable = Unreadable ?? "a shown string could not be split into codes (" + ex.GetType().Name + ")";
                    return found;
                }
                foreach (CMapCodec.Code code in codes)
                {
                    if (!code.CodespaceMatched) found.Add(new NotdefReference(code.Value, NotdefCause.NoCodespace, null));
                    else if (!code.CidMapped) found.Add(new NotdefReference(code.Value, NotdefCause.NoCidMapping, null));
                    else if (code.Cid == 0) found.Add(new NotdefReference(code.Value, NotdefCause.Cid0, null));
                }
                return found;
            }

            if (!(_font is PdfSimpleFont simple)) return found;
            foreach (byte code in shown)
            {
                if (!simple.DifferenceGlyphNames.TryGetValue(code, out string name))
                {
                    if (_names != null && !_names.ContainsKey(code)) found.Add(new NotdefReference(code, NotdefCause.UnnamedCode, null));
                    continue;
                }
                if (name == ".notdef") { found.Add(new NotdefReference(code, NotdefCause.NamedNotdef, name)); continue; }
                if (_font is PdfTrueTypeFont trueType)
                {
                    if (trueType.DifferenceResolvesToNotdef(code)) found.Add(new NotdefReference(code, NotdefCause.GlyphZero, name));
                }
                else if (_probe != null && ProgramDefines(name) == false)
                    found.Add(new NotdefReference(code, NotdefCause.NameNotInProgram, name));
            }
            return found;
        }

        private bool? ProgramDefines(string glyphName)
        {
            try { return _probe.EmbeddedProgramDefines(glyphName); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Unreadable = Unreadable ?? "the embedded font program could not be read (" + ex.GetType().Name + ")";
                return null;
            }
        }
    }
}
