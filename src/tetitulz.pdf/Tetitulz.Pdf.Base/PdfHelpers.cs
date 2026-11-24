// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base
{
    /// <summary>
    /// Shared utility methods used across the operations layer. DRY principle: one
    /// definition, used everywhere.
    ///
    /// <para>Both members are thin forwarders to the single implementation:
    /// <see cref="EnsureSeekable"/> to <c>Core.IO.SeekableStream</c> and
    /// <see cref="CloneDictionary"/> to <c>PdfDictionary.CloneShallow</c>.</para>
    ///
    /// <para><b>Not to be confused with</b> <c>PdfMetadataWriter.EnsureSeekable</c>, which is
    /// a different contract: it always returns a <see cref="MemoryStream"/>, because the
    /// incremental-update writer needs to read the source bytes more than once.</para>
    /// </summary>
    public static class PdfHelpers
    {
        /// <summary>
        /// Ensures a stream is seekable and positioned at 0. An already-seekable stream is
        /// rewound and returned as-is; anything else is copied into a fresh
        /// <see cref="MemoryStream"/>. A <c>null</c> argument throws.
        /// </summary>
        /// <remarks>
        /// <b>The result's ownership is not expressed, and that is this signature's flaw</b> — a
        /// caller cannot tell the fresh buffer from its own argument, so it must either leak the
        /// one or risk disposing the other. Where that matters, call
        /// <c>Core.IO.SeekableStream.Ensure(stream, rewind, out bool owned)</c> directly; where the
        /// consumer is <c>PdfDocument.Open</c>, call nothing at all — it buffers a non-seekable
        /// stream itself and owns the copy.
        /// </remarks>
        public static Stream EnsureSeekable(Stream stream)
            => Core.IO.SeekableStream.Ensure(stream, rewind: true);

        /// <summary>
        /// Whether <paramref name="input"/> is an encrypted document.
        /// </summary>
        /// <remarks>
        /// <para><b>For entry points that must refuse rather than answer.</b> An encrypted document
        /// leaves <see cref="PdfDocument"/> with no readable catalog, so every read below it
        /// returns <i>nothing</i> — and for an operation whose result is a <i>verdict</i>, nothing
        /// is not a missing answer but a <b>wrong</b> one. Two different encrypted documents
        /// compared as zero pages against zero pages are "identical".</para>
        ///
        /// <para><b>Not every reader should use this.</b> A byte-level operation
        /// (<c>GetRevisions</c>, <c>InspectIncrementalSave</c>) reads the <c>%%EOF</c>/<c>startxref</c>
        /// chain, which is not encrypted, and works correctly on an encrypted file — refusing there
        /// would break a working operation. The ones that need it are those that read the object
        /// graph but cannot take a password, which is a short list held by
        /// <c>FacadePasswordCoverageTests.DeliberateExclusions</c>.</para>
        ///
        /// <para><b>It does not restore the caller's stream position, deliberately.</b> A first
        /// draft did, and a mutation that deleted the restore killed nothing — so the callees were
        /// read rather than the line kept on faith. Every consumer rewinds for itself:
        /// <c>SeekableStream.Ensure</c> and <c>SeekableStream.Buffered</c> both assign
        /// <c>Position = 0</c>, <c>PdfVisualDiff.CopyToMemory</c> does the same, and
        /// <c>PdfDocument.Open</c> seeks to find <c>startxref</c> whatever position it is handed.
        /// The restore was a redundant guard against a real hazard — the hardest kind to see
        /// through — and keeping it would have meant a line no test could reach.</para>
        /// </remarks>
        internal static bool IsEncryptedDocument(Stream input)
        {
            if (input == null) return false;

            Stream seekable = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool owned);
            try
            {
                using var doc = PdfDocument.Open(seekable);
                return doc.IsEncrypted;
            }
            catch (Core.PdfException)
            {
                // Unreadable for some other reason: not this method's question. The caller's own
                // parse will report it, and answering "encrypted" here would mislabel it.
                return false;
            }
            finally
            {
                if (owned) seekable.Dispose();
            }
        }

        /// <summary>
        /// Shallow-clones a PdfDictionary, by delegating to
        /// <see cref="PdfDictionary.CloneShallow"/> — the single implementation.
        ///
        /// <para><b>A <c>null</c> source throws <see cref="System.ArgumentNullException"/></b>: an
        /// empty dictionary would turn "you passed me nothing" into a valid-looking empty page or font
        /// dictionary that fails later and somewhere else.</para>
        /// </summary>
        public static PdfDictionary CloneDictionary(PdfDictionary source)
            => PdfDictionary.CloneShallow(source);

        /// <summary>
        /// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="pageIndex"/> is
        /// not a page of <paramref name="doc"/>.
        /// </summary>
        /// <remarks>
        /// Three byte-identical copies existed -- <c>PdfPageContentAppender</c>,
        /// <c>PdfPageManipulator</c> and, inlined rather than extracted,
        /// <c>PdfAnnotationWriter</c> -- down to the interpolated message. The parameter keeps the
        /// name <c>pageIndex</c> deliberately: <c>nameof</c> resolves to the same literal, so the
        /// exception's <c>ParamName</c> is unchanged for every caller and a host catching on it
        /// still works.
        /// <para>Two of the three sat directly above a comment recording an earlier consolidation
        /// of exactly this kind ("One implementation, in Core.Objects.PdfDictionary.CloneShallow"),
        /// so neither was a documented deliberate split.</para>
        /// </remarks>
        public static void ValidatePageIndex(PdfDocument doc, int pageIndex)
        {
            int count = doc.Pages.Count;
            if (pageIndex < 0 || pageIndex >= count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex),
                    $"pageIndex must be between 0 and {count - 1}.");
        }
    }
}
