// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Which <c>SingleResponse</c> of an OCSP response is <b>about</b> a certificate, whether the
    /// response was signed by a responder that may speak for the certificate's issuer, and whether a
    /// delegated responder's certificate may sign a response relied on at a time — RFC 6960 §4.2.2.2,
    /// asked by the verifier and the LTV embedder alike.
    /// </summary>
    /// <remarks>
    /// <para><b>A well-signed, current response about another certificate is no evidence for this
    /// one.</b> The verifier matches the response to the certificate before reading its status, and
    /// the embedder before writing it: a responder answering about a different certificate — or about
    /// the same serial under another CA, serials being unique only within an issuer — must not have
    /// its response written into <c>/DSS</c> with <c>TryBuildDss</c> reporting revocation data
    /// obtained, a document claiming evidence it does not hold. One matcher, on both sides.</para>
    /// <para><b>The responder's own certificate has a validity period.</b> §4.2.2.2 accepts a delegated
    /// responder whose certificate the CA issued with the OCSP-signing purpose; that certificate is
    /// validated as any other (RFC 5280 §6.1.3 (a)(2): the time is within its period), beyond its
    /// anchor to the issuer and its purpose.</para>
    /// </remarks>
    internal static class OcspSingleResponse
    {
        /// <summary>
        /// The <c>SingleResponse</c> whose <c>CertID</c> names <paramref name="subject"/>: its serial
        /// and — when the issuer is known — the issuer's name and key hashes, re-derived under the
        /// <c>CertID</c>'s own hash algorithm. <see langword="null"/> when the response is about other
        /// certificates, or when no <c>CertID</c> can be read.
        /// </summary>
        internal static SingleResp? Find(BasicOcspResp basic, X509Certificate subject, X509Certificate? issuer)
        {
            if (basic == null) throw new ArgumentNullException(nameof(basic));
            if (subject == null) throw new ArgumentNullException(nameof(subject));

            SingleResp[] responses;
            try { responses = basic.Responses; }
            catch { return null; }
            if (responses == null) return null;

            foreach (var sr in responses)
            {
                CertificateID? certId;
                try { certId = sr?.GetCertID(); }
                catch { continue; }
                if (certId?.SerialNumber == null) continue;
                if (!certId.SerialNumber.Equals(subject.SerialNumber)) continue;
                if (issuer != null && !MatchesIssuer(certId, issuer)) continue;
                return sr;
            }
            return null;
        }

        private static bool MatchesIssuer(CertificateID certId, X509Certificate issuer)
        {
            try { return certId.MatchesIssuer(issuer); }
            catch { return false; } // an unknown hash algorithm in the CertID: cannot confirm
        }

        /// <summary>
        /// Why <paramref name="basic"/> was not signed by a responder that may speak for
        /// <paramref name="issuer"/>'s certificates at <paramref name="at"/> (RFC 6960 §4.2.2.2), or
        /// <see langword="null"/> when it was: the issuer itself, or a delegated responder whose
        /// certificate travels in the response, was issued by the issuer, carries id-kp-OCSPSigning
        /// and is within its validity period.
        /// </summary>
        /// <remarks>
        /// <para><b>The two callers chose the responder differently.</b> The verifier tried the
        /// issuer's key first and passed over an embedded certificate it could not anchor; the
        /// embedder took the first embedded certificate whose key verified and refused on it. One
        /// search now: the issuer first — the common case, a response with no certificates — then
        /// each certificate the response carries, the reason for the last one refused kept.</para>
        /// <para>The reason is a predicate ("is signed by …") for the caller to prefix.</para>
        /// </remarks>
        internal static string? WhyTheResponderCannotVouch(
            BasicOcspResp basic, X509Certificate? issuer, DateTime at)
        {
            if (basic == null) throw new ArgumentNullException(nameof(basic));
            if (issuer == null)
                return "cannot be anchored: no issuer certificate is available to check its responder against.";

            try { if (basic.Verify(PublicKeyCost.KeyOf(issuer))) return null; }
            catch { /* the embedded certificates below */ }

            X509Certificate[] carried;
            try { carried = basic.GetCerts() ?? Array.Empty<X509Certificate>(); }
            catch (Exception ex) { return "carries certificates that cannot be read: " + ex.Message; }

            string? refused = null;
            foreach (var candidate in carried)
            {
                if (candidate == null) continue;
                bool signedIt;
                try { signedIt = basic.Verify(PublicKeyCost.KeyOf(candidate)); }
                catch { signedIt = false; }
                if (!signedIt) continue;

                if (candidate.Equals(issuer)) return null; // the issuer, carried as its own certificate

                try { candidate.Verify(PublicKeyCost.KeyOf(issuer)); }
                catch (Exception ex)
                {
                    refused = $"is signed by '{candidate.SubjectDN}', which the certificate's issuer " +
                              $"did not issue: {ex.Message}";
                    continue;
                }

                if (!HasOcspSigningPurpose(candidate))
                {
                    refused = $"is signed by the delegated responder '{candidate.SubjectDN}', which " +
                              "lacks the id-kp-OCSPSigning EKU (RFC 6960 §4.2.2.2).";
                    continue;
                }

                string? cannotSign = WhyADelegatedResponderCannotSign(candidate, at);
                if (cannotSign != null)
                {
                    refused = $"is signed by the delegated responder '{candidate.SubjectDN}', which " +
                              $"cannot sign: {cannotSign}.";
                    continue;
                }
                return null;
            }

            return refused
                ?? "has a signature that verifies under neither the issuer's key nor any certificate it carries.";
        }

        private static bool HasOcspSigningPurpose(X509Certificate certificate)
        {
            try
            {
                var purposes = certificate.GetExtendedKeyUsage();
                if (purposes == null) return false;
                foreach (var oid in purposes)
                    if (oid?.ToString() == PdfEkuOids.OcspSigning) return true;
            }
            catch { /* an unreadable extension grants nothing */ }
            return false;
        }

        /// <summary>
        /// Why <paramref name="responder"/>, a delegated responder's certificate already anchored to
        /// the issuer and carrying the OCSP-signing purpose, cannot sign a response relied on at
        /// <paramref name="at"/> — outside its validity period — or <see langword="null"/> when it can.
        /// </summary>
        internal static string? WhyADelegatedResponderCannotSign(X509Certificate responder, DateTime at)
        {
            if (responder == null) throw new ArgumentNullException(nameof(responder));

            DateTime notBefore, notAfter;
            try
            {
                notBefore = responder.NotBefore.ToUniversalTime();
                notAfter = responder.NotAfter.ToUniversalTime();
            }
            catch (Exception ex) { return "its validity period cannot be read: " + ex.Message; }

            DateTime t = at.ToUniversalTime();
            if (t < notBefore) return $"it is not valid before {notBefore:u} (assessed at {t:u})";
            if (t > notAfter) return $"it expired at {notAfter:u} (assessed at {t:u})";
            return null;
        }
    }
}
