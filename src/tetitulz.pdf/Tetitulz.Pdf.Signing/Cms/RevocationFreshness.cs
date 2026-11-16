// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Whether revocation data — an OCSP response (RFC 6960 §3.1.2, §4.2.2.1) or a CRL (RFC 5280
    /// §5.1.2.4–5.1.2.5) — is <b>current</b> at a time: the verifier reading a <c>/DSS</c> and the
    /// LTV embedder choosing what to write into one alike.
    /// </summary>
    /// <remarks>
    /// <para><b>One rule, so the embedder never writes what the verifier will not read.</b> Two
    /// copies would disagree: an embedder with a fixed skew, no age cap for a CRL without
    /// <c>nextUpdate</c> and no reading of <c>producedAt</c> would embed a two-year-old CRL with no
    /// <c>nextUpdate</c>, <c>TryBuildDss</c> would report revocation data obtained, and default
    /// verification of the same document would read the CRL as <c>Unknown</c>.</para>
    ///
    /// <para><b>One rule, two sets of limits.</b> The verifier passes its options' skew and age cap;
    /// the embedder passes the verifier's defaults, and for an OCSP response without
    /// <c>nextUpdate</c> its own tighter cap — tighter is safe, since everything it then embeds the
    /// verifier also reads; looser would be the disagreement above.</para>
    ///
    /// <para>Deliberately a leaf with no static fields: the defaults are computed properties, so
    /// neither caller gains a type-initialization edge (CLAUDE.md, "share downward into a leaf").</para>
    /// </remarks>
    internal static class RevocationFreshness
    {
        /// <summary>
        /// The tolerance on every comparison, absorbing clock differences between the responder and
        /// this host — the default of <c>SignatureVerificationOptions.ClockSkew</c> and the
        /// embedder's own.
        /// </summary>
        internal static TimeSpan DefaultClockSkew => TimeSpan.FromMinutes(5);

        /// <summary>
        /// How old data with no <c>nextUpdate</c> may be — the default of
        /// <c>SignatureVerificationOptions.MaxRevocationDataAge</c>, and the cap the embedder applies
        /// to a CRL. A policy figure, not a specification: RFC 5280 makes a CRL's <c>nextUpdate</c>
        /// optional and gives no bound.
        /// </summary>
        internal static TimeSpan DefaultMaxAgeWithoutNextUpdate => TimeSpan.FromDays(365);

        /// <summary>
        /// Why data dated <paramref name="thisUpdate"/> / <paramref name="nextUpdate"/> (and, for an
        /// OCSP response, <paramref name="producedAt"/>) cannot stand as current at
        /// <paramref name="at"/>, or <see langword="null"/> when it can. The reason is a predicate
        /// for the caller to prefix with what was judged ("OCSP response", "CRL for serial 12").
        /// </summary>
        /// <param name="thisUpdate">When the issuer or responder says the data was correct.</param>
        /// <param name="nextUpdate">When newer data will be available, or null if none was given.</param>
        /// <param name="producedAt">An OCSP response's <c>producedAt</c>; null for a CRL.</param>
        /// <param name="at">The time the data is judged at.</param>
        /// <param name="clockSkew">The tolerance on the three time comparisons.</param>
        /// <param name="maxAgeWithoutNextUpdate">
        /// How old data with no <c>nextUpdate</c> may be; <see cref="TimeSpan.Zero"/> or less switches
        /// the cap off, as <c>MaxRevocationDataAge</c> documents.
        /// </param>
        /// <remarks>
        /// Every comparison is a difference against a bound rather than a bound moved by a span, so
        /// an option set to <see cref="TimeSpan.MaxValue"/> caps nothing instead of throwing from
        /// <see cref="DateTime.Subtract(TimeSpan)"/>.
        /// </remarks>
        internal static string? WhyNotCurrent(
            DateTime thisUpdate, DateTime? nextUpdate, DateTime? producedAt, DateTime at,
            TimeSpan clockSkew, TimeSpan maxAgeWithoutNextUpdate)
        {
            if (thisUpdate - at > clockSkew)
                return $"is dated in the future (thisUpdate {thisUpdate:u} > {at:u}); possible clock skew or forgery.";

            if (nextUpdate.HasValue && at - nextUpdate.Value > clockSkew)
                return $"is stale: it expired at {nextUpdate.Value:u}, before {at:u}.";

            if (!nextUpdate.HasValue && maxAgeWithoutNextUpdate > TimeSpan.Zero
                && at - thisUpdate > maxAgeWithoutNextUpdate)
                return $"has no nextUpdate and its thisUpdate ({thisUpdate:u}) is older than " +
                       $"{maxAgeWithoutNextUpdate.TotalDays:0} days, so it cannot stand as current " +
                       "revocation evidence.";

            if (producedAt.HasValue && producedAt.Value - at > clockSkew)
                return $"was produced in the future (producedAt {producedAt.Value:u} > {at:u}).";

            return null;
        }
    }
}
