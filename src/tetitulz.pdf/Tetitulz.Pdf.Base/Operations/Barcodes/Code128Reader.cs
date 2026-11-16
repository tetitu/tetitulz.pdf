// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Decodes a Code 128 symbol from one row's runs — all three code sets, and the Code A, Code B,
    /// Code C and Shift characters that move between them.
    /// </summary>
    /// <remarks>
    /// <para><b>A value's meaning is its code set's, and getting that wrong is not an error but a
    /// plausible wrong payload.</b> Code set C reads each value as a <i>pair</i> of digits, so a C symbol
    /// misread as B returns half as many characters and looks perfectly well-formed; A maps 64–95 to
    /// the control characters where B has lowercase letters. So the code set is tracked through every
    /// switch, and Shift reads exactly one following value in the other of A and B.</para>
    ///
    /// <para><b>The function characters are reported, not read as text</b> (ISO/IEC 15417, as established readers
    /// read it). FNC1 in the first data position marks GS1 data (identifier <c>]C1</c>), after
    /// one character or one digit pair an AIM application (<c>]C2</c>) — neither is transmitted — and
    /// anywhere else is the GS separator U+001D. FNC4 alone adds 128 to the next character; two in a
    /// row latch extended ASCII until two more, and inside the latch a single FNC4 takes the next
    /// character back down. FNC2 asks for the payload to be appended to the next symbol's and FNC3 makes
    /// the symbol a reader instruction; each is a flag on <see cref="BarcodeSymbolInfo"/>.</para>
    ///
    /// <para><b>The check character is the reader's independent evidence.</b> It is a weighted sum
    /// over the decoded <i>values</i>, computed by a rule with nothing to do with the bar patterns,
    /// so a misread character almost never survives it. Without that check a reader returns wrong
    /// text with the same confidence as right text.</para>
    /// </remarks>
    internal static class Code128Reader
    {
        /// <summary>Elements in an ordinary character: three bars and three spaces.</summary>
        private const int CharacterElements = 6;

        /// <summary>Elements in Stop, which carries a seventh for its terminating bar.</summary>
        private const int StopElements = 7;

        /// <summary>Start value for code set A.</summary>
        private const int StartA = 103;

        /// <summary>Start value for code set C.</summary>
        private const int StartC = 105;

        /// <summary>Highest value code sets A and B map to a character; above this are switches and functions.</summary>
        private const int LastTextValue = 95;

        /// <summary>Highest value code set C maps to a digit pair.</summary>
        private const int LastDigitPairValue = 99;

        /// <summary>Code set A's values from here up are the control characters NUL–US.</summary>
        private const int FirstControlValueInA = 64;

        /// <summary>Shift, in A or B: the next value only is read in the other of the two.</summary>
        private const int Shift = 98;

        /// <summary>Code C, in A or B.</summary>
        private const int CodeC = 99;

        /// <summary>Code B in A and C; FNC4 in B.</summary>
        private const int CodeBOrFnc4 = 100;

        /// <summary>Code A in B and C; FNC4 in A.</summary>
        private const int CodeAOrFnc4 = 101;

        /// <summary>FNC3 in A and B (a digit pair in C).</summary>
        private const int Fnc3 = 96;

        /// <summary>FNC2 in A and B (a digit pair in C).</summary>
        private const int Fnc2 = 97;

        /// <summary>FNC1, in every code set.</summary>
        private const int Fnc1 = 102;

        /// <summary>The GS separator a later FNC1 stands for in GS1 data.</summary>
        private const char GroupSeparator = '\u001D';

        /// <summary>Most characters a symbol may carry before this stops looking.</summary>
        /// <remarks>
        /// A bound, not a limit of the symbology: it keeps a pathological row from being walked
        /// character by character to no purpose. A real symbol is far shorter, and a row cannot hold
        /// more characters than its width admits anyway.
        /// </remarks>
        private const int MaxCharacters = 512;

        /// <summary>
        /// Decodes the symbol starting at run <paramref name="first"/>, or returns
        /// <see langword="null"/>.
        /// </summary>
        /// <param name="runs">The row's runs.</param>
        /// <param name="first">Index of the run the Start character begins at; must be a bar.</param>
        /// <param name="endRun">Index one past the symbol's last run, when this succeeds.</param>
        /// <param name="refusal">
        /// Why a symbol that <i>was</i> a Code 128 start could not be read — null when the run did
        /// not begin one at all. The distinction carries the whole design: every dark run on the
        /// page is tried as a start, so "not a barcode" must stay silent, while "a barcode this
        /// cannot read" must not.
        /// </param>
        internal static string Decode(
            List<BarcodeScanline.Run> runs, int first, out int endRun, out string refusal)
            => Decode(runs, first, out endRun, out refusal, out _);

        /// <summary>As <see cref="Decode(List{BarcodeScanline.Run}, int, out int, out string)"/>, with what the symbol's function characters said.</summary>
        internal static string Decode(
            List<BarcodeScanline.Run> runs, int first, out int endRun, out string refusal,
            out BarcodeSymbolInfo info)
        {
            endRun = first;
            refusal = null;
            info = null;

            if (first + CharacterElements > runs.Count || !runs[first].IsDark) return null;

            var widths = new int[StopElements];
            if (!BarcodeScanline.Quantise(runs, first, CharacterElements,
                                          Code128Symbols.CharacterModules, widths))
                return null;

            int start = Code128Symbols.Value(
                BarcodeScanline.Pattern(widths, CharacterElements, firstIsDark: true));

            // Any of the three Starts is parsed, because the module patterns are subset-independent
            // -- a value is a value, and only its *meaning* depends on the subset. Which subset it
            // was is decided at the end, once there is evidence a symbol is really there.
            if (start != Code128Symbols.StartB && start != StartA && start != StartC) return null;

            // Everything between Start and Stop, in order. The last entry is the check character
            // rather than payload, and it is excluded from the sum that validates it — which is far
            // easier to see written this way than folded into the reading loop.
            var values = new List<int>();
            int run = first + CharacterElements;
            bool sawStop = false;

            for (int character = 0; character <= MaxCharacters && !sawStop; character++)
            {
                // Stop is tried first, and must be: it is 13 modules in seven elements, and
                // quantising its first six runs as an 11-module character can succeed by accident,
                // which would swallow the terminator and read on into the quiet zone.
                if (run + StopElements <= runs.Count
                    && BarcodeScanline.Quantise(runs, run, StopElements,
                                                Code128Symbols.StopModules, widths)
                    && Code128Symbols.Value(
                           BarcodeScanline.Pattern(widths, StopElements, firstIsDark: true))
                       == Code128Symbols.Stop)
                {
                    endRun = run + StopElements;
                    sawStop = true;
                    break;
                }

                if (run + CharacterElements > runs.Count) return null;
                if (!BarcodeScanline.Quantise(runs, run, CharacterElements,
                                              Code128Symbols.CharacterModules, widths))
                    return null;

                int value = Code128Symbols.Value(
                    BarcodeScanline.Pattern(widths, CharacterElements, firstIsDark: true));
                if (value < 0) return null;

                values.Add(value);
                run += CharacterElements;
            }

            if (!sawStop || values.Count < 1) return null;

            // The check character may legitimately hold any value, including one that is a subset
            // switch when read as data -- so it is verified before anything is asked about meaning.
            int check = values[values.Count - 1];
            int checksum = start;
            for (int i = 0; i < values.Count - 1; i++) checksum += (i + 1) * values[i];

            // ---------------------------------------------------------------------------------
            //  Nothing above this line reports anything, and that is deliberate.
            //
            //  Every dark run on the page is offered here as a possible Start, and ordinary text
            //  throws up six-element groups that quantise to a valid Start pattern often enough to
            //  matter -- reporting on a Start alone would say "subset A is not decoded" for a
            //  page of plain prose. A Start alone is not evidence of a barcode. A Start, a well-formed Stop,
            //  every character between them a known value, and a matching check character together
            //  are: that is the point past which a refusal is worth a caller's attention.
            // ---------------------------------------------------------------------------------
            if (checksum % Code128Symbols.ChecksumModulus != check)
            {
                refusal = "Code 128 symbol's check character does not match its contents";
                return null;
            }

            char codeSet = start == StartA ? 'A' : start == StartC ? 'C' : 'B';
            int dataCount = values.Count - 1;
            var text = new StringBuilder(2 * dataCount);
            var symbol = new BarcodeSymbolInfo();

            // Data characters (a text character or a digit pair) seen so far, and whether the first
            // was one — what FNC1's position is measured by. Switches and Shift are not data.
            int dataSeen = 0;
            bool sawFnc1 = false;
            bool extendedLatch = false, extendedNext = false;

            for (int i = 0; i < dataCount; i++)
            {
                int value = values[i];

                if (value == Fnc1)
                {
                    if (!sawFnc1 && dataSeen == 0) { symbol.IsGs1 = true; symbol.Modifier = 1; }
                    else if (!sawFnc1 && dataSeen == 1) symbol.Modifier = 2;
                    else text.Append(GroupSeparator);
                    sawFnc1 = true;
                    continue;
                }

                if (codeSet == 'C')
                {
                    if (value <= LastDigitPairValue)
                    {
                        text.Append((char)('0' + value / 10)).Append((char)('0' + value % 10));
                        dataSeen++;
                        continue;
                    }
                    if (value == CodeBOrFnc4) { codeSet = 'B'; continue; }
                    if (value == CodeAOrFnc4) { codeSet = 'A'; continue; }
                    refusal = FunctionRefusal(value);
                    return null;
                }

                if (value <= LastTextValue)
                {
                    AppendText(text, Character(codeSet, value), ref extendedNext, extendedLatch);
                    dataSeen++;
                    continue;
                }

                switch (value)
                {
                    case Shift:
                        // One value, read in the other set; a Shift with no text value after it is
                        // not a symbol this can read.
                        if (i + 1 >= dataCount || values[i + 1] > LastTextValue)
                        {
                            refusal = "Code 128 symbol's Shift is not followed by a character";
                            return null;
                        }
                        AppendText(text, Character(codeSet == 'A' ? 'B' : 'A', values[++i]), ref extendedNext, extendedLatch);
                        dataSeen++;
                        continue;
                    case CodeC:
                        codeSet = 'C';
                        continue;
                    case CodeBOrFnc4 when codeSet == 'A':
                        codeSet = 'B';
                        continue;
                    case CodeAOrFnc4 when codeSet == 'B':
                        codeSet = 'A';
                        continue;
                    case CodeAOrFnc4 when codeSet == 'A':
                    case CodeBOrFnc4 when codeSet == 'B':
                        // FNC4: two in a row toggle the extended latch; one shifts the next
                        // character into (or, inside the latch, out of) the upper half.
                        if (i + 1 < dataCount && values[i + 1] == value)
                        {
                            extendedLatch = !extendedLatch;
                            i++;
                            continue;
                        }
                        if (i + 1 >= dataCount)
                        {
                            refusal = "Code 128 symbol's FNC4 is not followed by a character";
                            return null;
                        }
                        extendedNext = true;
                        continue;
                    case Fnc2:
                        symbol.AppendsToNext = true;
                        continue;
                    case Fnc3:
                        symbol.ReaderProgramming = true;
                        continue;
                    default:
                        refusal = FunctionRefusal(value);
                        return null;
                }
            }

            info = symbol;
            return text.ToString();
        }

        /// <summary>A text character, in the upper half when the FNC4 latch and a pending single FNC4 disagree.</summary>
        private static void AppendText(StringBuilder text, char c, ref bool extendedNext, bool extendedLatch)
        {
            bool upper = extendedLatch != extendedNext;
            extendedNext = false;
            text.Append(upper ? (char)(c + 128) : c);
        }

        /// <summary>The character a text value (0–95) stands for in code set A or B.</summary>
        private static char Character(char codeSet, int value)
            => codeSet == 'A' && value >= FirstControlValueInA
                ? (char)(value - FirstControlValueInA)
                : (char)(value + Code128Symbols.SubsetBFirstAscii);

        /// <summary>
        /// Why a value with no meaning where it stands refuses the symbol. Every function character
        /// has a meaning, so what is left is a Start or Stop value among the data.
        /// </summary>
        private static string FunctionRefusal(int value)
            => $"Code 128 symbol carries a Start or Stop value ({value}) among its data";
    }
}
