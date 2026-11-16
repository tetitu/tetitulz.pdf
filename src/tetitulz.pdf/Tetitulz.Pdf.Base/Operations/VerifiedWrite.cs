// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// The <b>one</b> "did the appended revision actually reach the output?" mechanism in this
    /// assembly.
    ///
    /// <para>
    /// <b>Why it exists.</b> <see cref="DocumentRevisionWriter.Write"/> is documented never
    /// to throw: it catches every failure — an <see cref="IOException"/> from the caller's stream,
    /// an object it had to skip, a trailer it could not rebuild — records it on
    /// <see cref="DocumentEditDiagnostics"/> and leaves the output holding just the original bytes.
    /// A caller that assembles its result <em>before</em> that call therefore describes a document
    /// that may never have been produced: "7 headings renumbered", "1 font embedded",
    /// <c>ContentMarked = true</c>, about a file that is byte-for-byte the input. Every writer in
    /// this assembly asks this type instead, and reports zero when the answer is no.
    /// </para>
    ///
    /// <para>
    /// <b>Why the byte count and not the diagnostic slot.</b> Comparing
    /// <see cref="DocumentEditDiagnostics.LastError"/> across the call is a proxy, and a lossy one
    /// in both directions: Core's <c>IncrementalUpdateWriter</c> also reports through this sink for
    /// conditions it recovers from (a <c>/Prev</c> it could not resolve, an <c>/ID</c> it had to
    /// synthesise), so a perfectly good revision can move the slot, while a failure that repeats the
    /// previous message moves it not at all. Counting the bytes <c>output</c> actually accepted is
    /// exact: a revision was appended if and only if more than <c>originalBytes.Length</c> bytes
    /// were written. The diagnostic is still read, but only to <em>name</em> the cause.
    /// </para>
    ///
    /// <para>
    /// <b>Why the message is the caller's.</b> Each writer says what <em>its own</em> edit was and
    /// what the output therefore lacks ("nothing was tagged … the structure elements, MCIDs and
    /// marked content this call planned are NOT present"), and each owns its warning channel — a
    /// warnings list, <see cref="DocumentEditDiagnostics"/>, or a <c>[ThreadStatic]</c>
    /// <c>Last*Error</c>. So this type returns the two facts a message needs — whether the output is
    /// truncated, and the cause — and never composes or reports one itself. That is what let the
    /// mechanism be shared without changing a single existing warning string.
    /// </para>
    /// </summary>
    public static class VerifiedWrite
    {
        /// <summary>
        /// Performs the incremental write and reports whether the appended revision reached
        /// <paramref name="output"/>.
        /// </summary>
        /// <param name="output">Destination for the whole document; the writer emits the original bytes itself.</param>
        /// <param name="originalBytes">The complete original PDF.</param>
        /// <param name="doc">The parsed original.</param>
        /// <param name="index">Index over <paramref name="doc"/>; may be null.</param>
        /// <param name="modifiedObjects">Existing objects being revised, keyed by object number. May be null.</param>
        /// <param name="newObjects">Objects being added, keyed by object number. May be null.</param>
        /// <param name="newInfoObjectNumber">Object number of a newly written <c>/Info</c>, or -1.</param>
        /// <param name="truncated">
        /// On <c>false</c>: whether the output holds <em>less</em> than the original document
        /// (the caller's stream refused part of the copy) rather than exactly the original.
        /// Meaningless when this returns <c>true</c>.
        /// </param>
        /// <param name="cause">
        /// On <c>false</c>: the writer's own diagnostic for the failure, or a fixed phrase when it
        /// gave none. <c>null</c> when this returns <c>true</c>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the revision was appended, or when there was legitimately nothing to
        /// append and the original document was copied through; <c>false</c> when the output holds
        /// only — or less than — the original document despite an edit having been planned.
        /// </returns>
        public static bool Write(
            Stream output,
            byte[] originalBytes,
            PdfDocument doc,
            PdfObjectIndex index,
            IReadOnlyDictionary<int, PdfObject> modifiedObjects,
            IReadOnlyDictionary<int, PdfObject> newObjects,
            int newInfoObjectNumber,
            out bool truncated,
            out string cause)
        {
            var counted = new ByteCountingStream(output);
            string beforeWrite = DocumentEditDiagnostics.LastError;

            DocumentRevisionWriter.Write(
                counted, originalBytes, doc, index, modifiedObjects, newObjects, newInfoObjectNumber);

            string afterWrite = DocumentEditDiagnostics.LastError;

            // An empty edit set is a legitimate no-op passthrough (the writer's documented
            // contract), so it only has to produce the original bytes. Otherwise one byte more than
            // the original is the minimum proof that a revision was appended.
            bool revisionExpected =
                (modifiedObjects != null && modifiedObjects.Count > 0) ||
                (newObjects      != null && newObjects.Count      > 0);

            long required = revisionExpected ? (long)originalBytes.Length + 1 : originalBytes.Length;
            if (counted.BytesWritten >= required)
            {
                truncated = false;
                cause     = null;
                return true;
            }

            truncated = counted.BytesWritten < originalBytes.Length;
            cause = afterWrite != null && !string.Equals(beforeWrite, afterWrite, StringComparison.Ordinal)
                ? afterWrite
                : "the writer appended no revision and gave no reason";
            return false;
        }

        /// <summary>
        /// <see cref="Write"/>, plus the failure message and the diagnostic — the shape every
        /// caller actually wants.
        /// </summary>
        /// <param name="operation">Stable <c>"Type.Method"</c> identifier used in the diagnostic.</param>
        /// <param name="whatWasPlanned">
        /// Noun phrase naming the edit, e.g. <c>"the renumbered heading levels"</c> or
        /// <c>"the embedded font programs"</c>. Spliced into the message so the caller is told
        /// which edit was lost, not merely that one was.
        /// </param>
        /// <param name="failureDetail">
        /// The reason the revision did not land, or <c>null</c> when this returns <c>true</c>.
        /// Already reported through <see cref="DocumentEditDiagnostics"/>.
        /// </param>
        /// <remarks>
        /// This lived on <c>PdfStructureTreeEditor</c> in the Accessibility assembly, which meant
        /// the font remediator had to reach into a structure-tree editor to write a font program —
        /// a dependency with no conceptual content, and one of the seven layers plan 04 D2 has to
        /// unpick. It is the reporting half of the mechanism <see cref="Write"/> is the measuring
        /// half of, so it belongs next to it.
        /// </remarks>
        /// <param name="output">Receives the original bytes plus the appended revision.</param>
        /// <param name="originalBytes">
        /// The document exactly as read. Written through unchanged, which is what keeps a signature
        /// over them verifiable.
        /// </param>
        /// <param name="doc">The open document the edit was planned against.</param>
        /// <param name="index">
        /// Object index for <paramref name="doc"/>, supplying the next free object number and the
        /// existing generation numbers.
        /// </param>
        /// <param name="modifiedObjects">
        /// Existing objects to supersede, keyed by object number. Each keeps its number, so the
        /// revision replaces rather than duplicates.
        /// </param>
        /// <param name="newObjects">Objects that do not yet exist, keyed by their new number.</param>
        /// <param name="newInfoObjectNumber">
        /// Object number of a newly minted <c>/Info</c> the trailer must point at, or 0 when the
        /// edit mints none. Without it a minted <c>/Info</c> is written and unreachable.
        /// </param>
        public static bool WriteOrExplain(
            Stream output,
            byte[] originalBytes,
            PdfDocument doc,
            PdfObjectIndex index,
            IReadOnlyDictionary<int, PdfObject> modifiedObjects,
            IReadOnlyDictionary<int, PdfObject> newObjects,
            int newInfoObjectNumber,
            string operation,
            string whatWasPlanned,
            out string failureDetail)
        {
            if (Write(output, originalBytes, doc, index, modifiedObjects, newObjects,
                      newInfoObjectNumber, out bool truncated, out string cause))
            {
                failureDetail = null;
                return true;
            }

            failureDetail = "The incremental revision carrying " + whatWasPlanned +
                " never reached the output stream (" + cause + "), so the output holds " +
                (truncated ? "only part of the original document" : "the original, unedited document") +
                " and nothing was changed. The result is therefore reported as zero rather than as " +
                "an edit the output does not contain.";

            DocumentEditDiagnostics.Report(operation, failureDetail);
            return false;
        }

        /// <summary>
        /// Pass-through <see cref="Stream"/> that records how many bytes the caller's stream
        /// actually accepted. Everything else is forwarded verbatim — in particular
        /// <see cref="CanSeek"/> and <see cref="Position"/>, which Core's incremental writer reads
        /// to keep byte offsets correct when the caller pre-positioned the stream.
        ///
        /// <para>
        /// The wrapped stream is never disposed: it belongs to the caller of the public operation,
        /// who may keep writing to it.
        /// </para>
        /// </summary>
        private sealed class ByteCountingStream : Stream
        {
            private readonly Stream _inner;

            internal ByteCountingStream(Stream inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            /// <summary>Bytes successfully written through this wrapper.</summary>
            internal long BytesWritten { get; private set; }

            public override bool CanRead  => _inner.CanRead;
            public override bool CanSeek  => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length   => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
                => _inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                // Count only what the inner stream accepted: a throwing write must not be counted,
                // or a failed copy would look like a successful one.
                _inner.Write(buffer, offset, count);
                BytesWritten += count;
            }

            // There is deliberately no Write(ReadOnlySpan<byte>) override, and it is not an
            // oversight: Base multi-targets netstandard2.0, where Stream declares no such method
            // to override — this type only compiled with one while it lived in the net10.0-only
            // Accessibility assembly. Removing it is behaviour-preserving rather than a
            // concession, because Stream's own span overload routes through
            // Write(byte[], int, int), which is overridden above and counted. The cost is one
            // pooled-array copy per call on the modern target, against a wrapper that runs once
            // per document write. (This codebase has no #if anywhere in Core or Base; adding the
            // first one to save that copy would be a poor trade.)

            public override void WriteByte(byte value)
            {
                _inner.WriteByte(value);
                BytesWritten++;
            }

            protected override void Dispose(bool disposing)
            {
                // Deliberately does not dispose _inner — see the type remarks.
                base.Dispose(disposing);
            }
        }
    }
}
