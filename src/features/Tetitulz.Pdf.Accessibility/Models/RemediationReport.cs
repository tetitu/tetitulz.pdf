// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.Accessibility.Models
{
    /// <summary>
    /// Outcome of one step of the remediation pipeline (set language, auto-tag, alt text,
    /// heading hierarchy, artifact marking, reading order, ...).
    ///
    /// <para>A step is in exactly one of three states:</para>
    /// <list type="bullet">
    ///   <item><see cref="Applied"/> — the step ran against the document. It may still have
    ///   changed nothing, in which case <see cref="ItemsChanged"/> is 0 (e.g. "fix heading
    ///   hierarchy" on a document whose headings were already correct).</item>
    ///   <item><see cref="Skipped"/> — the step did not run; <see cref="SkipReason"/> says why
    ///   (disabled in the options, or a precondition such as "document already tagged" was not
    ///   met).</item>
    ///   <item><see cref="Failed"/> — the step ran and threw; <see cref="SkipReason"/> carries
    ///   the error text and the document was left as it was before the step. This is derived
    ///   (<c>!Applied &amp;&amp; !Skipped</c>), not stored.</item>
    /// </list>
    ///
    /// <para>Build instances through <see cref="Ran"/>, <see cref="Skip"/> and
    /// <see cref="Fail"/> rather than the constructor — they enforce the state invariants.</para>
    /// </summary>
    public sealed class RemediationStepResult
    {
        /// <summary>
        /// Stable, human-readable step name, e.g. "SetDocumentLanguage", "AutoTag",
        /// "SetFigureAltText", "FixHeadingHierarchy", "MarkArtifacts", "FixReadingOrder".
        /// </summary>
        public string Name { get; }

        /// <summary>True when the step executed against the document (it may have changed nothing).</summary>
        public bool Applied { get; }

        /// <summary>True when the step did not execute; see <see cref="SkipReason"/>.</summary>
        public bool Skipped { get; }

        /// <summary>
        /// True when the step neither applied nor was deliberately skipped — i.e. it was
        /// attempted and errored. <see cref="SkipReason"/> holds the error text.
        /// </summary>
        public bool Failed => !Applied && !Skipped;

        /// <summary>
        /// Why the step was skipped, or the error text when it <see cref="Failed"/>.
        /// Empty string when the step applied normally (never null).
        /// </summary>
        public string SkipReason { get; }

        /// <summary>
        /// How many document items the step changed — figures given alt text, headings
        /// renumbered, elements reordered, structure elements added, artifacts marked.
        /// 0 for steps that report no count, that were skipped, or that failed.
        /// </summary>
        public int ItemsChanged { get; }

        /// <summary>
        /// Warnings emitted by the step (e.g. <see cref="AutoTagResult.Warnings"/>).
        /// Never null; empty when the step reported none.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// Creates a step result. Prefer the <see cref="Ran"/> / <see cref="Skip"/> /
        /// <see cref="Fail"/> factories, which cannot produce a contradictory state.
        /// </summary>
        /// <param name="name">Step name (required).</param>
        /// <param name="applied">Whether the step executed. Forced to false when
        /// <paramref name="skipped"/> is true — a step cannot both run and be skipped.</param>
        /// <param name="skipped">Whether the step was deliberately not executed.</param>
        /// <param name="skipReason">Skip reason or error text; null becomes an empty string.</param>
        /// <param name="itemsChanged">Number of items changed; negative values are clamped to 0.</param>
        /// <param name="warnings">Step warnings; null becomes an empty list.</param>
        public RemediationStepResult(
            string name,
            bool applied,
            bool skipped,
            string skipReason,
            int itemsChanged,
            IReadOnlyList<string> warnings)
        {
            Name         = name ?? throw new ArgumentNullException(nameof(name));
            Skipped      = skipped;
            Applied      = applied && !skipped;
            SkipReason   = skipReason ?? string.Empty;
            ItemsChanged = itemsChanged > 0 ? itemsChanged : 0;
            Warnings     = warnings ?? Array.Empty<string>();
        }

        /// <summary>Records a step that executed, changing <paramref name="itemsChanged"/> items.</summary>
        public static RemediationStepResult Ran(
            string name, int itemsChanged = 0, IReadOnlyList<string> warnings = null)
            => new RemediationStepResult(name, applied: true, skipped: false,
                                         skipReason: null, itemsChanged: itemsChanged, warnings: warnings);

        /// <summary>Records a step that was deliberately not executed.</summary>
        /// <param name="name">Step name.</param>
        /// <param name="reason">Why it was skipped, e.g. "disabled in options" or
        /// "document already has a structure tree".</param>
        public static RemediationStepResult Skip(string name, string reason)
            => new RemediationStepResult(name, applied: false, skipped: true,
                                         skipReason: reason, itemsChanged: 0, warnings: null);

        /// <summary>
        /// Records a step that was attempted and errored. The pipeline is expected to keep the
        /// pre-step document and carry on, so the failure is reported rather than thrown.
        /// </summary>
        /// <param name="name">Step name.</param>
        /// <param name="error">Error text (typically the exception message).</param>
        public static RemediationStepResult Fail(string name, string error)
            => new RemediationStepResult(name, applied: false, skipped: false,
                                         skipReason: error, itemsChanged: 0,
                                         warnings: string.IsNullOrEmpty(error) ? null : new[] { error });

        /// <summary>
        /// Renders the step in a stable, greppable single-line format, e.g.
        /// <c>AutoTag: applied, 84 item(s) changed, 2 warning(s)</c> or
        /// <c>MarkArtifacts: skipped (disabled in options)</c>.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder(96);
            sb.Append(Name).Append(": ");

            if (Skipped)
            {
                sb.Append("skipped");
                if (SkipReason.Length > 0) sb.Append(" (").Append(SkipReason).Append(')');
            }
            else if (Failed)
            {
                sb.Append("failed");
                if (SkipReason.Length > 0) sb.Append(" (").Append(SkipReason).Append(')');
            }
            else
            {
                sb.Append("applied, ")
                  .Append(ItemsChanged.ToString(CultureInfo.InvariantCulture))
                  .Append(" item(s) changed");
            }

            if (Warnings.Count > 0)
                sb.Append(", ")
                  .Append(Warnings.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(" warning(s)");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Full result of the <c>PdfAccessibility.Remediate</c> pipeline: the audit before and after,
    /// what each step did, every warning raised, and the document sizes.
    ///
    /// <para>Returning only the final audit throws away everything the pipeline learned — how
    /// many figures got alt text, which steps were skipped and why, what the auto-tagger warned
    /// about — so a UI could neither show a remediation log nor explain a step that silently did
    /// nothing. This type carries all of it.</para>
    ///
    /// <para>Typical rendering:</para>
    /// <code>
    /// var result = PdfAccessibility.Remediate(input, output, options);
    /// Console.WriteLine(result);                  // one-line before -&gt; after summary
    /// Console.WriteLine(result.ToDetailedString()); // per-step log + remaining issues
    /// </code>
    /// </summary>
    public sealed class RemediationReport
    {
        /// <summary>Audit of the document as it was supplied, before any step ran.</summary>
        public AccessibilityAuditReport BeforeAudit { get; }

        /// <summary>Audit of the document that was written to the output stream.</summary>
        public AccessibilityAuditReport AfterAudit { get; }

        /// <summary>
        /// Steps in pipeline execution order, including the ones that were skipped or failed —
        /// "why did nothing happen?" is only answerable if skipped steps are reported too.
        /// Never null.
        /// </summary>
        public IReadOnlyList<RemediationStepResult> Steps { get; }

        /// <summary>
        /// Every warning from every step, flattened in step order. Each entry is prefixed with
        /// its originating step name in square brackets — <c>"[AutoTag] page 3 has no text"</c> —
        /// so the list stays meaningful when rendered on its own. Per-step, unprefixed warnings
        /// remain available through <see cref="Steps"/>. Never null.
        /// </summary>
        public IReadOnlyList<string> AllWarnings { get; }

        /// <summary>Length in bytes of the input document.</summary>
        public long InputLength { get; }

        /// <summary>Length in bytes of the remediated document written to the output stream.</summary>
        public long OutputLength { get; }

        /// <summary>Steps that executed.</summary>
        public IReadOnlyList<RemediationStepResult> AppliedSteps { get; }

        /// <summary>Steps that were deliberately not executed.</summary>
        public IReadOnlyList<RemediationStepResult> SkippedSteps { get; }

        /// <summary>Steps that were attempted and errored.</summary>
        public IReadOnlyList<RemediationStepResult> FailedSteps { get; }

        /// <summary>Sum of <see cref="RemediationStepResult.ItemsChanged"/> over all steps.</summary>
        public int TotalItemsChanged { get; }

        /// <summary>True when any step raised a warning.</summary>
        public bool HasWarnings => AllWarnings.Count > 0;

        /// <summary>True when any step failed. The output document is still written.</summary>
        public bool HasFailedSteps => FailedSteps.Count > 0;

        /// <summary>
        /// Reduction in Error-severity issues (before minus after). Negative if remediation
        /// introduced errors — which is worth surfacing, so it is deliberately not clamped.
        /// </summary>
        public int ErrorsFixed => BeforeAudit.ErrorCount - AfterAudit.ErrorCount;

        /// <summary>Reduction in Warning-severity issues (before minus after); may be negative.</summary>
        public int WarningsFixed => BeforeAudit.WarningCount - AfterAudit.WarningCount;

        /// <summary>
        /// True when the output has fewer errors than the input, or the same number of errors and
        /// fewer warnings.
        /// </summary>
        public bool IsImproved =>
            ErrorsFixed > 0 || (ErrorsFixed == 0 && WarningsFixed > 0);

        /// <summary>
        /// True when the remediated document has no <i>detected</i> Error-severity issue left.
        /// Carries every caveat documented on <see cref="AccessibilityAuditReport.IsConforming"/> —
        /// in particular it is not a PDF/UA-1 conformance claim.
        /// </summary>
        public bool IsConformingAfter => AfterAudit.IsConforming;

        /// <summary>
        /// Creates a remediation report.
        /// </summary>
        /// <param name="beforeAudit">Audit of the input document (required).</param>
        /// <param name="afterAudit">Audit of the output document (required).</param>
        /// <param name="steps">Steps in execution order; null becomes an empty list.</param>
        /// <param name="inputLength">Input length in bytes; negative values are clamped to 0.</param>
        /// <param name="outputLength">Output length in bytes; negative values are clamped to 0.</param>
        public RemediationReport(
            AccessibilityAuditReport beforeAudit,
            AccessibilityAuditReport afterAudit,
            IReadOnlyList<RemediationStepResult> steps,
            long inputLength,
            long outputLength)
        {
            BeforeAudit  = beforeAudit ?? throw new ArgumentNullException(nameof(beforeAudit));
            AfterAudit   = afterAudit  ?? throw new ArgumentNullException(nameof(afterAudit));
            Steps        = steps ?? Array.Empty<RemediationStepResult>();
            InputLength  = inputLength  > 0 ? inputLength  : 0;
            OutputLength = outputLength > 0 ? outputLength : 0;

            AppliedSteps = Steps.Where(s => s.Applied).ToList();
            SkippedSteps = Steps.Where(s => s.Skipped).ToList();
            FailedSteps  = Steps.Where(s => s.Failed).ToList();

            TotalItemsChanged = Steps.Sum(s => s.ItemsChanged);

            AllWarnings = Steps
                .SelectMany(s => s.Warnings.Select(w => "[" + s.Name + "] " + w))
                .ToList();
        }

        /// <summary>
        /// One-line summary in a stable, greppable format:
        /// <code>
        /// Remediation: 12 -> 3 error(s), 5 -> 2 warning(s); 5 step(s) applied, 2 skipped, 0 failed;
        /// 84 item(s) changed; 10240 -> 11890 bytes
        /// </code>
        /// </summary>
        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            return "Remediation: "
                 + BeforeAudit.ErrorCount.ToString(c)   + " -> " + AfterAudit.ErrorCount.ToString(c)   + " error(s), "
                 + BeforeAudit.WarningCount.ToString(c) + " -> " + AfterAudit.WarningCount.ToString(c) + " warning(s); "
                 + AppliedSteps.Count.ToString(c) + " step(s) applied, "
                 + SkippedSteps.Count.ToString(c) + " skipped, "
                 + FailedSteps.Count.ToString(c)  + " failed; "
                 + TotalItemsChanged.ToString(c)  + " item(s) changed; "
                 + InputLength.ToString(c) + " -> " + OutputLength.ToString(c) + " bytes";
        }

        /// <summary>
        /// Multi-line remediation log: the summary, every step with its warnings, and the issues
        /// still present in the remediated document (grouped document-level first, then by page).
        /// Suitable for a console, a log file, or the body of a UI report pane.
        /// </summary>
        public string ToDetailedString()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine(ToString());

            sb.AppendLine();
            sb.AppendLine("Steps:");
            if (Steps.Count == 0)
            {
                sb.AppendLine("  (none)");
            }
            else
            {
                foreach (var step in Steps)
                {
                    sb.Append("  ").AppendLine(step.ToString());
                    foreach (string warning in step.Warnings)
                        sb.Append("      ! ").AppendLine(warning);
                }
            }

            sb.AppendLine();
            sb.Append("Remaining issues (").Append(AfterAudit.ToString()).AppendLine("):");

            if (AfterAudit.Issues.Count == 0)
            {
                sb.AppendLine("  (none)");
                return sb.ToString();
            }

            foreach (var issue in AfterAudit.DocumentLevelIssues)
                sb.Append("  ").AppendLine(issue.ToString());

            foreach (int page in AfterAudit.PagesWithIssues)
            {
                sb.Append("  Page ").Append(page.ToString(CultureInfo.InvariantCulture)).AppendLine(":");
                foreach (var issue in AfterAudit.ByPage[page])
                    sb.Append("    ").AppendLine(issue.ToString());
            }

            return sb.ToString();
        }
    }
}
