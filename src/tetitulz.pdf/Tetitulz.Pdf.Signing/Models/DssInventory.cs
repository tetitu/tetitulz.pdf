// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Signing.Models
{
    /// <summary>Which kind of revocation evidence a <see cref="DssRevocationEntry"/> describes.</summary>
    public enum DssEvidenceKind
    {
        /// <summary>An OCSP response (RFC 6960).</summary>
        OcspResponse,

        /// <summary>A certificate revocation list (RFC 5280).</summary>
        Crl,
    }

    /// <summary>One piece of revocation evidence carried in a document's <c>/DSS</c>.</summary>
    /// <remarks>
    /// The timestamps are the issuer's own: <c>thisUpdate</c> is when the data was correct and
    /// <c>nextUpdate</c> when newer data is promised. Both may be absent — <c>nextUpdate</c> is
    /// optional for a CRL (RFC 5280 §5.1.2.5) and for an OCSP response (RFC 6960 §4.2.2.1) — and an
    /// entry that could not be parsed at all reports neither, with the reason in
    /// <see cref="Issue"/>.
    /// </remarks>
    public sealed class DssRevocationEntry
    {
        /// <summary>Creates an entry.</summary>
        public DssRevocationEntry(DssEvidenceKind kind, int byteLength,
                                  DateTime? thisUpdate, DateTime? nextUpdate,
                                  bool isCurrent, string? issue)
        {
            Kind = kind;
            ByteLength = byteLength;
            ThisUpdate = thisUpdate;
            NextUpdate = nextUpdate;
            IsCurrent = isCurrent;
            Issue = issue;
        }

        /// <summary>OCSP response or CRL.</summary>
        public DssEvidenceKind Kind { get; }

        /// <summary>Size of the embedded DER bytes, so a caller can spot a truncated entry.</summary>
        public int ByteLength { get; }

        /// <summary>When the issuer says this data was correct, or null if it could not be read.</summary>
        public DateTime? ThisUpdate { get; }

        /// <summary>When newer data is promised, or null when the issuer gave none.</summary>
        public DateTime? NextUpdate { get; }

        /// <summary>
        /// Whether this evidence still stands as current, by the same rule the signer applies when
        /// deciding what to embed.
        /// </summary>
        public bool IsCurrent { get; }

        /// <summary>Why it is not current, or why it could not be read. Null when it is fine.</summary>
        public string? Issue { get; }
    }

    /// <summary>
    /// What revocation evidence a document's <c>/DSS</c> actually carries, and whether it is stale.
    /// </summary>
    /// <remarks>
    /// <para><b>This answers a different question from verification.</b>
    /// <c>SignatureVerificationResult</c> reports a <i>verdict</i> computed over the <c>/DSS</c> and
    /// never enumerates it, so a caller could not tell whether a document's LTV evidence needed
    /// refreshing — only whether the signature verified today. That is the question a PAdES
    /// B-LT/B-LTA archival cycle turns on.</para>
    ///
    /// <para><b>Currency is evaluated against a caller-supplied instant</b> so the answer is
    /// reproducible and testable; there is no hidden clock.</para>
    /// </remarks>
    public sealed class DssInventory
    {
        /// <summary>Creates an inventory.</summary>
        public DssInventory(bool hasDss, int certificateCount,
                            IReadOnlyList<DssRevocationEntry> ocspResponses,
                            IReadOnlyList<DssRevocationEntry> crls,
                            IReadOnlyList<string> vriKeys,
                            DateTime evaluatedAtUtc)
            : this(hasDss, certificateCount, ocspResponses, crls, vriKeys,
                   Array.Empty<DssVriEvidence>(), evaluatedAtUtc)
        {
        }

        /// <summary>
        /// Creates an inventory that also carries the evidence held under each <c>/VRI</c> key.
        /// </summary>
        /// <remarks>
        /// <b>A separate constructor rather than an optional parameter</b>, because C# bakes an
        /// optional argument's default in at the <i>call site</i>: widening the shipped signature
        /// above would be a binary break for an already-compiled consumer. Same rule as the
        /// password overloads on <c>PdfOperations</c>.
        /// </remarks>
        public DssInventory(bool hasDss, int certificateCount,
                            IReadOnlyList<DssRevocationEntry> ocspResponses,
                            IReadOnlyList<DssRevocationEntry> crls,
                            IReadOnlyList<string> vriKeys,
                            IReadOnlyList<DssVriEvidence> vriEvidence,
                            DateTime evaluatedAtUtc)
        {
            HasDss = hasDss;
            CertificateCount = certificateCount;
            OcspResponses = ocspResponses ?? Array.Empty<DssRevocationEntry>();
            Crls = crls ?? Array.Empty<DssRevocationEntry>();
            VriKeys = vriKeys ?? Array.Empty<string>();
            VriEvidence = vriEvidence ?? Array.Empty<DssVriEvidence>();
            EvaluatedAtUtc = evaluatedAtUtc;
        }

        /// <summary>
        /// Whether the catalog has a <c>/DSS</c> at all. <c>false</c> means the document carries no
        /// LTV evidence — distinct from a <c>/DSS</c> that is present and empty, which is a
        /// producer that started the job and did not finish it.
        /// </summary>
        public bool HasDss { get; }

        /// <summary>How many certificates <c>/DSS /Certs</c> holds.</summary>
        public int CertificateCount { get; }

        /// <summary>
        /// The OCSP responses in the <b>document-wide</b> <c>/OCSPs</c> list.
        /// </summary>
        /// <remarks>
        /// <b>Evidence reachable only through <c>/VRI</c> is not included, and that is a real
        /// limitation.</b> ISO 32000-2 §12.8.4.3 lets a <c>/DSS</c> scope revocation data to one
        /// signature under a <c>/VRI</c> key, and this library's own verifier treats such an entry
        /// as <i>authoritative</i> for that signature — so a document whose LTV data lives only
        /// under <c>/VRI</c> reports empty lists here. <see cref="VriKeys"/> is what says such
        /// entries exist. Reading them would mean deciding which signature the caller is asking
        /// about, which this inventory deliberately does not do.
        /// </remarks>
        public IReadOnlyList<DssRevocationEntry> OcspResponses { get; }

        /// <summary>The CRLs in the document-wide <c>/CRLs</c> list; see <see cref="OcspResponses"/> on <c>/VRI</c>.</summary>
        public IReadOnlyList<DssRevocationEntry> Crls { get; }

        /// <summary>
        /// The <c>/VRI</c> keys present — the uppercase hex SHA-1 of each signature's
        /// <c>/Contents</c> that has signature-specific evidence (ISO 32000-2 §12.8.4.3).
        /// </summary>
        public IReadOnlyList<string> VriKeys { get; }

        /// <summary>
        /// What each <c>/VRI</c> key actually holds — the evidence scoped to that one signature.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the detail <see cref="VriKeys"/> could only point at.</b> A document
        /// whose LTV evidence is entirely <c>/VRI</c>-scoped reports empty
        /// <see cref="OcspResponses"/> and <see cref="Crls"/> and <see cref="IsCurrent"/> false; a
        /// caller then had to walk the <c>/DSS</c> itself to find out whether a refresh was
        /// actually needed. Now it can read it here, keyed by signature.</para>
        ///
        /// <para><b><see cref="IsCurrent"/> still judges the document-wide lists only</b>, and is
        /// deliberately unchanged: it is a published answer, and folding <c>/VRI</c> into it would
        /// silently redefine what a <c>false</c> means for every existing caller. Check both — which
        /// the remarks on <see cref="IsCurrent"/> have always said, and which this member is what
        /// makes possible.</para>
        /// </remarks>
        public IReadOnlyList<DssVriEvidence> VriEvidence { get; }

        /// <summary>The instant currency was judged against.</summary>
        public DateTime EvaluatedAtUtc { get; }

        /// <summary>
        /// True when the document carries evidence and <b>all</b> of it is current. False when any
        /// entry is stale or unreadable.
        /// </summary>
        /// <remarks>
        /// <b>Judged over the document-wide lists only</b>, so a document whose evidence is
        /// <c>/VRI</c>-scoped reports <c>false</c> with a non-empty <see cref="VriKeys"/> — check
        /// both before concluding a refresh is needed.
        /// <para>A document with a <c>/DSS</c> but no OCSP or CRL entries is <b>not</b> current: there is
        /// nothing standing behind the signature, which is the case a refresh exists to fix.
        /// Reporting it as current because nothing failed would be the emptiest kind of pass.</para>
        /// </remarks>
        public bool IsCurrent
        {
            get
            {
                if (OcspResponses.Count == 0 && Crls.Count == 0) return false;
                foreach (var e in OcspResponses) if (!e.IsCurrent) return false;
                foreach (var e in Crls) if (!e.IsCurrent) return false;
                return true;
            }
        }
    }

    /// <summary>
    /// The revocation evidence a <c>/DSS /VRI</c> entry holds for <b>one</b> signature
    /// (ISO 32000-2 §12.8.4.3).
    /// </summary>
    /// <remarks>
    /// <para><b>Reporting these decides nothing, which is why they can be reported.</b> The
    /// objection recorded against reading <c>/VRI</c> was that it would mean choosing which
    /// signature the caller is asking about — true of a single flat list, and not true of a list
    /// keyed by signature. <see cref="Key"/> <i>is</i> the signature's identity: the uppercase hex
    /// SHA-1 of its <c>/Contents</c>. The caller still chooses; it now has something to choose
    /// from.</para>
    ///
    /// <para><b>The keys inside a <c>/VRI</c> entry are singular</b> — <c>/OCSP</c> and <c>/CRL</c>,
    /// not the document-wide <c>/OCSPs</c> and <c>/CRLs</c>. Reading it with the plural names finds
    /// nothing and reports an entry with no evidence, which is indistinguishable from a document
    /// that has none. <c>RevocationChecker.SelectSource</c> has always known this; it is the one
    /// detail this type had to get right to exist at all.</para>
    /// </remarks>
    public sealed class DssVriEvidence
    {
        /// <summary>Creates an entry.</summary>
        public DssVriEvidence(string key, int certificateCount,
                              IReadOnlyList<DssRevocationEntry> ocspResponses,
                              IReadOnlyList<DssRevocationEntry> crls)
        {
            Key = key ?? string.Empty;
            CertificateCount = certificateCount;
            OcspResponses = ocspResponses ?? Array.Empty<DssRevocationEntry>();
            Crls = crls ?? Array.Empty<DssRevocationEntry>();
        }

        /// <summary>
        /// The <c>/VRI</c> key: the uppercase hex SHA-1 of the signature's <c>/Contents</c>, which
        /// is how a caller matches this evidence to a signature.
        /// </summary>
        public string Key { get; }

        /// <summary>How many certificates this entry's <c>/Cert</c> array holds.</summary>
        public int CertificateCount { get; }

        /// <summary>The OCSP responses scoped to this signature, from <c>/OCSP</c>.</summary>
        public IReadOnlyList<DssRevocationEntry> OcspResponses { get; }

        /// <summary>The CRLs scoped to this signature, from <c>/CRL</c>.</summary>
        public IReadOnlyList<DssRevocationEntry> Crls { get; }

        /// <summary>
        /// True when this entry carries evidence and <b>all</b> of it is current — the same rule
        /// <see cref="DssInventory.IsCurrent"/> applies to the document-wide lists.
        /// </summary>
        public bool IsCurrent
        {
            get
            {
                if (OcspResponses.Count == 0 && Crls.Count == 0) return false;
                foreach (var e in OcspResponses) if (!e.IsCurrent) return false;
                foreach (var e in Crls) if (!e.IsCurrent) return false;
                return true;
            }
        }
    }
}