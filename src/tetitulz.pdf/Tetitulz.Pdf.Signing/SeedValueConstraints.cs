// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security.Certificates;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// A signature field's seed value dictionary (ISO 32000-2 §12.7.5.5, Tables 237 and 238), applied
    /// when the field is signed: what the document's author constrained the signature to be.
    /// </summary>
    /// <remarks>
    /// <para><b>Table 237:</b> the <c>/Ff</c> flags say which entries "shall be honoured" and which are
    /// "merely recommendations"; for a required one the signer can not satisfy, "signing shall not take
    /// place". <c>PdfSigner</c> adopted a seeded field without reading its <c>/SV</c>, so an author's
    /// "this field is to be signed with SHA-512, for one of these reasons, by a certificate from this
    /// CA" was ignored at the moment it applied — and the document's own
    /// <c>PdfSigningOperations.SetSeedValueDigestMethods</c> wrote constraints nothing enforced.</para>
    ///
    /// <para><b>What is honoured, what is refused.</b> <c>/Filter</c>, <c>/SubFilter</c>,
    /// <c>/DigestMethod</c>, <c>/V</c>, <c>/Reasons</c>, <c>/MDP</c>, <c>/TimeStamp</c>,
    /// <c>/LockDocument</c> and the <c>/Cert</c> constraints this signer can check (<c>/Subject</c>,
    /// <c>/Issuer</c>, <c>/OID</c>, <c>/SubjectDN</c>, <c>/KeyUsage</c>) are applied. A required
    /// <c>/LegalAttestation</c>, <c>/AddRevInfo true</c>, <c>/AppearanceFilter</c> or <c>/Cert /URL</c>
    /// names something this signer does not do — a legal attestation, revocation data inside the CMS,
    /// a named appearance, fetching a credential — and is refused rather than signed past. The same
    /// entries as optional constraints are ignored, which Table 237 allows.</para>
    ///
    /// <para><b>Every decision comes out as one <see cref="Choices"/></b>, made before a byte is
    /// written, so the signature dictionary, the CMS and the certification level cannot each read the
    /// seed a different way.</para>
    /// </remarks>
    internal sealed class SeedValueConstraints
    {
        // Table 237 /Ff bit positions, 1-based: 1 Filter, 2 SubFilter, 3 V, 4 Reasons, 5 LegalAttestation,
        // 6 AddRevInfo, 7 DigestMethod, 8 LockDocument, 9 AppearanceFilter.
        private const int FilterRequired = 1 << 0;
        private const int SubFilterRequired = 1 << 1;
        private const int VersionRequired = 1 << 2;
        private const int ReasonsRequired = 1 << 3;
        private const int LegalAttestationRequired = 1 << 4;
        private const int AddRevInfoRequired = 1 << 5;
        private const int DigestMethodRequired = 1 << 6;
        private const int LockDocumentRequired = 1 << 7;
        private const int AppearanceFilterRequired = 1 << 8;

        // Table 238 /Ff bit positions: 1 Subject, 2 Issuer, 3 OID, 4 SubjectDN, 5 reserved, 6 KeyUsage, 7 URL.
        private const int CertSubjectRequired = 1 << 0;
        private const int CertIssuerRequired = 1 << 1;
        private const int CertOidRequired = 1 << 2;
        private const int CertSubjectDnRequired = 1 << 3;
        private const int CertKeyUsageRequired = 1 << 5;
        private const int CertUrlRequired = 1 << 6;

        /// <summary>
        /// Table 237 <c>/V</c>: "A value of 3 specifies that it shall be able to recognise all seed value
        /// dictionary entries specified in PDF 2.0 and earlier" — every entry of Tables 237 and 238 is
        /// read here.
        /// </summary>
        internal const int ParserCapability = 3;

        /// <summary>The signature handler this signer writes as <c>/Filter</c> — and checks a seed value against.</summary>
        internal const string ThisFilter = "Adobe.PPKLite";
        /// <summary>The PAdES <c>/SubFilter</c> (ISO 32000-2 §12.8.3.4), the second this signer writes.</summary>
        internal const string PadesSubFilter = "ETSI.CAdES.detached";

        private static readonly string[] SubFiltersThisSignerWrites = { "adbe.pkcs7.detached", PadesSubFilter };

        private readonly PdfDictionary _seed;
        private readonly Func<PdfObject?, PdfObject?> _resolve;
        private readonly int _flags;

        private SeedValueConstraints(PdfDictionary seed, Func<PdfObject?, PdfObject?> resolve)
        {
            _seed = seed;
            _resolve = resolve;
            // A flag word: the low 32 bits of the integer (PdfNumber.FlagWord), as /P and an
            // annotation's /F are read — never a saturating or an unchecked cast of the double.
            _flags = FlagWord(seed[PdfName.Ff]);
        }

        /// <summary>A Table 237/238 <c>/Ff</c> flag word, or 0 when the entry is absent or not an integer.</summary>
        private int FlagWord(PdfObject? value) => (_resolve(value) as PdfNumber)?.FlagWord ?? 0;

        /// <summary>The signature's settled choices — the caller's, as the seed value allows or requires.</summary>
        internal sealed class Choices
        {
            /// <summary><c>adbe.pkcs7.detached</c> or <c>ETSI.CAdES.detached</c>, without the slash.</summary>
            internal string SubFilter = SubFiltersThisSignerWrites[0];

            /// <summary>The <c>/Reason</c> to write, or <c>null</c> to write none.</summary>
            internal string? Reason;

            /// <summary>The digest to sign with, in this library's naming ("SHA-256", "SHA512").</summary>
            internal string DigestAlgorithm = "SHA-256";

            /// <summary>The DocMDP level the signature certifies at.</summary>
            internal DocMdpPermission Permission;

            /// <summary>The timestamping server, or <c>null</c> for no signature timestamp.</summary>
            internal string? TimestampUrl;

            /// <summary>Whether a field's seed value may name <see cref="TimestampUrl"/> (<c>SignatureParameters.UseDocumentTimestampAuthority</c>).</summary>
            internal bool DocumentTimestampAllowed;

            /// <summary>Whether <see cref="TimestampUrl"/> is the document's, not the caller's: it is reached through the guarded transport.</summary>
            internal bool TimestampUrlFromDocument;
        }

        /// <summary>
        /// The choices for signing <paramref name="field"/> with <paramref name="parameters"/>: the
        /// caller's when the field carries no <c>/SV</c>, otherwise as the seed value constrains them.
        /// </summary>
        /// <param name="field">The field being signed, or <c>null</c> for a newly minted one.</param>
        /// <param name="resolve">Follows an indirect reference.</param>
        /// <param name="parameters">What the caller asked for.</param>
        /// <param name="permission">The DocMDP level settled so far (the caller's, or a lock's).</param>
        /// <param name="certificate">The signing certificate and its chain, for Table 238.</param>
        /// <exception cref="PdfSigningException">A required constraint cannot be met.</exception>
        internal static Choices Decide(
            PdfDictionary? field, Func<PdfObject?, PdfObject?> resolve,
            SignatureParameters parameters, DocMdpPermission permission, CertificateData certificate)
        {
            var choices = new Choices
            {
                SubFilter = parameters.UseEtsiPadesSubFilter ? SubFiltersThisSignerWrites[1] : SubFiltersThisSignerWrites[0],
                Reason = parameters.Reason,
                DigestAlgorithm = parameters.DigestAlgorithm,
                Permission = permission,
                TimestampUrl = parameters.TimestampAuthorityUrl,
                DocumentTimestampAllowed = parameters.UseDocumentTimestampAuthority,
            };

            if (field != null && resolve(field[PdfName.SV]) is PdfDictionary seed)
                new SeedValueConstraints(seed, resolve).Apply(choices, certificate);

            return choices;
        }

        private void Apply(Choices choices, CertificateData certificate)
        {
            // /Filter — "the signature handler specified by this entry shall be used when signing;
            // otherwise, signing shall not take place."
            string? filter = Name(_seed[PdfName.Filter]);
            if (filter != null && filter != ThisFilter && Required(FilterRequired))
                Refuse($"the signature handler /{filter} (/Filter); this signer is /{ThisFilter}");

            // /SubFilter — "The first name in the array that matches an encoding supported by the
            // signature handler shall be the encoding that is actually used", required or not.
            var subFilters = Names(_seed, PdfName.SubFilter);
            if (subFilters.Count > 0)
            {
                string? match = subFilters.Find(n => Array.IndexOf(SubFiltersThisSignerWrites, n) >= 0);
                if (match != null) choices.SubFilter = match;
                else if (Required(SubFilterRequired))
                    Refuse($"one of the encodings {Join(subFilters)} (/SubFilter); this signer writes " +
                           Join(SubFiltersThisSignerWrites));
            }

            // /DigestMethod — "acceptable digest algorithms to use while signing". The caller's stays
            // when it is acceptable; otherwise the first one this signer has that is not weak: a seed
            // value naming SHA-1 would have the signer sign the author's own prefix under a
            // collision-broken hash (WeakAlgorithmPolicy.DetectWeakDigest, the verifier's own rule).
            var digests = Names(_seed, PdfName.DigestMethod);
            if (digests.Count > 0)
            {
                string? callerOid = OidOrNull(choices.DigestAlgorithm);
                string? chosen = null;
                foreach (string digest in digests)
                {
                    string? oid = OidOrNull(digest);
                    if (oid == null) continue;
                    if (oid == callerOid) { chosen = choices.DigestAlgorithm; break; }
                    if (WeakAlgorithmPolicy.DetectWeakDigest(oid) != null) continue;
                    chosen ??= digest;
                }

                if (chosen != null) choices.DigestAlgorithm = chosen;
                else if (Required(DigestMethodRequired))
                    Refuse($"one of the digest algorithms {Join(digests)} (/DigestMethod), none of which this signer has, " +
                           "or only a weak one");
            }

            // /V — the seed value parser this dictionary needs.
            if (Required(VersionRequired) && _resolve(_seed[PdfName.V]) is PdfNumber version
                && version.DoubleValue > ParserCapability)
                Refuse($"a seed value parser of version {version} (/V); this signer recognises every entry " +
                       $"up to version {ParserCapability}");

            // /Reasons — required: one of them, or none at all when the array is absent or holds a PERIOD.
            if (Required(ReasonsRequired))
            {
                var reasons = Strings(_seed, PdfName.Reasons);
                if (reasons.Count == 0 || (reasons.Count == 1 && reasons[0] == "."))
                    choices.Reason = null;
                else if (string.IsNullOrEmpty(choices.Reason))
                    choices.Reason = reasons[0];
                else if (!reasons.Contains(choices.Reason!))
                    Refuse($"one of the reasons {Join(reasons)} (/Reasons); the caller's reason '{choices.Reason}' " +
                           "is not among them");
            }

            if (Required(LegalAttestationRequired) && Strings(_seed, PdfName.LegalAttestation).Count > 0)
                Refuse("a legal content attestation (/LegalAttestation, §12.8.7), which this signer does not write");

            // /AddRevInfo true — revocation data "within the signature value" (PKCS #7). This library
            // embeds it in a /DSS instead, which is a different place and not what the author required.
            if (Required(AddRevInfoRequired) && _resolve(_seed[PdfName.AddRevInfo]) is PdfBoolean addRevInfo
                && addRevInfo.Value)
                Refuse("revocation information inside the signature value (/AddRevInfo true); this signer embeds " +
                       "it in a /DSS (SignatureParameters.EmbedLtv), not in the CMS");

            // /MDP — the author's intent for the signature's type. No /Ff bit governs it, so it is
            // taken where the caller expressed nothing and left alone where the caller did.
            if (_resolve(_seed[PdfName.MDP]) is PdfDictionary mdp
                && _resolve(mdp[PdfName.P]) is PdfNumber mdpLevel
                && choices.Permission == DocMdpPermission.None)
            {
                int level = mdpLevel.IntValue;   // saturating, never an unchecked cast (CLAUDE.md, file-supplied numbers)
                if (level >= 1 && level <= 3) choices.Permission = (DocMdpPermission)level;
            }

            // /LockDocument — "the document is locked after signing": no change permitted, which is
            // DocMDP /P 1 (Table 257). Required, it overrides a caller who asked for nothing and refuses
            // one who asked for another level; optional, the caller may override.
            string? lockDocument = Name(_seed[PdfName.LockDocument]);
            if (lockDocument == "true")
            {
                if (choices.Permission == DocMdpPermission.None)
                    choices.Permission = DocMdpPermission.NoChanges;
                else if (choices.Permission != DocMdpPermission.NoChanges && Required(LockDocumentRequired))
                    Refuse("the document to be locked after signing (/LockDocument true, DocMDP /P 1), while " +
                           $"DocMdpPermission asks for /P {(int)choices.Permission}");
            }
            else if (lockDocument == "false" && Required(LockDocumentRequired)
                     && choices.Permission == DocMdpPermission.NoChanges)
                Refuse("the document not to be locked after signing (/LockDocument false), while DocMdpPermission " +
                       "asks for /P 1");

            if (Required(AppearanceFilterRequired) && _resolve(_seed[PdfName.AppearanceFilter]) is PdfString appearance)
                Refuse($"the named signature appearance '{appearance.GetText()}' (/AppearanceFilter); this signer " +
                       "keeps no named appearances");

            // /TimeStamp — /Ff 1 means "the signature shall have a timestamp". The server the author
            // names is the document's choice of address, asked from the signing host: used only when
            // the caller named none and allowed the document's, and then reached through the guarded
            // transport (TsaClient.GetTimestampTokenFromDocumentUrl), never the caller's TSA client.
            if (_resolve(_seed[PdfName.TimeStamp]) is PdfDictionary timeStamp)
            {
                string? url = (_resolve(timeStamp[PdfName.URL]) as PdfString)?.GetText();
                if (string.IsNullOrEmpty(choices.TimestampUrl) && !string.IsNullOrEmpty(url) && choices.DocumentTimestampAllowed)
                {
                    choices.TimestampUrl = url;
                    choices.TimestampUrlFromDocument = true;
                }

                bool must = FlagWord(timeStamp[PdfName.Ff]) == 1;
                if (must && string.IsNullOrEmpty(choices.TimestampUrl))
                    Refuse("a signature timestamp (/TimeStamp /Ff 1); set SignatureParameters.TimestampAuthorityUrl, " +
                           "or UseDocumentTimestampAuthority to ask the server the document names");
            }

            if (_resolve(_seed[PdfName.Cert]) is PdfDictionary cert)
                ApplyCertificate(cert, certificate);
        }

        /// <summary>Table 238 — what the signing certificate has to be.</summary>
        private void ApplyCertificate(PdfDictionary cert, CertificateData certificate)
        {
            int flags = FlagWord(cert[PdfName.Ff]);
            X509Certificate leaf = certificate.Certificate;

            if ((flags & CertSubjectRequired) != 0)
            {
                var subjects = Bytes(cert, PdfName.Subject);
                if (subjects.Count > 0 && !subjects.Exists(der => Same(der, leaf.GetEncoded())))
                    Refuse("one of the certificates listed in /Cert /Subject; the signing certificate is not among them");
            }

            var issuers = Bytes(cert, PdfName.Issuer);
            if ((flags & CertIssuerRequired) != 0 && issuers.Count > 0 && !IssuedByAny(issuers, certificate))
                Refuse("a certificate issued by one of the issuers listed in /Cert /Issuer");

            // "This field shall only be used if the value of Issuer is not empty."
            if ((flags & CertOidRequired) != 0 && issuers.Count > 0)
            {
                var policies = PolicyOids(leaf);
                foreach (string oid in Strings(cert, PdfName.OID))
                    if (!policies.Contains(oid))
                        Refuse($"the certificate policy {oid} (/Cert /OID), which the signing certificate does not carry");
            }

            if ((flags & CertSubjectDnRequired) != 0 && _resolve(cert[PdfName.SubjectDN]) is PdfArray names
                && names.Count > 0 && !AnyNameMatches(names, leaf.SubjectDN))
                Refuse("a subject distinguished name with the attributes listed in /Cert /SubjectDN");

            if ((flags & CertKeyUsageRequired) != 0)
            {
                var patterns = Strings(cert, PdfName.KeyUsage);
                if (patterns.Count > 0 && !patterns.Exists(p => KeyUsageMatches(p, leaf.GetKeyUsage())))
                    Refuse($"a certificate whose key usage matches one of {Join(patterns)} (/Cert /KeyUsage)");
            }

            // /URL with /URLType /Browser is for enrolling a person interactively, and "the Ff attribute's
            // URL bit shall be ignored for this usage"; any other usage asks this signer to fetch a
            // credential, which it does not do.
            string urlType = Name(cert[PdfName.URLType]) ?? "Browser";
            if ((flags & CertUrlRequired) != 0 && _resolve(cert[PdfName.URL]) is PdfString && urlType != "Browser")
                Refuse($"a credential obtained from /Cert /URL (/URLType /{urlType}), which this signer does not fetch");
        }

        // ---------------------------------------------------------------- Table 238 matching

        /// <summary>
        /// "If the signer's certificate refers to any of the specified issuers (either directly or
        /// indirectly), the certificate shall be considered acceptable": a listed certificate is in the
        /// chain above the leaf, or is the leaf's direct issuer by name.
        /// </summary>
        private static bool IssuedByAny(List<byte[]> issuers, CertificateData certificate)
        {
            var parser = new X509CertificateParser();
            foreach (byte[] der in issuers)
            {
                X509Certificate? issuer;
                try { issuer = parser.ReadCertificate(der); }
                catch (Exception ex) when (ex is System.IO.IOException || ex is CertificateException || ex is ArgumentException)
                { continue; }
                if (issuer == null) continue;

                foreach (var c in certificate.Chain)
                    if (!c.Equals(certificate.Certificate) && Same(c.GetEncoded(), der)) return true;

                if (certificate.Certificate.IssuerDN.Equivalent(issuer.SubjectDN, true)) return true;
            }
            return false;
        }

        /// <summary>The policy identifiers of the leaf's certificatePolicies extension (RFC 5280 §4.2.1.4).</summary>
        private static HashSet<string> PolicyOids(X509Certificate leaf)
        {
            var oids = new HashSet<string>(StringComparer.Ordinal);
            Asn1OctetString? extension = leaf.GetExtensionValue(X509Extensions.CertificatePolicies);
            if (extension == null) return oids;

            var policies = CertificatePolicies.GetInstance(X509ExtensionUtilities.FromExtensionValue(extension));
            foreach (PolicyInformation policy in policies.GetPolicyInformation())
                oids.Add(policy.PolicyIdentifier.Id);
            return oids;
        }

        /// <summary>
        /// Whether one of the <c>/SubjectDN</c> dictionaries is wholly present in
        /// <paramref name="subject"/> — every attribute it names, with that value. An author who lists
        /// several dictionaries lists alternatives: two with different <c>/CN</c> values could not both
        /// hold of one certificate.
        /// </summary>
        private bool AnyNameMatches(PdfArray names, X509Name subject)
        {
            foreach (var item in names)
            {
                if (!(_resolve(item) is PdfDictionary wanted) || wanted.Count == 0) continue;

                bool all = true;
                foreach (var entry in wanted)
                {
                    DerObjectIdentifier? attribute = AttributeOid(entry.Key.Value);
                    string? value = (_resolve(entry.Value) as PdfString)?.GetText();
                    if (attribute == null || value == null || !subject.GetValueList(attribute).Contains(value))
                    { all = false; break; }
                }
                if (all) return true;
            }
            return false;
        }

        /// <summary>The attribute an /SubjectDN key names: a dotted OID as written, or a short name.</summary>
        private static DerObjectIdentifier? AttributeOid(string key)
        {
            if (key.Length > 0 && char.IsDigit(key[0]))
            {
                try { return new DerObjectIdentifier(key); }
                catch (FormatException) { return null; }
            }

            switch (key.ToUpperInvariant())
            {
                case "CN": return X509Name.CN;
                case "O": return X509Name.O;
                case "OU": return X509Name.OU;
                case "C": return X509Name.C;
                case "L": return X509Name.L;
                case "ST": return X509Name.ST;
                case "STREET": return X509Name.Street;
                case "E":
                case "EMAIL":
                case "EMAILADDRESS": return X509Name.EmailAddress;
                case "SN":
                case "SERIALNUMBER": return X509Name.SerialNumber;
                case "T": return X509Name.T;
                case "DC": return X509Name.DC;
                case "UID": return X509Name.UID;
                case "GIVENNAME": return X509Name.GivenName;
                case "SURNAME": return X509Name.Surname;
                default: return null;
            }
        }

        /// <summary>
        /// Table 238's key-usage string against RFC 5280's bits, in the order both use: digitalSignature,
        /// nonRepudiation, keyEncipherment, dataEncipherment, keyAgreement, keyCertSign, cRLSign,
        /// encipherOnly, decipherOnly. '1' must be set, '0' must be clear, anything else — 'X', or a
        /// missing character — "does not matter".
        /// </summary>
        internal static bool KeyUsageMatches(string pattern, bool[]? usage)
        {
            for (int i = 0; i < 9; i++)
            {
                char wanted = i < pattern.Length ? pattern[i] : 'X';
                bool set = usage != null && i < usage.Length && usage[i];
                if (wanted == '1' && !set) return false;
                if (wanted == '0' && set) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- reading

        private bool Required(int flag) => (_flags & flag) != 0;

        private int Int(PdfObject? value) => _resolve(value) is PdfNumber n ? n.IntValue : 0;

        private string? Name(PdfObject? value) => (_resolve(value) as PdfName)?.Value;

        private List<string> Names(PdfDictionary dict, PdfName key)
        {
            var names = new List<string>();
            if (_resolve(dict[key]) is PdfArray array)
                foreach (var item in array)
                    if (_resolve(item) is PdfName name) names.Add(name.Value);
            return names;
        }

        private List<string> Strings(PdfDictionary dict, PdfName key)
        {
            var strings = new List<string>();
            if (_resolve(dict[key]) is PdfArray array)
                foreach (var item in array)
                    if (_resolve(item) is PdfString s) strings.Add(s.GetText());
            return strings;
        }

        private List<byte[]> Bytes(PdfDictionary dict, PdfName key)
        {
            var bytes = new List<byte[]>();
            if (_resolve(dict[key]) is PdfArray array)
                foreach (var item in array)
                    if (_resolve(item) is PdfString s) bytes.Add(s.RawBytes);
            return bytes;
        }

        private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private static string? OidOrNull(string name)
        {
            try { return DigestAlgorithms.ResolveOidForNewSignature(name, nameof(name)); }
            catch (ArgumentException) { return null; }
        }

        private static string Join(IEnumerable<string> values) => string.Join(", ", values);

        private static void Refuse(string what)
            => throw new PdfSigningException(
                $"The signature field's seed value dictionary (/SV) requires {what}. ISO 32000-2 Table 237: " +
                "for a required constraint the signer cannot meet, signing shall not take place.");
    }
}
