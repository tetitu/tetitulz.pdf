// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="VisualSignatureBuilder.Sign(Stream, Stream, byte[], SignatureParameters, int, float, float, float, float)"/>:
/// a visible image signature under the caller's own parameters — an external signer (a smart card's
/// shape) or a certificate stream — with a PNG's transparency kept.
/// </summary>
public sealed class VisualSignatureImageParametersTests
{
    private static readonly Lazy<(SigningTestCrypto.Issued Leaf, SigningTestCrypto.Issued Root)> Keys = new(() =>
    {
        var root = SigningTestCrypto.Ca();
        return (SigningTestCrypto.Leaf(root), root);
    });

    [Fact]
    public void ATransparentPng_UnderAnExternalSigner_VerifiesAndKeepsItsMask()
    {
        var signer = new CountingSigner(Keys.Value.Leaf, Keys.Value.Root.Cert);
        byte[] signed = Sign(Png(30, 12, transparentLeftHalf: true), new SignatureParameters { ExternalSigner = signer, Reason = "Approved" });

        Assert.Equal(1, signer.Calls);
        var result = Verify(signed);
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.Equal("Approved", result.Reason);

        string text = Encoding.Latin1.GetString(signed);
        Assert.Contains("/SMask", text);
        Assert.Matches(@"/Rect \[50\.00 60\.00 170\.00 108\.00\]", text);
    }

    [Fact]
    public void AnOpaquePng_WritesNoMask()
    {
        byte[] signed = Sign(Png(30, 12, transparentLeftHalf: false),
            new SignatureParameters { ExternalSigner = new CountingSigner(Keys.Value.Leaf, Keys.Value.Root.Cert) });

        Assert.True(Verify(signed).IsIntegrityValid);
        Assert.DoesNotContain("/SMask", Encoding.Latin1.GetString(signed));
        Assert.Contains("/FlateDecode", Encoding.Latin1.GetString(signed));   // the PNG's samples are there
    }

    [Fact]
    public void ACertificateStreamInTheParameters_IsUsed_NotOverwritten()
    {
        var (pfx, password) = SigningTestCrypto.Pfx(Keys.Value.Leaf, Keys.Value.Root.Cert);
        byte[] signed = Sign(Png(10, 10, transparentLeftHalf: true),
            new SignatureParameters { CertificateStream = new MemoryStream(pfx), CertificatePassword = password });

        var result = Verify(signed);
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
    }

    [Fact]
    public void TheSignatureNamesTheFieldTheWidgetWasWrittenFor()
    {
        var parameters = new SignatureParameters
        {
            ExternalSigner = new CountingSigner(Keys.Value.Leaf, Keys.Value.Root.Cert),
            SignatureFieldName = "CallersOwnName",
        };
        byte[] signed = Sign(Png(8, 8, transparentLeftHalf: false), parameters);

        string widgetName = Regex.Match(Encoding.Latin1.GetString(signed), @"/FT /Sig /T \((VisualSig\d+)\)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(widgetName));
        Assert.Equal(widgetName, parameters.SignatureFieldName);
        Assert.Equal(widgetName, Verify(signed).FieldName);
    }

    [Theory]
    [InlineData(double.NaN, 40.0)]
    [InlineData(120.0, 0.0)]
    [InlineData(double.PositiveInfinity, 40.0)]
    public void ASizeThatIsNotPositiveAndFinite_IsRefused_BeforeAnythingIsWritten(double width, double height)
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        Assert.ThrowsAny<ArgumentException>(() => VisualSignatureBuilder.Sign(input, output, Png(4, 4, false),
            new SignatureParameters { ExternalSigner = new CountingSigner(Keys.Value.Leaf, Keys.Value.Root.Cert) },
            1, 50, 60, (float)width, (float)height));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void BytesThatAreNeitherPngNorJpeg_AreRefused_AndNothingIsSigned()
    {
        var signer = new CountingSigner(Keys.Value.Leaf, Keys.Value.Root.Cert);
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() => VisualSignatureBuilder.Sign(input, output, Encoding.ASCII.GetBytes("not an image"),
            new SignatureParameters { ExternalSigner = signer }, 1, 50, 60, 120, 48));

        Assert.Contains("PNG", ex.Message);
        Assert.Equal(0, signer.Calls);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Sign(byte[] image, SignatureParameters parameters)
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        VisualSignatureBuilder.Sign(input, output, image, parameters, pageNumber: 1, x: 50, y: 60, width: 120, height: 48);
        return output.ToArray();
    }

    private static SignatureVerificationResult Verify(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { Keys.Value.Root.Cert },
        }).Single();
    }

    /// <summary>An RGBA PNG: dark blue ink, the left half fully transparent when asked.</summary>
    private static byte[] Png(int width, int height, bool transparentLeftHalf)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);   // filter: none
            for (int x = 0; x < width; x++)
            {
                raw.WriteByte(10); raw.WriteByte(20); raw.WriteByte(140);
                raw.WriteByte(transparentLeftHalf && x < width / 2 ? (byte)0 : (byte)255);
            }
        }
        byte[] idat;
        using (var z = new MemoryStream())
        {
            using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zl);
            idat = z.ToArray();
        }

        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, width); WriteBe(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 6;   // 8-bit, RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBe(len, 0, data.Length); s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes); s.Write(data);
        var crc = new byte[4]; WriteBe(crc, 0, (int)Crc32(typeBytes.Concat(data).ToArray())); s.Write(crc);
    }

    private static void WriteBe(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>An external signer over a local key — the shape a smart card's signer has — counting its calls.</summary>
    internal sealed class CountingSigner : IExternalSigner
    {
        private readonly SigningTestCrypto.Issued _leaf;
        private readonly X509Certificate _root;

        public CountingSigner(SigningTestCrypto.Issued leaf, X509Certificate root) { _leaf = leaf; _root = root; }

        public int Calls { get; private set; }

        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            Calls++;
            return CmsSignatureGenerator.GenerateDetachedSignature(
                data, _leaf.Private, _leaf.Cert, new List<X509Certificate> { _leaf.Cert, _root }, digestAlgorithm);
        }

        public byte[] SignDigest(byte[] digest, string digestAlgorithm) => throw new NotSupportedException();

        public IList<byte[]> GetCertificateChain() => new[] { _leaf.Cert.GetEncoded(), _root.GetEncoded() };

        public int EstimatedSignatureSize => 32768;
    }
}
