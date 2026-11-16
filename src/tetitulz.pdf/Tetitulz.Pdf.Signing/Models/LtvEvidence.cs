// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Signing.Models
{
    /// <summary>
    /// Revocation evidence to append to a document's <c>/DSS</c>, and what happened when it was.
    /// </summary>
    /// <remarks>
    /// Evidence is supplied as raw DER rather than fetched by the writer, which is what makes the
    /// revision-writing half of an LTV refresh testable without a network and usable from a host
    /// that reaches its OCSP responder through a proxy this assembly has no seam for (GAPS §1a
    /// item 3). The convenience overload that fetches evidence itself does not exist yet — see GAPS §1a item 1.
    /// </remarks>
    public sealed class LtvEvidence
    {
        /// <summary>Creates an evidence set. Any list may be null or empty.</summary>
        public LtvEvidence(IReadOnlyList<byte[]>? ocspResponses = null,
                           IReadOnlyList<byte[]>? crls = null,
                           IReadOnlyList<byte[]>? certificates = null)
        {
            OcspResponses = ocspResponses ?? Array.Empty<byte[]>();
            Crls = crls ?? Array.Empty<byte[]>();
            Certificates = certificates ?? Array.Empty<byte[]>();
        }

        /// <summary>DER-encoded OCSP responses.</summary>
        public IReadOnlyList<byte[]> OcspResponses { get; }

        /// <summary>DER-encoded CRLs.</summary>
        public IReadOnlyList<byte[]> Crls { get; }

        /// <summary>DER-encoded certificates for <c>/DSS /Certs</c>.</summary>
        public IReadOnlyList<byte[]> Certificates { get; }

        /// <summary>True when there is nothing to append.</summary>
        public bool IsEmpty
            => OcspResponses.Count == 0 && Crls.Count == 0 && Certificates.Count == 0;
    }

    /// <summary>What an LTV refresh appended.</summary>
    public sealed class LtvRefreshResult
    {
        /// <summary>Creates a result.</summary>
        public LtvRefreshResult(int ocspAdded, int crlsAdded, int certificatesAdded,
                                int ocspCarriedForward, int crlsCarriedForward,
                                int certificatesCarriedForward,
                                IReadOnlyList<string> warnings)
        {
            OcspAdded = ocspAdded;
            CrlsAdded = crlsAdded;
            CertificatesAdded = certificatesAdded;
            OcspCarriedForward = ocspCarriedForward;
            CrlsCarriedForward = crlsCarriedForward;
            CertificatesCarriedForward = certificatesCarriedForward;
            Warnings = warnings ?? Array.Empty<string>();
        }

        /// <summary>OCSP responses written by this refresh.</summary>
        public int OcspAdded { get; }

        /// <summary>CRLs written by this refresh.</summary>
        public int CrlsAdded { get; }

        /// <summary>Certificates written by this refresh.</summary>
        public int CertificatesAdded { get; }

        /// <summary>
        /// OCSP responses that were already in the document and are still referenced.
        /// </summary>
        /// <remarks>
        /// <b>Carried forward, not rewritten.</b> The new <c>/DSS</c> references the existing
        /// stream objects by their original numbers; the bytes are not copied. A refresh that
        /// replaced the store instead of merging would destroy the evidence an earlier B-LT
        /// signature depends on — which is a defect this repository has shipped once, in
        /// <c>PdfCounterSigner</c>, and no test caught because the loss is an <i>absence</i>.
        /// </remarks>
        public int OcspCarriedForward { get; }

        /// <summary>CRLs already in the document and still referenced.</summary>
        public int CrlsCarriedForward { get; }

        /// <summary>Certificates already in the document and still referenced.</summary>
        public int CertificatesCarriedForward { get; }

        /// <summary>Anything tolerated rather than fatal.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }
}