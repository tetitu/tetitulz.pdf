// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>Inline image embedding in the centralized PDF renderer (PNG decode path).</summary>
public class ImageTests
{
    [Fact]
    public void Png_InlineImage_EmbedsXObject()
    {
        byte[] png = MakeRgbPng(4, 4);
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s
                .AddParagraph(p => p.AddText("Above"))
                .AddParagraph(p => p.AddImage(png, 48, 48)))
            .Build().ToPdf();

        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Image", raw);       // image XObject subtype
        Assert.Contains("/DeviceRGB", raw);   // PNG decoded to RGB (only images name a colour space)
        Assert.Contains("/XObject", raw);     // referenced from the page resources
    }

    [Fact]
    public void Png_Decoder_HandlesAlphaWithoutThrowing()
    {
        // An RGBA PNG should decode (alpha composited over white) and embed.
        byte[] png = MakeRgbaPng(2, 2);
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(png, 24, 24)))
            .Build().ToPdf();
        Assert.Contains("/Subtype /Image", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void BadImageBytes_AreSkippedGracefully()
    {
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddImage(new byte[] { 1, 2, 3, 4 }, 24, 24)))
            .Build().ToPdf();
        // No image XObject, but a valid PDF is still produced.
        Assert.Equal((byte)'%', pdf[0]);
    }

    // ---- minimal PNG synthesis ----

    private static byte[] MakeRgbPng(int w, int h) => MakePng(w, h, colorType: 2, channels: 3);
    private static byte[] MakeRgbaPng(int w, int h) => MakePng(w, h, colorType: 6, channels: 4);

    private static byte[] MakePng(int w, int h, byte colorType, int channels)
    {
        byte[] raw = new byte[h * (1 + w * channels)];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0; // filter: none
            for (int x = 0; x < w; x++)
            {
                raw[p++] = 200; raw[p++] = 50; raw[p++] = 50;        // R,G,B
                if (channels == 4) raw[p++] = 128;                   // A
            }
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(outMs, "IHDR", ihdr);
        WriteChunk(outMs, "IDAT", idat);
        WriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
        byte[] t = Encoding.ASCII.GetBytes(type);
        var crcIn = new byte[4 + data.Length];
        Array.Copy(t, 0, crcIn, 0, 4); Array.Copy(data, 0, crcIn, 4, data.Length);
        s.Write(t, 0, 4); s.Write(data, 0, data.Length);
        uint crc = Crc32(crcIn);
        var crcb = new byte[4]; WriteBE(crcb, 0, (int)crc); s.Write(crcb, 0, 4);
    }

    private static void WriteBE(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
        }
        return c ^ 0xFFFFFFFF;
    }
}
