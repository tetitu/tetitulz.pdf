// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The template engine numbers the objects it adds after the larger of the trailer's
/// <c>/Size</c> and the cross-reference table's highest number. A <c>/Size</c> whose next number
/// the int range cannot hold numbers nothing: at <see cref="int.MaxValue"/> the next number,
/// <c>max + 1</c>, overflowed, and the replacement threw an <see cref="System.ArgumentException"/>
/// for object −2147483648; past the range, <c>(int)LongValue</c> kept the low 32 bits.
/// </summary>
/// <remarks>
/// The output is a full rewrite, which numbers what it writes afresh — so the figure is seen only
/// when it breaks the replacement, which is what the first case did before the guard.
/// </remarks>
public sealed class TemplateObjectNumberingTests
{
    [Theory]
    [InlineData("2147483647")]                // int.MaxValue: max + 1 overflowed
    [InlineData("4294967302")]                // 2^32 + 6: read as 6
    [InlineData("9223372036854775807")]
    public void ATrailerSizeWhoseNextNumberTheIntRangeCannotHold_IsIgnored_AndTheReplacementSucceeds(string size)
    {
        byte[] output = Replace(Template(size));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.InRange(doc.CrossReferenceTable.GetMaxObjectNumber(), 6, 1000);   // the font's objects, after the five
    }

    /// <summary>
    /// Hebrew, which Helvetica cannot encode, so the engine embeds the supplied font — new objects,
    /// numbered by the figure under test.
    /// </summary>
    private const string Value = "שלום";

    private static byte[] Replace(byte[] template)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        byte[] font = Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build(Value);
        var result = PdfTemplateEngine.Replace(input, output, new Dictionary<string, string> { ["name"] = Value },
            new TemplateReplaceOptions { VerifyErasure = false, AllowSystemFontSubstitution = false, FontProvider = _ => font });
        Assert.Contains("name", result.ReplacedKeys);
        return output.ToArray();
    }

    /// <summary>Five objects, the page showing <c>{{name}}</c>; the trailer's <c>/Size</c> as given.</summary>
    private static byte[] Template(string size)
    {
        const string content = "BT /F1 12 Tf 20 100 Td ({{name}}) Tj ET";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
