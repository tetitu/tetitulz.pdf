// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;

namespace Tetitulz.Pdf.Accessibility
{
    /// <summary>
    /// Options for <see cref="PdfAccessibility.Audit(System.IO.Stream, AuditOptions)"/>.
    /// <para>Every property has the default the option-less overloads use: <c>new AuditOptions()</c>
    /// audits against PDF/UA-1 under the deep checker's own content ceilings, so
    /// <c>Audit(stream, new AuditOptions())</c> and <c>Audit(stream)</c> are the same audit.</para>
    /// </summary>
    public sealed class AuditOptions
    {
        /// <summary>
        /// The part of ISO 14289 to judge the document against. Default <see cref="PdfUaVersion.Ua1"/>;
        /// see <see cref="PdfUaVersion"/> for what each value runs and how <see cref="PdfUaVersion.Auto"/>
        /// decides.
        /// </summary>
        public PdfUaVersion Target { get; set; } = PdfUaVersion.Ua1;

        /// <summary>
        /// Per-page decoded-content ceiling in bytes for the deep checks; <c>0</c> or less disables
        /// it. Default <see cref="PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes"/>. See
        /// <see cref="PdfAccessibility.Audit(System.IO.Stream, int, long)"/> for what a page outside
        /// the ceiling is reported as.
        /// </summary>
        public int MaxPageContentBytes { get; set; } = PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes;

        /// <summary>
        /// Document-wide decoded-content budget in bytes for the deep checks; <c>0</c> or less
        /// disables it. Default <see cref="PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes"/>.
        /// </summary>
        public long MaxDocumentContentBytes { get; set; } = PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes;
    }
}
