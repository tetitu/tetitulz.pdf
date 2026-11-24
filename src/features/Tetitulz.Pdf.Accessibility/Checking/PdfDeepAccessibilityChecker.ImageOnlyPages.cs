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
        //  UA24 — Image-only pages
        // ================================================================

        /// <summary>
        /// Reports a page that draws images and no text — the signature of a scanned page whose
        /// content no screen reader, search or text selection can reach.
        ///
        /// <para>
        /// The text count includes text drawn inside Form XObjects (see
        /// <see cref="SummariseOperators"/>): counting page-level <c>/Contents</c> alone would
        /// accuse a perfectly good page of being a scan merely because its text sits in a form.
        /// When any part of the content could not be read
        /// (<see cref="PageInfo.FormXObjectScanIncomplete"/>) the page is skipped rather than
        /// accused — this finding names a document defect, so it must never rest on a count the
        /// checker knows is incomplete — <b>and</b> recorded through
        /// <see cref="NotePartialContentGap"/>, because a page dropped in silence produces a report
        /// indistinguishable from one for a page that was scanned in full and found clean.
        /// </para>
        /// </summary>
        private static void CheckImageOnlyPages(CheckContext ctx)
        {
            var scanned = new List<Occurrence>();

            foreach (PageInfo page in ctx.Pages)
            {
                if (ContentUnreadable(ctx, page, CheckNameUa24)) continue;
                if (page.HasText) continue;

                if (page.FormXObjectScanIncomplete)
                {
                    // The text this page draws may be entirely inside the form that would not read,
                    // so "no text" is not established and the page must not be accused. It must not
                    // be dropped in silence either: without this the report is byte-identical to one
                    // for a page that was scanned in full and found clean.
                    NotePartialContentGap(ctx, page, CheckNameUa24, checkRan: false);
                    continue;
                }
                if (!page.HasImage) continue;

                var occurrence = Occurrence.ForPage(page);
                occurrence.Detail = page.HasInlineImage && page.ImageXObjectInvocations == 0
                    ? "an inline image"
                    : page.ImageXObjectInvocations.ToString(CultureInfo.InvariantCulture) + " image XObject(s)";
                scanned.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA24", IssueCategory.Images, WcagLevel.A,
                null, "1.1.1", scanned,
                o => "Page draws " + o.Detail + " and no text at all — it is almost certainly a scanned " +
                     "image. Its content is unavailable to screen readers, search and text selection " +
                     "(WCAG 1.1.1; text rendered as an image also engages 1.4.5 Images of Text).",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) draw images and no text at all " +
                    "(e.g. " + objects + ") — almost certainly scanned pages whose content is unavailable to " +
                    "screen readers, search and text selection.",
                "Run OCR over the document to add a real text layer (this library ships an OCR plugin, " +
                "Tetitulz.Pdf.Plugins.Ocr), then tag the recovered text. If a page is genuinely decorative, " +
                "mark its content as an artifact instead.");
        }

        // ================================================================
        //  Occurrence model and emission
        // ================================================================

        /// <summary>
        /// One place a finding was observed. Carries everything
        /// <see cref="AccessibilityAuditReport.ByPage"/> and a remediation UI need in order to be
        /// actionable.
        /// </summary>
        private sealed class Occurrence
        {
            internal int? ObjectNumber;
            internal int? PageNumber;      // 1-based, per the report model
            internal string StructPath;
            internal string Detail;

            internal static Occurrence For(CheckContext ctx, StructNode node)
            {
                var occurrence = new Occurrence();
                if (node == null) return occurrence;

                if (node.ObjectNumber > 0) occurrence.ObjectNumber = node.ObjectNumber;
                if (node.PageIndex >= 0) occurrence.PageNumber = node.PageIndex + 1;

                string path = ctx.PathOf(node);
                if (!string.IsNullOrEmpty(path)) occurrence.StructPath = path;
                return occurrence;
            }

            internal static Occurrence ForPage(PageInfo page) => new Occurrence
            {
                PageNumber = page.PageNumber,
                ObjectNumber = page.ObjectNumber > 0 ? page.ObjectNumber : null,
            };

            internal static Occurrence ForAnnotation(AnnotationInfo annot) => new Occurrence
            {
                PageNumber = annot.PageNumber,
                ObjectNumber = annot.ObjectNumber > 0 ? annot.ObjectNumber : null,
            };

            internal static Occurrence ForFont(FontUse font) => new Occurrence
            {
                PageNumber = font.PageNumber > 0 ? font.PageNumber : null,
                ObjectNumber = font.ObjectNumber > 0 ? font.ObjectNumber : null,
            };
        }

        /// <summary>
        /// Emits a finding observed at zero or more places.
        ///
        /// <para>Up to <see cref="IndividualReportLimit"/> occurrences become one fully-located
        /// issue each. Beyond that they collapse into a single issue anchored at the first
        /// occurrence, carrying <see cref="AccessibilityIssue.InstanceCount"/> and naming the first
        /// few object numbers — so a document with ten thousand empty elements produces one entry,
        /// while <c>ErrorCount</c> and <c>TotalInstanceCount</c> stay meaningfully different.</para>
        /// </summary>
        private static void EmitOccurrences(
            CheckContext ctx,
            IssueSeverity severity,
            string code,
            IssueCategory category,
            WcagLevel wcagLevel,
            string matterhornCheckpoint,
            string wcagCriterion,
            IReadOnlyList<Occurrence> occurrences,
            Func<Occurrence, string> singularMessage,
            Func<int, string, string> aggregateMessage,
            string remediation)
        {
            if (occurrences == null || occurrences.Count == 0) return;

            if (occurrences.Count <= IndividualReportLimit)
            {
                foreach (Occurrence occurrence in occurrences)
                {
                    ctx.Issues.Add(new AccessibilityIssue(
                        severity, code, singularMessage(occurrence), remediation, category,
                        occurrence.PageNumber, wcagLevel, occurrence.ObjectNumber,
                        occurrence.StructPath, matterhornCheckpoint, wcagCriterion, null));
                }
                return;
            }

            Occurrence first = occurrences[0];

            ctx.Issues.Add(new AccessibilityIssue(
                severity, code,
                aggregateMessage(occurrences.Count, DescribeOccurrences(occurrences)),
                remediation, category,
                first.PageNumber, wcagLevel, first.ObjectNumber,
                first.StructPath, matterhornCheckpoint, wcagCriterion,
                occurrences.Count));
        }

        /// <summary>
        /// Renders the first few occurrences as "obj 12, obj 34, p.7" so an aggregated message
        /// still points somewhere concrete.
        /// </summary>
        private static string DescribeOccurrences(IReadOnlyList<Occurrence> occurrences)
        {
            var parts = new List<string>(ObjectNumbersInMessage);

            for (int i = 0; i < occurrences.Count && parts.Count < ObjectNumbersInMessage; i++)
            {
                Occurrence o = occurrences[i];
                if (o.ObjectNumber.HasValue)
                    parts.Add("obj " + o.ObjectNumber.Value.ToString(CultureInfo.InvariantCulture));
                else if (o.PageNumber.HasValue)
                    parts.Add("p." + o.PageNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (parts.Count == 0) return "no identifiable object or page";

            string joined = string.Join(", ", parts);
            return occurrences.Count > parts.Count ? joined + ", …" : joined;
        }

        // ================================================================
        //  Small helpers
        // ================================================================

        private static AccessibilityIssue Issue(
            IssueSeverity severity, string code, string message, string remediation,
            IssueCategory category, WcagLevel wcagLevel = WcagLevel.None)
            => new AccessibilityIssue(severity, code, message, remediation, category, null, wcagLevel);

        private static AccessibilityIssue Locate(CheckContext ctx, AccessibilityIssue issue, StructNode node)
        {
            if (node == null) return issue;

            if (node.ObjectNumber > 0)
                issue = issue.AtObject(node.ObjectNumber, node.PageIndex >= 0 ? node.PageIndex + 1 : null);
            else if (node.PageIndex >= 0)
                issue = issue.AtPage(node.PageIndex + 1);

            string path = ctx.PathOf(node);
            return string.IsNullOrEmpty(path) ? issue : issue.InStruct(path);
        }

        /// <summary>
        /// The object <paramref name="obj"/> names, falling back to <paramref name="obj"/> itself
        /// when the reference resolves to nothing.
        /// </summary>
        /// <remarks>
        /// The resolve is <c>Internal.Resolve</c>'s — the one implementation. The
        /// <c>?? obj</c> is <b>not</b>, and is kept deliberately: a dangling reference here should
        /// leave the raw value for the caller to inspect rather than vanish, because this checker's
        /// job is to describe what a document contains, including the parts of it that are broken.
        /// The shared helper returns null in that case, which is the right default for a caller
        /// that is going to act on the value.
        /// </remarks>
        private static PdfObject ResolveObject(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            return Internal.Resolve.Value(doc, obj, "PdfDeepAccessibilityChecker.ResolveObject") ?? obj;
        }

        private static PdfDictionary ResolveDictionary(PdfDocument doc, PdfObject obj)
            => ResolveObject(doc, obj) as PdfDictionary ?? obj as PdfDictionary;

        /// <summary>
        /// The <see cref="PdfName"/> value of <paramref name="key"/>, <b>resolved</b>, or null.
        /// </summary>
        /// <remarks>
        /// Any value in a PDF dictionary may be indirect (§7.3.10), and <see cref="PdfDictionary"/>'s
        /// indexer does not follow a reference — so <c>dict[key] as PdfName</c> yields null for a
        /// name written as <c>n 0 R</c>, indistinguishable from an absent key. Every partial in this
        /// folder resolves before casting — <c>/Subtype</c>, <c>/BaseFont</c> and <c>/BaseEncoding</c>
        /// included. Read bare, the consequences run both ways: a Type0 font with an indirect
        /// <c>/Subtype</c> would never take the Type0 branch, so <c>/FontDescriptor</c> would be read
        /// off the Type0 dictionary (which correctly has none) and a properly-embedded CIDFont reported
        /// as UA21 — while a Form XObject with an indirect <c>/Subtype</c> would not be recognised as a
        /// Form, so the fonts used only inside it would never be collected and UA20/UA21 would
        /// silently not run for them.
        ///
        /// <para>It exists as a named helper rather than an inline <c>ResolveObject(...) as
        /// PdfName</c> so the omission is visible if it recurs.</para>
        /// </remarks>
        private static string ResolvedName(CheckContext ctx, PdfDictionary dict, PdfName key)
            => dict == null ? null : (ResolveObject(ctx.Doc, dict[key]) as PdfName)?.Value;

        private static int? GetInteger(CheckContext ctx, PdfObject obj)
        {
            if (obj is PdfReference) obj = ResolveObject(ctx.Doc, obj);
            return obj is PdfNumber n ? n.IntValue : null;
        }

        private static int GetObjectNumber(CheckContext ctx, PdfObject raw, PdfDictionary resolved)
        {
            if (raw is PdfReference reference) return reference.ReferenceObjectNumber;
            if (ctx.Index != null && resolved != null)
            {
                int number = ctx.Index.GetObjectNumber(resolved);
                if (number > 0) return number;
            }
            return resolved != null && resolved.ObjectNumber > 0 ? resolved.ObjectNumber : -1;
        }

        private static string JoinCapped(IReadOnlyList<string> values)
        {
            const int max = 10;
            if (values.Count <= max) return string.Join(", ", values);

            var head = new string[max];
            for (int i = 0; i < max; i++) head[i] = values[i];
            return string.Join(", ", head) + ", … (" +
                   (values.Count - max).ToString(CultureInfo.InvariantCulture) + " more)";
        }
    }
}
