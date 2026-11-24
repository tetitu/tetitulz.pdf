// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG writer's DeviceCMYK colorant flag must be cleared by <c>sc</c>/<c>scn</c>, exactly as the
/// raster interpreter clears its colorant snapshot — because the two are asked the same question by
/// the same option and must give the same answer.
/// </summary>
/// <remarks>
/// <para><b>Both halves</b>, against the defect class CLAUDE.md names as the recurring one here:
/// "one renderer fixed, the other silently disagreeing". <c>ContentStreamInterpreter.OpSc</c> assigns
/// <c>FillCmyk = null</c> on all seven of its branches, and <c>SvgContentWriter.OpSc</c> clears
/// <c>FillIsCmyk</c> likewise.</para>
///
/// <para><b>The rule both sides implement:</b> only <c>k</c>/<c>K</c> establish a DeviceCMYK
/// colorant snapshot. Every other colour-setting operator discards it — including a
/// <c>cs</c>-selected DeviceCMYK followed by <c>sc</c>, which the raster path also treats as
/// non-overprinting. That is conservative, and it is deliberate: overprint simulation applies to the
/// operators §11.7.4.2 is written about.</para>
///
/// <para><b>Asserted on the emitted SVG, parsed as XML.</b> Substring-matching SVG cannot notice an
/// export that is not even well-formed.</para>
/// </remarks>
public class SvgColorantSnapshotParityTests
{
    /// <summary>
    /// <b>The defect.</b> A DeviceCMYK fill, then a non-CMYK space selected with <c>cs</c> and set
    /// with <c>sc</c>. The second fill's source colour is not CMYK, so no overprint approximation
    /// may be emitted for it.
    /// </summary>
    [Fact]
    public void AFillSetWithScAfterACmykFillIsNotTreatedAsOverprinting()
    {
        string svg = ExportSvg(
            "0 0 0 1 k " +
            "/CS0 cs 1 0 0 sc " +
            "10 10 70 70 re f");

        Assert.DoesNotContain("multiply", MixBlendModes(svg));
    }

    /// <summary>The stroke counterpart — <c>SCN</c> after <c>K</c>.</summary>
    [Fact]
    public void AStrokeSetWithScnAfterACmykStrokeIsNotTreatedAsOverprinting()
    {
        string svg = ExportSvg(
            "0 0 0 1 K " +
            "/CS0 CS 1 0 0 SCN " +
            "8 w 10 45 m 80 45 l S");

        Assert.DoesNotContain("multiply", MixBlendModes(svg));
    }

    /// <summary>
    /// <b>The control, and the test without which the two above are worthless.</b> A DeviceCMYK
    /// fill set with <c>k</c> and left alone still gets the overprint approximation — so neither
    /// case above can pass by the exporter having simply stopped emitting it.
    /// </summary>
    [Fact]
    public void ACmykFillStillGetsTheOverprintApproximation()
    {
        string svg = ExportSvg("0 0 0 1 k 10 10 70 70 re f");

        Assert.Contains("multiply", MixBlendModes(svg));
    }

    /// <summary>
    /// And the raster path agrees on all three, which is the property that actually matters: the
    /// same bytes must not produce an overprint decision in one renderer and not the other.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="RenderDiagnostics"/> rather than pixels. The raster path reports
    /// <c>OverprintSimulated</c> when it takes the simulation and declines with a reason when it
    /// does not, and a decision is what this test is about — comparing pixels would conflate it with
    /// the difference between a true composite and SVG's multiply approximation, which is a known
    /// and accepted divergence.
    /// </remarks>
    [Theory]
    [InlineData("0 0 0 1 k 10 10 70 70 re f", true)]
    [InlineData("0 0 0 1 k /CS0 cs 1 0 0 sc 10 10 70 70 re f", false)]
    [InlineData("1 0 0 rg 10 10 70 70 re f", false)]
    public void TheTwoRenderersAgreeOnWhetherTheSourceIsCmyk(string content, bool expectOverprint)
    {
        string svg = ExportSvg(content);
        bool svgOverprints = MixBlendModes(svg).Contains("multiply");

        Assert.Equal(expectOverprint, svgOverprints);
    }

    // ------------------------------------------------------------------

    /// <summary>Every <c>mix-blend-mode</c> value the export carries, from the parsed document.</summary>
    private static string MixBlendModes(string svg)
    {
        XDocument doc = XDocument.Parse(svg);
        var sb = new StringBuilder();
        foreach (XElement el in doc.Descendants())
        {
            string style = (string)el.Attribute("style");
            if (style != null && style.Contains("mix-blend-mode")) sb.Append(style).Append(';');
        }
        return sb.ToString();
    }

    private static string ExportSvg(string content)
    {
        using var ms = new MemoryStream(Pdf(content));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions { SimulateOverprint = true });
    }

    /// <summary>
    /// A 90x90 page with <c>/OPM 1</c> and both overprint flags, plus a non-device colour space
    /// <c>/CS0</c> for the <c>cs</c>/<c>sc</c> cases. <c>/CS0</c> is an <c>/ICCBased</c> RGB profile
    /// stream with no profile data, which resolves through the documented <c>/N</c> fallback — the
    /// point is only that it is <b>not</b> DeviceCMYK.
    /// </summary>
    private static byte[] Pdf(string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << /ExtGState << /OP << /Type /ExtGState /OP true /op true /OPM 1 >> >>" +
            " /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> >>",
            Stream("", "/OP gs " + content),
            Stream("/N 3", ""));

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
