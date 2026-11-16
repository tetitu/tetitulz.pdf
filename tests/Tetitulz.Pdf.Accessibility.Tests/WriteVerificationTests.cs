// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// One invariant, pinned for every remaining accessibility writer:
/// <b>a reported count / <c>true</c> implies the output differs from the input.</b>
///
/// <para>
/// The incremental writer is deliberately failure-tolerant: it catches an <see cref="IOException"/>
/// from the caller's stream, a trailer it could not rebuild, an object it had to skip, reports the
/// cause through the assembly diagnostics sink and leaves the output holding exactly the original
/// bytes. That is the right behaviour — an accessibility edit must never corrupt a caller's file —
/// but it means every writer that returns its <i>plan</i> can report "N items changed" for an
/// output that is byte-identical to the input. The defect was found and fixed twice before this
/// round (<c>PdfAutoTagger.ContentMarked</c>, <c>PdfArtifactMarker.ArtifactsMarked</c>); these are
/// the sites that were still open.
/// </para>
///
/// <para>
/// Each site gets two tests. The <b>negative</b> one drives the writer against a sink that accepts
/// exactly the original document and then throws — precisely the "fell back to copying the
/// original" condition — and asserts zero reported, a warning present, and output equal to input.
/// The <b>positive</b> one asserts the other half of the same invariant: a non-zero report comes
/// with output bytes that differ from the input <i>and</i> a re-parse that finds the change in the
/// produced document, so the count is measured against the file rather than restated from the
/// value that produced it.
/// </para>
///
/// <para>
/// Every fixture is an in-memory <c>byte[]</c>; nothing is read from disk. The one thing not built
/// here is the sfnt font program — <see cref="MinimalTrueType"/>
/// already synthesises a real one in memory, and duplicating a TrueType builder to avoid naming it
/// would be worse than the coupling.
/// </para>
/// </summary>
public class WriteVerificationTests
{
    // =====================================================================
    //  1. PdfStructureTreeEditor.SetFigureAltText
    // =====================================================================

    [Fact]
    public void SetFigureAltText_WhenTheRevisionNeverReachesTheOutput_ReportsZeroUpdatedAndWarns()
    {
        byte[] input = TaggedPdf("Figure");
        var sink = new FailsAfterStream(input.Length);

        SetAltTextResult result;
        using (var stream = new MemoryStream(input))
            result = PdfStructureTreeEditor.SetFigureAltText(stream, sink, "A bar chart.");

        Assert.Equal(input, sink.Accepted());          // the produced file is the input
        Assert.Equal(0, result.Updated);               // so nothing was updated
        Assert.Equal(1, result.Failed);                // the figure it planned to update is accounted for
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void SetFigureAltText_SuccessfulRun_ReportsACountTheOutputActuallyContains()
    {
        byte[] input = TaggedPdf("Figure");

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        SetAltTextResult result = PdfStructureTreeEditor.SetFigureAltText(stream, output, "A bar chart.");

        byte[] produced = output.ToArray();

        Assert.Equal(1, result.Updated);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);
        Assert.Equal("A bar chart.", TextEntry(produced, FirstLeafObjectNumber, "Alt"));
    }

    // =====================================================================
    //  2. PdfStructureTreeEditor.SetActualText
    // =====================================================================

    [Fact]
    public void SetActualText_WhenTheRevisionNeverReachesTheOutput_ReportsZeroAndWarns()
    {
        byte[] input = TaggedPdf("P");
        var sink = new FailsAfterStream(input.Length);

        int count;
        using (var stream = new MemoryStream(input))
            count = PdfStructureTreeEditor.SetActualText(stream, sink, "P", (i, existing) => "Spelled out.");

        Assert.Equal(input, sink.Accepted());
        Assert.Equal(0, count);
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void SetActualText_SuccessfulRun_ReportsACountTheOutputActuallyContains()
    {
        byte[] input = TaggedPdf("P");

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        int count = PdfStructureTreeEditor.SetActualText(
            stream, output, "P", (i, existing) => "Spelled out.");

        byte[] produced = output.ToArray();

        Assert.Equal(1, count);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);
        Assert.Equal("Spelled out.", TextEntry(produced, FirstLeafObjectNumber, "ActualText"));
    }

    // =====================================================================
    //  3. PdfStructureTreeEditor.SetDocumentLanguage
    // =====================================================================

    [Fact]
    public void SetDocumentLanguage_WhenTheRevisionNeverReachesTheOutput_ReturnsFalseAndWarns()
    {
        byte[] input = UntaggedPdf();
        var sink = new FailsAfterStream(input.Length);

        bool ok;
        using (var stream = new MemoryStream(input))
            ok = PdfStructureTreeEditor.SetDocumentLanguage(stream, sink, "he-IL");

        Assert.Equal(input, sink.Accepted());
        Assert.False(ok);
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void SetDocumentLanguage_SuccessfulRun_TrueMeansTheOutputCarriesTheLanguage()
    {
        byte[] input = UntaggedPdf();

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        bool ok = PdfStructureTreeEditor.SetDocumentLanguage(stream, output, "he-IL");

        byte[] produced = output.ToArray();

        Assert.True(ok);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);

        using var doc = PdfDocument.Open(new MemoryStream(produced, writable: false));
        var lang = doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("Lang")]) as PdfString;
        Assert.NotNull(lang);
        Assert.Equal("he-IL", lang.GetText());
    }

    // =====================================================================
    //  4. PdfStructureTreeEditor.SetDocumentTitle
    // =====================================================================

    /// <summary>
    /// This site had a guard already, but the wrong one: it asked "was the edit set empty?", which
    /// is a question about the plan. Against a failing sink the edit set is non-empty and the
    /// output is still the untitled input, so the old check returned <c>true</c>.
    /// </summary>
    [Fact]
    public void SetDocumentTitle_WhenTheRevisionNeverReachesTheOutput_ReturnsFalseAndWarns()
    {
        byte[] input = UntaggedPdf();
        var sink = new FailsAfterStream(input.Length);

        bool ok;
        using (var stream = new MemoryStream(input))
            ok = PdfStructureTreeEditor.SetDocumentTitle(stream, sink, "Quarterly Report");

        Assert.Equal(input, sink.Accepted());
        Assert.False(ok);
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void SetDocumentTitle_SuccessfulRun_TrueMeansTheOutputCarriesTheTitle()
    {
        byte[] input = UntaggedPdf();

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        bool ok = PdfStructureTreeEditor.SetDocumentTitle(stream, output, "Quarterly Report");

        byte[] produced = output.ToArray();

        Assert.True(ok);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);

        using var doc = PdfDocument.Open(new MemoryStream(produced, writable: false));

        // /ViewerPreferences /DisplayDocTitle is the half of the edit that lands on the catalog and
        // is what makes the title visible in a reader's window bar (PDF/UA-1 7.1).
        var viewerPrefs = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("ViewerPreferences")]) as PdfDictionary;
        Assert.NotNull(viewerPrefs);
        Assert.True(((PdfBoolean)viewerPrefs[new PdfName("DisplayDocTitle")]).Value);
    }

    // =====================================================================
    //  5. PdfStructureTreeEditor.FixHeadingHierarchy
    // =====================================================================

    [Fact]
    public void FixHeadingHierarchy_WhenTheRevisionNeverReachesTheOutput_ReportsZeroAndWarns()
    {
        byte[] input = TaggedPdf("H1", "H3");
        var sink = new FailsAfterStream(input.Length);

        int fixedCount;
        using (var stream = new MemoryStream(input))
            fixedCount = PdfStructureTreeEditor.FixHeadingHierarchy(stream, sink);

        Assert.Equal(input, sink.Accepted());
        Assert.Equal(0, fixedCount);
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void FixHeadingHierarchy_SuccessfulRun_ReportsACountTheOutputActuallyContains()
    {
        byte[] input = TaggedPdf("H1", "H3");

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        int fixedCount = PdfStructureTreeEditor.FixHeadingHierarchy(stream, output);

        byte[] produced = output.ToArray();

        Assert.Equal(1, fixedCount);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);

        // The H3 really is an H2 in the produced bytes, and the H1 was left alone.
        Assert.Equal("H1", NameEntry(produced, FirstLeafObjectNumber,     "S"));
        Assert.Equal("H2", NameEntry(produced, FirstLeafObjectNumber + 1, "S"));
    }

    // =====================================================================
    //  6. PdfStructureTreeEditor.SetFieldTooltip
    // =====================================================================

    [Fact]
    public void SetFieldTooltip_WhenTheRevisionNeverReachesTheOutput_ReturnsFalseAndWarns()
    {
        byte[] input = FormPdf();
        var sink = new FailsAfterStream(input.Length);

        bool ok;
        using (var stream = new MemoryStream(input))
            ok = PdfStructureTreeEditor.SetFieldTooltip(stream, sink, "Surname", "Family name");

        Assert.Equal(input, sink.Accepted());
        Assert.False(ok);
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void SetFieldTooltip_SuccessfulRun_TrueMeansTheOutputCarriesTheTooltip()
    {
        byte[] input = FormPdf();

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        bool ok = PdfStructureTreeEditor.SetFieldTooltip(stream, output, "Surname", "Family name");

        byte[] produced = output.ToArray();

        Assert.True(ok);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);
        Assert.Equal("Family name", TextEntry(produced, FormFieldObjectNumber, "TU"));
    }

    // =====================================================================
    //  7. PdfStructureTreeEditor.SetStructureTabOrder
    // =====================================================================

    [Fact]
    public void SetStructureTabOrder_WhenTheRevisionNeverReachesTheOutput_ReportsZeroAndWarns()
    {
        byte[] input = AnnotatedPdf();
        var sink = new FailsAfterStream(input.Length);

        int changed;
        using (var stream = new MemoryStream(input))
            changed = PdfStructureTreeEditor.SetStructureTabOrder(stream, sink);

        Assert.Equal(input, sink.Accepted());
        Assert.Equal(0, changed);
        AssertWriteWarning(PdfStructureTreeEditor.LastWriteError);
    }

    [Fact]
    public void SetStructureTabOrder_SuccessfulRun_ReportsACountTheOutputActuallyContains()
    {
        byte[] input = AnnotatedPdf();

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        int changed = PdfStructureTreeEditor.SetStructureTabOrder(stream, output);

        byte[] produced = output.ToArray();

        Assert.Equal(1, changed);
        Assert.NotEqual(input, produced);
        Assert.Null(PdfStructureTreeEditor.LastWriteError);

        using var doc = PdfDocument.Open(new MemoryStream(produced, writable: false));
        var tabs = doc.ResolveObject(doc.Pages[0].Dictionary[new PdfName("Tabs")]) as PdfName;
        Assert.NotNull(tabs);
        Assert.Equal("S", tabs.Value);
    }

    // =====================================================================
    //  8. PdfReadingOrderAnalyzer.Fix
    // =====================================================================

    [Fact]
    public void ReadingOrderFix_WhenTheRevisionNeverReachesTheOutput_ReportsZeroAndWarns()
    {
        byte[] input = ScrambledPdf();
        var sink = new FailsAfterStream(input.Length);

        ReadingOrderFixResult result;
        using (var stream = new MemoryStream(input))
            result = PdfReadingOrderAnalyzer.Fix(stream, sink);

        Assert.Equal(input, sink.Accepted());
        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);

        // ReadingOrderFixResult carries a count and nothing else, so LastError is this operation's
        // warning channel.
        AssertWriteWarning(PdfReadingOrderAnalyzer.LastError);
    }

    [Fact]
    public void ReadingOrderFix_SuccessfulRun_ReportsACountTheOutputActuallyContains()
    {
        byte[] input = ScrambledPdf();

        // The fixture is tagged bottom-first: object 9 (y = 500) is tagged before object 10
        // (y = 600) while sitting lower on the page, so the tree order is the wrong way round.
        Assert.Equal(new[] { FirstLeafObjectNumber, FirstLeafObjectNumber + 1 }, DocumentKidNumbers(input));

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(stream, output);

        byte[] produced = output.ToArray();

        Assert.True(result.ElementsReordered > 0);
        Assert.True(result.Modified);
        Assert.NotEqual(input, produced);

        // The permutation is in the produced bytes: the two /K slots swapped occupants.
        Assert.Equal(
            new[] { FirstLeafObjectNumber + 1, FirstLeafObjectNumber },
            DocumentKidNumbers(produced));
    }

    // =====================================================================
    //  9. PdfFontEmbeddingRemediator.EmbedMissingFonts
    // =====================================================================

    [Fact]
    public void EmbedMissingFonts_WhenTheRevisionNeverReachesTheOutput_ReportsZeroAndWarns()
    {
        byte[] input = NonEmbeddedFontPdf();
        var options = new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
            SubsetEmbeddedFonts = false,
        };

        var sink = new FailsAfterStream(input.Length);

        FontEmbeddingResult result;
        using (var stream = new MemoryStream(input))
            result = PdfFontEmbeddingRemediator.EmbedMissingFonts(stream, sink, options);

        Assert.Equal(input, sink.Accepted());

        Assert.Equal(0, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSubsetted);
        Assert.False(result.Changed);

        // The font whose program was lost is still an open UA21/UA25 violation, with the write
        // failure as its stated reason — not silently dropped from the accounting.
        Assert.Equal(1, result.FontsSkipped);
        Assert.Contains(result.SkippedFonts,
            s => s.Reason.IndexOf("never reached the output stream", StringComparison.Ordinal) >= 0);

        Assert.Contains(result.Warnings,
            w => w.IndexOf("never reached the output stream", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void EmbedMissingFonts_SuccessfulRun_ReportsACountTheOutputActuallyContains()
    {
        byte[] input = NonEmbeddedFontPdf();
        var options = new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
            SubsetEmbeddedFonts = false,
        };

        using var stream = new MemoryStream(input);
        using var output = new MemoryStream();
        FontEmbeddingResult result = PdfFontEmbeddingRemediator.EmbedMissingFonts(stream, output, options);

        byte[] produced = output.ToArray();

        Assert.Equal(1, result.FontsEmbedded);
        Assert.Equal(0, result.FontsSkipped);
        Assert.True(result.Changed);
        Assert.NotEqual(input, produced);

        // The program is really in the file: the descriptor names a /FontFile2 that resolves to a
        // stream with bytes in it.
        using var doc = PdfDocument.Open(new MemoryStream(produced, writable: false));
        var resources = doc.Pages[0].Resources;
        var fonts = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;
        var fontDict = doc.ResolveObject(fonts[new PdfName("F1")]) as PdfDictionary;
        var descriptor = doc.ResolveObject(fontDict[new PdfName("FontDescriptor")]) as PdfDictionary;
        Assert.NotNull(descriptor);

        var program = doc.ResolveObject(descriptor[new PdfName("FontFile2")]) as PdfStream;
        Assert.NotNull(program);
        Assert.True(program.GetDecodedData().Length > 0);
    }

    // =====================================================================
    //  Shared assertions
    // =====================================================================

    /// <summary>
    /// A lost revision must never be silent: the operation's warning channel has to name it, or a
    /// caller who got 0 back cannot tell "nothing to do" from "the write did not land".
    /// </summary>
    private static void AssertWriteWarning(string channel)
    {
        Assert.NotNull(channel);
        Assert.Contains("never reached the output stream", channel, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Fixtures — every one an in-memory byte[]; nothing touches disk
    // =====================================================================

    /// <summary>
    /// Object number of the first structure-element leaf produced by <see cref="TaggedPdf"/>:
    /// 1 catalog, 2 pages, 3 page, 4 contents, 5 font, 6 StructTreeRoot, 7 ParentTree,
    /// 8 &lt;Document&gt;, then one leaf per role.
    /// </summary>
    private const int FirstLeafObjectNumber = 9;

    /// <summary>Object number of the single AcroForm field in <see cref="FormPdf"/>.</summary>
    private const int FormFieldObjectNumber = 5;

    /// <summary>
    /// A tagged single-page document whose <c>&lt;Document&gt;</c> holds one leaf element per entry
    /// of <paramref name="leafRoles"/>, each owning one marked-content sequence on the page and one
    /// <c>/ParentTree</c> entry — so the tree genuinely reaches content and every element is a real
    /// indirect object the incremental writer can revise.
    /// </summary>
    /// <param name="leafRoles">The <c>/S</c> of each leaf, in document order.</param>
    private static byte[] TaggedPdf(params string[] leafRoles) => TaggedPdf(false, leafRoles);

    /// <summary>
    /// As <see cref="TaggedPdf(string[])"/>. When <paramref name="bottomFirst"/> is true the leaves
    /// are drawn from the bottom of the page upward while still being tagged in list order, which
    /// is a real, repairable single-column reading-order defect.
    /// </summary>
    private static byte[] TaggedPdf(bool bottomFirst, params string[] leafRoles)
    {
        int leafCount = leafRoles.Length;

        var content = new StringBuilder();
        for (int i = 0; i < leafCount; i++)
        {
            // 100 pt apart in the bottom-first layout, so the two blocks cannot be mistaken for one
            // horizontal band (which the analyzer refuses to reorder, by design).
            int y = bottomFirst ? 500 + (100 * i) : 720 - (18 * i);
            content.Append("/P <</MCID ").Append(i.ToString(CultureInfo.InvariantCulture))
                   .Append(">> BDC\nBT /F1 12 Tf 72 ").Append(y.ToString(CultureInfo.InvariantCulture))
                   .Append(" Td (Block ").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                   .Append(") Tj ET\nEMC\n");
        }

        var leafRefs = new StringBuilder();
        for (int i = 0; i < leafCount; i++)
        {
            leafRefs.Append((FirstLeafObjectNumber + i).ToString(CultureInfo.InvariantCulture))
                    .Append(" 0 R ");
        }

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(content.ToString()),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree 7 0 R /ParentTreeNextKey 1 >>",
            "<< /Nums [0 [" + leafRefs + "]] >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [" + leafRefs + "] >>",
        };

        for (int i = 0; i < leafCount; i++)
        {
            bodies.Add("<< /Type /StructElem /S /" + leafRoles[i] + " /P 8 0 R /Pg 3 0 R /K "
                     + i.ToString(CultureInfo.InvariantCulture) + " >>");
        }

        return BuildPdf(bodies);
    }

    /// <summary>Two paragraphs of one column, tagged bottom-first.</summary>
    private static byte[] ScrambledPdf() => TaggedPdf(bottomFirst: true, "P", "P");

    /// <summary>Minimal untagged document — a catalog, a page tree and one page.</summary>
    private static byte[] UntaggedPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
    });

    /// <summary>
    /// A document with one AcroForm text field named <c>Surname</c> (object
    /// <see cref="FormFieldObjectNumber"/>) and no <c>/TU</c> on it.
    /// </summary>
    private static byte[] FormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] >>",
        "<< /Fields [5 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Surname) /Rect [72 700 272 720] >>",
    });

    /// <summary>A page that carries an annotation and no <c>/Tabs</c> — exactly the UA09 case.</summary>
    private static byte[] AnnotatedPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
        "<< /Type /Annot /Subtype /Link /Rect [72 700 272 720] >>",
    });

    /// <summary>
    /// A tagged page drawing text with a TrueType font whose descriptor carries no program — the
    /// open UA21/UA25 shape the embedding step exists to close. Object 5 is the font, 9 its
    /// descriptor.
    /// </summary>
    private static byte[] NonEmbeddedFontPdf()
    {
        var widths = new StringBuilder("[");
        for (int code = 32; code <= 122; code++) widths.Append("600 ");
        widths.Append(']');

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (Hello) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 32 /LastChar 122 "
                + "/Widths " + widths + " /Encoding /WinAnsiEncoding /FontDescriptor 9 0 R >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [7 0 R]] >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 32 /FontBBox [0 -200 1000 900] "
                + "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        });
    }

    // ---------------------------------------------------------------------
    //  Minimal classic-xref PDF assembly
    // ---------------------------------------------------------------------

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(128 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---------------------------------------------------------------------
    //  Read-back helpers — every assertion below comes out of produced bytes
    // ---------------------------------------------------------------------

    private static string TextEntry(byte[] pdf, int objectNumber, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var dict = doc.GetObject(objectNumber) as PdfDictionary;
        Assert.NotNull(dict);

        var value = doc.ResolveObject(dict[new PdfName(key)]) as PdfString;
        return value?.GetText();
    }

    private static string NameEntry(byte[] pdf, int objectNumber, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var dict = doc.GetObject(objectNumber) as PdfDictionary;
        Assert.NotNull(dict);

        var value = doc.ResolveObject(dict[new PdfName(key)]) as PdfName;
        return value?.Value;
    }

    /// <summary>Object numbers listed by the <c>&lt;Document&gt;</c> element's <c>/K</c>.</summary>
    private static IReadOnlyList<int> DocumentKidNumbers(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var element = doc.GetObject(8) as PdfDictionary;
        Assert.NotNull(element);

        var kids = doc.ResolveObject(element[new PdfName("K")]) as PdfArray;
        Assert.NotNull(kids);

        var numbers = new List<int>(kids.Count);
        for (int i = 0; i < kids.Count; i++)
        {
            var reference = kids[i] as PdfReference;
            Assert.NotNull(reference);
            numbers.Add(reference.ReferenceObjectNumber);
        }

        return numbers;
    }

    // ---------------------------------------------------------------------
    //  The failing sink
    // ---------------------------------------------------------------------

    /// <summary>
    /// Write-only sink that accepts the first <c>limit</c> bytes and then throws
    /// <see cref="IOException"/> — a disk filling up part-way through the copy. Sized at the
    /// original document's length it reproduces exactly the condition these tests are about: the
    /// incremental writer emits the original bytes first and the appended revision second, so the
    /// original lands and the revision does not. Everything accepted is retained so a test can
    /// compare the produced file with the input.
    ///
    /// <para>
    /// <see cref="CanSeek"/> is false, which is also the harder case for the writer. Deliberately a
    /// private copy of the equivalent fixture in <c>WriteAccountingTests</c>: a shared test double
    /// that two suites tune against each other is how a fixture stops reproducing the condition it
    /// was written for.
    /// </para>
    /// </summary>
    private sealed class FailsAfterStream : Stream
    {
        private readonly MemoryStream _accepted = new MemoryStream();
        private readonly long _limit;

        internal FailsAfterStream(long limit) => _limit = limit;

        /// <summary>Exactly the bytes the caller managed to write.</summary>
        internal byte[] Accepted() => _accepted.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => _accepted.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Reject the whole write rather than truncating it: a stream that accepted part of a
            // buffer and then failed would make the byte-for-byte comparison ambiguous.
            if (_accepted.Length + count > _limit)
                throw new IOException("There is not enough space on the disk.");

            _accepted.Write(buffer, offset, count);
        }
    }
}
