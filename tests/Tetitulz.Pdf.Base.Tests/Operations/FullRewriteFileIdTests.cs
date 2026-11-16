// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A full rewrite (the template engine's, the redaction engine's, the page-element writer's)
/// writes a trailer <c>/ID</c> (§14.4; ISO 32000-2 Table 15 requires one): the document's first
/// half kept, since it names the file whatever its revision, and a new second half, since the
/// content changed; both new when the document carries none.
/// </summary>
public class FullRewriteFileIdTests
{
    private static readonly byte[] First = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Second = Enumerable.Range(17, 16).Select(i => (byte)i).ToArray();

    [Fact]
    public void ARewrite_KeepsTheFirstHalfAndMintsTheSecond()
    {
        byte[] output = Replace(TemplatePdf(withId: true));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var id = doc.ResolveObject(doc.Trailer[PdfName.ID]) as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id!.Count);
        Assert.Equal(First, (id[0] as PdfString)!.RawBytes);
        byte[] second = (id[1] as PdfString)!.RawBytes;
        Assert.Equal(16, second.Length);
        Assert.NotEqual(Second, second);
        Assert.NotEqual(First, second);
    }

    /// <summary>With no <c>/ID</c> to keep, both halves are minted — equal, as §14.4 has them for a new file.</summary>
    [Fact]
    public void ARewriteOfADocumentWithNoId_MintsBothHalves()
    {
        byte[] output = Replace(TemplatePdf(withId: false));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var id = doc.ResolveObject(doc.Trailer[PdfName.ID]) as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id!.Count);
        byte[] first = (id[0] as PdfString)!.RawBytes, second = (id[1] as PdfString)!.RawBytes;
        Assert.Equal(16, first.Length);
        Assert.Equal(first, second);
        Assert.Contains(first, b => b != 0);
    }

    // ------------------------------------------------------------------

    private static byte[] Replace(byte[] pdf)
    {
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.Replace(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["name"] = "Jo" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Jo") });
        Assert.Contains("name", result.ReplacedKeys);
        return output.ToArray();
    }

    private static string Hex(byte[] bytes) => "<" + string.Concat(bytes.Select(b => b.ToString("x2"))) + ">";

    private static byte[] TemplatePdf(bool withId)
    {
        const string content = "BT /F1 12 Tf 72 700 Td ({{name}} Smith) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R");
        if (withId) sb.Append(" /ID [").Append(Hex(First)).Append(' ').Append(Hex(Second)).Append(']');
        sb.Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
