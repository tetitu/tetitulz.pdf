// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Result returned by <see cref="IValidationPlugin.Validate"/>.
    /// </summary>
    public sealed class PluginValidationResult
    {
        /// <summary>The conformance level that was tested (e.g. "PDF/A-1b", "PDF/UA-1").</summary>
        public string ConformanceLevel { get; set; } = string.Empty;

        /// <summary>True if the document meets the conformance level.</summary>
        public bool IsConformant { get; set; }

        /// <summary>List of violations found during validation.</summary>
        public List<PluginValidationViolation> Violations { get; } = new List<PluginValidationViolation>();

        /// <summary>
        /// Checks the plugin could <b>not run</b> — a scan ceiling reached, a stream that would not
        /// decode, a traversal that ran out of budget. These are not defects in the document and
        /// deliberately do not affect <see cref="IsConformant"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this channel exists.</b> A skipped check reports no violation, so without
        /// it <c>IsConformant == true</c> with an empty <see cref="Violations"/> list meant two
        /// different things — "every check ran and found nothing" and "the checks never ran" — and
        /// a caller had no way to tell them apart. For a print-exchange or accessibility verdict
        /// that is the harmful direction: a false pass reaches the RIP, a false failure does not.
        /// <c>PdfXValidator</c> reports this internally as X99, and the plugin surface carries it
        /// out.</para>
        /// <para>The mirror of <c>PdfXValidationResult.Informational</c>, and the same rule
        /// <c>PdfUaChecker</c>'s UA99 and the accessibility report follow: an entry here says a
        /// check did not run, not that the document is wrong.</para>
        /// </remarks>
        public List<PluginValidationViolation> Informational { get; } = new List<PluginValidationViolation>();

        /// <summary>
        /// True when at least one check was skipped, so a clean result is not a full pass.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="Informational"/> rather than settable on purpose: a plugin must
        /// not be able to claim partiality while naming nothing, nor report a skipped check while
        /// claiming the run was complete. Same shape, and the same reason, as
        /// <c>PdfXValidationResult.IsPartial</c>.
        /// </remarks>
        public bool IsPartial => Informational.Count > 0;
    }

    /// <summary>A single conformance violation found by a validation plugin.</summary>
    public sealed class PluginValidationViolation
    {
        /// <summary>Short violation code (e.g. "6.2.3").</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>Human-readable description.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>1-based page number where the violation occurs, or 0 if document-level.</summary>
        public int PageNumber { get; set; }
    }
}
