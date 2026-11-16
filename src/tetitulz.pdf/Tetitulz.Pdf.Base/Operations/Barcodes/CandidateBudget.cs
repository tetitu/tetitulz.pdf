// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// What a 2-D locator may spend on candidates that are not read: the modules it sampled for them. A
    /// candidate read costs nothing, so a page of many symbols — a sheet of labels — is read to the last.
    /// </summary>
    /// <remarks>
    /// <para><b>Counted in modules, not candidates.</b> How many false candidates come before a page's real ones
    /// depends on the page: a sheet of 48 labels on an equal pitch, whose neighbours' finders make squares as
    /// square as any label's own, failed 56 QR candidates before its last label; a sheet of 130 failed none; data
    /// inside a large turned symbol made 21. The square Data Matrix locator failed 43 Ls on twenty labels and 224
    /// on 130. A count fit for one of those is wrong for another — a fixed count of sixteen or eight candidates,
    /// read or not, would read one QR symbol from a sheet of twenty labels — while a module's price is the same
    /// everywhere. A small false candidate is charged little: the square Data Matrix
    /// locator counting grids would let two single dark modules near the top of a page, 31 pixels across and
    /// fitting every square size, spend the count before the scan reached the real symbol further down.</para>
    /// <para><b>The price, measured</b> (warm, a Release build, on hostile pages of candidates none of
    /// which reads, each search's own costs per candidate included): spending the budget took 13 ms in the square
    /// Data Matrix locator, 80 ms in the outline one and 120 ms in the QR one. It is a thousand and more failed
    /// version 2 QR symbols, or thirty-three of version 40.</para>
    /// </remarks>
    internal sealed class CandidateBudget
    {
        /// <summary>Modules a locator may sample for candidates that are not read.</summary>
        internal const long MaxFailedModules = 1L << 20;

        /// <summary>Modules sampled so far for candidates that were not read.</summary>
        internal long FailedModules { get; private set; }

        /// <summary>Whether the budget is spent.</summary>
        internal bool Spent => FailedModules >= MaxFailedModules;

        /// <summary>Charges a candidate of <paramref name="rows"/> by <paramref name="columns"/> modules that was not read.</summary>
        internal void Fail(int rows, int columns) => FailedModules += (long)rows * columns;
    }
}
