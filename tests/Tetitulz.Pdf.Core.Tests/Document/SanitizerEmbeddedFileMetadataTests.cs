// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <c>RemoveEmbeddedFiles</c> emptied an embedded file's bytes and left its stream dictionary:
/// <c>/Params</c> with the file's size, checksum and modification date, <c>/Subtype</c> with its
/// media type, and <c>/Type /EmbeddedFile</c>, which kept the empty stream an embedded file to
/// PDF/X's X16. The whole description now goes with the bytes.
/// </summary>
public class SanitizerEmbeddedFileMetadataTests
{
    private const string Payload = "SECRET-ATTACHMENT-BODY";
    private const string ModDate = "D:20240102030405";

    [Fact]
    public void RemoveEmbeddedFiles_ClearsTheStreamsDescriptionAndItsBytes()
    {
        byte[] pdf = Build();
        string before = Encoding.Latin1.GetString(pdf);
        Assert.Contains(Payload, before);
        Assert.Contains(ModDate, before);
        Assert.Contains("/EmbeddedFile ", before);

        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(new MemoryStream(pdf), output, new SanitizeOptions { RemoveEmbeddedFiles = true });

        string after = Encoding.Latin1.GetString(output.ToArray());
        Assert.DoesNotContain(Payload, after);
        Assert.DoesNotContain(ModDate, after);
        Assert.DoesNotContain("/EmbeddedFile ", after);
        Assert.DoesNotContain("text#2Fplain", after);

        // Still a readable document: the emptied stream did not take the file with it.
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(1, doc.Pages.Count);
    }

    /// <summary>The control: without the option the description and the bytes are kept.</summary>
    [Fact]
    public void WithoutTheOption_TheEmbeddedFileIsKept()
    {
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(new MemoryStream(Build()), output, new SanitizeOptions { RemoveEmbeddedFiles = false });

        string after = Encoding.Latin1.GetString(output.ToArray());
        Assert.Contains(ModDate, after);
        Assert.Contains("/EmbeddedFile", after);
    }

    private static byte[] Build()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            "<< /Type /Filespec /F (a.txt) /EF << /F 6 0 R >> >>",
            $"<< /Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size {Payload.Length} " +
            $"/ModDate ({ModDate}) >> /Length {Payload.Length} >>\nstream\n{Payload}\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
