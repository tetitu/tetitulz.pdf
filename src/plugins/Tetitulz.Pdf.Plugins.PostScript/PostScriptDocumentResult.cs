// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>The outcome of converting a whole PostScript or EPS document to PDF.</summary>
    public sealed class PostScriptDocumentResult
    {
        /// <summary>
        /// Pages written. Always at least 1 — a source that drew nothing still yields a valid
        /// single-page PDF, because a zero-page file is not one a caller can open.
        /// </summary>
        public int PageCount { get; set; }

        /// <summary>
        /// True when the PDF holds only the blank page written in the document's place: the source
        /// drew nothing and showed no page, or its conversion failed (reported among the diagnostics, so
        /// the result is not <see cref="IsClean"/>).
        /// </summary>
        public bool BlankPageOnly { get; set; }

        /// <summary>
        /// True when the source announced itself as EPS (<c>%!PS-Adobe-… EPSF-…</c>). An EPS is a
        /// single illustration rather than a document, so it is expected to produce one page.
        /// </summary>
        public bool IsEps { get; set; }

        /// <summary>
        /// The declared <c>%%HiResBoundingBox</c> / <c>%%BoundingBox</c> as
        /// <c>[llx lly urx ury]</c>, or null when the source declared neither. When present it
        /// determines the page size, in preference to the measured extent.
        /// </summary>
        public double[] DeclaredBoundingBox { get; set; }

        /// <summary>Operators the source used that this interpreter does not implement.</summary>
        public IReadOnlyList<string> UnimplementedOperators { get; set; } = Array.Empty<string>();

        /// <summary>The last tolerated failure, or null when the conversion ran clean.</summary>
        public string LastError { get; set; }

        /// <summary>How many failures were tolerated. Zero means nothing was recovered from.</summary>
        public int DiagnosticCount { get; set; }

        /// <summary>True when a page or byte cap stopped further pages being collected.</summary>
        public bool PageLimitReached { get; set; }

        /// <summary>True when the operation or allocation budget ran out mid-conversion.</summary>
        public bool BudgetExhausted { get; set; }

        /// <summary>
        /// True when an <c>eexec</c> or <c>%%BeginBinary</c> section was skipped rather than
        /// interpreted. Embedded font programs are out of scope, so glyphs they defined are absent.
        /// </summary>
        public bool SkippedBinarySection { get; set; }

        /// <summary>
        /// True when nothing was tolerated, truncated or exhausted. Says nothing about
        /// <i>fidelity</i> — an unimplemented operator still counts as a diagnostic, which is the
        /// point.
        /// </summary>
        public bool IsClean => DiagnosticCount == 0 && !PageLimitReached && !BudgetExhausted;
    }
}
