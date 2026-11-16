// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Adobe StandardEncoding as a character code → <b>glyph name</b> table (ISO 32000-1 Annex D.2,
    /// equivalently ATN #5002 Appendix B). 149 of the 256 codes are defined; the rest are
    /// <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The other direction.</b> <see cref="StandardEncodings"/> already owns
    /// StandardEncoding in the code → <i>character</i> direction
    /// (<c>StandardEncodings.AdobeStandardEncoding</c>), and that is the only direction it has. The
    /// glyph-name direction is what a font needs — a Type 1 <c>seac</c> names its base and accent
    /// components by StandardEncoding code, and a simple font's <c>/Encoding</c> resolves a code to
    /// a name before any glyph exists — so <c>FontCache.BuildStandardEncoding</c> (feeding
    /// <c>/Encoding</c> and <c>/BaseEncoding</c> resolution) and
    /// <c>Type1CharstringEvaluator.BuildStandardEncoding</c> (feeding <c>seac</c>) both read this
    /// table rather than a hand-written copy.</para>
    ///
    /// <para><b>This is drift-risk hygiene, not a bug fix.</b> The two copies were compared code by
    /// code before they were deleted and were identical at all 149 — no input separated them, and
    /// nothing was rendering wrong. What was missing was any reason for that to stay true: a table
    /// this long is corrected one entry at a time, and the two copies sat in different files with
    /// different comments and different consumers.</para>
    ///
    /// <para><b>A separate type rather than another <c>StandardEncodings</c> partial.</b>
    /// <see cref="StandardEncodings"/>' type initializer builds eight code → character dictionaries
    /// <i>and</i> the ~4 340-entry Adobe Glyph List in <c>StandardEncodings.GlyphData.cs</c>. A
    /// <c>seac</c> lookup during charstring evaluation wants 149 strings and should not pay for all
    /// of that. Keeping it out also keeps this a leaf — one static field, holding the one table it
    /// owns — which is what makes it safe to reach from anywhere without adding a
    /// type-initialization edge.</para>
    ///
    /// <para><b>Two same-looking tables are deliberately left alone.</b>
    /// <c>Tetitulz.Pdf.Plugins.PostScript</c>'s <c>PsEncodings.StandardEncoding</c> stays where it
    /// is: it is a plugin, so it cannot see Core's internals, and its provenance is PLRM Appendix
    /// E.1 (PostScript's own <c>StandardEncoding</c> array) rather than PDF Annex D.2. And
    /// <c>CffParser</c> answers the same question in Core already —
    /// <c>TryGetStandardEncodingGlyphName</c> — but it does so by composing <i>code → SID</i> with
    /// the CFF standard-string table (TN #5176 Appendix A), because the code → SID half is what its
    /// <c>encodingVal == 0</c> charset walk actually needs. Rewiring that to read this table would
    /// break the very consistency that makes it correct. They are pinned against each other by test
    /// instead: <c>AdobeStandardEncodingNamesTests</c> asserts the two agree at every code.</para>
    /// </remarks>
    internal static class AdobeStandardEncodingNames
    {
        private static readonly string[] _names = Build();

        /// <summary>
        /// The 256-entry table, indexed by character code. Undefined codes are <c>null</c>.
        /// Read-only so the single shared instance cannot be written through.
        /// </summary>
        internal static IReadOnlyList<string> Table => _names;

        /// <summary>
        /// The glyph name StandardEncoding gives <paramref name="code"/>, or <c>null</c> when the
        /// code is undefined or out of range.
        /// </summary>
        internal static string Get(int code)
        {
            return (uint)code < (uint)_names.Length ? _names[code] : null;
        }

        private static string[] Build()
        {
            var t = new string[256];
            AsciiGlyphNames.Fill(t);

            // StandardEncoding's only two disagreements with WinAnsi/MacRoman below 127. It
            // predates the typewriter apostrophe: 0x27 is a right single quote here and 0x60
            // a left one. Overriding after the shared fill is what keeps that visible.
            t[39]="quoteright";
            t[96]="quoteleft";
            t[161]="exclamdown";t[162]="cent";t[163]="sterling";t[164]="fraction";
            t[165]="yen";t[166]="florin";t[167]="section";t[168]="currency";
            t[169]="quotesingle";t[170]="quotedblleft";t[171]="guillemotleft";
            t[172]="guilsinglleft";t[173]="guilsinglright";t[174]="fi";t[175]="fl";
            t[177]="endash";t[178]="dagger";t[179]="daggerdbl";t[180]="periodcentered";
            t[182]="paragraph";t[183]="bullet";t[184]="quotesinglbase";
            t[185]="quotedblbase";t[186]="quotedblright";t[187]="guillemotright";
            t[188]="ellipsis";t[189]="perthousand";t[191]="questiondown";
            t[193]="grave";t[194]="acute";t[195]="circumflex";t[196]="tilde";
            t[197]="macron";t[198]="breve";t[199]="dotaccent";t[200]="dieresis";
            t[202]="ring";t[203]="cedilla";t[205]="hungarumlaut";t[206]="ogonek";
            t[207]="caron";t[208]="emdash";
            t[225]="AE";t[227]="ordfeminine";t[232]="Lslash";t[233]="Oslash";
            t[234]="OE";t[235]="ordmasculine";t[241]="ae";t[245]="dotlessi";
            t[248]="lslash";t[249]="oslash";t[250]="oe";t[251]="germandbls";
            return t;
        }
    }
}
