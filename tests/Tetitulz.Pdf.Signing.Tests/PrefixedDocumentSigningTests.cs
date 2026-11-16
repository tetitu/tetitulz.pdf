// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A document with bytes before its <c>%PDF-</c> header signs, chains and verifies.
/// </summary>
/// <remarks>
/// <para>ISO 32000-2 §7.5.2: byte offsets "shall be calculated from the PERCENT SIGN", and its note
/// allows arbitrary leading bytes. The signer's refusal for a document whose previous
/// cross-reference section cannot be verified read the section's offset as a position from byte 0
/// — through Core's probe — so a conforming prefixed document, whose offsets count from the header,
/// was refused as unnameable. The probe now tries the header-relative position first, and this
/// pins the whole path: the signature is written, the revision chains to the original's section
/// (a <c>/Prev</c>, not a re-declared document), the page is still reachable, and the signature
/// covers the file to its end.</para>
///
/// <para>The control for the refusal itself is <see cref="UnnameableXrefSigningTests"/>; this is
/// its floor for a file that is merely prefixed rather than damaged.</para>
/// </remarks>
public sealed class PrefixedDocumentSigningTests
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("junk-junk-junk-junk\n");

    [Fact]
    public void AConformingPrefixedDocumentSignsChainsAndVerifies()
    {
        var (pfx, pw) = Certificate();
        byte[] prefixed = Prefixed(SignedPdfFixtures.MinimalPdf());

        using var input = new MemoryStream(prefixed);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, Parameters(pfx, pw));
        byte[] signed = output.ToArray();

        Assert.True(signed.Length > prefixed.Length, "nothing was appended");
        Assert.Equal(Prefix, signed.Take(Prefix.Length).ToArray());   // the prefix is part of the signed bytes

        using (var ms = new MemoryStream(signed))
        using (var doc = PdfDocument.Open(ms))
        {
            Assert.Single(doc.Pages);   // the page is still named — the revision chained rather than re-declared
            Assert.IsType<PdfNumber>(doc.Parser.Trailer[PdfName.Prev]);
        }

        using var verify = new MemoryStream(signed);
        var result = new PdfSignatureVerifier().VerifyAll(PdfDocument.Open(verify)).Single();
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.True(result.CoversEntireDocument);
    }

    // ------------------------------------------------------------------

    private static byte[] Prefixed(byte[] pdf)
    {
        var all = new byte[Prefix.Length + pdf.Length];
        Buffer.BlockCopy(Prefix, 0, all, 0, Prefix.Length);
        Buffer.BlockCopy(pdf, 0, all, Prefix.Length, pdf.Length);
        return all;
    }

    private static (byte[] Pfx, string Password) Certificate()
    {
        var root = SigningTestCrypto.Ca("CN=Prefixed Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Prefixed Leaf");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }

    private static SignatureParameters Parameters(byte[] pfx, string pw) => new()
    {
        CertificateStream = new MemoryStream(pfx),
        CertificatePassword = pw,
        SignatureFieldName = "Signature1",
        Reason = "test",
    };
}
