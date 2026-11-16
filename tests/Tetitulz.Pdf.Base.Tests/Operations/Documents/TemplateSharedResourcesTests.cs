// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The template engine's replacement font is added to one copy of the resources for every page that
/// starts from the same dictionary: a direct copy on each page wrote the whole dictionary once a
/// page — 9.9 MB from 224 KB at 800 pages sharing 800 XObjects.
/// </summary>
public class TemplateSharedResourcesTests
{
    private const int Pages = 40, Entries = 40;

    [Fact]
    public void AdversarialPagesSharingResources_ShareOneCopyCarryingTheReplacementFont()
    {
        byte[] output = Replace(Document(Pages, Entries, ownResourcesOnLastPage: false));

        string text = Encoding.Latin1.GetString(output);
        Assert.InRange(Regex.Matches(text, @"/Im0 ").Count, 1, 2);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var first = Assert.IsType<PdfReference>(doc.Pages[0].Dictionary[PdfName.Resources]);
        for (int p = 0; p < Pages; p++)
        {
            var named = Assert.IsType<PdfReference>(doc.Pages[p].Dictionary[PdfName.Resources]);
            Assert.Equal(first.ReferenceObjectNumber, named.ReferenceObjectNumber);
        }
        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(first));
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.Font]));
        Assert.NotNull(fonts[new PdfName("F0")]);
        Assert.NotNull(fonts[new PdfName("TplF0")]);
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.XObject]));
        Assert.NotNull(xobjects[new PdfName("Im" + (Entries - 1))]);
    }

    /// <summary>A page with resources of its own is given a copy of its own, its fonts and the replacement font in it.</summary>
    [Fact]
    public void APageWithItsOwnResources_GetsACopyOfItsOwn()
    {
        byte[] output = Replace(Document(3, 2, ownResourcesOnLastPage: true));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var shared = Assert.IsType<PdfReference>(doc.Pages[0].Dictionary[PdfName.Resources]);
        var own = Assert.IsType<PdfReference>(doc.Pages[2].Dictionary[PdfName.Resources]);
        Assert.NotEqual(shared.ReferenceObjectNumber, own.ReferenceObjectNumber);
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(Assert.IsType<PdfDictionary>(doc.ResolveObject(own))[PdfName.Font]));
        Assert.NotNull(fonts[new PdfName("F9")]);
        Assert.NotNull(fonts[new PdfName("TplF0")]);
    }

    private static byte[] Replace(byte[] pdf)
    {
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.Replace(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["name"] = "ābc" },
            new TemplateReplaceOptions { AllowSystemFontSubstitution = true, VerifyErasure = false });
        Assert.Contains("name", result.ReplacedKeys);
        return output.ToArray();
    }

    /// <summary>
    /// <paramref name="pages"/> pages with a <c>{{name}}</c> token each, inheriting one indirect
    /// <c>/Resources</c> of a font and <paramref name="entries"/> images; the last page, with
    /// <paramref name="ownResourcesOnLastPage"/>, has a dictionary of its own naming font F9.
    /// </summary>
    private static byte[] Document(int pages, int entries, bool ownResourcesOnLastPage)
    {
        var bodies = new List<string>();
        int images = 5, firstPage = images + entries, content = firstPage + pages;
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        var x = new StringBuilder();
        for (int i = 0; i < entries; i++) x.Append("/Im").Append(i).Append(' ').Append(images + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] /Resources 3 0 R >>");
        bodies.Add($"<< /Font << /F0 4 0 R >> /XObject << {x}>> >>");
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < entries; i++)
            bodies.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\nabc\nendstream");
        for (int p = 0; p < pages; p++)
            bodies.Add(ownResourcesOnLastPage && p == pages - 1
                ? $"<< /Type /Page /Parent 2 0 R /Contents {content + 1} 0 R /Resources << /Font << /F9 4 0 R >> >> >>"
                : $"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R >>");
        string text = "BT /F0 12 Tf 72 700 Td ({{name}}) Tj ET";
        bodies.Add($"<< /Length {text.Length} >>\nstream\n{text}\nendstream");
        string own = "BT /F9 12 Tf 72 700 Td ({{name}}) Tj ET";
        bodies.Add($"<< /Length {own.Length} >>\nstream\n{own}\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
