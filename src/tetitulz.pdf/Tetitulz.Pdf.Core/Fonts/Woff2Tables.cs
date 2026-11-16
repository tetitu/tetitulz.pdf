// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The two normative tables WOFF 2.0 defines by enumeration: the known table tags (§4.1) and the
    /// coordinate triplet encoding (§5.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Both were extracted from the W3C recommendation, not transcribed from memory.</b>
    /// This repository has a written rule about that, earned the hard way: three of five JBIG2
    /// Annex B tables written from recollection were <i>provably impossible</i> (their Kraft sums
    /// exceeded 1) and had to be deleted rather than left to mislead. A 128-row table is exactly the
    /// artefact that rule is about.</para>
    ///
    /// <para><b>And the triplet table has its own Kraft-sum equivalent</b>, which
    /// <c>Woff2NormativeTableTests</c> asserts: the spec says Byte Count <i>includes</i> the flag
    /// byte, so <c>xBits + yBits</c> must equal <c>(byteCount - 1) * 8</c> for every one of the 128
    /// rows, and a sign must be present exactly where the matching bit width is non-zero. That is
    /// checkable without owning the specification, and it is what turns "I copied a table" into
    /// something a reader can verify.</para>
    ///
    /// <para><b>The tags are padded here, not in the spec's rendering.</b> §4.1 says tags shorter
    /// than four characters are padded with trailing spaces, and the specification's own table
    /// renders <c>cvt </c>, <c>CFF </c> and <c>SVG </c> trimmed. Reading them trimmed produces
    /// three-character tags that match no sfnt table and are rejected by
    /// <see cref="SfntBuilder"/> — loudly, which is the one mercy in it.</para>
    /// </remarks>
    internal static class Woff2Tables
    {
        /// <summary>One row of the §5.2 triplet encoding.</summary>
        /// <remarks>
        /// <c>ByteCount</c> counts the flag byte too, so the coordinate bytes to read from the glyph
        /// stream are <c>ByteCount - 1</c>. Getting that wrong shifts every later point of the glyph
        /// rather than failing, which is why it is stated here rather than at the call site.
        /// </remarks>
        internal readonly struct TripletRow
        {
            internal TripletRow(int byteCount, int xBits, int yBits, int deltaX, int deltaY,
                                int xSign, int ySign)
            {
                ByteCount = byteCount;
                XBits = xBits;
                YBits = yBits;
                DeltaX = deltaX;
                DeltaY = deltaY;
                XSign = xSign;
                YSign = ySign;
            }

            internal int ByteCount { get; }
            internal int XBits { get; }
            internal int YBits { get; }
            internal int DeltaX { get; }
            internal int DeltaY { get; }

            /// <summary>-1, +1, or 0 when the coordinate has no bits and so no sign.</summary>
            internal int XSign { get; }

            /// <summary>-1, +1, or 0 when the coordinate has no bits and so no sign.</summary>
            internal int YSign { get; }
        }

        /// <summary>The §4.1 known table tags, indexed by the low six bits of the directory flags.</summary>
        /// <remarks>
        /// Index 63 is not here: it means "an arbitrary four-byte tag follows", which is a shape and
        /// not a tag.
        /// </remarks>
        private static readonly string[] KnownTags =
        {
            "cmap", "head", "hhea", "hmtx", "maxp",   // 0
            "name", "OS/2", "post", "cvt ", "fpgm",   // 5
            "glyf", "loca", "prep", "CFF ", "VORG",   // 10
            "EBDT", "EBLC", "gasp", "hdmx", "kern",   // 15
            "LTSH", "PCLT", "VDMX", "vhea", "vmtx",   // 20
            "BASE", "GDEF", "GPOS", "GSUB", "EBSC",   // 25
            "JSTF", "MATH", "CBDT", "CBLC", "COLR",   // 30
            "CPAL", "SVG ", "sbix", "acnt", "avar",   // 35
            "bdat", "bloc", "bsln", "cvar", "fdsc",   // 40
            "feat", "fmtx", "fvar", "gvar", "hsty",   // 45
            "just", "lcar", "mort", "morx", "opbd",   // 50
            "prop", "trak", "Zapf", "Silf", "Glat",   // 55
            "Gloc", "Feat", "Sill",   // 60
        };

        /// <summary>The §5.2 triplet encoding, indexed by the low seven bits of a point's flag byte.</summary>
        private static readonly TripletRow[] Triplets =
        {
            new TripletRow(2,  0,  8,     0,     0,  0, -1),  // 0
            new TripletRow(2,  0,  8,     0,     0,  0,  1),  // 1
            new TripletRow(2,  0,  8,     0,   256,  0, -1),  // 2
            new TripletRow(2,  0,  8,     0,   256,  0,  1),  // 3
            new TripletRow(2,  0,  8,     0,   512,  0, -1),  // 4
            new TripletRow(2,  0,  8,     0,   512,  0,  1),  // 5
            new TripletRow(2,  0,  8,     0,   768,  0, -1),  // 6
            new TripletRow(2,  0,  8,     0,   768,  0,  1),  // 7
            new TripletRow(2,  0,  8,     0,  1024,  0, -1),  // 8
            new TripletRow(2,  0,  8,     0,  1024,  0,  1),  // 9
            new TripletRow(2,  8,  0,     0,     0, -1,  0),  // 10
            new TripletRow(2,  8,  0,     0,     0,  1,  0),  // 11
            new TripletRow(2,  8,  0,   256,     0, -1,  0),  // 12
            new TripletRow(2,  8,  0,   256,     0,  1,  0),  // 13
            new TripletRow(2,  8,  0,   512,     0, -1,  0),  // 14
            new TripletRow(2,  8,  0,   512,     0,  1,  0),  // 15
            new TripletRow(2,  8,  0,   768,     0, -1,  0),  // 16
            new TripletRow(2,  8,  0,   768,     0,  1,  0),  // 17
            new TripletRow(2,  8,  0,  1024,     0, -1,  0),  // 18
            new TripletRow(2,  8,  0,  1024,     0,  1,  0),  // 19
            new TripletRow(2,  4,  4,     1,     1, -1, -1),  // 20
            new TripletRow(2,  4,  4,     1,     1,  1, -1),  // 21
            new TripletRow(2,  4,  4,     1,     1, -1,  1),  // 22
            new TripletRow(2,  4,  4,     1,     1,  1,  1),  // 23
            new TripletRow(2,  4,  4,     1,    17, -1, -1),  // 24
            new TripletRow(2,  4,  4,     1,    17,  1, -1),  // 25
            new TripletRow(2,  4,  4,     1,    17, -1,  1),  // 26
            new TripletRow(2,  4,  4,     1,    17,  1,  1),  // 27
            new TripletRow(2,  4,  4,     1,    33, -1, -1),  // 28
            new TripletRow(2,  4,  4,     1,    33,  1, -1),  // 29
            new TripletRow(2,  4,  4,     1,    33, -1,  1),  // 30
            new TripletRow(2,  4,  4,     1,    33,  1,  1),  // 31
            new TripletRow(2,  4,  4,     1,    49, -1, -1),  // 32
            new TripletRow(2,  4,  4,     1,    49,  1, -1),  // 33
            new TripletRow(2,  4,  4,     1,    49, -1,  1),  // 34
            new TripletRow(2,  4,  4,     1,    49,  1,  1),  // 35
            new TripletRow(2,  4,  4,    17,     1, -1, -1),  // 36
            new TripletRow(2,  4,  4,    17,     1,  1, -1),  // 37
            new TripletRow(2,  4,  4,    17,     1, -1,  1),  // 38
            new TripletRow(2,  4,  4,    17,     1,  1,  1),  // 39
            new TripletRow(2,  4,  4,    17,    17, -1, -1),  // 40
            new TripletRow(2,  4,  4,    17,    17,  1, -1),  // 41
            new TripletRow(2,  4,  4,    17,    17, -1,  1),  // 42
            new TripletRow(2,  4,  4,    17,    17,  1,  1),  // 43
            new TripletRow(2,  4,  4,    17,    33, -1, -1),  // 44
            new TripletRow(2,  4,  4,    17,    33,  1, -1),  // 45
            new TripletRow(2,  4,  4,    17,    33, -1,  1),  // 46
            new TripletRow(2,  4,  4,    17,    33,  1,  1),  // 47
            new TripletRow(2,  4,  4,    17,    49, -1, -1),  // 48
            new TripletRow(2,  4,  4,    17,    49,  1, -1),  // 49
            new TripletRow(2,  4,  4,    17,    49, -1,  1),  // 50
            new TripletRow(2,  4,  4,    17,    49,  1,  1),  // 51
            new TripletRow(2,  4,  4,    33,     1, -1, -1),  // 52
            new TripletRow(2,  4,  4,    33,     1,  1, -1),  // 53
            new TripletRow(2,  4,  4,    33,     1, -1,  1),  // 54
            new TripletRow(2,  4,  4,    33,     1,  1,  1),  // 55
            new TripletRow(2,  4,  4,    33,    17, -1, -1),  // 56
            new TripletRow(2,  4,  4,    33,    17,  1, -1),  // 57
            new TripletRow(2,  4,  4,    33,    17, -1,  1),  // 58
            new TripletRow(2,  4,  4,    33,    17,  1,  1),  // 59
            new TripletRow(2,  4,  4,    33,    33, -1, -1),  // 60
            new TripletRow(2,  4,  4,    33,    33,  1, -1),  // 61
            new TripletRow(2,  4,  4,    33,    33, -1,  1),  // 62
            new TripletRow(2,  4,  4,    33,    33,  1,  1),  // 63
            new TripletRow(2,  4,  4,    33,    49, -1, -1),  // 64
            new TripletRow(2,  4,  4,    33,    49,  1, -1),  // 65
            new TripletRow(2,  4,  4,    33,    49, -1,  1),  // 66
            new TripletRow(2,  4,  4,    33,    49,  1,  1),  // 67
            new TripletRow(2,  4,  4,    49,     1, -1, -1),  // 68
            new TripletRow(2,  4,  4,    49,     1,  1, -1),  // 69
            new TripletRow(2,  4,  4,    49,     1, -1,  1),  // 70
            new TripletRow(2,  4,  4,    49,     1,  1,  1),  // 71
            new TripletRow(2,  4,  4,    49,    17, -1, -1),  // 72
            new TripletRow(2,  4,  4,    49,    17,  1, -1),  // 73
            new TripletRow(2,  4,  4,    49,    17, -1,  1),  // 74
            new TripletRow(2,  4,  4,    49,    17,  1,  1),  // 75
            new TripletRow(2,  4,  4,    49,    33, -1, -1),  // 76
            new TripletRow(2,  4,  4,    49,    33,  1, -1),  // 77
            new TripletRow(2,  4,  4,    49,    33, -1,  1),  // 78
            new TripletRow(2,  4,  4,    49,    33,  1,  1),  // 79
            new TripletRow(2,  4,  4,    49,    49, -1, -1),  // 80
            new TripletRow(2,  4,  4,    49,    49,  1, -1),  // 81
            new TripletRow(2,  4,  4,    49,    49, -1,  1),  // 82
            new TripletRow(2,  4,  4,    49,    49,  1,  1),  // 83
            new TripletRow(3,  8,  8,     1,     1, -1, -1),  // 84
            new TripletRow(3,  8,  8,     1,     1,  1, -1),  // 85
            new TripletRow(3,  8,  8,     1,     1, -1,  1),  // 86
            new TripletRow(3,  8,  8,     1,     1,  1,  1),  // 87
            new TripletRow(3,  8,  8,     1,   257, -1, -1),  // 88
            new TripletRow(3,  8,  8,     1,   257,  1, -1),  // 89
            new TripletRow(3,  8,  8,     1,   257, -1,  1),  // 90
            new TripletRow(3,  8,  8,     1,   257,  1,  1),  // 91
            new TripletRow(3,  8,  8,     1,   513, -1, -1),  // 92
            new TripletRow(3,  8,  8,     1,   513,  1, -1),  // 93
            new TripletRow(3,  8,  8,     1,   513, -1,  1),  // 94
            new TripletRow(3,  8,  8,     1,   513,  1,  1),  // 95
            new TripletRow(3,  8,  8,   257,     1, -1, -1),  // 96
            new TripletRow(3,  8,  8,   257,     1,  1, -1),  // 97
            new TripletRow(3,  8,  8,   257,     1, -1,  1),  // 98
            new TripletRow(3,  8,  8,   257,     1,  1,  1),  // 99
            new TripletRow(3,  8,  8,   257,   257, -1, -1),  // 100
            new TripletRow(3,  8,  8,   257,   257,  1, -1),  // 101
            new TripletRow(3,  8,  8,   257,   257, -1,  1),  // 102
            new TripletRow(3,  8,  8,   257,   257,  1,  1),  // 103
            new TripletRow(3,  8,  8,   257,   513, -1, -1),  // 104
            new TripletRow(3,  8,  8,   257,   513,  1, -1),  // 105
            new TripletRow(3,  8,  8,   257,   513, -1,  1),  // 106
            new TripletRow(3,  8,  8,   257,   513,  1,  1),  // 107
            new TripletRow(3,  8,  8,   513,     1, -1, -1),  // 108
            new TripletRow(3,  8,  8,   513,     1,  1, -1),  // 109
            new TripletRow(3,  8,  8,   513,     1, -1,  1),  // 110
            new TripletRow(3,  8,  8,   513,     1,  1,  1),  // 111
            new TripletRow(3,  8,  8,   513,   257, -1, -1),  // 112
            new TripletRow(3,  8,  8,   513,   257,  1, -1),  // 113
            new TripletRow(3,  8,  8,   513,   257, -1,  1),  // 114
            new TripletRow(3,  8,  8,   513,   257,  1,  1),  // 115
            new TripletRow(3,  8,  8,   513,   513, -1, -1),  // 116
            new TripletRow(3,  8,  8,   513,   513,  1, -1),  // 117
            new TripletRow(3,  8,  8,   513,   513, -1,  1),  // 118
            new TripletRow(3,  8,  8,   513,   513,  1,  1),  // 119
            new TripletRow(4, 12, 12,     0,     0, -1, -1),  // 120
            new TripletRow(4, 12, 12,     0,     0,  1, -1),  // 121
            new TripletRow(4, 12, 12,     0,     0, -1,  1),  // 122
            new TripletRow(4, 12, 12,     0,     0,  1,  1),  // 123
            new TripletRow(5, 16, 16,     0,     0, -1, -1),  // 124
            new TripletRow(5, 16, 16,     0,     0,  1, -1),  // 125
            new TripletRow(5, 16, 16,     0,     0, -1,  1),  // 126
            new TripletRow(5, 16, 16,     0,     0,  1,  1),  // 127
        };

        /// <summary>The tag for a directory flags byte, or <see langword="null"/> for index 63.</summary>
        internal static string TagFor(int knownIndex)
            => knownIndex >= 0 && knownIndex < KnownTags.Length ? KnownTags[knownIndex] : null;

        /// <summary>The triplet row for a point flag's low seven bits.</summary>
        internal static TripletRow TripletFor(int flag) => Triplets[flag & 0x7F];

        /// <summary>How many rows the triplet table has — 128, and asserted rather than assumed.</summary>
        internal static int TripletCount => Triplets.Length;

        /// <summary>How many known tags there are — 63, index 63 being the escape.</summary>
        internal static int KnownTagCount => KnownTags.Length;

        /// <summary>The triplet row at an index, for the guard that checks the table's shape.</summary>
        internal static TripletRow TripletAt(int index) => Triplets[index];
    }
}
