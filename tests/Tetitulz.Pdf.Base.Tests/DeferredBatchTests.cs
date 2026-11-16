// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the deferred-items follow-up sprint:
///  • 3D annotation placeholder ( /Subtype /3D )
///  • PdfRenderOptions — Screen vs Print + explicit box overrides
///  • PdfActionRunner — event surface + JavaScript execution
///  • PdfPage.Transition — /Trans round-trip via end-to-end PDF
/// </summary>
public sealed class DeferredBatchTests
{
    // ==================================================================
    //  3D annotation fallback placeholder
    // ==================================================================

    /// <summary>
    /// A <c>/Subtype /3D</c> annotation with no <c>/AP</c> cannot be visualised, so the renderer
    /// draws a placeholder — a blue tinted fill, a dashed outline, a diagonal cross and a "3D"
    /// caption — over its <c>/Rect</c> rather than dropping the content silently.
    ///
    /// <para>The page itself is a flat 0.9 grey wash, so every pixel is technically "ink". The
    /// placeholder is identified by its <b>colour</b> instead: inside the <c>/Rect</c> the blue
    /// channel must lead the red one, and outside it the wash must still be perfectly neutral.
    /// The predecessor rendered the same file and asserted only that a PNG came back, which a page
    /// with no annotation renderer at all satisfies.</para>
    /// </summary>
    [Fact]
    public void Annotation_3D_WithNoAppearanceStream_DrawsAPlaceholderInItsRect()
    {
        // /Rect [100 600 300 750] — 200x150 pt, well inside the 612x792 MediaBox.
        byte[] pdf = BuildPdfWithAnnot(subtype: "3D", rect: "100 600 300 750");

        using var bmp = Rendering.PixelAssert.Render(pdf);

        // (140,700) sits inside the /Rect but off both diagonals and clear of the caption, so it
        // sees the placeholder's translucent blue fill over the grey wash and nothing else.
        var tinted = Rendering.PixelAssert.At(bmp, 140, 700);
        Assert.True(tinted.Blue > tinted.Red + 15,
            $"no 3D placeholder tint inside the /Rect; got {tinted} on a neutral grey page");

        // The dashed outline / cross / caption are drawn in saturated blue — at least one such
        // pixel must exist inside the box.
        int strong = 0;
        for (int ux = 101; ux < 300; ux += 2)
            for (int uy = 601; uy < 750; uy += 2)
            {
                var c = Rendering.PixelAssert.At(bmp, ux, uy);
                if (c.Blue > c.Red + 60) strong++;
            }
        Assert.True(strong > 0, "the placeholder drew no outline, cross or caption inside the /Rect");

        // Outside the annotation the page is the untouched neutral wash.
        var outside = Rendering.PixelAssert.At(bmp, 450, 300);
        Assert.Equal(outside.Red, outside.Blue);
        Assert.Equal(outside.Red, outside.Green);
        Assert.True(outside.Red > 200, $"the page wash was overpainted outside the /Rect; got {outside}");
    }

    // ==================================================================
    //  PdfRenderOptions
    // ==================================================================

    [Fact]
    public void Render_DefaultOptions_UsesCropBox()
    {
        // Page has MediaBox 0 0 612 792, CropBox 100 100 300 300.
        // Default render should use CropBox → 200×200 pts → at 72 dpi = 200×200 px.
        byte[] pdf = BuildPdfWithBoxes(
            mediaBox: "0 0 612 792", cropBox: "100 100 300 300",
            trimBox: null);

        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        var (w, h) = ReadPngDims(png);
        Assert.Equal(200, w);
        Assert.Equal(200, h);
    }

    [Fact]
    public void Render_UseTrimBox_ClipsToTrimBox()
    {
        // Smaller TrimBox (150×150) — explicit UseTrimBox flag overrides.
        byte[] pdf = BuildPdfWithBoxes(
            mediaBox: "0 0 612 792", cropBox: "100 100 300 300",
            trimBox:  "100 100 250 250");

        var opts = new PdfRenderOptions { UseTrimBox = true };
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72, opts);
        var (w, h) = ReadPngDims(png);
        Assert.Equal(150, w);
        Assert.Equal(150, h);
    }

    [Fact]
    public void Render_PrintIntent_UsesPrintAreaFromViewerPrefs()
    {
        // ViewerPreferences says /PrintArea /TrimBox.  Print intent should
        // pick TrimBox automatically without an explicit flag.
        byte[] pdf = BuildPdfWithBoxesAndPrintArea(
            mediaBox: "0 0 612 792", cropBox: "100 100 300 300",
            trimBox:  "100 100 250 250",
            printArea: "TrimBox");

        var opts = new PdfRenderOptions { Intent = RenderIntent.Print };
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72, opts);
        var (w, h) = ReadPngDims(png);
        Assert.Equal(150, w);
        Assert.Equal(150, h);
    }

    // ==================================================================
    //  PdfPage.Transition end-to-end
    // ==================================================================

    [Fact]
    public void PdfPage_Transition_ReturnsParsedObject()
    {
        byte[] pdf = BuildPdfWithTrans(
            styleName: "Blinds", duration: 2, dimension: "V");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var t = doc.Pages[0].Transition;
        Assert.NotNull(t);
        Assert.Equal("Blinds", t.Style);
        Assert.Equal(2.0,      t.Duration);
        Assert.Equal("V",      t.Dimension);
    }

    [Fact]
    public void PdfPage_Transition_AbsentReturnsNull()
    {
        byte[] pdf = BuildPdfMinimal("0 0 612 792 re");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        Assert.Null(doc.Pages[0].Transition);
    }

    // ==================================================================
    //  PdfActionRunner
    // ==================================================================

    [Fact]
    public void ActionRunner_CatalogOpenAction_Uri_RaisesEvent()
    {
        byte[] pdf = BuildPdfWithCatalogOpenUriAction("https://example.com/");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var runner = new PdfActionRunner(doc);
        string raised = null;
        runner.UriRequested += (_, e) => raised = e.Uri;

        var result = runner.RunCatalogOpenAction();
        Assert.NotNull(result);
        Assert.Equal("Uri", result.EventRaised);
        Assert.Equal("https://example.com/", raised);
    }

    /// <summary>
    /// With no <c>IJavaScriptPlugin</c> registered — which is Base's state on its own since the
    /// engine moved to <c>Tetitulz.Pdf.Plugins.JavaScript</c> (GAPS.md §15b) — a JavaScript action
    /// must be reported as not run, never silently treated as run.
    /// </summary>
    /// <remarks>
    /// This test was <c>ActionRunner_JavaScriptAction_ExecutesAndMutatesContext</c> and asserted the
    /// opposite, back when Base carried the Jint engine. It is renamed rather than weakened,
    /// because a test whose name and body disagree is how a real defect keeps green coverage; the
    /// execution case it used to cover now lives in
    /// <c>Tetitulz.Pdf.Plugins.JavaScript.Tests.JintJavaScriptPluginTests</c>, where the engine can
    /// actually be registered.
    /// </remarks>
    [Fact]
    public void ActionRunner_JavaScriptAction_WithNoEnginePlugin_IsReportedNotSilentlyDropped()
    {
        byte[] pdf = BuildPdfWithCatalogOpenJsAction(
            "this.getField('Name').value = 'modified';");

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        var jsCtx = new PdfJsExecutionContext();
        jsCtx.FieldValues["Name"] = "initial";

        var result = runner.RunCatalogOpenAction(jsCtx);

        Assert.NotNull(result);
        // Not executed, and the context is untouched — the script really did not run.
        Assert.False(result.JavaScriptExecuted);
        Assert.False(jsCtx.ModifiedFieldValues.ContainsKey("Name"));
        Assert.Equal("initial", jsCtx.FieldValues["Name"]);
        // And the caller is told why, by name, rather than being left to infer it from a no-op.
        Assert.Contains(result.Warnings, w => w.Contains("IJavaScriptPlugin"));
    }

    [Fact]
    public void ActionRunner_MissingOpenAction_ReturnsNull()
    {
        byte[] pdf = BuildPdfMinimal("0 0 612 792 re");
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        Assert.Null(runner.RunCatalogOpenAction());
    }

    /// <summary>
    /// An action the runner declines to perform must say so and do nothing else — the warning is
    /// the entire product, since the host decides what (if anything) happens next. So the text is
    /// asserted, along with the two facts that make "declined" different from "handled": no event
    /// was raised for the host to act on, and no JavaScript ran.
    ///
    /// <para>The predecessor asserted only <c>NotEmpty(result.Warnings)</c>, which any warning
    /// satisfies — including one about an unrelated failure — and said nothing about whether the
    /// runner had quietly gone ahead and raised an event anyway.</para>
    /// </summary>
    [Fact]
    public void ActionRunner_UnknownActionType_ReturnsWarning()
    {
        using var ms = new MemoryStream(BuildPdfMinimal("0 0 612 792 re"));
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        var action = new PdfAction
        {
            ActionType = PdfActionType.Rendition,
            Target = "",
            Trigger = "OpenDocument",
            FieldName = null
        };

        var result = runner.RunAction(action);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Rendition", warning);
        Assert.Null(result.EventRaised);
        Assert.False(result.JavaScriptExecuted);
    }

    // ==================================================================
    //  Helpers — minimal PDF builders
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

    private static byte[] BuildPdfWithAnnot(string subtype, string rect)
    {
        const string cs = "0.9 0.9 0.9 rg 0 0 612 792 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /Annot /Subtype /{0} /Rect [{1}] /C [0 0 0] /CA 1 >>\nendobj\n",
            subtype, rect);
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Annots [5 0 R] /Resources << >> >>\nendobj\n");
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

    private static byte[] BuildPdfWithBoxes(string mediaBox, string cropBox, string trimBox)
    {
        const string cs = "0.8 0.8 0.8 rg 0 0 612 792 re f";
        string trimStr = trimBox != null ? $" /TrimBox [{trimBox}]" : "";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [{0}] /CropBox [{1}]{2}" +
            " /Contents 4 0 R /Resources << >> >>\nendobj\n",
            mediaBox, cropBox, trimStr);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
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

    private static byte[] BuildPdfWithBoxesAndPrintArea(string mediaBox, string cropBox,
        string trimBox, string printArea)
    {
        const string cs = "0.8 0.8 0.8 rg 0 0 612 792 re f";
        string trimStr = trimBox != null ? $" /TrimBox [{trimBox}]" : "";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.AppendFormat(
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R" +
            " /ViewerPreferences << /PrintArea /{0} >> >>\nendobj\n", printArea);
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [{0}] /CropBox [{1}]{2}" +
            " /Contents 4 0 R /Resources << >> >>\nendobj\n",
            mediaBox, cropBox, trimStr);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
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

    private static byte[] BuildPdfWithTrans(string styleName, double duration, string dimension)
    {
        const string cs = "0 0 612 792 re";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.AppendFormat(
            System.Globalization.CultureInfo.InvariantCulture,
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Trans << /S /{0} /D {1} /Dm /{2} >>" +
            " /Contents 4 0 R /Resources << >> >>\nendobj\n",
            styleName, duration, dimension);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
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

    private static byte[] BuildPdfWithCatalogOpenUriAction(string uri)
    {
        const string cs = "0 0 612 792 re";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /S /URI /URI ({0}) >>\nendobj\n", uri);
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

    private static byte[] BuildPdfWithCatalogOpenJsAction(string jsBody)
    {
        const string cs = "0 0 612 792 re";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        // JS body as a PdfString literal (escape parentheses by using hex-string form).
        var hex = new StringBuilder("<");
        foreach (byte b in Encoding.UTF8.GetBytes(jsBody)) hex.AppendFormat("{0:X2}", b);
        hex.Append(">");
        sb.AppendFormat("5 0 obj\n<< /S /JavaScript /JS {0} >>\nendobj\n", hex);
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

    // Minimal PNG IHDR reader — width/height are the 2nd and 3rd 4-byte
    // big-endian ints starting at byte 16 (after the 8-byte PNG magic +
    // the IHDR length + "IHDR" chunk id).
    private static (int w, int h) ReadPngDims(byte[] png)
    {
        if (png == null || png.Length < 24) return (0, 0);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }
}
