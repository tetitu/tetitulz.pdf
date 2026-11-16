// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Builds a signature field seed-value dictionary (PDF 32000-1:2008 §12.7.4.3, Table 234).
    /// </summary>
    /// <remarks>
    /// <para><b>Scoped to the <c>/DigestMethod</c> entry only, deliberately.</b> Table 234 has a
    /// dozen entries (<c>/Filter</c>, <c>/SubFilter</c>, <c>/V</c>, <c>/Cert</c>, <c>/Reasons</c>,
    /// <c>/MDP</c>, <c>/TimeStamp</c>, <c>/LegalAttestation</c>, <c>/AddRevInfo</c> among them);
    /// this covers the one this library's own signing pipeline can act on today — ISO/TS 32001
    /// extended the set of acceptable names <c>/DigestMethod</c> may list (adding
    /// <c>SHA3-256</c>/<c>SHA3-384</c>/<c>SHA3-512</c>/<c>SHAKE256</c> to the base standard's
    /// <c>SHA1</c>/<c>SHA256</c>/<c>SHA384</c>/<c>SHA512</c>/<c>RIPEMD160</c>), which is exactly
    /// what <see cref="Core.Constants.PdfDigestOids"/> already models. Building a constraint no
    /// signer here reads would be dead weight; the other entries are left for whenever a caller
    /// needs them.</para>
    /// </remarks>
    public static class SignatureSeedValue
    {
        /// <summary>
        /// Bit 7 (1-based, so value <c>1 &lt;&lt; 6</c> = 64) of the seed-value dictionary's
        /// <c>/Ff</c> entry — "the <c>/DigestMethod</c> entry is a required constraint" (Table 234).
        /// </summary>
        private const int DigestMethodRequiredFlag = 1 << 6;

        /// <summary>
        /// Builds a seed-value dictionary constraining which digest algorithms a signer may use,
        /// per Table 234's <c>/DigestMethod</c> entry.
        /// </summary>
        /// <param name="digestMethodNames">
        /// Acceptable digest algorithm names, in preference order — e.g.
        /// <see cref="Core.Constants.PdfDigestOids.Sha256Name"/>. Not validated against a fixed
        /// vocabulary: Table 234's base set predates ISO/TS 32001's SHA-3/SHAKE256 additions, and a
        /// future extension should not need a matching change here.
        /// </param>
        /// <param name="required">
        /// When true, sets the <c>/Ff</c> bit that makes <c>/DigestMethod</c> a required rather
        /// than an optional constraint — per Table 234, a conforming signer must then use one of
        /// the listed names or refuse to sign, rather than merely preferring one.
        /// </param>
        public static PdfDictionary BuildDigestMethodDictionary(
            IReadOnlyList<string> digestMethodNames, bool required = false)
        {
            if (digestMethodNames == null) throw new ArgumentNullException(nameof(digestMethodNames));
            if (digestMethodNames.Count == 0)
                throw new ArgumentException(
                    "At least one digest method name is required.", nameof(digestMethodNames));

            var names = new PdfArray();
            foreach (string name in digestMethodNames)
            {
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException(
                        "Digest method names must not be null or empty.", nameof(digestMethodNames));
                names.Add(new PdfName(name));
            }

            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.SV);
            dict.Set(PdfName.DigestMethod, names);
            if (required) dict.Set(PdfName.Ff, new PdfNumber(DigestMethodRequiredFlag));
            return dict;
        }
    }
}
