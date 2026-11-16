// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Images;

/// <summary>
/// The image doors: a chain ending in an image codec is written as that codec's file, not wrapped in
/// a Netpbm header; and a door that takes no password refuses an encrypted document rather than
/// answer "no images" about pages it could not read.
/// </summary>
public sealed class ImageDoorReviewTests
{
    private static byte[] Jpeg()
    {
        using var bmp = new SKBitmap(2, 2);
        bmp.Erase(SKColors.Red);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A page drawing a 2×2 RGB image whose data is a JPEG under /ASCIIHexDecode.</summary>
    private static byte[] Pdf()
    {
        string hex = Convert.ToHexString(Jpeg()) + ">";
        string content = "q 10 0 0 10 0 0 cm /Im0 Do Q";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter [/ASCIIHexDecode /DCTDecode] /Length {hex.Length} >>\nstream\n{hex}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-imgdoor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AChainEndingInDct_IsWrittenAsAJpeg_NotANetpbmFile()
    {
        string dir = NewDir();
        try
        {
            var info = Assert.Single(PdfOperations.ExtractImages(new MemoryStream(Pdf()), dir));
            Assert.Equal("jpeg", info.Format);
            Assert.EndsWith(".jpg", info.FilePath, StringComparison.Ordinal);
            byte[] written = File.ReadAllBytes(info.FilePath);
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, written.Take(3).ToArray());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static byte[] Encrypted()
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(Pdf()), output,
            new EncryptionParameters { UserPassword = "", OwnerPassword = "owner", Algorithm = EncryptionAlgorithm.AES128 });
        return output.ToArray();
    }

    [Fact]
    public void AdversarialEncryptedDocument_IsRefused_ByTheImageDoorsThatTakeNoPassword()
    {
        byte[] enc = Encrypted();
        string dir = NewDir();
        try
        {
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfImageReplacer.FindImages(new MemoryStream(enc)));
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfOperations.ExtractImages(new MemoryStream(enc), dir));
            Assert.Throws<PdfEncryptedDocumentException>(() => PdfImageExporter.CountImages(new MemoryStream(enc), 0));

            // Control: decrypted, the same document's image is found.
            using var plain = new MemoryStream();
            PdfDecryptor.Decrypt(new MemoryStream(enc), plain, "");
            Assert.Single(PdfImageReplacer.FindImages(new MemoryStream(plain.ToArray())));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
