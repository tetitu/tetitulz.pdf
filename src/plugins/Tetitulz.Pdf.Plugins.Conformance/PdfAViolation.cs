// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Describes a single PDF/A-1b conformance violation.
    /// </summary>
    public sealed class PdfAViolation
    {
        /// <summary>Short machine-readable violation code (e.g., "A01", "A06-FontNotEmbedded").</summary>
        public string Code { get; }

        /// <summary>Human-readable description of the violation.</summary>
        public string Message { get; }

        public PdfAViolation(string code, string message)
            : this(code, message, null)
        {
        }

        internal PdfAViolation(string code, string message, string[] concerns)
        {
            Code = code;
            Message = message;
            Concerns = concerns ?? new[] { code };
        }

        /// <summary>
        /// The checks this entry is about: a violation's own code, and for an A99 the codes of the
        /// checks that did not run in full.
        /// </summary>
        /// <remarks>
        /// <see cref="PdfAConverter"/> sets aside the checks it cannot judge before writing the
        /// output (A07, A15–A18), and it has to set aside a partial run of those checks for the same
        /// reason — so an A99 has to say which check it is about in a form the converter can read,
        /// not only in its message. A violation concerns itself, so one rule serves both lists.
        /// </remarks>
        internal IReadOnlyList<string> Concerns { get; }

        public override string ToString() => $"[{Code}] {Message}";
    }
}
