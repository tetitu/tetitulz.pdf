// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// ExtGState entries whose value is written as an <b>indirect reference</b>, plus the
/// <c>/SMask /Identity</c> spelling — the reads the raster interpreter did not resolve.
/// </summary>
/// <remarks>
/// <para><b>These are the residue of the non-resolving-read sweep</b>
/// (a sweep of its own; not GAPS §15, which is the assembly boundary).
/// That round fixed 117 non-resolving
/// reads and closed; grepping <c>extGs[PdfName.X]</c> across <c>Base/Rendering</c> afterwards still
/// returned three — <c>/TR2</c>, <c>/TR</c> and <c>/SMask</c> — all in
/// <c>ContentStreamInterpreter.ApplyExtGState</c>, three lines below the same method's <c>/BM</c>,
/// <c>/OP</c> and <c>/op</c>, which do resolve. The <c>/D</c> dash read was half-fixed: the outer
/// array resolved, its <i>elements</i> and the phase did not.</para>
///
/// <para><b>The SVG writer had every one of these right</b>, and its own comments say so — the
/// one-renderer-ahead divergence this repository keeps finding, here between two files named to sit
/// side by side. So each case below is a raster test: the SVG side already passes it.</para>
///
/// <para><b>Every case pairs the indirect spelling with the direct one.</b> The direct spellings are
/// the ones the existing suite uses and they always worked, so without the pair a test could pass
/// by the fixture never reaching the feature.</para>
///
/// <para><b>There is deliberately no <c>/TR</c> case, and that is a finding rather than an
/// omission.</b> <c>/TR2</c> and <c>/TR</c> were unresolved too and were fixed with these, but they
/// were not producing wrong output: an indirect entry fell to the else branch and
/// <c>TransferFunctionLut.Build</c> resolves its own argument, so a referenced function was still
/// built and a reference to <c>/Identity</c> still gave a null LUT. The outcomes converge, so a
/// test here would assert a difference that does not exist — which is how an unfalsifiable test
/// gets written with the best of intentions.</para>
/// </remarks>
public class IndirectExtGStateReadTests
{
    // ====================================================================
    //  /D — the dash array's elements and its phase
    // ====================================================================

    /// <summary>
    /// A dash <i>length</i> written as a reference. Unresolved it read as <b>0</b>, making the
    /// pattern <c>[0 10]</c> — zero-length dashes, so the line vanished entirely.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /D [[7 0 R 10] 0]
    [InlineData(false)]  // /D [[10 10] 0]
    public void DashLength_IsResolved(bool indirect)
    {
        var bmp = Render(DashPdf(indirect ? "[7 0 R 10]" : "[10 10]", "0"));

        // Pattern [10 10] phase 0 from x=10: ink over 10..20, gap over 20..30.
        AssertInk(bmp, 15, "the first dash is missing");
        AssertNoInk(bmp, 25, "the first gap was painted");
    }

    /// <summary>
    /// The dash <i>phase</i> written as a reference. Unresolved it read as <b>0</b>, so every
    /// ExtGState dash started at the beginning of its pattern.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /D [[10 10] 7 0 R]
    [InlineData(false)]  // /D [[10 10] 10]
    public void DashPhase_IsResolved(bool indirect)
    {
        var bmp = Render(DashPdf("[10 10]", indirect ? "7 0 R" : "10"));

        // Phase 10 starts 10 units into [10 10] — i.e. in the GAP. So the bands invert:
        // gap over 10..20, ink over 20..30. This is the exact inverse of the test above,
        // which is what makes a phase of 0 detectable rather than merely different.
        AssertNoInk(bmp, 15, "the phase was ignored — the line started with ink, not a gap");
        AssertInk(bmp, 25, "the dash after the phase offset is missing");
    }

    // ====================================================================
    //  /SMask — the /Identity spelling, and an indirect /None
    // ====================================================================

    /// <summary>
    /// <c>/SMask /Identity</c> means "no soft mask". The raster path cleared only on <c>/None</c>,
    /// so the mask installed by the previous <c>gs</c> stayed in force and everything drawn after
    /// it was masked away — visible missing output, not a latent difference.
    /// </summary>
    [Fact]
    public void SoftMaskIdentityName_ClearsAnActiveSoftMask()
    {
        AssertInk(Render(SoftMaskPdf("/Identity")), 100,
            "/SMask /Identity did not clear the active soft mask");
    }

    /// <summary>The same for <c>/None</c> written as a reference.</summary>
    [Fact]
    public void SoftMaskNoneName_IsResolved()
    {
        AssertInk(Render(SoftMaskPdf("7 0 R")), 100,
            "an indirect /SMask /None did not clear the active soft mask");
    }

    /// <summary>The direct spelling, which always worked.</summary>
    [Fact]
    public void SoftMaskNoneName_ClearsAnActiveSoftMask()
    {
        AssertInk(Render(SoftMaskPdf("/None")), 100,
            "/SMask /None did not clear the active soft mask");
    }

    /// <summary>
    /// The floor, and the test the three above are worthless without: with <b>no</b> second
    /// <c>gs</c> the fully-black luminosity mask really does hide the rectangle. Without this,
    /// an implementation that never applied a soft mask at all would pass every case above.
    /// </summary>
    [Fact]
    public void ActiveSoftMask_HidesTheRectangle()
    {
        AssertNoInk(Render(SoftMaskPdf(null)), 100,
            "the luminosity soft mask did not hide anything, so the fixture proves nothing");
    }

    // ------------------------------------------------------------------ assertions

    /// <summary>Pixel at a PDF user-space point (origin bottom-left); the page is 1 px per unit.</summary>
    private static SKColor At(SKBitmap bmp, int userX, int userY)
        => bmp.GetPixel(userX, bmp.Height - 1 - userY);

    private static void AssertInk(SKBitmap bmp, int x, string because)
    {
        var c = At(bmp, x, 100);
        Assert.True(c.Red < 128 && c.Green < 128 && c.Blue < 128,
            $"{because}: expected dark ink at x={x}, got RGB({c.Red},{c.Green},{c.Blue}).");
    }

    private static void AssertNoInk(SKBitmap bmp, int x, string because)
    {
        var c = At(bmp, x, 100);
        Assert.True(c.Red > 200 && c.Green > 200 && c.Blue > 200,
            $"{because}: expected white page at x={x}, got RGB({c.Red},{c.Green},{c.Blue}).");
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A black horizontal line at y=100 from x=10 to x=190, dashed through an ExtGState.
    /// Object 7 carries <c>10</c>, so either the array element or the phase can point at it.
    /// </summary>
    private static byte[] DashPdf(string dashArray, string phase) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
        " /Resources << /ExtGState << /GS1 5 0 R >> >> >>",
        StreamObj("/GS1 gs 0 0 0 RG 6 w 10 100 m 190 100 l S\n"),
        "<< /Type /ExtGState /D [" + dashArray + " " + phase + "] >>",
        "null",
        "10");

    /// <summary>
    /// A fully-black <c>/Luminosity</c> soft mask (object 8) installed by <c>/GS1</c>, then — when
    /// <paramref name="secondSMask"/> is given — a <c>/GS2</c> naming it, then a black rectangle
    /// across the middle of the page. Object 7 carries <c>/None</c> for the indirect case.
    /// </summary>
    private static byte[] SoftMaskPdf(string secondSMask)
    {
        string content = "/GS1 gs " +
                         (secondSMask != null ? "/GS2 gs " : "") +
                         "0 0 0 rg 10 90 180 20 re f\n";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS1 5 0 R /GS2 6 0 R >> >> >>",
            StreamObj(content),
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 8 0 R >> >>",
            "<< /Type /ExtGState /SMask " + (secondSMask ?? "/None") + " >>",
            "/None",
            // The mask group: a transparency group form painting pure black, so its luminosity
            // is 0 everywhere and the resulting alpha is 0 everywhere.
            StreamObj("0 g 0 0 200 200 re f\n",
                      "/Type /XObject /Subtype /Form /BBox [0 0 200 200]" +
                      " /Group << /S /Transparency /CS /DeviceGray >>"));
    }

    /// <summary>
    /// A stream object. <paramref name="extraKeys"/> goes into the dictionary ahead of
    /// <c>/Length</c>, which is always computed — a hand-written length is the kind of fixture
    /// error that makes a test measure the parser's recovery rather than the feature.
    /// </summary>
    private static string StreamObj(string data, string extraKeys = null) =>
        "<< " + (extraKeys != null ? extraKeys + " " : "") +
        "/Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
