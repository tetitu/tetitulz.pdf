// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    public sealed class PdfSignatureField : PdfFormField
    {
        private readonly PdfDocument _document;

        public PdfSignatureField(PdfDictionary dictionary, PdfDocument document, PdfFormField parent)
            : base(dictionary, document, parent)
        {
            _document = document;
        }

        /// <summary>
        /// The signature dictionary this field's own <c>/V</c> names, or null.
        /// </summary>
        /// <remarks>
        /// <para><b>Its own, not inherited, and that is deliberate.</b> §12.7.3.1 makes <c>/V</c>
        /// inheritable, but a widget showing its parent field's value is not a <i>second</i>
        /// signature — <c>SignatureFieldWalk.DistinctSignatures</c> relies on the null here to keep
        /// a value-less node out of the count.</para>
        ///
        /// <para>The document is null-guarded like every read in the base class: both constructors
        /// are public and take the document as a plain parameter, so a caller can build a wrapper
        /// with none. This line dereferenced it unguarded, in the file next door to the one whose
        /// <c>LoadKids</c> comment records fixing exactly that.</para>
        /// </remarks>
        public PdfDictionary SignatureValue
        {
            get
            {
                var v = Dictionary[PdfName.V];
                if (v == null) return null;
                return _document?.ResolveObject(v) as PdfDictionary ?? v as PdfDictionary;
            }
        }

        public bool HasSignature => SignatureValue != null;

        /// <summary>The signature dictionary's <c>/Filter</c> name, following an indirect reference.</summary>
        public string Filter => (Entry(PdfName.Filter) as PdfName)?.Value;

        /// <summary>The signature dictionary's <c>/SubFilter</c> name, following an indirect reference.</summary>
        public string SubFilter => (Entry(PdfName.SubFilter) as PdfName)?.Value;

        /// <summary>The signature dictionary's <c>/ByteRange</c> array, following an indirect reference.</summary>
        public PdfArray ByteRange => Entry(PdfName.ByteRange) as PdfArray;

        /// <summary>
        /// The signature dictionary's <c>/Contents</c> string — its own, never a reference.
        /// </summary>
        /// <remarks>
        /// The one entry this class does not resolve. Its bytes are the hole the <c>/ByteRange</c>
        /// leaves out of the digest, so a conforming signer cannot have written it anywhere but inline
        /// (§12.8.1, Table 255: with a byte range digest every value "shall be direct"). Following a
        /// reference here would let a malformed file answer "has contents" for bytes the range could
        /// not have excluded.
        /// </remarks>
        public PdfString Contents => SignatureValue?.GetString(PdfName.Contents);

        /// <summary>The signature dictionary's <c>/Reason</c>, following an indirect reference.</summary>
        public string Reason => (Entry(PdfName.Reason) as PdfString)?.GetText();

        /// <summary>The signature dictionary's <c>/Location</c>, following an indirect reference.</summary>
        public string Location => (Entry(PdfName.Location) as PdfString)?.GetText();

        /// <summary>The signature dictionary's <c>/M</c> date string, following an indirect reference.</summary>
        public string SigningTime => (Entry(PdfName.M) as PdfString)?.GetText();

        /// <summary>
        /// An entry of the signature dictionary, following an indirect reference — which the typed
        /// getters do not, so <c>/SubFilter 9 0 R</c> read as absent here while the verifier, which
        /// resolves its own reads, reported the real value: two public answers about one file.
        /// </summary>
        private PdfObject Entry(PdfName key)
        {
            var value = SignatureValue?[key];
            return value is PdfReference && _document != null ? _document.ResolveObject(value) : value;
        }
    }
}
