// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.WasmSign
{
    /// <summary>A private key and its certificate chain, signer first.</summary>
    internal sealed record SigningIdentity(AsymmetricKeyParameter PrivateKey, IList<X509Certificate> Chain);

    /// <summary>What a signing run produced and what the verifier then said about it.</summary>
    internal sealed record SigningOutcome(byte[] Pdf, DateTime? TimestampTime, string? TimestampAuthority,
                                          IReadOnlyList<SignatureVerificationResult> Verification);

    /// <summary>
    /// Signs a PDF in the browser through <see cref="PdfDeferredSigner"/>, with the key held here and an
    /// optional RFC 3161 token fetched asynchronously from the host's <c>/tsa</c> relay.
    /// </summary>
    internal static class BrowserSigner
    {
        private const string Digest = "SHA-256";
        private const string SignatureAlgorithm = "SHA256withRSA";

        /// <summary>Loads the first key entry of a PKCS#12 file (through BouncyCastle — browser-wasm has no
        /// <c>X509Certificate2</c> private keys).</summary>
        public static SigningIdentity LoadPkcs12(byte[] pfx, string password)
        {
            var store = new Pkcs12StoreBuilder().Build();
            store.Load(new MemoryStream(pfx), password.ToCharArray());
            foreach (string alias in store.Aliases)
            {
                if (!store.IsKeyEntry(alias)) continue;
                var chain = store.GetCertificateChain(alias).Select(e => e.Certificate).ToList();
                return new SigningIdentity(store.GetKey(alias).Key, chain);
            }
            throw new InvalidOperationException("The PKCS#12 file holds no private key.");
        }

        /// <summary>A throwaway self-signed RSA identity. Its signatures are intact but trusted by nobody.</summary>
        public static SigningIdentity CreateDemoIdentity()
        {
            var rng = new SecureRandom();
            var kpg = new RsaKeyPairGenerator();
            kpg.Init(new KeyGenerationParameters(rng, 2048));
            AsymmetricCipherKeyPair kp = kpg.GenerateKeyPair();
            var name = new X509Name("CN=Tetitulz WasmSign Demo");
            var gen = new X509V3CertificateGenerator();
            gen.SetSerialNumber(BigInteger.ProbablePrime(64, rng));
            gen.SetIssuerDN(name);
            gen.SetSubjectDN(name);
            gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            gen.SetNotAfter(DateTime.UtcNow.AddYears(1));
            gen.SetPublicKey(kp.Public);
            X509Certificate cert = gen.Generate(new Asn1SignatureFactory("SHA256WITHRSA", kp.Private, rng));
            return new SigningIdentity(kp.Private, new List<X509Certificate> { cert });
        }

        public static async Task<SigningOutcome> SignAsync(
            byte[] pdf, SigningIdentity identity, string? reason, HttpClient? tsaRelay,
            CancellationToken cancellationToken = default)
        {
            X509Certificate signer = identity.Chain[0];

            var prepared = new MemoryStream();
            PreparedSignature descriptor = PdfDeferredSigner.Prepare(
                new MemoryStream(pdf), prepared,
                identity.Chain.Select(c => c.GetEncoded()).ToList(),
                new SignatureParameters { Reason = reason });

            byte[] attributes = CmsDeferredSignatureBuilder.GetSignedAttributes(descriptor.SignedBytes, signer, Digest);
            ISigner rsa = SignerUtilities.GetSigner(SignatureAlgorithm);
            rsa.Init(true, identity.PrivateKey);
            rsa.BlockUpdate(attributes, 0, attributes.Length);
            byte[] signatureValue = rsa.GenerateSignature();

            TimeStampToken? token = tsaRelay == null
                ? null
                : await FetchTimestampAsync(tsaRelay, signatureValue, cancellationToken);

            byte[] cms = CmsDeferredSignatureBuilder.AssembleDetachedSignature(
                descriptor.SignedBytes, attributes, signer, identity.Chain, Digest, signatureValue,
                timestampToken: token?.GetEncoded());

            var completed = new MemoryStream();
            PdfDeferredSigner.Complete(new MemoryStream(prepared.ToArray()), descriptor, cms, completed);
            byte[] signedPdf = completed.ToArray();

            return new SigningOutcome(signedPdf, token?.TimeStampInfo.GenTime, token?.SignerID.Issuer?.ToString(),
                                      PdfSigningOperations.VerifySignatures(new MemoryStream(signedPdf)));
        }

        /// <summary>
        /// RFC 3161 Appendix A: a signature timestamp's imprint is the hash of the signature value. The reply
        /// is checked against the request (status, imprint, nonce) before it is trusted to be embedded.
        /// </summary>
        private static async Task<TimeStampToken> FetchTimestampAsync(
            HttpClient relay, byte[] signatureValue, CancellationToken cancellationToken)
        {
            var generator = new TimeStampRequestGenerator();
            generator.SetCertReq(true);
            byte[] nonce = new byte[8];
            new SecureRandom().NextBytes(nonce);
            TimeStampRequest request = generator.Generate(
                TspAlgorithms.Sha256, DigestUtilities.CalculateDigest(Digest, signatureValue), new BigInteger(1, nonce));

            var body = new ByteArrayContent(request.GetEncoded());
            body.Headers.ContentType = new("application/timestamp-query");
            using HttpResponseMessage reply = await relay.PostAsync("tsa", body, cancellationToken);
            if (!reply.IsSuccessStatusCode)
                throw new InvalidOperationException($"The timestamp relay answered HTTP {(int)reply.StatusCode}.");

            var response = new TimeStampResponse(await reply.Content.ReadAsByteArrayAsync(cancellationToken));
            response.Validate(request);
            return response.TimeStampToken
                ?? throw new InvalidOperationException("The TSA granted no token: " + response.GetStatusString());
        }

        /// <summary>A one-page PDF for the self-test, with a correct cross-reference table.</summary>
        public static byte[] SamplePdf()
        {
            string[] objects =
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            };
            var sb = new StringBuilder("%PDF-1.7\n");
            var offsets = new List<int>();
            for (int i = 0; i < objects.Length; i++)
            {
                offsets.Add(sb.Length);
                sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            int xref = sb.Length;
            sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (int offset in offsets) sb.Append($"{offset:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }
}
