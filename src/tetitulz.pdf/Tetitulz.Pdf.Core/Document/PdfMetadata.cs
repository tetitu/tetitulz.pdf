// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    public sealed class PdfMetadata
    {
        private readonly PdfDictionary _infoDict;
        private readonly PdfDocument _document;

        /// <summary>
        /// Reads <c>/Info</c> without a document, so an entry written as an indirect reference
        /// cannot be followed and reads as absent.
        /// </summary>
        /// <remarks>
        /// Kept for source and binary compatibility. <b>Prefer the overload that takes the
        /// document</b> — see it for why the difference destroys data rather than merely hiding it.
        /// </remarks>
        public PdfMetadata(PdfDictionary infoDict)
            : this(infoDict, null)
        {
        }

        /// <summary>
        /// Reads <c>/Info</c>, resolving entries written as indirect references.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the document is needed, and why its absence was destructive.</b> §7.3.10
        /// permits any value to be indirect, and a producer that shares one string object between
        /// <c>/Info</c> and its XMP packet writes exactly that. Without a document
        /// <c>_infoDict[key]</c> returns the <see cref="PdfReference"/>, the <c>is PdfString</c>
        /// test fails, and the property reads <see langword="null"/> — indistinguishable from "this
        /// document has no title".</para>
        ///
        /// <para>That is not merely a missing read. <c>PdfMetadataWriter.WriteInfo</c> builds a
        /// <b>brand new</b> <c>/Info</c> dictionary, repopulates it from these very properties, and
        /// stamps it with the <b>original object number</b> — so the appended revision supersedes
        /// the original. Every indirectly-written entry the reader could not see is dropped from the
        /// document, by an operation that reports success. Setting one field silently erased the
        /// others.</para>
        /// </remarks>
        public PdfMetadata(PdfDictionary infoDict, PdfDocument document)
        {
            _infoDict = infoDict;
            _document = document;
        }

        public string Title => GetStringValue(PdfName.Title);
        public string Author => GetStringValue(PdfName.Author);
        public string Subject => GetStringValue(PdfName.Subject);
        public string Keywords => GetStringValue(PdfName.Keywords);
        public string Creator => GetStringValue(PdfName.Creator);
        public string Producer => GetStringValue(PdfName.Producer);

        /// <summary>Raw PDF date string for /CreationDate.</summary>
        public string CreationDate => GetStringValue(PdfName.CreationDate);

        /// <summary>Raw PDF date string for /ModDate.</summary>
        public string ModDate => GetStringValue(PdfName.ModDate);

        /// <summary>
        /// Parses /CreationDate as a DateTimeOffset.
        /// PDF date format: D:YYYYMMDDHHmmSSOHH'mm'
        /// Returns null if not present or unparseable.
        /// </summary>
        public DateTimeOffset? CreationDateParsed => ParsePdfDate(CreationDate);

        /// <summary>
        /// Parses /ModDate as a DateTimeOffset.
        /// </summary>
        public DateTimeOffset? ModDateParsed => ParsePdfDate(ModDate);

        private string GetStringValue(PdfName key)
        {
            var raw = _infoDict[key];
            var obj = _document?.ResolveObject(raw) ?? raw;
            if (obj is PdfString str)
                return str.GetText();
            return null;
        }

        /// <summary>
        /// Parses a PDF date string (§7.9.4) into a <see cref="DateTimeOffset"/> — forwards to
        /// <see cref="Tetitulz.Pdf.Core.Util.PdfDate.TryParse(string)"/>, the one implementation.
        ///
        /// <para>This was the complete copy of the two; the other took eight characters and threw
        /// away the time of day and the time zone. Kept as a name because it is <c>internal</c>
        /// and already called from several places.</para>
        /// </summary>
        internal static DateTimeOffset? ParsePdfDate(string dateStr)
            => Tetitulz.Pdf.Core.Util.PdfDate.TryParse(dateStr);
    }
}
