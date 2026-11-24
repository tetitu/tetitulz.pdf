// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A widget with no <c>/AP</c> is drawn by the renderer's fallback, and the <c>/DA</c> that governs
/// its text is <b>inheritable</b> (§12.7.3.3): the nearest one up the <c>/Parent</c> chain, then
/// the document-wide <c>/AcroForm /DA</c>.
/// </summary>
/// <remarks>
/// <para><b>The widget alone is the one place a producer does not put it.</b> <c>/DA</c> normally
/// sits on the <i>field</i>, or once on <c>/AcroForm</c> for the whole document. Read off the widget
/// alone, the string would come back null on a conformant file, <c>ParseDefaultAppearance(null)</c>
/// yield size 0 and black, and every <c>/AP</c>-less field draw at the rect-height fallback size in
/// black whatever the file asked for.</para>
///
/// <para><b>Three inheritable keys, one rule</b>: <c>/FT</c>, <c>/V</c> and <c>/DA</c> all climb
/// <c>/Parent</c>. One of the three left out would not look like a missing case.</para>
///
/// <para><b>The assertions are on decoded pixels, and colour is what makes them falsifiable.</b>
/// A wrong <c>/DA</c> still paints text, so any "did it draw something" check passes with the
/// inheritance deleted. Red ink appears only if the file's own <c>/DA</c> was found; black-only means the
/// fallback ran.</para>
/// </remarks>
public class WidgetDefaultAppearanceInheritanceTests
{
    /// <summary>The field carries <c>/DA</c>; its widget kid carries none. The common shape.</summary>
    [Fact]
    public void TheFieldsDefaultAppearanceGovernsItsWidget()
    {
        Assert.True(HasRedInk(Render(
            acroFormDa: null,
            field: "<< /FT /Tx /T (name) /V (Ada) /DA (/Helv 18 Tf 1 0 0 rg) /Kids [7 0 R] >>",
            widget: "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [50 700 400 740] >>")),
            "the field's /DA specified red; the widget drew none, so the climb did not happen");
    }

    /// <summary>
    /// No field in the chain sets one, so the document-wide <c>/AcroForm /DA</c> applies.
    /// </summary>
    /// <remarks>
    /// This is the half a <c>/Parent</c> climb structurally cannot reach — the root field's parent
    /// is the form, not another field — so a climb alone leaves a document that sets one default
    /// for every field rendering none of them.
    /// </remarks>
    [Fact]
    public void TheDocumentWideDefaultAppearanceAppliesWhenNoFieldSetsOne()
    {
        Assert.True(HasRedInk(Render(
            acroFormDa: "/DA (/Helv 18 Tf 1 0 0 rg)",
            field: "<< /FT /Tx /T (name) /V (Ada) /Kids [7 0 R] >>",
            widget: "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [50 700 400 740] >>")),
            "/AcroForm /DA specified red and nothing overrode it");
    }

    /// <summary>
    /// The control: a <c>/DA</c> on the widget itself already worked, so the two cases above are
    /// about inheritance and not about <c>/DA</c> parsing or about text drawing at all.
    /// </summary>
    [Fact]
    public void ADefaultAppearanceOnTheWidgetItselfIsUnchanged()
    {
        Assert.True(HasRedInk(Render(
            acroFormDa: null,
            field: "<< /FT /Tx /T (name) /V (Ada) /Kids [7 0 R] >>",
            widget: "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [50 700 400 740] "
                    + "/DA (/Helv 18 Tf 1 0 0 rg) >>")),
            "a /DA on the widget was always read directly");
    }

    /// <summary>
    /// The nearer <c>/DA</c> wins: a widget's own overrides its field's, and a field's overrides
    /// the document's.
    /// </summary>
    /// <remarks>
    /// The floor for the climb's <i>direction</i>. Without it, "return the first /DA found anywhere
    /// in the document" satisfies every case above — and would silently repaint every field in the
    /// colour of whichever one happened to be reached first.
    /// </remarks>
    [Fact]
    public void TheNearestDefaultAppearanceWinsOverTheOnesAboveIt()
    {
        // Widget green, field red, document red — green must win.
        Assert.False(HasRedInk(Render(
            acroFormDa: "/DA (/Helv 18 Tf 1 0 0 rg)",
            field: "<< /FT /Tx /T (name) /V (Ada) /DA (/Helv 18 Tf 1 0 0 rg) /Kids [7 0 R] >>",
            widget: "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [50 700 400 740] "
                    + "/DA (/Helv 18 Tf 0 1 0 rg) >>")),
            "the widget's own green /DA must not be overridden by the red ones above it");
    }

    /// <summary>
    /// With no <c>/DA</c> anywhere the fallback still runs and the value is still painted — in
    /// black.
    /// </summary>
    /// <remarks>
    /// The other floor: the climb must not invent a colour. Without this, a change that always
    /// returned some red <c>/DA</c> would satisfy the first three cases.
    /// </remarks>
    [Fact]
    public void WithNoDefaultAppearanceAnywhereTheTextIsStillPaintedInBlack()
    {
        SKBitmap bmp = Render(
            acroFormDa: null,
            field: "<< /FT /Tx /T (name) /V (Ada) /Kids [7 0 R] >>",
            widget: "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [50 700 400 740] >>");

        using (bmp)
        {
            Assert.False(HasRed(bmp), "nothing in the file asked for red");
            Assert.True(DarkPixels(bmp) > 0, "the field's value must still be painted");
        }
    }

    // ------------------------------------------------------------------

    private static bool HasRedInk(SKBitmap bmp)
    {
        using (bmp) return HasRed(bmp);
    }

    /// <summary>Any pixel whose red channel clearly dominates green and blue.</summary>
    private static bool HasRed(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha > 128 && c.Red > 120 && c.Red - c.Green > 60 && c.Red - c.Blue > 60)
                    return true;
            }
        return false;
    }

    private static int DarkPixels(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha > 128 && c.Red < 100 && c.Green < 100 && c.Blue < 100) n++;
            }
        return n;
    }

    private static SKBitmap Render(string acroFormDa, string field, string widget)
    {
        using var ms = new MemoryStream(Build(acroFormDa, field, widget), writable: false);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static byte[] Build(string acroFormDa, string field, string widget)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /NeedAppearances true "
                + (acroFormDa ?? string.Empty)
                + " /DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << >> /Annots [7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            field,
            widget,
        };

        var sb = new StringBuilder();
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
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
