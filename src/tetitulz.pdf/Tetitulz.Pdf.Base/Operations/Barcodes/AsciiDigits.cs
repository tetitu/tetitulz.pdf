// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Whether a character is one of the ten ASCII digits — the question the symbologies that encode
    /// digits specially (EAN-13 entirely, Data Matrix's digit pairs) both ask.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>char.IsDigit</c>.</b> That is Unicode's Nd category: Arabic-Indic, Devanagari and
    /// full-width digits all pass it, and <c>c - '0'</c> on one of them is a value in the thousands.
    /// EAN-13 indexed its symbol tables with it and threw <see cref="System.IndexOutOfRangeException"/>;
    /// Data Matrix made it a codeword far outside 0–255 and died inside Reed–Solomon.
    /// <c>char.IsAsciiDigit</c> is .NET 7+, and this assembly also targets netstandard2.0.
    /// </remarks>
    internal static class AsciiDigits
    {
        /// <summary>'0' through '9', and nothing else.</summary>
        internal static bool Is(char c) => c >= '0' && c <= '9';
    }
}
