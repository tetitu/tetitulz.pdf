// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>Separations as a Photoshop document (<see cref="PdfSeparations.ToPsd"/>).</summary>
/// <remarks>
/// Checked while it was written against the reference renderer's PSD output of the same page, both files read by
/// an independent reader 1.25: the same header, the same channel names, and every plane equal but the black one, which is one
/// level apart where the ink is 0.1 (25.5: rounded here, truncated there — the separation renderer's documented
/// tolerance). The reference renderer's planes are pinned below.
/// </remarks>
public class PsdOutputTests
{
    /// <summary>
    /// A 40 × 30 page: process CMYK 0.2 0.4 0.6 0.1 over x 0 … 24, a spot ink (<c>SPOT 185 C</c>, CMYK 0 .91 .76 0
    /// at full tint) at 0.6 over x 16 … 40, knocking the process inks out where they meet.
    /// </summary>
    internal static byte[] SpotPage(string tintTransform = "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0.91 0.76 0] /N 1 >>",
                                    string spotName = "SPOT#20185#20C") => HalftoneTests.Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 30] /Resources << /ColorSpace << /Cs1 [/Separation /{spotName} /DeviceCMYK 5 0 R] >> >> /Contents 4 0 R >>",
        HalftoneTests.Stream("", Encoding.ASCII.GetBytes("0.2 0.4 0.6 0.1 k 0 0 24 30 re f /Cs1 cs 0.6 scn 16 0 24 30 re f")),
        tintTransform);

    [Fact]
    public void ToPsd_IsTheReferencePsd_ProcessPlatesThenTheSpot()
    {
        var separations = PdfSeparationRenderer.RenderPage(new MemoryStream(SpotPage()), 0, 72);
        var psd = Psd.Read(separations.ToPsd());

        Assert.Equal((1, 5, 40, 30, 8, 4), (psd.Version, psd.Channels, psd.Width, psd.Height, psd.Depth, psd.Mode));
        Assert.Equal(1, psd.Compression);   // PackBits

        // The reference renderer's planes at x 2 (process), 20 (spot over the process, which it knocked out) and 35 (spot):
        // stored as 255 − ink.
        int[,] reference =
        {
            { 204, 255, 255 },   // Cyan 0.2
            { 153, 255, 255 },   // Magenta 0.4
            { 102, 255, 255 },   // Yellow 0.6
            { 230, 255, 255 },   // Black 0.1 — 25.5 truncated; 229 here, rounded
            { 255, 102, 102 },   // SPOT 185 C at 0.6
        };
        int[] xs = { 2, 20, 35 };
        for (int c = 0; c < 5; c++)
            for (int i = 0; i < 3; i++)
                for (int y = 0; y < 30; y++)
                    Assert.InRange(psd.Planes[c][y * 40 + xs[i]], reference[c, i] - 1, reference[c, i]);

        // Every plane is its separation, inverted.
        for (int c = 0; c < 5; c++)
            Assert.Equal(separations.Plates[c].Tints.Select(t => (byte)(255 - t)), psd.Planes[c]);
    }

    [Fact]
    public void ToPsd_NamesTheSpotChannel_ShowsItInItsColour_AndRecordsTheResolution()
    {
        var psd = Psd.Read(PdfSeparationRenderer.RenderPage(new MemoryStream(SpotPage()), 0, 150).ToPsd());

        Assert.Equal(new[] { "SPOT 185 C" }, PascalStrings(psd.Resources[1006]));

        // ResolutionInfo: 150 dpi as 16.16 fixed, pixels per inch (1) in inches (1), across and down.
        Assert.Equal(Be(150, 0, 1, 1, 150, 0, 1, 1), psd.Resources[1005]);

        // The ink's sRGB at full tint — CMYK 0 .91 .76 0 by the naive conversion, (255, 23, 61) — as RGB (0),
        // sixteen bits a component, solidity 0, kind 2 (a spot channel): the reference renderer's fourteen-byte 1007 and
        // the thirteen-byte 1077 an independent reader reads after its version.
        byte[] colour = Be(0, 255 * 257, 23 * 257, 61 * 257, 0, 0);
        Assert.Equal(colour.Concat(new byte[] { 2, 0 }), psd.Resources[1007]);
        Assert.Equal(Be(0, 1).Concat(colour).Concat(new byte[] { 2 }), psd.Resources[1077]);
    }

    [Fact]
    public void ToPsd_WithoutSpots_IsFourChannelsAndNoChannelResources()
    {
        byte[] pdf = HalftoneTests.Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("0 1 0 0 k 0 0 5 10 re f")));
        var psd = Psd.Read(PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72).ToPsd());

        Assert.Equal(4, psd.Channels);
        Assert.Equal(new[] { 1005 }, psd.Resources.Keys.OrderBy(k => k));
        Assert.Equal(0, psd.Planes[1][2]);     // magenta at full ink where painted
        Assert.Equal(255, psd.Planes[1][7]);   // and none beside it
    }

    [Fact]
    public void ToPsd_ASpotWithNoTintTransform_IsShownBlack_AndReported()
    {
        // The plate needs only the tint; the colour it is shown in needs the transform, here a number.
        byte[] pdf = SpotPage("42");
        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == thread && kind == RenderDiagnosticKind.SeparationApproximated)
                lock (reports) reports.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        byte[] file;
        try { file = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72).ToPsd(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        var psd = Psd.Read(file);
        Assert.Equal(5, psd.Channels);
        Assert.Equal(Be(0, 0, 0, 0, 0, 0).Concat(new byte[] { 2, 0 }), psd.Resources[1007]);
        Assert.Contains(reports, r => r.Contains("PSD shows it black", StringComparison.Ordinal));
    }

    [Fact]
    public void ToPsd_WritesANameOutsideAsciiWithQuestionMarks()
    {
        var psd = Psd.Read(PdfSeparationRenderer.RenderPage(new MemoryStream(SpotPage(spotName: "Gr#C3#BCn")), 0, 72).ToPsd());
        string name = PascalStrings(psd.Resources[1006]).Single();
        Assert.StartsWith("Gr", name, StringComparison.Ordinal);
        Assert.EndsWith("n", name, StringComparison.Ordinal);
        Assert.Contains("?", name, StringComparison.Ordinal);
        Assert.All(name, ch => Assert.InRange(ch, ' ', '~'));
    }

    [Fact]
    public void EncodePsd_RefusesWhatAPsdCannotHold()
    {
        static (string, byte[], SKColor?) Ink(string name, int n) => (name, new byte[n], null);
        var four = new[] { "Cyan", "Magenta", "Yellow", "Black" };

        Assert.Throws<Core.PdfException>(() => PdfImageEncoder.EncodePsd(30001, 1, four.Select(n => Ink(n, 30001)).ToList(), 72));
        Assert.Throws<Core.PdfException>(() => PdfImageEncoder.EncodePsd(1, 30001, four.Select(n => Ink(n, 30001)).ToList(), 72));
        var fiftySeven = four.Concat(Enumerable.Range(0, 53).Select(i => "S" + i)).Select(n => Ink(n, 1)).ToList();
        Assert.Throws<Core.PdfException>(() => PdfImageEncoder.EncodePsd(1, 1, fiftySeven, 72));
        // ResolutionInfo's signed 16.16 field: 32 768 dpi overflowed it (an OverflowException, not a refusal).
        var past = Assert.Throws<Core.PdfException>(() => PdfImageEncoder.EncodePsd(1, 1, four.Select(n => Ink(n, 1)).ToList(), 32768));
        Assert.Contains("resolution of at most 32767 dpi", past.Message, StringComparison.Ordinal);

        // The limits themselves are accepted.
        Assert.Equal(30000, Psd.Read(PdfImageEncoder.EncodePsd(30000, 1, four.Select(n => Ink(n, 30000)).ToList(), 72)).Width);
        Assert.Equal(56, Psd.Read(PdfImageEncoder.EncodePsd(1, 1, fiftySeven.Take(56).ToList(), 72)).Channels);
        Assert.Equal(Be(32767, 0, 1, 1, 32767, 0, 1, 1),
                     Psd.Read(PdfImageEncoder.EncodePsd(1, 1, four.Select(n => Ink(n, 1)).ToList(), 32767)).Resources[1005]);
    }

    // ------------------------------------------------------------------ a reader of the specification's layout

    private static byte[] Be(params int[] shorts)
    {
        var b = new byte[shorts.Length * 2];
        for (int i = 0; i < shorts.Length; i++) { b[2 * i] = (byte)(shorts[i] >> 8); b[2 * i + 1] = (byte)shorts[i]; }
        return b;
    }

    private static List<string> PascalStrings(byte[] data)
    {
        var names = new List<string>();
        for (int at = 0; at < data.Length && data[at] > 0;)
        {
            int n = data[at];
            names.Add(Encoding.ASCII.GetString(data, at + 1, n));
            at += 1 + n;
        }
        return names;
    }

    /// <summary>A PSD read as Adobe's specification lays it out: header, colour mode data, image resources, layers, image data.</summary>
    private sealed class Psd
    {
        public int Version, Channels, Width, Height, Depth, Mode, Compression;
        public readonly Dictionary<int, byte[]> Resources = new();
        public readonly List<byte[]> Planes = new();

        public static Psd Read(byte[] file)
        {
            int at = 0;
            int U16() { int v = (file[at] << 8) | file[at + 1]; at += 2; return v; }
            int U32() { int v = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3]; at += 4; return v; }

            Assert.Equal("8BPS", Encoding.ASCII.GetString(file, 0, 4));
            at = 4;
            var psd = new Psd { Version = U16() };
            Assert.Equal(new byte[6], file.Skip(at).Take(6).ToArray());
            at += 6;
            psd.Channels = U16(); psd.Height = U32(); psd.Width = U32(); psd.Depth = U16(); psd.Mode = U16();
            // A length is read before it is skipped: "at += U32()" reads at first and loses the four bytes.
            int colourModeData = U32();
            at += colourModeData;

            int resourcesEnd = U32() + at;
            while (at < resourcesEnd)
            {
                Assert.Equal("8BIM", Encoding.ASCII.GetString(file, at, 4));
                at += 4;
                int id = U16();
                int nameLength = file[at];
                at += (1 + nameLength + 1) & ~1;
                int size = U32();
                psd.Resources[id] = file.Skip(at).Take(size).ToArray();
                at += (size + 1) & ~1;
            }
            Assert.Equal(resourcesEnd, at);
            int layers = U32();
            at += layers;

            psd.Compression = U16();
            Assert.Equal(1, psd.Compression);
            var counts = new int[psd.Channels * psd.Height];
            for (int i = 0; i < counts.Length; i++) counts[i] = U16();
            for (int c = 0; c < psd.Channels; c++)
            {
                var plane = new List<byte>(psd.Width * psd.Height);
                for (int y = 0; y < psd.Height; y++)
                {
                    int end = at + counts[c * psd.Height + y], rowStart = plane.Count;
                    while (at < end)
                    {
                        sbyte n = (sbyte)file[at++];
                        if (n >= 0) { plane.AddRange(file.Skip(at).Take(n + 1)); at += n + 1; }
                        else if (n != -128) { plane.AddRange(Enumerable.Repeat(file[at], 1 - n)); at++; }
                    }
                    Assert.Equal(psd.Width, plane.Count - rowStart);
                }
                psd.Planes.Add(plane.ToArray());
            }
            Assert.Equal(file.Length, at);
            return psd;
        }
    }
}
