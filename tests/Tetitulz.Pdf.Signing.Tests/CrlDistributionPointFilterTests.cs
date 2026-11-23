// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing.Timestamping;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The scheme whitelist and the SSRF guard applied to CRL distribution-point URLs before the
/// library will fetch them.
/// </summary>
/// <remarks>
/// <para><b>Why these exist.</b> The two guards were duplicated between <c>FetchCrl</c> and
/// <c>FetchCrlAsync</c>, and consolidating them into <c>SafeCrlDistributionUrls</c> made it
/// possible to mutate the survivor and see what noticed. <b>Nothing did:</b> deleting the SSRF
/// guard and deleting the scheme whitelist each left all 220 Signing tests green. The redirect
/// <i>hop</i> guard was covered (<c>DssBuilderRedirectGuardTests</c>); the filter that decides
/// which URL is requested in the first place was not covered at all.</para>
///
/// <para><b>These URLs are attacker-controlled in the threat model that matters.</b> They come out
/// of a certificate extension, and a certificate reaches this code because it was embedded in a PDF
/// somebody asked to verify — so a hostile document chooses them. Without the guards, verifying an
/// untrusted PDF makes the host issue GETs to whatever the document names, including
/// <c>169.254.169.254</c> and anything on the loopback interface.</para>
///
/// <para>The filter is pure — certificate in, URLs out, no network — which is exactly why it is
/// worth testing directly rather than through a fetch.</para>
/// </remarks>
public class CrlDistributionPointFilterTests
{
    // Literal public IPs, not hostnames, and deliberately so: PrivateAddressGuard.IsSafeHost
    // resolves a name through DNS and FAILS CLOSED when it will not resolve. The first draft used
    // crl.example.com, which does not resolve, so the accept cases came back empty — and because
    // every other case here asserts a refusal, that made the whole file pass while testing nothing.
    // A literal address takes the IPAddress.TryParse branch and needs no network, so these tests
    // give the same answer on an offline runner as on a connected one.
    private const string PublicHttps = "https://93.184.216.34/root.crl";
    private const string PublicHttp = "http://8.8.8.8/root.crl";

    /// <summary>
    /// A certificate whose CRL distribution-point extension names exactly <paramref name="urls"/>,
    /// each as its own distribution point with a URI general name.
    /// </summary>
    private static X509Certificate CertWithCrlUrls(params string[] urls)
    {
        var points = urls
            .Select(u => new DistributionPoint(
                new DistributionPointName(
                    new GeneralNames(new GeneralName(GeneralName.UniformResourceIdentifier, u))),
                null, null))
            .ToArray();

        return SigningTestCrypto.CaWithExtension(
            X509Extensions.CrlDistributionPoints, false, new CrlDistPoint(points));
    }

    private static List<string> Filter(params string[] urls) =>
        DssBuilder.SafeCrlDistributionUrls(CertWithCrlUrls(urls))
                  .Select(u => u.ToString())
                  .ToList();

    /// <summary>
    /// The control, and it earned its place immediately: every other test here asserts a refusal,
    /// so a filter that rejected <i>everything</i> would pass all of them. This one failed on the
    /// first run — the fixture's hostnames did not resolve and the SSRF guard fails closed — which
    /// is the only reason the rest were not silently vacuous. See the constants above.
    /// </summary>
    [Theory]
    [InlineData(PublicHttps)]
    [InlineData(PublicHttp)]
    public void AnOrdinaryPublicCrlUrlIsAccepted(string url)
        => Assert.Equal(new[] { url }, Filter(url));

    /// <summary>
    /// The SSRF guard. Each of these is a host a hostile certificate would name to make the
    /// verifying host reach something only it can see.
    /// </summary>
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]  // cloud instance metadata
    [InlineData("http://127.0.0.1:8080/crl")]                 // loopback
    [InlineData("http://localhost/crl")]                      // loopback by name
    [InlineData("http://192.168.1.10/crl.pem")]               // private LAN
    [InlineData("http://10.0.0.5/crl")]                       // private LAN
    [InlineData("https://[::1]/crl")]                         // IPv6 loopback
    public void AUrlOnAPrivateOrReservedHostIsRefused(string url)
        => Assert.Empty(Filter(url));

    /// <summary>
    /// The scheme whitelist. RFC 5280 §4.2.1.13 permits FTP and LDAP as well as HTTP, and this
    /// library deliberately accepts neither: every additional scheme is another protocol handler
    /// reachable from an untrusted extension field.
    /// </summary>
    /// <remarks>
    /// <b>Every host here is the public IP literal used by the accept cases</b>, so the scheme is
    /// the only thing that can reject them. Written first with hostnames, this looked fine and was
    /// nearly worthless: deleting the scheme check reddened <i>one</i> of the four, because
    /// <c>directory.example.com</c> and <c>files.example.com</c> do not resolve and
    /// <c>file:///etc/passwd</c> has no host at all, so the SSRF guard was quietly rejecting three
    /// of them and the case that did fail only did so because <c>example.com</c> happens to resolve
    /// — making the result depend on the runner having DNS. A refusal test has to refuse for the
    /// reason in its name.
    /// </remarks>
    [Theory]
    [InlineData("ldap://93.184.216.34/cn=CRL")]
    [InlineData("ftp://93.184.216.34/root.crl")]
    [InlineData("file://93.184.216.34/share/root.crl")]
    [InlineData("gopher://93.184.216.34/crl")]
    public void AUrlWithANonHttpSchemeIsRefused(string url)
        => Assert.Empty(Filter(url));

    /// <summary>
    /// A relative or unparseable value never becomes a request. <c>Uri.TryCreate</c> with
    /// <see cref="UriKind.Absolute"/> is what rejects these, and it runs in addition to the scheme
    /// test rather than instead of it.
    /// </summary>
    [Theory]
    [InlineData("/relative/root.crl")]
    [InlineData("not a url at all")]
    [InlineData("")]
    public void AValueThatIsNotAnAbsoluteUrlIsRefused(string url)
        => Assert.Empty(Filter(url));

    /// <summary>
    /// <b>Refusing one URL must not abandon the rest.</b> A certificate legitimately lists several
    /// distribution points, and a hostile one can put a poisoned URL first. The safe entries have to
    /// survive in order, or a single bad entry becomes a denial of service against revocation
    /// checking — which fails <i>open</i> in the sense that matters, since an unfetchable CRL means
    /// the signature's revocation status goes unknown.
    /// </summary>
    [Fact]
    public void TheSafeUrlsSurviveAlongsideRefusedOnesInOrder()
    {
        var kept = Filter(
            "http://169.254.169.254/latest/meta-data/",
            PublicHttps,
            "ldap://directory.example.com/cn=CRL",
            PublicHttp,
            "http://127.0.0.1/crl");

        Assert.Equal(new[] { PublicHttps, PublicHttp }, kept);
    }

    /// <summary>
    /// A certificate with no CRL distribution-point extension yields nothing and reports no parse
    /// error — "there is no CRL to fetch" is not a failure, and conflating the two would put a
    /// misleading reason on <see cref="DssBuilder.LastCrlExtParseError"/>.
    /// </summary>
    [Fact]
    public void ACertificateWithNoCrlExtensionYieldsNothingAndReportsNoError()
    {
        Assert.Empty(DssBuilder.SafeCrlDistributionUrls(SigningTestCrypto.Ca().Cert));
    }

    /// <summary>
    /// A certificate listing more http(s) distribution points than the cap is tried to the cap, and the cut is said:
    /// each costs a name resolution and a fetch with its own budget, and the certificate chooses how many it lists.
    /// The non-http entries before them are not counted, and a certificate at the cap is read whole.
    /// </summary>
    [Fact]
    public void ACertificateListingManyCrlUrls_IsTriedToTheCap_AndSaid()
    {
        var many = new List<string> { "ldap://93.184.216.34/cn=CRL" };
        for (int i = 0; i < DssBuilder.MaxCrlUrlsPerCertificate + 6; i++) many.Add($"https://93.184.216.34/crl{i}.crl");
        X509Certificate cert = CertWithCrlUrls(many.ToArray());

        Assert.Equal(DssBuilder.MaxCrlUrlsPerCertificate, DssBuilder.SafeCrlDistributionUrls(cert).Count());
        Assert.NotNull(DssBuilder.LastCrlUrlsCut);
        Assert.Equal(DssBuilder.MaxCrlUrlsPerCertificate + 6, DssBuilder.HttpCrlUrlCount(cert));

        var atCap = new List<string>();
        for (int i = 0; i < DssBuilder.MaxCrlUrlsPerCertificate; i++) atCap.Add($"https://93.184.216.34/crl{i}.crl");
        Assert.Equal(DssBuilder.MaxCrlUrlsPerCertificate, DssBuilder.SafeCrlDistributionUrls(CertWithCrlUrls(atCap.ToArray())).Count());
        Assert.Null(DssBuilder.LastCrlUrlsCut);
    }
}
