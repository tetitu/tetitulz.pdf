// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Signing.Models
{
    /// <summary>
    /// How <see cref="PdfLtvRefresher.RefreshVerificationInfo"/> obtains the evidence it appends.
    /// </summary>
    /// <remarks>
    /// <para><b>The default fetches over the network, and that is why this type exists.</b>
    /// <c>PdfLtvRefresher.AddVerificationInfo</c> takes evidence and never reaches a responder,
    /// deliberately — it is the half that can be tested without a network and used behind a proxy.
    /// The convenience overload had to keep that property rather than trade it away, so the fetch
    /// is a *replaceable* step rather than a hard-wired one.</para>
    ///
    /// <para>Two callers want to replace it for reasons that have nothing to do with testing: a
    /// deployment whose OCSP and CRL traffic goes through an authenticated proxy (this assembly has
    /// no proxy seam of its own), and an air-gapped one that takes evidence from a local store.</para>
    /// </remarks>
    public sealed class LtvRefreshOptions
    {
        /// <summary>
        /// Supplies the evidence for one signer. Called once per signature found in the document,
        /// with that signature's own certificate and the chain its CMS carried.
        /// </summary>
        /// <remarks>
        /// <para><b>Null means "fetch"</b> — the default walks the chain through
        /// <c>DssBuilder.TryBuildDss</c>, which contacts each certificate's OCSP responder and
        /// falls back to its CRL distribution points. Outbound revocation fetching is a
        /// network-egress decision; see the repository's security policy before relying on it.</para>
        ///
        /// <para><b>A provider's own diagnostics are its own.</b> The warnings on
        /// <see cref="LtvRefreshResult"/> come from the default fetcher; a replacement that wants to
        /// report something should do so through its own channel, because this delegate returns
        /// evidence and nothing else.</para>
        ///
        /// <para>Returning <c>null</c> or empty evidence for a signer is not an error: a signature
        /// whose responder is unreachable simply contributes nothing, and the refresh still appends
        /// whatever the other signatures yielded.</para>
        /// </remarks>
        public Func<X509Certificate, IReadOnlyList<X509Certificate>, LtvEvidence>? EvidenceProvider { get; set; }

        /// <summary>
        /// Whether to include each signer's own certificate chain in the appended <c>/DSS /Certs</c>.
        /// Default <c>true</c>.
        /// </summary>
        /// <remarks>
        /// A verifier that cannot reach the issuing CA needs the chain in the document, which is the
        /// point of a long-term-validation store. Turn it off only when the chain is known to be in
        /// the <c>/DSS</c> already and the document's size matters more.
        /// </remarks>
        public bool IncludeSignerChains { get; set; } = true;

        /// <summary>
        /// Credentials, proxy and timeout for the <b>default</b> fetcher's OCSP and CRL requests.
        /// Ignored when <see cref="EvidenceProvider"/> is supplied, which does its own fetching.
        /// </summary>
        /// <remarks>
        /// The two settings answer the same need at different depths: a host that only needs its
        /// proxy honoured sets this and keeps the built-in walk, with its private-address guard and
        /// its hand-followed redirects; a host that needs to replace the source entirely supplies
        /// an <see cref="EvidenceProvider"/> and takes both on itself.
        /// </remarks>
        public SigningNetworkOptions? Network { get; set; }
    }
}
