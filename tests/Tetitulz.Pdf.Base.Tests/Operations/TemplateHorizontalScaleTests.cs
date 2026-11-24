// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Regression test for the <c>Tz</c> half of the 2026-08 scan's PdfTemplateEngine findings.
/// </summary>
/// <remarks>
/// <c>EmitShowWithDrops</c> declared <c>float tfs = 1f, th = 1f;</c> and then assigned
/// <c>th = 1f</c> from a glyph that had no <c>Th</c> field at all, while its own doc comment
/// claimed the horizontal scale came "from a glyph the operator produced". <c>Drop.Advance</c>
/// was computed in WalkSegs with <c>gs.Th</c> applied, so the TJ adjustment that compensates for
/// an erased token under-compensated by exactly that factor — moving every glyph after the token.
/// </remarks>
public class TemplateHorizontalScaleTests
{
    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string content)
        => $"{n} 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n";

    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        var head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    /// <summary>A one-page template whose text object sets <c>Tz</c> to <paramref name="tz"/>.</summary>
    private static byte[] TemplatePdf(int tz)
    {
        string content =
            $"BT /F1 12 Tf {tz} Tz 72 700 Td ({{{{name}}}} Smith) Tj ET\n";

        return Assemble(new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                   "/Resources << /Font << /F1 5 0 R >> >> >>"),
            StreamObj(4, content),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        });
    }

    /// <summary>The X of the first glyph of "Smith" — the text that follows the token.</summary>
    private static float TrailingWordX(byte[] pdf)
    {
        var pages = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));
        var words = pages[0].Lines.SelectMany(l => l.Words).ToList();
        var smith = words.FirstOrDefault(w => w.Text.Contains("Smith", StringComparison.Ordinal));
        Assert.True(smith != null,
            "the fixture never produced a 'Smith' word: " +
            string.Join(" | ", words.Select(w => w.Text)));
        return smith.X;
    }

    /// <summary>
    /// Templating a line under <c>50 Tz</c> must leave the text after the token where it was.
    /// A TJ adjustment computed as if Th were 1 would move "Smith" by half a token-width and make
    /// VerifySurvivingGlyphs throw — making any such document impossible to template at all.
    /// </summary>
    [Fact]
    public void Replace_UnderNonDefaultTz_KeepsTheFollowingTextInPlace()
    {
        byte[] pdf = TemplatePdf(tz: 50);
        float before = TrailingWordX(pdf);

        using var outMs = new MemoryStream();
        PdfTemplateEngine.Replace(
            new MemoryStream(pdf), outMs,
            new Dictionary<string, string> { ["name"] = "Jo" },
            new TemplateReplaceOptions());

        byte[] produced = outMs.ToArray();
        float after = TrailingWordX(produced);

        // Half a point of tolerance: the replacement is re-fitted into the slot, so the surviving
        // text should land on its original position, not merely near it.
        Assert.Equal(before, after, 0.5);
    }

    /// <summary>
    /// The control: at the default 100 Tz the same template must behave identically, so the test
    /// above is measuring the horizontal-scale factor and not templating in general.
    /// </summary>
    [Fact]
    public void Replace_UnderDefaultTz_KeepsTheFollowingTextInPlace()
    {
        byte[] pdf = TemplatePdf(tz: 100);
        float before = TrailingWordX(pdf);

        using var outMs = new MemoryStream();
        PdfTemplateEngine.Replace(
            new MemoryStream(pdf), outMs,
            new Dictionary<string, string> { ["name"] = "Jo" },
            new TemplateReplaceOptions());

        float after = TrailingWordX(outMs.ToArray());

        Assert.Equal(before, after, 0.5);
    }
}
