// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <c>PdfSignatureVerifier</c> downgrades <c>ChainStatus</c> to <c>Invalid</c> when the document's
/// embedded revocation data reports the signer <b>revoked</b> — a certificate that has been revoked
/// does not chain to anything a caller should trust, whatever the path arithmetic says.
///
/// <para><b>This was the last survivor of the verdict mutation sample.</b> Removing that downgrade
/// left the whole suite green, because <c>IsFullyTrusted</c> checks revocation <i>independently</i>
/// and so masks it. The downgrade is still load-bearing for the many callers who read
/// <c>ChainStatus</c> directly — a UI showing a green padlock keyed on "Trusted" would show it for
/// a revoked certificate — and nothing asserted it.</para>
///
/// <para>Closing it needed the fixture the sample did not have: a signed document carrying a
/// <c>/DSS</c> whose CRL actually revokes the leaf. The CRL is generated here rather than fetched,
/// and the revision is appended with <see cref="IncrementalUpdateWriter"/> so the original signed
/// bytes stay byte-identical — which is also what keeps the signature's own integrity verdict
/// true, so the test isolates the revocation effect instead of confusing it with tampering.</para>
///
/// <para><b>Why no test here asserts <c>IsFullyTrusted</c>, and do not add one.</b> Adding the
/// <c>/DSS</c> puts bytes after the last signature's <c>/ByteRange</c>, which
/// <c>PdfSignatureVerifier</c> records as a shadow-attack <c>ValidationError</c> — so
/// <c>IsValid</c>, and therefore <c>IsFullyTrusted</c>, is <b>false in the revoked and the
/// non-revoked fixture alike</b>. An <c>Assert.False(IsFullyTrusted)</c> would pass with revocation
/// checking deleted outright, which is banned pattern 3 wearing a plausible name. The two fixtures
/// differ in exactly one thing — whether the CRL lists the leaf's serial — so it is the
/// <i>difference</i> between them that carries the claim, and the properties that actually move are
/// <c>RevocationStatus</c> and <c>ChainStatus</c>. <c>IsFullyTrusted</c>'s own composition is
/// pinned directly by the truth table in <c>SignatureVerdictCompositionTests</c>.</para>
/// </summary>
public class RevokedCertificateChainStatusTests
{
    private const string Field = "Signature1";
    private static readonly SecureRandom Random = new SecureRandom();
    private static long _crlNumber = 5000;

    /// <summary>A real, signed X.509 v2 CRL, optionally listing <paramref name="revokedSerial"/>.</summary>
    internal static byte[] Crl(SigningTestCrypto.Issued issuer, BigInteger revokedSerial)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuer.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        if (revokedSerial != null)
            gen.AddCrlEntry(revokedSerial, DateTime.UtcNow.AddHours(-1), CrlReason.KeyCompromise);

        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", issuer.Private, Random)).GetEncoded();
    }

    /// <summary>
    /// Appends a revision carrying <c>/DSS &lt;&lt; /CRLs [ … ] &gt;&gt;</c> on the catalog.
    /// The original bytes are untouched, so the signature still covers what it signed.
    /// </summary>
    internal static byte[] AppendDssWithCrl(byte[] signed, byte[] crl) => AppendDssWithCrls(signed, crl);

    /// <summary>As above, with several CRLs in the one <c>/CRLs</c> array — a chain's worth.</summary>
    internal static byte[] AppendDssWithCrls(byte[] signed, params byte[][] crls)
    {
        using var doc = PdfDocument.Open(new MemoryStream(signed));

        var catalogDict = doc.Catalog?.Dictionary;
        Assert.NotNull(catalogDict);

        // The catalog's own object number, from the trailer's /Root reference.
        //
        // ReferenceObjectNumber, NOT ObjectNumber: PdfReference inherits PdfObject.ObjectNumber,
        // which is "the number THIS object was stored under" and is 0 for a parsed reference. It
        // compiles, and it silently yields object 0 - the free-list head - so the rewritten catalog
        // is discarded and /DSS never appears. That cost an hour here.
        var rootRef = doc.Trailer[PdfName.Root] as PdfReference;
        Assert.True(rootRef != null, "the fixture's trailer has no indirect /Root to rewrite");
        int catalogObj = rootRef.ReferenceObjectNumber;
        Assert.True(catalogObj > 0, "the /Root reference did not name a real object");

        int nextObj = doc.Trailer?.GetInteger(PdfName.Size) ?? 0;
        Assert.True(nextObj > 0, "the signed fixture has no /Size to extend from");

        var objects = new List<IndirectObject>();
        var crlArray = new PdfArray();
        foreach (byte[] crl in crls)
        {
            var crlStream = new PdfStream { RawData = crl };
            crlStream.Set(PdfName.Length, new PdfNumber(crl.Length));
            objects.Add(new IndirectObject(nextObj, 0, crlStream));
            crlArray.Add(new PdfReference(nextObj, 0));
            nextObj++;
        }

        int dssObj = nextObj;
        var dss = new PdfDictionary();
        dss.Set(new PdfName("CRLs"), crlArray);

        // The catalog is rewritten in place - same object number, one key added.
        var newCatalog = PdfDictionary.CloneShallow(catalogDict);
        newCatalog.Set(new PdfName("DSS"), new PdfReference(dssObj, 0));

        objects.Add(new IndirectObject(dssObj, 0, dss));
        objects.Add(new IndirectObject(catalogObj, 0, newCatalog));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(signed, output, objects, new IncrementalUpdateOptions());
        return output.ToArray();
    }

    internal static SignatureVerificationResult Verify(byte[] pdf, X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { anchor },
        });

        var match = results.FirstOrDefault(r => r.FieldName == Field);
        Assert.True(match != null, "the fixture produced no signature field to verify");
        return match;
    }

    /// <summary>
    /// The assertion that kills the surviving mutation: a revoked signer must not report
    /// <c>ChainStatus == Trusted</c>, whatever the path arithmetic concluded.
    /// </summary>
    [Fact]
    public void ARevokedSignerDowngradesChainStatusAndIsNotFullyTrusted()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);
        byte[] withDss = AppendDssWithCrl(signed, Crl(ca, leaf.Cert.SerialNumber));

        var r = Verify(withDss, ca.Cert);

        Assert.Equal(PdfRevocationStatus.Revoked, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Invalid, r.ChainStatus);
    }

    /// <summary>
    /// The control, and it is what makes the test above a statement about <b>revocation</b> rather
    /// than about appending a revision: the same document with a CRL that lists <i>somebody else</i>
    /// keeps a trusted chain. Without it, an appended revision breaking the chain for any reason
    /// would satisfy the assertion.
    ///
    /// <para>It asserts <see cref="PdfRevocationStatus.Good"/> specifically, and that is the whole
    /// strength of it. <c>Good</c> is reachable only by finding the <c>/DSS</c>, verifying the CRL's
    /// own signature and finding it current — so it says the revocation machinery <b>ran</b>.
    /// <c>NotChecked</c> is what a document with no <c>/DSS</c> reports, so a control asserting
    /// merely "not revoked" would pass with the <c>/DSS</c> ignored outright, which is exactly the
    /// bug that made this fixture silently useless while it was being written.</para>
    /// </summary>
    [Fact]
    public void ACrlThatDoesNotListTheSignerIsConsultedAndLeavesTheChainTrusted()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);
        // A serial the CA never issued.
        byte[] withDss = AppendDssWithCrl(signed, Crl(ca, BigInteger.ValueOf(0x5EEDBEEF)));

        var r = Verify(withDss, ca.Cert);

        Assert.Equal(PdfRevocationStatus.Good, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
    }

    /// <summary>
    /// The third leg, and the one that separates "the CRL was read" from "a revision was appended":
    /// the <b>same signed bytes with no <c>/DSS</c> at all</b> report <c>NotChecked</c>. Together
    /// with the control above this pins the fixture itself — if <c>AppendDssWithCrl</c> ever stops
    /// producing a reachable <c>/DSS</c> (it did, by writing the catalog as object 0), this test
    /// keeps passing while the other two fail, which is what names the fixture as the culprit
    /// rather than the verifier.
    /// </summary>
    [Fact]
    public void WithNoDssTheSameSignatureReportsRevocationNotChecked()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);

        var r = Verify(signed, ca.Cert);

        Assert.Equal(PdfRevocationStatus.NotChecked, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
    }

    // ================================================================ the chain above the leaf

    /// <summary>
    /// A leaf whose own CRL says Good under an intermediate nothing vouches for is <c>Unknown</c> as a
    /// chain — the chain as a whole has not been shown unrevoked. The control adds the root's CRL for
    /// the intermediate, and the same document reads <c>Good</c>. No other test reaches this branch of
    /// the chain walk: removing the degradation reddens nothing else.
    /// </summary>
    [Fact]
    public void AnUnresolvedIntermediate_DegradesTheLeafsGoodToUnknown()
    {
        var root = SigningTestCrypto.Ca("CN=Chain Root");
        var intermediate = SigningTestCrypto.IssuedIntermediate(root, SigningTestCrypto.Keys(), "CN=Chain Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, intermediate.Cert, root.Cert);
        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);

        BigInteger somebodyElse = BigInteger.ValueOf(0x5EEDBEEF);
        byte[] leafOnly = AppendDssWithCrls(signed, Crl(intermediate, somebodyElse));
        byte[] wholeChain = AppendDssWithCrls(signed, Crl(intermediate, somebodyElse), Crl(root, somebodyElse));

        Assert.Equal(PdfRevocationStatus.Unknown, Verify(leafOnly, root.Cert).RevocationStatus);
        Assert.Equal(PdfRevocationStatus.Good, Verify(wholeChain, root.Cert).RevocationStatus);
    }

    /// <summary>
    /// The CMS bag carries, first, a copy of the signer's intermediate — its name and its public key —
    /// issued by an attacker's own root. The walk anchors through the real intermediate; a separately
    /// built chain followed the copy, whose key verifies the leaf just as well, so the root's CRL listing
    /// the real intermediate was never asked about and a revoked chain read as merely unknown. Revocation
    /// is checked along the path the walk anchored. The control: the root's CRL listing somebody else.
    /// </summary>
    [Fact]
    public void AdversarialSameNameSameKeyIntermediate_DoesNotDivertTheRevocationCheck()
    {
        var root = SigningTestCrypto.Ca("CN=A Long Name For The Real Root Certification Authority");
        var attacker = SigningTestCrypto.Ca("CN=X");
        var keys = SigningTestCrypto.Keys();
        var intermediate = SigningTestCrypto.IssuedIntermediate(root, keys, "CN=Shared Intermediate");
        var copy = SigningTestCrypto.IssuedIntermediate(attacker, keys, "CN=Shared Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate);
        byte[] signed = SignWithBag(leaf, leaf.Cert, copy.Cert, attacker.Cert, intermediate.Cert, root.Cert);

        BigInteger somebodyElse = BigInteger.ValueOf(0x5EEDBEEF);
        byte[] revoked = AppendDssWithCrls(signed, Crl(intermediate, somebodyElse), Crl(root, intermediate.Cert.SerialNumber));
        byte[] notRevoked = AppendDssWithCrls(signed, Crl(intermediate, somebodyElse), Crl(root, somebodyElse));

        var r = Verify(revoked, root.Cert);
        Assert.Equal(PdfRevocationStatus.Revoked, r.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Invalid, r.ChainStatus);

        var control = Verify(notRevoked, root.Cert);
        Assert.Equal(PdfRevocationStatus.Good, control.RevocationStatus);
        Assert.Equal(PdfSignatureChainStatus.Trusted, control.ChainStatus);
    }

    /// <summary>
    /// A relying party that anchors the signer's own certificate still has it checked against its
    /// issuer's CRL: the path the walk anchored ends at the leaf, and is continued above it by the issuer
    /// whose key verifies it. The control: the CRL lists somebody else.
    /// </summary>
    [Fact]
    public void ADirectlyAnchoredLeaf_IsStillCheckedAgainstItsIssuersCrl()
    {
        var ca = SigningTestCrypto.Ca("CN=Issuer Of An Anchored Leaf");
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] signed = SignWithBag(leaf, leaf.Cert, ca.Cert);

        Assert.Equal(PdfRevocationStatus.Revoked,
            Verify(AppendDssWithCrl(signed, Crl(ca, leaf.Cert.SerialNumber)), leaf.Cert).RevocationStatus);
        Assert.Equal(PdfRevocationStatus.Good,
            Verify(AppendDssWithCrl(signed, Crl(ca, BigInteger.ValueOf(0x5EEDBEEF))), leaf.Cert).RevocationStatus);
    }

    /// <summary>Signs with a CMS whose certificate set is exactly <paramref name="bag"/>, in that order.</summary>
    private static byte[] SignWithBag(SigningTestCrypto.Issued leaf, params X509Certificate[] bag)
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            ExternalSigner = new BagSigner(leaf, bag),
            SignatureFieldName = Field,
            EstimatedSignatureSize = 32768,
        });
        return output.ToArray();
    }

    private sealed class BagSigner : IExternalSigner
    {
        private readonly SigningTestCrypto.Issued _leaf;
        private readonly List<X509Certificate> _bag;

        public BagSigner(SigningTestCrypto.Issued leaf, X509Certificate[] bag)
        {
            _leaf = leaf;
            _bag = new List<X509Certificate>(bag);
        }

        public byte[] SignData(byte[] data, string digestAlgorithm)
            => Tetitulz.Pdf.Signing.Cms.CmsSignatureGenerator.GenerateDetachedSignature(data, _leaf.Private, _leaf.Cert, _bag, digestAlgorithm);

        public byte[] SignDigest(byte[] digest, string digestAlgorithm) => throw new NotSupportedException();

        public IList<byte[]> GetCertificateChain() => _bag.Select(c => c.GetEncoded()).ToList();

        public int EstimatedSignatureSize => 32768;
    }
}
