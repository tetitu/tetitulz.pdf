// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// What the signatures a document already carries allow a new one to be — ISO 32000-2 §12.8.1's
    /// ordering of certification and approval signatures, and §12.8.2.2's DocMDP permissions.
    /// </summary>
    /// <remarks>
    /// <para><b>ISO 32000-2 §12.8.1:</b> a document may contain "at most one certification signature",
    /// and approval signatures "shall follow the certification signature if one is present". Document
    /// timestamps are "any number", in any position. <b>§12.8.2.2, Table 257:</b> under DocMDP
    /// <c>/P 1</c> "no changes to the document shall be permitted; any change to the document shall
    /// invalidate the signature" — and adding a DSS or a document timestamp is not a change.</para>
    ///
    /// <para><b>The signer checked none of it.</b> Certifying a document someone had already signed
    /// wrote a second certification, or a certification behind an approval, and signing a document
    /// certified with <c>/P 1</c> silently broke that certification — the verifier then reported the
    /// certifying signature invalid, which reads as the author's signature having failed rather than
    /// as this signature having broken it.</para>
    /// </remarks>
    internal static class SignatureOrdering
    {
        /// <summary>The certification and approval signatures a document already carries.</summary>
        internal readonly struct Existing
        {
            internal Existing(bool hasApproval, int certificationPermission)
            {
                HasApproval = hasApproval;
                CertificationPermission = certificationPermission;
            }

            /// <summary>At least one approval signature is present.</summary>
            internal bool HasApproval { get; }

            /// <summary>The certification's DocMDP <c>/P</c> (1–3), or 0 when the document is not certified.</summary>
            internal int CertificationPermission { get; }
        }

        /// <summary>
        /// Reads the signatures a document carries — through its <c>/Perms /DocMDP</c> and through every
        /// signature value in its form — classified as certification, approval or document timestamp.
        /// </summary>
        /// <param name="catalog">The document's catalog.</param>
        /// <param name="form">Its resolved <c>/AcroForm</c>, or <c>null</c>.</param>
        /// <param name="resolve">Follows an indirect reference.</param>
        internal static Existing Read(PdfDictionary catalog, PdfDictionary? form, Func<PdfObject?, PdfObject?> resolve)
        {
            Func<PdfObject, PdfObject> resolveCore = o => resolve(o)!;
            bool hasApproval = false;

            // /Perms /DocMDP names the certification signature directly (Table 263) — read the way the
            // DocMDP analysis reads it, default permission included.
            DocMdpAnalyzer.CertificationNamedByPerms(catalog, resolveCore, out int certification);

            // Where the analysis picks ONE certification to report on, this keeps the strictest of all
            // of them: it decides whether a new signature may be written, and breaking any certification
            // a document claims is the outcome it exists to prevent.
            var seen = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            SignatureFieldRegistration.Walk(form, resolve, (field, _, _) =>
            {
                if (!(resolve(field[PdfName.V]) is PdfDictionary signature)
                    || !DocMdpAnalyzer.IsSignatureValue(signature, resolveCore))
                    return;
                if (!seen.Add(signature) || DocMdpAnalyzer.IsDocumentTimestamp(signature, resolveCore))
                    return;

                int p = DocMdpAnalyzer.DocMdpPermissionOf(signature, resolveCore, out _);
                if (p == 0) hasApproval = true;
                else if (certification == 0 || p < certification) certification = p;
            });

            return new Existing(hasApproval, certification);
        }

        /// <summary>
        /// Refuses a signature the document's existing signatures do not allow, before anything is
        /// written.
        /// </summary>
        /// <exception cref="PdfSigningException">
        /// A certification of a document already carrying a certification or approval signature, or any
        /// signature on a document certified with <c>/P 1</c> unless
        /// <see cref="SignatureParameters.AllowCertificationInvalidation"/> is set.
        /// </exception>
        internal static void RefuseConflicts(Existing existing, SignatureParameters parameters)
            => RefuseConflicts(existing, parameters.DocMdpPermission, parameters.AllowCertificationInvalidation);

        /// <summary>
        /// <see cref="RefuseConflicts(Existing, SignatureParameters)"/> for the level the signature is
        /// actually made at — which a locked field's <c>/P</c> may have chosen (see
        /// <c>SignatureFieldLock.Reconcile</c>).
        /// </summary>
        internal static void RefuseConflicts(Existing existing, DocMdpPermission permission, bool allowCertificationInvalidation)
        {
            if (permission != DocMdpPermission.None)
            {
                if (existing.CertificationPermission > 0)
                    throw new PdfSigningException(
                        "The document is already certified, and ISO 32000-2 §12.8.1 allows at most one " +
                        "certification signature. Sign it with an approval signature instead " +
                        "(DocMdpPermission.None).");

                if (existing.HasApproval)
                    throw new PdfSigningException(
                        "The document already carries an approval signature, and ISO 32000-2 §12.8.1 " +
                        "requires approval signatures to follow the certification signature — a signed " +
                        "document cannot be certified afterwards. Certify it before anyone signs it.");
            }

            if (existing.CertificationPermission == 1 && !allowCertificationInvalidation)
                throw new PdfSigningException(
                    "The document is certified with DocMDP /P 1, which permits no changes (ISO 32000-2 " +
                    "§12.8.2.2), so a new signature would invalidate the certification. Set " +
                    "SignatureParameters.AllowCertificationInvalidation to sign it anyway.");
        }
    }
}
