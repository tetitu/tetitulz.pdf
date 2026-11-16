// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Resilient wrapper around <see cref="PdfDocument.Open(Stream)"/> that falls
    /// back to <see cref="PdfRepair"/> when the stream cannot be parsed as-is.
    /// </summary>
    public static class PdfDocumentOpener
    {
        /// <summary>
        /// Opens the stream; on parse failure, runs <see cref="PdfRepair.Repair(Stream,Stream)"/>
        /// and retries once. Throws the original parse exception if repair also fails.
        /// </summary>
        [ThreadStatic]
        private static string _lastRepairOutcome;

        /// <summary>
        /// Describes the repair the most recent <see cref="OpenWithRepair"/> call <b>on this
        /// thread</b> performed, or <c>null</c> when the document opened healthy or could not be
        /// improved.
        /// </summary>
        /// <remarks>
        /// A recovered document is not the document the caller was given, and silently substituting
        /// one is exactly the kind of "confidently wrong" behaviour this library avoids elsewhere.
        /// Read it on the thread that made the call (see the Diagnostics Pattern in CLAUDE.md).
        /// </remarks>
        public static string LastRepairOutcome
        {
            get => _lastRepairOutcome;
            private set => _lastRepairOutcome = value;
        }

        public static PdfDocument OpenWithRepair(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Cleared at the start, on every path: a caller reads this AFTER the call, so a stale
            // value from a previous document would answer the wrong question.
            LastRepairOutcome = null;

            // Buffer once so we can retry after a failed parse. Rewind first, the way every other
            // stream entry point under Operations/ does: copying from wherever the caller left the
            // position buffers only the tail of the file, so a healthy PDF whose header the caller
            // had already sniffed arrived here headerless, failed the parse, gave PdfRepair a
            // headerless byte range to scan, and surfaced as a parse exception for an intact file
            // — with LastRepairOutcome null, so nothing pointed at the real cause.
            using var buffered = new MemoryStream();
            PdfHelpers.EnsureSeekable(input).CopyTo(buffered);
            byte[] original = buffered.ToArray();

            PdfDocument first = null;
            Exception firstEx = null;
            try
            {
                first = PdfDocument.Open(new MemoryStream(original, writable: false));
                // A healthy document is returned untouched - no repair, nothing reported.
                if (first.Pages.Count > 0) return first;
            }
            catch (Exception ex)
            {
                firstEx = ex;
            }

            // Two ways to get here, and only one of them used to. The parse THREW - or it
            // succeeded and produced a document with no pages at all, which is what a wrong
            // `startxref` does: the table cannot be read, nothing is indexed, and the document
            // opens perfectly happily reporting zero pages. That is the single most recoverable
            // real-world corruption and it fell straight through this method. See plan 09 R1.
            int originalPages = first?.Pages.Count ?? 0;

            try
            {
                // The repaired bytes must outlive this scope: PdfDocument reads lazily from the
                // stream it was handed. A `using` on that stream disposed it before the caller
                // ever touched the returned document, so the first Pages access threw
                // ObjectDisposedException. Copy the bytes out and hand over a stream that lives.
                byte[] repairedBytes;
                using (var src = new MemoryStream(original, writable: false))
                using (var repaired = new MemoryStream())
                {
                    PdfRepair.Repair(src, repaired);
                    repairedBytes = repaired.ToArray();
                }

                var repairedDoc = PdfDocument.Open(new MemoryStream(repairedBytes, writable: false));

                // Accept the repair ONLY when it is an improvement. Repair cannot invent pages, and
                // returning a differently-broken document in place of the caller's would be worse
                // than returning theirs.
                if (repairedDoc.Pages.Count > originalPages)
                {
                    LastRepairOutcome =
                        $"Cross-reference data was unusable; rebuilt by scanning. " +
                        $"Pages recovered: {originalPages} -> {repairedDoc.Pages.Count}.";
                    first?.Dispose();
                    return repairedDoc;
                }

                repairedDoc.Dispose();
            }
            catch
            {
                // Fall through: the original outcome is what the caller gets.
            }

            // Repair did not help. Surface the original parse exception when there was one - it is
            // more informative than any downstream repair failure - otherwise hand back the
            // document as opened, empty but honest.
            // Rethrown with the stack it was thrown with: `throw firstEx` reset it to this line, so the
            // trace pointed here rather than at the parse that failed.
            if (firstEx != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstEx).Throw();
            return first;
        }
    }
}
