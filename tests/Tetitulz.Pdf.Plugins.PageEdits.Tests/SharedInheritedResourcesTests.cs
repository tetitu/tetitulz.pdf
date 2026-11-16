// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// An inherited attribute the source holds as an indirect object — one <c>/Resources</c> on the
/// <c>/Pages</c> node — is carried as that object by merge and split, every page naming it: it was
/// copied onto every page, pages × entries in the output, 40 MB from 455 KB at eight hundred of each.
/// </summary>
public class SharedInheritedResourcesTests
{
    private const int Pages = 40, Fonts = 40;

    /// <summary><see cref="Pages"/> pages inheriting one indirect resource dictionary of <see cref="Fonts"/> fonts, or a direct one when <paramref name="direct"/>.</summary>
    private static byte[] Document(bool direct = false)
    {
        var bodies = new List<string>();
        var kids = new StringBuilder();
        var fonts = new StringBuilder();
        int firstFont = 4, firstPage = firstFont + Fonts, content = firstPage + Pages;
        for (int p = 0; p < Pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        for (int i = 0; i < Fonts; i++) fonts.Append("/F").Append(i).Append(' ').Append(firstFont + i).Append(" 0 R ");
        string resources = $"<< /Font << {fonts}>> >>";
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {Pages} /MediaBox [0 0 612 792] /Resources {(direct ? resources : "3 0 R")} >>");
        bodies.Add(direct ? "<< >>" : resources);
        for (int i = 0; i < Fonts; i++) bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (int p = 0; p < Pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R >>");
        bodies.Add("<< /Length 35 >>\nstream\nBT /F7 12 Tf 72 700 Td (Hi) Tj ET\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Every page's raw <c>/Resources</c> entry, and each page's resolved fonts.</summary>
    private static (List<PdfObject> Raw, List<int> FontCounts) Read(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var raw = new List<PdfObject>();
        var counts = new List<int>();
        foreach (var page in doc.Pages)
        {
            raw.Add(page.Dictionary[PdfName.Resources]);
            counts.Add((doc.ResolveObject(page.Resources?[PdfName.Font]) as PdfDictionary)?.Count ?? 0);
        }
        return (raw, counts);
    }

    [Fact]
    public void AdversarialSharedResources_AreWrittenOnce_ByTheSplitter()
    {
        byte[] input = Document();
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(new MemoryStream(input), output, Enumerable.Range(1, Pages).ToList());

        var (raw, counts) = Read(output.ToArray());
        Assert.All(counts, n => Assert.Equal(Fonts, n));
        var references = raw.OfType<PdfReference>().Select(r => r.ReferenceObjectNumber).Distinct().ToList();
        Assert.Single(references);                       // every page names the one dictionary
        Assert.Equal(Pages, raw.Count(r => r is PdfReference));
        Assert.True(output.Length < 2 * input.Length, $"the output is {output.Length} bytes from {input.Length}");
    }

    [Fact]
    public void AdversarialSharedResources_AreWrittenOnce_ByTheMerger()
    {
        byte[] input = Document();
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(input), new MemoryStream(input));

        var (raw, counts) = Read(output.ToArray());
        Assert.Equal(2 * Pages, counts.Count);
        Assert.All(counts, n => Assert.Equal(Fonts, n));
        Assert.Equal(2, raw.OfType<PdfReference>().Select(r => r.ReferenceObjectNumber).Distinct().Count());   // one a source
        Assert.True(output.Length < 3 * input.Length, $"the output is {output.Length} bytes from {input.Length}");
    }

    /// <summary>
    /// <b>Adversarial: a form whose <c>/DR</c> holds thirty fonts, merged with itself.</b> Every name
    /// collides, each is reported once — kept distinct by a set, where a search of the list made n
    /// names n² / 2 comparisons — and the warning names the first twenty and counts the rest.
    /// </summary>
    [Fact]
    public void AdversarialManyDefaultResourceCollisions_AreReportedOnceEach_UnderACap()
    {
        var fonts = new StringBuilder();
        for (int i = 0; i < 30; i++) fonts.Append("/F").Append(i).Append(" 4 0 R ");
        string[] bodies =
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /DR << /Font << {fonts}>> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        PdfMerger.Merge(new MemoryStream(), new MemoryStream(pdf), new MemoryStream(pdf));

        string warning = PdfMerger.LastFormWarning;
        Assert.Contains("/Font /F19 and 10 more", warning, System.StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(warning, "/Font /F0,").Count);
        Assert.DoesNotContain("/Font /F20", warning, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A direct <c>/Resources</c> on the <c>/Pages</c> node has no object to name, and was copied onto
    /// each page — pages × entries from pages + entries. It is now written once and every page that
    /// inherits it names that one copy.
    /// </summary>
    [Fact]
    public void ADirectInheritedResourceDictionary_IsWrittenOnceAndNamedByEachPage()
    {
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(new MemoryStream(Document(direct: true)), output, new[] { 1, 2 });

        var (raw, counts) = Read(output.ToArray());
        Assert.All(counts, n => Assert.Equal(Fonts, n));
        Assert.Equal(2, raw.Count);
        Assert.Single(raw.Select(r => Assert.IsType<PdfReference>(r).ReferenceObjectNumber).Distinct());
    }

    [Fact]
    public void ADirectInheritedResourceDictionary_OnePageInherits_IsCopiedOntoThatPage()
    {
        // The control: an extract of one page has nothing to share, and is as it was.
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(new MemoryStream(Document(direct: true)), output, new[] { 3 });

        var (raw, counts) = Read(output.ToArray());
        Assert.IsType<PdfDictionary>(Assert.Single(raw));
        Assert.Equal(Fonts, Assert.Single(counts));
    }

    [Fact]
    public void AdversarialDirectSharedResources_AreWrittenAndWalkedOnce_ByTheSplitter()
    {
        byte[] input = Document(direct: true);
        long walked = PdfSplitter.CollectionSteps, copied = PdfObjectHelper.ClonedValues;
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(new MemoryStream(input), output, Enumerable.Range(1, Pages).ToList());
        walked = PdfSplitter.CollectionSteps - walked;
        copied = PdfObjectHelper.ClonedValues - copied;

        var (raw, counts) = Read(output.ToArray());
        Assert.All(counts, n => Assert.Equal(Fonts, n));
        Assert.Single(raw.Select(r => Assert.IsType<PdfReference>(r).ReferenceObjectNumber).Distinct());
        Assert.True(output.Length < 2 * input.Length, $"the output is {output.Length} bytes from {input.Length}");
        // pages + fonts, not pages × fonts (1 600 for each of the two below).
        Assert.InRange(walked, Pages, 20L * (Pages + Fonts));
        Assert.InRange(copied, Pages, 20L * (Pages + Fonts));
    }

    [Fact]
    public void AdversarialDirectSharedResources_AreWrittenOnce_ByTheMerger()
    {
        byte[] input = Document(direct: true);
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(input), new MemoryStream(input));

        var (raw, counts) = Read(output.ToArray());
        Assert.Equal(2 * Pages, counts.Count);
        Assert.All(counts, n => Assert.Equal(Fonts, n));
        Assert.Equal(2, raw.Select(r => Assert.IsType<PdfReference>(r).ReferenceObjectNumber).Distinct().Count());   // one a source
        Assert.True(output.Length < 3 * input.Length, $"the output is {output.Length} bytes from {input.Length}");
    }

    [Theory]
    [InlineData("[0 0 612 792]", false)]
    [InlineData("[0 0 612 792 0 0 0 0 0 0]", true)]
    public void AnInheritedBox_IsSharedOnlyWhenItHoldsMoreThanARectangle(string box, bool shared)
    {
        // Four numbers copied onto a page cost a constant; a longer array is the same amplification as
        // a dictionary, and is written once.
        string pdf = Encoding.ASCII.GetString(Document()).Replace("/MediaBox [0 0 612 792]", "/MediaBox " + box);
        byte[] source = Encoding.ASCII.GetBytes(FixOffsets(pdf));
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(new MemoryStream(source), output, new[] { 1, 2 });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var raw = doc.Pages.Select(p => p.Dictionary[PdfName.MediaBox]).ToList();
        Assert.All(doc.Pages, p => Assert.Equal(612, (doc.ResolveObject(p.MediaBox[2]) as PdfNumber)?.IntValue));
        if (shared) Assert.Single(raw.Select(r => Assert.IsType<PdfReference>(r).ReferenceObjectNumber).Distinct());
        else Assert.All(raw, r => Assert.IsType<PdfArray>(r));
    }

    // Rewrites the cross-reference offsets of a document whose text was edited.
    private static string FixOffsets(string pdf)
    {
        var offsets = new List<int>();
        int at = 0;
        for (int n = 1; ; n++)
        {
            int found = pdf.IndexOf("\n" + n + " 0 obj\n", at, System.StringComparison.Ordinal);
            if (found < 0) break;
            offsets.Add(found + 1);
            at = found + 1;
        }
        int xref = pdf.IndexOf("xref\n", System.StringComparison.Ordinal);
        var sb = new StringBuilder(pdf.Substring(0, xref));
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return sb.ToString();
    }
}
