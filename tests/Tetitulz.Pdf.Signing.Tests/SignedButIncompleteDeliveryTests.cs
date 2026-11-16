// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Net;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A refusal raised after the signature is written — B-LT asked for, and the test chain names no
/// revocation responder — still leaves the signed document in the output of
/// <see cref="PdfSigningOperations.Sign(Stream, Stream, SignatureParameters)"/>, as
/// <see cref="PdfSigner.Sign"/> leaves it: the façade signed into a buffer and discarded it, so the
/// caller got an empty stream under a message saying the PDF was signed. The exception's type is
/// still exactly <see cref="PdfSigningException"/>.
/// </summary>
public sealed class SignedButIncompleteDeliveryTests
{
    [Fact]
    public void AnLtvShortfallAfterSigning_StillDeliversTheSignedDocument()
    {
        var root = SigningTestCrypto.Ca("CN=Delivery Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Delivery Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        var parameters = new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            TimestampNetwork = new SigningNetworkOptions { Proxy = new WebProxy() },
        }.ApplyPadesBaseline(PadesBaseline.B_LT, tsa.Url);

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfSigningException>(() => PdfSigningOperations.Sign(
            new MemoryStream(SignedPdfFixtures.MinimalPdf()), output, parameters));
        Assert.Contains("signed successfully", ex.Message, System.StringComparison.Ordinal);

        Assert.True(output.Length > 0);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.True(new PdfSignatureVerifier().VerifyAll(doc).Single().IsIntegrityValid);
    }
}
