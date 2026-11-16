// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// An open PDF file: its cross-reference table, trailer, catalog and pages, read on demand.
    /// </summary>
    /// <remarks>
    /// <para><b>One thread at a time.</b> An instance is not thread-safe. Every read seeks the one
    /// stream it was opened over, and what it has read is cached in unsynchronised dictionaries (the
    /// parser's objects, each font's resolved glyphs). Two threads reading one instance can corrupt
    /// both. Open one instance per thread over its own stream, as the library's parallel page
    /// renderer does for each of its workers.</para>
    /// <para>The static operations that take a stream or bytes open their own instance, so they may
    /// run concurrently on different inputs. The <c>Last*</c> diagnostic slots are per thread: read
    /// one on the thread that made the call. <see cref="Constants.PdfLimits"/> is process-global and
    /// is set once, at startup.</para>
    /// </remarks>
    public sealed class PdfDocument : IDisposable
    {
        /// <summary>
        /// Size of the in-process read buffer wrapped around a seekable input stream. The
        /// tokenizer reads one byte per call, so an unbuffered stream costs one syscall per
        /// byte of the file; 64&#160;KiB amortises that away without holding the whole document.
        /// </summary>
        private const int ReadBufferSizeBytes = 64 * 1024;

        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly PdfParser _parser;
        private List<PdfPage> _pages;

        public PdfParser Parser => _parser;

        /// <summary>
        /// The underlying seekable stream holding the raw PDF bytes.
        /// Plugins and external code may read and seek this stream; do not dispose it.
        /// </summary>
        public Stream BaseStream => _stream;

        private PdfDocument(Stream stream, bool ownsStream)
        {
            _stream = stream;
            _ownsStream = ownsStream;
            _parser = new PdfParser(stream);
        }

        public PdfVersion Version { get; private set; }
        public PdfDictionary Trailer => _parser.Trailer;
        public CrossReferenceTable CrossReferenceTable => _parser.CrossReferenceTable;

        public PdfCatalog Catalog { get; private set; }
        public PdfMetadata Metadata { get; private set; }

        public IReadOnlyList<PdfPage> Pages
        {
            get
            {
                if (_pages == null)
                    _pages = PdfPageTree.GetPages(this);
                return _pages;
            }
        }

        private Dictionary<int, int> _pageIndexByObject;

        /// <summary>
        /// The zero-based index of the page whose dictionary is object <paramref name="objectNumber"/>,
        /// or -1 — its first occurrence, as a scan of <see cref="Pages"/> would find it.
        /// </summary>
        /// <remarks>
        /// Built once, at the first question: a destination, an outline item and a document part each
        /// name their page by reference, and finding it by a scan of the pages would cost
        /// destinations × pages, both counts the file's. <see cref="Pages"/> is fixed once read, so the index is too.
        /// </remarks>
        internal int PageIndexOfObject(int objectNumber)
        {
            if (objectNumber <= 0) return -1;
            if (_pageIndexByObject == null)
            {
                var pages = Pages;
                var index = new Dictionary<int, int>(pages.Count);
                for (int i = 0; i < pages.Count; i++)
                {
                    int number = pages[i]?.Dictionary?.ObjectNumber ?? 0;
                    if (number > 0 && !index.ContainsKey(number)) index[number] = i;
                }
                _pageIndexByObject = index;
            }
            return _pageIndexByObject.TryGetValue(objectNumber, out int found) ? found : -1;
        }

        /// <summary>Whether <see cref="PageIndexOfObject"/> has built its index, for tests: the index's liveness.</summary>
        internal bool PageIndexBuilt => _pageIndexByObject != null;

        public PdfAcroForm AcroForm { get; private set; }

        /// <summary>
        /// Gets whether this PDF is encrypted.
        /// </summary>
        public bool IsEncrypted { get; private set; }

        /// <summary>
        /// Gets the encryption dictionary if the PDF is encrypted.
        /// </summary>
        public PdfDictionary EncryptionDictionary { get; private set; }

        public static PdfDocument Open(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            Stream workStream;
            bool ownsStream;

            if (stream.CanSeek)
            {
                // The tokenizer reads one byte per call (PeekByte = read-then-Position--).
                // Over an unbuffered seekable stream (e.g. a raw FileStream) that is
                // O(filesize) syscalls.  Wrap it in a BufferedStream so the parser and
                // tokenizer (which share this one cursor) read through a 64 KiB in-process
                // buffer instead.  A MemoryStream is already in-memory, so leave it as-is.
                // We do NOT own the caller's stream, and netstandard2.0's BufferedStream
                // has no leave-open option, so we keep ownsStream=false: Dispose never
                // touches the wrapper, the caller's underlying stream stays open as before,
                // and the read-only wrapper is reclaimed by the GC (nothing to flush).
                workStream = (stream is MemoryStream || stream is BufferedStream)
                    ? stream
                    : new BufferedStream(stream, ReadBufferSizeBytes);
                ownsStream = false;
            }
            else
            {
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                workStream = ms;
                ownsStream = true;
            }

            var doc = new PdfDocument(workStream, ownsStream);
            try { doc.Load(); }
            catch { doc.Dispose(); throw; }
            return doc;
        }

        public static PdfDocument Open(string filePath)
        {
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Buffer the file: the tokenizer's one-byte-per-call reads would otherwise be
            // O(filesize) syscalls.  The doc owns the BufferedStream wrapper; disposing it
            // disposes the inner FileStream, so the file handle is still released correctly.
            var stream = new BufferedStream(fileStream, ReadBufferSizeBytes);
            var doc = new PdfDocument(stream, true);
            try { doc.Load(); }
            catch { doc.Dispose(); throw; }
            return doc;
        }

        private void Load()
        {
            Version = _parser.ParseHeader();
            _parser.ParseCrossReferences();

            // Check for encryption
            if (Trailer.ContainsKey(PdfName.Encrypt))
            {
                IsEncrypted = true;
                var encryptRef = Trailer[PdfName.Encrypt];
                var encryptObj = _parser.ResolveObject(encryptRef);
                EncryptionDictionary = encryptObj as PdfDictionary;

                // When encrypted, we cannot resolve objects stored in object
                // streams (ObjStm) because their data is still encrypted.
                // The decryptor only needs the trailer and encryption dictionary,
                // so skip loading catalog/metadata/AcroForm.
                Metadata = new PdfMetadata(new PdfDictionary());
                return;
            }

            // Load catalog
            var rootRef = Trailer[PdfName.Root];
            var rootObj = ResolveObject(rootRef);
            // A trailer whose /Root is missing or names nothing, over a cross-reference table that
            // read cleanly, does not end here: the scan recovery a broken table takes finds the last
            // /Type /Catalog object instead, and
            // PdfParser.LastRootRecovery says so; a file with no catalog at all still refuses.
            if (!(rootObj is PdfDictionary) && _parser.TryRecoverRoot())
                rootObj = ResolveObject(Trailer[PdfName.Root]);
            if (rootObj is PdfDictionary catalogDict)
                Catalog = new PdfCatalog(catalogDict, this);
            else
                throw new PdfParseException("Cannot find document catalog." +
                    (PdfParser.LastHeaderRecovery != null
                        ? " The file has no readable %PDF- header either (" + PdfParser.LastHeaderRecovery + "); it may not be a PDF."
                        : ""));

            // §7.5.2: the catalog's /Version override wins over the header when present and
            // numerically later. Malformed or downgrading values are ignored — the header
            // stays the floor, since a producer can raise the version but a corrupt or
            // adversarial override must not silently lower what the rest of this library
            // already gated against the header-parsed value.
            var versionEntry = Catalog.Dictionary[PdfName.Version];
            if (versionEntry != null &&
                ResolveObject(versionEntry) is PdfName versionName &&
                PdfVersion.TryParse(versionName.Value, out PdfVersion catalogVersion) &&
                catalogVersion > Version)
            {
                Version = catalogVersion;
            }

            // Load metadata from /Info dictionary
            var infoRef = Trailer[PdfName.Info];
            if (infoRef != null)
            {
                var infoObj = ResolveObject(infoRef);
                if (infoObj is PdfDictionary infoDict)
                    Metadata = new PdfMetadata(infoDict, this);
            }
            if (Metadata == null)
                Metadata = new PdfMetadata(new PdfDictionary());

            // Load AcroForm
            if (Catalog != null)
            {
                var acroFormObj = Catalog.Dictionary[PdfName.AcroForm];
                if (acroFormObj != null)
                {
                    var acroFormDict = ResolveObject(acroFormObj) as PdfDictionary;
                    if (acroFormDict != null)
                        AcroForm = new PdfAcroForm(acroFormDict, this);
                }
            }
        }

        public PdfObject ResolveObject(PdfObject obj)
        {
            return _parser.ResolveObject(obj);
        }

        public PdfObject GetObject(int objectNumber)
        {
            return _parser.GetObject(objectNumber);
        }

        public PdfObject ResolveReference(PdfReference reference)
        {
            if (reference == null) return PdfNull.Instance;
            return _parser.GetObject(reference.ReferenceObjectNumber);
        }

        public void Dispose()
        {
            _pages?.Clear();
            _pages = null;
            _pageIndexByObject = null;
            if (_ownsStream)
                _stream?.Dispose();
        }
    }
}
