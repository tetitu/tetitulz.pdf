// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// The two lexical character classes of ISO 32000-1 §7.2.2 and §7.2.3 — <b>white-space</b> and
    /// <b>delimiter</b> — for code outside this assembly that scans raw PDF bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>A deliberately narrow façade over <c>Core.PdfConstants</c>, which stays
    /// <c>internal</c>.</b> That type also carries eighteen named byte constants and the hex-digit
    /// helpers; promoting it wholesale would commit all of it to the public surface to serve two
    /// predicates. Three plugins had hand-written copies —
    /// <c>Plugins.EthereumSign.PdfBlockchainVerifier</c>,
    /// <c>Plugins.Maintenance.PdfOptimizer</c> and <c>Plugins.PostScript.PsScanner</c> — and the
    /// first of those says in its own comment: *"If a third plugin needs it, promote the predicate
    /// then rather than duplicating a third time."* This is that promotion.</para>
    ///
    /// <para><b><c>PsScanner</c> deliberately does NOT route through here, and the reason is the
    /// point.</b> Its two predicates implement <b>PLRM §3.1</b>, PostScript's lexical syntax, not
    /// §7.2.3. The two character sets are byte-for-byte identical today — both take
    /// <c>NUL HT LF FF CR SP</c> and both take <c>( ) &lt; &gt; [ ] { } / %</c> — but they are
    /// identical <i>by coincidence of two specifications</i>, not because one defines the other.
    /// Routing a PostScript scanner through a constant named for the PDF spec would make a change
    /// to one silently change the other, which is the trap
    /// <c>PdfLimits.BezierFlattenSteps</c> vs <c>SvgGradientStopCount</c> records. Centralising a
    /// value is not the same as unifying it.</para>
    ///
    /// <para><b>No static fields</b> — a leaf, like the rest of <c>Core.Constants</c>.</para>
    /// </remarks>
    public static class PdfSyntax
    {
        /// <summary>
        /// §7.2.3 Table 1 white-space: NUL, HT, LF, FF, CR and SP. Note that <b>NUL counts</b> —
        /// a scanner that stops at the usual four ASCII spaces will run past a legal separator.
        /// </summary>
        public static bool IsWhitespace(byte b) => PdfConstants.IsWhitespace(b);

        /// <summary>
        /// §7.2.3 Table 2 delimiters: <c>( ) &lt; &gt; [ ] { } / %</c>. These end a token without
        /// being consumed by it.
        /// </summary>
        public static bool IsDelimiter(byte b) => PdfConstants.IsDelimiter(b);

        /// <summary>
        /// True where a regular token ends — white-space or a delimiter. The composite is here
        /// because a caller testing only one of the two silently runs a name or a number into the
        /// delimiter that terminates it.
        /// </summary>
        public static bool IsEndOfToken(byte b) => PdfConstants.IsEndOfToken(b);
    }
}
