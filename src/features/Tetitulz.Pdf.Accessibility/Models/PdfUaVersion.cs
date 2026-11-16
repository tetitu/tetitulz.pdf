// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>
    /// Which part of ISO 14289 an audit judges a document against.
    /// </summary>
    /// <remarks>
    /// <para><b>The two parts are different conformance targets, not one target with more
    /// checkpoints.</b> A PDF/UA-2 document audited against PDF/UA-1 is wrong twice: it is told its
    /// <c>pdfuaid:part</c> must be 1, and none of ISO 14289-2's own clauses are checked. The audit
    /// therefore runs one standard's checkers or the other's, never both, and a finding's code says
    /// which — <c>UA00</c>–<c>UA99</c> for PDF/UA-1, <c>UA2-</c> followed by the clause number for
    /// PDF/UA-2.</para>
    ///
    /// <para><see cref="Auto"/> exists because the document already says which it claims to be:
    /// the XMP identification schema's <c>pdfuaid:part</c>. Only a declared <c>2</c> selects
    /// PDF/UA-2; a document declaring <c>1</c>, declaring nothing, or carrying a packet that cannot
    /// be read is judged against PDF/UA-1, which is also what a caller that names no target
    /// gets.</para>
    /// </remarks>
    public enum PdfUaVersion
    {
        /// <summary>PDF/UA-1 (ISO 14289-1:2014). The default; nothing moves for a caller that names no target.</summary>
        Ua1,

        /// <summary>
        /// PDF/UA-2 (ISO 14289-2:2024): Clause 5's version identification
        /// (<c>Plugins.Conformance.PdfUa2Checker</c>, <c>UA2-5</c>) and the deep structure and
        /// content clauses (<c>Checking.Ua2.PdfUa2StructureChecker</c>, <c>UA2-8.x</c>).
        /// </summary>
        Ua2,

        /// <summary>
        /// Read the document's own <c>pdfuaid:part</c> declaration: <c>2</c> selects
        /// <see cref="Ua2"/>, anything else — including no packet at all — selects
        /// <see cref="Ua1"/>. The part actually applied is reported as
        /// <c>AccessibilityAuditReport.ConformanceTarget</c>, which is never this value.
        /// </summary>
        Auto,
    }
}
