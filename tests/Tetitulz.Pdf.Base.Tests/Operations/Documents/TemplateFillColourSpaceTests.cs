// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// A replacement is drawn in the placeholder's fill colour however it was set: through
/// <c>cs</c> and <c>sc</c>/<c>scn</c> as well as <c>g</c>, <c>rg</c> and <c>k</c>, where a colour
/// set in a named space was replayed as black.
/// </summary>
public class TemplateFillColourSpaceTests
{
    [Theory]
    [InlineData("/DeviceRGB cs 1 0 0 sc", "/DeviceRGB cs 1 0 0 sc")]
    [InlineData("/DeviceGray cs 0.5 scn", "/DeviceGray cs 0.5 scn")]
    [InlineData("0 0 1 rg", "0 0 1 rg")]
    public void TheReplacement_IsDrawnInThePlaceholdersFillColour(string colour, string replayed)
    {
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceText(new MemoryStream(Pdf(colour)), output,
            new Dictionary<string, string> { ["{{n}}"] = "Ada" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Ada") });
        Assert.Contains("{{n}}", result.ReplacedKeys);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        string inserted = content.Substring(content.IndexOf("ET", StringComparison.Ordinal) + 2);

        Assert.Contains(replayed, inserted, StringComparison.Ordinal);
        Assert.DoesNotContain("0 g", inserted, StringComparison.Ordinal);
    }

    private static byte[] Pdf(string colour)
    {
        string content = colour + " BT /F1 12 Tf 72 700 Td ({{n}}) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
