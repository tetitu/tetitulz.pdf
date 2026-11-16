// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Returns the number of pages in the PDF.
        /// </summary>
        /// <remarks>
        /// Delegates to the stream overload rather than opening the file directly, so an encrypted
        /// document is decrypted and <see cref="LastExtractionError"/> is reset — see
        /// <see cref="GetMetadata(string)"/> for why that matters.
        /// </remarks>
        public static int GetPageCount(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return GetPageCount(fs);
        }

        /// <summary>Returns the number of pages in the PDF stream.</summary>
        public static int GetPageCount(Stream input, string password = null)
            => WithDecrypted(input, password, s =>
            {
                using (var doc = PdfDocument.Open(s))
                    return doc.Pages.Count;
            });

        /// <summary>
        /// Returns the document metadata (/Info dictionary) from the PDF.
        /// Properties may be null when not present in the file.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this goes through the stream overload.</b> Opening the path directly
        /// neither decrypted nor reset <see cref="LastExtractionError"/> — while that property's
        /// own contract is "reset at the start of every call, on every path, so a value never
        /// survives from an earlier document". A caller who read an encrypted document on this
        /// thread and then called this method on a clean file saw the previous document's failure
        /// reason and rejected a perfectly good file. Two overloads of one public method also gave
        /// different answers for the same bytes.</para>
        /// </remarks>
        public static PdfMetadata GetMetadata(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return GetMetadata(fs);
        }

        /// <summary>
        /// Returns the document metadata from a PDF stream.
        /// </summary>
        public static PdfMetadata GetMetadata(Stream input, string password = null)
            => WithDecrypted(input, password, s =>
            {
                using (var doc = PdfDocument.Open(s))
                    return doc.Metadata;
            });

        /// <summary>
        /// Runs an <b>object-graph</b> read over a possibly-encrypted stream, decrypting first.
        /// </summary>
        /// <remarks>
        /// Only for operations that parse the object graph. Two neighbouring categories must
        /// <b>not</b> use it:
        /// <list type="bullet">
        ///   <item><b>Byte-level</b> reads — <see cref="GetRevisions"/>,
        ///     <see cref="ExtractRevision"/> — already work on an encrypted file, because a
        ///     <c>%%EOF</c>/<c>startxref</c> chain is not encrypted. Decrypting would re-serialise
        ///     the file and give it entirely different offsets from the ones a revision boundary is
        ///     defined by, turning a working operation into a broken one.
        ///     <b>The exception is <see cref="GetRevisionsWithSignatures"/></b>, whose boundaries
        ///     are still byte-level but whose attribution needs the form: it reports the
        ///     encrypted case through <c>SignaturesExamined</c> instead of routing here, for the
        ///     same reason — decrypting would move every offset it just measured.</item>
        ///   <item><b>Writers</b> must keep failing closed: a decrypting rewrite emits a clear-text
        ///     file where the reader expects per-object ciphertext. That is what
        ///     <c>Encryption.PdfEncryptedRoundTrip</c> (decrypt → operate → re-encrypt) exists to
        ///     avoid.</item>
        /// </list>
        /// <para><b>Decrypting is only half the guarantee.</b> With no <c>IPdfDecryptor</c>
        /// registered — the state of every host that never referenced
        /// <c>Tetitulz.Pdf.Encryption</c> — the bytes come back unchanged and no exception is
        /// raised, so <paramref name="read"/> parses an unreadable catalog and reports an empty
        /// outline, no links and empty metadata. That is the silent-empty shape
        /// <see cref="LastExtractionError"/> exists to forbid, and <c>ExtractText</c> was the only
        /// caller guarding against it; putting the check here is what gives every caller of this
        /// helper the same answer instead of it having to be remembered ~44 more times.</para>
        /// <para>The return value is deliberately left alone: a caller that ignores the diagnostic
        /// still sees exactly what it saw before, and callers that check it can now tell
        /// "encrypted, could not read" from "read fine, found nothing".</para>
        /// <para><b>Password overloads are additive, never widened.</b> Routing a shipped reader
        /// through here means giving it a <c>password</c> parameter, and the tempting way to do that
        /// — appending <c>string password = null</c> to the existing signature — is a
        /// <em>binary</em> break. C# bakes optional-argument defaults in at the <i>call site</i>, so
        /// an assembly already compiled against the narrow form emits a <c>call</c> to a member that
        /// no longer exists and fails with <see cref="MissingMethodException"/> at run time, even
        /// though it still compiles from source. This library ships as NuGet packages and
        /// multi-targets <c>netstandard2.0</c> precisely to serve net462 hosts, which is exactly the
        /// consumer that break reaches. So the original signature stays as it shipped and delegates
        /// to a <i>new</i> overload carrying the password — the same rule, for the same reason, that
        /// keeps <c>AutoTagResult</c>'s six-parameter constructor alongside its wider one.</para>
        /// <para>Where the original already ended in optional parameters, the new overload takes
        /// <c>password</c> <i>before</i> them (a required parameter cannot follow an optional one).
        /// That placement is also what makes the pair unambiguous at every arity: the password
        /// overload is inapplicable unless a password is actually supplied, so no existing call site
        /// can start resolving to it.</para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        private static T WithDecrypted<T>(Stream input, string password, Func<Stream, T> read)
        {
            // Checked before EnsureSeekable, which dereferences `input.CanSeek` and so surfaced a
            // null as NullReferenceException — never a contract, and not what a caller catching
            // the ArgumentNullException every sibling on this facade throws would catch.
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Reset first, on every path. A slot a caller reads AFTER an operation must be cleared
            // at the START of it, or the previous document's reason survives into this one's answer
            // — the LastPermsValidation lesson, which [ThreadStatic] storage does not fix.
            _lastExtractionError = null;

            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            Stream source = Rendering.PdfPageRenderer.EnsureDecryptedStream(
                seekable, password, out bool ownsSource, out bool stillEncrypted);
            try
            {
                if (stillEncrypted)
                    _lastExtractionError =
                        "the document is encrypted and could not be decrypted, so nothing was read " +
                        "(register Tetitulz.Pdf.Encryption.PdfEncryptionOperations and pass the user password)";

                return read(source);
            }
            finally
            {
                if (ownsSource) source.Dispose();
                // EnsureSeekable copies a non-seekable input into a fresh MemoryStream; disposing
                // only `source` left that full-document copy alive for the GC, which for an
                // unencrypted document meant nothing was disposed at all. One buffer per request
                // surviving to gen 2 is the leak PdfPageRenderer.WithDecryptedDocument was
                // extracted to end — its remarks name this drift.
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// Returns the PDF version (e.g., "1.4", "2.0").
        /// </summary>
        public static string GetVersion(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return GetVersion(fs);
        }

        /// <summary>Returns the PDF version of the stream (e.g., "1.4", "2.0").</summary>
        public static string GetVersion(Stream input, string password = null)
            => WithDecrypted(input, password, s =>
            {
                using (var doc = PdfDocument.Open(s))
                    return doc.Version.ToString();
            });
    }
}
