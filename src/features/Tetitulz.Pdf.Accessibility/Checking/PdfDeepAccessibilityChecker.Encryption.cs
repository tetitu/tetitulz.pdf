// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA06 — the security handler must permit accessibility extraction
        // ================================================================

        /// <summary>
        /// Reports UA06 when an encrypted document's security handler does not — or cannot be
        /// shown to — permit content extraction for accessibility. Returns whether the document is
        /// encrypted at all, which is a different question and is what the rest of the audit needs.
        ///
        /// <para><b>This is deliberately the same rule, in the same order, with the same outcomes
        /// as <c>PdfUaChecker.CheckEncryption</c> in the Conformance plugin</b> — and the one step
        /// that can disagree, <i>what number <c>/P</c> denotes</i>, is written once: both halves
        /// read it through <see cref="PdfPermissionValue.FromNumber"/> (a <c>PdfNumber.IntValue</c>
        /// read would disagree with the plugin on every document whose <c>/P</c> uses the unsigned
        /// spelling). The two run over
        /// the same bytes and their findings are merged into one report by
        /// <c>PdfAccessibility.Audit</c>, so a second, independently-reasoned interpretation of
        /// /P bit 10 would produce a report that contradicts itself about a single document. If
        /// this method and that one ever have to change, they change together. Both halves
        /// therefore <i>produce</i> a UA06 when the permission is denied — this checker is a public
        /// API in its own right and cannot assume the plugin ran — so neither says "encrypted, cannot
        /// check" beside the other's real findings.</para>
        ///
        /// <para><b>The pair is collapsed by <c>PdfAccessibility.AuditBytes</c>, which keeps this
        /// one.</b> Its collapse is conditional — it drops the plugin's copy only where this method
        /// actually produced an entry — because an unconditional rule (a
        /// <c>SupersededByDeepCheck</c> entry, or one keyed on <c>doc.IsEncrypted</c>) would delete
        /// the report's only UA06 on any run where this half did not reach its verdict, turning a
        /// double-count into a lost finding. This entry is the one kept because it carries the WCAG
        /// criterion and a remediation naming the permission to grant; the mapped plugin violation
        /// carries neither. <b>Calling this checker standalone still yields its own UA06</b> —
        /// the collapse belongs to the facade that merged the two reports, never here.</para>
        ///
        /// <para>Encryption on its own is <b>not</b> reported. ISO 14289-1 §7.1 requires that
        /// assistive technology be able to extract the content; it does not forbid encrypting the
        /// file. One UA06 Error for any encrypted document, with the permission bits never read and
        /// every other check abandoned, would fail a conformant file and give a non-conformant one
        /// half an audit.</para>
        /// </summary>
        private static bool CheckEncryption(PdfDocument doc, List<AccessibilityIssue> issues)
        {
            if (!doc.IsEncrypted) return false;

            const string remediation =
                "Grant the accessibility-extraction permission when encrypting (ISO 32000-1 Table 22, " +
                "/P bit 10, value 512, standard security handler), or publish a decrypted copy. " +
                "PDF/UA-1 does not forbid encryption — it forbids withholding the content from " +
                "assistive technology.";

            PdfDictionary enc = doc.EncryptionDictionary;
            if (enc == null)
            {
                issues.Add(Issue(IssueSeverity.Error, "UA06",
                    "The document is encrypted but its encryption dictionary could not be read, so it " +
                    "cannot be established that the security handler permits content extraction for " +
                    "accessibility (ISO 32000-1 Table 22, /P bit 10).",
                    remediation, IssueCategory.Tagging, WcagLevel.A)
                    .MappedTo(null, "1.3.1"));
                return true;
            }

            string filter = (ResolveObject(doc, enc[PdfName.Filter]) as PdfName)?.Value;
            if (filter != null && !string.Equals(filter, "Standard", StringComparison.Ordinal))
            {
                issues.Add(Issue(IssueSeverity.Error, "UA06",
                    "The document uses the non-standard security handler /" + filter + ". PDF/UA-1 " +
                    "requires that content extraction for accessibility be permitted, which cannot be " +
                    "verified outside the standard handler's /P bits.",
                    remediation, IssueCategory.Tagging, WcagLevel.A)
                    .MappedTo(null, "1.3.1"));
                return true;
            }

            int revision = (ResolveObject(doc, enc[PdfName.R]) as PdfNumber)?.IntValue ?? 0;

            // Revision 2: permission bits 7–12 are reserved and shall be 1, so accessibility
            // extraction is always permitted.
            // Revision 6 (ISO 32000-2 / AES-256): bit 10 is deprecated and shall be treated as if
            // it were set. Neither case is a finding.
            if (revision <= 2 || revision >= 6) return true;

            // PdfPermissionValue, not PdfNumber.IntValue: /P is a signed 32-bit bitmask that
            // producers write in its unsigned spelling (/P 4294963392 for -3904), and IntValue is a
            // double -> int cast of an out-of-range value, which SATURATES to int.MaxValue on
            // .NET Core 3.0+. Bit 10 then reads as set for every such document, so a file that
            // withholds its content from assistive technology audited clean — a false pass, and the
            // opposite verdict from PdfUaChecker over the same bytes. PdfPermissionValue's own
            // remarks name PDF/UA UA06 as one of the four defects it was created to close; this was
            // the copy that never moved onto it.
            int? p = PdfPermissionValue.FromNumber(ResolveObject(doc, enc[NamePermissions]));
            if (p == null)
            {
                issues.Add(Issue(IssueSeverity.Error, "UA06",
                    "The encryption dictionary has no /P permissions entry, so it cannot be established " +
                    "that content extraction for accessibility is permitted (ISO 32000-1 Table 22, " +
                    "/P bit 10).",
                    remediation, IssueCategory.Tagging, WcagLevel.A)
                    .MappedTo(null, "1.3.1"));
                return true;
            }

            if ((p.Value & PermissionExtractForAccessibility) == 0)
            {
                issues.Add(Issue(IssueSeverity.Error, "UA06",
                    "The document's security handler denies content extraction for accessibility (/P " +
                    "bit 10 is clear; /P = " + p.Value.ToString(CultureInfo.InvariantCulture) +
                    ", /R = " + revision.ToString(CultureInfo.InvariantCulture) + "). PDF/UA-1 requires " +
                    "assistive technology to be able to extract the document's content.",
                    remediation, IssueCategory.Tagging, WcagLevel.A)
                    .MappedTo(null, "1.3.1"));
            }

            return true;
        }

        /// <summary>
        /// The document catalog, resolved directly from the trailer when the parser did not load
        /// one.
        ///
        /// <para><c>PdfDocument.Load</c> deliberately skips catalog, metadata and page
        /// loading for an encrypted document, because objects inside object streams cannot be
        /// decoded without the key — so <c>doc.Catalog</c> is null and the old
        /// <c>doc.Catalog?.Dictionary</c> would have reported a perfectly well-formed encrypted
        /// file as having no catalog at all. The catalog is virtually always a top-level object,
        /// and its structure is not ciphertext, so resolving <c>/Root</c> by hand recovers it and
        /// the audit proceeds. Same fallback, same reason, as
        /// <c>PdfUaChecker.ResolveCatalog</c>.</para>
        /// </summary>
        private static PdfDictionary ResolveCatalog(PdfDocument doc)
        {
            PdfDictionary catalog = doc.Catalog?.Dictionary;
            if (catalog != null) return catalog;

            try
            {
                return ResolveObject(doc, doc.Trailer?[PdfName.Root]) as PdfDictionary;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.ResolveCatalog",
                    "the trailer's /Root could not be resolved; the document has no reachable catalog", ex);
                return null;
            }
        }

        /// <summary>
        /// States, once and exactly, what the audit of an encrypted document could not establish.
        ///
        /// <para>The alternative to this issue is what the encryption handling exists to prevent: a report that is quietly missing several checks and looks identical to a report
        /// in which those checks ran and found nothing. Everything listed here was recorded at the
        /// moment it was actually skipped or weakened — the list is derived from what happened to
        /// <i>this</i> document, never from a fixed catalogue of what encryption usually
        /// costs.</para>
        /// </summary>
        private static void ReportEncryptionCoverage(CheckContext ctx)
        {
            if (!ctx.Encrypted) return;

            var sb = new StringBuilder(700);
            sb.Append("This report covers an encrypted document. Encryption is not itself a PDF/UA-1 ")
              .Append("failure and the audit was not abandoned: dictionary structure is never ciphertext, ")
              .Append("so the structural checks — element roles and /RoleMap resolution, /K linkage, ")
              .Append("empty and hollow subtrees, /ParentTree and /StructParents coverage, list and table ")
              .Append("nesting and regularity, /Suspects, font embedding and annotation ")
              .Append("reachability — ran in full and their findings are real. ");

            if (ctx.EncryptionLimitations.Count == 0)
            {
                sb.Append("Nothing else was skipped.");
            }
            else
            {
                sb.Append("This checker does not decrypt (the Accessibility feature does not reference ")
                  .Append("Tetitulz.Pdf.Encryption), so the following ")
                  .Append(ctx.EncryptionLimitations.Count.ToString(CultureInfo.InvariantCulture))
                  .Append(" item(s) are missing from it: ");

                for (int i = 0; i < ctx.EncryptionLimitations.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append('(').Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(") ")
                      .Append(ctx.EncryptionLimitations[i]).Append('.');
                }
            }

            ctx.Issues.Add(Issue(IssueSeverity.Info, "UA99", sb.ToString(),
                "Run this audit again over a decrypted copy to obtain the content-level findings. " +
                "Until then read a clean report as 'no structural defects found', not as 'no defects " +
                "found' — the checks named above reported nothing because they did not run.",
                IssueCategory.Tagging));
        }

        // ================================================================
        //  Content-coverage gaps — the unencrypted counterpart of the above
        // ================================================================

        //  Names used in the report for the four checks that need a readable content stream.
        //  They are constants rather than literals at the call sites so the same page never
        //  collects two spellings of the same check.

        private const string CheckNameUa14Mapping =
            "UA14's marked-content mapping (whether every MCID a structure element names really " +
            "exists on the page, and whether the /ParentTree leads back to that element)";

        private const string CheckNameUa17 =
            "UA17 (content drawn outside every marked-content sequence)";

        private const string CheckNameUa18 =
            "UA18 (a page holding structural content but no /StructParents)";

        private const string CheckNameUa24 =
            "UA24 (image-only, probably scanned pages)";

        /// <summary>
        /// One page whose content stream could not be read, and the checks that were consequently
        /// not attempted on it.
        /// </summary>
        private sealed class ContentCoverageGap
        {
            internal int PageNumber;
            internal int ObjectNumber = -1;
            internal string Reason;
            internal readonly List<string> ChecksSkipped = new List<string>(4);

            /// <summary>
            /// Checks that <em>did</em> run for this page but on input the checker knows is short,
            /// so what they reported is a lower bound. Only ever populated for a partial gap (see
            /// <see cref="NotePartialContentGap"/>); a page whose content stream would not decode
            /// at all has nothing in this list, because no check ran on it.
            /// </summary>
            internal readonly List<string> ChecksOnPartialInput = new List<string>(2);
        }

        /// <summary>
        /// The gate every check that needs decoded page content applies, and the only place that
        /// gate is written.
        ///
        /// <para>
        /// Returns <c>true</c> when the page must be skipped, recording <i>why</i> and <i>which
        /// check</i> as it does so. <see cref="RunContentCheck"/> already establishes the rule for
        /// the encrypted case — "a check that neither ran nor said so is indistinguishable from a
        /// check that ran and found nothing" — but the rule is not about encryption. A stray
        /// <c>)</c> delimiter, a PostScript calculator brace, an inline image of indeterminate
        /// length or a corrupt Flate payload leaves <see cref="PageInfo.ContentUsable"/> false on a
        /// document with no <c>/Encrypt</c> at all, and an <see cref="AccessibilityDiagnostics"/> event
        /// alone is not part of the report: without this a hollow page would produce a report
        /// byte-identical to a clean one.
        /// </para>
        /// </summary>
        private static bool ContentUnreadable(CheckContext ctx, PageInfo page, string checkName)
        {
            if (page == null || page.ContentUsable) return false;
            NoteContentCoverageGap(ctx, page, checkName);
            return true;
        }

        /// <summary>
        /// Records, once per page, that a check could not be attempted because the page's content
        /// could not be read.
        ///
        /// <para>Encrypted documents are deliberately excluded: every page of one is unreadable by
        /// construction, and <see cref="ReportEncryptionCoverage"/> already names the same checks
        /// with the same precision. Saying it twice in two shapes would add no fact and make the
        /// report harder to read.</para>
        /// </summary>
        private static void NoteContentCoverageGap(CheckContext ctx, PageInfo page, string checkName)
        {
            if (ctx.Encrypted || page == null) return;

            if (!ctx.ContentCoverageGapsByPage.TryGetValue(page.PageNumber, out ContentCoverageGap gap))
            {
                gap = new ContentCoverageGap
                {
                    PageNumber   = page.PageNumber,
                    ObjectNumber = page.ObjectNumber,
                    Reason       = FirstNonEmpty(page.ContentError, page.Scan?.Error)
                                   ?? "its content stream could not be read",
                };
                ctx.ContentCoverageGapsByPage[page.PageNumber] = gap;
                ctx.ContentCoverageGaps.Add(gap);
            }

            if (!gap.ChecksSkipped.Contains(checkName)) gap.ChecksSkipped.Add(checkName);
        }

        private static string FirstNonEmpty(string a, string b)
            => !string.IsNullOrEmpty(a) ? a : (!string.IsNullOrEmpty(b) ? b : null);

        /// <summary>
        /// Records that a page was scanned but <b>not in full</b>, and what that cost.
        ///
        /// <para>
        /// <b>Why <see cref="NoteContentCoverageGap"/> cannot do this.</b> That path is gated on
        /// <see cref="PageInfo.ContentUsable"/>, which is
        /// <c>ContentError == null &amp;&amp; Scan != null &amp;&amp; Scan.IsSafe</c> — all three
        /// facts about the page's own <c>/Contents</c>. A page whose <c>/Contents</c> decoded
        /// perfectly but whose Form XObject would not resolve, would not decode, would not scan
        /// safely or fell outside the audit's form budget has <c>ContentUsable == true</c>, so
        /// <see cref="ReportContentCoverage"/> emits nothing for it. Its counts are nevertheless
        /// lower bounds: <see cref="CheckImageOnlyPages"/> and
        /// <see cref="CheckMarkedContentMapping"/> skip such a page outright, and
        /// <see cref="CheckUnmarkedPageContent"/> / <see cref="CheckPageStructParents"/> report from
        /// numbers they know are short. An <see cref="AccessibilityDiagnostics"/> event alone is not
        /// part of the report — so without this the audit of a page it never finished looking at
        /// would be byte-identical to that of a page it scanned in full and found clean, and could
        /// return
        /// <see cref="AccessibilityAuditReport.IsConforming"/> on the strength of it.
        /// </para>
        /// </summary>
        /// <param name="checkRan">
        /// <c>false</c> when the caller skipped the page entirely; <c>true</c> when it reported from
        /// the partial counts. The two are said differently in the report because they are different
        /// facts — "nothing was established" versus "what was reported is a lower bound".
        /// </param>
        /// <param name="ctx">Per-check state: the document, the options and the findings list.</param>
        /// <param name="page">The page whose scan was incomplete.</param>
        /// <param name="checkName">The check that could not be run in full, named in the notice.</param>
        private static void NotePartialContentGap(
            CheckContext ctx, PageInfo page, string checkName, bool checkRan)
        {
            // On an encrypted document no page is scanned at all and ReportEncryptionCoverage is
            // the authority; saying it again in a second shape would add no fact.
            if (ctx.Encrypted || page == null) return;

            if (!ctx.PartialContentGapsByPage.TryGetValue(page.PageNumber, out ContentCoverageGap gap))
            {
                gap = new ContentCoverageGap
                {
                    PageNumber   = page.PageNumber,
                    ObjectNumber = page.ObjectNumber,
                    Reason       = DescribePartialScan(page),
                };
                ctx.PartialContentGapsByPage[page.PageNumber] = gap;
                ctx.PartialContentGaps.Add(gap);
            }

            List<string> target = checkRan ? gap.ChecksOnPartialInput : gap.ChecksSkipped;
            if (!target.Contains(checkName)) target.Add(checkName);
        }

        /// <summary>
        /// Names, in the page's own terms, which half of the scan came up short. Both flags can be
        /// set at once, and the two repairs are different, so neither is folded into the other.
        /// </summary>
        private static string DescribePartialScan(PageInfo page)
        {
            if (page.FormXObjectScanIncomplete && page.MarkedContentIdsIncomplete)
                return "one or more Form XObjects it invokes could not be read, and at least one " +
                       "marked-content sequence names a property list that is not in /Properties";

            if (page.FormXObjectScanIncomplete)
                return "one or more Form XObjects it invokes could not be read, so the operators drawn " +
                       "inside them were not counted";

            if (page.MarkedContentIdsIncomplete)
                return "at least one marked-content sequence names a property list that is not in " +
                       "/Properties, so its /MCID could not be read";

            return "part of its content could not be read";
        }

        /// <summary>
        /// The partially-scanned counterpart of <see cref="ReportContentCoverage"/>, emitted for the
        /// same reason and at the same severity: <b>Info</b>, because the document has not been shown
        /// to be wrong — only not fully examined — so this must not flip
        /// <see cref="AccessibilityAuditReport.IsConforming"/> on its own. What it must do is stop a
        /// silent page from reading as a clean one.
        /// </summary>
        private static void ReportPartialContentCoverage(CheckContext ctx)
        {
            if (ctx.PartialContentGaps.Count == 0) return;

            var occurrences = new List<Occurrence>(ctx.PartialContentGaps.Count);
            foreach (ContentCoverageGap gap in ctx.PartialContentGaps)
            {
                var detail = new StringBuilder(gap.Reason);

                if (gap.ChecksSkipped.Count > 0)
                    detail.Append(" — so the following did not run for this page: ")
                          .Append(JoinCapped(gap.ChecksSkipped));

                if (gap.ChecksOnPartialInput.Count > 0)
                    detail.Append(gap.ChecksSkipped.Count > 0 ? "; and the following " : " — so the following ")
                          .Append("reported from counts that are lower bounds: ")
                          .Append(JoinCapped(gap.ChecksOnPartialInput));

                occurrences.Add(new Occurrence
                {
                    PageNumber   = gap.PageNumber,
                    ObjectNumber = gap.ObjectNumber > 0 ? gap.ObjectNumber : (int?)null,
                    Detail       = detail.ToString(),
                });
            }

            EmitOccurrences(ctx, IssueSeverity.Info, "UA99", IssueCategory.Tagging, WcagLevel.None,
                null, null, occurrences,
                o => "This page was scanned only in part (" + o.Detail +
                     "). A clean result for this page therefore means 'not established', not " +
                     "'nothing wrong'.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) were scanned only in part " +
                    "(e.g. " + objects + ") because content they invoke could not be read, so the " +
                    "content-level checks — " + CheckNameUa14Mapping + ", " + CheckNameUa17 + ", " +
                    CheckNameUa18 + " and " + CheckNameUa24 + " — either did not run for them or " +
                    "reported from counts that are lower bounds.",
                "Repair the Form XObjects and named /Properties entries those pages reference (for example " +
                "with the Maintenance plugin's sanitizer) and re-run the audit; treat this report as " +
                "incomplete for the pages named until they scan cleanly.");
        }

        /// <summary>
        /// States which pages the content-level checks could not be run on, and what was therefore
        /// not established about them. The unencrypted counterpart of
        /// <see cref="ReportEncryptionCoverage"/>, and emitted for exactly the same reason.
        ///
        /// <para>Severity is <b>Info</b>, matching the encryption notice: the document has not been
        /// shown to be wrong, so this must not flip
        /// <see cref="AccessibilityAuditReport.IsConforming"/> on its own. What it must do — and
        /// what nothing did before — is stop a clean report from silently meaning "these checks did
        /// not run".</para>
        /// </summary>
        private static void ReportContentCoverage(CheckContext ctx)
        {
            if (ctx.ContentCoverageGaps.Count == 0) return;

            var occurrences = new List<Occurrence>(ctx.ContentCoverageGaps.Count);
            foreach (ContentCoverageGap gap in ctx.ContentCoverageGaps)
            {
                occurrences.Add(new Occurrence
                {
                    PageNumber   = gap.PageNumber,
                    ObjectNumber = gap.ObjectNumber > 0 ? gap.ObjectNumber : (int?)null,
                    Detail       = gap.Reason + " — so the following did not run for this page: " +
                                   JoinCapped(gap.ChecksSkipped),
                });
            }

            EmitOccurrences(ctx, IssueSeverity.Info, "UA99", IssueCategory.Tagging, WcagLevel.None,
                null, null, occurrences,
                o => "This page's content stream could not be read (" + o.Detail +
                     "). Whatever those checks would have found is missing from this report, so a " +
                     "clean result for this page means 'not established', not 'nothing wrong'.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) whose content stream could " +
                    "not be read (e.g. " + objects + "), so the content-level checks — " +
                    CheckNameUa14Mapping + ", " + CheckNameUa17 + ", " + CheckNameUa18 + " and " +
                    CheckNameUa24 + " — did not run for them. Whatever those checks would have found " +
                    "is missing from this report.",
                "Repair the page content streams (for example with the Maintenance plugin's sanitizer) " +
                "and re-run the audit; treat this report as incomplete for the pages named until the " +
                "content-level checks run cleanly.");
        }
    }
}
