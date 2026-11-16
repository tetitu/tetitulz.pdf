// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="SimpleFontWidthProbe"/> for a Type 3 font: the glyph named by <c>/Differences</c>, its
/// width the first operand of its procedure's leading <c>d0</c> or <c>d1</c> (§9.6.5), read from the
/// procedure's first <see cref="SimpleFontWidthProbe.Type3WidthBytesRead"/> bytes.
/// </summary>
public class SimpleFontWidthProbeType3Tests
{
    private static SimpleFontWidthProbe Probe(string glyphProc, string differences = "[65 /a]")
    {
        string font = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                      "/CharProcs << /a 5 0 R >> /Encoding << /Type /Encoding /Differences " + differences + " >> " +
                      "/FirstChar 65 /LastChar 66 /Widths [500 500] >>";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 4 0 R >> >> >>",
            font,
            "<< /Length " + glyphProc.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + glyphProc + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        var doc = PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())));
        return new SimpleFontWidthProbe((PdfDictionary)doc.ResolveObject(new PdfReference(4, 0)), doc);
    }

    [Theory]
    [InlineData("400 0 d0 0 0 10 10 re f", 400)]
    [InlineData("600 0 0 0 10 10 d1 0 0 10 10 re f", 600)]
    [InlineData("612.5 0 d0", 612.5)]
    public void TryGetEmbeddedWidth_IsTheGlyphProceduresD0OrD1Width(string glyphProc, double expected)
    {
        var probe = Probe(glyphProc);
        Assert.True(probe.IsType3);
        Assert.False(probe.IsType1);
        Assert.True(probe.TryGetEmbeddedWidth(65, out double width));
        Assert.Equal(expected, width);
        Assert.Equal(500, probe.GetWidth(65));                // /Widths, in the same glyph space
    }

    [Theory]
    [InlineData("0 0 10 10 re f 400 0 d0")]                   // not the first operator
    [InlineData("d0")]                                         // no operand
    [InlineData("/x 0 d0")]                                    // not a number
    public void TryGetEmbeddedWidth_WithoutALeadingWidthOperator_IsDeclined(string glyphProc)
        => Assert.False(Probe(glyphProc).TryGetEmbeddedWidth(65, out _));

    [Fact]
    public void TryGetEmbeddedWidth_ACodeDifferencesDoesNotName_IsDeclined()
        => Assert.False(Probe("400 0 d0").TryGetEmbeddedWidth(66, out _));

    [Fact]
    public void TryGetEmbeddedWidth_ANameCharProcsLacks_IsDeclined()
        => Assert.False(Probe("400 0 d0", "[65 /b]").TryGetEmbeddedWidth(65, out _));

    [Fact]
    public void TryGetEmbeddedWidth_ReadsOnlyTheProceduresStart()
    {
        // The operator past the bytes read is not found: the bound is what the probe reads.
        string padded = new string(' ', SimpleFontWidthProbe.Type3WidthBytesRead) + "400 0 d0";
        Assert.False(Probe(padded).TryGetEmbeddedWidth(65, out _));
        string within = new string(' ', SimpleFontWidthProbe.Type3WidthBytesRead - 20) + "400 0 d0";
        Assert.True(Probe(within).TryGetEmbeddedWidth(65, out double width));
        Assert.Equal(400, width);
    }
}
