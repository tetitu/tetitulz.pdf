// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Doors that take no password refuse an encrypted document rather than answer "no signature" about a
/// form they could not read; and every <see cref="VisualSignatureBuilder"/> overload refuses a
/// non-finite rectangle, which '{0:F2}' wrote into <c>/Rect</c> as <c>NaN</c>.
/// </summary>
public sealed class VerificationDoorRefusalTests
{
    /// <summary>A one-page document whose trailer names an encryption dictionary.</summary>
    private static byte[] Encrypted()
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int x = sb.Length;
        string o = new string('A', 64), u = new string('B', 64);
        sb.Append("xref\n0 3\n0000000000 65535 f \n")
          .Append(o1.ToString("D10")).Append(" 00000 n \n")
          .Append(o2.ToString("D10")).Append(" 00000 n \n")
          .Append($"trailer\n<< /Size 3 /Root 1 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>] ")
          .Append($"/Encrypt << /Filter /Standard /V 1 /R 2 /O <{o}> /U <{u}> /P -4 >> >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void AdversarialEncryptedDocument_IsRefused_ByTheDoorsThatTakeNoPassword()
    {
        var a = Assert.Throws<PdfSigningException>(() => PdfSignatureValidator.Validate(new MemoryStream(Encrypted())));
        Assert.Contains("encrypted", a.Message, StringComparison.Ordinal);
        var b = Assert.Throws<PdfSigningException>(() => PdfSigningOperations.VerifySignatures(new MemoryStream(Encrypted())));
        Assert.Contains("PdfEncryptionOperations.VerifySignatures", b.Message, StringComparison.Ordinal);

        // Control: an unencrypted, unsigned document is still answered — no signature, no throw.
        var plain = PdfSignatureValidator.Validate(new MemoryStream(SignedPdfFixtures.MinimalPdf()));
        Assert.False(plain.HasSignature);
    }

    [Theory]
    [InlineData(float.NaN, 80f)]
    [InlineData(100f, float.PositiveInfinity)]
    [InlineData(-5f, 80f)]
    public void AdversarialNonFiniteOrNonPositiveSize_IsRefused_ByTheOlderOverloads(float width, float height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VisualSignatureBuilder.Sign(
            new MemoryStream(SignedPdfFixtures.MinimalPdf()), new MemoryStream(),
            "unused.pfx", "unused", new byte[] { 0xFF, 0xD8 }, 1, 50, 50, width, height));
    }
}
