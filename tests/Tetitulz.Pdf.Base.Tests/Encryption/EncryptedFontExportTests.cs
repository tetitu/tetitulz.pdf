// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// Opened without its password, an encrypted document has no pages, and
/// <see cref="PdfFontExporter.ExtractFonts"/> answered an empty list with no error — "embeds no
/// fonts". It says why now, through its own <c>LastError</c>, as the facade's reads do through
/// <c>LastExtractionError</c>. The facade's password overload still extracts.
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedFontExportTests
{
    private const string UserPassword = "s3cret";

    public EncryptedFontExportTests() => PdfEncryptionOperations.Register();

    [Fact]
    public void ExtractFonts_OnAnEncryptedDocument_SaysWhyItFoundNone()
    {
        var fonts = PdfFontExporter.ExtractFonts(new MemoryStream(Encrypt(FontPdf())));

        Assert.Empty(fonts);
        Assert.Contains("encrypted", PdfFontExporter.LastError);
    }

    /// <summary>The acceptance control: with the password, the font comes back, byte for byte.</summary>
    [Fact]
    public void ThePasswordRoute_StillExtractsTheFont()
    {
        var font = Assert.Single(PdfOperations.ExtractFonts(new MemoryStream(Encrypt(FontPdf())), UserPassword));
        Assert.Equal(MinimalTrueType.Build("AB"), font.Data);
    }

    private static byte[] FontPdf()
    {
        string program = Encoding.Latin1.GetString(MinimalTrueType.Build("AB"));
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /FontFile2 7 0 R >>",
            $"<< /Length {program.Length} >>\nstream\n{program}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(plain), output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }
}
