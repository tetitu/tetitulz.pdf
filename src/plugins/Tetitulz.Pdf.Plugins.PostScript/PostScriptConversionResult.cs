// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>
    /// The outcome of converting one PostScript fragment.
    ///
    /// <para><b>Why the diagnostics are duplicated here.</b> <see cref="PostScriptDiagnostics"/>'s
    /// slots are <c>[ThreadStatic]</c>, which is the repo-mandated shape and the right one for a
    /// "what went wrong just now" query — but it makes a concurrent host read thread state to learn
    /// about a value it already holds. These properties carry the same three facts on the result
    /// object, so the common path never touches thread statics. Both, not one.</para>
    /// </summary>
    public sealed class PostScriptConversionResult
    {
        /// <summary>
        /// The PDF content stream, ready to run as a form XObject body. Empty when the program drew
        /// nothing — check <see cref="LastError"/> to tell that from a failure.
        /// </summary>
        public byte[] Content { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Resources the content stream names (<c>/Font</c>, <c>/XObject</c>, <c>/ProcSet</c>), or
        /// null when it names none. Every entry is a <b>direct</b> object: the plugin does not own
        /// the host document's object-number space and must not invent references into it.
        /// </summary>
        public PdfDictionary Resources { get; set; }

        /// <summary>
        /// The extent actually drawn, in the output's coordinate space, or null when nothing was.
        /// Taken from <c>%%HiResBoundingBox</c> / <c>%%BoundingBox</c> when the source declared one,
        /// else measured from the marks.
        /// </summary>
        public double[] BoundingBox { get; set; }

        /// <summary>
        /// Operators the program used that this interpreter does not implement, deduplicated. A
        /// non-empty list means the drawing is missing something that was asked for — the single
        /// most useful signal for deciding whether to trust the output.
        /// </summary>
        public IReadOnlyList<string> UnimplementedOperators { get; set; } = Array.Empty<string>();

        /// <summary>The last tolerated failure, or null when the conversion ran clean.</summary>
        public string LastError { get; set; }

        /// <summary>
        /// How many failures were tolerated. Zero and a non-empty <see cref="Content"/> is the only
        /// combination that means "this is what the program said".
        /// </summary>
        public int DiagnosticCount { get; set; }

        /// <summary>
        /// True when emission stopped at the byte cap. <see cref="Content"/> is still a valid,
        /// balanced content stream — it is simply not all of the drawing.
        /// </summary>
        public bool ContentTruncated { get; set; }

        /// <summary>
        /// True when the operation or allocation budget ran out. The program did not finish, so
        /// what did get drawn may be missing a <c>grestore</c>'s worth of context.
        /// </summary>
        public bool BudgetExhausted { get; set; }

        /// <summary>
        /// True when an <c>eexec</c> or <c>%%BeginBinary</c> section was skipped. Embedded font
        /// programs are out of scope, so glyphs they defined will be absent.
        /// </summary>
        public bool SkippedBinarySection { get; set; }

        /// <summary>
        /// True when the conversion ran with no tolerated failures, no truncation and no exhausted
        /// budget. Note this says nothing about <i>fidelity</i> — an unimplemented operator that was
        /// recorded still sets <see cref="DiagnosticCount"/>, which is exactly the point.
        /// </summary>
        public bool IsClean => DiagnosticCount == 0 && !ContentTruncated && !BudgetExhausted;
    }
}
