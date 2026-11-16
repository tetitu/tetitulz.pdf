// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A check box or radio button whose <c>/AS</c> names a state its <c>/AP /N</c> does not carry — its off
/// state, which ISO 32000-2 §12.7.5.2.3 makes optional, and which jsPDF and others leave out — shows
/// nothing. The raster renderer, the SVG export and the flattener each fell back to the first or only
/// entry, the on state, so every unchecked box drew checked and a flattened form kept the inverted value.
/// The fallback is for a widget with no <c>/AS</c> at all.
/// </summary>
public sealed class WidgetMissingStateAppearanceTests
{
    private const string Red = "1 0 0 rg 0 0 30 30 re f";

    private static byte[] Pdf(string state)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (c) /F 4 /Rect [10 10 40 40] " + state + " /AP << /N << /Yes 5 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Length " + Red.Length + " >>\nstream\n" + Red + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Whether the pixel at user (25, 25) is red: the widget's on appearance drawn there.</summary>
    private static bool RedAtTheWidget(byte[] pdf)
    {
        var page = PdfOperations.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        int x = 25, y = page.Height - 25;
        int at = y * page.Stride + x * 4;
        return page.Pixels[at] > 200 && page.Pixels[at + 1] < 60 && page.Pixels[at + 2] < 60;
    }

    private static string Svg(byte[] pdf) => PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0);

    private static byte[] Flattened(byte[] pdf)
    {
        using var output = new MemoryStream();
        PdfOperations.FlattenForm(new MemoryStream(pdf), output);
        return output.ToArray();
    }

    [Fact]
    public void AdversarialOffStateMissingFromTheAppearances_DrawsNothing_InEveryRendererAndTheFlattener()
    {
        byte[] off = Pdf("/AS /Off");
        Assert.False(RedAtTheWidget(off));
        Assert.False(SvgPaintsRed(Svg(off)));
        byte[] flattenedOff = Flattened(off);
        Assert.False(RedAtTheWidget(flattenedOff));
        // And the flattener takes the widget off the page with the form: it shows nothing to stamp.
        Assert.DoesNotContain("/Annots", Encoding.Latin1.GetString(flattenedOff)[off.Length..], StringComparison.Ordinal);

        // Control: the state the appearances carry is drawn, in each.
        byte[] on = Pdf("/AS /Yes");
        Assert.True(RedAtTheWidget(on));
        Assert.True(SvgPaintsRed(Svg(on)));
        Assert.True(RedAtTheWidget(Flattened(on)));
    }

    private static bool SvgPaintsRed(string svg)
        => svg.Contains("#ff0000", StringComparison.OrdinalIgnoreCase)
           || svg.Contains("#f00\"", StringComparison.OrdinalIgnoreCase)
           || svg.Contains("rgb(255,0,0)", StringComparison.OrdinalIgnoreCase)
           || svg.Contains("rgb(255, 0, 0)", StringComparison.OrdinalIgnoreCase);

    /// <summary>With no <c>/AS</c> at all, the one appearance still draws (the fallback's own case).</summary>
    [Fact]
    public void AWidgetWithNoStateName_DrawsItsOnlyAppearance()
    {
        Assert.True(RedAtTheWidget(Pdf("")));
    }
}
