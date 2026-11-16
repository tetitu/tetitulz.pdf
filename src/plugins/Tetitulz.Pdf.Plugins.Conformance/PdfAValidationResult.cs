// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Result of a PDF/A-1b conformance check.
    /// </summary>
    public sealed class PdfAValidationResult
    {
        /// <summary>
        /// True when the checks that ran found no violations — consult <see cref="IsPartial"/> to
        /// find out whether every check did run.
        /// </summary>
        public bool IsConformant => Violations.Count == 0;

        /// <summary>All violations found during validation.</summary>
        public IReadOnlyList<PdfAViolation> Violations { get; }

        /// <summary>
        /// Checks that did not run in full, as A99 entries naming the check and the reason — a
        /// traversal budget spent, a nesting cap reached, a stream that would not decode.
        /// </summary>
        /// <remarks>
        /// A skipped check reports no violation, so without this list an empty
        /// <see cref="Violations"/> meant both "every check ran and found nothing" and "some checks
        /// never finished". It deliberately does <b>not</b> affect <see cref="IsConformant"/>: an A99
        /// says a check did not run, not that the document is wrong — the rule
        /// <c>PdfXValidationResult.Informational</c> and PDF/UA's UA99 follow.
        /// </remarks>
        public IReadOnlyList<PdfAViolation> Informational { get; }

        /// <summary>True when at least one check did not run in full, so a clean result is not a full pass.</summary>
        public bool IsPartial => Informational.Count > 0;

        public PdfAValidationResult(IReadOnlyList<PdfAViolation> violations)
            : this(violations, null)
        {
        }

        internal PdfAValidationResult(
            IReadOnlyList<PdfAViolation> violations, IReadOnlyList<PdfAViolation> informational)
        {
            Violations = violations ?? new List<PdfAViolation>();
            Informational = informational ?? new List<PdfAViolation>();
        }
    }
}
