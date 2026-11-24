// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Tetitulz.Pdf.Plugins.Docx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// <see cref="PdfToPptxConverter"/>'s OPC package shape.
///
/// <para><b>Why the assertions are what they are.</b> A presentation part and slide parts and
/// nothing else — no slide master, no slide layout, and therefore no
/// <c>p:sldMasterIdList</c> and no slide-to-layout relationship — make a package that zips, unzips and
/// parses as XML, so "bytes came back", "it starts with PK", and even "the slide XML contains the
/// page's text" all pass while PowerPoint shows the repair prompt and drops every slide. Only a
/// schema/relationship-level check separates the two, which is why <see cref="OpenXmlValidator"/>
/// is the primary assertion here and the part/relationship checks localise what it would
/// report.</para>
/// </summary>
public class PptxPackageStructureTests
{
    private static byte[] Pptx(int pageCount) =>
        PdfToPptxConverter.Convert(MinimalPdfBuilder.CreateMultiPagePdf(pageCount));

    /// <summary>
    /// The SDK validator walks the schema <i>and</i> the part/relationship constraints, so a
    /// missing master, a missing layout, or the two id-lists appended in the wrong order all
    /// surface here. <c>CT_Presentation</c> is a sequence: <c>p:sldMasterIdList</c> must precede
    /// <c>p:sldIdList</c>, and appending them the other way round produces a file that is
    /// byte-plausible and schema-invalid.
    /// </summary>
    [Fact]
    public void Converted_presentation_has_no_OpenXml_validation_errors()
    {
        using var ms = new MemoryStream(Pptx(2));
        using var pptDoc = PresentationDocument.Open(ms, false);

        var errors = new OpenXmlValidator().Validate(pptDoc).ToList();

        Assert.True(errors.Count == 0,
            "OpenXmlValidator reported " + errors.Count + " problem(s): " +
            string.Join(" | ", errors.Select(e => e.Path?.XPath + " -> " + e.Description)));
    }

    /// <summary>
    /// The master and layout must be real parts in the package, not merely referenced. A file
    /// naming a relationship id that resolves to nothing is exactly the repair case.
    /// </summary>
    [Fact]
    public void Package_contains_a_slide_master_and_a_slide_layout()
    {
        using var ms = new MemoryStream(Pptx(1));
        using var pptDoc = PresentationDocument.Open(ms, false);

        var masters = pptDoc.PresentationPart!.SlideMasterParts.ToList();
        Assert.Single(masters);
        Assert.NotNull(masters[0].SlideMaster);
        Assert.Single(masters[0].SlideLayoutParts);
        Assert.NotNull(masters[0].SlideLayoutParts.First().SlideLayout);
    }

    /// <summary>
    /// Every slide relates to a layout. The parts existing is not enough — the relationship is
    /// what PowerPoint resolves, and a slide without it is dropped on repair.
    /// </summary>
    [Fact]
    public void Every_slide_relates_to_a_slide_layout()
    {
        using var ms = new MemoryStream(Pptx(3));
        using var pptDoc = PresentationDocument.Open(ms, false);

        var slides = pptDoc.PresentationPart!.SlideParts.ToList();
        Assert.Equal(3, slides.Count);
        foreach (var slide in slides)
            Assert.NotNull(slide.SlideLayoutPart);
    }

    /// <summary>
    /// The presentation itself names the master. Without <c>p:sldMasterIdList</c> the master part
    /// is an orphan the consumer never reaches, which looks identical in the zip listing.
    /// </summary>
    [Fact]
    public void Presentation_declares_the_master_before_its_slides()
    {
        using var ms = new MemoryStream(Pptx(2));
        using var pptDoc = PresentationDocument.Open(ms, false);

        var presentation = pptDoc.PresentationPart!.Presentation;
        var masterList = presentation.GetFirstChild<DocumentFormat.OpenXml.Presentation.SlideMasterIdList>();
        var slideList = presentation.GetFirstChild<DocumentFormat.OpenXml.Presentation.SlideIdList>();

        Assert.NotNull(masterList);
        Assert.NotNull(slideList);
        Assert.Single(masterList!.Elements<DocumentFormat.OpenXml.Presentation.SlideMasterId>());

        var children = presentation.ChildElements.ToList();
        Assert.True(children.IndexOf(masterList!) < children.IndexOf(slideList!),
            "p:sldMasterIdList must come before p:sldIdList; CT_Presentation is a sequence.");
    }

    /// <summary>
    /// A floor under the three checks above: the package must still be a readable zip carrying the
    /// master and layout at their conventional part names, so a change that satisfied the SDK
    /// object model while writing nothing to the archive would not slip through.
    /// </summary>
    [Fact]
    public void Archive_carries_the_master_and_layout_parts()
    {
        using var archive = new ZipArchive(new MemoryStream(Pptx(1)), ZipArchiveMode.Read);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains(names, n => n.Contains("slideMasters/slideMaster"));
        Assert.Contains(names, n => n.Contains("slideLayouts/slideLayout"));
    }
}
