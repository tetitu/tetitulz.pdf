// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>execform</c> (PLRM §4.7) as a PDF form XObject, and <c>setpagedevice</c>'s <c>/PageSize</c> as
/// the page's medium (PLRM §6.1.1). Both were arity-table entries: a form drew nothing, and a page
/// device's size was lost to the drawn extent.
/// </summary>
public sealed class FormAndPageDeviceTests
{
    private const string Form =
        "/F << /FormType 1 /BBox [0 0 10 10] /Matrix [1 0 0 1 0 0]" +
        " /PaintProc { pop 0 0 moveto 10 0 lineto 10 10 lineto closepath fill } >> def ";

    private static PostScriptConversionResult Convert(string postScript)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(postScript));

    private static string[] Ops(byte[] content)
        => ContentStreamAssert.Parse(content).Select(o => o.ToString()).ToArray();

    private static PdfStream XObject(PostScriptConversionResult r, string name)
        => (PdfStream)((PdfDictionary)r.Resources[PdfName.XObject])[new PdfName(name)];

    /// <summary>A form is a form XObject drawn by <c>Do</c>: its box, its content from the PaintProc, built once however often drawn.</summary>
    [Fact]
    public void Execform_DrawsAFormXObject_BuiltOnce()
    {
        var r = Convert(Form + "F execform 50 50 translate F execform");
        string[] ops = Ops(r.Content);
        Assert.Equal(2, ops.Count(o => o == "/Fm1 Do"));
        PdfStream form = XObject(r, "Fm1");
        Assert.Equal("Form", ((PdfName)form[PdfName.Subtype]).Value);
        Assert.Equal(new[] { 0.0, 0, 10, 10 }, ((PdfArray)form[PdfName.BBox]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
        Assert.Null(form[PdfName.Matrix]);
        Assert.Contains("f", Ops(form.RawData));
        Assert.Single((PdfDictionary)r.Resources[PdfName.XObject]);
    }

    /// <summary>A form's <c>/Matrix</c> is written for the reader to apply, as PLRM applies it.</summary>
    [Fact]
    public void AFormsMatrix_IsWritten()
    {
        var r = Convert(Form.Replace("/Matrix [1 0 0 1 0 0]", "/Matrix [2 0 0 2 5 5]") + "F execform");
        Assert.Equal(new[] { 2.0, 0, 0, 2, 5, 5 },
            ((PdfArray)XObject(r, "Fm1")[PdfName.Matrix]).Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
    }

    /// <summary>
    /// A form's stream inherits the state it is drawn in, as PLRM runs the PaintProc in it: a form that
    /// sets no colour writes none, so each <c>Do</c> paints in the colour around it.
    /// </summary>
    [Fact]
    public void AFormThatSetsNoColour_WritesNone()
    {
        var r = Convert("1 0 0 setrgbcolor " + Form + "F execform");
        string[] content = Ops(XObject(r, "Fm1").RawData);
        Assert.Contains("f", content);
        Assert.DoesNotContain(content, o => o.EndsWith(" rg") || o.EndsWith(" g") || o.EndsWith(" k"));
        var coloured = Convert(Form.Replace("{ pop 0 0", "{ pop 0 0 1 setrgbcolor 0 0") + "F execform");
        Assert.Contains("0 0 1 rg", Ops(XObject(coloured, "Fm1").RawData));
    }

    /// <summary>
    /// What a form inherits is written before its <c>Do</c>: drawn red then blue, each <c>Do</c> follows
    /// its colour — this emitter writes colour only when something paints, and a <c>Do</c> did not
    /// count, so both came out black.
    /// </summary>
    [Fact]
    public void TheColourAFormInherits_IsWrittenBeforeItsDo()
    {
        string[] ops = Ops(Convert(Form + "1 0 0 setrgbcolor F execform 0 0 1 setrgbcolor F execform").Content);
        int red = System.Array.IndexOf(ops, "1 0 0 rg"), blue = System.Array.IndexOf(ops, "0 0 1 rg");
        int first = System.Array.IndexOf(ops, "/Fm1 Do"), second = System.Array.LastIndexOf(ops, "/Fm1 Do");
        Assert.True(red >= 0 && red < first && first < blue && blue < second, string.Join(" | ", ops));
    }

    /// <summary>The page's extent takes the form's box through its matrix and the CTM.</summary>
    [Fact]
    public void AFormsExtent_IsItsBoxPlaced()
    {
        var r = Convert(Form.Replace("/Matrix [1 0 0 1 0 0]", "/Matrix [2 0 0 2 0 0]") + "100 100 translate F execform");
        Assert.Equal(new[] { 100.0, 100, 120, 120 }, r.BoundingBox);
    }

    /// <summary>A malformed form is PLRM's error, which <c>stopped</c> catches; a well-formed one is the control.</summary>
    [Theory]
    [InlineData("/FormType 2", true)]
    [InlineData("/BBox [0 0 10]", true)]
    [InlineData("/PaintProc 5", true)]
    [InlineData("/Matrix 5", true)]
    [InlineData("", false)]
    public void AMalformedForm_IsAnError(string replacement, bool stopped)
    {
        string form = replacement.Length == 0 ? Form : Form.Replace(" >> def", " " + replacement + " >> def");
        var r = Convert(form + "{ F execform } stopped { 0 0 moveto 1 1 lineto stroke } if");
        Assert.Equal(stopped, Ops(r.Content).Contains("S"));
    }

    /// <summary><c>setpagedevice</c>'s <c>/PageSize</c> is the page's <c>/MediaBox</c>, from the origin, ahead of the drawn extent.</summary>
    [Fact]
    public void APageDevicesPageSize_IsTheMediaBox()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(
            "%!PS\n<< /PageSize [300 400] >> setpagedevice 10 10 moveto 20 20 lineto stroke showpage\n" +
            "100 100 moveto 120 120 lineto stroke showpage\n"));
        using var output = new MemoryStream();
        var result = PostScriptConversion.ConvertDocument(input, output);
        Assert.Equal(2, result.PageCount);
        var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        foreach (int page in new[] { 0, 1 })
        {
            double[] box = doc.Pages[page].MediaBox.Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray();
            Assert.Equal(new[] { 0.0, 0, 300, 400 }, box);
        }
    }

    /// <summary>A document whose last page has no <c>showpage</c> — every EPS — takes the medium too.</summary>
    [Fact]
    public void APageDevicesPageSize_ReachesALastPageWithNoShowpage()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(
            "%!PS\n<< /PageSize [300 400] >> setpagedevice 10 10 moveto 20 20 lineto stroke\n"));
        using var output = new MemoryStream();
        Assert.Equal(1, PostScriptConversion.ConvertDocument(input, output).PageCount);
        var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(new[] { 0.0, 0, 300, 400 }, doc.Pages[0].MediaBox.Cast<PdfNumber>().Select(n => n.DoubleValue).ToArray());
    }

    /// <summary><c>currentpagedevice</c> answers the <c>/PageSize</c> in force; <c>clippath</c> the same medium.</summary>
    [Fact]
    public void CurrentpagedeviceAndClippath_AnswerThePageSize()
    {
        var r = Convert("<< /PageSize [300 400] >> setpagedevice currentpagedevice /PageSize get aload pop" +
                        " /h exch def /w exch def clippath pathbbox /ury exch def /urx exch def pop pop" +
                        " w 300 eq h 400 eq and urx 300 eq and ury 400 eq and { 0 0 moveto 1 1 lineto stroke } if");
        Assert.Contains("S", Ops(r.Content));
    }

    /// <summary>A <c>/PageSize</c> that is not two positive numbers is PLRM's error.</summary>
    [Theory]
    [InlineData("[300]", true)]
    [InlineData("[0 400]", true)]
    [InlineData("[300 400]", false)]
    public void AMalformedPageSize_IsAnError(string size, bool stopped)
    {
        var r = Convert("{ << /PageSize " + size + " >> setpagedevice } stopped { 0 0 moveto 1 1 lineto stroke } if");
        Assert.Equal(stopped, Ops(r.Content).Contains("S"));
    }
}

/// <summary>The form counts, set through <see cref="PsLimits"/>.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class FormLimitTests
{
    /// <summary>
    /// Hostile: a PaintProc that draws its own form recurses; the cells nest no deeper than
    /// <c>MaxFormNestingDepth</c>, the conversion finishing with the outer form built.
    /// </summary>
    [Fact]
    public void HostileSelfDrawingForm_NestsNoDeeperThanTheCap()
    {
        try
        {
            PsLimits.MaxFormNestingDepth = 2;
            PostScriptDiagnostics.Clear();
            var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "/F << /FormType 1 /BBox [0 0 10 10] /Matrix [1 0 0 1 0 0] /PaintProc { pop /G << /FormType 1 /BBox [0 0 10 10]" +
                " /Matrix [1 0 0 1 0 0] /PaintProc { pop G execform } >> def G execform } >> def F execform"));
            Assert.Contains(ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()), o => o == "/Fm1 Do");
            Assert.Contains("nested past 2", PostScriptDiagnostics.LastError ?? "", System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }

    /// <summary>Hostile: a loop of fresh forms builds no more than <c>MaxForms</c>, and says so once.</summary>
    [Fact]
    public void HostileFormLoop_StopsAtTheCap()
    {
        try
        {
            PsLimits.MaxForms = 2;
            PostScriptDiagnostics.Clear();
            var r = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(
                "5 { << /FormType 1 /BBox [0 0 10 10] /Matrix [1 0 0 1 0 0] /PaintProc { pop 0 0 moveto 1 1 lineto stroke } >> execform } repeat"));
            Assert.Equal(2, ((PdfDictionary)r.Resources[PdfName.XObject]).Count);
            Assert.Equal(1, PostScriptDiagnostics.ReportCount);
            Assert.Contains("2-form cap", PostScriptDiagnostics.LastError, System.StringComparison.Ordinal);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
