// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The glyph scan walks a Type 3 font's <c>/CharProcs</c> once for the procedures and resources it is
/// walked under — not again for each font sharing one <c>/CharProcs</c>, nor, for a font written
/// directly in the <c>/Font</c> dictionary, which has no object number to cache it under, again at
/// every <c>Tf</c>. The glyphs recorded are the same whether
/// the walk ran once or two hundred times, so the evidence is the count of walks.
/// </summary>
public sealed class Type3ProcedureWalkOnceTests
{
    [Fact]
    public void TwoHundredFontsSharingTheirProcedures_WalkThemOnce()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[20] = CharProcs(objects);
        objects[21] = new PdfDictionary();   // the fonts' shared /Resources
        var fonts = new PdfDictionary();
        var content = new StringBuilder("BT\n");
        for (int i = 0; i < 200; i++)
        {
            objects[100 + i] = Type3Font(new PdfReference(20, 0), new PdfReference(21, 0));
            fonts.Set(new PdfName("F" + i), new PdfReference(100 + i, 0));
            content.Append("/F").Append(i).Append(" 1 Tf <00> Tj\n");
        }
        content.Append("ET");

        FontGlyphUsageScanner.Result result = Scan(objects, fonts, content.ToString());

        Assert.Null(result.Reason);
        Assert.Equal(1, result.Type3ProcedureWalks);
        Assert.Equal(200, result.FontsAnalysed);   // two hundred fonts, each analysed once
    }

    /// <summary>The control: the same procedures under two different <c>/Resources</c> are two walks.</summary>
    [Fact]
    public void SharedProceduresUnderOtherResources_AreWalkedForEach()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[20] = CharProcs(objects);
        objects[21] = new PdfDictionary();
        objects[22] = new PdfDictionary();
        objects[100] = Type3Font(new PdfReference(20, 0), new PdfReference(21, 0));
        objects[101] = Type3Font(new PdfReference(20, 0), new PdfReference(22, 0));
        var fonts = new PdfDictionary();
        fonts.Set(new PdfName("F0"), new PdfReference(100, 0));
        fonts.Set(new PdfName("F1"), new PdfReference(101, 0));

        FontGlyphUsageScanner.Result result = Scan(objects, fonts, "BT /F0 1 Tf <00> Tj /F1 1 Tf <00> Tj ET");

        Assert.Null(result.Reason);
        Assert.Equal(2, result.Type3ProcedureWalks);
    }

    [Fact]
    public void ADirectFontSelectedAHundredTimes_IsWalkedOnce()
    {
        var objects = new Dictionary<int, PdfObject>();
        objects[20] = CharProcs(objects);
        var fonts = new PdfDictionary();
        fonts.Set(new PdfName("F0"), Type3Font(new PdfReference(20, 0), null));   // written in place
        var content = new StringBuilder("BT\n");
        for (int i = 0; i < 100; i++) content.Append("/F0 1 Tf <00> Tj\n");
        content.Append("ET");

        FontGlyphUsageScanner.Result result = Scan(objects, fonts, content.ToString());

        Assert.Null(result.Reason);
        Assert.Equal(1, result.Type3ProcedureWalks);
        Assert.Equal(1, result.FontsAnalysed);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A <c>/CharProcs</c> of one procedure, a glyph that fills a square (object 30).</summary>
    private static PdfDictionary CharProcs(Dictionary<int, PdfObject> objects)
    {
        objects[30] = new PdfStream { RawData = Encoding.ASCII.GetBytes("1000 0 d0 0 0 1000 1000 re f") };
        var procs = new PdfDictionary();
        procs.Set(new PdfName("a"), new PdfReference(30, 0));
        return procs;
    }

    private static PdfDictionary Type3Font(PdfReference charProcs, PdfReference resources)
    {
        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Font);
        font.Set(PdfName.Subtype, new PdfName("Type3"));
        font.Set(PdfName.CharProcs, charProcs);
        if (resources != null) font.Set(PdfName.Resources, resources);
        return font;
    }

    private static FontGlyphUsageScanner.Result Scan(Dictionary<int, PdfObject> objects, PdfDictionary fonts, string content)
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, new PdfName("Catalog"));
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        objects[1] = catalog;

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, new PdfName("Pages"));
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));
        objects[2] = pages;

        var resources = new PdfDictionary();
        resources.Set(PdfName.Font, fonts);
        var box = new PdfArray();
        foreach (double v in new double[] { 0, 0, 612, 792 }) box.Add(new PdfNumber(v));
        var page = new PdfDictionary();
        page.Set(PdfName.Type, new PdfName("Page"));
        page.Set(PdfName.Parent, new PdfReference(2, 0));
        page.Set(PdfName.MediaBox, box);
        page.Set(PdfName.Resources, resources);
        page.Set(PdfName.Contents, new PdfReference(4, 0));
        objects[3] = page;
        objects[4] = new PdfStream { RawData = Encoding.ASCII.GetBytes(content) };

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return FontGlyphUsageScanner.Scan(objects, trailer, new OptimizationOptions());
    }
}
