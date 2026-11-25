// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Org.BouncyCastle.Cms;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Where the counter-signer writes the new CMS blob, when the region excluded by
/// <c>/ByteRange</c> is not exactly the <c>&lt;...&gt;</c> string.
/// </summary>
/// <remarks>
/// <para>
/// <c>contentsStart = br0 + br1 + 1</c> assumes the byte at <c>br0 + br1</c> is the <c>'&lt;'</c>.
/// A producer may pad the hole with whitespace — <c>PdfSignatureVerifier</c> explicitly tolerates
/// that ("allow a small slack for whitespace some writers add around it") — so a writer making that
/// assumption would destroy the file: the leading <c>'&lt;'</c> overwritten with a hex digit and the
/// tail spilled past the closing <c>'&gt;'</c> into the following dictionary bytes.
/// </para>
/// <para>
/// The fixture reproduces that shape by widening the declared hole by one byte, which is what a
/// producer that wrote <c>/Contents &lt;…&gt;</c> with an extra space produces. The assertions are on
/// the bytes: the delimiters must still be delimiters afterwards.
/// </para>
/// </remarks>
public sealed class CounterSignatureWriteOffsetTests
{
    [Fact]
    public void APaddedContentsHole_IsWrittenWithoutOverwritingTheDelimiters()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");
        var (signerPfx, signerPw) = SigningTestCrypto.Pfx(signer, root.Cert);
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), signerPfx, signerPw);
        byte[] padded = WidenTheDeclaredHoleByOneByte(signed, out long holeStart);

        // Sanity: the fixture must really be the awkward shape, or the test proves nothing.
        Assert.NotEqual((byte)'<', padded[holeStart]);
        Assert.Equal((byte)'<', padded[holeStart + 1]);

        using var input = new MemoryStream(padded);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw);
        byte[] result = output.ToArray();

        Assert.Equal((byte)'<', result[holeStart + 1]);

        int closing = IndexOfClosingDelimiter(result, holeStart);
        Assert.True(closing > 0, "the closing '>' of /Contents was overwritten");

        // And what was written between them is a CMS carrying a counter-signature.
        byte[] contents = HexToBytes(result, holeStart + 2, closing);
        // Trimmed of the reserve padding, as the production readers do.
        var cms = new CmsSignedData(SigningDefaults.TrimToAsn1Object(contents));
        foreach (SignerInformation s in cms.GetSignerInfos().GetSigners())
            Assert.NotEmpty(s.GetCounterSignatures().GetSigners());
    }

    /// <summary>
    /// Rewrites the signature's <c>/ByteRange</c> so its first segment ends one byte earlier,
    /// i.e. the excluded hole begins on the space before <c>'&lt;'</c>. The array is rewritten
    /// inside its existing (space-padded) placeholder, so every other offset in the file is
    /// unchanged.
    /// </summary>
    private static byte[] WidenTheDeclaredHoleByOneByte(byte[] pdf, out long holeStart)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int arrayAt = text.IndexOf("/ByteRange [", StringComparison.Ordinal);
        Assert.True(arrayAt > 0, "the fixture carries no /ByteRange");

        int open = text.IndexOf('[', arrayAt);
        int close = text.IndexOf(']', open);
        string[] parts = text.Substring(open + 1, close - open - 1)
                             .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, parts.Length);

        long a = long.Parse(parts[0]), b = long.Parse(parts[1]);
        long c = long.Parse(parts[2]), d = long.Parse(parts[3]);

        // The signer excludes both delimiters, so a + b is the '<'. Move the boundary back one.
        Assert.Equal((byte)'<', pdf[a + b]);
        holeStart = a + b - 1;

        string replacement = $"[{a} {b - 1} {c} {d}]";
        int spanLength = close - open + 1;
        Assert.True(replacement.Length <= spanLength,
            "the rewritten array does not fit the original placeholder");
        replacement = replacement.PadRight(spanLength);

        byte[] result = (byte[])pdf.Clone();
        Encoding.ASCII.GetBytes(replacement).CopyTo(result, open);
        return result;
    }

    private static int IndexOfClosingDelimiter(byte[] pdf, long holeStart)
    {
        for (long i = holeStart + 2; i < pdf.Length; i++)
        {
            if (pdf[i] == (byte)'>') return (int)i;
            bool hex = (pdf[i] >= '0' && pdf[i] <= '9')
                    || (pdf[i] >= 'a' && pdf[i] <= 'f')
                    || (pdf[i] >= 'A' && pdf[i] <= 'F');
            if (!hex) return -1;   // something that is neither hex nor the delimiter: corrupted
        }
        return -1;
    }

    private static byte[] HexToBytes(byte[] pdf, long start, long end)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        for (long i = start; i + 1 < end; i += 2)
        {
            int hi = Convert.ToInt32(((char)pdf[i]).ToString(), 16);
            int lo = Convert.ToInt32(((char)pdf[i + 1]).ToString(), 16);
            bytes.Add((byte)((hi << 4) | lo));
        }
        return bytes.ToArray();
    }
}
