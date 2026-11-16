// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Forms;

/// <summary>
/// A widget the page does not show — §12.5.3's Hidden or NoView, or optional content the default
/// configuration turns off — is not stamped by the form flattener: stamping it would make content that
/// renders invisible part of the page. It leaves with the form.
/// </summary>
public sealed class FlattenHiddenWidgetTests
{
    private const string Draw = "BT /Helv 12 Tf 2 4 Td (secret) Tj ET";

    private static byte[] Pdf(string widgetExtra, bool ocOff = false)
    {
        string oc = ocOff ? " /OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>" : "";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DR << /Font << /Helv 7 0 R >> >> >>" + oc + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (t) /V (secret) /Rect [10 10 110 30] " + widgetExtra + " /AP << /N 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /Helv 7 0 R >> >> /Length " + Draw.Length + " >>\nstream\n" + Draw + "\nendstream",
            "<< /Type /OCG /Name (layer) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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

    private static string FlattenedText(byte[] pdf, out byte[] flattened)
    {
        using var output = new MemoryStream();
        PdfOperations.FlattenForm(new MemoryStream(pdf), output);
        flattened = output.ToArray();
        return PdfOperations.ExtractText(new MemoryStream(flattened), 1);
    }

    [Theory]
    [InlineData("/F 2")]     // Hidden
    [InlineData("/F 6")]     // Hidden | Print
    [InlineData("/F 36")]    // NoView | Print
    public void AdversarialHiddenWidget_IsNotStamped_AndLeavesWithTheForm(string flags)
    {
        string text = FlattenedText(Pdf(flags), out byte[] flattened);

        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        // The rewritten page lists no annotation: the widget left with the form.
        string revision = Encoding.Latin1.GetString(flattened)[Pdf(flags).Length..];
        Assert.Contains("/Type /Page", revision.Replace("/Type/Page", "/Type /Page"), StringComparison.Ordinal);
        Assert.DoesNotContain("/Annots", revision, StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialWidgetInOptionalContentThatIsOff_IsNotStamped()
    {
        string text = FlattenedText(Pdf("/F 4 /OC 6 0 R", ocOff: true), out _);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);

        // Control: the same layer on, the widget is stamped.
        Assert.Contains("secret", FlattenedText(Pdf("/F 4 /OC 6 0 R"), out _), StringComparison.Ordinal);
    }

    /// <summary>The control: a printing, visible widget is stamped and its text read back.</summary>
    [Fact]
    public void AVisibleWidget_IsStamped()
    {
        Assert.Contains("secret", FlattenedText(Pdf("/F 4"), out _), StringComparison.Ordinal);
    }
}
