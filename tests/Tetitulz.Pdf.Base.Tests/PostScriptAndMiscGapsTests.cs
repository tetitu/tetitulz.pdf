// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the 2026-04 PostScript / legacy-compat / misc-catalog gap closure:
///  • /Subtype /PS XObject placeholder (raster + SVG)
///  • Legacy inline "PS" content-stream operator — no-op, must not break parsing
///  • FreeText /RC (rich-text) plain-text strip fallback
///  • PdfViewerPreferences catalog parser
///
/// <para>These pin the <b>no-plugin</b> behaviour, which an <c>IPostScriptPlugin</c> now overrides
/// (see <c>Rendering/PostScriptPluginDispatchTests</c>). They register nothing themselves, so they
/// join <see cref="TestInfrastructure.PluginHostCollection"/> only to keep a plugin leaked by a
/// concurrently-running test file from making them flaky.</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class PostScriptAndMiscGapsTests
{
    // ==================================================================
    //  /Subtype /PS XObject — placeholder rendering
    // ==================================================================

    /// <summary>
    /// A /Subtype /PS XObject cannot be executed, so the renderer draws a hatched placeholder
    /// over its BBox rather than dropping the content silently. The BBox is [0 0 200 100]
    /// placed at (100,400), so the placeholder must ink that region and nothing outside it.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_DrawsAPlaceholderOverTheBBox()
    {
        using var ms = new MemoryStream(BuildPdfWithPsXObject());
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        bool IsInk(int x, int yUser)
        {
            var c = bmp!.GetPixel(x, bmp.Height - 1 - yUser);
            return c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240);
        }

        int inked = 0;
        for (int x = 105; x < 295; x += 3)
            for (int y = 405; y < 495; y += 3)
                if (IsInk(x, y)) inked++;
        Assert.True(inked > 0, "no placeholder was drawn for the PostScript XObject.");

        // Well outside the BBox the page must stay clean.
        Assert.False(IsInk(350, 450), "the placeholder painted outside the XObject BBox.");
        Assert.False(IsInk(200, 600), "the placeholder painted outside the XObject BBox.");
    }

    [Fact]
    public void PsXObject_Svg_EmitsPlaceholderMarker()
    {
        byte[] pdf = BuildPdfWithPsXObject();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("data-postscript-placeholder", svg);
    }

    // ==================================================================
    //  Legacy "PS" content-stream operator — must not break parsing
    // ==================================================================

    /// <summary>
    /// With no <c>IPostScriptPlugin</c> registered the legacy inline <c>(…) PS</c> operator is a
    /// no-op — but a <b>reported</b> one. "No-op" is asserted the only way it can be: the page is
    /// compared pixel-for-pixel against the identical content stream with the operator deleted, so
    /// the operator can neither paint anything of its own nor disturb the operators around it (a
    /// mis-parse that consumed the following operands would move the green square). The
    /// <c>PostScriptUnsupported</c> diagnostic is what stops "ignored" from meaning "silently lost".
    ///
    /// <para>The predecessor asserted only that a non-empty PNG came back, which a blank page
    /// satisfies.</para>
    /// </summary>
    [Fact]
    public void InlinePsOperator_Raster_IsIgnoredAndTheDropIsReported()
    {
        // `( some ps ) PS` — the string is an operand, PS is the operator. Red square at
        // (0,0)-(100,100) before it, green square at (100,100)-(200,200) after it.
        const string withPs =
            "1 0 0 rg 0 0 100 100 re f (ps-code-goes-here) PS 0 1 0 rg 100 100 100 100 re f";
        const string withoutPs =
            "1 0 0 rg 0 0 100 100 re f 0 1 0 rg 100 100 100 100 re f";

        // The "no IPostScriptPlugin is registered" report is one-shot per thread - a configuration
        // fact, not a property of this page, on the same terms as the JPEG 2000 no-codec report.
        // Re-armed so this measures THIS render rather than whatever a neighbour already consumed.
        Tetitulz.Pdf.Base.Rendering.PluginPostScriptTranslator.ResetNoPluginReporting();

        var kinds = new System.Collections.Generic.List<RenderDiagnosticKind>();
        void Sink(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            lock (kinds) kinds.Add(kind);
        }

        SKBitmap withBmp;
        RenderDiagnostics.OnEvent += Sink;
        try { withBmp = RenderToBitmap(BuildPdfMinimal(withPs)); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        using (withBmp)
        using (var withoutBmp = RenderToBitmap(BuildPdfMinimal(withoutPs)))
        {
            // The surrounding operators really did paint, so "identical" is not two blank pages.
            Assert.True(IsNear(PixelAt(withBmp, 50, 50), SKColors.Red),
                $"the fill before the PS operator is missing; got {PixelAt(withBmp, 50, 50)}");
            Assert.True(IsNear(PixelAt(withBmp, 150, 150), SKColors.Lime),
                $"the fill after the PS operator is missing; got {PixelAt(withBmp, 150, 150)}");

            Assert.Equal(withoutBmp.Width, withBmp.Width);
            Assert.Equal(withoutBmp.Height, withBmp.Height);
            int differing = 0;
            for (int y = 0; y < withBmp.Height; y++)
                for (int x = 0; x < withBmp.Width; x++)
                    if (withBmp.GetPixel(x, y) != withoutBmp.GetPixel(x, y)) differing++;
            Assert.Equal(0, differing);
        }

        lock (kinds)
            Assert.Contains(RenderDiagnosticKind.PostScriptUnsupported, kinds);
    }

    private static SKBitmap RenderToBitmap(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>Pixel at a PDF user-space point (origin bottom-left), 1 px per point.</summary>
    private static SKColor PixelAt(SKBitmap bmp, int userX, int userY)
        => bmp.GetPixel(userX, bmp.Height - 1 - userY);

    private static bool IsNear(SKColor a, SKColor b, int tolerance = 12)
        => System.Math.Abs(a.Red   - b.Red)   <= tolerance
        && System.Math.Abs(a.Green - b.Green) <= tolerance
        && System.Math.Abs(a.Blue  - b.Blue)  <= tolerance;

    [Fact]
    public void InlinePsOperator_Svg_DoesNotThrow()
    {
        const string cs = "(pscmd) PS 1 0 0 rg 50 50 200 200 re f";
        byte[] pdf = BuildPdfMinimal(cs);
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
            Assert.Contains("<path", svg);
        });
        Assert.Null(ex);
    }

    // ==================================================================
    //  FreeText /RC rich-text fallback
    // ==================================================================

    /// <summary>
    /// With /Contents absent the FreeText fallback must take its text from /RC, stripping the
    /// XHTML-lite markup. Compared against an /RC that carries only markup and no words: the
    /// former must ink glyphs inside the annotation /Rect, the latter must not.
    /// </summary>
    [Fact]
    public void FreeText_RcOnly_StripsTagsAndDrawsTheText()
    {
        byte[] withText = BuildPdfWithFreeTextAnnot(
            rc: "<body><p><b>Hello</b> <i>World</i>&nbsp;&amp; done</p></body>",
            includeContents: false);
        byte[] markupOnly = BuildPdfWithFreeTextAnnot(
            rc: "<body><p><b></b><i></i></p></body>",
            includeContents: false);

        Assert.True(GlyphInkInRect(withText) > 0,
            "the /RC text was not rendered — tags may have swallowed the content.");
        Assert.Equal(0, GlyphInkInRect(markupOnly));
    }

    /// <summary>Inked pixels inside the FreeText /Rect [100 700 400 740].</summary>
    private static int GlyphInkInRect(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int n = 0;
        for (int yUser = 702; yUser < 738; yUser++)
            for (int x = 102; x < 398; x++)
            {
                var c = bmp!.GetPixel(x, bmp.Height - 1 - yUser);
                if (c.Alpha > 8 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) n++;
            }
        return n;
    }

    /// <summary>
    /// With both <c>/Contents</c> and <c>/RC</c> present the fallback must draw the
    /// <c>/Contents</c> string. That is asserted by rendering two more fixtures and comparing
    /// pixels inside the annotation <c>/Rect</c>: the both-present page must match the
    /// <c>/RC</c>-only page whose <c>/RC</c> carries the <b>same words as /Contents</b>, and must
    /// <b>not</b> match the <c>/RC</c>-only page carrying different words. Either half alone is
    /// satisfiable by an annotation that draws nothing, so both are asserted together with a
    /// non-blank check.
    ///
    /// <para>The predecessor rendered one fixture and asserted a non-empty PNG, which says nothing
    /// about which of the two strings won — or whether either was drawn.</para>
    /// </summary>
    [Fact]
    public void FreeText_ContentsTakesPrecedenceOverRc()
    {
        const string contentsText = "Plain text from /Contents";   // what BuildPdfWithFreeTextAnnot writes

        byte[] both        = BuildPdfWithFreeTextAnnot(rc: "<b>fromRc</b>",  includeContents: true);
        byte[] rcSameText  = BuildPdfWithFreeTextAnnot(rc: contentsText,     includeContents: false);
        byte[] rcOtherText = BuildPdfWithFreeTextAnnot(rc: "<b>fromRc</b>",  includeContents: false);

        Assert.True(GlyphInkInRect(both) > 0,
            "the annotation drew no text at all, so precedence cannot be observed");

        Assert.Equal(RectPixels(rcSameText), RectPixels(both));
        Assert.NotEqual(RectPixels(rcOtherText), RectPixels(both));
    }

    /// <summary>Every pixel inside the FreeText /Rect [100 700 400 740], row-major.</summary>
    private static SKColor[] RectPixels(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        var px = new SKColor[36 * 296];
        int i = 0;
        for (int yUser = 702; yUser < 738; yUser++)
            for (int x = 102; x < 398; x++)
                px[i++] = bmp!.GetPixel(x, bmp.Height - 1 - yUser);
        return px;
    }

    // ==================================================================
    //  PdfViewerPreferences parser
    // ==================================================================

    [Fact]
    public void ViewerPreferences_AllBoolsAndNamesResolve()
    {
        var dict = new PdfDictionary();
        dict[new PdfName("HideToolbar")]     = PdfBoolean.True;
        dict[new PdfName("HideMenubar")]     = PdfBoolean.False;
        dict[new PdfName("CenterWindow")]    = PdfBoolean.True;
        dict[new PdfName("DisplayDocTitle")] = PdfBoolean.True;
        dict[new PdfName("Direction")]       = new PdfName("R2L");
        dict[new PdfName("PrintScaling")]    = new PdfName("None");
        dict[new PdfName("NumCopies")]       = new PdfNumber(3);

        var vp = InvokeInternalCtor(dict);
        Assert.True(vp.HideToolbar);
        Assert.False(vp.HideMenubar);
        Assert.True(vp.CenterWindow);
        Assert.True(vp.DisplayDocTitle);
        Assert.Equal("R2L",  vp.Direction);
        Assert.Equal("None", vp.PrintScaling);
        Assert.Equal(3,      vp.NumCopies);
    }

    [Fact]
    public void ViewerPreferences_NullDict_ReturnsAllNulls()
    {
        var vp = InvokeInternalCtor(null);
        Assert.Null(vp.HideToolbar);
        Assert.Null(vp.Direction);
        Assert.Null(vp.NumCopies);
        Assert.Null(vp.Dictionary);
    }

    [Fact]
    public void ViewerPreferences_PartialDict_UnspecifiedAreNull()
    {
        var dict = new PdfDictionary();
        dict[new PdfName("FitWindow")] = PdfBoolean.True;
        var vp = InvokeInternalCtor(dict);
        Assert.True(vp.FitWindow);
        Assert.Null(vp.HideToolbar);       // not specified
        Assert.Null(vp.NonFullScreenPageMode);
    }

    // Helper: PdfViewerPreferences's constructor is `internal` so tests go via
    // reflection.  Keeps the public surface minimal.
    /// <remarks>
    /// The constructor gained a <c>PdfDocument</c> parameter so the entries inside
    /// <c>/ViewerPreferences</c> can be resolved — they were read straight off the dictionary,
    /// which does not follow an indirect reference, so every one of them read as absent. It is
    /// optional, and <see langword="null"/> here on purpose: these cases are about the
    /// <i>direct</i> readers, and a document-less wrapper must keep working for a caller holding
    /// only a dictionary. The resolving behaviour is covered by
    /// <c>Core.Tests.IndirectCoreReadTests</c>.
    ///
    /// <para>Reflection asks for the exact signature, so this helper had to move with it: an
    /// optional parameter is still one constructor with two parameters, and the old lookup
    /// returned null.</para>
    /// </remarks>
    private static PdfViewerPreferences InvokeInternalCtor(PdfDictionary dict)
    {
        var ctor = typeof(PdfViewerPreferences)
            .GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(PdfDictionary), typeof(PdfDocument) },
                modifiers: null);
        Assert.NotNull(ctor);
        return (PdfViewerPreferences)ctor!.Invoke(new object[] { dict, null });
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static byte[] BuildPdfMinimal(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithPsXObject()
    {
        // The PS XObject carries no usable content; just a BBox so the placeholder
        // renderer has a box to outline.
        const string psContent = "% this would be PostScript code";
        const string cs = "q 1 0 0 1 100 400 cm /PsXo Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /PS /BBox [0 0 200 100]" +
            " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            psContent.Length, psContent);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /XObject << /PsXo 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithFreeTextAnnot(string rc, bool includeContents)
    {
        // Build a FreeText annotation with optional /Contents and mandatory /RC.
        const string cs = "1 1 1 rg 0 0 612 792 re f";
        string contentsKey = includeContents ? " /Contents (Plain text from /Contents)" : "";
        string rcStr = rc.Replace("(", "\\(").Replace(")", "\\)");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /Annot /Subtype /FreeText /Rect [100 700 400 740]" +
            " /C [0 0 0] /CA 1 /DA (/Helv 10 Tf 0 0 0 rg){0} /RC ({1}) >>\nendobj\n",
            contentsKey, rcStr);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Annots [6 0 R]" +
                  " /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
