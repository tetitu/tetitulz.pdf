// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// A placeholder drawn under <c>200 Tz</c> has a slot measured with that scale, and the replacement
/// is drawn under a text matrix carrying it too, so shrink-to-fit compares the replacement's width
/// scaled the same way: compared unscaled, a value up to twice the slot was drawn unshrunk over the
/// text after it. The control, a placeholder at <c>100 Tz</c>, is unchanged.
/// </summary>
public class TemplateHorizontalScalingTests
{
    [Theory]
    [InlineData("200 Tz")]
    [InlineData("100 Tz")]
    public void AWideReplacementUnderHorizontalScaling_StaysInsideItsSlot(string scaling)
    {
        byte[] pdf = Pdf(scaling);
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceText(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["{{n}}"] = "WWWWWWWWWW" },
            new TemplateReplaceOptions { KeepSlot = true, FontProvider = _ => MinimalTrueType.BuildWithDistinctWidths("W Smith") });
        Assert.Contains("{{n}}", result.ReplacedKeys);

        var words = PdfStructuredTextExtractor.Extract(new MemoryStream(output.ToArray()))[0].Lines
            .SelectMany(l => l.Words).ToList();
        var replacement = words.Single(w => w.Text.StartsWith("WWW", System.StringComparison.Ordinal));
        var smith = words.Single(w => w.Text == "Smith");

        Assert.True(replacement.X + replacement.Width <= smith.X + 0.5f,
            $"the replacement ends at {replacement.X + replacement.Width} and Smith starts at {smith.X}");
    }

    private static byte[] Pdf(string scaling)
    {
        string content = $"BT /F1 12 Tf {scaling} 72 700 Td ({{{{n}}}}) Tj ( Smith) Tj ET\n";
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
