// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// The one way a reader reaches a document's signatures: the AcroForm on a document whose catalog
    /// may not have been loaded, and every signature in it once each, nested ones included.
    /// </summary>
    /// <remarks>
    /// <para><b>In Core so both sides can reach it.</b> It lived in <c>Base.Operations.Documents</c>,
    /// serving the incremental-save report and the revision attribution, and <c>Signing</c> — which
    /// may not reference <c>Base</c> — restated its rule in <c>PdfLtvRefresher</c> rather than
    /// approximate it. A restated rule is a second implementation waiting to drift, and the two already
    /// differed: the Base copy compared dictionaries with the default equality, which is identity only
    /// until someone gives <see cref="PdfDictionary"/> an <c>Equals</c>.</para>
    ///
    /// <para><b>Why the verifier does not use it.</b> <c>PdfSignatureVerifier.VerifyAll</c> reports per
    /// field, and a caller who asks for a field by name must find it; collapsing two fields that name
    /// one signature dictionary would lose the second name.</para>
    /// </remarks>
    internal static class SignatureFieldWalk
    {
        /// <summary>
        /// The document's AcroForm, resolved from the trailer when <see cref="PdfDocument.AcroForm"/>
        /// is null.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the fallback.</b> <c>PdfDocument.Load</c> returns before the catalog for an
        /// encrypted file, because objects inside an object stream cannot be read until it is
        /// decrypted — so <c>AcroForm</c> is null there even when the object graph <i>is</i> readable,
        /// which it is for the common xref-table layout. A <c>/ByteRange</c> is four numbers, and
        /// numbers are not encrypted (§7.6.2 encrypts strings and streams), so the incremental-save
        /// report can still do its job on such a file.</para>
        ///
        /// <para>Note what it does <b>not</b> buy: <c>/T</c> is a string and therefore <i>is</i>
        /// encrypted, so a field name read this way is ciphertext. Callers that report names must
        /// account for that — <c>PdfRevisions.ListWithSignatures</c> refuses an encrypted document
        /// outright for exactly this reason.</para>
        /// </remarks>
        internal static PdfAcroForm ResolveAcroForm(PdfDocument doc)
        {
            if (doc == null) return null;
            if (doc.AcroForm != null) return doc.AcroForm;

            var catalog = doc.ResolveObject(doc.Trailer?[PdfName.Root]) as PdfDictionary;
            if (catalog == null) return null;
            var acroDict = doc.ResolveObject(catalog[PdfName.AcroForm]) as PdfDictionary;
            return acroDict == null ? null : new PdfAcroForm(acroDict, doc);
        }

        /// <summary>
        /// Every signature in the form, once each, paired with its signature dictionary (<c>/V</c>).
        /// An unsigned signature <i>field</i> — a placeholder with no <c>/V</c> — is not a signature and
        /// is skipped.
        /// </summary>
        /// <remarks>
        /// <para><b>Why both filters exist, and what each one actually catches.</b> A field's widget
        /// kids inherit <c>/FT</c> through the <c>/Parent</c> chain (§12.7.3.1, Table 220), and
        /// <c>PdfFormField.Create</c> resolves it that way — so <c>GetSignatureFields</c> hands the
        /// widget back as a <see cref="PdfSignatureField"/> too, one per widget.
        /// <see cref="PdfSignatureField.SignatureValue"/> reads <c>/V</c> from the node's <i>own</i>
        /// dictionary (the spec makes <c>/V</c> inheritable, but a widget showing its parent's value is
        /// not a second signature), so the widget's is null — and the null check is what keeps a
        /// value-less node out of the count. The identity set behind it catches the other shape: two
        /// field nodes that each name the same <c>/V</c> object, which would otherwise make a
        /// two-signature document report as a three-signature one.</para>
        ///
        /// <para>Both were mutation-checked rather than assumed. Removing the null check hands every
        /// caller a null signature: the <c>/ByteRange</c> readers now tolerate one, so the Base reports
        /// no longer see the difference, but the LTV refresher reads <c>/Contents</c> from it —
        /// <c>AWidgetKidWithNoValueOfItsOwnIsNotASignature</c> is the test that does. Removing the
        /// identity set doubles a signature whose widget repeats its <c>/V</c>. Neither is redundant,
        /// and each has a test of its own.</para>
        /// </remarks>
        internal static IEnumerable<(PdfSignatureField Field, PdfDictionary Signature)> DistinctSignatures(
            PdfAcroForm acro)
        {
            if (acro == null) yield break;

            // Reference identity: the parser caches one instance per object number, so two references
            // to the same /V resolve to the same instance.
            var seen = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            foreach (var field in acro.GetSignatureFields())
            {
                var sig = field.SignatureValue;
                if (sig == null) continue;
                if (!seen.Add(sig)) continue;
                yield return (field, sig);
            }
        }
    }
}
