// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// ICC.1 lut-based profiles — <c>A2B0</c> in its <c>lut8Type</c>/<c>lut16Type</c> form.
///
/// <para><b>What was missing.</b> Only matrix/TRC (RGB) and <c>kTRC</c> (grey) were evaluated, and
/// <b>every CMYK profile is lut-based</b> — four inks have no matrix form — so the entire CMYK half
/// of ICC colour management fell through to <c>PdfColor.CmykToRgb</c>, a fixed formula that knows
/// nothing about the press the profile describes. A CMYK profile also almost always declares a
/// <c>Lab</c> PCS, which the parser rejected outright one check earlier.</para>
///
/// <para><b>How these fixtures avoid grading my own homework.</b> A synthetic profile whose CLUT is
/// written with the same formula the evaluator reads it back with would pass under any
/// self-consistent encoding, including a wrong one. So the CLUT entries here are written as the raw
/// integers the ICC encoding is <i>defined by</i> — <c>0xFF00</c> is L* = 100, <c>0x8000</c> is
/// a* = b* = 0 — and the expected colour is computed by asking <c>PdfColor.LabToSRgb</c> for that
/// Lab value. The landmark constants come from the specification, not from
/// <c>IccLut.DecodeLab</c>; an implementation that treated full scale as L* = 100 (the obvious
/// reading, and wrong by 0.4%) disagrees with them.</para>
/// </summary>
public class IccLutProfileTests
{
    /// <summary>
    /// The claim: a 4-component <c>/ICCBased</c> fill colour is resolved through the profile's own
    /// table rather than through <c>PdfColor.CmykToRgb</c>. The CLUT is built so that every entry
    /// is the encoded Lab of a strong green, which no CMYK-to-RGB formula would ever produce for
    /// the operands used — that is what makes the assertion able to tell the two apart.
    /// </summary>
    [Fact]
    public void ACmykIccBasedFill_IsResolvedThroughTheProfilesOwnLut()
    {
        // Lab (60, -60, 40): a green well outside anything (1-c)(1-k) yields for 0.2/0.2/0.2/0.2.
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        byte[] profile = CmykLabLutProfile(lab);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, components: 4, operands: "0.2 0.2 0.2 0.2"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        SKColor expected = PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b);
        Assert.True(PixelAssert.Near(painted, expected, tolerance: 6),
            $"expected the profile's own colour {expected}, got {painted}");

        // The control that makes the assertion mean something: this is NOT what the device
        // fallback produces, so a run that quietly ignored the profile could not pass.
        SKColor deviceFallback = PdfColor.CmykToRgb(0.2f, 0.2f, 0.2f, 0.2f);
        Assert.False(PixelAssert.Near(painted, deviceFallback, tolerance: 24),
            $"the painted colour {painted} is the DeviceCMYK fallback {deviceFallback} — the " +
            "profile was not consulted, and the primary assertion would be passing by accident");
    }

    /// <summary>
    /// The legacy Lab encoding, asserted against its own landmarks. <c>0xFF00</c> / <c>0x8000</c> /
    /// <c>0x8000</c> is L* = 100, a* = b* = 0 — the PCS white — which must come back as sRGB white.
    ///
    /// <para>This is the case that catches the plain-scaling mistake: reading full scale as
    /// L* = 100 puts the white point at L* ≈ 99.6, a visible grey.</para>
    /// </summary>
    [Fact]
    public void TheLabEncodingsWhitePoint_RendersAsWhite()
    {
        byte[] profile = CmykLutProfileRaw(l16: 0xFF00, a16: 0x8000, b16: 0x8000);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 4, "0 0 0 0"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        // EXACTLY white, and the exactness is the whole assertion. 0xFF00 × 100 / 65280 is 100
        // and 0x8000 × 255 / 65280 is 128, both integers — the encoding's landmarks are chosen so
        // the PCS white lands on them dead on. Reading full scale as L* = 100 instead puts it at
        // L* 99.61, which is 254 rather than 255: one count, invisible to a human and invisible to
        // any tolerance above zero. A tolerance of 3 here let that mutation through, which is what
        // this comment exists to stop someone kindly restoring.
        Assert.Equal(SKColors.White, painted);
    }

    /// <summary>
    /// An RGB profile carrying <b>only</b> an <c>A2B0</c> lut and no <c>rXYZ</c>/<c>gXYZ</c>/
    /// <c>bXYZ</c> matrix. It used to report "missing one of the rXYZ/gXYZ/bXYZ tags" and stop;
    /// the matrix form is still preferred when present, and this is the fall-through.
    /// </summary>
    /// <remarks>
    /// Asserted on the profile at absolute colorimetric, which reads the same table uncompensated.
    /// The fixture's lut is one colour everywhere, so its darkest colorant — its black point — is
    /// that colour, and black-point compensation (on by default since 2026-10-03, as the reference renderer and
    /// a common viewer have it) would take it to black: right for the profile, useless for this claim.
    /// </remarks>
    [Fact]
    public void AnRgbProfileWithOnlyALut_FallsThroughToIt()
    {
        var lab = (l: 40.0, a: 55.0, b: -50.0);   // a purple, unlike any plain 0.5/0.5/0.5 reading
        byte[] profile = LutProfile("RGB ", inputs: 3, lab: lab, sixteenBit: true);

        IccProfile parsed = IccProfile.Parse(profile);
        Assert.Equal(IccModel.LutAToB, parsed.Model);
        Assert.True(parsed.TryEvaluate(new[] { 0.5f, 0.5f, 0.5f }, IccRenderingIntent.AbsoluteColorimetric, out SKColor painted));

        Assert.True(PixelAssert.Near(painted, PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b),
                                     tolerance: 6),
            $"a lut-only RGB profile must be evaluated through its lut; got {painted}");
    }

    /// <summary>
    /// The 8-bit form, which real profiles use with a Lab PCS. Its landmarks are the same values
    /// read at lower precision — 0xFF is L* = 100, 0x80 is a* = 0 — so a single shared scale
    /// constant applied to both widths would be wrong for one of them.
    /// </summary>
    [Fact]
    public void TheEightBitLutForm_IsReadWithItsOwnLandmarks()
    {
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        byte[] profile = LutProfile("CMYK", inputs: 4, lab: lab, sixteenBit: false);

        using var bmp = PixelAssert.Render(IccFillPdf(profile, 4, "0.2 0.2 0.2 0.2"));
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        // Wider tolerance than the 16-bit case, and it is the encoding's rather than the reader's:
        // one 8-bit step is 0.39 of an L* unit and half an a*/b* unit.
        Assert.True(PixelAssert.Near(painted, PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b),
                                     tolerance: 10),
            $"expected the profile's colour, got {painted}");
    }

    /// <summary>
    /// Adversarial: a profile whose declared table sizes run past the tag. Nothing may throw and
    /// nothing may read out of bounds — the device fallback is the correct outcome, and the reason
    /// has to reach the caller rather than being swallowed.
    /// </summary>
    [Fact]
    public void AMalformedLutTag_FallsBackAndSaysWhy()
    {
        byte[] profile = CmykLabLutProfile((60, -60, 40));
        // The tag's own size stays as written, but the CLUT grid is inflated so the declared table
        // bytes exceed it by orders of magnitude.
        int tagStart = FindTagOffset(profile, "A2B0");
        profile[tagStart + 10] = 200;   // grid points per axis: 200^4 × 3 samples

        var reasons = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != own) return;
            if (kind == RenderDiagnosticKind.IccTransformFailed)
                lock (reasons) reasons.Add(detail ?? "");
        }

        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var bmp = PixelAssert.Render(IccFillPdf(profile, 4, "0.2 0.2 0.2 0.2"));
            SKColor painted = PixelAssert.At(bmp, 50, 50);
            Assert.True(PixelAssert.Near(painted, PdfColor.CmykToRgb(0.2f, 0.2f, 0.2f, 0.2f), 24),
                "a lut that cannot be read must leave the device conversion in place");
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (reasons)
            Assert.Contains(reasons, r => r.Contains("A2B0"));
    }

    /// <summary>
    /// Parity. Both renderers resolve an <c>[/ICCBased]</c> colour through the one
    /// <c>IccColorTransform</c> entry point, so a lut profile must produce the same colour in the
    /// SVG export as on the canvas. This repository's most-repeated rendering defect is one
    /// renderer gaining a fix and the other silently keeping the old answer, and a colour that
    /// arrives by a shared helper is exactly the shape that has drifted before.
    /// </summary>
    [Fact]
    public void BothRenderersResolveALutProfileToTheSameColour()
    {
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        byte[] pdf = IccFillPdf(CmykLabLutProfile(lab), 4, "0.2 0.2 0.2 0.2");

        using var bmp = PixelAssert.Render(pdf);
        SKColor raster = PixelAssert.At(bmp, 50, 50);

        string svg;
        using (var ms = new MemoryStream(pdf)) svg = PdfOperations.RenderPageToSvg(ms, 0);

        string expected = PdfColor.ToCssRgb(raster);
        Assert.Contains(expected, svg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <b>CMYK image</b> whose <c>/ColorSpace</c> is an <c>[/ICCBased]</c> lut profile is
    /// colour-managed per sample.
    ///
    /// <para><b>Why this could not be done after the decode.</b> The bitmap-level ICC hook runs on
    /// pixels that are already sRGB, and a CMYK image's four components are collapsed by
    /// <c>PdfColor.CmykToRgb</c> before it ever sees them. Recovering them by inverting that
    /// formula is exact only when min(c,m,y) = 0 — a pixel with k = 0 and all of c,m,y non-zero
    /// comes back with k′ &gt; 0 — so the conversion has to happen where the samples still are.
    /// The converter is resolved once per image and consulted per sample at all three decode
    /// sites.</para>
    /// </summary>
    [Fact]
    public void ACmykImage_IsColourManagedPerSample()
    {
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        byte[] pdf = CmykImagePdf(CmykLabLutProfile(lab), c: 51, m: 51, y: 51, k: 51);

        using var bmp = PixelAssert.Render(pdf);
        SKColor painted = PixelAssert.At(bmp, 50, 50);

        Assert.True(PixelAssert.Near(painted, PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b), 8),
            $"the image sample was not run through the profile; got {painted}");
        Assert.False(PixelAssert.Near(painted, PdfColor.CmykToRgb(0.2f, 0.2f, 0.2f, 0.2f), 24),
            "the painted colour is the device conversion — the profile was not consulted");
    }

    /// <summary>
    /// The same image exported to SVG. The raster path gained colour-managed CMYK images and the
    /// SVG path had its own copy of the conversion loop; a fix landing in one renderer only is
    /// this repository's most-repeated rendering defect, and an embedded raster in an SVG export
    /// is exactly where it would hide.
    /// </summary>
    [Fact]
    public void ACmykImage_IsColourManagedInTheSvgExportToo()
    {
        var lab = (l: 60.0, a: -60.0, b: 40.0);
        byte[] pdf = CmykImagePdf(CmykLabLutProfile(lab), c: 51, m: 51, y: 51, k: 51);

        string svg;
        using (var ms = new MemoryStream(pdf)) svg = PdfOperations.RenderPageToSvg(ms, 0);

        // The export embeds the decoded raster as a PNG data URI, so the assertion decodes it
        // back rather than matching text — the samples are the claim, not the markup.
        SKColor exported = DecodeFirstEmbeddedImagePixel(svg);
        Assert.True(PixelAssert.Near(exported,
                        PdfColor.LabToSRgb((float)lab.l, (float)lab.a, (float)lab.b), 8),
            $"the SVG export kept the device conversion; got {exported}");
    }

    private static SKColor DecodeFirstEmbeddedImagePixel(string svg)
    {
        const string marker = "base64,";
        int at = svg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the export embedded no raster image at all, so this asserts nothing");
        int start = at + marker.Length;
        int end = svg.IndexOfAny(new[] { '"', '\'' }, start);
        Assert.True(end > start, "the data URI was not terminated");

        byte[] png = Convert.FromBase64String(svg.Substring(start, end - start));
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        return decoded!.GetPixel(decoded.Width / 2, decoded.Height / 2);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>A CMYK profile, Lab PCS, whose CLUT is uniformly one Lab colour.</summary>
    private static byte[] CmykLabLutProfile((double l, double a, double b) lab) =>
        LutProfile("CMYK", inputs: 4, lab: lab, sixteenBit: true);

    /// <summary>
    /// As <see cref="CmykLabLutProfile"/> but with the encoded integers given directly, so a test
    /// can assert against the encoding's landmarks without going through the decoder's arithmetic.
    /// </summary>
    private static byte[] CmykLutProfileRaw(int l16, int a16, int b16) =>
        BuildProfile("CMYK", "Lab ", 4, sixteenBit: true,
                     sample: new[] { l16, a16, b16 });

    private static byte[] LutProfile(string dataSpace, int inputs,
                                     (double l, double a, double b) lab, bool sixteenBit)
    {
        // The ICC encoding's own definition, written here rather than imported from the evaluator:
        // 0xFF00 is L*=100 and 0x8000 is a*=b*=0 (and 0xFF/0x80 at 8 bits).
        int[] sample = sixteenBit
            ? new[]
            {
                (int)Math.Round(lab.l / 100.0 * 0xFF00),
                (int)Math.Round((lab.a + 128.0) / 255.0 * 0xFF00),
                (int)Math.Round((lab.b + 128.0) / 255.0 * 0xFF00),
            }
            : new[]
            {
                (int)Math.Round(lab.l / 100.0 * 0xFF),
                (int)Math.Round(lab.a + 128.0),
                (int)Math.Round(lab.b + 128.0),
            };
        return BuildProfile(dataSpace, "Lab ", inputs, sixteenBit, sample);
    }

    /// <summary>
    /// A minimal but structurally valid ICC profile: header, a one-entry tag table, and an
    /// <c>A2B0</c> <c>mft1</c>/<c>mft2</c> tag with identity input/output curves and a CLUT of one
    /// repeated sample. A constant CLUT is deliberate — the test is about the encoding and the
    /// plumbing, and a constant makes the expected colour independent of interpolation.
    /// </summary>
    private static byte[] BuildProfile(string dataSpace, string pcs, int inputs, bool sixteenBit,
                                       int[] sample)
    {
        const int grid = 2;
        const int outputs = 3;
        int curveEntries = sixteenBit ? 2 : 256;
        int sampleBytes = sixteenBit ? 2 : 1;

        var lut = new MemoryStream();
        WriteAscii(lut, sixteenBit ? "mft2" : "mft1");
        WriteU32(lut, 0);
        lut.WriteByte((byte)inputs);
        lut.WriteByte(outputs);
        lut.WriteByte(grid);
        lut.WriteByte(0);
        // 3×3 matrix, identity in s15Fixed16. Ignored for a device input space, written because
        // the layout requires it.
        for (int i = 0; i < 9; i++) WriteU32(lut, (uint)(i % 4 == 0 ? 0x00010000 : 0));
        if (sixteenBit) { WriteU16(lut, curveEntries); WriteU16(lut, curveEntries); }

        // Input curves: identity, one per channel.
        for (int c = 0; c < inputs; c++)
            WriteIdentityCurve(lut, curveEntries, sampleBytes);

        // CLUT: grid^inputs cells, each the same sample.
        int cells = 1;
        for (int i = 0; i < inputs; i++) cells *= grid;
        for (int cell = 0; cell < cells; cell++)
            for (int k = 0; k < outputs; k++)
                WriteSample(lut, sample[k], sampleBytes);

        // Output curves: identity, one per PCS channel.
        for (int c = 0; c < outputs; c++)
            WriteIdentityCurve(lut, curveEntries, sampleBytes);

        byte[] lutBytes = lut.ToArray();

        const int headerBytes = 128;
        int tagTableBytes = 4 + 12;
        int lutOffset = headerBytes + tagTableBytes;
        int total = lutOffset + lutBytes.Length;

        var profile = new byte[total];
        WriteU32At(profile, 0, (uint)total);                 // profile size
        WriteAsciiAt(profile, 12, "prtr");                   // device class
        WriteAsciiAt(profile, 16, dataSpace);
        WriteAsciiAt(profile, 20, pcs);
        WriteAsciiAt(profile, 36, "acsp");
        WriteU32At(profile, 128, 1);                         // tag count
        WriteAsciiAt(profile, 132, "A2B0");
        WriteU32At(profile, 136, (uint)lutOffset);
        WriteU32At(profile, 140, (uint)lutBytes.Length);
        Array.Copy(lutBytes, 0, profile, lutOffset, lutBytes.Length);
        return profile;
    }

    private static int FindTagOffset(byte[] profile, string signature)
    {
        // One tag, at the fixed place BuildProfile puts it.
        Assert.Equal(signature, Encoding.ASCII.GetString(profile, 132, 4));
        return (int)(((uint)profile[136] << 24) | ((uint)profile[137] << 16) |
                     ((uint)profile[138] << 8) | profile[139]);
    }

    private static void WriteIdentityCurve(Stream s, int entries, int sampleBytes)
    {
        int max = sampleBytes == 2 ? 65535 : 255;
        for (int e = 0; e < entries; e++)
            WriteSample(s, entries == 1 ? max : (int)Math.Round((double)e / (entries - 1) * max),
                        sampleBytes);
    }

    private static void WriteSample(Stream s, int value, int sampleBytes)
    {
        if (sampleBytes == 2) WriteU16(s, value);
        else s.WriteByte((byte)Math.Max(0, Math.Min(255, value)));
    }

    private static void WriteAscii(Stream s, string v)
    {
        foreach (byte b in Encoding.ASCII.GetBytes(v)) s.WriteByte(b);
    }

    private static void WriteU16(Stream s, int v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteU32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
    }

    private static void WriteU32At(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void WriteAsciiAt(byte[] b, int off, string v)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(v);
        Array.Copy(bytes, 0, b, off, bytes.Length);
    }

    /// <summary>
    /// A page drawing one 2x2 CMYK image, in an <c>[/ICCBased]</c> space, over the whole page.
    /// Four identical samples, so the assertion does not depend on where in the image it looks.
    /// </summary>
    private static byte[] CmykImagePdf(byte[] profile, byte c, byte m, byte y, byte k)
    {
        var samples = new byte[2 * 2 * 4];
        for (int i = 0; i < 4; i++)
        {
            samples[i * 4] = c; samples[i * 4 + 1] = m;
            samples[i * 4 + 2] = y; samples[i * 4 + 3] = k;
        }

        string content = "q 100 0 0 100 0 0 cm /Im0 Do Q";
        var parts = new List<(string dict, byte[] raw)>
        {
            ("<< /Type /Catalog /Pages 2 0 R >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
             " /Resources << /XObject << /Im0 5 0 R >> >> >>", null),
            ($"<< /Length {content.Length} >>\nstream\n{content}\nendstream", null),
            ("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8" +
             $" /ColorSpace [/ICCBased 6 0 R] /Length {samples.Length} >>", samples),
            ($"<< /N 4 /Length {profile.Length} >>", profile),
        };

        return BuildWithStreams(parts);
    }

    /// <summary>
    /// Assembles objects where some carry raw (non-ASCII) stream payloads, which is why this
    /// cannot go through the string-only builder below.
    /// </summary>
    private static byte[] BuildWithStreams(List<(string dict, byte[] raw)> parts)
    {
        var bytes = new MemoryStream();
        var offsets = new List<int>();
        void Append(string s) => bytes.Write(Encoding.ASCII.GetBytes(s), 0, Encoding.ASCII.GetByteCount(s));

        Append("%PDF-1.7\n");
        for (int i = 0; i < parts.Count; i++)
        {
            offsets.Add((int)bytes.Length);
            Append($"{i + 1} 0 obj\n");
            Append(parts[i].dict);
            if (parts[i].raw != null)
            {
                Append("\nstream\n");
                bytes.Write(parts[i].raw, 0, parts[i].raw.Length);
                Append("\nendstream");
            }
            Append("\nendobj\n");
        }

        int xref = (int)bytes.Length;
        Append($"xref\n0 {parts.Count + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) Append(off.ToString("D10") + " 00000 n \n");
        Append($"trailer\n<< /Size {parts.Count + 1} /Root 1 0 R >>\n");
        Append($"startxref\n{xref}\n%%EOF\n");
        return bytes.ToArray();
    }

    /// <summary>
    /// A page filling itself with one <c>sc</c> colour in an <c>[/ICCBased]</c> space carrying
    /// <paramref name="profile"/>.
    /// </summary>
    private static byte[] IccFillPdf(byte[] profile, int components, string operands)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();

        string content = $"q /CS0 cs {operands} sc 0 0 100 100 re f Q";
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            null,   // the profile stream, written as bytes below
        };

        var bytes = new MemoryStream();
        void Append(string s) => bytes.Write(Encoding.ASCII.GetBytes(s), 0,
                                             Encoding.ASCII.GetByteCount(s));

        Append("%PDF-1.7\n");
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add((int)bytes.Length);
            Append($"{i + 1} 0 obj\n");
            if (objects[i] != null)
            {
                Append(objects[i]);
            }
            else
            {
                Append($"<< /N {components} /Length {profile.Length} >>\nstream\n");
                bytes.Write(profile, 0, profile.Length);
                Append("\nendstream");
            }
            Append("\nendobj\n");
        }

        int xref = (int)bytes.Length;
        Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) Append(off.ToString("D10") + " 00000 n \n");
        Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        Append($"startxref\n{xref}\n%%EOF\n");
        return bytes.ToArray();
    }
}
