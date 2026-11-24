// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Timestamping;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Skips — <b>visibly</b> — when the checked-in source is unreachable from the test assembly,
/// rather than passing vacuously on an empty read (banned test pattern 2).
/// </summary>
public sealed class DssSourceFactAttribute : FactAttribute
{
    public DssSourceFactAttribute()
    {
        if (TestCorpus.RepoRoot == null)
            Skip = "The repository root is not reachable from the test assembly, so " +
                   "DssBuilder.cs cannot be read.";
    }
}

/// <summary>
/// A CRL may only reach <c>/DSS /CRLs</c> once it has been shown to be <b>this certificate's
/// issuer's</b> CRL and to be current.
/// </summary>
/// <remarks>
/// <para><b>Every OCSP response was validated and no CRL was validated at all.</b>
/// <c>DssBuilder.FetchOcspResponse</c> ran its body through
/// <c>ValidateAndFilterOcspResponse</c> — parse, responder signature, issuer anchoring, EKU,
/// freshness — while <c>FetchCrl</c>'s body went straight into the output list. A CRL
/// distribution-point URL is read out of the <i>untrusted</i> certificate's own CRL-DP extension
/// and fetched over plain HTTP, so "HTTP 200" was the entire admission test: an error page, a CRL
/// belonging to an unrelated CA, or one forged by anybody on the wire was embedded, and
/// <c>TryBuildDss</c> returned <c>true</c> — revocation data obtained.</para>
///
/// <para><b>The rule is tested on the filter, not on the fetch.</b> Reaching it through
/// <c>TryBuildDss</c> would need a live distribution point, which is exactly the reason the OCSP
/// freshness rule had to be extracted before its own hole could be seen.</para>
///
/// <para><b>The missing <c>nextUpdate</c> case is deliberately not a refusal.</b> RFC 5280
/// §5.1.2.5 makes it optional, so an age cap of the kind OCSP carries would start refusing
/// revocation data that is genuinely the CA's current list. It is embedded with a warning
/// instead, and <see cref="ACrlWithNoNextUpdateIsEmbeddedWithAWarningRatherThanRefused"/> is the
/// control that stops the OCSP rule being copied onto it.</para>
/// </remarks>
public class DssCrlValidationTests
{
    // ------------------------------------------------------------------ fixtures

    private static readonly SecureRandom Random = new();
    private static long _crlNumber = 1;

    /// <summary>
    /// A real, signed X.509 v2 CRL. <paramref name="signer"/> is separate from
    /// <paramref name="issuerDn"/> on purpose: passing a different key is how the forged-CRL case
    /// below produces a CRL that <i>names</i> the right issuer and is signed by the wrong one.
    /// </summary>
    private static byte[] Crl(
        X509Name issuerDn,
        SigningTestCrypto.Issued signer,
        DateTime thisUpdate,
        DateTime? nextUpdate,
        BigInteger? revokedSerial = null)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(issuerDn);
        gen.SetThisUpdate(thisUpdate);
        if (nextUpdate.HasValue) gen.SetNextUpdate(nextUpdate.Value);
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        if (revokedSerial != null)
            gen.AddCrlEntry(revokedSerial, thisUpdate, CrlReason.KeyCompromise);

        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signer.Private, Random))
                  .GetEncoded();
    }

    private static byte[] CurrentCrl(SigningTestCrypto.Issued ca, BigInteger? revokedSerial = null)
        => Crl(ca.Cert.SubjectDN, ca, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(7),
               revokedSerial);

    // ------------------------------------------------------------------ refusals

    /// <summary>
    /// The shape the defect actually took in the field: a distribution point that answers 200 with
    /// something that is not a CRL at all (an HTML error page, a captive-portal login, a truncated
    /// body). It was embedded verbatim.
    /// </summary>
    [Fact]
    public void AnHttpBodyThatIsNotACrlIsRefusedAndReported()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        byte[] body = Encoding.ASCII.GetBytes("<html><body>503 Service Unavailable</body></html>");

        Assert.Null(DssBuilder.ValidateAndFilterCrl(body, leaf.Cert, ca.Cert));
        Assert.False(DssBuilder.LastCrlSignatureValid);
        Assert.Contains("failed to parse", DssBuilder.LastCrlSignatureIssue);
    }

    /// <summary>
    /// A perfectly well-formed CRL from a CA that has nothing to do with this certificate. Its own
    /// signature verifies against its own key, so anything short of comparing the issuer name to
    /// the certificate's issuer accepts it — and it lists nothing, so it reads as "not revoked".
    /// </summary>
    [Fact]
    public void ACrlFromAForeignIssuerIsRefusedAndReported()
    {
        var ca = SigningTestCrypto.Ca("CN=Real Root CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        var foreign = SigningTestCrypto.Ca("CN=Unrelated Root CA");

        byte[] crl = CurrentCrl(foreign);

        Assert.Null(DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert));
        Assert.False(DssBuilder.LastCrlSignatureValid);
        Assert.Contains("Unrelated Root CA", DssBuilder.LastCrlSignatureIssue);
        Assert.Null(DssBuilder.LastCrlFreshnessIssue);
    }

    /// <summary>
    /// The forgery the name check alone cannot catch: the attacker's CRL <b>claims</b> the real
    /// CA's issuer name and is signed by the attacker's key. Only verifying against the issuer's
    /// public key separates it from the genuine list.
    /// </summary>
    [Fact]
    public void ACrlForgedUnderTheIssuersNameIsRefusedAndReported()
    {
        var ca = SigningTestCrypto.Ca("CN=Real Root CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        var impostor = SigningTestCrypto.Ca("CN=Real Root CA");   // same DN, different key

        byte[] forged = Crl(ca.Cert.SubjectDN, impostor,
            DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(7));

        Assert.Null(DssBuilder.ValidateAndFilterCrl(forged, leaf.Cert, ca.Cert));
        Assert.False(DssBuilder.LastCrlSignatureValid);
        Assert.Contains("signature verification failed", DssBuilder.LastCrlSignatureIssue);
    }

    /// <summary>
    /// An expired CRL proves nothing about "not revoked": the certificate may have been revoked in
    /// the interval since. The verify side already discards one, so embedding it makes a document
    /// that claims LTV and then fails to validate on it.
    /// </summary>
    [Fact]
    public void ACrlWhoseNextUpdateHasElapsedIsRefusedAndReported()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        byte[] stale = Crl(ca.Cert.SubjectDN, ca,
            DateTime.UtcNow.AddDays(-90), DateTime.UtcNow.AddDays(-60));

        Assert.Null(DssBuilder.ValidateAndFilterCrl(stale, leaf.Cert, ca.Cert));
        // The signature was fine; it is the window that failed, and the two are reported apart.
        Assert.True(DssBuilder.LastCrlSignatureValid);
        Assert.Null(DssBuilder.LastCrlSignatureIssue);
        Assert.Contains("stale", DssBuilder.LastCrlFreshnessIssue);
    }

    /// <summary>A <c>thisUpdate</c> in the future is clock skew or forgery, not evidence.</summary>
    [Fact]
    public void ACrlDatedInTheFutureIsRefusedAndReported()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        byte[] future = Crl(ca.Cert.SubjectDN, ca,
            DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(30));

        Assert.Null(DssBuilder.ValidateAndFilterCrl(future, leaf.Cert, ca.Cert));
        Assert.Contains("future", DssBuilder.LastCrlFreshnessIssue);
    }

    /// <summary>
    /// Nothing can be verified without the issuer's key, and "cannot verify" must not read as
    /// "verified" — the pairing <c>TryBuildDss</c> passes is <c>allCerts[i]</c> / <c>allCerts[i+1]</c>,
    /// so a one-certificate chain reaches here with nothing above it.
    /// </summary>
    [Fact]
    public void ACrlWithNoIssuerCertificateAvailableIsRefused()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        Assert.Null(DssBuilder.ValidateAndFilterCrl(CurrentCrl(ca), leaf.Cert, issuer: null));
        Assert.False(DssBuilder.LastCrlSignatureValid);
        Assert.Contains("no issuer certificate", DssBuilder.LastCrlSignatureIssue);
    }

    // ------------------------------------------------------------------ acceptances

    /// <summary>
    /// The control that keeps the guard from being "refuse everything": the genuine, current,
    /// issuer-signed CRL is returned <b>byte-for-byte</b>, because what gets embedded must be the
    /// CA's signed bytes and not a re-encoding of them.
    /// </summary>
    [Fact]
    public void AnIssuerSignedCurrentCrlIsEmbeddedUnchanged()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = CurrentCrl(ca);

        var warnings = new List<string>();
        byte[] embedded = DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert, warnings);

        Assert.Equal(crl, embedded);
        Assert.True(DssBuilder.LastCrlSignatureValid);
        Assert.Null(DssBuilder.LastCrlSignatureIssue);
        Assert.Null(DssBuilder.LastCrlFreshnessIssue);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// A CRL that lists this very certificate as revoked is still honest evidence and belongs in
    /// the document — the embedder must not quietly drop the one CRL that says something bad.
    /// </summary>
    [Fact]
    public void ACrlListingTheCertificateAsRevokedIsStillEmbedded()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = CurrentCrl(ca, leaf.Cert.SerialNumber);

        Assert.Equal(crl, DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert));
    }

    /// <summary>
    /// RFC 5280 §5.1.2.5 makes <c>nextUpdate</c> optional, so its absence must <b>not</b> be
    /// refused the way OCSP's is — that would discard valid revocation data. Within the verifier's
    /// age cap it is embedded, and the currency is said out loud on the caller's warning list.
    /// </summary>
    /// <remarks>
    /// This is the control against copying <c>IsOcspResponseCurrent</c>'s seven-day cap onto CRLs:
    /// the <c>thisUpdate</c> here is three hundred days old, which the OCSP rule refuses outright.
    /// It is inside the verifier's default 365-day cap, so the CRL this test embeds is one default
    /// verification reads; the test after it pins the refusal past the cap.
    /// </remarks>
    [Fact]
    public void ACrlWithNoNextUpdateWithinTheVerifiersCapIsEmbeddedWithAWarning()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca.Cert.SubjectDN, ca, DateTime.UtcNow.AddDays(-300), nextUpdate: null);

        var warnings = new List<string>();
        Assert.Equal(crl, DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert, warnings));
        Assert.Null(DssBuilder.LastCrlFreshnessIssue);   // not a refusal, so not on the refusal slot
        Assert.Contains(warnings, w => w.Contains("no nextUpdate"));

        // ...and the OCSP rule, which the CRL path must not inherit, refuses the same pair.
        Assert.False(DssBuilder.IsOcspResponseCurrent(
            DateTime.UtcNow.AddDays(-300), null, DateTime.UtcNow, out _));
    }

    /// <summary>
    /// Past the verifier's cap (<c>SignatureVerificationOptions.MaxRevocationDataAge</c>, 365 days by
    /// default) a CRL with no <c>nextUpdate</c> is refused and reported: default verification reads
    /// absence from it as unknown, so embedding it would claim evidence the document does not hold.
    /// </summary>
    [Fact]
    public void ACrlWithNoNextUpdatePastTheVerifiersCapIsRefusedAndReported()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = Crl(ca.Cert.SubjectDN, ca, DateTime.UtcNow.AddDays(-400), nextUpdate: null);

        var warnings = new List<string>();
        Assert.Null(DssBuilder.ValidateAndFilterCrl(crl, leaf.Cert, ca.Cert, warnings));
        Assert.True(DssBuilder.LastCrlSignatureValid);   // the window failed, not the signature
        Assert.Contains("no nextUpdate", DssBuilder.LastCrlFreshnessIssue, StringComparison.Ordinal);
        Assert.Contains("365 days", DssBuilder.LastCrlFreshnessIssue, StringComparison.Ordinal);
        Assert.Empty(warnings);
    }

    // ------------------------------------------------------------------ /DSS /CRLs

    /// <summary>
    /// The end the row is about: what the <c>/DSS /CRLs</c> array ends up holding. Two bodies are
    /// offered — a forgery and the real CRL — and the array must name <b>one</b> stream. Before the
    /// filter both were written as stream objects and both were referenced here.
    /// </summary>
    [Fact]
    public void OnlyValidatedCrlsReachTheDssCrlsArray()
    {
        var ca = SigningTestCrypto.Ca("CN=Real Root CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        var impostor = SigningTestCrypto.Ca("CN=Real Root CA");

        byte[] good = CurrentCrl(ca);
        byte[] forged = Crl(ca.Cert.SubjectDN, impostor,
            DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(7));

        // The loop PdfSigner runs: everything that survives the filter becomes one stream object,
        // and BuildDssDictionary references them in order.
        var objectNumbers = new List<int>();
        int nextObjNum = 10;
        foreach (byte[] body in new[] { forged, good })
            if (DssBuilder.ValidateAndFilterCrl(body, leaf.Cert, ca.Cert) != null)
                objectNumbers.Add(nextObjNum++);

        var dss = DssBuilder.BuildDssDictionary(null, objectNumbers);
        var crls = dss[new PdfName("CRLs")] as PdfArray;

        Assert.NotNull(crls);
        var only = Assert.IsType<PdfReference>(Assert.Single(crls!));
        Assert.Equal(10, only.ReferenceObjectNumber);   // the good CRL, and only it
    }

    /// <summary>
    /// With every candidate refused the array must be <b>absent</b>, not present and empty: a
    /// <c>/DSS</c> naming an empty <c>/CRLs</c> asserts that CRL evidence was gathered.
    /// </summary>
    [Fact]
    public void ADssBuiltFromOnlyRefusedCrlsHasNoCrlsArray()
    {
        var ca = SigningTestCrypto.Ca("CN=Real Root CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        var foreign = SigningTestCrypto.Ca("CN=Unrelated Root CA");

        var objectNumbers = new List<int>();
        if (DssBuilder.ValidateAndFilterCrl(CurrentCrl(foreign), leaf.Cert, ca.Cert) != null)
            objectNumbers.Add(10);

        var dss = DssBuilder.BuildDssDictionary(null, objectNumbers);

        Assert.Empty(objectNumbers);
        Assert.Null(dss[new PdfName("CRLs")]);
    }

    // ------------------------------------------------------------------ the one walk

    /// <summary>
    /// <c>TryBuildDss</c> and <c>TryBuildDssAsync</c> run one chain walk, <c>WalkRevocationPath</c>,
    /// and that walk passes a fetched CRL through the filter. Two <b>copies</b> would let a gate added to
    /// one and not the other make the async signing path embed what the sync path refuses — this repo's
    /// most-repeated defect class. The guard checks that no copy comes back: one walk fetches a CRL, it
    /// gates what it fetched, and both entries run it.
    /// </summary>
    /// <remarks>
    /// A source scan, because the walk is unreachable offline: it begins by downloading from a URL
    /// in the certificate's CRL-DP extension, so no in-memory fixture can drive its CRL arm.
    /// Everything above tests the filter; this tests that the walk is wired to it.
    /// </remarks>
    [DssSourceFact]
    public void TheOneChainWalkGatesItsFetchedCrlThroughTheFilter_AndBothEntriesRunIt()
    {
        string path = Path.Combine(TestCorpus.RepoRoot!, "src", "tetitulz.pdf",
            "Tetitulz.Pdf.Signing", "Timestamping", "DssBuilder.cs");
        string source = File.ReadAllText(path);

        const string fetch = "FetchCrlCore(subject, network, sync, ct)";
        int at = source.IndexOf(fetch, StringComparison.Ordinal);
        Assert.True(at >= 0,
            $"'{fetch}' no longer appears in DssBuilder.cs, so this guard is aimed at code " +
            "that moved. Re-point it rather than deleting it.");
        Assert.True(source.IndexOf(fetch, at + 1, StringComparison.Ordinal) < 0,
            "A second chain walk fetches a CRL — the sync/async copy this guard exists to keep merged.");

        string walk = source.Substring(at, Math.Min(1200, source.Length - at));
        Assert.True(walk.Contains("ValidateAndFilterCrl(", StringComparison.Ordinal),
            "The chain walk adds its CRL to the /DSS without passing it through ValidateAndFilterCrl, " +
            "so an HTTP body of any shape is embedded and reported as revocation data obtained.");

        Assert.Contains("WalkRevocationPath(chain, signerCert, network, sync: true", source, StringComparison.Ordinal);
        Assert.Contains("WalkRevocationPath(chain, signerCert, network, sync: false", source, StringComparison.Ordinal);
    }
}
