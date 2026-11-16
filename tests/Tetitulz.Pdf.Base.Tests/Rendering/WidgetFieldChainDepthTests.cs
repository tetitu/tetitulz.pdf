// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The widget fallback's <c>/Parent</c> climb must reach the root of any field tree the rest of the
/// library will descend, and must never invent a field type it could not resolve.
/// </summary>
/// <remarks>
/// <para><b>The cap was 8, matched to no descent anywhere in the tree, and untested.</b> A loop of
/// <c>d &lt; 8</c> reads at most eight dictionaries, so a field nested eight levels deep — nine
/// nodes counting the leaf — never reaches its root. Every other walker in the library descends to
/// 100 (<c>PdfFormReader</c>, <c>PdfAcroForm</c>) or 50 (the Conformance walk), so the renderer
/// alone gave up on trees the library otherwise reads happily. This is the only cap in the
/// inheritable-attribute family that a <b>conformant</b> file reaches through a public entry point.
/// Its own doc comment named the assumption that fails: "Real AcroForm hierarchies are one or two
/// levels deep."</para>
///
/// <para><b>And past the cap it lied.</b> <c>ft ??= "Tx"</c> turned "I could not determine the
/// type" into "the type is text", with no diagnostic on that path — so a check box whose
/// <c>/FT</c> sat out of reach typeset the word <c>Yes</c> where a tick belongs. Raising the cap
/// alone would only have moved the boundary of that.</para>
///
/// <para>The depth cases assert <b>pixel equality between two files that differ by one nesting
/// level</b>, which is what makes them about depth and nothing else — plus a dark-pixel floor, so
/// the equality cannot be satisfied by two blank pages.</para>
/// </remarks>
public class WidgetFieldChainDepthTests
{
    /// <summary>
    /// Nine levels renders identically to eight. At the old cap of 8 the ninth could not reach its
    /// root and drew differently.
    /// </summary>
    [Fact]
    public void ADeepFieldChainRendersTheSameAsAShallowOne()
    {
        byte[] shallow = RenderPixels(MergedChain(levels: 8));
        byte[] deep = RenderPixels(MergedChain(levels: 9));

        Assert.True(DarkPixels(shallow) > 0, "the control must actually draw something");
        Assert.Equal(shallow, deep);
    }

    /// <summary>
    /// The split-widget shape — field and widget as separate objects, which is every radio group —
    /// breaks one level earlier, because the widget consumes a hop.
    /// </summary>
    /// <remarks>
    /// No comment in the tree recorded that. Seven levels plus a widget is eight dictionaries, so
    /// the old cap failed here at a depth the merged case still cleared.
    /// </remarks>
    [Fact]
    public void ASplitWidgetChainRendersTheSameOneLevelEarlier()
    {
        byte[] shallow = RenderPixels(SplitChain(levels: 6));
        byte[] deep = RenderPixels(SplitChain(levels: 8));

        Assert.True(DarkPixels(shallow) > 0, "the control must actually draw something");
        Assert.Equal(shallow, deep);
    }

    /// <summary>
    /// A widget whose <c>/FT</c> is reachable nowhere draws its border and <b>nothing inside it</b>,
    /// and says so.
    /// </summary>
    /// <remarks>
    /// <para>The interior is inspected inset from the border, because the border is drawn either
    /// way and is not what this is about. §12.7.4 defines four field types and no default, so there
    /// is no value to fall back to.</para>
    ///
    /// <para>The diagnostic handler is filtered to the calling thread. <c>RenderDiagnostics.OnEvent</c>
    /// is process-wide, so an unfiltered handler makes an assertion about the whole test host — this
    /// repository's recorded intermittent-failure class.</para>
    /// </remarks>
    [Fact]
    public void AWidgetWithNoReachableTypeDrawsNothingInsideItsBorderAndReportsWhy()
    {
        var kinds = new List<RenderDiagnosticKind>();
        using (Collect(kinds))
        {
            using SKBitmap bmp = Decode(RenderPng(UntypedWidget()));
            Assert.Equal(0, DarkPixelsInside(bmp));
        }

        Assert.Equal(RenderDiagnosticKind.FormFieldTypeUnresolved, Assert.Single(kinds));
    }

    /// <summary>
    /// The control: the same file with a resolvable <c>/FT /Tx</c> paints its value and reports
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Without it, "never draw anything" and "always report" both satisfy the case above.
    /// </remarks>
    [Fact]
    public void AWidgetWithAReachableTypeStillPaintsAndReportsNothing()
    {
        var kinds = new List<RenderDiagnosticKind>();
        using (Collect(kinds))
        {
            using SKBitmap bmp = Decode(RenderPng(TypedWidget()));
            Assert.True(DarkPixelsInside(bmp) > 0);
        }

        Assert.DoesNotContain(RenderDiagnosticKind.FormFieldTypeUnresolved, kinds);
    }

    /// <summary>
    /// <c>/Q 1</c> and <c>/Q 2</c> are reported as ignored; <c>/Q 0</c> is not, because
    /// left-aligned is what the fallback already draws.
    /// </summary>
    /// <remarks>
    /// The repository's "presence is not deviation" rule: a channel that fires on conformant input
    /// is one a caller learns to ignore, which is worse than never having reported. Four false
    /// reports shipped once for exactly this reason.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void OnlyANonDefaultQuaddingIsReportedAsIgnored(int q, bool reported)
    {
        var kinds = new List<RenderDiagnosticKind>();
        using (Collect(kinds)) RenderPng(TypedWidget(quadding: "/Q " + q.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(reported, kinds.Contains(RenderDiagnosticKind.FormFieldQuaddingIgnored));
    }

    /// <summary>
    /// The form-wide <c>/AcroForm /Q</c> is a request too (ISO 32000-2 Table 224), and both
    /// renderers report it; a widget's own <c>/Q 0</c> is an answer that overrides it, and is not.
    /// </summary>
    /// <remarks>
    /// Both renderers climbed the widget's chain and stopped, so a form that centres every field
    /// at the form level was drawn left-aligned with no report — the silent case the diagnostic
    /// exists for.
    /// </remarks>
    [Theory]
    [InlineData("", "/Q 1", true)]
    [InlineData("/Q 0", "/Q 1", false)]
    public void AFormWideQuaddingIsReportedAsIgnoredByBothRenderers(string widgetQ, string acroFormQ, bool reported)
    {
        byte[] pdf = TypedWidget(quadding: widgetQ, acroFormExtra: acroFormQ);

        var raster = new List<RenderDiagnosticKind>();
        using (Collect(raster)) RenderPng(pdf);
        var svg = new List<RenderDiagnosticKind>();
        using (Collect(svg)) PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf, writable: false), 0);

        Assert.Equal(reported, raster.Contains(RenderDiagnosticKind.FormFieldQuaddingIgnored));
        Assert.Equal(reported, svg.Contains(RenderDiagnosticKind.FormFieldQuaddingIgnored));
    }

    // ------------------------------------------------------------------

    private static IDisposable Collect(List<RenderDiagnosticKind> into)
    {
        int thread = Thread.CurrentThread.ManagedThreadId;
        Action<RenderDiagnosticKind, string, Exception> handler = (kind, detail, ex) =>
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) return;
            lock (into) into.Add(kind);
        };
        RenderDiagnostics.OnEvent += handler;
        return new Unsubscribe(() => RenderDiagnostics.OnEvent -= handler);
    }

    private sealed class Unsubscribe : IDisposable
    {
        private readonly Action _dispose;
        internal Unsubscribe(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }

    private static byte[] RenderPng(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfPageRenderer.RenderPage(ms, 0, 72);
    }

    private static SKBitmap Decode(byte[] png)
    {
        SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static byte[] RenderPixels(byte[] pdf)
    {
        using SKBitmap bmp = Decode(RenderPng(pdf));
        return bmp.Bytes;
    }

    private static int DarkPixels(byte[] rgba)
    {
        int n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
            if (rgba[i + 3] > 128 && rgba[i] < 100 && rgba[i + 1] < 100 && rgba[i + 2] < 100) n++;
        return n;
    }

    /// <summary>Dark pixels strictly inside the widget's /Rect, inset past the 1 pt border.</summary>
    private static int DarkPixelsInside(SKBitmap bmp)
    {
        // /Rect [50 700 400 740] at 72 dpi on a 612x792 page: x 50..400, y (792-740)..(792-700).
        const int Inset = 3;
        int n = 0;
        for (int y = 792 - 740 + Inset; y < 792 - 700 - Inset; y++)
            for (int x = 50 + Inset; x < 400 - Inset; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha > 128 && c.Red < 100 && c.Green < 100 && c.Blue < 100) n++;
            }
        return n;
    }

    // ---- fixtures ----------------------------------------------------

    /// <summary>
    /// A chain of <paramref name="levels"/> grouping fields; the deepest is merged field+widget and
    /// is the annotation on the page. Only the root declares <c>/FT</c>, <c>/V</c> and <c>/DA</c>.
    /// </summary>
    private static byte[] MergedChain(int levels) => Chain(levels, splitWidget: false);

    /// <summary>The same, with the widget as a separate object below the deepest field.</summary>
    private static byte[] SplitChain(int levels) => Chain(levels, splitWidget: true);

    private static byte[] Chain(int levels, bool splitWidget)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /NeedAppearances true "
                + "/DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "%ANNOT%",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        int first = 6;
        int last = first + levels - 1;
        int widget = splitWidget ? last + 1 : last;

        for (int i = 0; i < levels; i++)
        {
            int num = first + i;
            var sb = new StringBuilder("<< /T (L").Append(i).Append(')');
            if (i == 0) sb.Append(" /FT /Tx /V (Ada) /DA (/Helv 12 Tf 0 g)");
            else sb.Append(" /Parent ").Append(num - 1).Append(" 0 R");

            if (i < levels - 1) sb.Append(" /Kids [").Append(num + 1).Append(" 0 R]");
            else if (splitWidget) sb.Append(" /Kids [").Append(widget).Append(" 0 R]");
            else sb.Append(" /Type /Annot /Subtype /Widget /Rect [50 700 400 740]");
            sb.Append(" >>");
            bodies.Add(sb.ToString());
        }

        if (splitWidget)
        {
            bodies.Add("<< /Type /Annot /Subtype /Widget /Parent " + last
                       + " 0 R /Rect [50 700 400 740] >>");
        }

        bodies[2] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                    + "/Resources << >> /Annots [" + widget + " 0 R] >>";
        return Assemble(bodies);
    }

    /// <summary>A widget with no <c>/FT</c> anywhere in its chain, but a value to draw.</summary>
    private static byte[] UntypedWidget() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /NeedAppearances true "
            + "/DR << /Font << /Helv 5 0 R >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> "
            + "/Annots [6 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /Annot /Subtype /Widget /T (loose) /V (Ada) /DA (/Helv 12 Tf 0 g) "
            + "/MK << /BC [0 0 0] >> /Rect [50 700 400 740] >>",
    });

    private static byte[] TypedWidget(string quadding = "/Q 0", string acroFormExtra = "") => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /NeedAppearances true "
            + "/DR << /Font << /Helv 5 0 R >> >> " + acroFormExtra + " >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> "
            + "/Annots [6 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (typed) /V (Ada) /DA (/Helv 12 Tf 0 g) "
            + quadding + " /Rect [50 700 400 740] >>",
    });

    private static byte[] Assemble(List<string> bodies)
    {
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
