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
        /// Extracts all text from a PDF file.
        /// </summary>
        /// <remarks>
        /// <b>This opened the file directly</b>, and so did three things its <see cref="Stream"/>
        /// sibling does not: it never decrypted (an encrypted document extracted as the empty
        /// string, silently), it never fired the text-extraction plugin hook (a plugin observing
        /// extraction saw stream reads and was blind to file reads), and it never reset
        /// <see cref="LastExtractionError"/> — so a previous encrypted read's message survived into
        /// this call's successful answer, which is precisely what that property's contract forbids.
        /// </remarks>
        public static string ExtractText(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return ExtractText(fs, null);
        }

        /// <summary>
        /// Extracts text from a specific page of a PDF file (1-based page number).
        /// </summary>
        /// <remarks>Same three defects as the whole-document overload above; same fix.</remarks>
        public static string ExtractText(string pdfPath, int pageNumber)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return ExtractText(fs, pageNumber, null);
        }

        /// <summary>
        /// Extracts text from a specific page of a PDF stream (1-based page number), decrypting
        /// first when the document is encrypted and a decryptor is registered.
        /// </summary>
        /// <remarks>
        /// Added because the per-page read existed only in a file-path shape, so a caller holding a
        /// stream had to extract the whole document to get one page — and the file-path shape was
        /// the one that skipped decryption.
        /// </remarks>
        public static string ExtractText(Stream input, int pageNumber, string password = null)
        {
            return ReadDecrypted(input, password, doc =>
            {
                if (pageNumber < 1 || pageNumber > doc.Pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(pageNumber),
                        $"Page number must be between 1 and {doc.Pages.Count}.");

                string text = PdfTextExtractor.ExtractText(doc.Pages[pageNumber - 1]);

                var host = PdfPluginHost.Instance;
                if (host.GetPlugins().Count > 0)
                    host.InvokeTextExtracted(pageNumber - 1, text);

                return text;
            });
        }

        [System.ThreadStatic] private static string _lastExtractionError;

        /// <summary>
        /// Why the last <b>object-graph read</b> on this facade returned less than the document
        /// contains, or <c>null</c> when it did not.
        /// </summary>
        /// <remarks>
        /// <para>An encrypted document does not extract as the empty string with no exception and no
        /// diagnostic — a caller could not tell it from a scanned page carrying no text layer,
        /// which is a different problem with a different fix. Reset at the start of every call, on
        /// every path, so a value never survives from an earlier document.</para>
        /// <para><b>It covers more than <see cref="ExtractText(Stream, string)"/>.</b> Every reader
        /// routed through <c>WithDecrypted</c> — <see cref="GetMetadata(Stream, string)"/>,
        /// <see cref="GetBookmarks"/>, <see cref="ExtractLinks(Stream, string)"/>,
        /// <see cref="GetSignatures(Stream, string)"/> — had the same silent-empty shape and now
        /// reports here too, because a guard that has to be remembered per method is a guard that
        /// gets forgotten. One slot serves them all: each call resets it first, so the value always
        /// describes the most recent read on <i>this thread</i>.</para>
        /// </remarks>
        public static string LastExtractionError => _lastExtractionError;

        /// <summary>
        /// Extracts all text from a PDF stream, decrypting first when the document is encrypted and
        /// a decryptor is registered (<c>Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register</c>).
        /// </summary>
        /// <param name="password">
        /// The user password. Ignored for an unencrypted document; an empty password is what most
        /// "owner-password only" files need.
        /// </param>
        /// <param name="input">Stream holding the PDF.</param>
        public static string ExtractText(Stream input, string password = null)
        {
            return ReadDecrypted(input, password, doc =>
            {
                // One walk, whether or not anything is watching it. This was two loops, and they
                // disagreed: the plugin branch appended pages with NO separator, so the same file
                // extracted as "one\ntwo" or "onetwo" depending on whether some unrelated plugin —
                // an image decoder, an OCR engine — was registered at that moment. It also lost the
                // first-page error, because each page resets that slot.
                var host = PdfPluginHost.Instance;
                return host.GetPlugins().Count == 0
                    ? PdfTextExtractor.ExtractText(doc)
                    : PdfTextExtractor.ExtractText(
                          doc, (i, pageText) => host.InvokeTextExtracted(i, pageText));
            });
        }

        /// <summary>
        /// The prologue both text-extraction entry points need: reset the diagnostic, make the
        /// input seekable, decrypt it if a decryptor is registered, open it, and refuse an
        /// encrypted document with the reason — then release whatever this call owns.
        /// </summary>
        /// <remarks>
        /// <para><b>The disposal is the part that had already drifted.</b> One overload released
        /// only the decrypted stream and not the seekable one, and for an unencrypted non-seekable
        /// input those are the <i>same object</i> — so nothing was released at all, one
        /// whole-document buffer per call, surviving to gen 2. Two flags and two disposals, written
        /// twice, is how that happens; there is one copy now.</para>
        ///
        /// <para><b>Both overloads share the refusal, which makes one existing test blind to
        /// it.</b> <c>TextExtractionOverloadParityTests</c> compares the stream and file-path
        /// overloads against each other, deliberately, because the absolute outcome depends on
        /// process-wide decryptor registration — so a break applied to both sides leaves it green.
        /// The absolute assertions live in <c>EncryptedExtractionTests</c>, one per overload.</para>
        ///
        /// <para><b>The null check is a widening, stated rather than slipped in.</b> The per-page
        /// overload threw <see cref="ArgumentNullException"/> for a null stream and the
        /// whole-document one did not, reaching a dereference further down instead. They now both
        /// throw it, named.</para>
        /// </remarks>
        private static string ReadDecrypted(Stream input, string password,
            Func<PdfDocument, string> read)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            _lastExtractionError = null;

            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(
                input, rewind: true, out bool ownsSeekable);
            Stream source = Rendering.PdfPageRenderer.EnsureDecryptedStream(
                seekable, password, out bool ownsSource);
            try
            {
                using var doc = PdfDocument.Open(source);

                // Still encrypted here means no decryptor was registered, or it declined. The
                // catalog and page tree are unreadable in that state, so every extractor would
                // return "" — the silent wrong answer LastExtractionError exists to prevent.
                if (doc.IsEncrypted)
                {
                    _lastExtractionError =
                        "the document is encrypted and could not be decrypted, so no text was read " +
                        "(register Tetitulz.Pdf.Encryption.PdfEncryptionOperations and pass the user password)";
                    return string.Empty;
                }

                return read(doc);
            }
            finally
            {
                if (ownsSource) source.Dispose();
                if (ownsSeekable) seekable.Dispose();
            }
        }
    }
}
