// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The shape of the answer <see cref="PdfAnnotationExtractor"/> gives: what it reports per
/// annotation, and what it does when a page carries more than it will look at.
/// </summary>
/// <remarks>
/// <b>An inventory that silently truncates is worse than one that refuses.</b> This is the API a
/// caller uses to decide what to remove, so a list that quietly omits the annotations it did not
/// examine is a list of everything the caller will act on and nothing it will not.
/// </remarks>
public class AnnotationInventoryTests
{
    // ==================================================================
    //  The per-page ceiling
    // ==================================================================

    /// <summary>
    /// <b>A page carrying more annotations than the ceiling is reported as incomplete.</b>
    /// </summary>
    /// <remarks>
    /// <para>The ceiling is a <c>const</c> rather than a parameter, so the only fixture that reaches
    /// it is one at production scale — which is why nothing had exercised it. The annotations are
    /// written <b>directly</b> into <c>/Annots</c> rather than as indirect objects, so the file is
    /// one large array instead of fifty thousand cross-reference entries; the walk resolves either
    /// spelling, and this one parses in a fraction of the time.</para>
    ///
    /// <para>Both halves are asserted. The count alone would be satisfied by a walk that stopped
    /// early and said nothing, and the message alone by one that reported truncation without
    /// truncating.</para>
    /// </remarks>
    [Fact]
    public void APageWithMoreAnnotationsThanTheCeilingIsReportedAsIncomplete()
    {
        int over = PdfAnnotationExtractor.MaxAnnotationsPerPage + 10;

        var listed = PdfAnnotationExtractor.ListAnnotations(
            new MemoryStream(PageOfManyAnnotations(over)));

        Assert.Equal(PdfAnnotationExtractor.MaxAnnotationsPerPage, listed.Count);
        Assert.NotNull(PdfAnnotationExtractor.LastError);
        Assert.Contains("incomplete", PdfAnnotationExtractor.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance control: a page just under the ceiling is listed in full and reports nothing.
    /// Without it, the refusal above is also satisfied by a walk that truncates everything.
    /// </summary>
    [Fact]
    public void APageJustUnderTheCeilingIsListedInFullAndReportsNothing()
    {
        int under = PdfAnnotationExtractor.MaxAnnotationsPerPage - 1;

        var listed = PdfAnnotationExtractor.ListAnnotations(
            new MemoryStream(PageOfManyAnnotations(under)));

        Assert.Equal(under, listed.Count);
        Assert.Null(PdfAnnotationExtractor.LastError);
    }

    // ==================================================================
    //  Per-annotation reporting
    // ==================================================================

    /// <summary>
    /// <b>A <c>/Rect</c> with fewer than four numbers is left alone, not read past.</b> §12.5.2
    /// defines it as two corners; a shorter array is malformed, and reading absent elements as zero
    /// yields a rectangle that looks like a real one.
    /// </summary>
    [Fact]
    public void AMalformedRectangleWithTooFewNumbersIsNotPartiallyRead()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Square /Rect [10 20] >>"));

        var only = Assert.Single(PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)));

        Assert.Equal("Square", only.Subtype);
        Assert.Equal(0d, only.X);
        Assert.Equal(0d, only.Y);
        Assert.Equal(0d, only.Width);
        Assert.Equal(0d, only.Height);
    }

    /// <summary>
    /// The control for the one above: a well-formed <c>/Rect</c> <i>is</i> read, so the zeros there
    /// are a refusal rather than a reader that never populates the rectangle at all.
    /// </summary>
    [Fact]
    public void AWellFormedRectangleIsRead()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Square /Rect [10 20 40 60] >>"));

        var only = Assert.Single(PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)));

        Assert.Equal(10d, only.X);
        Assert.Equal(20d, only.Y);
        Assert.Equal(30d, only.Width);
        Assert.Equal(40d, only.Height);
    }

    /// <summary>
    /// <b><c>HasAppearance</c> needs an <c>/N</c>, not merely an <c>/AP</c>.</b> An empty appearance
    /// dictionary supplies no appearance, and a caller reading this property is deciding whether a
    /// viewer must synthesise one.
    /// </summary>
    [Theory]
    [InlineData("/AP << /N 5 0 R >>", true)]
    [InlineData("/AP << >>", false)]
    [InlineData("", false)]
    public void HasAppearanceRequiresAnNEntryAndNotJustAnApDictionary(
        string appearanceEntry, bool expected)
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] "
                   + appearanceEntry + " >>"),
            StreamObj(5, " /Type /XObject /Subtype /Form /BBox [0 0 10 10]", "q Q\n"));

        var only = Assert.Single(PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)));

        Assert.Equal(expected, only.HasAppearance);
    }

    // ==================================================================
    //  Fixture assembly
    // ==================================================================

    /// <summary>
    /// One page whose <c>/Annots</c> array holds <paramref name="count"/> annotations written
    /// directly, each distinguishable by its <c>/NM</c>.
    /// </summary>
    private static byte[] PageOfManyAnnotations(int count)
    {
        var annots = new StringBuilder("[");
        for (int i = 0; i < count; i++)
            annots.Append("<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] >> ");
        annots.Append(']');

        return OnePage("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots "
                       + annots + " >>");
    }

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {content.Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    private static byte[] OnePage(string pageDictBody, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, pageDictBody),
        };
        bodies.AddRange(extraObjects);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string b in bodies) { offsets.Add(sb.Length); sb.Append(b); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
