// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Shared X.509 chain walking and trust-anchoring.
    /// <para>
    /// Shared so the RFC 3161 timestamp path reaches it too: a TSA certificate is anchored like a
    /// signer's, never honoured because a token embeds a self-signed "TSA" certificate.
    /// </para>
    /// </summary>
    internal static class CertificateChainValidator
    {
        /// <summary>Upper bound on chain length; also the loop/cycle guard.</summary>
        internal const int MaxChainHops = 16;

        /// <summary>
        /// How many candidate issuers one walk verifies a certificate against — the trust search and
        /// <see cref="BuildChain"/> alike — before it stops trying.
        /// </summary>
        /// <remarks>
        /// A bag's certificates may share a name at will, so the candidates at a hop are the bag's to
        /// choose, and <see cref="BuildChain"/> asks them again at every hop whose issuer has that
        /// name: hops × bag verifications, each up to what <see cref="PublicKeyCost"/> admits
        /// (40 ms for a 16 384-bit modulus). A genuine bag has a few candidates per hop — a
        /// cross-signed intermediate, a rollover.
        /// </remarks>
        internal const int MaxIssuerVerifications = 64;

        [ThreadStatic]
        private static int _issuerVerifications;

        /// <summary>How many candidate issuers this thread has verified a certificate against — the allowance's evidence.</summary>
        internal static int IssuerVerifications => _issuerVerifications;

        /// <summary>
        /// Walks <paramref name="leaf"/> up to a root, checking validity, KeyUsage and CA
        /// constraints at each hop, and reports whether the chain terminates in
        /// <paramref name="trustAnchors"/>.
        /// </summary>
        /// <param name="leaf">The end-entity certificate (signer or TSA).</param>
        /// <param name="certStore">Certificates accompanying the signature (the CMS cert bag).</param>
        /// <param name="trustAnchors">Caller-supplied anchors; null means nothing can be trusted.</param>
        /// <param name="validationTime">Time at which certificate validity is assessed.</param>
        /// <param name="warnings">Diagnostics sink.</param>
        /// <param name="leafRole">Label used in warning text ("Signer", "TSA", …).</param>
        /// <param name="requireDocumentSigningPurpose">
        /// Hold the leaf to <see cref="WhyThePurposeForbidsDocumentSigning"/>: a certificate whose
        /// extended key usage names purposes, none of them one a document is signed under, is
        /// <see cref="PdfSignatureChainStatus.Invalid"/>. A timestamp authority's leaf is held to
        /// its own purpose by its verifier and passes <c>false</c>.
        /// </param>
        public static PdfSignatureChainStatus Resolve(
            X509Certificate leaf,
            IStore<X509Certificate>? certStore,
            IEnumerable<X509Certificate>? trustAnchors,
            DateTime validationTime,
            List<string> warnings,
            string leafRole = "Signer",
            bool requireDocumentSigningPurpose = false)
            => Resolve(leaf, certStore, trustAnchors, validationTime, warnings, out _, leafRole, requireDocumentSigningPurpose);

        /// <summary>
        /// <see cref="Resolve(X509Certificate, IStore{X509Certificate}, IEnumerable{X509Certificate}, DateTime, List{string}, string, bool)"/>,
        /// also handing back the path it anchored.
        /// </summary>
        /// <param name="leaf">The end-entity certificate (signer or TSA).</param>
        /// <param name="certStore">Certificates accompanying the signature (the CMS cert bag).</param>
        /// <param name="trustAnchors">Caller-supplied anchors; null means nothing can be trusted.</param>
        /// <param name="validationTime">Time at which certificate validity is assessed.</param>
        /// <param name="warnings">Diagnostics sink.</param>
        /// <param name="leafRole">Label used in warning text ("Signer", "TSA", …).</param>
        /// <param name="requireDocumentSigningPurpose">Hold the leaf to the document-signing purposes, as the other overload does.</param>
        /// <param name="trustedPath">
        /// When the status is <see cref="PdfSignatureChainStatus.Trusted"/>, the path the walk anchored:
        /// the leaf first, each issuer whose key verified the certificate below it, the anchor last.
        /// Empty otherwise. Revocation is checked along this path: a separately built one follows the
        /// first certificate whose key verifies, which a bag can make a decoy (a certificate with the
        /// issuer's name and key under another root), so the issuer the relying party anchored through
        /// would never be asked about.
        /// </param>
        public static PdfSignatureChainStatus Resolve(
            X509Certificate leaf,
            IStore<X509Certificate>? certStore,
            IEnumerable<X509Certificate>? trustAnchors,
            DateTime validationTime,
            List<string> warnings,
            out List<X509Certificate> trustedPath,
            string leafRole = "Signer",
            bool requireDocumentSigningPurpose = false)
        {
            trustedPath = new List<X509Certificate>();
            if (WhyTheLeafIsRefused(leaf, validationTime, leafRole, requireDocumentSigningPurpose) is { } refused)
            {
                warnings.Add(refused);
                return PdfSignatureChainStatus.Invalid;
            }

            // Self-signed detection: issuer == subject and signature verifies against own key.
            if (leaf.IssuerDN.Equivalent(leaf.SubjectDN, true))
            {
                try
                {
                    leaf.Verify(PublicKeyCost.KeyOf(leaf));
                    if (trustAnchors != null && ContainsCert(trustAnchors, leaf))
                    {
                        trustedPath.Add(leaf);
                        return PdfSignatureChainStatus.Trusted;
                    }
                    return PdfSignatureChainStatus.SelfSigned;
                }
                catch (Exception ex)
                {
                    warnings.Add($"{leafRole} self-signature verification failed: {ex.Message}");
                    return PdfSignatureChainStatus.Invalid;
                }
            }

            // Walk up the chain: each cert's issuer is sought among the accompanying certificates
            // and the trust anchors — a CMS need not carry its root, and many do not — until we hit a
            // root, which is trusted only if it is in trustAnchors. Every issuer must be a valid CA
            // (basic constraints CA=true, path length, keyCertSign), carry no critical extension this
            // walk does not process, and admit the names of the certificates below it.
            //
            // The walk SEARCHES rather than commits. Several certificates in one bag can share a
            // SubjectDN: routinely during a CA root rollover (one intermediate cross-signed by the
            // legacy and the current root), and at will when the bag is attacker-supplied, since a
            // CMS certificate set is not authenticated. Taking the first DN match and never
            // backtracking therefore (a) reported UntrustedRoot for a chain that does anchor,
            // because the cross-signed copy leading to the anchored root was second in the bag, and
            // (b) let anyone turn a genuine signature Invalid by prepending a same-DN decoy whose
            // key does not verify the link - a remote denial of trust on any document.
            var visited = new HashSet<string>(StringComparer.Ordinal) { Fingerprint(leaf) };
            int[] budget = { MaxChainHops, MaxIssuerVerifications };
            var pathWarnings = new List<string>();
            var path = new List<X509Certificate> { leaf };
            var anchored = new List<X509Certificate>();
            PdfSignatureChainStatus status = SearchIssuers(
                leaf, 0, certStore, trustAnchors, validationTime, leafRole, visited, budget, pathWarnings, path, anchored);
            warnings.AddRange(pathWarnings);
            if (status == PdfSignatureChainStatus.Trusted) trustedPath = anchored;
            return status;
        }

        /// <summary>
        /// Why a leaf certificate is refused before any issuer is sought, or <c>null</c>: it is
        /// outside its validity period at <paramref name="validationTime"/>, its key usage does not
        /// permit signing, its extended key usage names no document-signing purpose (asked only
        /// when <paramref name="requireDocumentSigningPurpose"/> is set), or it marks critical an
        /// extension this walk does not process — the first of them, as a warning reads.
        /// </summary>
        /// <remarks>
        /// One judgement for the walk (<see cref="Resolve(X509Certificate, IStore{X509Certificate}, IEnumerable{X509Certificate}, DateTime, List{string}, string, bool)"/>) and for the signer, which asks it of
        /// its own certificate before it writes (<c>PdfSigner.LastSignerWarning</c>): a signer told
        /// nothing learns of a refusal from the relying party.
        /// </remarks>
        internal static string? WhyTheLeafIsRefused(
            X509Certificate leaf, DateTime validationTime, string leafRole, bool requireDocumentSigningPurpose)
        {
            var said = new List<string>();
            if (!CheckValidity(leaf, validationTime, leafRole, said)
                || !CheckSigningKeyUsage(leaf, leafRole, said))
                return said.Count > 0 ? said[0] : $"{leafRole} certificate cannot sign.";
            if (requireDocumentSigningPurpose && WhyThePurposeForbidsDocumentSigning(leaf) is { } purpose)
                return $"{leafRole} certificate {purpose}";
            if (WhyExtensionsAreNotUnderstood(leaf) is { } notUnderstood)
                return $"{leafRole} certificate {notUnderstood}";
            return null;
        }

        /// <summary>
        /// Why <paramref name="cert"/>'s extended key usage keeps it from signing a document, or
        /// <c>null</c>. A certificate with no such extension is good for every purpose (RFC 5280
        /// §4.2.1.12); one that names purposes is good for those alone, so a certificate issued for
        /// TLS, for timestamping or for OCSP does not sign a document under the same trusted root.
        /// Which purposes sign a document is <see cref="PdfEkuOids.PermitsDocumentSigning"/>.
        /// </summary>
        internal static string? WhyThePurposeForbidsDocumentSigning(X509Certificate cert)
        {
            System.Collections.IEnumerable? purposes;
            try { purposes = cert.GetExtendedKeyUsage(); }
            catch (Exception ex) { return "extended key usage could not be read: " + ex.Message; }
            if (purposes == null) return null;

            var named = new List<string>();
            foreach (object? oid in purposes)
            {
                string? id = oid?.ToString();
                if (id == null) continue;
                if (PdfEkuOids.PermitsDocumentSigning(id)) return null;
                named.Add(id);
            }
            return "is issued for other purposes (extended key usage " + string.Join(", ", named) +
                   "), none of which a document is signed under.";
        }

        /// <summary>
        /// The critical extensions this walk processes or that restrict nothing it decides: key
        /// usage, basic constraints, extended key usage, name constraints, the alternative names,
        /// CRL distribution points (<see cref="CrlScope"/>), the policy extensions — no policy is
        /// required of a chain, so any is acceptable — and qualified-certificate statements.
        /// </summary>
        private static readonly HashSet<string> UnderstoodCriticalExtensions = new HashSet<string>(StringComparer.Ordinal)
        {
            "2.5.29.15",            // keyUsage
            "2.5.29.19",            // basicConstraints
            "2.5.29.37",            // extKeyUsage
            "2.5.29.30",            // nameConstraints
            "2.5.29.17",            // subjectAltName
            "2.5.29.18",            // issuerAltName
            "2.5.29.31",            // cRLDistributionPoints
            "2.5.29.32",            // certificatePolicies
            "2.5.29.33",            // policyMappings
            "2.5.29.36",            // policyConstraints
            "2.5.29.54",            // inhibitAnyPolicy
            "2.5.29.14",            // subjectKeyIdentifier
            "2.5.29.35",            // authorityKeyIdentifier
            "1.3.6.1.5.5.7.1.3",    // qcStatements
        };

        /// <summary>
        /// Why <paramref name="cert"/> cannot be relied on for its extensions, or <c>null</c>: it
        /// marks critical an extension this walk does not process. RFC 5280 §4.2 has a validator
        /// refuse such a certificate — critical is the issuer's statement that a relying party who
        /// does not understand the extension must not use the certificate.
        /// </summary>
        internal static string? WhyExtensionsAreNotUnderstood(X509Certificate cert)
        {
            ICollection<string>? critical;
            try { critical = cert.GetCriticalExtensionOids(); }
            catch (Exception ex) { return "extensions could not be read: " + ex.Message; }
            if (critical == null) return null;
            foreach (string oid in critical)
                if (!UnderstoodCriticalExtensions.Contains(oid))
                    return $"carries the critical extension {oid}, which this validator does not process (RFC 5280 §4.2).";
            return null;
        }

        /// <summary>
        /// Why a certificate of <paramref name="below"/> — the path from the leaf up to the
        /// certificate <paramref name="issuer"/> issued — has a name <paramref name="issuer"/>'s
        /// name constraints do not admit, or <c>null</c> (RFC 5280 §4.2.1.10, §6.1.4). The subject
        /// and every alternative name are held to the permitted and excluded subtrees; a self-issued
        /// intermediate is passed over, the leaf never. A constraint that cannot be read refuses.
        /// </summary>
        internal static string? WhyNamesAreOutsideConstraints(X509Certificate issuer, IList<X509Certificate> below)
        {
            try
            {
                Org.BouncyCastle.Asn1.Asn1Object? parsed = issuer.GetExtensionParsedValue(X509Extensions.NameConstraints);
                if (parsed == null) return null;

                NameConstraints constraints = NameConstraints.GetInstance(parsed);
                var validator = new Org.BouncyCastle.Pkix.PkixNameConstraintValidator();
                if (constraints.PermittedSubtreesValue != null)
                    validator.IntersectPermittedSubtree(constraints.PermittedSubtreesValue.Elements);
                if (constraints.ExcludedSubtreesValue != null)
                    foreach (GeneralSubtree subtree in constraints.ExcludedSubtreesValue.GetElements())
                        validator.AddExcludedSubtree(subtree);

                for (int i = 0; i < below.Count; i++)
                {
                    X509Certificate cert = below[i];
                    if (i > 0 && cert.IssuerDN.Equivalent(cert.SubjectDN, true)) continue;

                    // An empty subject carries no directory name to hold to a constraint (§4.2.1.10).
                    if (Org.BouncyCastle.Asn1.Asn1Sequence.GetInstance(cert.SubjectDN.ToAsn1Object()).Count > 0)
                        validator.CheckDN(cert.SubjectDN);

                    GeneralNames? alternative = cert.GetSubjectAlternativeNameExtension();
                    if (alternative != null)
                    {
                        foreach (GeneralName name in alternative.GetNames()) validator.CheckName(name);
                    }
                    else
                    {
                        // With no alternative name, an rfc822 constraint applies to the subject's
                        // own e-mail address attribute.
                        foreach (string email in cert.SubjectDN.GetValueList(X509Name.EmailAddress))
                            validator.CheckEmail(email);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"The name constraints of '{issuer.SubjectDN}' do not admit a certificate below it: {ex.Message}";
            }
        }

        /// <summary>
        /// Depth-first search for a path from <paramref name="current"/> to a trust anchor, trying
        /// every certificate whose SubjectDN matches the issuer DN and keeping the best outcome.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ranking is what makes the attacker case safe: <c>Trusted</c> beats
        /// <c>UntrustedRoot</c> beats <c>Invalid</c>, so a decoy that fails to verify cannot
        /// suppress a genuine path that does. Only the warnings of the reported path are surfaced,
        /// so a rejected candidate does not add noise to a successful verification.
        /// </para>
        /// <para>
        /// <paramref name="budget"/> — hops first, then <see cref="MaxIssuerVerifications"/> — is a
        /// single allowance shared across the WHOLE search (not per path) and
        /// <paramref name="visited"/> is never unwound, so a crafted bag cannot make this
        /// exponential or cyclic.
        /// </para>
        /// </remarks>
        private static PdfSignatureChainStatus SearchIssuers(
            X509Certificate current,
            int intermediates,
            IStore<X509Certificate>? certStore,
            IEnumerable<X509Certificate>? trustAnchors,
            DateTime validationTime,
            string leafRole,
            HashSet<string> visited,
            int[] budget,
            List<string> warnings,
            List<X509Certificate> path,
            List<X509Certificate> anchored)
        {
            if (trustAnchors != null && ContainsCert(trustAnchors, current))
            {
                if (anchored.Count == 0) anchored.AddRange(path);   // the path ends at this anchor
                return PdfSignatureChainStatus.Trusted;
            }

            if (budget[0]-- <= 0)
                return PdfSignatureChainStatus.UntrustedRoot;

            List<X509Certificate> candidates = FindIssuers(current, certStore, trustAnchors);
            if (candidates.Count == 0)
            {
                warnings.Add($"{leafRole} issuer certificate not found in the accompanying certificates or the trust anchors");
                return PdfSignatureChainStatus.UntrustedRoot;
            }

            PdfSignatureChainStatus best = PdfSignatureChainStatus.Invalid;
            List<string>? bestWarnings = null;

            foreach (X509Certificate issuer in candidates)
            {
                var attempt = new List<string>();
                PdfSignatureChainStatus status = TryIssuer(
                    current, issuer, intermediates, certStore, trustAnchors, validationTime,
                    leafRole, visited, budget, attempt, path, anchored);

                if (status == PdfSignatureChainStatus.Trusted)
                {
                    warnings.AddRange(attempt);
                    return PdfSignatureChainStatus.Trusted;
                }
                if (bestWarnings == null || Rank(status) > Rank(best))
                {
                    best = status;
                    bestWarnings = attempt;
                }
            }

            if (bestWarnings != null) warnings.AddRange(bestWarnings);
            return best;
        }

        /// <summary>One candidate issuer: the checks a single-path walk applies inline.</summary>
        private static PdfSignatureChainStatus TryIssuer(
            X509Certificate current,
            X509Certificate issuer,
            int intermediates,
            IStore<X509Certificate>? certStore,
            IEnumerable<X509Certificate>? trustAnchors,
            DateTime validationTime,
            string leafRole,
            HashSet<string> visited,
            int[] budget,
            List<string> warnings,
            List<X509Certificate> path,
            List<X509Certificate> anchored)
        {
            if (!visited.Add(Fingerprint(issuer)))
                return PdfSignatureChainStatus.UntrustedRoot;   // already on this search's frontier

            if (!CheckValidity(issuer, validationTime, "Issuer", warnings))
                return PdfSignatureChainStatus.Invalid;
            if (!CheckCaConstraints(issuer, intermediates, warnings))
                return PdfSignatureChainStatus.Invalid;

            if (budget[1]-- <= 0)
            {
                warnings.Add($"{leafRole} issuer search stopped after {MaxIssuerVerifications} verifications.");
                return PdfSignatureChainStatus.UntrustedRoot;
            }
            _issuerVerifications++;
            try { current.Verify(PublicKeyCost.KeyOf(issuer)); }
            catch (Exception ex)
            {
                warnings.Add("Chain link verification failed: " + ex.Message);
                return PdfSignatureChainStatus.Invalid;
            }

            // A trust anchor is the relying party's own statement, so its extensions are not asked
            // whether they are understood (RFC 5280 §6.1 validates the path below an anchor); the
            // names it constrains are still held to it, which only narrows what it vouches for.
            bool isAnchor = trustAnchors != null && ContainsCert(trustAnchors, issuer);
            if (!isAnchor && WhyExtensionsAreNotUnderstood(issuer) is { } notUnderstood)
            {
                warnings.Add("Issuer certificate " + notUnderstood);
                return PdfSignatureChainStatus.Invalid;
            }
            if (WhyNamesAreOutsideConstraints(issuer, path) is { } outside)
            {
                warnings.Add(outside);
                return PdfSignatureChainStatus.Invalid;
            }

            if (isAnchor)
            {
                if (anchored.Count == 0) { anchored.AddRange(path); anchored.Add(issuer); }
                return PdfSignatureChainStatus.Trusted;
            }

            // A self-signed root ends this path: trusted only if it is an anchor, which the test
            // above already answered.
            if (issuer.IssuerDN.Equivalent(issuer.SubjectDN, true))
                return PdfSignatureChainStatus.UntrustedRoot;

            path.Add(issuer);
            try
            {
                return SearchIssuers(issuer, intermediates + 1, certStore, trustAnchors, validationTime,
                    leafRole, visited, budget, warnings, path, anchored);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        /// <summary>Preference order when several candidate paths fail differently.</summary>
        private static int Rank(PdfSignatureChainStatus status) => status switch
        {
            PdfSignatureChainStatus.Trusted => 3,
            PdfSignatureChainStatus.UntrustedRoot => 2,
            PdfSignatureChainStatus.SelfSigned => 2,
            _ => 1,
        };

        /// <summary>
        /// Best-effort ordered chain from <paramref name="leaf"/> upward, for a certificate the walk
        /// did not anchor and for the LTV embedder: each link an issuer whose key verifies the
        /// certificate below it. Stops at a self-signed root, at a certificate no candidate verifies,
        /// or at <see cref="MaxChainHops"/>. A trusted chain's revocation is checked along the path
        /// <see cref="Resolve(X509Certificate, IStore{X509Certificate}, IEnumerable{X509Certificate}, DateTime, List{string}, out List{X509Certificate}, string, bool)"/>
        /// anchored instead.
        /// <para>
        /// Guarded against a certificate cycle: a crafted cert bag can contain A issued-by B and
        /// B issued-by A (DN equivalence does not imply a real signature relationship), which
        /// would otherwise spin until the hop cap on every verification.
        /// </para>
        /// </summary>
        /// <param name="leaf">The end-entity certificate.</param>
        /// <param name="certStore">Certificates accompanying the signature (the CMS cert bag).</param>
        /// <param name="trustAnchors">
        /// The relying party's anchors, sought as issuers after the bag: a CMS that leaves its root
        /// out still has its top intermediate's issuer, and with it that intermediate's revocation
        /// status. <c>null</c> for a caller that has none (the LTV embedder).
        /// </param>
        public static List<X509Certificate> BuildChain(
            X509Certificate leaf, IStore<X509Certificate>? certStore,
            IEnumerable<X509Certificate>? trustAnchors = null)
        {
            var chain = new List<X509Certificate>();
            if (leaf == null) return chain;
            chain.Add(leaf);

            var seen = new HashSet<string>(StringComparer.Ordinal) { Fingerprint(leaf) };
            int[] verifications = { MaxIssuerVerifications };
            X509Certificate current = leaf;
            for (int hops = 0; hops < MaxChainHops; hops++)
            {
                if (current.IssuerDN.Equivalent(current.SubjectDN, true)) break; // self-signed root
                var issuer = FindIssuer(current, certStore, trustAnchors, verifications);
                if (issuer == null) break;
                if (!seen.Add(Fingerprint(issuer))) break;                       // cycle guard
                chain.Add(issuer);
                current = issuer;
            }
            return chain;
        }

        /// <summary>
        /// The certificates a trusted chain's revocation is checked along: <paramref name="trustedPath"/>,
        /// the path the walk anchored, continued above an anchor that is not a self-signed root by
        /// <see cref="BuildChain"/> — the relying party may anchor a leaf or an intermediate, and that
        /// certificate's status is still its issuer's to state.
        /// </summary>
        internal static List<X509Certificate> RevocationPath(
            List<X509Certificate> trustedPath, IStore<X509Certificate>? certStore, IEnumerable<X509Certificate>? trustAnchors)
        {
            X509Certificate top = trustedPath[trustedPath.Count - 1];
            if (top.IssuerDN.Equivalent(top.SubjectDN, true)) return trustedPath;
            var path = new List<X509Certificate>(trustedPath);
            List<X509Certificate> above = BuildChain(top, certStore, trustAnchors);
            for (int i = 1; i < above.Count; i++)
                if (!ContainsCert(path, above[i])) path.Add(above[i]);
            return path;
        }

        private static string Fingerprint(X509Certificate cert)
        {
            try { return Convert.ToBase64String(cert.GetEncoded()); }
            catch { return cert.SubjectDN?.ToString() ?? string.Empty; }
        }

        /// <summary>A certificate must be within [NotBefore, NotAfter] at the assessed time.</summary>
        public static bool CheckValidity(X509Certificate cert, DateTime time, string role, List<string> warnings)
        {
            try
            {
                if (!cert.IsValid(time))
                {
                    warnings.Add($"{role} certificate is outside its validity period " +
                                 $"({cert.NotBefore:u} – {cert.NotAfter:u}) at {time:u}.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{role} certificate validity check failed: {ex.Message}");
                return false;
            }
            return true;
        }

        // The leaf certificate, if it declares a KeyUsage extension, must allow digital
        // signatures (digitalSignature [0] or contentCommitment/nonRepudiation [1]). A cert with
        // no KeyUsage extension is unconstrained and accepted.
        private static bool CheckSigningKeyUsage(X509Certificate cert, string role, List<string> warnings)
        {
            bool[] ku = cert.GetKeyUsage();
            if (ku == null) return true;
            bool ok = (ku.Length > 0 && ku[0]) || (ku.Length > 1 && ku[1]);
            if (!ok) warnings.Add($"{role} certificate KeyUsage does not permit digital signatures.");
            return ok;
        }

        // An issuer certificate must be a CA (basic constraints CA=true), satisfy the path-length
        // constraint, and (if it declares KeyUsage) permit certificate signing (keyCertSign [5]).
        private static bool CheckCaConstraints(X509Certificate cert, int intermediatesBelow, List<string> warnings)
        {
            int bc = cert.GetBasicConstraints(); // -1 = not a CA; otherwise the pathLen (int.MaxValue = unlimited)
            if (bc < 0)
            {
                warnings.Add("Issuer certificate is not a CA (basic constraints CA=false or absent).");
                return false;
            }
            if (bc != int.MaxValue && intermediatesBelow > bc)
            {
                warnings.Add($"Path-length constraint violated: {intermediatesBelow} intermediate CA(s) " +
                             $"below a CA limited to {bc}.");
                return false;
            }
            bool[] ku = cert.GetKeyUsage();
            if (ku != null && (ku.Length <= 5 || !ku[5]))
            {
                warnings.Add("Issuer certificate KeyUsage does not permit certificate signing (keyCertSign).");
                return false;
            }
            return true;
        }

        /// <summary>
        /// The candidate issuers of <paramref name="cert"/>: every certificate in the bag whose
        /// SubjectDN matches its IssuerDN, in bag order, then every trust anchor that does. More
        /// than one is normal (a cross-signed intermediate) and also trivially forgeable, which is
        /// why callers must try each rather than commit to the first.
        /// </summary>
        /// <remarks>
        /// The anchors are candidates because a CMS need not carry its root (RFC 5652 §5.1: the
        /// certificate set may hold more or fewer than a path needs): sought in the bag alone, a
        /// chain whose root the relying party holds and the signer left out never reaches it.
        /// </remarks>
        public static List<X509Certificate> FindIssuers(
            X509Certificate cert, IStore<X509Certificate>? store,
            IEnumerable<X509Certificate>? trustAnchors = null)
        {
            var found = new List<X509Certificate>();
            if (cert == null) return found;
            if (store != null)
                foreach (var c in store.EnumerateMatches(null))
                    if (c.SubjectDN.Equivalent(cert.IssuerDN, true))
                        found.Add(c);
            // An anchor the bag also carries is one candidate, not two: tried a second time it is
            // "already visited", which outranks the refusal its first try earned.
            if (trustAnchors != null)
                foreach (var a in trustAnchors)
                    if (a != null && a.SubjectDN.Equivalent(cert.IssuerDN, true) && !ContainsCert(found, a))
                        found.Add(a);
            return found;
        }

        /// <summary>
        /// The single issuer to follow when only one can be followed: the first candidate whose key
        /// verifies <paramref name="cert"/>, or <c>null</c> when none does within
        /// <paramref name="verifications"/>, the walk's allowance. A name match whose key does not
        /// verify is no issuer: revocation data checked under it — a decoy's — answers nothing about
        /// the certificate, and reading that as "unknown" let a revoked one pass.
        /// </summary>
        private static X509Certificate? FindIssuer(
            X509Certificate cert, IStore<X509Certificate>? store, IEnumerable<X509Certificate>? trustAnchors,
            int[] verifications)
        {
            List<X509Certificate> candidates = FindIssuers(cert, store, trustAnchors);
            foreach (var c in candidates)
            {
                if (verifications[0]-- <= 0) break;
                _issuerVerifications++;
                try { cert.Verify(PublicKeyCost.KeyOf(c)); return c; }
                catch { /* try the next same-DN candidate */ }
            }
            return null;
        }

        /// <summary>Exact (DER-identical) membership test against a trust-anchor set.</summary>
        public static bool ContainsCert(IEnumerable<X509Certificate> anchors, X509Certificate target)
        {
            if (anchors == null || target == null) return false;
            byte[] targetDer;
            try { targetDer = target.GetEncoded(); }
            catch { return false; }

            foreach (var a in anchors)
            {
                if (a == null) continue;
                if (!a.SubjectDN.Equivalent(target.SubjectDN, true)) continue;
                byte[] anchorDer;
                try { anchorDer = a.GetEncoded(); }
                catch { continue; }
                if (anchorDer.Length == targetDer.Length
                    && anchorDer.AsSpan().SequenceEqual(targetDer))
                    return true;
            }
            return false;
        }
    }
}
