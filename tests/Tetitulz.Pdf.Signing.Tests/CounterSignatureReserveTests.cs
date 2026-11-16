// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A counter-signed CMS that is larger than the original signature's <c>/Contents</c> reserve is
/// refused before anything is written.
/// </summary>
/// <remarks>
/// The reserve cannot grow afterwards — every byte after it is under the original signature's
/// <c>/ByteRange</c> — so the only honest outcomes are fitting and refusing. The comparison is between
/// a byte count and a count of hex digits, two per byte, which is the arithmetic this pins.
/// </remarks>
public sealed class CounterSignatureReserveTests
{
    [Fact]
    public void ACounterSignatureThatDoesNotFitTheReserve_IsRefusedWithoutWritingOutput()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");
        var (signerPfx, signerPw) = SigningTestCrypto.Pfx(signer, root.Cert);
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        // A reserve just large enough for the original signature: the CMS a first signing produces,
        // plus a margin for its signing time's encoding.
        int cmsBytes = SigningDefaults.TrimToAsn1Object(ContentsOf(
            Sign(SignedPdfFixtures.MinimalPdf(), signerPfx, signerPw, reserve: 16384))).Length;
        byte[] tight = Sign(SignedPdfFixtures.MinimalPdf(), signerPfx, signerPw, reserve: cmsBytes + 16);

        using var input = new MemoryStream(tight);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfSigningException>(() =>
            PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw));

        Assert.Contains("does not fit in the reserved", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    private static byte[] Sign(byte[] pdf, byte[] pfx, string password, int reserve)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            EstimatedSignatureSize = reserve,
        });
        return output.ToArray();
    }

    private static byte[] ContentsOf(byte[] pdf)
    {
        int open = Encoding.ASCII.GetString(pdf).IndexOf("/Contents <", StringComparison.Ordinal)
                   + "/Contents ".Length;
        int close = Array.IndexOf(pdf, (byte)'>', open);
        var bytes = new byte[(close - open - 1) / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(Encoding.ASCII.GetString(pdf, open + 1 + 2 * i, 2), 16);
        return bytes;
    }
}
