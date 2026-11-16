// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// The Code 128 symbol table — the one implementation, shared by the writer and the reader.
    /// </summary>
    /// <remarks>
    /// <para><b>A leaf by construction.</b> It holds the table and the lookup built from it, and
    /// references nothing else in the assembly, so pointing either direction's code at it creates
    /// no type-initialization edge. CLAUDE.md's rule is to share <i>downward into a leaf</i> rather
    /// than sideways between peers, and a writer and a reader are peers.</para>
    ///
    /// <para><b>Two module counts, and the difference is the whole point.</b> A character is
    /// 11 modules and ends with a space so that characters abut without their bars merging. The
    /// symbol as a whole must still end in dark — a final white element cannot be told from the
    /// quiet zone — so <see cref="Stop"/> carries a seventh element and is 13 modules. Any code
    /// walking these patterns must take the width from <see cref="ModulesOf"/> rather than assume
    /// eleven; assuming eleven is precisely the defect this class was extracted to fix.</para>
    /// </remarks>
    internal static class Code128Symbols
    {
        /// <summary>Modules in an ordinary character.</summary>
        internal const int CharacterModules = 11;

        /// <summary>Modules in <see cref="Stop"/>, which carries the terminating bar.</summary>
        internal const int StopModules = 13;

        /// <summary>Value of the Start character for subset B.</summary>
        internal const int StartB = 104;

        /// <summary>Value of the Stop character.</summary>
        internal const int Stop = 106;

        /// <summary>Highest value that is an ordinary 11-module character.</summary>
        internal const int LastCharacterValue = 105;

        /// <summary>The checksum is taken modulo this.</summary>
        internal const int ChecksumModulus = 103;

        /// <summary>Lowest ASCII code subset B encodes.</summary>
        internal const int SubsetBFirstAscii = 32;

        /// <summary>Highest ASCII code subset B encodes.</summary>
        internal const int SubsetBLastAscii = 127;

        // Values 0-95 are ASCII 32-127; 96=FNC3 97=FNC2 98=ShiftA 99=CodeC 100=CodeB 101=CodeA
        // 102=FNC1 103=StartA 104=StartB 105=StartC 106=Stop.
        //
        // Every row is 11 modules, six alternating elements, bar-first and space-last, with an even
        // number of bar modules — Code 128's own parity rule. `BarcodeSymbolTableTests` asserts all
        // four properties over the whole table, which is what a transcription error trips on: a
        // round trip cannot catch one, because both halves would share it.
        private static readonly int[] Table =
        {
            // Values  0..  9
            0b11011001100, 0b11001101100, 0b11001100110, 0b10010011000, 0b10010001100,
            0b10001001100, 0b10011001000, 0b10011000100, 0b10001100100, 0b11001001000,
            // 10..19
            0b11001000100, 0b11000100100, 0b10110011100, 0b10011011100, 0b10011001110,
            0b10111001100, 0b10011101100, 0b10011100110, 0b11001110010, 0b11001011100,
            // 20..29
            0b11001001110, 0b11011100100, 0b11001110100, 0b11101101110, 0b11101001100,
            0b11100101100, 0b11100100110, 0b11101100100, 0b11100110100, 0b11100110010,
            // 30..39
            0b11011011000, 0b11011000110, 0b11000110110, 0b10100011000, 0b10001011000,
            0b10001000110, 0b10110001000, 0b10001101000, 0b10001100010, 0b11010001000,
            // 40..49
            0b11000101000, 0b11000100010, 0b10110111000, 0b10110001110, 0b10001101110,
            0b10111011000, 0b10111000110, 0b10001110110, 0b11101110110, 0b11010001110,
            // 50..59
            0b11000101110, 0b11011101000, 0b11011100010, 0b11011101110, 0b11101011000,
            0b11101000110, 0b11100010110, 0b11101101000, 0b11101100010, 0b11100011010,
            // 60..69
            0b11101111010, 0b11001000010, 0b11110001010, 0b10100110000, 0b10100001100,
            0b10010110000, 0b10010000110, 0b10000101100, 0b10000100110, 0b10110010000,
            // 70..79
            0b10110000100, 0b10011010000, 0b10011000010, 0b10000110100, 0b10000110010,
            0b11000010010, 0b11001010000, 0b11110111010, 0b11000010100, 0b10001111010,
            // 80..89
            0b10100111100, 0b10010111100, 0b10010011110, 0b10111100100, 0b10011110100,
            0b10011110010, 0b11110100100, 0b11110010100, 0b11110010010, 0b11011011110,
            // 90..99
            0b11011110110, 0b11110110110, 0b10101111000, 0b10100011110, 0b10001011110,
            0b10111101000, 0b10111100010, 0b11110101000, 0b11110100010, 0b10111011110,
            // 100..106
            0b10111101110, 0b11101011110, 0b11110101110,
            0b11010000100,   // 103 = StartA
            0b11010010000,   // 104 = StartB
            0b11010011100,   // 105 = StartC
            //
            // 106 = Stop, and it is THIRTEEN modules: element widths 2,3,3,1,1,1,2. The trailing
            // two-module bar is what terminates the symbol in dark. It was missing here for as long
            // as this library only ever wrote barcodes and never read one.
            0b1100011101011,
        };

        private static readonly Dictionary<int, int> ValueByPattern = BuildLookup();

        /// <summary>The module pattern for a symbol value, most significant module first.</summary>
        internal static int Pattern(int value) => Table[value];

        /// <summary>How many modules <paramref name="value"/>'s pattern occupies.</summary>
        internal static int ModulesOf(int value)
            => value == Stop ? StopModules : CharacterModules;

        /// <summary>Whether the pattern's first module is a bar.</summary>
        internal static bool StartsWithBar(int value)
            => ((Table[value] >> (ModulesOf(value) - 1)) & 1) != 0;

        /// <summary>Whether the pattern's last module is a bar.</summary>
        internal static bool EndsWithBar(int value) => (Table[value] & 1) != 0;

        /// <summary>
        /// The symbol value a pattern encodes, or -1 when the pattern is not in the table.
        /// </summary>
        /// <remarks>
        /// Patterns are distinct across the whole table (asserted), and the 13-module Stop cannot
        /// collide with an 11-module character because its extra modules put it out of range — so
        /// one dictionary serves both widths.
        /// </remarks>
        internal static int Value(int pattern)
            => ValueByPattern.TryGetValue(pattern, out int value) ? value : -1;

        private static Dictionary<int, int> BuildLookup()
        {
            var map = new Dictionary<int, int>(Table.Length);
            for (int value = 0; value < Table.Length; value++) map[Table[value]] = value;
            return map;
        }
    }
}
