// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The revocation walk asks about each certificate under the certificate that issued it, whatever
/// order the chain arrives in.
/// </summary>
/// <remarks>
/// <para><c>DssBuilder</c> took the chain's list order as the certification path, pairing each
/// certificate with the next one as its issuer. <c>PdfLtvRefresher</c> hands it the certificates a
/// signature's CMS carried, and a CMS certificate set is a SET OF — DER sorts it by encoding — so with
/// an intermediate in the chain the leaf was asked about under the root and the root under the
/// intermediate. An external signer returning its chain in any other order did the same through
/// <c>PdfSigner</c>. A responder answers such a request with "unknown" or nothing.</para>
///
/// <para><b>No network.</b> These certificates name no OCSP responder and no CRL distribution point,
/// so every link yields a warning naming it and its issuer — which is what makes the walk's pairs
/// observable. Since 2026-10-21 the synchronous and asynchronous entries run one walk; until then the
/// asynchronous copy's warnings named the subject only.</para>
/// </remarks>
public sealed class DssChainOrderTests
{
    private static readonly Lazy<(X509Certificate Root, X509Certificate Intermediate, X509Certificate Leaf)> Chain = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Dss Order Root");
        var intermediate = SigningTestCrypto.IssuedIntermediate(root, SigningTestCrypto.Keys(), "CN=Dss Order Intermediate");
        var leaf = SigningTestCrypto.Leaf(intermediate, "CN=Dss Order Leaf");
        return (root.Cert, intermediate.Cert, leaf.Cert);
    }));

    public static IEnumerable<object[]> Orders()
    {
        yield return new object[] { "leaf, intermediate, root" };   // the control: already a path
        yield return new object[] { "leaf, root, intermediate" };
        yield return new object[] { "root, intermediate, leaf" };
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public void EachCertificateIsAskedAboutUnderItsIssuer(string order)
    {
        DssBuilder.TryBuildDss(InOrder(order), Chain.Value.Leaf, out _, out _, out var warnings);

        Assert.Contains(warnings, w => w.Contains("'CN=Dss Order Leaf' (issuer: 'CN=Dss Order Intermediate')", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'CN=Dss Order Intermediate' (issuer: 'CN=Dss Order Root')", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("certificate 'CN=Dss Order Root'", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task TheAsynchronousWalkAsksEachCertificateUnderItsIssuer_AsTheSynchronousOneDoes(string order)
    {
        var (asyncOk, _, _, asyncWarnings) = await DssBuilder.TryBuildDssAsync(InOrder(order), Chain.Value.Leaf);
        bool syncOk = DssBuilder.TryBuildDss(InOrder(order), Chain.Value.Leaf, out _, out _, out var syncWarnings);

        var asked = asyncWarnings.Where(w => w.StartsWith("No OCSP response returned for", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[]
        {
            "No OCSP response returned for certificate 'CN=Dss Order Leaf' (issuer: 'CN=Dss Order Intermediate').",
            "No OCSP response returned for certificate 'CN=Dss Order Intermediate' (issuer: 'CN=Dss Order Root').",
        }, asked);
        Assert.Equal(syncWarnings, asyncWarnings);
        Assert.Equal(syncOk, asyncOk);
    }

    /// <summary>
    /// A chain missing the leaf's issuer has no link to ask about, and says so — rather than asking
    /// about the leaf under the root, or saying nothing.
    /// </summary>
    [Fact]
    public void AChainMissingAnIssuerSaysWhichCertificateCannotBeChecked()
    {
        var chain = new List<X509Certificate> { Chain.Value.Leaf, Chain.Value.Root };

        bool obtained = DssBuilder.TryBuildDss(chain, Chain.Value.Leaf, out _, out _, out var warnings);

        Assert.False(obtained);
        Assert.Contains(warnings, w => w.Contains("issuer of certificate 'CN=Dss Order Leaf'", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("(issuer: 'CN=Dss Order Root')", StringComparison.Ordinal));
    }

    private static List<X509Certificate> InOrder(string order) =>
        order.Split(new[] { ", " }, StringSplitOptions.None).Select(name => name switch
        {
            "leaf" => Chain.Value.Leaf,
            "intermediate" => Chain.Value.Intermediate,
            _ => Chain.Value.Root,
        }).ToList();
}
