// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>
    /// Full accessibility audit report for a PDF document, carrying the issues from both
    /// checkers <c>PdfAccessibility.Audit</c> runs:
    /// <list type="bullet">
    ///   <item>the base PDF/UA-1 checker in <c>Tetitulz.Pdf.Plugins.Conformance</c>, which emits
    ///     <c>UA00</c>–<c>UA09</c>, <c>UA25</c>–<c>UA29</c>, <c>UA50</c> and up, and <c>UA99</c>;</item>
    ///   <item>the extended deep checker, which emits <c>UA00</c>, <c>UA06</c>,
    ///     <c>UA10</c>–<c>UA24</c>, <c>UA30</c>–<c>UA35</c> and <c>UA99</c>, and maps issues to WCAG
    ///     criteria and Matterhorn checkpoints.</item>
    /// </list>
    /// The two ranges overlap deliberately, and a combined audit drops the base checker's
    /// <c>UA28</c>, <c>UA29</c> and <c>UA50</c>, which the deep checker supersedes with the more
    /// precise <c>UA14</c> (/ParentTree coverage), <c>UA16</c> (empty structure elements) and
    /// <c>UA16</c>–<c>UA18</c> (which follow form XObjects, as UA50 does not). Codes are
    /// this library's own identifiers, not clause numbers of ISO 14289-1.
    /// </summary>
    public sealed class AccessibilityAuditReport
    {
        /// <summary>All issues found, ordered by severity then page number.</summary>
        public IReadOnlyList<AccessibilityIssue> Issues { get; }

        /// <summary>Number of Error-severity issues.</summary>
        public int ErrorCount { get; }

        /// <summary>Number of Warning-severity issues.</summary>
        public int WarningCount { get; }

        /// <summary>Number of Info-severity issues.</summary>
        public int InfoCount { get; }

        /// <summary>
        /// Total number of individual occurrences behind <see cref="Issues"/>, i.e. the sum of
        /// <see cref="AccessibilityIssue.InstanceCount"/> <c>?? 1</c> over every issue.
        /// <para>Use this — not <c>Issues.Count</c> — to size the remediation workload: a single
        /// aggregated entry such as "27 figures are missing /Alt" counts once in
        /// <see cref="ErrorCount"/> but 27 times here.</para>
        /// </summary>
        public int TotalInstanceCount { get; }

        /// <summary>
        /// True when there are zero Error-severity issues.
        ///
        /// <para><b>What this does NOT prove.</b> It is <i>not</i> a statement of PDF/UA-1
        /// conformance, and must never be surfaced to a user as "this file is accessible":</para>
        /// <list type="bullet">
        ///   <item>Only the checks this library implements ran. Content-level requirements —
        ///   marked-content correctness, complete Unicode mapping for every glyph, table
        ///   header/scope association, meaningful reading order — are covered partially or not
        ///   at all, so an unchecked failure cannot appear here.</item>
        ///   <item>Machine checking cannot decide the human requirements at all: whether alt text
        ///   is <i>accurate</i>, whether heading levels reflect the real document outline, or
        ///   whether the reading order is the intended one. PDF/UA-1 requires human review.</item>
        ///   <item>Issues reported as <see cref="IssueSeverity.Warning"/> or
        ///   <see cref="IssueSeverity.Info"/> are ignored by this property even when they would
        ///   fail a stricter validator; see <see cref="IsFullyConforming"/>.</item>
        /// </list>
        /// <para>Read it as "no blocking issue was <i>detected</i>" — equivalently
        /// <c>!</c><see cref="HasBlockingIssues"/> — and validate with an independent PDF/UA checker before
        /// making a conformance claim.</para>
        /// </summary>
        public bool IsConforming => ErrorCount == 0;

        /// <summary>
        /// True when there are zero errors and zero warnings. Carries the same caveats as
        /// <see cref="IsConforming"/>: it reports the absence of <i>detected</i> problems, not
        /// PDF/UA-1 conformance.
        /// </summary>
        public bool IsFullyConforming => ErrorCount == 0 && WarningCount == 0;

        /// <summary>
        /// True when at least one Error-severity issue was found, i.e. remediation is required
        /// before the document can plausibly conform. The positive-sense counterpart to
        /// <see cref="IsConforming"/>, for call sites that read better without a negation.
        /// </summary>
        public bool HasBlockingIssues => ErrorCount > 0;

        /// <summary>Issues grouped by category for structured display.</summary>
        public IReadOnlyDictionary<IssueCategory, IReadOnlyList<AccessibilityIssue>> ByCategory { get; }

        /// <summary>
        /// Issues grouped by 1-based page number (only issues that carry a page number).
        /// Document-level issues are in <see cref="DocumentLevelIssues"/> instead.
        /// </summary>
        public IReadOnlyDictionary<int, IReadOnlyList<AccessibilityIssue>> ByPage { get; }

        /// <summary>
        /// Issues grouped by check <see cref="AccessibilityIssue.Code"/> (ordinal comparison),
        /// so a UI can collapse "27 x UA07" into one expandable row.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<AccessibilityIssue>> ByCode { get; }

        /// <summary>
        /// Issues grouped by <see cref="AccessibilityIssue.MatterhornCheckpoint"/>, for lining
        /// this report up with a Matterhorn-based checker's output. Issues with no checkpoint mapping are excluded.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<AccessibilityIssue>> ByMatterhornCheckpoint { get; }

        /// <summary>
        /// Issues grouped by <see cref="AccessibilityIssue.WcagCriterion"/> (e.g. "1.3.1"), for
        /// WCAG-oriented reporting. Issues with no criterion mapping are excluded.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<AccessibilityIssue>> ByWcagCriterion { get; }

        /// <summary>
        /// Issues that apply to the document as a whole rather than to one page (missing /Lang,
        /// missing /StructTreeRoot, metadata problems). These are exactly the issues absent from
        /// <see cref="ByPage"/>.
        /// </summary>
        public IReadOnlyList<AccessibilityIssue> DocumentLevelIssues { get; }

        /// <summary>Ascending list of the 1-based page numbers that have at least one issue.</summary>
        public IReadOnlyList<int> PagesWithIssues { get; }

        /// <summary>
        /// The part of ISO 14289 the document was judged against — the one actually applied, so
        /// never <see cref="PdfUaVersion.Auto"/>. <see cref="PdfUaVersion.Ua1"/> for every audit
        /// that named no target. Read it before reading <see cref="IsConforming"/>: the caveats on
        /// that property are about the standard this names.
        /// </summary>
        public PdfUaVersion ConformanceTarget { get; }

        public AccessibilityAuditReport(IReadOnlyList<AccessibilityIssue> issues)
            : this(issues, PdfUaVersion.Ua1)
        {
        }

        /// <param name="issues">Every issue found.</param>
        /// <param name="conformanceTarget">
        /// The part the audit ran against; <see cref="PdfUaVersion.Auto"/> is not a part and is
        /// rejected, because a report must say what was checked, not what was asked for.
        /// </param>
        public AccessibilityAuditReport(IReadOnlyList<AccessibilityIssue> issues, PdfUaVersion conformanceTarget)
        {
            if (conformanceTarget == PdfUaVersion.Auto)
                throw new ArgumentOutOfRangeException(nameof(conformanceTarget),
                    "A report names the part it was checked against; Auto is a request, not a part.");
            ConformanceTarget = conformanceTarget;
            Issues = issues ?? throw new ArgumentNullException(nameof(issues));
            ErrorCount   = issues.Count(i => i.Severity == IssueSeverity.Error);
            WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
            InfoCount    = issues.Count(i => i.Severity == IssueSeverity.Info);
            TotalInstanceCount = issues.Sum(i => i.InstanceCount ?? 1);

            ByCategory = issues
                .GroupBy(i => i.Category)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<AccessibilityIssue>)g.OrderBy(i => i.Severity).ToList());

            ByPage = issues
                .Where(i => i.PageNumber.HasValue)
                .GroupBy(i => i.PageNumber.Value)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<AccessibilityIssue>)g.OrderBy(i => i.Severity).ToList());

            ByCode = issues
                .GroupBy(i => i.Code, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<AccessibilityIssue>)g
                        .OrderBy(i => i.Severity)
                        .ThenBy(i => i.PageNumber ?? 0)
                        .ToList(),
                    StringComparer.Ordinal);

            ByMatterhornCheckpoint = GroupByKey(issues, i => i.MatterhornCheckpoint);
            ByWcagCriterion        = GroupByKey(issues, i => i.WcagCriterion);

            DocumentLevelIssues = issues
                .Where(i => !i.PageNumber.HasValue)
                .OrderBy(i => i.Severity)
                .ToList();

            PagesWithIssues = ByPage.Keys.OrderBy(p => p).ToList();
        }

        /// <summary>
        /// Groups the issues by a nullable string key, skipping issues whose key is null.
        /// </summary>
        private static IReadOnlyDictionary<string, IReadOnlyList<AccessibilityIssue>> GroupByKey(
            IReadOnlyList<AccessibilityIssue> issues,
            Func<AccessibilityIssue, string> keySelector)
            => issues
                .Where(i => keySelector(i) != null)
                .GroupBy(keySelector, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<AccessibilityIssue>)g
                        .OrderBy(i => i.Severity)
                        .ThenBy(i => i.PageNumber ?? 0)
                        .ToList(),
                    StringComparer.Ordinal);

        /// <summary>
        /// One-line summary.
        ///
        /// <para>
        /// The clean case deliberately reads "NO BLOCKING ISSUES DETECTED (not a PDF/UA-1
        /// conformance claim)" rather than "CONFORMING". This string is the one that reaches a
        /// human — it is embedded verbatim in <c>RemediationReport.ToDetailedString()</c> — so
        /// every caveat carefully documented on <see cref="IsConforming"/> is invisible at the
        /// moment it matters most. A bare "CONFORMING" overstates what this library can know:
        /// several checks are structural only, and no automated tool can certify PDF/UA-1.
        /// </para>
        /// </summary>
        public override string ToString() =>
            $"Accessibility Audit: {ErrorCount} error(s), {WarningCount} warning(s), {InfoCount} info — " +
            (IsConforming
                ? "NO BLOCKING ISSUES DETECTED (not a " +
                  (ConformanceTarget == PdfUaVersion.Ua2 ? "PDF/UA-2" : "PDF/UA-1") +
                  " conformance claim)"
                : "NON-CONFORMING");
    }
}
