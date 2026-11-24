// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    // The AFM metrics of Symbol and ZapfDingbats — the two Standard-14 fonts whose
    // encoding is their own (ISO 32000-1 §9.6.6.4: "always use their built-in encoding"), so a
    // Unicode-keyed table of the kind the twelve Latin faces have cannot carry them.
    //
    // Stated as the AFM states them: code, glyph name, width. The built-in encoding IS
    // the AFM's C column, so keying by code is not the transcription hazard the class
    // header warns about for the Latin faces — there, the tables are keyed by Unicode and the
    // AFM by StandardEncoding code, and the two disagree. Here there is no second key to disagree
    // with. A code of −1 (an unencoded glyph) does not occur in either font.
    //
    // Three lookups, one table each. By code, for a byte shown through the built-in
    // encoding (a /Symbol font with no /Widths, PostScript's show); by glyph
    // name, for a /Differences entry; and by Unicode, through the Adobe Glyph List, for a
    // caller that has already resolved its code to a character — the reader's
    // PdfSimpleFont.GetWidth holds characters, not names. The Unicode map is derived from
    // the name map at first use, so the three can never disagree.
    //
    // Provenance. The widths were read through the URW clones
    // (StandardSymbolsPS, Dingbats), which are metric-compatible with Adobe's Symbol and
    // ZapfDingbats by design, one stringwidth per encoded code at size 1000 — and spot-checked
    // against the published AFMs (Symbol: alpha 631, Omega 768, universal 713; ZapfDingbats: a1
    // 974, a191 918). 190 and 202 glyphs, the AFMs' own counts.
    //
    // Without them GetWidthByUnicode would answer 0 for every character, so a
    // /Symbol font with no /Widths would render at /MissingWidth — every glyph
    // on one point — and the PostScript plugin's show advance its pen by nothing.
    public static partial class Standard14Metrics
    {
        /// <summary>
        /// Whether a Standard-14 name is one of the two whose built-in encoding a reader uses
        /// whatever <c>/Encoding</c> says (§9.6.6.4): <c>Symbol</c>, <c>ZapfDingbats</c> and its
        /// form-field alias <c>ZaDb</c>. The one answer to that question — the reader's
        /// <c>PdfSimpleFont</c> and the PostScript plugin's font mapper and resource collector ask it.
        /// </summary>
        public static bool HasBuiltInEncoding(string baseFontName) => SymbolicTable(baseFontName) != null;

        /// <summary>
        /// The glyph a <paramref name="code"/> selects in <c>Symbol</c> or <c>ZapfDingbats</c>'s
        /// built-in encoding, and its AFM advance width in 1/1000 em. <c>false</c> for any other
        /// font, and for a code the encoding leaves empty.
        /// </summary>
        public static bool TryGetBuiltInGlyph(string baseFontName, int code,
                                              out string glyphName, out double width)
        {
            glyphName = null;
            width = 0;
            var table = SymbolicTable(baseFontName);
            if (table == null || !table.ByCode.TryGetValue(code, out var glyph)) return false;
            glyphName = glyph.Name;
            width = glyph.Width;
            return true;
        }

        /// <summary>
        /// The AFM advance width of a glyph of <c>Symbol</c> or <c>ZapfDingbats</c> by its name —
        /// what a <c>/Differences</c> entry carries. <c>false</c> for any other font or name.
        /// </summary>
        public static bool TryGetWidthByGlyphName(string baseFontName, string glyphName, out double width)
        {
            width = 0;
            var table = SymbolicTable(baseFontName);
            if (table == null || glyphName == null || !table.ByName.TryGetValue(glyphName, out width))
                return false;
            return true;
        }

        /// <summary>
        /// The code <c>Symbol</c> or <c>ZapfDingbats</c>'s built-in encoding gives a glyph name, or
        /// −1: how a producer shows a glyph it holds by name through the font's own encoding
        /// instead of minting a <c>/Differences</c> for it.
        /// </summary>
        public static int BuiltInCodeOf(string baseFontName, string glyphName)
        {
            var table = SymbolicTable(baseFontName);
            return table != null && glyphName != null && table.CodeByName.TryGetValue(glyphName, out int code)
                ? code
                : -1;
        }

        /// <summary>
        /// The AFM width of the <c>Symbol</c> or <c>ZapfDingbats</c> glyph the Adobe Glyph List
        /// names for <paramref name="unicode"/>, or 0. Derived from the name table through
        /// <see cref="StandardEncodings.TryGetGlyphUnicode"/>, so a glyph whose name the list does
        /// not know (none of these 392 is such a glyph) would simply be absent here.
        /// </summary>
        private static double SymbolicWidthByUnicode(string baseFontName, char unicode)
        {
            var table = SymbolicTable(baseFontName);
            return table != null && table.ByUnicode.TryGetValue(unicode, out double w) ? w : 0;
        }

        private static SymbolicFontTable SymbolicTable(string baseFontName) => baseFontName switch
        {
            StandardFonts.Symbol => _symbolTable ??= new SymbolicFontTable(SymbolAfm),
            StandardFonts.ZapfDingbats or StandardFonts.ZapfDingbatsAbbrev
                => _zapfDingbatsTable ??= new SymbolicFontTable(ZapfDingbatsAfm),
            _ => null
        };

        private static SymbolicFontTable _symbolTable;
        private static SymbolicFontTable _zapfDingbatsTable;

        private sealed class SymbolicFontTable
        {
            public readonly Dictionary<int, (string Name, double Width)> ByCode = new();
            public readonly Dictionary<string, double> ByName = new(System.StringComparer.Ordinal);
            public readonly Dictionary<string, int> CodeByName = new(System.StringComparer.Ordinal);
            public readonly Dictionary<char, double> ByUnicode = new();

            public SymbolicFontTable((int Code, string Name, double Width)[] afm)
            {
                foreach (var (code, name, width) in afm)
                {
                    ByCode[code] = (name, width);
                    ByName[name] = width;
                    CodeByName[name] = code;
                    if (StandardEncodings.TryGetGlyphUnicode(name, out char unicode) && unicode != '\0')
                        ByUnicode[unicode] = width;
                }
            }
        }

        // ── Symbol (AFM C / N / WX) ─────────────────────────────────────────────
        private static readonly (int Code, string Name, double Width)[] SymbolAfm =
        {
            (32, "space", 250),
            (33, "exclam", 333),
            (34, "universal", 713),
            (35, "numbersign", 500),
            (36, "existential", 549),
            (37, "percent", 833),
            (38, "ampersand", 778),
            (39, "suchthat", 439),
            (40, "parenleft", 333),
            (41, "parenright", 333),
            (42, "asteriskmath", 500),
            (43, "plus", 549),
            (44, "comma", 250),
            (45, "minus", 549),
            (46, "period", 250),
            (47, "slash", 278),
            (48, "zero", 500),
            (49, "one", 500),
            (50, "two", 500),
            (51, "three", 500),
            (52, "four", 500),
            (53, "five", 500),
            (54, "six", 500),
            (55, "seven", 500),
            (56, "eight", 500),
            (57, "nine", 500),
            (58, "colon", 278),
            (59, "semicolon", 278),
            (60, "less", 549),
            (61, "equal", 549),
            (62, "greater", 549),
            (63, "question", 444),
            (64, "congruent", 549),
            (65, "Alpha", 722),
            (66, "Beta", 667),
            (67, "Chi", 722),
            (68, "Delta", 612),
            (69, "Epsilon", 611),
            (70, "Phi", 763),
            (71, "Gamma", 603),
            (72, "Eta", 722),
            (73, "Iota", 333),
            (74, "theta1", 631),
            (75, "Kappa", 722),
            (76, "Lambda", 686),
            (77, "Mu", 889),
            (78, "Nu", 722),
            (79, "Omicron", 722),
            (80, "Pi", 768),
            (81, "Theta", 741),
            (82, "Rho", 556),
            (83, "Sigma", 592),
            (84, "Tau", 611),
            (85, "Upsilon", 690),
            (86, "sigma1", 439),
            (87, "Omega", 768),
            (88, "Xi", 645),
            (89, "Psi", 795),
            (90, "Zeta", 611),
            (91, "bracketleft", 333),
            (92, "therefore", 863),
            (93, "bracketright", 333),
            (94, "perpendicular", 658),
            (95, "underscore", 500),
            (96, "radicalex", 500),
            (97, "alpha", 631),
            (98, "beta", 549),
            (99, "chi", 549),
            (100, "delta", 494),
            (101, "epsilon", 439),
            (102, "phi", 521),
            (103, "gamma", 411),
            (104, "eta", 603),
            (105, "iota", 329),
            (106, "phi1", 603),
            (107, "kappa", 549),
            (108, "lambda", 549),
            (109, "mu", 576),
            (110, "nu", 521),
            (111, "omicron", 549),
            (112, "pi", 549),
            (113, "theta", 521),
            (114, "rho", 549),
            (115, "sigma", 603),
            (116, "tau", 439),
            (117, "upsilon", 576),
            (118, "omega1", 713),
            (119, "omega", 686),
            (120, "xi", 493),
            (121, "psi", 686),
            (122, "zeta", 494),
            (123, "braceleft", 480),
            (124, "bar", 200),
            (125, "braceright", 480),
            (126, "similar", 549),
            (128, "apple", 790),
            (160, "Euro", 750),
            (161, "Upsilon1", 620),
            (162, "minute", 247),
            (163, "lessequal", 549),
            (164, "fraction", 167),
            (165, "infinity", 713),
            (166, "florin", 500),
            (167, "club", 753),
            (168, "diamond", 753),
            (169, "heart", 753),
            (170, "spade", 753),
            (171, "arrowboth", 1042),
            (172, "arrowleft", 987),
            (173, "arrowup", 603),
            (174, "arrowright", 987),
            (175, "arrowdown", 603),
            (176, "degree", 400),
            (177, "plusminus", 549),
            (178, "second", 411),
            (179, "greaterequal", 549),
            (180, "multiply", 549),
            (181, "proportional", 713),
            (182, "partialdiff", 494),
            (183, "bullet", 460),
            (184, "divide", 549),
            (185, "notequal", 549),
            (186, "equivalence", 549),
            (187, "approxequal", 549),
            (188, "ellipsis", 1000),
            (189, "arrowvertex", 603),
            (190, "arrowhorizex", 1000),
            (191, "carriagereturn", 658),
            (192, "aleph", 823),
            (193, "Ifraktur", 686),
            (194, "Rfraktur", 795),
            (195, "weierstrass", 987),
            (196, "circlemultiply", 768),
            (197, "circleplus", 768),
            (198, "emptyset", 823),
            (199, "intersection", 768),
            (200, "union", 768),
            (201, "propersuperset", 713),
            (202, "reflexsuperset", 713),
            (203, "notsubset", 713),
            (204, "propersubset", 713),
            (205, "reflexsubset", 713),
            (206, "element", 713),
            (207, "notelement", 713),
            (208, "angle", 768),
            (209, "gradient", 713),
            (210, "registerserif", 790),
            (211, "copyrightserif", 790),
            (212, "trademarkserif", 890),
            (213, "product", 823),
            (214, "radical", 549),
            (215, "dotmath", 250),
            (216, "logicalnot", 713),
            (217, "logicaland", 603),
            (218, "logicalor", 603),
            (219, "arrowdblboth", 1042),
            (220, "arrowdblleft", 987),
            (221, "arrowdblup", 603),
            (222, "arrowdblright", 987),
            (223, "arrowdbldown", 603),
            (224, "lozenge", 494),
            (225, "angleleft", 329),
            (226, "registersans", 790),
            (227, "copyrightsans", 790),
            (228, "trademarksans", 786),
            (229, "summation", 713),
            (230, "parenlefttp", 384),
            (231, "parenleftex", 384),
            (232, "parenleftbt", 384),
            (233, "bracketlefttp", 384),
            (234, "bracketleftex", 384),
            (235, "bracketleftbt", 384),
            (236, "bracelefttp", 494),
            (237, "braceleftmid", 494),
            (238, "braceleftbt", 494),
            (239, "braceex", 494),
            (241, "angleright", 329),
            (242, "integral", 274),
            (243, "integraltp", 686),
            (244, "integralex", 686),
            (245, "integralbt", 686),
            (246, "parenrighttp", 384),
            (247, "parenrightex", 384),
            (248, "parenrightbt", 384),
            (249, "bracketrighttp", 384),
            (250, "bracketrightex", 384),
            (251, "bracketrightbt", 384),
            (252, "bracerighttp", 494),
            (253, "bracerightmid", 494),
            (254, "bracerightbt", 494),
        };

        // ── ZapfDingbats (AFM C / N / WX) ───────────────────────────────────────
        private static readonly (int Code, string Name, double Width)[] ZapfDingbatsAfm =
        {
            (32, "space", 278),
            (33, "a1", 974),
            (34, "a2", 961),
            (35, "a202", 974),
            (36, "a3", 980),
            (37, "a4", 719),
            (38, "a5", 789),
            (39, "a119", 790),
            (40, "a118", 791),
            (41, "a117", 690),
            (42, "a11", 960),
            (43, "a12", 939),
            (44, "a13", 549),
            (45, "a14", 855),
            (46, "a15", 911),
            (47, "a16", 933),
            (48, "a105", 911),
            (49, "a17", 945),
            (50, "a18", 974),
            (51, "a19", 755),
            (52, "a20", 846),
            (53, "a21", 762),
            (54, "a22", 761),
            (55, "a23", 571),
            (56, "a24", 677),
            (57, "a25", 763),
            (58, "a26", 760),
            (59, "a27", 759),
            (60, "a28", 754),
            (61, "a6", 494),
            (62, "a7", 552),
            (63, "a8", 537),
            (64, "a9", 577),
            (65, "a10", 692),
            (66, "a29", 786),
            (67, "a30", 788),
            (68, "a31", 788),
            (69, "a32", 790),
            (70, "a33", 793),
            (71, "a34", 794),
            (72, "a35", 816),
            (73, "a36", 823),
            (74, "a37", 789),
            (75, "a38", 841),
            (76, "a39", 823),
            (77, "a40", 833),
            (78, "a41", 816),
            (79, "a42", 831),
            (80, "a43", 923),
            (81, "a44", 744),
            (82, "a45", 723),
            (83, "a46", 749),
            (84, "a47", 790),
            (85, "a48", 792),
            (86, "a49", 695),
            (87, "a50", 776),
            (88, "a51", 768),
            (89, "a52", 792),
            (90, "a53", 759),
            (91, "a54", 707),
            (92, "a55", 708),
            (93, "a56", 682),
            (94, "a57", 701),
            (95, "a58", 826),
            (96, "a59", 815),
            (97, "a60", 789),
            (98, "a61", 789),
            (99, "a62", 707),
            (100, "a63", 687),
            (101, "a64", 696),
            (102, "a65", 689),
            (103, "a66", 786),
            (104, "a67", 787),
            (105, "a68", 713),
            (106, "a69", 791),
            (107, "a70", 785),
            (108, "a71", 791),
            (109, "a72", 873),
            (110, "a73", 761),
            (111, "a74", 762),
            (112, "a203", 762),
            (113, "a75", 759),
            (114, "a204", 759),
            (115, "a76", 892),
            (116, "a77", 892),
            (117, "a78", 788),
            (118, "a79", 784),
            (119, "a81", 438),
            (120, "a82", 138),
            (121, "a83", 277),
            (122, "a84", 415),
            (123, "a97", 392),
            (124, "a98", 392),
            (125, "a99", 668),
            (126, "a100", 668),
            (128, "a89", 390),
            (129, "a90", 390),
            (130, "a93", 317),
            (131, "a94", 317),
            (132, "a91", 276),
            (133, "a92", 276),
            (134, "a205", 509),
            (135, "a85", 509),
            (136, "a206", 410),
            (137, "a86", 410),
            (138, "a87", 234),
            (139, "a88", 234),
            (140, "a95", 334),
            (141, "a96", 334),
            (161, "a101", 732),
            (162, "a102", 544),
            (163, "a103", 544),
            (164, "a104", 910),
            (165, "a106", 667),
            (166, "a107", 760),
            (167, "a108", 760),
            (168, "a112", 776),
            (169, "a111", 595),
            (170, "a110", 694),
            (171, "a109", 626),
            (172, "a120", 788),
            (173, "a121", 788),
            (174, "a122", 788),
            (175, "a123", 788),
            (176, "a124", 788),
            (177, "a125", 788),
            (178, "a126", 788),
            (179, "a127", 788),
            (180, "a128", 788),
            (181, "a129", 788),
            (182, "a130", 788),
            (183, "a131", 788),
            (184, "a132", 788),
            (185, "a133", 788),
            (186, "a134", 788),
            (187, "a135", 788),
            (188, "a136", 788),
            (189, "a137", 788),
            (190, "a138", 788),
            (191, "a139", 788),
            (192, "a140", 788),
            (193, "a141", 788),
            (194, "a142", 788),
            (195, "a143", 788),
            (196, "a144", 788),
            (197, "a145", 788),
            (198, "a146", 788),
            (199, "a147", 788),
            (200, "a148", 788),
            (201, "a149", 788),
            (202, "a150", 788),
            (203, "a151", 788),
            (204, "a152", 788),
            (205, "a153", 788),
            (206, "a154", 788),
            (207, "a155", 788),
            (208, "a156", 788),
            (209, "a157", 788),
            (210, "a158", 788),
            (211, "a159", 788),
            (212, "a160", 894),
            (213, "a161", 838),
            (214, "a163", 1016),
            (215, "a164", 458),
            (216, "a196", 748),
            (217, "a165", 924),
            (218, "a192", 748),
            (219, "a166", 918),
            (220, "a167", 927),
            (221, "a168", 928),
            (222, "a169", 928),
            (223, "a170", 834),
            (224, "a171", 873),
            (225, "a172", 828),
            (226, "a173", 924),
            (227, "a162", 924),
            (228, "a174", 917),
            (229, "a175", 930),
            (230, "a176", 931),
            (231, "a177", 463),
            (232, "a178", 883),
            (233, "a179", 836),
            (234, "a193", 836),
            (235, "a180", 867),
            (236, "a199", 867),
            (237, "a181", 696),
            (238, "a200", 696),
            (239, "a182", 874),
            (241, "a201", 874),
            (242, "a183", 760),
            (243, "a184", 946),
            (244, "a197", 771),
            (245, "a185", 865),
            (246, "a194", 771),
            (247, "a198", 888),
            (248, "a186", 967),
            (249, "a195", 888),
            (250, "a187", 831),
            (251, "a188", 873),
            (252, "a189", 927),
            (253, "a190", 970),
            (254, "a191", 918),
        };
    }
}
