// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// The EAN-13 symbol tables — the one implementation, shared by the writer and the reader.
    /// </summary>
    /// <remarks>
    /// <para><b>Three tables that are not independent, which is what makes them checkable.</b>
    /// A right-hand pattern is the complement of the left-odd one for the same digit, and a
    /// left-even pattern is the right-hand one read backwards. So only the ten left-odd rows are
    /// free; the other twenty are determined. `BarcodeSymbolTableTests` asserts both relations,
    /// which is the only way a transcription error in a table like this can be caught — a round
    /// trip through the same table cannot see one.</para>
    ///
    /// <para><b>The complement is not decoration.</b> A right-hand digit begins with a space and a
    /// left-hand digit with a bar, so a scanner reading a symbol upside down can tell which half it
    /// is in and reverse itself. This library's reader does not need to: it reads a typeset page
    /// left to right and makes no rotation search (<see cref="PdfBarcodeReader"/>'s remarks), so a
    /// reversed symbol is simply not found there.</para>
    ///
    /// <para><b>The first digit is not printed as a digit at all.</b> Only twelve digits are
    /// encoded; the thirteenth is carried by <i>which</i> of the two left-hand tables each of the
    /// six left digits uses — see <see cref="FirstDigitParity"/>.</para>
    /// </remarks>
    internal static class EanSymbols
    {
        /// <summary>Modules per encoded digit.</summary>
        internal const int DigitModules = 7;

        /// <summary>Total modules in an EAN-13 symbol, excluding quiet zones.</summary>
        internal const int SymbolModules = 95;

        /// <summary>Digits in a complete EAN-13 code, check digit included.</summary>
        internal const int Digits = 13;

        /// <summary>The <c>101</c> bar pattern that opens and closes the symbol.</summary>
        internal const int EdgeGuard = 0b101;

        /// <summary>Modules in <see cref="EdgeGuard"/>.</summary>
        internal const int EdgeGuardModules = 3;

        /// <summary>The <c>01010</c> pattern separating the two halves.</summary>
        internal const int CentreGuard = 0b01010;

        /// <summary>Modules in <see cref="CentreGuard"/>.</summary>
        internal const int CentreGuardModules = 5;

        // Left half, odd parity ("L"). These ten rows are the only free ones; see the remarks.
        private static readonly int[] LeftOddCode =
        {
            0b0001101, 0b0011001, 0b0010011, 0b0111101, 0b0100011,
            0b0110001, 0b0101111, 0b0111011, 0b0110111, 0b0001011,
        };

        // Left half, even parity ("G") — each row is its Right row reversed.
        private static readonly int[] LeftEvenCode =
        {
            0b0100111, 0b0110011, 0b0011011, 0b0100001, 0b0011101,
            0b0111001, 0b0000101, 0b0010001, 0b0001001, 0b0010111,
        };

        // Right half ("R") — each row is its LeftOdd row complemented.
        private static readonly int[] RightCode =
        {
            0b1110010, 0b1100110, 0b1101100, 0b1000010, 0b1011100,
            0b1001110, 0b1010000, 0b1000100, 0b1001000, 0b1110100,
        };

        // Which of the six left digits use the even-parity table, MSB = the first left digit.
        private static readonly int[] ParityByFirstDigit =
        {
            0b000000,   // 0
            0b001011,   // 1
            0b001101,   // 2
            0b001110,   // 3
            0b010011,   // 4
            0b011001,   // 5
            0b011100,   // 6
            0b010101,   // 7
            0b010110,   // 8
            0b011010,   // 9
        };

        /// <summary>The odd-parity left-hand pattern for a digit.</summary>
        internal static int LeftOdd(int digit) => LeftOddCode[digit];

        /// <summary>The even-parity left-hand pattern for a digit.</summary>
        internal static int LeftEven(int digit) => LeftEvenCode[digit];

        /// <summary>The right-hand pattern for a digit.</summary>
        internal static int Right(int digit) => RightCode[digit];

        /// <summary>
        /// Which of the six left-hand digits take the even-parity table, as six bits with the first
        /// left digit in bit 5.
        /// </summary>
        internal static int FirstDigitParity(int firstDigit) => ParityByFirstDigit[firstDigit];

        /// <summary>
        /// The digit a left-hand pattern encodes, and whether it was even parity; digit is -1 when
        /// the pattern is in neither table.
        /// </summary>
        internal static int LeftDigit(int pattern, out bool evenParity)
        {
            for (int digit = 0; digit <= 9; digit++)
            {
                if (LeftOddCode[digit] == pattern) { evenParity = false; return digit; }
                if (LeftEvenCode[digit] == pattern) { evenParity = true;  return digit; }
            }
            evenParity = false;
            return -1;
        }

        /// <summary>The digit a right-hand pattern encodes, or -1.</summary>
        internal static int RightDigit(int pattern)
        {
            for (int digit = 0; digit <= 9; digit++)
                if (RightCode[digit] == pattern) return digit;
            return -1;
        }

        /// <summary>
        /// The first digit implied by the parity of the six left-hand digits, or -1 when the
        /// combination is not one the table defines.
        /// </summary>
        internal static int FirstDigitFromParity(int parity)
        {
            for (int digit = 0; digit <= 9; digit++)
                if (ParityByFirstDigit[digit] == parity) return digit;
            return -1;
        }

        /// <summary>
        /// The EAN-13 check digit for the first twelve digits.
        /// </summary>
        /// <remarks>
        /// <b>The reader's one genuinely independent check.</b> It is computed from the decoded
        /// digits by a rule that has nothing to do with the module patterns, so a misread bar
        /// almost always yields a payload that fails it. That is what lets the reader refuse a
        /// wrong answer instead of returning one.
        /// </remarks>
        internal static int CheckDigit(string twelveDigits)
        {
            int sum = 0;
            for (int i = 0; i < 12; i++)
                sum += (twelveDigits[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return (10 - (sum % 10)) % 10;
        }
    }
}
