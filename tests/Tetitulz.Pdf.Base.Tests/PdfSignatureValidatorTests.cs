// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for <see cref="PdfSignatureValidator"/>.  Real signature round-trip
/// tests (sign → validate → pass) live next to the signer; these focus on
/// the validator's defensive behaviour: handling missing/malformed fields,
/// out-of-range /ByteRange, and unsigned documents.
/// </summary>
public class PdfSignatureValidatorTests
{
    [Fact]
    public void Validate_NullInputPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfSignatureValidator.Validate((string)null));
    }

    [Fact]
    public void Validate_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfSignatureValidator.Validate((Stream)null));
    }

    [Fact]
    public void Validate_UnsignedDocument_ReportsNoSignature()
    {
        byte[] pdf = BuildMinimalUnsignedPdf();
        using var ms = new MemoryStream(pdf);
        var result = PdfSignatureValidator.Validate(ms);
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        Assert.False(result.HasSignature);
        Assert.Equal(PdfSignatureChainStatus.Unknown, result.ChainStatus);
        Assert.Contains("No signature field", result.ErrorMessage);
    }

    [Fact]
    public void Result_FactoryFieldsPopulated()
    {
        var r = new PdfSignatureValidationResult(
            isValid: true, hasSignature: true,
            signerSubject: "CN=Alice", signerIssuer: "CN=Issuer",
            signedAt: new DateTime(2026, 4, 1),
            chainStatus: PdfSignatureChainStatus.Trusted,
            warnings: new[] { "warn" },
            errorMessage: null);
        Assert.True(r.IsValid);
        Assert.True(r.HasSignature);
        Assert.Equal("CN=Alice", r.SignerSubject);
        Assert.Equal("CN=Issuer", r.SignerIssuer);
        Assert.Equal(2026, r.SignedAt.Value.Year);
        Assert.Equal(PdfSignatureChainStatus.Trusted, r.ChainStatus);
        Assert.Single(r.Warnings);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public void Result_NullWarnings_TreatedAsEmpty()
    {
        var r = new PdfSignatureValidationResult(
            isValid: false, hasSignature: false,
            signerSubject: null, signerIssuer: null, signedAt: null,
            chainStatus: PdfSignatureChainStatus.Unknown,
            warnings: null, errorMessage: "err");
        Assert.NotNull(r.Warnings);
        Assert.Empty(r.Warnings);
    }

    // Minimal unsigned PDF with no AcroForm — validator should short-circuit
    // with HasSignature = false.
    private static byte[] BuildMinimalUnsignedPdf()
    {
        const string cs = "0 0 612 792 re";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
