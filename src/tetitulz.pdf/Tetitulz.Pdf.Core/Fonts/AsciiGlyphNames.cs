// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The Adobe glyph names for character codes 32–126, which WinAnsiEncoding, MacRomanEncoding
    /// and Adobe StandardEncoding all share — <b>apart from two code points, which each table sets
    /// for itself.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is one method and not three tables.</b> The three encodings spelled this
    /// block out independently, ~22 lines of <c>t[65]="A";t[66]="B";…</c> each, identical except at
    /// <c>0x27</c> and <c>0x60</c>. Two near-identical walls of literals differing in two places is
    /// where a typo lives undetected — and worse, it invites exactly the wrong repair: merging all
    /// three into one table, which would silently give StandardEncoding the WinAnsi apostrophe and
    /// change the glyph a Type 1 font draws.</para>
    ///
    /// <para><b>So the shared part is shared and the difference is stated, once, at each caller.</b>
    /// This method fills the <i>common</i> block using the WinAnsi/MacRoman spellings, and
    /// <see cref="AdobeStandardEncodingNames"/> overrides its two afterwards. A reader can now see
    /// the whole disagreement between the encodings in two lines instead of by diffing three
    /// paragraphs of literals.</para>
    ///
    /// <para>The two exceptions are real and predate the ASCII apostrophe: StandardEncoding was
    /// built for typesetting, where <c>0x27</c> is a right single quote (<c>quoteright</c>) and
    /// <c>0x60</c> a left one (<c>quoteleft</c>), not the typewriter marks WinAnsi inherited from
    /// Latin-1. <c>LatinEncodingTableAgreementTests</c> pins both the agreement and the exceptions,
    /// so a future consolidation that erases the difference fails rather than changes glyphs.</para>
    /// </remarks>
    internal static class AsciiGlyphNames
    {
        /// <summary>
        /// Writes codes 32–126 into <paramref name="t"/>, leaving every other entry untouched so a
        /// caller can fill its own high range before or after.
        /// </summary>
        /// <param name="t">A 256-entry table to populate.</param>
        internal static void Fill(string[] t)
        {
            t[32]="space";t[33]="exclam";t[34]="quotedbl";t[35]="numbersign";
            t[36]="dollar";t[37]="percent";t[38]="ampersand";t[39]="quotesingle";
            t[40]="parenleft";t[41]="parenright";t[42]="asterisk";t[43]="plus";
            t[44]="comma";t[45]="hyphen";t[46]="period";t[47]="slash";
            t[48]="zero";t[49]="one";t[50]="two";t[51]="three";t[52]="four";
            t[53]="five";t[54]="six";t[55]="seven";t[56]="eight";t[57]="nine";
            t[58]="colon";t[59]="semicolon";t[60]="less";t[61]="equal";
            t[62]="greater";t[63]="question";t[64]="at";
            t[65]="A";t[66]="B";t[67]="C";t[68]="D";t[69]="E";t[70]="F";
            t[71]="G";t[72]="H";t[73]="I";t[74]="J";t[75]="K";t[76]="L";
            t[77]="M";t[78]="N";t[79]="O";t[80]="P";t[81]="Q";t[82]="R";
            t[83]="S";t[84]="T";t[85]="U";t[86]="V";t[87]="W";t[88]="X";
            t[89]="Y";t[90]="Z";
            t[91]="bracketleft";t[92]="backslash";t[93]="bracketright";
            t[94]="asciicircum";t[95]="underscore";t[96]="grave";
            t[97]="a";t[98]="b";t[99]="c";t[100]="d";t[101]="e";t[102]="f";
            t[103]="g";t[104]="h";t[105]="i";t[106]="j";t[107]="k";t[108]="l";
            t[109]="m";t[110]="n";t[111]="o";t[112]="p";t[113]="q";t[114]="r";
            t[115]="s";t[116]="t";t[117]="u";t[118]="v";t[119]="w";t[120]="x";
            t[121]="y";t[122]="z";
            t[123]="braceleft";t[124]="bar";t[125]="braceright";t[126]="asciitilde";
        }
    }
}
