// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Decodes an EAN-13 symbol from one row's runs.
    /// </summary>
    /// <remarks>
    /// <para><b>EAN-13 is fixed-length, which makes it easier to read than Code 128 and easier to
    /// read <i>wrongly</i>.</b> Every symbol is 95 modules in 59 elements, so there is no
    /// terminator to find — the reader has to commit to a starting position and check afterwards
    /// whether what it read was a barcode. Two things do that checking: the centre and end guards
    /// must land exactly where the fixed layout says, and the check digit must match.</para>
    ///
    /// <para><b>The first digit is not encoded anywhere as a digit.</b> Only twelve digits are
    /// drawn; the thirteenth is carried by which of the two left-hand tables each of the six left
    /// digits used. A reader that ignored parity would return a twelve-digit answer that looked
    /// entirely reasonable, so the parity word is assembled as the digits are read and mapped back
    /// through the same table the writer used.</para>
    /// </remarks>
    internal static class Ean13Reader
    {
        /// <summary>Elements per encoded digit: two bars and two spaces.</summary>
        private const int DigitElements = 4;

        /// <summary>Elements in the <c>101</c> guards at each end.</summary>
        private const int EdgeGuardElements = 3;

        /// <summary>Elements in the <c>01010</c> centre guard.</summary>
        private const int CentreGuardElements = 5;

        /// <summary>Digits encoded in each half.</summary>
        private const int HalfDigits = 6;

        /// <summary>
        /// Total elements in a symbol: two edge guards, a centre guard and twelve digits.
        /// </summary>
        internal const int SymbolElements =
            EdgeGuardElements + (HalfDigits * DigitElements)
            + CentreGuardElements + (HalfDigits * DigitElements) + EdgeGuardElements;

        /// <summary>
        /// Decodes the symbol starting at run <paramref name="first"/>, or returns
        /// <see langword="null"/>.
        /// </summary>
        /// <param name="runs">The row's runs.</param>
        /// <param name="first">Index of the run the leading guard begins at; must be a bar.</param>
        /// <param name="endRun">Index one past the symbol's last run, when this succeeds.</param>
        /// <param name="refusal">
        /// Why a well-formed-looking symbol was rejected, or null when this run simply did not begin
        /// one. Only the check digit sets it: a wrong guard or an unknown pattern means "not an
        /// EAN-13 here", which on a page full of ordinary ink is the answer almost every time and
        /// must stay silent.
        /// </param>
        internal static string Decode(
            List<BarcodeScanline.Run> runs, int first, out int endRun, out string refusal)
        {
            endRun = first;
            refusal = null;

            if (first + SymbolElements > runs.Count || !runs[first].IsDark) return null;

            // Quantised as one block: the module width is estimated across all 95 modules rather
            // than from the three-element leading guard, which is far too short a base to divide by
            // at any realistic rendering size. A symbol foreshortened along its length — leaning away,
            // its modules narrowing towards the far end — has no one module width, so when the block
            // does not divide, each guard and digit is quantised on its own width instead, as readers
            // reads each digit by its own four elements; the same guards, tables and check digit judge
            // both.
            var widths = new int[SymbolElements];
            if (!BarcodeScanline.Quantise(runs, first, SymbolElements, EanSymbols.SymbolModules, widths)
                && !QuantisePieces(runs, first, widths))
                return null;

            int element = 0;
            if (!Guard(widths, ref element, EdgeGuardElements,
                       EanSymbols.EdgeGuard, EanSymbols.EdgeGuardModules, firstIsDark: true))
                return null;

            var digits = new StringBuilder(EanSymbols.Digits);
            int parity = 0;

            for (int i = 0; i < HalfDigits; i++)
            {
                // A left-hand digit follows the guard's final bar, so it starts with a space.
                int pattern = BarcodeScanline.Pattern(
                    Slice(widths, element, DigitElements), DigitElements, firstIsDark: false);
                element += DigitElements;

                int digit = EanSymbols.LeftDigit(pattern, out bool evenParity);
                if (digit < 0) return null;

                digits.Append((char)('0' + digit));
                parity = (parity << 1) | (evenParity ? 1 : 0);
            }

            if (!Guard(widths, ref element, CentreGuardElements,
                       EanSymbols.CentreGuard, EanSymbols.CentreGuardModules, firstIsDark: false))
                return null;

            for (int i = 0; i < HalfDigits; i++)
            {
                // A right-hand digit follows the centre guard's final space, so it starts with a bar.
                int pattern = BarcodeScanline.Pattern(
                    Slice(widths, element, DigitElements), DigitElements, firstIsDark: true);
                element += DigitElements;

                int digit = EanSymbols.RightDigit(pattern);
                if (digit < 0) return null;

                digits.Append((char)('0' + digit));
            }

            if (!Guard(widths, ref element, EdgeGuardElements,
                       EanSymbols.EdgeGuard, EanSymbols.EdgeGuardModules, firstIsDark: true))
                return null;

            int firstDigit = EanSymbols.FirstDigitFromParity(parity);
            if (firstDigit < 0) return null;

            string code = (char)('0' + firstDigit) + digits.ToString();
            if (EanSymbols.CheckDigit(code) != code[code.Length - 1] - '0')
            {
                refusal = $"EAN-13 symbol '{code}' fails its own check digit";
                return null;
            }

            endRun = first + SymbolElements;
            return code;
        }

        /// <summary>
        /// The symbol's element widths quantised a piece at a time — each guard on its own modules, each digit on
        /// seven — into <paramref name="widths"/>; <see langword="false"/> when a piece does not divide, or when its
        /// module is out of step with the nearest digit's. Foreshortening changes the module a few per cent from one
        /// digit to the next (a symbol whose far end is under half its near end's height, about six); an element a
        /// module too wide makes its digit an eighth larger and back, which per-digit reading would otherwise take for
        /// a digit, so a digit may differ from the one before it by <see cref="MaxDigitStep"/> at most and a malformed
        /// symbol stays refused. A guard's three or five modules are too short a base to hold to that — a pixel is
        /// a seventh of a small one — so a guard is held to <see cref="MaxGuardStep"/> against the digit beside it:
        /// its elements always divide into its modules, and the step is the evidence they are this symbol's.
        /// </summary>
        private static bool QuantisePieces(List<BarcodeScanline.Run> runs, int first, int[] widths)
        {
            var piece = new int[CentreGuardElements];
            int run = first, element = 0;
            double lastDigit = 0, pendingGuard = 0;
            static bool InStep(double a, double b, double step) => a <= step * b && b <= step * a;
            bool Next(int elements, int modules, bool digit)
            {
                if (!BarcodeScanline.Quantise(runs, run, elements, modules, piece)) return false;
                double total = 0;
                for (int i = 0; i < elements; i++) total += runs[run + i].Length;
                double module = total / modules;
                if (digit)
                {
                    if (lastDigit > 0 && !InStep(module, lastDigit, MaxDigitStep)) return false;
                    if (pendingGuard > 0 && !InStep(module, pendingGuard, MaxGuardStep)) return false;
                    lastDigit = module;
                    pendingGuard = 0;
                }
                else
                {
                    if (lastDigit > 0 && !InStep(module, lastDigit, MaxGuardStep)) return false;
                    pendingGuard = module;
                }
                for (int i = 0; i < elements; i++) widths[element + i] = piece[i];
                run += elements;
                element += elements;
                return true;
            }

            if (!Next(EdgeGuardElements, EanSymbols.EdgeGuardModules, digit: false)) return false;
            for (int i = 0; i < HalfDigits; i++) if (!Next(DigitElements, EanSymbols.DigitModules, digit: true)) return false;
            if (!Next(CentreGuardElements, EanSymbols.CentreGuardModules, digit: false)) return false;
            for (int i = 0; i < HalfDigits; i++) if (!Next(DigitElements, EanSymbols.DigitModules, digit: true)) return false;
            return Next(EdgeGuardElements, EanSymbols.EdgeGuardModules, digit: false);
        }

        /// <summary>How far one digit's module may differ from the one before it, as a ratio, when the symbol is read a piece at a time.</summary>
        private const double MaxDigitStep = 1.1;

        /// <summary>How far a guard's module may differ from the digit beside it, as a ratio.</summary>
        private const double MaxGuardStep = 1.5;

        /// <summary>Whether the next elements are the expected guard, advancing past them.</summary>
        private static bool Guard(
            int[] widths, ref int element, int elements, int expected, int modules, bool firstIsDark)
        {
            if (BarcodeScanline.Pattern(Slice(widths, element, elements), elements, firstIsDark)
                != expected)
                return false;

            element += elements;
            return true;
        }

        /// <summary>
        /// The element widths starting at <paramref name="from"/>, as their own array.
        /// </summary>
        /// <remarks>
        /// <see cref="BarcodeScanline.Pattern"/> reads from index 0, and a symbol is read in
        /// pieces, so each piece is copied out. The arrays are five entries at most and one symbol
        /// costs fifteen of them, which is not worth an offset parameter on the shared helper that
        /// every other caller would then have to pass zero for.
        /// </remarks>
        private static int[] Slice(int[] widths, int from, int count)
        {
            var slice = new int[count];
            for (int i = 0; i < count; i++) slice[i] = widths[from + i];
            return slice;
        }
    }
}
