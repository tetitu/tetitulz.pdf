// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// What a reader can say about a file's Annex F claim when it may hold only part of the file.
    /// </summary>
    /// <remarks>
    /// <para><b>The third value is the whole point.</b> A <c>bool</c> has to fold "I have not seen
    /// the bytes yet" into one of the two real answers, and it folds into <c>false</c> — so a
    /// download in progress is reported as an ordinary file, and a caller that was deciding whether
    /// to attempt a byte-range fetch decides not to, permanently, on no evidence.</para>
    ///
    /// <para><b><see cref="NotYetAvailable"/> means more bytes could change the answer</b>, and
    /// nothing else. It is not returned where the answer is already final: Annex F.3.3 puts the
    /// linearization dictionary inside the file's first 1024 bytes, so once that window has arrived
    /// and does not carry one, <see cref="NotLinearized"/> is the settled answer however much of the
    /// file is still in flight.</para>
    /// </remarks>
    public enum PdfLinearizationState
    {
        /// <summary>
        /// Not enough of the file has arrived to answer, and more of it would change the answer.
        /// </summary>
        NotYetAvailable = 0,

        /// <summary>
        /// The file's first object is not a linearization dictionary. Final — Annex F.3.3 fixes
        /// where such a dictionary must be, so later bytes cannot introduce one.
        /// </summary>
        NotLinearized = 1,

        /// <summary>
        /// The file declares itself linearized. This is the producer's <b>claim</b>, exactly as
        /// <c>PdfRevisions.IsLinearized</c> reports it — not a statement that its hint tables are
        /// correct or still match the file.
        /// </summary>
        Linearized = 2,
    }
}
