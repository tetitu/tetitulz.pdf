// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The PDF/UA-2 checker reads the pages' annotations once per validation, an <c>/Annots</c> array
/// several pages share once, and lists an annotation several arrays name once: about fourteen of
/// its checks walk the annotations, and pages naming one array made each walk pages × entries —
/// 800 pages over one array of 800 links took 6.9 s against 0.5 s for 200.
/// </summary>
public class SharedAnnotsArrayUa2Tests
{
    private const int Pages = 20, Annotations = 10;
    private const string SoundMovieCode = "UA2-8.9.2.4.11";

    [Fact]
    public void AdversarialPagesSharingOneAnnotsArray_ReadTheArrayOnce_AndReportEachAnnotationOnce()
    {
        PdfUa2StructureResult result = PdfUa2StructureChecker.Validate(new MemoryStream(Document(sharedArray: true)));

        Assert.Equal(Annotations, PdfUa2StructureChecker.LastAnnotationEntriesRead);
        AssertEachSoundAnnotationReportedOnce(result);
    }

    /// <summary>
    /// The control: pages with arrays of their own are each read, and the annotations they all
    /// name are still reported once each — the dedupe is by annotation, the skip by array.
    /// </summary>
    [Fact]
    public void PagesWithArraysOfTheirOwnNamingTheSameAnnotations_AreEachRead_AndEachAnnotationReportedOnce()
    {
        PdfUa2StructureResult result = PdfUa2StructureChecker.Validate(new MemoryStream(Document(sharedArray: false)));

        Assert.Equal(Pages * Annotations, PdfUa2StructureChecker.LastAnnotationEntriesRead);
        AssertEachSoundAnnotationReportedOnce(result);
    }

    /// <summary>
    /// The validation opens one page-scan scope for its checks: the font and appearance checks each
    /// ask <c>ScanPage</c> for every page, and outside a scope each scan read the shared array again.
    /// </summary>
    [Fact]
    public void TheChecksThatScanEveryPage_ShareOneScanOfIt()
    {
        PdfUa2StructureChecker.Validate(new MemoryStream(Document(sharedArray: true)));

        Assert.True(PdfUa2StructureChecker.LastScanHits >= Pages,
            $"{PdfUa2StructureChecker.LastScanHits} scans reused");
    }

    /// <summary>
    /// Inside the scope the page scans read annotations under the validation's ceiling, and a
    /// validation that reached it says so; the control is the same document under the default.
    /// </summary>
    [Fact]
    public void AdversarialAnnotationsPastTheScanCeiling_AreSaid_AndUnderTheDefaultNothingIsSaid()
    {
        byte[] pdf = Document(sharedArray: true);
        var ceiling = typeof(Tetitulz.Pdf.Plugins.Conformance.PdfUaChecker).Assembly
            .GetType("Tetitulz.Pdf.Plugins.Conformance.ScanReuse", throwOnError: true)
            .GetField("TestMaxAnnotationVisits", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            ceiling.SetValue(null, (long)(5 * Annotations));
            PdfUa2StructureResult cut = PdfUa2StructureChecker.Validate(new MemoryStream(pdf));
            Assert.Contains(cut.Violations, v => v.Code == "UA2-99"
                && v.Message.Contains("the font and appearance checks", StringComparison.Ordinal));
        }
        finally
        {
            ceiling.SetValue(null, 0L);
        }

        PdfUa2StructureResult whole = PdfUa2StructureChecker.Validate(new MemoryStream(pdf));
        Assert.DoesNotContain(whole.Violations, v => v.Code == "UA2-99");
        Assert.Contains(whole.Violations, v => v.Code == SoundMovieCode);
    }

    /// <summary>
    /// An annotation stored as a direct dictionary has no object number, so no <c>/OBJR</c> could
    /// name it: the checks that cross-reference the tree pass it over rather than accuse it. The
    /// control is the same markup annotation stored indirectly, which §8.9.2.3 then accuses.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void AMarkupAnnotationOutsideAnyAnnotElement_IsAccusedOnlyWhenAnOBJRCouldNameIt(bool indirect, int accused)
    {
        const string text = "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (note) >>";
        string annots = indirect ? "[5 0 R]" : $"[{text}]";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> /Lang (en) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            $"<< /Type /Page /Parent 2 0 R /Annots {annots} >>",
            "<< /Type /StructTreeRoot /K [] >>",
            text,
        };
        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        PdfUa2StructureResult result = PdfUa2StructureChecker.Validate(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));

        Assert.Equal(accused, result.Violations.Count(v => v.Code == "UA2-8.9.2.3"));
    }

    private static void AssertEachSoundAnnotationReportedOnce(PdfUa2StructureResult result)
    {
        var sound = result.Violations.Where(v => v.Code == SoundMovieCode).Select(v => v.Message).ToList();
        Assert.Equal(Annotations, sound.Count);
        Assert.Equal(Annotations, sound.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(sound, m => m.Contains("more, not listed", StringComparison.Ordinal));
    }

    /// <summary>
    /// <see cref="Pages"/> pages naming <see cref="Annotations"/> Sound annotations — through one
    /// shared /Annots array, or each through an array of its own listing the same annotations.
    /// </summary>
    private static byte[] Document(bool sharedArray)
    {
        var bodies = new List<string>();
        int sharedAnnots = 3, firstAnnot = 4, firstPage = firstAnnot + Annotations, content = firstPage + Pages;
        var kids = new StringBuilder();
        for (int p = 0; p < Pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        var refs = new StringBuilder();
        for (int i = 0; i < Annotations; i++) refs.Append(firstAnnot + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R /Lang (en) >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {Pages} /MediaBox [0 0 612 792] >>");
        bodies.Add($"[{refs}]");
        for (int i = 0; i < Annotations; i++)
            bodies.Add($"<< /Type /Annot /Subtype /Sound /Rect [{i} 0 {i + 1} 1] /Contents (sound {i}) >>");
        string annots = sharedArray ? $"{sharedAnnots} 0 R" : $"[{refs}]";
        for (int p = 0; p < Pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R /Annots {annots} >>");
        bodies.Add("<< /Length 0 >>\nstream\n\nendstream");

        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
