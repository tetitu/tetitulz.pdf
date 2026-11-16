// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Redaction charges every form walk its decoded length against
/// <see cref="PdfLimits.MaxFormContentBytesPerPage"/>, and refuses a form it cannot read.
/// </summary>
/// <remarks>
/// <para>Each invocation of a form meeting a region is walked again and written as a clone of its
/// own, so the walk budget alone bounds nothing about size: a 3.5 KB file the redaction fuzzer found
/// drew one form into itself until the redaction wrote 36 MB, and a larger form under the same walk
/// count is gigabytes. The fixture is a page drawing one form that draws a leaf form many times; the
/// leaf's rectangles all fall in the region, so every walk removes something and writes a clone.</para>
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public class RedactionFormContentBudgetTests
{
    private const string LeafLine = "0 0 5 5 re f\n";

    private static byte[] Pdf(int draws, int leafLines, string leafDictionary = null, byte[] leafData = null)
    {
        string calls = string.Concat(Enumerable.Repeat("/L Do ", draws));
        byte[] leaf = leafData ?? Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(LeafLine, leafLines)));
        var bodies = new List<byte[]>
        {
            A("<< /Type /Catalog /Pages 2 0 R >>"),
            A("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /F0 5 0 R >> >> >>"),
            A("<< /Length 7 >>\nstream\n/F0 Do\nendstream"),
            A($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /XObject << /L 6 0 R >> >> /Length {calls.Length} >>\nstream\n{calls}\nendstream"),
            A($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] {leafDictionary}/Length {leaf.Length} >>\nstream\n")
                .Concat(leaf).Concat(A("\nendstream")).ToArray(),
        };

        var file = new List<byte>(A("%PDF-1.7\n"));
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(file.Count);
            file.AddRange(A($"{i + 1} 0 obj\n"));
            file.AddRange(bodies[i]);
            file.AddRange(A("\nendobj\n"));
        }
        var tail = new StringBuilder();
        int xref = file.Count;
        tail.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) tail.AppendFormat("{0:D10} 00000 n \n", off);
        tail.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        file.AddRange(A(tail.ToString()));
        return file.ToArray();
    }

    private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Redact(byte[] pdf)
    {
        var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 50, Height = 50 },
        });
        return output.ToArray();
    }

    /// <summary>The clones of the leaf the redaction wrote, named in the outer form's resources.</summary>
    private static int LeafClones(byte[] output)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output));
        var pageXObjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var outer = (PdfStream)doc.ResolveObject(pageXObjects[new PdfName("F0")]);
        var outerResources = (PdfDictionary)doc.ResolveObject(outer[PdfName.Resources]);
        var outerXObjects = (PdfDictionary)doc.ResolveObject(outerResources[PdfName.XObject]);
        return outerXObjects.Count(e => e.Key.Value.StartsWith("RdxForm", StringComparison.Ordinal));
    }

    [Fact]
    public void FormContentWalkedPastTheByteBudget_IsRefused_UnderTheWalkBudget()
    {
        try
        {
            PdfLimits.MaxFormContentBytesPerPage = 64 * 1024;
            // 40 walks of a 5.2 KB leaf: 208 KB of form content, 41 walks of the 100 000 the walk budget admits.
            var refusal = Assert.Throws<PdfRedactionException>(() => Redact(Pdf(draws: 40, leafLines: 400)));
            Assert.Contains("too much to redact safely", refusal.Message, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void FormContentWithinTheByteBudget_IsRedacted_OneCloneAnInvocation()
    {
        try
        {
            PdfLimits.MaxFormContentBytesPerPage = 64 * 1024;
            // 10 walks of the same leaf: 52 KB, inside the budget.
            Assert.Equal(10, LeafClones(Redact(Pdf(draws: 10, leafLines: 400))));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void AdversarialFormThatWillNotDecode_IsRefused_NotReadAsEmpty()
    {
        // A Flate form whose data is no zlib stream: read as empty, it would remove nothing and keep its bytes.
        byte[] notZlib = Enumerable.Range(0, 64).Select(i => (byte)(0xF0 ^ i)).ToArray();
        var refusal = Assert.Throws<PdfRedactionException>(() =>
            Redact(Pdf(draws: 1, leafLines: 0, leafDictionary: "/Filter /FlateDecode ", leafData: notZlib)));
        Assert.Contains("could not be read", refusal.Message, StringComparison.Ordinal);

        // The control: the same leaf, readable and drawn once, is redacted in its own name.
        byte[] output = Redact(Pdf(draws: 1, leafLines: 400));
        Assert.Contains("re f", LeafContent(Pdf(draws: 1, leafLines: 400)), StringComparison.Ordinal);
        Assert.DoesNotContain("re f", LeafContent(output), StringComparison.Ordinal);
    }

    /// <summary>The decoded content of the form the outer form names <c>/L</c>.</summary>
    private static string LeafContent(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        var pageXObjects = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var outer = (PdfStream)doc.ResolveObject(pageXObjects[new PdfName("F0")]);
        var outerXObjects = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(outer[PdfName.Resources]))[PdfName.XObject]);
        return Encoding.ASCII.GetString(((PdfStream)doc.ResolveObject(outerXObjects[new PdfName("L")])).GetDecodedData());
    }
}
