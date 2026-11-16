// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The shape of a <c>/ByteRange</c>: two spans from the start of the file, with the
/// <c>/Contents</c> hole between them. Found by the overnight sweep, where the hole's own check
/// survived every mutation.
/// </summary>
public sealed class ByteRangeShapeTests
{
    /// <summary>
    /// A <c>/ByteRange</c> whose second span starts where the first ends has no hole: the
    /// signature would have to sign its own <c>/Contents</c>. It is reported as unexpected gaps —
    /// the shape is wrong, whatever the digest then says. The control is the file as signed, whose
    /// range has its hole and no such error.
    /// </summary>
    [Fact]
    public void ARangeWithNoHoleForTheContentsIsReportedAsMisshapen()
    {
        var root = SigningTestCrypto.Ca("CN=ByteRange Shape Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=ByteRange Shape Signer");
        byte[] signed = Sign(leaf, root);

        Assert.DoesNotContain(Verify(signed, root).ValidationErrors,
            e => e.Contains("unexpected gaps", System.StringComparison.Ordinal));

        var closed = Verify(CloseTheHole(signed), root);

        Assert.False(closed.IsValid);
        Assert.Contains(closed.ValidationErrors,
            e => e.Contains("unexpected gaps", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Rewrites <c>[0 a b c]</c> as <c>[0 a a b+c-a]</c> — the same end of coverage, no hole —
    /// padded to the original width so no offset in the file moves.
    /// </summary>
    private static byte[] CloseTheHole(byte[] pdf)
    {
        string text = Encoding.GetEncoding("ISO-8859-1").GetString(pdf);
        var m = Regex.Match(text, @"/ByteRange\s*\[\s*0\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(m.Success, "The fixture has no /ByteRange to rewrite.");
        long a = long.Parse(m.Groups[1].Value), b = long.Parse(m.Groups[2].Value), c = long.Parse(m.Groups[3].Value);

        string body = $"/ByteRange [0 {a} {a} {b + c - a}";
        Assert.True(body.Length < m.Length, "The rewritten range must fit the original's width.");
        string rewritten = body + new string(' ', m.Length - body.Length - 1) + "]";

        byte[] result = (byte[])pdf.Clone();
        Encoding.ASCII.GetBytes(rewritten).CopyTo(result, m.Index);
        return result;
    }

    private static byte[] Sign(SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root)
    {
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
        });
        return output.ToArray();
    }

    private static SignatureVerificationResult Verify(byte[] pdf, SigningTestCrypto.Issued root)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
        }).Single();
    }
}
