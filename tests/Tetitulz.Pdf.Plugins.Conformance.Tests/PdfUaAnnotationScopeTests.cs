// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfUaAnnotationScope"/>: which annotations PDF/UA treats as content a reader is
/// presented with — the one predicate UA27, the deep checker and the auto-tagger share — and the
/// checks that read it. ISO 14289-1 §7.18.1: "The requirements of this clause shall not apply to
/// annotations whose hidden flag is set or whose rectangle is outside the CropBox or whose Subtype is
/// Popup", and "Any annotation that does not have a Contents key in its dictionary shall have an
/// alternative description" — the enclosing structure element's /Alt — for UA27 and, through a
/// widget, UA08. The conformance corpus 7.18.1-t02-pass-a to -h and -t03-pass-b to -d, each of which the
/// checker failed; and UA07's empty /ActualText (7.3-t01-pass-c).
/// </summary>
public class PdfUaAnnotationScopeTests
{
    [Theory]
    [InlineData(PdfAnnotationSubtypes.Link, null, true)]
    [InlineData(PdfAnnotationSubtypes.Square, 0, true)]
    [InlineData(PdfAnnotationSubtypes.Widget, null, true)]      // UA27 narrows this itself; the scope does not
    [InlineData(PdfAnnotationSubtypes.Popup, null, false)]
    [InlineData(PdfAnnotationSubtypes.PrinterMark, null, false)]
    [InlineData(PdfAnnotationSubtypes.Link, PdfAnnotationFlags.Hidden, false)]
    [InlineData(PdfAnnotationSubtypes.Link, PdfAnnotationFlags.NoView, false)]
    [InlineData(PdfAnnotationSubtypes.Link, PdfAnnotationFlags.Print, true)]
    [InlineData(null, null, true)]                               // no /Subtype is not an exemption
    public void ReaderContentIsEverythingButPopupsPrinterMarksAndHidden(string subtype, int? flags, bool presented)
    {
        Assert.Equal(presented, PdfUaAnnotationScope.IsPresentedToReader(subtype, flags));
    }

    /// <summary>
    /// A <c>/Rect</c> may hold indirect numbers: read through the resolver they place the annotation,
    /// read raw they are no numbers and the exemption cannot be read, so the annotation stays in scope.
    /// </summary>
    [Fact]
    public void IndirectRectElements_AreReadThroughTheResolver()
    {
        var objects = new Dictionary<int, PdfObject>
        {
            [9] = new PdfNumber(2000), [10] = new PdfNumber(2000), [11] = new PdfNumber(2010), [12] = new PdfNumber(2010),
        };
        var rect = new PdfArray(new PdfObject[] { new PdfReference(9, 0), new PdfReference(10, 0), new PdfReference(11, 0), new PdfReference(12, 0) });
        PdfObject Resolve(PdfObject o) => o is PdfReference r && objects.TryGetValue(r.ReferenceObjectNumber, out var v) ? v : o;

        Assert.False(PdfUaAnnotationScope.IsPresentedToReader(PdfAnnotationSubtypes.Link, null, rect, Box("0 0 612 792"), Resolve));
        Assert.True(PdfUaAnnotationScope.IsPresentedToReader(PdfAnnotationSubtypes.Link, null, rect, Box("0 0 612 792")));
        Assert.True(PdfUaAnnotationScope.IsPresentedToReader(PdfAnnotationSubtypes.Link, null, Box("0 0 10 10"), Box("0 0 612 792"), Resolve));
    }

    /// <summary>The four-argument form adds the CropBox to the same answer.</summary>
    [Theory]
    [InlineData(PdfAnnotationSubtypes.Link, null, "0 0 10 10", true)]
    [InlineData(PdfAnnotationSubtypes.Link, null, "700 0 710 10", false)]
    [InlineData(PdfAnnotationSubtypes.Popup, null, "0 0 10 10", false)]
    public void TheCropBoxNarrowsTheSameScope(string subtype, int? flags, string rect, bool presented)
    {
        Assert.Equal(presented, PdfUaAnnotationScope.IsPresentedToReader(subtype, flags, Box(rect), Box("0 0 612 792")));
    }

    [Theory]
    [InlineData("0 0 10 10", "0 0 612 792", false)]
    [InlineData("700 0 710 10", "0 0 612 792", true)]      // right of the box
    [InlineData("0 800 10 810", "0 0 612 792", true)]      // above it
    [InlineData("-20 -20 -10 -10", "0 0 612 792", true)]
    [InlineData("612 0 620 10", "0 0 612 792", false)]     // touching the edge is inside
    [InlineData("10 10 0 0", "612 792 0 0", false)]        // corners in either order
    [InlineData("5 5 5 5", "0 0 612 792", false)]          // a zero-size rectangle within the page
    public void IsOutsideCropBox_IsWhollyOutside(string rect, string box, bool outside)
    {
        Assert.Equal(outside, PdfUaAnnotationScope.IsOutsideCropBox(Box(rect), Box(box)));
    }

    /// <summary>An array that is not four numbers keeps the annotation in scope.</summary>
    [Fact]
    public void IsOutsideCropBox_UnreadableBoxesKeepTheAnnotationInScope()
    {
        Assert.False(PdfUaAnnotationScope.IsOutsideCropBox(null, Box("0 0 612 792")));
        Assert.False(PdfUaAnnotationScope.IsOutsideCropBox(new PdfArray(new PdfObject[] { new PdfName("x"), new PdfNumber(0), new PdfNumber(1), new PdfNumber(1) }), Box("0 0 1 1")));
        Assert.True(PdfUaAnnotationScope.IsOutsideCropBox(Box("5 5 6 6"), Box("0 0 1 1")));   // the control
    }

    /// <summary>UA27: a stamp outside the CropBox, hidden, or described by its element's /Alt needs no /Contents.</summary>
    [Theory]
    [InlineData("/Rect [30 30 60 60] /F 4", "", true)]
    [InlineData("/Rect [700 30 760 60] /F 4", "", false)]
    [InlineData("/Rect [30 30 60 60] /F 6", "", false)]
    [InlineData("/Rect [30 30 60 60] /F 4", "/Alt (A stamp)", false)]
    [InlineData("/Rect [30 30 60 60] /F 4", "/Alt ()", true)]
    [InlineData("/Rect [30 30 60 60] /F 4 /Contents (Approved)", "", false)]
    public void Ua27_AnAnnotationOutOfScopeOrDescribedByItsElementNeedsNoContents(string annotEntries, string elementEntries, bool reportsUa27)
    {
        byte[] pdf = Document(elementEntries, "/Annots [7 0 R]",
            "<< /Type /Annot /Subtype /Stamp " + annotEntries + " /StructParent 1 >>");
        Assert.Equal(reportsUa27, Validate(pdf).Violations.Any(v => v.Code == "UA27"));
    }

    /// <summary>UA08: a field with no /TU whose widget is outside the CropBox, hidden, or described by its element's /Alt.</summary>
    [Theory]
    [InlineData("/Rect [0 0 50 10] /F 4", "", true)]
    [InlineData("/Rect [700 0 750 10] /F 4", "", false)]
    [InlineData("/Rect [0 0 50 10] /F 6", "", false)]
    [InlineData("/Rect [0 0 50 10] /F 4", "/Alt (Your name)", false)]
    [InlineData("/Rect [0 0 50 10] /F 4 /TU (Your name)", "", false)]
    public void Ua08_AWidgetOutOfScopeOrDescribedByItsElementNeedsNoTooltip(string widgetEntries, string elementEntries, bool reportsUa08)
    {
        byte[] pdf = Document(elementEntries, "/Annots [7 0 R]",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) " + widgetEntries + " /StructParent 1 >>",
            catalogEntries: "/AcroForm << /Fields [7 0 R] >>");
        Assert.Equal(reportsUa08, Validate(pdf).Violations.Any(v => v.Code == "UA08"));
    }

    /// <summary>UA08 still judges a field none of whose widgets is on a page: no /TU, reported.</summary>
    [Fact]
    public void Ua08_AFieldWithNoWidgetOnAPageKeepsTheWalksVerdict()
    {
        byte[] pdf = Document("", "", "<< /FT /Tx /T (orphan) >>", catalogEntries: "/AcroForm << /Fields [7 0 R] >>");
        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA08");
    }

    /// <summary>UA07: an /ActualText is replacement text even when empty; an empty /Alt describes nothing.</summary>
    [Theory]
    [InlineData("/ActualText ()", false)]
    [InlineData("/Alt ()", true)]
    [InlineData("/Alt (A chart)", false)]
    [InlineData("", true)]
    public void Ua07_AnEmptyActualTextCountsAndAnEmptyAltDoesNot(string entries, bool reportsUa07)
    {
        byte[] pdf = Document(entries, "", "<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] /F 2 >>", role: "Figure");
        Assert.Equal(reportsUa07, Validate(pdf).Violations.Any(v => v.Code == "UA07"));
    }

    // ---------------------------------------------------------------- fixtures

    private static PdfUaValidationResult Validate(byte[] pdf) => PdfUaChecker.Validate(new MemoryStream(pdf));

    private static PdfArray Box(string numbers)
        => new PdfArray(numbers.Split(' ').Select(n => (PdfObject)new PdfNumber(int.Parse(n, CultureInfo.InvariantCulture))).ToArray());

    /// <summary>
    /// Catalog 1 (with a /Lang), pages 2, page 3 (612 × 792), an empty content stream 4, structure
    /// root 5, an element 6 of <paramref name="role"/> whose kid is an /OBJR to object 7.
    /// </summary>
    private static byte[] Document(string elementEntries, string pageEntries, string seventh,
        string catalogEntries = "", string role = "Annot")
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot 5 0 R " + catalogEntries + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Tabs /S " + pageEntries + " >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /" + role + " /P 5 0 R /Pg 3 0 R /K [<< /Type /OBJR /Obj 7 0 R >>] " + elementEntries + " >>",
            seventh,
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
