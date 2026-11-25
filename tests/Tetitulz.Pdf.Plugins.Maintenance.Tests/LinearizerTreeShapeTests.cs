// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// What the linearizer does with a document whose shape Annex F cannot number, whose page tree's two
/// directions disagree, or that holds an object it cannot parse.
/// </summary>
public sealed class LinearizerTreeShapeTests
{
    /// <summary>
    /// A catalog written directly in the trailer has no number for the linearized trailers to name; it
    /// fell back to "the object after the linearization dictionary", which is some other object.
    /// </summary>
    [Fact]
    public void ADirectCatalog_IsRefused()
    {
        byte[] pdf = Build(new[]
        {
            "<< /Type /Pages /Kids [2 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 1 0 R /MediaBox [0 0 100 100] >>",
        }, root: "<< /Type /Catalog /Pages 1 0 R >>");

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfLinearizer.Linearize(new MemoryStream(pdf), output));
        Assert.Contains("/Root is not an indirect reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>A page written directly in its parent's <c>/Kids</c> has no number for the hint tables to name.</summary>
    [Fact]
    public void APageWrittenInItsParentsKids_IsRefused()
    {
        byte[] pdf = Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        });

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfLinearizer.Linearize(new MemoryStream(pdf), output));
        Assert.Contains("page 2 is written directly in its parent's /Kids", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page is reached through object 2's <c>/Kids</c>, which stores a 100-point box, while its
    /// <c>/Parent</c> names object 4, a node outside the tree storing 500 points. Core draws the page
    /// at 100, and so does the linearized file; the value climbed to up <c>/Parent</c> was 500, and the
    /// 100 on the tree node was then removed.
    /// </summary>
    [Fact]
    public void AnInheritedAttribute_IsPushedDownFromTheNodeWhoseKidsReachThePage()
    {
        byte[] pdf = Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 4 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 /MediaBox [0 0 500 500] >>",
        });
        Assert.Equal(100, Width(pdf));   // what Core reads before

        using var output = new MemoryStream();
        PdfLinearizer.Linearize(new MemoryStream(pdf), output);

        Assert.Equal(100, Width(output.ToArray()));
    }

    /// <summary>
    /// An object the parser cannot read was dropped in silence; the file is still linearized, and the
    /// caller is told which object was left out. The control: a clean file reports nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnObjectThatDoesNotParse_IsReported(bool damaged)
    {
        byte[] pdf = Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Extra 4 0 R >>",
            damaged ? "]" : "<< /K 1 >>",   // a stray ] (CLAUDE.md: the test's unreadable object)
        });

        using var output = new MemoryStream();
        PdfLinearizer.Linearize(new MemoryStream(pdf), output);

        Assert.True(output.Length > 0);
        if (damaged)
        {
            Assert.NotNull(DocumentEditDiagnostics.LastError);
            Assert.Contains("could not be parsed", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
            Assert.Contains(": 4.", DocumentEditDiagnostics.LastError, StringComparison.Ordinal);
        }
        else Assert.Null(DocumentEditDiagnostics.LastError);
    }

    private static double Width(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfArray box = doc.Pages[0].MediaBox;
        return ((PdfNumber)box[2]).DoubleValue;
    }

    /// <summary>Objects numbered from 1 in order; the trailer's <c>/Root</c> is object 1 unless <paramref name="root"/> is written in its place.</summary>
    private static byte[] Build(string[] bodies, string root = null)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root ").Append(root ?? "1 0 R")
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
