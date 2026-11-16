// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A counter-signature rewrites the whole <c>/Contents</c> placeholder, padding included — so bytes
/// someone parked in the old padding do not survive into the counter-signed document.
/// </summary>
/// <remarks>
/// <para><b>Why the padding matters here and nowhere else.</b> Every other writer fills a placeholder
/// that is zeros already, so padding the new value out to the reserve changes nothing for them. The
/// counter-signer overwrites a signature that exists, and its padding lies in the <c>/ByteRange</c>
/// gap: unsigned, writable after signing, and exactly where
/// <c>SignatureVerificationOptions.RejectNonZeroContentsPadding</c> looks. Writing only the new CMS
/// would leave whatever followed it, and carry a tamper signal forward under a fresh
/// counter-signature.</para>
/// </remarks>
public sealed class CounterSignatureContentsPaddingTests
{
    [Fact]
    public void BytesInTheOldPaddingAreZeroedByTheCounterSignature()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");
        var (signerPfx, signerPw) = SigningTestCrypto.Pfx(signer, root.Cert);
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), signerPfx, signerPw);
        byte[] parked = ParkBytesInThePadding(signed);

        // The premise: the fixture's padding really is non-zero before the counter-signature.
        SigningDefaults.TrimToAsn1Object(ContentsOf(parked), out bool dirtyBefore);
        Assert.True(dirtyBefore, "the fixture did not put anything in the padding");

        using var input = new MemoryStream(parked);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw);

        SigningDefaults.TrimToAsn1Object(ContentsOf(output.ToArray()), out bool dirtyAfter);
        Assert.False(dirtyAfter);
    }

    /// <summary>Overwrites the last 40 hex digits of <c>/Contents</c> — well inside the zero padding — with <c>AB</c>.</summary>
    private static byte[] ParkBytesInThePadding(byte[] pdf)
    {
        byte[] result = (byte[])pdf.Clone();
        int close = ContentsClose(result);
        for (int i = close - 40; i < close; i += 2)
        {
            Assert.Equal((byte)'0', result[i]);
            result[i] = (byte)'A';
            result[i + 1] = (byte)'B';
        }
        return result;
    }

    private static byte[] ContentsOf(byte[] pdf)
    {
        int open = ContentsOpen(pdf);
        int close = ContentsClose(pdf);
        var bytes = new byte[(close - open - 1) / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(Encoding.ASCII.GetString(pdf, open + 1 + 2 * i, 2), 16);
        return bytes;
    }

    private static int ContentsOpen(byte[] pdf)
    {
        int at = Encoding.ASCII.GetString(pdf).IndexOf("/Contents <", StringComparison.Ordinal);
        Assert.True(at > 0, "the fixture carries no /Contents string");
        return at + "/Contents ".Length;
    }

    private static int ContentsClose(byte[] pdf) => Array.IndexOf(pdf, (byte)'>', ContentsOpen(pdf));
}
