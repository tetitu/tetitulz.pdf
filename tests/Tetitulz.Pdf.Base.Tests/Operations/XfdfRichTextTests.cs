// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// XFDF's rich text, <c>&lt;contents-richtext&gt;</c>, is an annotation's <c>/RC</c>: read as its
/// inner XHTML, as an independent implementation reads it, and written back as
/// markup only when it is markup. The written file is parsed as XML, which is what another reader does.
/// </summary>
public class XfdfRichTextTests
{
    private const string Xhtml = "http://www.w3.org/1999/xhtml";

    private static string Xfdf(string annotationBody) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\" xml:space=\"preserve\"><annots>" +
        "<square page=\"0\" rect=\"10,10,100,100\" name=\"n1\">" + annotationBody + "</square>" +
        "</annots></xfdf>";

    private static FormDataAnnotation Read(string xfdf)
        => PdfFormDataExchange.ReadXfdf(new MemoryStream(Encoding.UTF8.GetBytes(xfdf))).Annotations.Single();

    private static XDocument Written(FormDataAnnotation annotation)
    {
        var data = new FormData();
        data.Annotations.Add(annotation);
        using var output = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(data, output);
        return XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void ContentsRichText_IsReadAsTheAnnotationsRichText_AndItsTextFillsAMissingContents()
    {
        FormDataAnnotation a = Read(Xfdf(
            "<contents-richtext><body xmlns=\"" + Xhtml + "\"><p>Fix <b>this</b> line</p></body></contents-richtext>"));

        string rc = ((PdfString)a.Carried["RC"]).GetText();
        Assert.Contains("<b>this</b>", rc, StringComparison.Ordinal);
        Assert.Contains(Xhtml, rc, StringComparison.Ordinal);
        Assert.Equal("Fix this line", a.Contents);
    }

    [Fact]
    public void APlainContentsBesideTheRichText_IsKept()
    {
        FormDataAnnotation a = Read(Xfdf(
            "<contents>plain</contents><contents-richtext><body xmlns=\"" + Xhtml + "\"><p>rich</p></body></contents-richtext>"));
        Assert.Equal("plain", a.Contents);
        Assert.True(a.Carried.ContainsKey("RC"));
    }

    [Fact]
    public void RichText_IsWrittenBackAsMarkup_InAFileThatParses()
    {
        FormDataAnnotation a = Read(Xfdf(
            "<contents-richtext><body xmlns=\"" + Xhtml + "\"><p>Fix <b>this</b></p></body></contents-richtext>"));

        XDocument written = Written(a);
        XElement rich = written.Descendants().Single(e => e.Name.LocalName == "contents-richtext");
        XElement body = rich.Elements().Single();
        Assert.Equal(XName.Get("body", Xhtml), body.Name);
        Assert.Equal("this", body.Descendants(XName.Get("b", Xhtml)).Single().Value);
    }

    /// <summary>
    /// An <c>/RC</c> that is not well-formed XML — which an FDF can carry — is left out rather than
    /// written raw; the file still parses and keeps the plain contents. The control is a well-formed one,
    /// which is written.
    /// </summary>
    [Fact]
    public void RichTextThatIsNotMarkup_IsLeftOut_AndTheFileStillParses()
    {
        var broken = new FormDataAnnotation(PdfAnnotationSubtypes.Square) { Contents = "plain" };
        broken.Carried["RC"] = new PdfString("<p>unclosed");
        XDocument written = Written(broken);
        Assert.DoesNotContain(written.Descendants(), e => e.Name.LocalName == "contents-richtext");
        Assert.Equal("plain", written.Descendants().Single(e => e.Name.LocalName == "contents").Value);

        var fine = new FormDataAnnotation(PdfAnnotationSubtypes.Square);
        fine.Carried["RC"] = new PdfString("<p>closed</p>");
        Assert.Single(Written(fine).Descendants(), e => e.Name.LocalName == "contents-richtext");
    }

    /// <summary>
    /// A compact file — no white space between elements — loses nothing: the <c>&lt;vertices&gt;</c> after
    /// a <c>&lt;contents&gt;</c>, and every <c>&lt;gesture&gt;</c> of an <c>&lt;inklist&gt;</c>. A reader
    /// stepping past whatever follows an element it has read would lose the vertices and every second
    /// stroke; an indented file, which is what this library writes, would hide it.
    /// </summary>
    [Fact]
    public void ACompactFile_LosesNoElementAfterOneThatWasRead()
    {
        string compact =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<polygon page=\"0\" rect=\"0,0,50,50\"><contents>note</contents><vertices>1,2,3,4,5,6</vertices></polygon>" +
            "<ink page=\"0\" rect=\"0,0,50,50\"><inklist><gesture>1,1;2,2</gesture><gesture>3,3;4,4</gesture>" +
            "<gesture>5,5;6,6</gesture></inklist></ink>" +
            "</annots></xfdf>";
        var annotations = PdfFormDataExchange.ReadXfdf(new MemoryStream(Encoding.UTF8.GetBytes(compact))).Annotations;

        FormDataAnnotation polygon = annotations.Single(x => x.Subtype == PdfAnnotationSubtypes.Polygon);
        Assert.Equal("note", polygon.Contents);
        Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6 }, polygon.Vertices);

        FormDataAnnotation ink = annotations.Single(x => x.Subtype == PdfAnnotationSubtypes.Ink);
        Assert.Equal(3, ink.InkList.Count);
        Assert.Equal(new double[] { 5, 5, 6, 6 }, ink.InkList[2]);
    }

    /// <summary>An FDF's <c>/RC</c> reaches an XFDF export: the two formats carry the same rich text.</summary>
    [Fact]
    public void AnFdfsRichText_IsWrittenToXfdf()
    {
        const string fdf =
            "%FDF-1.2\n1 0 obj\n<< /FDF << /Annots [2 0 R] >> >>\nendobj\n" +
            "2 0 obj\n<< /Type /Annot /Subtype /Square /Page 0 /Rect [10 10 100 100] " +
            "/RC (<body xmlns=\"http://www.w3.org/1999/xhtml\"><p>from fdf</p></body>) >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n";
        FormData data = PdfFormDataExchange.ReadFdf(new MemoryStream(Encoding.ASCII.GetBytes(fdf)));

        using var output = new MemoryStream();
        PdfFormDataExchange.WriteXfdf(data, output);
        XDocument written = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal("from fdf", written.Descendants(XName.Get("p", Xhtml)).Single().Value);
    }
}
