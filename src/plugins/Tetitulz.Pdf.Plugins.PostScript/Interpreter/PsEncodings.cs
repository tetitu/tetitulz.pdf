// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// The two encoding <b>arrays</b> PLRM Appendix E puts in <c>systemdict</c>:
    /// <c>StandardEncoding</c> and <c>ISOLatin1Encoding</c>.
    ///
    /// <para><b>These are names a program uses unconditionally, not features it probes for</b>, which
    /// is the distinction <see cref="Operators.PsSystemDict"/> draws between an entry that must exist
    /// and one that must stay undefined. The canonical re-encoding idiom is</para>
    /// <code>
    /// /Helvetica findfont dup length dict begin
    ///   {1 index /FID ne {def} {pop pop} ifelse} forall
    ///   /Encoding ISOLatin1Encoding def
    /// currentdict end /Helvetica-ISO exch definefont pop
    /// </code>
    /// <para>and it is how essentially every accented-Latin PostScript document selects its glyphs.
    /// With the name undefined that raises <c>undefined</c> <i>in the middle of the idiom</i>: the
    /// object is abandoned with <c>currentdict</c> still pushed and the copied dictionary still on
    /// the operand stack, so the <c>definefont</c> that follows runs against a desynchronised stack
    /// and the document draws with the wrong font rather than with none. A test supplying the
    /// array by hand (<c>/EncodingArray [ … ] def</c>) cannot see that — the one form real files do
    /// not use.</para>
    ///
    /// <para><b>A fresh array per interpreter.</b> A <c>PsArray</c> is mutable and a program may
    /// legally <c>put</c> into one of these, so a cached instance would carry one conversion's edit
    /// into the next — the same reason <see cref="Operators.PsSystemDict.Build"/> is not cached. The
    /// two 256-element arrays are built once at interpreter construction, before any program byte
    /// runs, so their cost is fixed rather than attacker-controlled and they are deliberately not
    /// charged to <see cref="PsAllocationBudget"/>.</para>
    /// </summary>
    internal static class PsEncodings
    {
        public const string StandardEncodingName = "StandardEncoding";
        public const string IsoLatin1EncodingName = "ISOLatin1Encoding";

        /// <summary>
        /// A fresh 256-element encoding array, or null when <paramref name="name"/> is not one of
        /// the two.
        /// </summary>
        public static PsArray Build(string name)
        {
            string[] names =
                name == StandardEncodingName ? StandardEncodingNames() :
                name == IsoLatin1EncodingName ? IsoLatin1EncodingNames() : null;

            if (names == null) return null;

            var items = new PsObject[256];
            for (int code = 0; code < 256; code++)
                items[code] = PsObject.LitName(names[code] ?? NotDef);

            return new PsArray(items);
        }

        private const string NotDef = ".notdef";

        // Built on first use: a static initialiser here runs before the Ascii table below it exists.
        private static string[] _standard;

        /// <summary>The glyph name StandardEncoding gives <paramref name="code"/>, or null for none.</summary>
        public static string StandardName(int code) =>
            (uint)code < 256u ? (_standard ??= StandardEncodingNames())[code] : null;

        // ------------------------------------------------------------------
        //  Appendix E data
        // ------------------------------------------------------------------
        //
        // Spelled as the shared ASCII run plus a per-encoding table of high codes, rather than as two
        // 256-entry literals. A 256-entry literal is exactly the shape an off-by-one hides in, and
        // these are spec tables where one wrong slot draws a wrong character with nothing downstream
        // able to notice.

        /// <summary>
        /// Codes 0x20-0x7E, which both encodings share except at two slots — see
        /// <see cref="IsoLatin1EncodingNames"/>.
        /// </summary>
        private static readonly string[] Ascii =
        {
            /* 20 */ "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand",
            /* 27 */ "quoteright",
            /* 28 */ "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
            /* 30 */ "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            /* 3a */ "colon", "semicolon", "less", "equal", "greater", "question", "at",
            /* 41 */ "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            /* 4e */ "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
            /* 5b */ "bracketleft", "backslash", "bracketright", "asciicircum", "underscore",
            /* 60 */ "quoteleft",
            /* 61 */ "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
            /* 6e */ "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
            /* 7b */ "braceleft", "bar", "braceright", "asciitilde"
        };

        private static string[] BaseWithAscii()
        {
            var names = new string[256];
            for (int k = 0; k < Ascii.Length; k++) names[0x20 + k] = Ascii[k];
            return names;
        }

        /// <summary>PLRM Appendix E.1 — <c>StandardEncoding</c>.</summary>
        private static string[] StandardEncodingNames()
        {
            var names = BaseWithAscii();

            Put(names,
                (0xA1, "exclamdown"), (0xA2, "cent"), (0xA3, "sterling"), (0xA4, "fraction"),
                (0xA5, "yen"), (0xA6, "florin"), (0xA7, "section"), (0xA8, "currency"),
                (0xA9, "quotesingle"), (0xAA, "quotedblleft"), (0xAB, "guillemotleft"),
                (0xAC, "guilsinglleft"), (0xAD, "guilsinglright"), (0xAE, "fi"), (0xAF, "fl"),

                (0xB1, "endash"), (0xB2, "dagger"), (0xB3, "daggerdbl"), (0xB4, "periodcentered"),
                (0xB6, "paragraph"), (0xB7, "bullet"), (0xB8, "quotesinglbase"),
                (0xB9, "quotedblbase"), (0xBA, "quotedblright"), (0xBB, "guillemotright"),
                (0xBC, "ellipsis"), (0xBD, "perthousand"), (0xBF, "questiondown"),

                (0xC1, "grave"), (0xC2, "acute"), (0xC3, "circumflex"), (0xC4, "tilde"),
                (0xC5, "macron"), (0xC6, "breve"), (0xC7, "dotaccent"), (0xC8, "dieresis"),
                (0xCA, "ring"), (0xCB, "cedilla"), (0xCD, "hungarumlaut"), (0xCE, "ogonek"),
                (0xCF, "caron"),

                (0xD0, "emdash"),

                (0xE1, "AE"), (0xE3, "ordfeminine"), (0xE8, "Lslash"), (0xE9, "Oslash"),
                (0xEA, "OE"), (0xEB, "ordmasculine"),

                (0xF1, "ae"), (0xF5, "dotlessi"), (0xF8, "lslash"), (0xF9, "oslash"),
                (0xFA, "oe"), (0xFB, "germandbls"));

            return names;
        }

        /// <summary>
        /// PLRM Appendix E.6 — <c>ISOLatin1Encoding</c>.
        ///
        /// <para>It is <b>not</b> ISO 8859-1 with AGL names, and the differences are the part worth
        /// stating. Codes 0x27 and 0x60 are <c>quotesingle</c> and <c>grave</c> where
        /// <c>StandardEncoding</c> has the typographic <c>quoteright</c> and <c>quoteleft</c>; and
        /// 0x90-0x9F, which ISO 8859-1 leaves as control codes, carry Adobe's floating accents.</para>
        /// </summary>
        private static string[] IsoLatin1EncodingNames()
        {
            var names = BaseWithAscii();

            names[0x27] = "quotesingle";
            names[0x60] = "grave";

            Put(names,
                (0x90, "dotlessi"), (0x91, "grave"), (0x92, "acute"), (0x93, "circumflex"),
                (0x94, "tilde"), (0x95, "macron"), (0x96, "breve"), (0x97, "dotaccent"),
                (0x98, "dieresis"), (0x9A, "ring"), (0x9B, "cedilla"), (0x9D, "hungarumlaut"),
                (0x9E, "ogonek"), (0x9F, "caron"),

                (0xA0, "space"), (0xA1, "exclamdown"), (0xA2, "cent"), (0xA3, "sterling"),
                (0xA4, "currency"), (0xA5, "yen"), (0xA6, "brokenbar"), (0xA7, "section"),
                (0xA8, "dieresis"), (0xA9, "copyright"), (0xAA, "ordfeminine"),
                (0xAB, "guillemotleft"), (0xAC, "logicalnot"), (0xAD, "hyphen"),
                (0xAE, "registered"), (0xAF, "macron"),

                (0xB0, "degree"), (0xB1, "plusminus"), (0xB2, "twosuperior"),
                (0xB3, "threesuperior"), (0xB4, "acute"), (0xB5, "mu"), (0xB6, "paragraph"),
                (0xB7, "periodcentered"), (0xB8, "cedilla"), (0xB9, "onesuperior"),
                (0xBA, "ordmasculine"), (0xBB, "guillemotright"), (0xBC, "onequarter"),
                (0xBD, "onehalf"), (0xBE, "threequarters"), (0xBF, "questiondown"),

                (0xC0, "Agrave"), (0xC1, "Aacute"), (0xC2, "Acircumflex"), (0xC3, "Atilde"),
                (0xC4, "Adieresis"), (0xC5, "Aring"), (0xC6, "AE"), (0xC7, "Ccedilla"),
                (0xC8, "Egrave"), (0xC9, "Eacute"), (0xCA, "Ecircumflex"), (0xCB, "Edieresis"),
                (0xCC, "Igrave"), (0xCD, "Iacute"), (0xCE, "Icircumflex"), (0xCF, "Idieresis"),

                (0xD0, "Eth"), (0xD1, "Ntilde"), (0xD2, "Ograve"), (0xD3, "Oacute"),
                (0xD4, "Ocircumflex"), (0xD5, "Otilde"), (0xD6, "Odieresis"), (0xD7, "multiply"),
                (0xD8, "Oslash"), (0xD9, "Ugrave"), (0xDA, "Uacute"), (0xDB, "Ucircumflex"),
                (0xDC, "Udieresis"), (0xDD, "Yacute"), (0xDE, "Thorn"), (0xDF, "germandbls"),

                (0xE0, "agrave"), (0xE1, "aacute"), (0xE2, "acircumflex"), (0xE3, "atilde"),
                (0xE4, "adieresis"), (0xE5, "aring"), (0xE6, "ae"), (0xE7, "ccedilla"),
                (0xE8, "egrave"), (0xE9, "eacute"), (0xEA, "ecircumflex"), (0xEB, "edieresis"),
                (0xEC, "igrave"), (0xED, "iacute"), (0xEE, "icircumflex"), (0xEF, "idieresis"),

                (0xF0, "eth"), (0xF1, "ntilde"), (0xF2, "ograve"), (0xF3, "oacute"),
                (0xF4, "ocircumflex"), (0xF5, "otilde"), (0xF6, "odieresis"), (0xF7, "divide"),
                (0xF8, "oslash"), (0xF9, "ugrave"), (0xFA, "uacute"), (0xFB, "ucircumflex"),
                (0xFC, "udieresis"), (0xFD, "yacute"), (0xFE, "thorn"), (0xFF, "ydieresis"));

            return names;
        }

        private static void Put(string[] names, params (int Code, string Name)[] entries)
        {
            foreach (var (code, name) in entries) names[code] = name;
        }
    }
}
