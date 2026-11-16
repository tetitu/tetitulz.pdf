// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Plugins.PageEdits;

namespace Tetitulz.Pdf.Splitter
{
    /// <summary>How the pages are divided into output files.</summary>
    internal enum SplitMode
    {
        /// <summary>The selected pages, into one file.</summary>
        Selection,
        /// <summary>Each comma-separated part of the range box, into a file of its own.</summary>
        PerGroup,
        /// <summary>The whole document, every N pages a file.</summary>
        EveryNPages,
        /// <summary>The whole document, in files each at most N megabytes.</summary>
        UnderMegabytes,
    }

    /// <summary>What writing one output file did, gathered on the thread that did it.</summary>
    internal sealed class SplitOutcome
    {
        public string Error;
        public readonly List<string> Warnings = new List<string>();

        /// <summary>True when the file was written under the source document's own protection.</summary>
        public bool Protected;
    }

    /// <summary>
    /// The splitter's work, apart from its window: which pages go into which file, and writing each
    /// file — under the source's own password when the source is encrypted.
    /// </summary>
    /// <remarks>
    /// An extract of an encrypted document is protected as the document is: it is extracted from
    /// the decrypted pages and encrypted again by <see cref="PdfEncryptedRoundTrip"/>, under the
    /// original's own key and permissions where that can be kept (<see cref="ReEncryptionPolicy.KeepOriginal"/>)
    /// and otherwise under the password that opened it. Nothing is written in the clear.
    /// </remarks>
    internal static class SplitJob
    {
        private const long BytesPerMegabyte = 1024L * 1024L;

        /// <summary>
        /// The page lists, 1-based, one per output file. <paramref name="selection"/> and
        /// <paramref name="groups"/> are the range box's; <paramref name="n"/> is the pages per file or
        /// the megabytes per file. <paramref name="password"/> is null for a document that is not
        /// encrypted. <paramref name="warning"/> is the planner's own note, or null.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<int>> Plan(byte[] bytes, string password, SplitMode mode,
            IReadOnlyList<int> selection, IReadOnlyList<IReadOnlyList<int>> groups, int n, out string warning)
        {
            warning = null;
            switch (mode)
            {
                case SplitMode.Selection:
                    return selection.Count == 0 ? new IReadOnlyList<int>[0] : new[] { selection };
                case SplitMode.PerGroup:
                    return groups;
                case SplitMode.EveryNPages:
                case SplitMode.UnderMegabytes:
                    if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "The number per file must be at least 1.");
                    using (var plain = Plaintext(bytes, password))
                    {
                        IReadOnlyList<IReadOnlyList<int>> plan = mode == SplitMode.EveryNPages
                            ? PdfSplitPlanner.PlanEveryNPages(plain, n)
                            : PdfSplitPlanner.PlanUnderByteCeiling(plain, n * BytesPerMegabyte);
                        warning = PdfSplitPlanner.LastPlanWarning;
                        return plan;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        /// <summary>The document's readable bytes: itself, or decrypted with <paramref name="password"/>.</summary>
        private static MemoryStream Plaintext(byte[] bytes, string password)
        {
            if (password == null) return new MemoryStream(bytes, false);
            var plain = new MemoryStream();
            using (var input = new MemoryStream(bytes, false))
                PdfDecryptor.Decrypt(input, plain, password);
            plain.Position = 0;
            return plain;
        }

        /// <summary>
        /// Writes <paramref name="pages"/> (1-based, in that order) of the document into
        /// <paramref name="output"/>. The stream is written only when the extract succeeds.
        /// </summary>
        /// <remarks>
        /// Reads the splitter's diagnostics on the thread that extracted: every
        /// <c>PdfSplitter.Last*Warning</c> is per thread, so reading them anywhere else would report
        /// "no warnings" for a document that lost its outlines.
        /// </remarks>
        public static SplitOutcome Write(byte[] bytes, string password, IReadOnlyList<int> pages, Stream output)
        {
            var outcome = new SplitOutcome();
            try
            {
                if (password == null)
                {
                    using (var input = new MemoryStream(bytes, false))
                    using (var produced = new MemoryStream())
                    {
                        PdfSplitter.ExtractPages(input, produced, pages);
                        CollectWarnings(outcome);
                        produced.Position = 0;
                        produced.CopyTo(output);
                    }
                    return outcome;
                }

                string refusal = null;
                foreach (ReEncryptionPolicy policy in new[] { ReEncryptionPolicy.KeepOriginal, ReEncryptionPolicy.ReuseOpenPassword })
                {
                    outcome.Warnings.Clear();
                    using (var input = new MemoryStream(bytes, false))
                    {
                        RoundTripResult result = PdfEncryptedRoundTrip.Run(input, output,
                            new EncryptedRoundTripOptions
                            {
                                OpenPassword = password,
                                Policy = policy,
                                // An extract is a new document: the source's signatures cover the
                                // source, never a part of it, so there is none to keep.
                                AllowSignatureInvalidation = true,
                            },
                            (plain, produced) =>
                            {
                                PdfSplitter.ExtractPages(plain, produced, pages);
                                CollectWarnings(outcome);
                            });
                        if (result.Succeeded)
                        {
                            outcome.Protected = true;
                            if (result.OwnerPasswordChanged)
                                outcome.Warnings.Add("The extract opens with the document's password; its owner password could not be kept, so its permissions are the default ones.");
                            foreach (string w in result.Warnings) outcome.Warnings.Add(w);
                            return outcome;
                        }
                        refusal = refusal ?? result.FailureReason;
                    }
                }
                outcome.Error = "The extract could not be protected as the document is, so nothing was written: " + refusal;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
            }
            return outcome;
        }

        private static void CollectWarnings(SplitOutcome outcome)
        {
            // Reported, not swallowed: an extract drops catalog entries that describe pages which
            // are no longer there (/PageLabels, /StructTreeRoot), and silence about that misleads.
            Add(outcome, PdfSplitter.LastCatalogWarning);
            Add(outcome, PdfSplitter.LastNameTreeWarning);
            Add(outcome, PdfSplitter.LastCloneWarning);
            Add(outcome, PdfSplitter.LastFormWarning);
            Add(outcome, PdfSplitter.LastXfaWarning);
        }

        private static void Add(SplitOutcome outcome, string warning)
        {
            if (!string.IsNullOrEmpty(warning)) outcome.Warnings.Add(warning);
        }

        /// <summary>
        /// A file name for <paramref name="pages"/> of the document named <paramref name="stem"/>:
        /// <c>stem_pages_1-3, 7.pdf</c>, the range cut at 40 characters and anything Windows refuses
        /// in a name replaced.
        /// </summary>
        public static string FileNameFor(string stem, IEnumerable<int> pages)
        {
            string range = PageRangeText.Format(pages);
            // Windows caps a path component at 255; a long list of single pages would reach it.
            if (range.Length > 40) range = range.Substring(0, 40) + "-etc";

            string name = (string.IsNullOrEmpty(stem) ? "extract" : stem) + "_pages_" + range + ".pdf";
            var cleaned = new StringBuilder(name.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
                cleaned.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return cleaned.ToString();
        }

        /// <summary>
        /// The file names for several parts beside <paramref name="target"/>: its name with
        /// <c>_part01</c>, <c>_part02</c>… and each part's pages.
        /// </summary>
        public static string[] PartPaths(string target, IReadOnlyList<IReadOnlyList<int>> chunks)
        {
            string dir = Path.GetDirectoryName(target) ?? "";
            string stem = Path.GetFileNameWithoutExtension(target);
            int digits = Math.Max(2, chunks.Count.ToString(CultureInfo.InvariantCulture).Length);
            var paths = new string[chunks.Count];
            for (int i = 0; i < chunks.Count; i++)
            {
                string part = stem + "_part" + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
                paths[i] = Path.Combine(dir, FileNameFor(part, chunks[i]));
            }
            return paths;
        }
    }
}
