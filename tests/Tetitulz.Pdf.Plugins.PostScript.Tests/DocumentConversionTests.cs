// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Whole-document conversion (S4): <c>.ps</c> / <c>.eps</c> in, a real PDF out. Every assertion
/// goes through <c>PdfDocument.Open</c> on the produced bytes, so a file that is not a valid PDF
/// fails rather than passing on a byte count.
/// </summary>
public sealed class DocumentConversionTests
{
    private static (byte[] Pdf, PostScriptDocumentResult Result) Convert(
        string postScript, PostScriptConversionOptions? options = null)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(postScript));
        using var output = new MemoryStream();
        var result = PostScriptConversion.ConvertDocument(source, output, options);
        return (output.ToArray(), result);
    }

    private static PdfDocument Open(byte[] pdf)
    {
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.NotNull(doc);
        return doc;
    }

    /// <summary>The first page's /MediaBox as (width, height).</summary>
    private static (double Width, double Height) MediaBoxSize(PdfDocument doc)
    {
        var box = doc.Pages[0].MediaBox;
        Assert.NotNull(box);
        Assert.True(box!.Count >= 4);

        double N(int i) => ((Core.Objects.PdfNumber)box[i]).DoubleValue;
        return (N(2) - N(0), N(3) - N(1));
    }

    private const string Square =
        "0 0 1 setrgbcolor newpath 10 10 moveto 90 10 lineto 90 90 lineto 10 90 lineto " +
        "closepath fill";

    // ==================================================================
    //  Pages
    // ==================================================================

    /// <summary>Every <c>showpage</c> ends a page; the trailing marks make the last one.</summary>
    [Fact]
    public void ShowPage_StartsANewPage()
    {
        var (pdf, result) = Convert(Square + " showpage " + Square + " showpage " + Square);

        Assert.Equal(3, result.PageCount);
        using var doc = Open(pdf);
        Assert.Equal(3, doc.Pages.Count);
    }

    /// <summary>
    /// A document that draws nothing and shows no page is one blank page, said in
    /// <c>BlankPageOnly</c>; the controls — a page shown with nothing on it, and a drawn page — are not.
    /// </summary>
    [Fact]
    public void ADocumentThatDrawsNothing_IsSaidToBeABlankPageOnly()
    {
        var (pdf, result) = Convert("%!PS-Adobe-3.0\n%%EOF\n");
        Assert.True(result.BlankPageOnly);
        using (var doc = Open(pdf)) Assert.Single(doc.Pages);

        Assert.False(Convert("%!PS-Adobe-3.0\nshowpage\n").Result.BlankPageOnly);
        Assert.False(Convert(Square).Result.BlankPageOnly);
    }

    /// <summary>
    /// A conversion that fails — here the output refuses its first write — still writes a readable
    /// PDF, a blank page in the document's place, says so in <c>BlankPageOnly</c>, and is not clean.
    /// </summary>
    [Fact]
    public void AFailedConversion_IsSaidToBeABlankPageOnly()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(Square + " showpage"));
        using var output = new FirstWriteFails();
        var result = PostScriptConversion.ConvertDocument(source, output);

        Assert.True(result.BlankPageOnly);
        Assert.False(result.IsClean);
        using var doc = Open(output.ToArray());
        Assert.Single(doc.Pages);
    }

    /// <summary>A memory stream whose first write throws, as a full disk would.</summary>
    private sealed class FirstWriteFails : MemoryStream
    {
        private bool _failed;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_failed) { _failed = true; throw new IOException("the disk is full"); }
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!_failed) { _failed = true; throw new IOException("the disk is full"); }
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            if (!_failed) { _failed = true; throw new IOException("the disk is full"); }
            base.WriteByte(value);
        }
    }

    [Fact]
    public void DocumentWithoutShowPage_StillProducesOnePage()
    {
        var (pdf, result) = Convert(Square);

        Assert.Equal(1, result.PageCount);
        using var doc = Open(pdf);
        Assert.Single(doc.Pages);
    }

    /// <summary>A source that draws nothing must still yield a PDF a caller can open.</summary>
    [Fact]
    public void DocumentThatDrawsNothing_StillProducesAValidSinglePagePdf()
    {
        var (pdf, result) = Convert("1 2 add pop");

        Assert.Equal(1, result.PageCount);
        using var doc = Open(pdf);
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// The VM survives a page break — PostScript preserves <c>userdict</c> across pages, so a
    /// prologue's procedures are still callable on page 2. Resetting between pages would break
    /// essentially every multi-page document.
    /// </summary>
    [Fact]
    public void ShowPage_PreservesTheVmSoPrologueProceduresSurvive()
    {
        var (pdf, result) = Convert(
            "/box { newpath 10 10 moveto 90 10 lineto 90 90 lineto closepath fill } def " +
            "/blue { 0 0 1 setrgbcolor } def " +
            "blue box showpage " +
            "blue box");   // page 2 calls the same procedures

        Assert.Equal(2, result.PageCount);
        Assert.DoesNotContain("Undefined", result.LastError ?? "");
        using var doc = Open(pdf);
        Assert.Equal(2, doc.Pages.Count);
    }

    /// <summary>A definition made on page 1 is still defined on page 2.</summary>
    [Fact]
    public void ShowPage_PreservesDefinitionsMadeBeforeIt()
    {
        var (_, result) = Convert("/x 5 def showpage x 10 mul 0 moveto 0 0 lineto stroke");

        Assert.DoesNotContain("Undefined", result.LastError ?? "");
        Assert.Equal(2, result.PageCount);
    }

    // ==================================================================
    //  Bounding box
    // ==================================================================

    [Fact]
    public void BoundingBox_IsTakenFromTheDscComment()
    {
        var (_, result) = Convert("%%BoundingBox: 10 20 110 220\n" + Square);

        Assert.NotNull(result.DeclaredBoundingBox);
        Assert.Equal(new double[] { 10, 20, 110, 220 }, result.DeclaredBoundingBox);
    }

    /// <summary>
    /// The boxes of an included document (<c>%%BeginDocument</c> … <c>%%EndDocument</c>) are the
    /// included figure's: the document's own box stands, and a box after the inclusion is read again.
    /// </summary>
    [Fact]
    public void BoundingBox_OfAnIncludedDocument_IsNotTheDocuments()
    {
        var (_, result) = Convert("%%BoundingBox: 10 20 110 220\n%%BeginDocument: fig.eps\n" +
                                  "%%BoundingBox: 0 0 5 5\n%%HiResBoundingBox: 0 0 5 5\n%%EndDocument\n" + Square);
        var (_, after) = Convert("%%BeginDocument: fig.eps\n%%BoundingBox: 0 0 5 5\n%%EndDocument\n" +
                                 "%%BoundingBox: 10 20 110 220\n" + Square);

        Assert.Equal(new double[] { 10, 20, 110, 220 }, result.DeclaredBoundingBox);
        Assert.Equal(new double[] { 10, 20, 110, 220 }, after.DeclaredBoundingBox);
    }

    /// <summary>
    /// <c>%%HiResBoundingBox</c> wins over the integer one: it is strictly more precise, and the
    /// integer form is required to be its outward rounding.
    /// </summary>
    [Fact]
    public void HiResBoundingBox_WinsOverBoundingBox()
    {
        var (_, result) = Convert(
            "%%BoundingBox: 10 20 110 220\n%%HiResBoundingBox: 10.5 20.25 110.75 220.5\n" + Square);

        Assert.Equal(new[] { 10.5, 20.25, 110.75, 220.5 }, result.DeclaredBoundingBox);
    }

    [Fact]
    public void PageSize_FollowsTheDeclaredBoundingBox()
    {
        var (pdf, _) = Convert("%%BoundingBox: 0 0 200 100\n" + Square);

        using var doc = Open(pdf);
        var (w, h) = MediaBoxSize(doc);
        Assert.Equal(200, w, 1);
        Assert.Equal(100, h, 1);
    }

    /// <summary>
    /// With no declared box the page is sized to what was actually drawn, rather than defaulting to
    /// Letter and cropping.
    /// </summary>
    [Fact]
    public void PageSize_WithNoDeclaredBox_FollowsTheDrawnExtent()
    {
        // Fills 10,10 to 90,90 → an 80x80 extent.
        var (pdf, _) = Convert(Square);

        using var doc = Open(pdf);
        var (w, h) = MediaBoxSize(doc);
        Assert.Equal(80, w, 1);
        Assert.Equal(80, h, 1);
    }

    [Fact]
    public void Eps_IsDetectedFromTheMagicLine()
    {
        var (_, result) = Convert("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 10 10\n" + Square);
        Assert.True(result.IsEps);

        var (_, plain) = Convert("%!PS-Adobe-3.0\n" + Square);
        Assert.False(plain.IsEps);
    }

    // ==================================================================
    //  Resources must survive into the written file
    // ==================================================================

    /// <summary>
    /// Text in a converted document keeps its font resource.
    ///
    /// <para>This is a regression test for a real defect: the writer originally built pages through
    /// <c>PdfDocumentBuilder</c>, which derives <c>/Resources</c> from what its own content-stream
    /// API registered — so raw-appended content got an <b>empty</b> resource dictionary and every
    /// <c>/F1 1 Tf</c> referenced a font that was not there. Nothing about the emitted content
    /// stream looked wrong; only the written file was.</para>
    /// </summary>
    [Fact]
    public void Text_KeepsItsFontResourceInTheWrittenPdf()
    {
        var (pdf, _) = Convert("/Helvetica findfont 24 scalefont setfont " +
                               "newpath 20 40 moveto (Hello) show");

        using var doc = Open(pdf);
        var page = doc.Pages[0];

        // The content really shows text...
        string content = Encoding.ASCII.GetString(page.GetContentStreamData() ?? Array.Empty<byte>());
        Assert.Contains("Tj", content);
        Assert.Contains("/F1", content);

        // ...and the font it names is reachable from the page.
        var resources = ResolveDict(doc, page.Dictionary?[Core.Objects.PdfName.Resources]);
        Assert.NotNull(resources);
        var fonts = ResolveDict(doc, resources![Core.Objects.PdfName.Font]);
        Assert.NotNull(fonts);

        var font = ResolveDict(doc, fonts![new Core.Objects.PdfName("F1")]);
        Assert.NotNull(font);
        Assert.Equal("Helvetica",
            ((Core.Objects.PdfName)font![Core.Objects.PdfName.BaseFont]).Value);
    }

    /// <summary>An image likewise reaches the file, as a proper indirect stream.</summary>
    [Fact]
    public void Image_KeepsItsXObjectResourceInTheWrittenPdf()
    {
        var (pdf, _) = Convert("gsave 60 60 scale 1 2 8 [ 1 0 0 -2 0 2 ] <00FF> image grestore");

        using var doc = Open(pdf);
        var page = doc.Pages[0];

        string content = Encoding.ASCII.GetString(page.GetContentStreamData() ?? Array.Empty<byte>());
        Assert.Contains("/Im1", content);
        Assert.Contains("Do", content);

        var resources = ResolveDict(doc, page.Dictionary?[Core.Objects.PdfName.Resources]);
        var xobjects = ResolveDict(doc, resources?[Core.Objects.PdfName.XObject]);
        Assert.NotNull(xobjects);

        var image = doc.ResolveObject(xobjects![new Core.Objects.PdfName("Im1")])
                    as Core.Objects.PdfStream;
        Assert.NotNull(image);
        Assert.Equal(1, ((Core.Objects.PdfNumber)image![Core.Objects.PdfName.Width]).LongValue);
        Assert.Equal(2, ((Core.Objects.PdfNumber)image[Core.Objects.PdfName.Height]).LongValue);

        // A stream in a written file must be indirect and must declare its length.
        Assert.NotNull(image[Core.Objects.PdfName.Length]);
        Assert.Equal(new byte[] { 0x00, 0xFF }, image.GetDecodedData());
    }

    /// <summary>Resources reach every page, not only the one that first used them.</summary>
    [Fact]
    public void Resources_ReachEveryPage()
    {
        var (pdf, result) = Convert("/Helvetica findfont 12 scalefont setfont " +
                                    "newpath 0 0 moveto (one) show showpage " +
                                    "newpath 0 0 moveto (two) show");

        Assert.Equal(2, result.PageCount);
        using var doc = Open(pdf);

        foreach (var page in doc.Pages)
        {
            var resources = ResolveDict(doc, page.Dictionary?[Core.Objects.PdfName.Resources]);
            var fonts = ResolveDict(doc, resources?[Core.Objects.PdfName.Font]);
            Assert.NotNull(fonts);
        }
    }

    private static Core.Objects.PdfDictionary? ResolveDict(PdfDocument doc, Core.Objects.PdfObject? o)
        => o == null ? null : doc.ResolveObject(o) as Core.Objects.PdfDictionary;

    // ==================================================================
    //  Import plugins
    // ==================================================================

    [Fact]
    public void PostScriptImportPlugin_DeclaresThePsFormatKey()
    {
        var plugin = new PostScriptImportPlugin();

        Assert.Equal("ps", plugin.FormatKey);
        Assert.Equal("application/postscript", plugin.SourceMimeType);
        Assert.Equal("Tetitulz.Plugins.PostScriptImport", plugin.Name);
    }

    [Fact]
    public void EpsImportPlugin_DeclaresTheEpsFormatKey()
    {
        var plugin = new EpsImportPlugin();

        Assert.Equal("eps", plugin.FormatKey);
        Assert.Equal("Tetitulz.Plugins.EpsImport", plugin.Name);
    }

    /// <summary>
    /// The two plugins must have distinct names, or <c>PdfPluginHost.Register</c> rejects the second
    /// as a duplicate and the format it serves becomes unreachable.
    /// </summary>
    [Fact]
    public void TheTwoImportPlugins_HaveDistinctNamesAndKeys()
    {
        var ps = new PostScriptImportPlugin();
        var eps = new EpsImportPlugin();

        Assert.NotEqual(ps.Name, eps.Name);
        Assert.NotEqual(ps.FormatKey, eps.FormatKey);
    }

    [Fact]
    public void ImportPlugin_ProducesAnOpenablePdf()
    {
        var plugin = new EpsImportPlugin();

        using var source = new MemoryStream(Encoding.ASCII.GetBytes(
            "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 100 100\n" + Square));
        using var output = new MemoryStream();

        plugin.Import(source, output, new Core.Plugins.ImportOptions());

        using var doc = Open(output.ToArray());
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// The plugin must not mutate a shared options instance — <c>ConvertDocument</c> forces
    /// <c>NewPage</c>, and leaking that into a caller's object would change the behaviour of a
    /// concurrent fragment conversion.
    /// </summary>
    [Fact]
    public void ImportPlugin_DoesNotMutateTheCallersOptions()
    {
        var options = new PostScriptConversionOptions { ShowPage = ShowPageBehavior.ResetOnly };
        var plugin = new PostScriptImportPlugin(options);

        using var source = new MemoryStream(Encoding.ASCII.GetBytes(Square));
        using var output = new MemoryStream();
        plugin.Import(source, output, new Core.Plugins.ImportOptions());

        Assert.Equal(ShowPageBehavior.ResetOnly, options.ShowPage);
    }

    // ==================================================================
    //  Adversarial
    // ==================================================================

    /// <summary><c>{ showpage } loop</c> is a page bomb; the cap has to stop it.</summary>
    [Fact]
    public void PageBomb_IsBoundedByTheMaxPagesCap()
    {
        var (pdf, result) = Convert("{ showpage } loop",
            new PostScriptConversionOptions { MaxTotalOps = 2_000_000 });

        Assert.True(result.PageCount <= PsLimits.MaxPages,
            $"{result.PageCount} pages were produced, past the {PsLimits.MaxPages} cap.");

        using var doc = Open(pdf);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void MalformedInput_StillProducesAnOpenablePdf()
    {
        var (pdf, result) = Convert("] >> } ) nosuchoperator 1 2 add");

        Assert.True(result.PageCount >= 1);
        using var doc = Open(pdf);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void GarbageBinaryInput_DoesNotThrow()
    {
        var bytes = new byte[8192];
        int state = 999;
        for (int i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 1103515245 + 12345);
            bytes[i] = (byte)(state >> 16);
        }

        using var source = new MemoryStream(bytes);
        using var output = new MemoryStream();

        var ex = Record.Exception(() => PostScriptConversion.ConvertDocument(source, output));

        Assert.Null(ex);
    }

    /// <summary>
    /// The converted <b>content</b> is reproducible run to run, which is what lets a caller diff two
    /// conversions and what lets these tests assert on output at all.
    ///
    /// <para>The PDF <i>file</i> is deliberately not byte-identical: <c>PdfDocumentBuilder</c> gives
    /// every build a fresh cryptographically random trailer <c>/ID</c>, because §14.4 defines that
    /// as a unique file identifier. So the comparison is of the page content streams, with the
    /// generated identifier excluded — asserting on the whole file would be asserting that Core has
    /// a bug.</para>
    /// </summary>
    [Fact]
    public void DocumentConversion_ProducesReproducibleContent()
    {
        const string source = "%%BoundingBox: 0 0 100 100\n" + Square + " showpage " + Square;

        var (first, _) = Convert(source);
        var (second, _) = Convert(source);

        Assert.Equal(PageContents(first), PageContents(second));
    }

    /// <summary>
    /// The font survives <c>showpage</c>. PLRM's <c>initgraphics</c> resets the transform, path,
    /// clip, colour and line parameters and leaves the font alone, so a prologue that sets the font
    /// once draws every page in it. The fresh graphics state a page break built carried no font, so
    /// page 2 drew in Helvetica at size 12 and reported that no font was set.
    /// </summary>
    [Fact]
    public void ShowPage_KeepsTheCurrentFont()
    {
        using var collector = TestInfrastructure.DiagnosticCollector.Start();
        var (pdf, result) = Convert("/Times-Bold findfont 24 scalefont setfont " +
                                    "newpath 20 40 moveto (one) show showpage " +
                                    "newpath 20 40 moveto (two) show");

        Assert.Equal(2, result.PageCount);
        Assert.False(collector.Any("no font set"));

        using var doc = Open(pdf);
        var page = doc.Pages[1];
        byte[] content = page.GetContentStreamData() ?? Array.Empty<byte>();

        double[] tm = TestInfrastructure.ContentStreamAssert.FirstOperandsOf(content, "Tm");
        Assert.Equal(24.0, tm[0], 6);
        Assert.Equal(24.0, tm[3], 6);

        var tf = TestInfrastructure.ContentStreamAssert.Parse(content).First(o => o.Operator == "Tf");
        var resources = ResolveDict(doc, page.Dictionary?[Core.Objects.PdfName.Resources]);
        var fonts = ResolveDict(doc, resources![Core.Objects.PdfName.Font]);
        var font = ResolveDict(doc, fonts![new Core.Objects.PdfName(tf.Operands[0].TrimStart('/'))]);
        Assert.Equal("Times-Bold",
            ((Core.Objects.PdfName)font![Core.Objects.PdfName.BaseFont]).Value);
    }

    /// <summary>Every page's decoded content stream, concatenated.</summary>
    private static string PageContents(byte[] pdf)
    {
        using var doc = Open(pdf);
        var sb = new StringBuilder();
        foreach (var page in doc.Pages)
            sb.Append(Encoding.ASCII.GetString(page.GetContentStreamData() ?? Array.Empty<byte>()));
        return sb.ToString();
    }
}
