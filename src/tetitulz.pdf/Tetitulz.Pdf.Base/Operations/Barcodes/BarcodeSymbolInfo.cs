// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// What a symbol's function characters said about its payload, beside the payload itself — the
    /// AIM symbology identifier's modifier (ISO/IEC 15424), GS1 data, a message to be appended to the
    /// next symbol, a reader-programming symbol, a Data Matrix structured-append position.
    /// </summary>
    /// <remarks>
    /// The function characters are application conventions rather than characters, so the reader
    /// <i>reports</i> them here instead of
    /// either refusing the symbol or pretending they are text: a GS1 symbol's leading FNC1 is its
    /// identifier (<c>]C1</c>, <c>]d2</c>), never a character of its payload, and every later FNC1 is
    /// the GS separator (U+001D) GS1's element strings are split by. The grammar is the established readers',
    /// two independent implementations of ISO/IEC 15417 and 16022, read against each other.
    /// </remarks>
    internal sealed class BarcodeSymbolInfo
    {
        /// <summary>The identifier's modifier digit: 0 for plain Code 128, 1 for GS1, 2 for AIM; 1, 2, 3 for Data Matrix.</summary>
        internal int Modifier;

        /// <summary>The symbol carries GS1 data (FNC1 in first position).</summary>
        internal bool IsGs1;

        /// <summary>Code 128's FNC2: the payload is to be stored and transmitted with the next symbol's.</summary>
        internal bool AppendsToNext;

        /// <summary>Code 128's FNC3 or Data Matrix's reader-programming codeword: the payload instructs the reader.</summary>
        internal bool ReaderProgramming;

        /// <summary>Data Matrix structured append: this symbol's position (1-based), or 0.</summary>
        internal int SequencePosition;

        /// <summary>Data Matrix structured append: how many symbols the message spans, or 0.</summary>
        internal int SequenceCount;
    }
}
