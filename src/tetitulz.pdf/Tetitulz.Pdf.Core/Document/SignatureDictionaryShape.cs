// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// What makes a dictionary a signature dictionary (ISO 32000-2 §12.8.1, Table 255), and the one
    /// consequence the encryption layer draws from it.
    /// </summary>
    /// <remarks>
    /// <para><b>ISO 32000-2 §7.6.2, "Application of encryption":</b> "Encryption applies to all
    /// strings and streams in the document's PDF file, with the following exceptions: … Any
    /// hexadecimal strings representing the value of the Contents key in a Signature dictionary".
    /// The <c>/Contents</c> hole is the bytes a <c>/ByteRange</c> skips, so the CMS written there
    /// must be the bytes the file stores — a writer that encrypted it would give the verifier
    /// ciphertext to parse, and a reader that decrypted it would garble a valid signature.</para>
    ///
    /// <para><b>One rule, four readers.</b> <c>DocMdpAnalyzer</c> classifies signature values by
    /// it; <c>Writing.PdfWriter</c> leaves the entry in the clear when it encrypts an appended
    /// object; <c>Tetitulz.Pdf.Encryption</c>'s decryptor and whole-file encryptor skip it in the
    /// same way. It is public because that assembly cannot see Core's internals, and the read side
    /// and the write side must answer the same question about the same dictionary — two spellings
    /// of "is this a signature dictionary" would decrypt what was never encrypted.</para>
    ///
    /// <para><b><c>/Type</c> is optional</b> (Table 255), so a dictionary carrying both a
    /// <c>/ByteRange</c> and a <c>/Contents</c> is one too — the reading other implementations adopted after
    /// a producer's untyped signature dictionary had its CMS decrypted into
    /// noise. A page dictionary's <c>/Contents</c> is a stream reference and it has no
    /// <c>/ByteRange</c>; a text annotation's <c>/Contents</c> string sits under <c>/Type /Annot</c>
    /// and has none either. Neither is exempted.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer.</para>
    /// </remarks>
    public static class SignatureDictionaryShape
    {
        /// <summary>
        /// Whether <paramref name="dictionary"/> is a signature field's value: <c>/Type /Sig</c> or
        /// <c>/DocTimeStamp</c>, or — <c>/Type</c> being optional — a <c>/ByteRange</c> beside a
        /// <c>/Contents</c>.
        /// </summary>
        /// <param name="dictionary">The dictionary to classify; <c>null</c> is not one.</param>
        /// <param name="resolve">
        /// Follows an indirect <c>/Type</c> (§7.3.10 permits one). Pass <see cref="Direct"/> where
        /// there is no document to resolve against — a writer serialising the dictionary, a
        /// decryptor walking one object.
        /// </param>
        public static bool IsSignatureValue(PdfDictionary dictionary, Func<PdfObject, PdfObject> resolve)
        {
            if (dictionary == null) return false;
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));

            // "DocTimeStamp" is a string compare, as the analyzer always spelled it: the name cache
            // has no field for it, and a switch-style compare needs none.
            string type = (resolve(dictionary[PdfName.Type]) as PdfName)?.Value;
            if (type == PdfName.Sig.Value || type == "DocTimeStamp") return true;
            return dictionary.ContainsKey(PdfName.ByteRange) && dictionary.ContainsKey(PdfName.Contents);
        }

        /// <summary>
        /// §7.6.2: whether the entry <paramref name="key"/> of <paramref name="owner"/> is left
        /// unencrypted in an encrypted document — true for the <c>/Contents</c> of a signature
        /// dictionary and for nothing else.
        /// </summary>
        /// <param name="owner">The dictionary the entry belongs to.</param>
        /// <param name="key">The entry's key.</param>
        public static bool IsUnencryptedEntry(PdfDictionary owner, PdfName key)
            => key == PdfName.Contents && IsSignatureValue(owner, Direct);

        /// <summary>The resolver for a dictionary read outside a document: the value as written.</summary>
        public static PdfObject Direct(PdfObject value) => value;
    }
}
