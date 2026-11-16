// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The glyph names a simple font's character codes select through its <c>/Encoding</c> (ISO 32000-1
    /// §9.6.6): the four named encodings' code → glyph-name tables (Annex D), the one switch from a name
    /// to its table, and a font's own code → name map with <c>/Differences</c> applied. The renderer
    /// (<c>FontCache</c>, <c>Type3Encoding</c>), the font remediator's <c>/CharSet</c> and the PDF/A
    /// subset check read the same tables.
    /// </summary>
    public static class SimpleFontEncodingNames
    {
        /// <summary>
        /// The §9.6.6 named simple-font encoding as a char code → glyph name table, or <c>null</c> for a
        /// name that is not one of the four (§7.3.5: names are case-sensitive).
        /// </summary>
        internal static IReadOnlyList<string> Named(string name)
        {
            if (string.Equals(name, "WinAnsiEncoding", StringComparison.Ordinal)) return WinAnsi;
            if (string.Equals(name, "MacRomanEncoding", StringComparison.Ordinal)) return MacRoman;
            if (string.Equals(name, "MacExpertEncoding", StringComparison.Ordinal)) return MacExpert;
            if (string.Equals(name, "StandardEncoding", StringComparison.Ordinal)) return AdobeStandardEncodingNames.Table;
            return null;
        }

        /// <summary>
        /// The glyph name each code of a simple font selects, as far as the font dictionary says: a named
        /// <c>/Encoding</c>'s table, or an encoding dictionary's <c>/BaseEncoding</c> table with its
        /// <c>/Differences</c> applied. A code the dictionary leaves to the font program's built-in
        /// encoding (no <c>/Encoding</c>, or no <c>/BaseEncoding</c> and no <c>/Differences</c> entry) is
        /// absent: the dictionary does not say which glyph it is.
        /// </summary>
        public static IReadOnlyDictionary<int, string> ByCode(PdfDictionary font, Func<PdfObject, PdfObject> resolve)
        {
            var names = new Dictionary<int, string>();
            if (font == null || resolve == null) return names;
            PdfObject encoding = resolve(font[PdfName.Encoding]);
            IReadOnlyList<string> table = null;
            PdfArray differences = null;
            if (encoding is PdfName named) table = Named(named.Value);
            else if (encoding is PdfDictionary dictionary)
            {
                if (resolve(dictionary[PdfName.BaseEncoding]) is PdfName baseName) table = Named(baseName.Value);
                differences = resolve(dictionary[PdfName.Differences]) as PdfArray;
            }
            if (table != null)
                for (int code = 0; code < table.Count; code++)
                    if (table[code] != null) names[code] = table[code];
            if (differences != null)
            {
                int code = -1;
                for (int i = 0; i < differences.Count; i++)
                {
                    PdfObject item = resolve(differences[i]);
                    if (item is PdfNumber number)
                        code = number.IsInteger && number.LongValue >= 0 && number.LongValue <= 255 ? (int)number.LongValue : -1;
                    else if (item is PdfName name && code >= 0)
                    {
                        if (code <= 255) names[code] = name.Value;
                        code++;
                    }
                }
            }
            return names;
        }

        // WinAnsi (Windows-1252) char code → Adobe glyph name
        internal static readonly string[] WinAnsi = BuildWinAnsi();
        private static string[] BuildWinAnsi()
        {
            var t = new string[256];
            // 32-126: the block WinAnsi, MacRoman and StandardEncoding share.
            AsciiGlyphNames.Fill(t);
            // 128–159: Windows-1252 extended (not in ISO 8859-1)
            t[128]="Euro";t[130]="quotesinglbase";t[131]="florin";
            t[132]="quotedblbase";t[133]="ellipsis";t[134]="dagger";
            t[135]="daggerdbl";t[136]="circumflex";t[137]="perthousand";
            t[138]="Scaron";t[139]="guilsinglleft";t[140]="OE";
            t[142]="Zcaron";t[145]="quoteleft";t[146]="quoteright";
            t[147]="quotedblleft";t[148]="quotedblright";t[149]="bullet";
            t[150]="endash";t[151]="emdash";t[152]="tilde";t[153]="trademark";
            t[154]="scaron";t[155]="guilsinglright";t[156]="oe";
            t[158]="zcaron";t[159]="Ydieresis";
            // 160–255: Latin-1 supplement
            t[160]="nbspace";t[161]="exclamdown";t[162]="cent";t[163]="sterling";
            t[164]="currency";t[165]="yen";t[166]="brokenbar";t[167]="section";
            t[168]="dieresis";t[169]="copyright";t[170]="ordfeminine";
            t[171]="guillemotleft";t[172]="logicalnot";t[173]="hyphen";
            t[174]="registered";t[175]="macron";t[176]="degree";t[177]="plusminus";
            t[178]="twosuperior";t[179]="threesuperior";t[180]="acute";t[181]="mu";
            t[182]="paragraph";t[183]="periodcentered";t[184]="cedilla";
            t[185]="onesuperior";t[186]="ordmasculine";t[187]="guillemotright";
            t[188]="onequarter";t[189]="onehalf";t[190]="threequarters";
            t[191]="questiondown";
            t[192]="Agrave";t[193]="Aacute";t[194]="Acircumflex";t[195]="Atilde";
            t[196]="Adieresis";t[197]="Aring";t[198]="AE";t[199]="Ccedilla";
            t[200]="Egrave";t[201]="Eacute";t[202]="Ecircumflex";t[203]="Edieresis";
            t[204]="Igrave";t[205]="Iacute";t[206]="Icircumflex";t[207]="Idieresis";
            t[208]="Eth";t[209]="Ntilde";t[210]="Ograve";t[211]="Oacute";
            t[212]="Ocircumflex";t[213]="Otilde";t[214]="Odieresis";t[215]="multiply";
            t[216]="Oslash";t[217]="Ugrave";t[218]="Uacute";t[219]="Ucircumflex";
            t[220]="Udieresis";t[221]="Yacute";t[222]="Thorn";t[223]="germandbls";
            t[224]="agrave";t[225]="aacute";t[226]="acircumflex";t[227]="atilde";
            t[228]="adieresis";t[229]="aring";t[230]="ae";t[231]="ccedilla";
            t[232]="egrave";t[233]="eacute";t[234]="ecircumflex";t[235]="edieresis";
            t[236]="igrave";t[237]="iacute";t[238]="icircumflex";t[239]="idieresis";
            t[240]="eth";t[241]="ntilde";t[242]="ograve";t[243]="oacute";
            t[244]="ocircumflex";t[245]="otilde";t[246]="odieresis";t[247]="divide";
            t[248]="oslash";t[249]="ugrave";t[250]="uacute";t[251]="ucircumflex";
            t[252]="udieresis";t[253]="yacute";t[254]="thorn";t[255]="ydieresis";
            // Annex D, Table D.2's note: "In WinAnsiEncoding, all unused codes greater than 40 [octal] map
            // to the bullet character" — 177, 201, 215, 217, 220 and 235 octal. Independent readers'
            // tables hold bullet there; without it such a code named nothing, drew nothing, and
            // read as a reference to .notdef.
            foreach (int unused in new[] { 127, 129, 141, 143, 144, 157 }) t[unused] = "bullet";
            return t;
        }

        // MacRoman char code → Adobe glyph name (Mac OS Roman encoding)
        internal static readonly string[] MacRoman = BuildMacRoman();
        private static string[] BuildMacRoman()
        {
            var t = new string[256];
            // 32-126: same as ASCII/WinAnsi - the shared block.
            AsciiGlyphNames.Fill(t);
            // 128–255: Mac OS Roman specifics
            t[128]="Adieresis";t[129]="Aring";t[130]="Ccedilla";t[131]="Eacute";
            t[132]="Ntilde";t[133]="Odieresis";t[134]="Udieresis";t[135]="aacute";
            t[136]="agrave";t[137]="acircumflex";t[138]="adieresis";t[139]="atilde";
            t[140]="aring";t[141]="ccedilla";t[142]="eacute";t[143]="egrave";
            t[144]="ecircumflex";t[145]="edieresis";t[146]="iacute";t[147]="igrave";
            t[148]="icircumflex";t[149]="idieresis";t[150]="ntilde";t[151]="oacute";
            t[152]="ograve";t[153]="ocircumflex";t[154]="odieresis";t[155]="otilde";
            t[156]="uacute";t[157]="ugrave";t[158]="ucircumflex";t[159]="udieresis";
            t[160]="dagger";t[161]="degree";t[162]="cent";t[163]="sterling";
            t[164]="section";t[165]="bullet";t[166]="paragraph";t[167]="germandbls";
            t[168]="registered";t[169]="copyright";t[170]="trademark";t[171]="acute";
            t[172]="dieresis";t[174]="AE";t[175]="Oslash";
            t[177]="plusminus";t[180]="yen";t[181]="mu";
            t[182]="partialdiff";t[183]="summation";t[184]="product";t[185]="pi";
            t[186]="integral";t[187]="ordfeminine";t[188]="ordmasculine";
            t[189]="Omega";t[190]="ae";t[191]="oslash";t[192]="questiondown";
            t[193]="exclamdown";t[194]="logicalnot";t[196]="florin";
            t[197]="approxequal";t[198]="Delta";t[199]="guillemotleft";
            t[200]="guillemotright";t[201]="ellipsis";t[203]="Agrave";
            t[204]="Atilde";t[205]="Otilde";t[206]="OE";t[207]="oe";
            t[208]="endash";t[209]="emdash";t[210]="quotedblleft";
            t[211]="quotedblright";t[212]="quoteleft";t[213]="quoteright";
            t[214]="divide";t[216]="ydieresis";t[217]="Ydieresis";t[218]="fraction";
            t[219]="Euro";t[220]="guilsinglleft";t[221]="guilsinglright";
            t[222]="fi";t[223]="fl";t[224]="daggerdbl";t[225]="periodcentered";
            t[226]="quotesinglbase";t[227]="quotedblbase";t[228]="perthousand";
            t[229]="Acircumflex";t[230]="Ecircumflex";t[231]="Aacute";
            t[232]="Edieresis";t[233]="Egrave";t[234]="Iacute";t[235]="Icircumflex";
            t[236]="Idieresis";t[237]="Igrave";t[238]="Oacute";t[239]="Ocircumflex";
            t[241]="Ograve";t[242]="Uacute";t[243]="Ucircumflex";t[244]="Ugrave";
            t[245]="dotlessi";t[246]="circumflex";t[247]="tilde";t[248]="macron";
            t[249]="breve";t[250]="dotaccent";t[251]="ring";t[252]="cedilla";
            t[253]="hungarumlaut";t[254]="ogonek";t[255]="caron";
            return t;
        }

        // MacExpertEncoding (PDF Annex D.4) — small caps, old-style figures,
        // fractions, superior/inferior digits, and ligatures.  Entries not listed
        // are left null (= .notdef).
        internal static readonly string[] MacExpert = BuildMacExpertEncoding();
        private static string[] BuildMacExpertEncoding()
        {
            var t = new string[256];
            t[32]="space";t[33]="exclamsmall";t[34]="Hungarumlautsmall";
            t[35]="centoldstyle";t[36]="dollaroldstyle";t[37]="dollarsuperior";
            t[38]="ampersandsmall";t[39]="Acutesmall";
            t[40]="parenleftsuperior";t[41]="parenrightsuperior";
            t[42]="twodotenleader";t[43]="onedotenleader";
            t[44]="comma";t[45]="hyphen";t[46]="period";t[47]="fraction";
            t[48]="zerooldstyle";t[49]="oneoldstyle";t[50]="twooldstyle";
            t[51]="threeoldstyle";t[52]="fouroldstyle";t[53]="fiveoldstyle";
            t[54]="sixoldstyle";t[55]="sevenoldstyle";t[56]="eightoldstyle";
            t[57]="nineoldstyle";t[58]="colon";t[59]="semicolon";
            t[61]="threequartersemdash";t[63]="questionsmall";
            t[68]="Ethsmall";
            t[71]="onequarter";t[72]="onehalf";t[73]="threequarters";
            t[74]="oneeighth";t[75]="threeeighths";t[76]="fiveeighths";
            t[77]="seveneighths";t[78]="onethird";t[79]="twothirds";
            t[86]="ff";t[87]="fi";t[88]="fl";t[89]="ffi";t[90]="ffl";
            t[91]="parenleftinferior";t[93]="parenrightinferior";
            t[94]="Circumflexsmall";t[95]="hypheninferior";t[96]="Gravesmall";
            t[97]="Asmall";t[98]="Bsmall";t[99]="Csmall";t[100]="Dsmall";
            t[101]="Esmall";t[102]="Fsmall";t[103]="Gsmall";t[104]="Hsmall";
            t[105]="Ismall";t[106]="Jsmall";t[107]="Ksmall";t[108]="Lsmall";
            t[109]="Msmall";t[110]="Nsmall";t[111]="Osmall";t[112]="Psmall";
            t[113]="Qsmall";t[114]="Rsmall";t[115]="Ssmall";t[116]="Tsmall";
            t[117]="Usmall";t[118]="Vsmall";t[119]="Wsmall";t[120]="Xsmall";
            t[121]="Ysmall";t[122]="Zsmall";
            t[123]="colonmonetary";t[124]="onefitted";t[125]="rupiah";
            t[126]="Tildesmall";
            t[136]="asuperior";t[137]="centsuperior";
            t[161]="Aacutesmall";t[162]="Agravesmall";t[163]="Acircumflexsmall";
            t[164]="Adieresissmall";t[165]="Atildesmall";t[166]="Aringsmall";
            t[167]="Ccedillasmall";t[168]="Eacutesmall";t[169]="Egravesmall";
            t[170]="Ecircumflexsmall";t[171]="Edieresissmall";
            t[172]="Iacutesmall";t[173]="Igravesmall";t[174]="Icircumflexsmall";
            t[175]="Idieresissmall";t[176]="Ntildesmall";t[177]="Oacutesmall";
            t[178]="Ogravesmall";t[179]="Ocircumflexsmall";t[180]="Odieresissmall";
            t[181]="Otildesmall";t[182]="Uacutesmall";t[183]="Ugravesmall";
            t[184]="Ucircumflexsmall";t[185]="Udieresissmall";
            t[187]="eightsuperior";t[188]="fourinferior";t[189]="threeinferior";
            t[190]="sixinferior";t[191]="eightinferior";t[192]="seveninferior";
            t[193]="Scaronsmall";t[195]="centinferior";t[196]="twoinferior";
            t[198]="Dieresissmall";t[200]="Caronsmall";t[201]="osuperior";
            t[202]="fiveinferior";t[204]="commainferior";t[205]="periodinferior";
            t[206]="Yacutesmall";t[208]="dollarinferior";
            t[211]="Thornsmall";t[213]="nineinferior";t[214]="zeroinferior";
            t[215]="Zcaronsmall";t[216]="AEsmall";t[217]="Oslashsmall";
            t[218]="questiondownsmall";t[219]="oneinferior";t[220]="Lslashsmall";
            t[228]="Cedillasmall";t[236]="OEsmall";t[237]="figuredash";
            t[238]="hyphensuperior";t[242]="exclamdownsmall";
            t[244]="Ydieresissmall";t[246]="onesuperior";t[247]="twosuperior";
            t[248]="threesuperior";t[249]="foursuperior";t[250]="fivesuperior";
            t[251]="sixsuperior";t[252]="sevensuperior";t[253]="ninesuperior";
            t[254]="zerosuperior";
            return t;
        }
    }
}
