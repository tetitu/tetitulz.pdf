// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A redaction that returns has removed what stood in its region, wherever the page keeps it: not only
/// the glyphs. Each case hides a marker and reads the whole output for it — every string the file writes
/// and every stream decoded.
/// </summary>
public sealed class RedactionLeftBehindTests
{
    private const string Marker = "SECRET-MARKER";
    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string Visible = "BT /F1 12 Tf 10 100 Td (visible) Tj ET ";
    private const string TextInForm = "BT /F1 12 Tf 10 100 Td (" + Marker + ") Tj ET";

    private static string S(string body, string dict = "") => $"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream";

    private static byte[] Pdf(string content, string resources = "", string pageExtra = "", string catalogExtra = "", params string[] extra)
    {
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R]       /Count 1 >>",   // room to list a second page in place
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> {pageExtra} >>",
            S(content),
            Font,
        };
        bodies.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] Redact(byte[] pdf, float x = 0, float y = 0, float width = 200, float height = 200)
    {
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = x, Y = y, Width = width, Height = height },
        });
        return output.ToArray();
    }

    /// <summary>The file's bytes, and every stream it holds decoded.</summary>
    private static string Everything(byte[] pdf)
    {
        var all = new StringBuilder(Encoding.Latin1.GetString(pdf));
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(pdf));
        foreach (CrossReferenceEntry e in doc.CrossReferenceTable.AllEntries)
            if (e.Type != CrossReferenceEntry.EntryType.Free
                && doc.GetObject(e.ObjectNumber) is PdfStream stream
                && stream.TryGetDecodedData(1 << 24, out byte[] decoded, out _))
                all.Append(Encoding.Latin1.GetString(decoded)).Append('\n');
        return all.ToString();
    }

    private static void AssertGone(byte[] pdf)
    {
        Assert.Contains(Marker, Everything(pdf), StringComparison.Ordinal);    // the fixture holds it
        Assert.DoesNotContain(Marker, Everything(Redact(pdf)), StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> InTheContent() => new[]
    {
        new object[] { "ActualText, inline", "/Span << /ActualText (" + Marker + ") >> BDC " + Visible + "EMC" },
        new object[] { "Alt, inline", "/Figure << /Alt (" + Marker + ") >> BDC " + Visible + "EMC" },
        new object[] { "any string in a property list", "/Note << /X (" + Marker + ") >> BDC " + Visible + "EMC" },
        new object[] { "an array in a property list", "/Note << /X [(" + Marker + ")] >> BDC " + Visible + "EMC" },
        new object[] { "a marked point", "/Note << /X (" + Marker + ") >> DP " + Visible },
        new object[] { "an operator no reader defines", "BX (" + Marker + ") xyz EX " + Visible },
        new object[] { "a name operand of one", "BX /" + Marker + " xyz EX " + Visible },
        new object[] { "a string before an operator that takes none", "(" + Marker + ") " + Visible },
        new object[] { "a short \" operator", "BT (" + Marker + ") \" ET " + Visible },
    };

    /// <summary>
    /// <b>What the content stream says besides what it draws.</b> A text extractor prefers
    /// <c>/ActualText</c> to the glyphs, so this one hands the removed text straight back.
    /// </summary>
    [Theory]
    [MemberData(nameof(InTheContent))]
    public void AdversarialTextBesideTheGlyphs_IsRemoved(string where, string content)
    {
        Assert.False(string.IsNullOrEmpty(where));
        AssertGone(Pdf(content));
    }

    [Fact]
    public void AdversarialXfaPackets_AreRemoved_AcroFormFieldsStaying()
    {
        const string datasets = "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><data><f>" + Marker + "</f></data></xfa:datasets>";
        byte[] pdf = Pdf(Visible, catalogExtra: "/AcroForm << /Fields [8 0 R] /XFA [(datasets) 6 0 R] >> /NeedsRendering true",
            extra: new[] { S(datasets), "null", "<< /T (kept) /FT /Tx /V (a field outside the region) >>" });

        AssertGone(pdf);

        byte[] output = Redact(pdf);
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Null(doc.Catalog.Dictionary[PdfName.NeedsRendering]);
        Assert.Contains("a field outside the region", Everything(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TU", "(" + Marker + ")")]
    [InlineData("MK", "<< /CA (" + Marker + ") >>")]
    [InlineData("Opt", "[(" + Marker + ")]")]
    public void AdversarialWordsOnADroppedWidget_ReachedFromTheStructureTree_AreRemoved(string key, string value)
    {
        // The field tree loses the widget; a reference from the catalog keeps its dictionary in the file.
        string widget = $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /Rect [10 10 50 50] /{key} {value} >>";
        byte[] pdf = Pdf(Visible, pageExtra: "/Annots [6 0 R]", catalogExtra: "/AcroForm << /Fields [6 0 R] >> /Extra 6 0 R", extra: widget);

        AssertGone(pdf);
    }

    /// <summary>
    /// A parent field holding the value, its one widget in the region: the field tree loses both, and the
    /// calculation order (or a reset action's <c>/Fields</c>) still names the parent. Its value leaves the
    /// file with its widget, and it leaves the order. The control's parent keeps a widget outside the region.
    /// </summary>
    [Theory]
    [InlineData("/CO [6 0 R]", "")]
    [InlineData("", "/OpenAction << /S /ResetForm /Fields [6 0 R] >>")]
    public void AdversarialParentFieldValue_StillNamedOutsideTheFieldTree_IsRemoved(string acroExtra, string catalogExtra)
    {
        string parent = "<< /FT /Tx /T (total) /V (" + Marker + ") /Kids [7 0 R] /AA << /C << /S /JavaScript /JS (event.value=1) >> >> >>";
        string widget = "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [50 50 150 70] /P 3 0 R >>";
        byte[] pdf = Pdf(Visible, pageExtra: "/Annots [7 0 R]",
            catalogExtra: $"/AcroForm << /Fields [6 0 R] {acroExtra} >> {catalogExtra}", extra: new[] { parent, widget });

        AssertGone(pdf);

        if (acroExtra.Length > 0)
        {
            using PdfDocument doc = PdfDocument.Open(new MemoryStream(Redact(pdf)));
            var acro = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
            Assert.Empty((PdfArray)doc.ResolveObject(acro[PdfName.CO]));
        }

        // Control: a second widget outside the region keeps the parent, its value and its place in the order.
        string kept = "<< /FT /Tx /T (total) /V (" + Marker + ") /Kids [7 0 R 8 0 R] >>";
        string outside = "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [150 150 190 190] /P 3 0 R >>";
        byte[] control = Pdf(Visible, pageExtra: "/Annots [7 0 R 8 0 R]",
            catalogExtra: "/AcroForm << /Fields [6 0 R] /CO [6 0 R] >>", extra: new[] { kept, widget, outside });
        Assert.Contains(Marker, Everything(Redact(control, 40, 40, 120, 40)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An associated file of a structure element on the redacted page — a formula's source — is the source
    /// of what the region held, as the page's own <c>/AF</c> is: it leaves with the element's alternatives.
    /// </summary>
    [Fact]
    public void AdversarialAssociatedFileOfAStructureElement_OnTheRedactedPage_IsRemoved()
    {
        byte[] pdf = Pdf("/P << /MCID 0 >> BDC " + Visible + "EMC", pageExtra: "/StructParents 0",
            catalogExtra: "/StructTreeRoot 6 0 R /MarkInfo << /Marked true >>",
            extra: new[]
            {
                "<< /Type /StructTreeRoot /K 7 0 R >>",
                "<< /Type /StructElem /S /Formula /P 6 0 R /Pg 3 0 R /K 0 /AF [8 0 R] >>",
                "<< /Type /Filespec /F (source.tex) /UF (source.tex) /AFRelationship /Source /EF << /F 9 0 R >> >>",
                S(Marker, "/Type /EmbeddedFile"),
            });

        AssertGone(pdf);
    }

    /// <summary>
    /// A file attachment in the region is dropped from the page, and the structure tree's object reference
    /// still reaches its dictionary: the file it carries (<c>/FS</c>), and an annotation's associated files
    /// (<c>/AF</c>), leave with its words.
    /// </summary>
    [Theory]
    [InlineData("/Subtype /FileAttachment /FS 7 0 R")]
    [InlineData("/Subtype /FreeText /AF [7 0 R]")]
    public void AdversarialFileOfADroppedAnnotation_ReachedFromTheStructureTree_IsRemoved(string entries)
    {
        byte[] pdf = Pdf(Visible, pageExtra: "/Annots [6 0 R]", catalogExtra: "/StructTreeRoot 9 0 R",
            extra: new[]
            {
                "<< /Type /Annot " + entries + " /Rect [10 10 50 50] /P 3 0 R >>",
                "<< /Type /Filespec /F (a.txt) /UF (a.txt) /EF << /F 8 0 R >> >>",
                S(Marker, "/Type /EmbeddedFile"),
                "<< /Type /StructTreeRoot /K << /S /Annot /Pg 3 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >> >>",
            });

        AssertGone(pdf);
    }

    /// <summary>A merged field and widget in the region leaves the calculation order with the field tree.</summary>
    [Fact]
    public void AdversarialMergedFieldInTheCalculationOrder_LeavesIt()
    {
        string merged = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /V (" + Marker + ") /Rect [50 50 150 70] /P 3 0 R >>";
        byte[] pdf = Pdf(Visible, pageExtra: "/Annots [6 0 R]", catalogExtra: "/AcroForm << /Fields [6 0 R] /CO [6 0 R] >>", extra: merged);

        AssertGone(pdf);
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(Redact(pdf)));
        var acro = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]);
        Assert.Empty((PdfArray)doc.ResolveObject(acro[PdfName.CO]));
        Assert.Empty((PdfArray)doc.ResolveObject(acro[PdfName.Fields]));
    }

    [Fact]
    public void AdversarialAnnotation_WithReferencedRectNumbers_IsDropped()
    {
        byte[] pdf = Pdf(Visible, pageExtra: "/Annots [6 0 R]",
            extra: new[] { "<< /Type /Annot /Subtype /FreeText /Contents (" + Marker + ") /Rect [7 0 R 7 0 R 8 0 R 8 0 R] >>", "10", "50" });

        AssertGone(pdf);
    }

    [Fact]
    public void AdversarialForm_WithReferencedBoxAndMatrixNumbers_IsRead()
    {
        const string form = "/Type /XObject /Subtype /Form /BBox [7 0 R 7 0 R 8 0 R 8 0 R] /Matrix [9 0 R 7 0 R 7 0 R 9 0 R 8 0 R 8 0 R] " +
                            "/Resources << /Font << /F1 5 0 R >> >>";
        byte[] pdf = Pdf("/X0 Do " + Visible, "/XObject << /X0 6 0 R >>",
            extra: new[] { S("BT /F1 12 Tf 10 10 Td (" + Marker + ") Tj ET", form), "0", "100", "1" });

        // The box's own corner at the matrix's origin: where the form draws, and nowhere near the page's corner.
        byte[] output = Redact(pdf, 105, 105, 20, 20);
        Assert.Contains(Marker, Everything(pdf), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Everything(output), StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialActualText_InNamedProperties_IsRemoved()
    {
        AssertGone(Pdf("/Span /P0 BDC " + Visible + "EMC", "/Properties << /P0 << /ActualText (" + Marker + ") >> >>"));
    }

    /// <summary>The structure tree's alternative for the removed text, on the element, through a reference, and above it.</summary>
    [Theory]
    [InlineData("<< /Type /StructTreeRoot /K 7 0 R >>", "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 /{0} (" + Marker + ") >>")]
    [InlineData("<< /Type /StructTreeRoot /K 7 0 R >>", "<< /Type /StructElem /S /P /P 6 0 R /K << /Type /MCR /Pg 3 0 R /MCID 0 >> /{0} (" + Marker + ") >>")]
    [InlineData("<< /Type /StructTreeRoot /K 7 0 R /{0} (" + Marker + ") >>", "<< /Type /StructElem /S /P /P 6 0 R /K << /Type /MCR /Pg 3 0 R /MCID 0 >> >>")]
    public void AdversarialStructureElementText_IsRemoved(string root, string element)
    {
        foreach (string key in new[] { "ActualText", "Alt", "E", "T" })
            AssertGone(Pdf("/P << /MCID 0 >> BDC " + Visible + "EMC", "", "/StructParents 0", "/StructTreeRoot 6 0 R",
                           string.Format(root, key), string.Format(element, key)));
    }

    /// <summary>The control: an element on another page keeps what it says.</summary>
    [Fact]
    public void AStructureElementOnAnotherPage_KeepsItsText()
    {
        byte[] pdf = Pdf("/P << /MCID 0 >> BDC " + Visible + "EMC", "", "/StructParents 0", "/StructTreeRoot 6 0 R",
            "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 /ActualText (" + Marker + ") >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 9 0 R /K 0 /ActualText (KEPT-ELSEWHERE) >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        pdf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(pdf).Replace("/Kids [3 0 R]       /Count 1", "/Kids [3 0 R 9 0 R] /Count 2"));

        string output = Everything(Redact(pdf));

        Assert.Contains("KEPT-ELSEWHERE", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, output, StringComparison.Ordinal);
    }

    /// <summary>A thumbnail is a picture of the page; the rest is what an application or a producer kept of it.</summary>
    [Theory]
    [InlineData("/Thumb 6 0 R", "/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray")]
    [InlineData("/Metadata 6 0 R", "/Type /Metadata /Subtype /XML")]
    [InlineData("/PieceInfo << /App << /Private 6 0 R >> >>", "")]
    [InlineData("/AF [<< /Type /Filespec /F (a.txt) /EF << /F 6 0 R >> >>]", "/Type /EmbeddedFile")]
    public void AdversarialPageEntry_DerivedFromThePage_IsRemoved(string entry, string dict)
    {
        AssertGone(Pdf(Visible, "", entry, "", S(Marker, dict)));
    }

    /// <summary>An image wholly covered goes, and so does what stands beside its pixels.</summary>
    [Theory]
    [InlineData("/SMask 7 0 R")]
    [InlineData("/Mask 7 0 R")]
    [InlineData("/Metadata 7 0 R")]
    [InlineData("/Alternates [<< /Image 7 0 R >>]")]
    public void AdversarialImage_RemovedWhole_TakesWhatItKeepsBesideItsPixels(string entry)
    {
        AssertGone(Pdf("q 100 0 0 100 10 50 cm /I0 Do Q", "/XObject << /I0 6 0 R >>", "", "",
            S("abcdefgh", "/Type /XObject /Subtype /Image /Width 8 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray " + entry),
            S(Marker, "/Type /XObject /Subtype /Image /Width 13 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray")));
    }

    /// <summary>A form's clone takes the form's other entries, but not these: the original goes, and they would keep it.</summary>
    [Fact]
    public void AdversarialFormEntry_OnARedactedClone_IsRemoved()
    {
        AssertGone(Pdf("/X0 Do", "/XObject << /X0 6 0 R >>", "", "",
            S(TextInForm.Replace(Marker, "gone"), "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /PieceInfo << /App << /Private (" + Marker + ") >> >> /Metadata 7 0 R"),
            S(Marker, "/Type /Metadata /Subtype /XML")));
    }

    /// <summary>What an annotation says in words, reached another way, and the popup and reply that repeat it.</summary>
    [Theory]
    [InlineData("<< /Type /Annot /Subtype /FreeText /Rect [10 100 50 120] /Contents (" + Marker + ") /DA (/F1 12 Tf) >>", "<< /Type /StructElem /S /Annot /P 8 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>")]
    [InlineData("<< /Type /Annot /Subtype /Text /Rect [10 100 50 120] /RC (" + Marker + ") >>", "<< /Type /StructElem /S /Annot /P 8 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>")]
    [InlineData("<< /Type /Annot /Subtype /Text /Rect [10 100 50 120] /T (" + Marker + ") >>", "<< /Type /StructElem /S /Annot /P 8 0 R /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>")]
    public void AdversarialAnnotationWords_ReachedThroughTheTree_AreRemoved(string annotation, string element)
    {
        AssertGone(Pdf(Visible, "", "/Annots [6 0 R]", "/StructTreeRoot 8 0 R", annotation, element, "<< /Type /StructTreeRoot /K 7 0 R >>"));
    }

    [Theory]
    [InlineData("/Parent 6 0 R /Subtype /Popup")]
    [InlineData("/IRT 6 0 R /Subtype /Text")]
    public void AdversarialPopupOrReply_OfADroppedAnnotation_IsRemoved(string link)
    {
        AssertGone(Pdf(Visible, "", "/Annots [6 0 R 7 0 R]", "",
            "<< /Type /Annot /Subtype /Text /Rect [10 100 50 120] >>",
            "<< /Type /Annot " + link + " /Rect [300 300 310 310] /Contents (" + Marker + ") >>"));
    }

    /// <summary>The control: an annotation answering to one that stays, outside the region, stays.</summary>
    [Fact]
    public void AReplyToAKeptAnnotation_StaysAndItsChainIsBounded()
    {
        byte[] pdf = Pdf(Visible, "", "/Annots [6 0 R 7 0 R]", "",
            "<< /Type /Annot /Subtype /Text /Rect [150 150 160 160] /IRT 7 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [170 170 180 180] /IRT 6 0 R /Contents (KEPT-REPLY) >>");

        Assert.Contains("KEPT-REPLY", Everything(Redact(pdf, 0, 0, 100, 100)), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A pattern's cell or a soft mask's group that draws text.</b> One stream for every tile, with
    /// no region to place on it: the redaction refuses, naming it, rather than return with the text there.
    /// </summary>
    [Theory]
    [InlineData("/Pattern cs /P0 scn 0 0 200 200 re f", "/Pattern << /P0 6 0 R >>", "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100", "tiling pattern")]
    [InlineData("/G0 gs 0 0 200 200 re f", "/ExtGState << /G0 << /SMask << /S /Luminosity /G 6 0 R >> >> >>", "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >>", "soft mask")]
    public void AdversarialPaintSource_DrawingText_IsRefused(string content, string resources, string dict, string named)
    {
        byte[] pdf = Pdf(content, resources, "", "", S(TextInForm, dict));

        var refused = Assert.Throws<PdfRedactionException>(() => Redact(pdf));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Through a form the pattern draws: the depth is the walk's, and a form it draws twice is read once.</summary>
    [Fact]
    public void AdversarialPaintSource_DrawingTextThroughAForm_IsRefused()
    {
        byte[] pdf = Pdf("/Pattern cs /P0 scn 0 0 200 200 re f", "/Pattern << /P0 6 0 R >>", "", "",
            S("/X0 Do /X0 Do", "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << /XObject << /X0 7 0 R >> >>"),
            S(TextInForm, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"));

        Assert.Throws<PdfRedactionException>(() => Redact(pdf));
    }

    /// <summary>The control: a pattern and a soft mask that draw no text are painted through as ever, and the operator stays.</summary>
    [Theory]
    [InlineData("/Pattern cs /P0 scn 0 0 200 200 re f", "/Pattern << /P0 6 0 R >>", "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100", "/P0 scn")]
    [InlineData("/G0 gs 0 0 200 200 re f", "/ExtGState << /G0 << /SMask << /S /Luminosity /G 6 0 R >> >> >>", "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency >>", "/G0 gs")]
    public void APaintSourceDrawingNoText_IsPaintedThrough(string content, string resources, string dict, string kept)
    {
        byte[] pdf = Pdf(content, resources, "", "", S("0 0 10 10 re f", dict));

        Assert.Contains(kept, Everything(Redact(pdf, 150, 150, 10, 10)), StringComparison.Ordinal);
    }

    /// <summary>The controls: what a page needs is written back as it was.</summary>
    [Fact]
    public void WhatARedactedPageNeeds_IsWrittenBack()
    {
        byte[] pdf = Pdf(
            "/P << /MCID 7 /Lang (he) >> BDC q 2 w [3 1] 0 d 1 0 0 rg 0 0 10 10 re S Q EMC /OC /Oc0 BDC EMC " + Visible,
            "/Properties << /Oc0 << /Type /OCG /Name (Layer) >> >>");

        string output = Everything(Redact(pdf, 150, 150, 10, 10));

        Assert.Contains("/MCID 7", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/Lang", output, StringComparison.Ordinal);
        Assert.Contains("[3 1] 0 d", output, StringComparison.Ordinal);
        Assert.Contains("1 0 0 rg", output, StringComparison.Ordinal);
        Assert.Contains("/OC /Oc0 BDC", output, StringComparison.Ordinal);
        Assert.Contains("/Name (Layer)", output, StringComparison.Ordinal);
        Assert.Contains("(visible) Tj", output, StringComparison.Ordinal);
    }

    /// <summary>A form the content never draws stands in no region: a limit the engine's summary states, pinned here.</summary>
    [Fact]
    public void AFormTheContentNeverDraws_StaysAsTheLimitsSay()
    {
        byte[] pdf = Pdf(Visible, "/XObject << /X0 6 0 R >>", "", "", S(TextInForm, "/Type /XObject /Subtype /Form /BBox [0 0 200 200]"));

        Assert.Contains(Marker, Everything(Redact(pdf)), StringComparison.Ordinal);
    }
}
