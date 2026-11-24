// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// The page's allowance for the form content an extractor runs — the structured text extractor's and the
    /// graphics extractor's alike, priced as the renderers price theirs: each invocation one step of
    /// <see cref="PdfLimits.MaxFormXObjectInvocationsPerPage"/>, each invocation's decoded bytes against
    /// <see cref="PdfLimits.MaxFormContentBytesPerPage"/>, and each stream's operations parsed once a page.
    /// </summary>
    /// <remarks>
    /// An invocation count alone bounds nothing a form's size chooses: a form of a megabyte of operators,
    /// compressed to a few hundred bytes, drawn a hundred thousand times is a hundred gigabytes of operators
    /// run under any invocation ceiling. Once refused the budget stays refused, and the extractor returns a
    /// partial page and says so (<see cref="Shortfall"/>). Not thread-safe: one per page walk.
    /// </remarks>
    internal sealed class FormContentBudget
    {
        private readonly WalkBudget _invocations = new WalkBudget(PdfLimits.MaxFormXObjectInvocationsPerPage);
        private readonly long _bytes = PdfLimits.MaxFormContentBytesPerPage;
        private long _bytesLeft = PdfLimits.MaxFormContentBytesPerPage;
        private readonly ContentOperationCache _operations = new ContentOperationCache();

        /// <summary>True once an invocation or a form's content has been refused.</summary>
        internal bool Exhausted => _invocations.Exhausted || BytesExhausted;

        /// <summary>True once a form's decoded content did not fit what the page had left.</summary>
        internal bool BytesExhausted { get; private set; }

        /// <summary>
        /// Form streams this thread's extractions parsed rather than took from their page's cache, all pages
        /// together: the evidence that a form drawn many times is parsed once a page. A test reads the
        /// difference across one call.
        /// </summary>
        [System.ThreadStatic] internal static long FormParses;

        /// <summary>Charges one invocation, before its work; false once the page's allowance is spent.</summary>
        internal bool TakeInvocation() => !BytesExhausted && _invocations.Take();

        /// <summary>
        /// The operations of a form's decoded <paramref name="data"/>, its length charged first; null once the
        /// page's content allowance cannot cover it, in which case the form runs nothing. The list is shared with
        /// every invocation of the same stream on the page and must not be modified.
        /// </summary>
        internal List<ContentStreamOperation> Operations(byte[] data)
        {
            long length = data?.LongLength ?? 0;
            if (BytesExhausted || length > _bytesLeft)
            {
                BytesExhausted = true;
                return null;
            }
            _bytesLeft -= length;
            int parsed = _operations.Parses;
            var ops = _operations.GetOrParse(data);
            FormParses += _operations.Parses - parsed;
            return ops;
        }

        /// <summary>
        /// The report for a page whose walk stopped short, naming the ceiling it reached; null when it did not.
        /// <paramref name="consequence"/> says what the caller's result lacks.
        /// </summary>
        internal string Shortfall(int pageIndex, string consequence)
        {
            if (BytesExhausted)
                return $"Page {pageIndex + 1}: the forms' decoded content exceeded {_bytes:N0} bytes " +
                       $"(PdfLimits.MaxFormContentBytesPerPage); {consequence}";
            if (_invocations.Exhausted)
                return $"Page {pageIndex + 1}: form-XObject invocations exceeded " +
                       $"{PdfLimits.MaxFormXObjectInvocationsPerPage:N0}; {consequence}";
            return null;
        }
    }
}
