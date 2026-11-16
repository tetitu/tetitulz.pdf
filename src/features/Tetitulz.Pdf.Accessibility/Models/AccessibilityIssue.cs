// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>Severity level of an accessibility issue.</summary>
    public enum IssueSeverity { Error, Warning, Info }

    /// <summary>
    /// Functional category of an accessibility issue.
    /// <para>
    /// <b>Ordering is part of the public contract</b> — members are persisted by name in
    /// reports and grouped by value. New members may only be <i>appended</i> at the end;
    /// never insert or reorder.
    /// </para>
    /// </summary>
    public enum IssueCategory
    {
        Structure,      // Structure tree, element nesting
        Language,       // /Lang, natural language
        Images,         // Alt text on figures
        Forms,          // Form field tooltips, interactive controls
        Color,          // Color contrast (informational only)
        ReadingOrder,   // Logical reading order
        Metadata,       // XMP, /Info dictionary
        Tagging,        // /MarkInfo, tagged content
        Navigation,     // Headings, bookmarks, tab order
        Tables,         // Table structure, header cells
        Lists,          // List structure
        Artifacts,      // Decorative/artifact marking
        Fonts,          // Embedding, /ToUnicode coverage, glyph-to-Unicode mapping
        Annotations,    // Annotation /Contents, /Alt, link objects, widget nesting
    }

    /// <summary>WCAG 2.1 success criterion level.</summary>
    public enum WcagLevel { A, AA, AAA, None }

    /// <summary>
    /// A single accessibility issue found in a PDF.
    ///
    /// <para><b>Locating the issue.</b> <see cref="PageNumber"/>, <see cref="ObjectNumber"/> and
    /// <see cref="StructPath"/> answer "where?". A remediation UI needs at least one of them to be
    /// actionable, so producers should always attach whatever they know — use
    /// <see cref="AtPage"/> / <see cref="AtObject"/> / <see cref="InStruct"/> or
    /// <see cref="With"/> rather than re-listing every constructor argument.</para>
    ///
    /// <para><b>Cross-referencing other tools.</b> <see cref="MatterhornCheckpoint"/> (PDF
    /// Association Matterhorn Protocol) and <see cref="WcagCriterion"/>
    /// (WCAG 2.1 success criterion number) let this report be lined up with an independent checker's output.
    /// The bespoke <see cref="Code"/> ("UA01".."UA15") stays as the stable primary key.</para>
    /// </summary>
    public sealed class AccessibilityIssue
    {
        /// <summary>Severity: Error blocks conformance, Warning is a best-practice deviation, Info is advisory.</summary>
        public IssueSeverity Severity { get; }

        /// <summary>Check code, e.g. "UA10", "UA11", "WCAG-1.3.1".</summary>
        public string Code { get; }

        /// <summary>Human-readable description of the issue.</summary>
        public string Message { get; }

        /// <summary>Suggested remediation action.</summary>
        public string Remediation { get; }

        /// <summary>Functional category.</summary>
        public IssueCategory Category { get; }

        /// <summary>1-based page number where the issue occurs, or null if document-level.</summary>
        public int? PageNumber { get; }

        /// <summary>Applicable WCAG 2.1 level.</summary>
        public WcagLevel WcagLevel { get; }

        /// <summary>
        /// Object number of the offending indirect object — the structure element, annotation,
        /// form field or page object the issue was raised against. Null when the issue is not
        /// attributable to one object (e.g. a missing catalog entry, or an aggregated issue).
        /// Values below 1 are normalised to null, since 0 is never a real object number.
        /// </summary>
        public int? ObjectNumber { get; }

        /// <summary>
        /// Path of the offending element through the structure tree, e.g.
        /// <c>"Document[1]/Sect[2]/Table[1]/TR[3]/TD[1]"</c>. Null when the issue is not anchored
        /// to a structure element (untagged document, catalog-level issue, annotation outside the
        /// tree).
        ///
        /// <para>
        /// <b>Every segment carries an index</b>, 1-based among siblings <i>of the same role</i>
        /// under the same parent — so the second <c>TR</c> is <c>TR[2]</c> whether or not any
        /// other <c>TR</c> exists, and the only <c>TD</c> in a row is <c>TD[1]</c>. The index is
        /// never omitted.
        /// </para>
        ///
        /// <para>
        /// The alternative — emitting an index only when a role repeats — made the path of an
        /// element depend on how many siblings it happened to have: a row with one cell produced
        /// <c>TR[2]/TD</c> and a row with two produced <c>TR[1]/TD[1]</c>, so the same position
        /// was spelled differently in different documents and adding a sibling silently rewrote
        /// the path of an element that had not changed. With indices always present the path is
        /// a stable key: safe to compare across two audits of the same file, to use as a
        /// dictionary key, and to round-trip through a remediation UI.
        /// </para>
        ///
        /// <para>
        /// Very deep paths are truncated from the left with a leading <c>"…/"</c>, keeping the
        /// informative tail. A truncated path is for display only.
        /// </para>
        /// </summary>
        public string StructPath { get; }

        /// <summary>
        /// Matterhorn Protocol checkpoint identifier, e.g. "09-004" or "13-004" — the same
        /// identifiers Matterhorn-based checkers report. Null when the check has no Matterhorn equivalent (most
        /// advisory/Info checks).
        /// </summary>
        public string MatterhornCheckpoint { get; }

        /// <summary>
        /// WCAG 2.1 success criterion number, e.g. "1.3.1" or "2.4.2". Complements
        /// <see cref="WcagLevel"/>, which records only the conformance level and therefore
        /// cannot be matched against another tool's findings on its own.
        /// Null when the check maps to no specific criterion.
        /// </summary>
        public string WcagCriterion { get; }

        /// <summary>
        /// Number of occurrences represented by this entry, for checks that legitimately
        /// aggregate (e.g. "27 figures are missing /Alt" reported once). Null means the entry
        /// represents a single occurrence — callers should therefore count
        /// <c>InstanceCount ?? 1</c>, which is what
        /// <see cref="AccessibilityAuditReport.TotalInstanceCount"/> does.
        /// Values below 1 are normalised to null.
        /// <para>Prefer emitting one issue per occurrence (each with its own page / object)
        /// when the count is bounded; aggregate only to avoid flooding the report.</para>
        /// </summary>
        public int? InstanceCount { get; }

        /// <summary>
        /// Compact "where" string built from <see cref="PageNumber"/> and
        /// <see cref="ObjectNumber"/> — "p.3 obj 12", "p.3", "obj 12" — or null when the
        /// issue is document-level and has neither.
        /// </summary>
        public string LocationDescription
        {
            get
            {
                string page = PageNumber.HasValue
                    ? "p." + PageNumber.Value.ToString(CultureInfo.InvariantCulture)
                    : null;
                string obj = ObjectNumber.HasValue
                    ? "obj " + ObjectNumber.Value.ToString(CultureInfo.InvariantCulture)
                    : null;

                if (page != null && obj != null) return page + " " + obj;
                return page ?? obj;
            }
        }

        /// <summary>
        /// Creates an issue with the core fields. Retained unchanged for source compatibility;
        /// use the full constructor or the fluent <see cref="With"/> helpers to attach object
        /// numbers, structure paths and external check mappings.
        /// </summary>
        public AccessibilityIssue(
            IssueSeverity severity,
            string code,
            string message,
            string remediation,
            IssueCategory category,
            int? pageNumber = null,
            WcagLevel wcagLevel = WcagLevel.None)
            : this(severity, code, message, remediation, category, pageNumber, wcagLevel,
                   null, null, null, null, null)
        {
        }

        /// <summary>
        /// Creates a fully-populated issue.
        /// </summary>
        /// <param name="severity">Error / Warning / Info.</param>
        /// <param name="code">Stable check code (required).</param>
        /// <param name="message">Human-readable description (required).</param>
        /// <param name="remediation">Suggested fix; null becomes an empty string.</param>
        /// <param name="category">Functional category.</param>
        /// <param name="pageNumber">1-based page number, or null for document-level.</param>
        /// <param name="wcagLevel">WCAG conformance level.</param>
        /// <param name="objectNumber">Offending indirect object number; values below 1 become null.</param>
        /// <param name="structPath">Structure-tree path; blank becomes null.</param>
        /// <param name="matterhornCheckpoint">Matterhorn checkpoint id; blank becomes null.</param>
        /// <param name="wcagCriterion">WCAG success criterion number; blank becomes null.</param>
        /// <param name="instanceCount">Aggregated occurrence count; values below 1 become null.</param>
        public AccessibilityIssue(
            IssueSeverity severity,
            string code,
            string message,
            string remediation,
            IssueCategory category,
            int? pageNumber,
            WcagLevel wcagLevel,
            int? objectNumber,
            string structPath,
            string matterhornCheckpoint,
            string wcagCriterion,
            int? instanceCount)
        {
            Severity    = severity;
            Code        = code ?? throw new ArgumentNullException(nameof(code));
            Message     = message ?? throw new ArgumentNullException(nameof(message));
            Remediation = remediation ?? string.Empty;
            Category    = category;
            PageNumber  = pageNumber;
            WcagLevel   = wcagLevel;

            ObjectNumber         = objectNumber.HasValue  && objectNumber.Value  >= 1 ? objectNumber  : null;
            InstanceCount        = instanceCount.HasValue && instanceCount.Value >= 1 ? instanceCount : null;
            StructPath           = Normalize(structPath);
            MatterhornCheckpoint = Normalize(matterhornCheckpoint);
            WcagCriterion        = Normalize(wcagCriterion);
        }

        /// <summary>
        /// Returns a copy of this issue with the supplied fields replaced.
        /// <para><b>A null / omitted argument keeps the current value</b> — this helper can set
        /// or overwrite a field but never clears one. Use a constructor to build an issue with a
        /// field deliberately absent.</para>
        /// </summary>
        public AccessibilityIssue With(
            IssueSeverity? severity = null,
            string code = null,
            string message = null,
            string remediation = null,
            IssueCategory? category = null,
            int? pageNumber = null,
            WcagLevel? wcagLevel = null,
            int? objectNumber = null,
            string structPath = null,
            string matterhornCheckpoint = null,
            string wcagCriterion = null,
            int? instanceCount = null)
            => new AccessibilityIssue(
                severity    ?? Severity,
                code        ?? Code,
                message     ?? Message,
                remediation ?? Remediation,
                category    ?? Category,
                pageNumber  ?? PageNumber,
                wcagLevel   ?? WcagLevel,
                objectNumber ?? ObjectNumber,
                structPath           ?? StructPath,
                matterhornCheckpoint ?? MatterhornCheckpoint,
                wcagCriterion        ?? WcagCriterion,
                instanceCount        ?? InstanceCount);

        /// <summary>Returns a copy located on the given 1-based page.</summary>
        public AccessibilityIssue AtPage(int pageNumber) => With(pageNumber: pageNumber);

        /// <summary>
        /// Returns a copy located on the given indirect object, optionally also recording the
        /// 1-based page the object belongs to.
        /// </summary>
        public AccessibilityIssue AtObject(int objectNumber, int? pageNumber = null)
            => With(objectNumber: objectNumber, pageNumber: pageNumber);

        /// <summary>
        /// Returns a copy anchored to a structure-tree path, e.g.
        /// "Document/Sect[2]/Table/TR[3]/TD[1]".
        /// </summary>
        public AccessibilityIssue InStruct(string structPath) => With(structPath: structPath);

        /// <summary>
        /// Returns a copy carrying the external check mappings used to line this issue up with
        /// Matterhorn- and WCAG-based reports. Either argument may be null to leave that
        /// mapping unchanged.
        /// </summary>
        public AccessibilityIssue MappedTo(string matterhornCheckpoint, string wcagCriterion = null)
            => With(matterhornCheckpoint: matterhornCheckpoint, wcagCriterion: wcagCriterion);

        /// <summary>
        /// Returns a copy that represents <paramref name="instanceCount"/> aggregated
        /// occurrences. Counts below 1 leave the issue as a single occurrence.
        /// </summary>
        public AccessibilityIssue Aggregating(int instanceCount)
            => instanceCount >= 1 ? With(instanceCount: instanceCount) : this;

        /// <summary>
        /// Renders the issue in a stable, greppable single-line format:
        /// <code>
        /// [CODE] [Severity] (p.3 obj 12) Message (x4) @ Document/Sect[2]/Figure [MH 09-004] [WCAG 1.3.1]
        /// </code>
        /// Every segment after the severity is omitted when the corresponding field is null, so
        /// a document-level issue with no mappings renders as <c>[CODE] [Severity] Message</c>.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder(128);
            sb.Append('[').Append(Code).Append("] [").Append(Severity).Append(']');

            string location = LocationDescription;
            if (location != null) sb.Append(" (").Append(location).Append(')');

            sb.Append(' ').Append(Message);

            if (InstanceCount.HasValue)
                sb.Append(" (x").Append(InstanceCount.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
            if (StructPath != null)
                sb.Append(" @ ").Append(StructPath);
            if (MatterhornCheckpoint != null)
                sb.Append(" [MH ").Append(MatterhornCheckpoint).Append(']');
            if (WcagCriterion != null)
                sb.Append(" [WCAG ").Append(WcagCriterion).Append(']');

            return sb.ToString();
        }

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
