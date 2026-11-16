// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// XFDF's <c>head</c>, <c>tail</c> and <c>intent</c> attributes are an annotation's <c>/LE</c> and <c>/IT</c>
/// (XFDF 2.0, the line, polygon/polyline and freetext attribute tables; an independent implementation reads the same names):
/// read into the carried entries an FDF exchange keeps, and written from them. Until 2026-10-22 an XFDF
/// export left both out though an FDF export carried them.
/// </summary>
public class XfdfEndingsAndIntentTests
{
    private static FormDataAnnotation Read(string element, string attributes)
        => PdfFormDataExchange.ReadXfdf(new MemoryStream(Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>"
            + $"<{element} page=\"0\" rect=\"10,10,100,100\" {attributes}/></annots></xfdf>"))).Annotations.Single();

    private static XElement Written(FormDataAnnotation annotation)
    {
        var data = new FormData();
        data.Annotations.Add(annotation);
        using var output = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(data, output);
        return XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()))
            .Descendants().Single(e => e.Attribute("page") != null);
    }

    private static string[] Names(PdfObject array) => ((PdfArray)array).Select(o => ((PdfName)o).Value).ToArray();

    /// <summary>A line's head and tail are its /LE, in order; its intent its /IT; and they are written back as read.</summary>
    [Fact]
    public void ALinesEndingsAndIntent_AreReadAndWrittenBack()
    {
        FormDataAnnotation a = Read("line", "start=\"10,10\" end=\"90,90\" head=\"OpenArrow\" tail=\"Circle\" intent=\"LineDimension\"");

        Assert.Equal(new[] { "OpenArrow", "Circle" }, Names(a.Carried["LE"]));
        Assert.Equal("LineDimension", ((PdfName)a.Carried["IT"]).Value);

        XElement written = Written(a);
        Assert.Equal("OpenArrow", (string)written.Attribute("head"));
        Assert.Equal("Circle", (string)written.Attribute("tail"));
        Assert.Equal("LineDimension", (string)written.Attribute("intent"));
    }

    /// <summary>An ending left out is None, the specification's default; a polyline takes both attributes too.</summary>
    [Fact]
    public void AnEndingLeftOut_IsNone_AndAPolylineHasEndings()
        => Assert.Equal(new[] { "None", "Square" }, Names(Read("polyline", "tail=\"Square\"").Carried["LE"]));

    /// <summary>
    /// The two dimension intents are named apart: XFDF's <c>polygon-dimension</c> and
    /// <c>polyline-dimension</c> are PDF's <c>PolygonDimension</c> and <c>PolyLineDimension</c>, both ways;
    /// <c>PolygonCloud</c> is the same in both.
    /// </summary>
    [Theory]
    [InlineData("polygon", "polygon-dimension", "PolygonDimension")]
    [InlineData("polyline", "polyline-dimension", "PolyLineDimension")]
    [InlineData("polygon", "PolygonCloud", "PolygonCloud")]
    [InlineData("freetext", "FreeTextCallout", "FreeTextCallout")]
    public void AnIntent_IsItsPdfName_BothWays(string element, string xfdf, string pdf)
    {
        FormDataAnnotation a = Read(element, $"intent=\"{xfdf}\"");
        Assert.Equal(pdf, ((PdfName)a.Carried["IT"]).Value);
        Assert.Equal(xfdf, (string)Written(a).Attribute("intent"));
    }

    /// <summary>
    /// Only where the specification puts them: a square's endings and intent are not XFDF attributes, so
    /// none is read or written (the control: the same entries carried on a line are).
    /// </summary>
    [Fact]
    public void ASquare_HasNoEndingsOrIntentAttributes()
    {
        FormDataAnnotation square = Read("square", "head=\"OpenArrow\" intent=\"LineArrow\"");
        Assert.False(square.Carried.ContainsKey("LE"));
        Assert.False(square.Carried.ContainsKey("IT"));

        square.Carried["LE"] = new PdfArray { new PdfName("Square"), new PdfName("None") };
        square.Carried["IT"] = new PdfName("LineArrow");
        XElement written = Written(square);
        Assert.Null(written.Attribute("head"));
        Assert.Null(written.Attribute("intent"));
    }
}
