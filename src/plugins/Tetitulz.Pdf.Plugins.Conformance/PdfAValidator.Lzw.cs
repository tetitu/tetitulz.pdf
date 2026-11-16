// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// A30 — no LZW compression, in a stream or an inline image, at every part. The published
        /// profiles quote the clauses:
        /// <list type="bullet">
        ///   <item>PDF/A-1: ISO 19005-1 §6.1.10, "The LZWDecode filter shall not be permitted"
        ///   (rules 6.1.10-1 for streams and 6.1.10-2 for inline images).</item>
        ///   <item>PDF/A-2 and -3: §6.1.7.2, "All standard stream filters … may be used, with the
        ///   exception of LZWDecode" (rule 6.1.7.2-1), and §6.1.10, "The value of the F key in the
        ///   Inline Image dictionary shall not be LZW, LZWDecode …" (rule 6.1.10-1).</item>
        ///   <item>PDF/A-4: ISO 19005-4 §6.1.6.2 (rule 6.1.6.2-1) and §6.1.9 (rule 6.1.9-1), the same
        ///   words against ISO 32000-2.</item>
        /// </list>
        /// <para>The reading is <see cref="LzwUse"/>'s, which PDF/X's X15 asks too: every stream the
        /// cross-reference table lists, and every inline image a page's content reaches.</para>
        /// <para>A31 rides the same two passes: the part-2-onwards rules also forbid a <c>Crypt</c>
        /// filter other than <c>Identity</c> and any filter outside ISO 32000's table, the answer
        /// being <see cref="PermittedFilters"/>'. PDF/A-1's §6.1.10 bans LZW alone.</para>
        /// </summary>
        private static void CheckLzw(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            bool checkOthers = part != PdfAConformancePart.Part1;
            string found = null, other = null;
            int unreadable = LzwUse.EachStream(doc, (number, stream) =>
            {
                if (found == null && LzwUse.NamesLzw(stream[PdfName.Filter]))
                    found = $"stream object {number}";
                if (checkOthers && other == null && PermittedFilters.FirstUnpermittedInStream(stream) is string name)
                    other = $"stream object {number} uses /{name}";
            });
            if (unreadable > 0)
                ReportPartial($"The A30 LZW check skipped {unreadable} object(s) the parser could not read.",
                              checkOthers ? new[] { "A30", "A31" } : new[] { "A30" });

            bool inline = false, otherInline = false;
            if (found == null || (checkOthers && other == null))
            {
                long documentBytes = 0;
                for (int i = 0; i < doc.Pages.Count && (found == null || (checkOthers && other == null)); i++)
                {
                    var scan = PdfXValidator.ScanPage(doc, doc.Pages[i],
                        PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                        PdfXValidator.DefaultMaxDocumentContentBytes);
                    if (scan.Incomplete != null)
                        ReportPartial($"Page {i + 1}: the A30 inline-image LZW check did not run in full — {scan.Incomplete}.",
                                      checkOthers ? new[] { "A30", "A31" } : new[] { "A30" });

                    foreach (byte[] content in scan.ContentStreams)
                    {
                        if (found == null && LzwUse.InlineImageUsesLzw(content))
                        {
                            found = $"an inline image on page {i + 1}";
                            inline = true;
                        }
                        if (checkOthers && other == null && PermittedFilters.FirstUnpermittedInInlineImage(content) is string name)
                        {
                            other = $"an inline image on page {i + 1} uses /{name}";
                            otherInline = true;
                        }
                    }
                }
            }

            if (found != null)
                v.Add(new PdfAViolation("A30",
                    $"PDF/A does not allow LZW compression; {found} uses it ({LzwClause(part, inline)})."));
            if (other != null)
                v.Add(new PdfAViolation("A31",
                    $"PDF/A allows only ISO 32000's standard filters, and Crypt only as Identity in a stream; {other} ({LzwClause(part, otherInline)})."));
        }

        /// <summary>The clause and rule for the stream or the inline-image half.</summary>
        private static string LzwClause(PdfAConformancePart part, bool inline)
        {
            switch (part)
            {
                case PdfAConformancePart.Part1:
                    return inline ? "ISO 19005-1 §6.1.10, rule 6.1.10-2" : "ISO 19005-1 §6.1.10, rule 6.1.10-1";
                case PdfAConformancePart.Part4:
                    return inline ? "ISO 19005-4 §6.1.9, rule 6.1.9-1" : "ISO 19005-4 §6.1.6.2, rule 6.1.6.2-1";
                default:
                    string iso = part == PdfAConformancePart.Part3 ? "ISO 19005-3" : "ISO 19005-2";
                    return inline ? iso + " §6.1.10, rule 6.1.10-1" : iso + " §6.1.7.2, rule 6.1.7.2-1";
            }
        }
    }
}
