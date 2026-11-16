// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A revision that refers to an object the document already has names it at that object's own
/// generation. <c>N 0 R</c> to an object at generation 1 names no object (§7.3.10), so a strict
/// reader drops the page an annotation belongs to, the <c>/Info</c> a title went into, the form a
/// field joined.
/// </summary>
/// <remarks>
/// <para>Every site here built <c>new PdfReference(n, 0)</c> to an existing object. The
/// incremental writer now keeps a superseded object's generation itself
/// (<c>IncrementalUpdateWriter.KeptGeneration</c>), but it cannot rewrite a reference. It cannot
/// tell one a caller built from one copied out of the original, and a stale reference copied
/// through must stay stale. So each reference site asks <c>ExistingObjectGeneration</c>.</para>
///
/// <para>Asserted on the appended bytes, because this library's parser resolves by number alone
/// and would reopen either spelling (CLAUDE.md's first blind spot). Each positive assertion has its
/// <c>… 0 R</c> negative beside it.</para>
/// </remarks>
public class ExistingObjectReferenceGenerationTests
{
    private const string Rgb = "RGB ";

    /// <summary>
    /// Catalog 1 at <paramref name="catalogGeneration"/>. Pages 2, page 3, /Info 4, /AcroForm 5 and
    /// /Names 6 all at generation 1, and every reference to them says so.
    /// </summary>
    private static byte[] AtGenerationOne(int catalogGeneration = 0) => new PdfFixtureBuilder().Header()
        .ObjAtGeneration(1, catalogGeneration, "<< /Type /Catalog /Pages 2 1 R /AcroForm 5 1 R /Names 6 1 R >>")
        .ObjAtGeneration(2, 1, "<< /Type /Pages /Kids [3 1 R] /Count 1 >>")
        .ObjAtGeneration(3, 1, "<< /Type /Page /Parent 2 1 R /MediaBox [0 0 612 792] >>")
        .ObjAtGeneration(4, 1, "<< /Title (Before) >>")
        .ObjAtGeneration(5, 1, "<< /Fields [] >>")
        .ObjAtGeneration(6, 1, "<< >>")
        .Finish(" /Info 4 1 R", root: "1 " + catalogGeneration + " R");

    private static string Appended(byte[] original, MemoryStream output)
    {
        byte[] all = output.ToArray();
        return Encoding.Latin1.GetString(all, original.Length, all.Length - original.Length);
    }

    [Fact]
    public void AnAnnotationNamesItsPageAtThePagesGeneration()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        PdfAnnotationWriter.AddHighlight(new MemoryStream(pdf), output, 0, 72, 700, 200, 720);

        string appended = Appended(pdf, output);
        Assert.Contains("/P 3 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/P 3 0 R", appended, StringComparison.Ordinal);
        // The page itself is superseded at its own generation — the writer's rule, not this site's.
        Assert.Matches(@"(^|\n)3 1 obj", appended);
    }

    [Fact]
    public void ABatchOfLinksNamesItsPageAtThePagesGeneration()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        int written = PdfAnnotationWriter.AddUriLinks(new MemoryStream(pdf), output, new[]
        {
            new PdfAnnotationWriter.UriLinkRequest { PageIndex = 0, X1 = 72, Y1 = 700, X2 = 200, Y2 = 720, Uri = "https://example.com/" },
        });

        Assert.Equal(1, written);
        string appended = Appended(pdf, output);
        Assert.Contains("/P 3 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/P 3 0 R", appended, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportedAnnotationNamesItsPageAtThePagesGeneration()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        int written = PdfAnnotationWriter.WriteAnnotations(new MemoryStream(pdf), output, new[]
        {
            new FormDataAnnotation("Square") { PageIndex = 0, Rect = new double[] { 72, 700, 200, 720 } },
        });

        Assert.Equal(1, written);
        string appended = Appended(pdf, output);
        Assert.Contains("/P 3 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/P 3 0 R", appended, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInfoDictionaryIsSupersededAndNamedAtItsGeneration()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteInfo(new MemoryStream(pdf), output, new PdfMetadataUpdate { Title = "After" });

        string appended = Appended(pdf, output);
        Assert.Contains("/Info 4 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Info 4 0 R", appended, StringComparison.Ordinal);
        Assert.Matches(@"(^|\n)4 1 obj", appended);
    }

    /// <summary>
    /// The trailer's <c>/Root</c> is carried, not rebuilt: the embedder re-pointed it at
    /// <c>1 0 R</c>, the same object, which was a no-op for every catalog but one at generation 1.
    /// </summary>
    [Fact]
    public void AnOutputIntentKeepsTheCatalogsGenerationInTheTrailer()
    {
        byte[] pdf = AtGenerationOne(catalogGeneration: 1);
        var profile = new byte[132];
        Encoding.ASCII.GetBytes(Rgb).CopyTo(profile, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);

        using var output = new MemoryStream();
        PdfIccEmbedder.EmbedProfile(new MemoryStream(pdf), output, profile);

        string appended = Appended(pdf, output);
        Assert.Contains("/Root 1 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Root 1 0 R", appended, StringComparison.Ordinal);
        Assert.Matches(@"(^|\n)1 1 obj", appended);
    }

    /// <summary>
    /// An indirect name dictionary is superseded where it lives, at its own generation, and the
    /// catalog is left alone. The destination writer used to rewrite the catalog too, and its
    /// reference to the dictionary had to be rebuilt at the right generation. Since the name-tree
    /// rewrite became <c>CatalogNameTree</c>, no reference is rebuilt, so there is none to get
    /// wrong.
    /// </summary>
    [Fact]
    public void ANamedDestinationSupersedesTheNamesDictionaryAtItsGeneration_AndLeavesTheCatalog()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), output, "Top", 0);

        string appended = Appended(pdf, output);
        Assert.Matches(@"(^|\n)6 1 obj", appended);
        Assert.DoesNotMatch(@"(^|\n)6 0 obj", appended);
        Assert.DoesNotContain("/Names 6 0 R", appended, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(^|\n)1 0 obj", appended);
    }

    [Fact]
    public void AnInsertedPageNamesItsParentAtThePageTreesGeneration()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        PdfPageManipulator.InsertBlankPage(new MemoryStream(pdf), output, 1, 612, 792);

        string appended = Appended(pdf, output);
        Assert.Contains("/Parent 2 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Parent 2 0 R", appended, StringComparison.Ordinal);
        Assert.Matches(@"(^|\n)2 1 obj", appended);
    }

    [Fact]
    public void ANewFieldNamesTheFormAndItsPageAtTheirGenerations()
    {
        byte[] pdf = AtGenerationOne();

        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddTextField(new MemoryStream(pdf), output,
            new FormTextField { FieldName = "name", PageIndex = 0, X = 72, Y = 700 });

        string appended = Appended(pdf, output);
        Assert.Contains("/AcroForm 5 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/AcroForm 5 0 R", appended, StringComparison.Ordinal);
        Assert.Contains("/P 3 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/P 3 0 R", appended, StringComparison.Ordinal);
    }

    [Fact]
    public void ARadioButtonNamesItsPageAtThePagesGeneration()
    {
        byte[] pdf = AtGenerationOne();
        var group = new FormRadioGroupField { FieldName = "choice", PageIndex = 0, SelectedValue = "A" };
        group.Buttons.Add(new RadioButtonOption("A", 0, 72, 700, 12, 12));
        group.Buttons.Add(new RadioButtonOption("B", 0, 92, 700, 12, 12));

        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddRadioGroup(new MemoryStream(pdf), output, group);

        string appended = Appended(pdf, output);
        Assert.Contains("/P 3 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/P 3 0 R", appended, StringComparison.Ordinal);
    }
}
