// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Whether a CRL's <b>scope</b> lets absence from it mean "not revoked" for a given certificate
    /// — RFC 5280 §6.3.3 steps (b) and (c), the part of CRL processing that comes after the issuer
    /// and the signature.
    /// </summary>
    /// <remarks>
    /// <para><b>An issuer-signed, current CRL that lists nothing is not evidence of anything unless
    /// it is the CRL for this certificate.</b> A CA may publish several: one for its CA
    /// certificates and one for end-entity certificates (<c>onlyContainsCACerts</c> /
    /// <c>onlyContainsUserCerts</c>), one per distribution point, one per set of reasons
    /// (<c>onlySomeReasons</c>), a delta against a base it does not contain, or an indirect CRL
    /// whose entries name other issuers. Each is genuine, verifies under the issuer's key and is
    /// current — and says nothing about a certificate outside its partition. A verifier that reads
    /// absence from any of them as <c>Good</c> lets whoever controls the <c>/DSS</c> choose the
    /// partition that does not list the certificate, which is a revocation bypass using nothing
    /// but the CA's own published data.</para>
    /// <para><b>One answer for both sides.</b> <c>RevocationChecker</c> asks it before reading
    /// absence as <c>Good</c>; <c>DssBuilder</c> asks it before embedding a fetched CRL that would
    /// then fail to validate. A CRL that <i>lists</i> the certificate is outside this question —
    /// a listing is revocation whatever the partition, and both callers check that first.</para>
    /// </remarks>
    internal static class CrlScope
    {
        /// <summary>
        /// Why <paramref name="crl"/> cannot vouch that <paramref name="cert"/> is unrevoked, or
        /// <see langword="null"/> when it can — the CRL being its issuer's, signature-verified and
        /// current is the caller's business.
        /// </summary>
        internal static string? WhyItCannotVouchFor(X509Crl crl, X509Certificate cert)
        {
            if (crl == null) throw new ArgumentNullException(nameof(crl));
            if (cert == null) throw new ArgumentNullException(nameof(cert));

            // §5.2.4: a delta CRL lists the changes since a base CRL; what it does not list may be
            // revoked in the base, which this CRL does not carry.
            if (crl.GetExtensionValue(X509Extensions.DeltaCrlIndicator) != null)
                return "is a delta CRL (RFC 5280 §5.2.4), which lists only the changes since a base CRL it does not carry";

            Asn1OctetString idpValue = crl.GetExtensionValue(X509Extensions.IssuingDistributionPoint);
            if (idpValue == null) return null;

            IssuingDistributionPoint idp;
            try
            {
                idp = IssuingDistributionPoint.GetInstance(X509ExtensionUtilities.FromExtensionValue(idpValue));
            }
            catch (Exception ex)
            {
                // The extension is critical (§5.2.5) and unreadable: a CRL whose scope cannot be
                // established cannot be shown to cover anything.
                return "carries an issuing distribution point that could not be read: " + ex.Message;
            }

            // §5.2.5: the two flags partition by certificate kind; the certificate's own
            // basicConstraints says which kind it is (-1 is "not a CA", BouncyCastle's reading).
            bool isCa = cert.GetBasicConstraints() >= 0;
            if (idp.OnlyContainsUserCerts && isCa)
                return "covers end-entity certificates only (onlyContainsUserCerts), and this certificate is a CA";
            if (idp.OnlyContainsCACerts && !isCa)
                return "covers CA certificates only (onlyContainsCACerts), and this certificate is not a CA";

            // §6.3.3 (b)(2)(iii): the reasons a CRL covers must be the union of every reason before
            // absence means anything; a CRL for some reasons cannot be it on its own.
            if (idp.OnlySomeReasons != null)
                return "covers some revocation reasons only (onlySomeReasons)";

            // §6.3.3 (b)(2)(iv), (c): an indirect CRL's entries may name an issuer other than the
            // CRL's through the certificateIssuer entry extension, which this reader does not follow.
            if (idp.IsIndirectCrl)
                return "is an indirect CRL, whose entries may belong to another issuer";

            // §6.3.3 (b)(2)(i): a CRL scoped to a distribution point speaks for the certificates
            // that name that point.
            if (idp.DistributionPoint != null && !NamesADistributionPointOf(idp.DistributionPoint, cert))
                return "is scoped to a distribution point the certificate does not name";

            return null;
        }

        /// <summary>
        /// Whether one of the certificate's <c>cRLDistributionPoints</c> names <paramref name="idpName"/>:
        /// for a full name, any general name in common; for a name relative to the CRL issuer, the
        /// same relative name. A certificate with no distribution points names none.
        /// </summary>
        private static bool NamesADistributionPointOf(DistributionPointName idpName, X509Certificate cert)
        {
            Asn1OctetString value = cert.GetExtensionValue(X509Extensions.CrlDistributionPoints);
            if (value == null) return false;

            CrlDistPoint points;
            try
            {
                points = CrlDistPoint.GetInstance(X509ExtensionUtilities.FromExtensionValue(value));
            }
            catch (Exception)
            {
                return false;
            }

            foreach (DistributionPoint point in points.GetDistributionPoints())
            {
                DistributionPointName? name = point.DistributionPointName;
                if (name == null || name.Type != idpName.Type) continue;

                if (name.Type == DistributionPointName.FullName)
                {
                    foreach (GeneralName theirs in GeneralNames.GetInstance(idpName.Name).GetNames())
                        foreach (GeneralName ours in GeneralNames.GetInstance(name.Name).GetNames())
                            if (theirs.Equals(ours)) return true;
                }
                else if (name.Name.ToAsn1Object().Equals(idpName.Name.ToAsn1Object()))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
