// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Tests for R9: per-pixel ink deposition through the rendering pipeline.
/// Validates that InkAccumulator receives tint deposits when Separation /
/// DeviceN color spaces are used in content streams.
/// </summary>
public class InkAccumulatorWiringTests
{
    // ------------------------------------------------------------------
    //  Helpers — build minimal in-memory PDFs with spot-colour content
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a 100×100 pt PDF with a single rectangle filled in the
    /// given Separation colour (e.g. "SPOT 185 C") at the given tint.
    /// </summary>
    private static byte[] BuildSeparationPdf(string colorantName, float tint = 1.0f)
    {
        // Minimal function: Type 2 exponential (identity for Separation → CMYK black)
        // The function dict is object 5.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Tint function: Type 2 exponential, maps [0,1] → [0,1] identity (N=1 C0=0 C1=1)
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /FunctionType 2 /Domain [0 1] /Range [0 1] /N 1 /C0 [0] /C1 [1] >>\nendobj\n");

        // Content stream: set Separation color space, set tint, draw rect
        string csName = colorantName.Replace(" ", "_");
        // Note: we use inline color space definition via cs operator
        // Resources: /ColorSpace << /Cs1 [/Separation /SPOT_185_C /DeviceGray 5 0 R] >>
        string safeColorant = colorantName.Replace("(", "\\(").Replace(")", "\\)");
        float tintClamped = Math.Max(0f, Math.Min(1f, tint));
        string tintStr = tintClamped.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        string cs = $"q\n/Cs1 cs\n{tintStr} scn\n0 0 100 100 re\nf\nQ\n";
        int csLen = Encoding.ASCII.GetByteCount(cs);

        int o3 = sb.Length;
        sb.Append($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]\n");
        sb.Append($"   /Resources << /ColorSpace << /Cs1 [/Separation /{csName} /DeviceGray 5 0 R] >> >>\n");
        sb.Append($"   /Contents 4 0 R >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", csLen, cs);

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildNoSpotColorPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        string cs = "q\n1 0 0 rg\n0 0 100 100 re\nf\nQ\n";
        int csLen = Encoding.ASCII.GetByteCount(cs);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", csLen, cs);
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

    // ------------------------------------------------------------------
    //  Tests
    // ------------------------------------------------------------------

    [Fact]
    public void ExtractInkCoverage_NoSpotColors_ReturnsEmptyChannels()
    {
        byte[] pdf = BuildNoSpotColorPdf();
        using var ms  = new MemoryStream(pdf);
        var acc = PdfOperations.ExtractInkCoverage(ms, 0, dpi: 72f);
        Assert.NotNull(acc);
        Assert.Empty(acc.Channels);
    }

    [Fact]
    public void ExtractInkCoverage_SeparationColor_ChannelRegistered()
    {
        byte[] pdf = BuildSeparationPdf("SPOT_185_C", tint: 1.0f);
        using var ms  = new MemoryStream(pdf);
        var acc = PdfOperations.ExtractInkCoverage(ms, 0, dpi: 72f);
        Assert.NotNull(acc);
        Assert.Contains("SPOT_185_C", acc.Channels.Keys);
    }

    [Fact]
    public void ExtractInkCoverage_SeparationColor_PixelsDeposited()
    {
        byte[] pdf = BuildSeparationPdf("Spot1", tint: 1.0f);
        using var ms  = new MemoryStream(pdf);
        // Render at 72 dpi: 100pt page → 100px × 100px
        var acc = PdfOperations.ExtractInkCoverage(ms, 0, dpi: 72f);
        Assert.Contains("Spot1", acc.Channels.Keys);
        var coverage = acc.Channels["Spot1"];
        // With a full-page rectangle and tint=1.0, at least some pixels must be > 0.
        bool anyDeposited = false;
        foreach (byte b in coverage)
            if (b > 0) { anyDeposited = true; break; }
        Assert.True(anyDeposited, "Expected non-zero ink deposit in Spot1 channel");
    }

    [Fact]
    public void ExtractInkCoverage_HalfTint_CoverageValueReflectsTint()
    {
        byte[] pdf = BuildSeparationPdf("HalfInk", tint: 0.5f);
        using var ms  = new MemoryStream(pdf);
        var acc = PdfOperations.ExtractInkCoverage(ms, 0, dpi: 72f);
        Assert.Contains("HalfInk", acc.Channels.Keys);
        var coverage = acc.Channels["HalfInk"];
        // With tint=0.5, deposited values should be ≈127. Check at least one pixel
        // is non-zero and less than 255.
        bool hasMidtone = false;
        foreach (byte b in coverage)
            if (b > 0 && b < 255) { hasMidtone = true; break; }
        Assert.True(hasMidtone, "Expected midtone deposit (≈127) for tint=0.5");
    }

    [Fact]
    public void InkAccumulator_Deposit_MaxCoverageModel()
    {
        var acc = new InkAccumulator(10, 10);
        acc.Deposit("Ink1", 5, 5, 100);
        acc.Deposit("Ink1", 5, 5, 200); // higher → should win
        acc.Deposit("Ink1", 5, 5, 50);  // lower  → should not overwrite
        Assert.Equal(200, acc.Channels["Ink1"][5 * 10 + 5]);
    }

    [Fact]
    public void InkAccumulator_Deposit_OutOfBounds_NoException()
    {
        var acc = new InkAccumulator(5, 5);
        acc.Deposit("Ink", -1, 0, 255);   // x out of bounds
        acc.Deposit("Ink", 0, -1, 255);   // y out of bounds
        acc.Deposit("Ink", 10, 0, 255);   // x too large
        acc.Deposit("Ink", 0, 10, 255);   // y too large
        // No channel should be created since every deposit was out of bounds.
        Assert.False(acc.Channels.ContainsKey("Ink"));
    }

    [Fact]
    public void InkAccumulator_Reset_ClearsAllChannels()
    {
        var acc = new InkAccumulator(5, 5);
        acc.Deposit("Ink1", 0, 0, 255);
        Assert.NotEmpty(acc.Channels);
        acc.Reset();
        Assert.Empty(acc.Channels);
    }

    [Fact]
    public void ExtractInkCoverage_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfOperations.ExtractInkCoverage(null, 0));
    }

    [Fact]
    public void ExtractInkCoverage_InvalidPageIndex_Throws()
    {
        byte[] pdf = BuildNoSpotColorPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.ExtractInkCoverage(ms, 99));
    }
}
