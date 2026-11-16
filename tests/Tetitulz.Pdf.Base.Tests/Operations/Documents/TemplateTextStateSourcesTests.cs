// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The template walk models the text state as the renderers read it: a font set through an
/// ExtGState's <c>/Font</c> (§8.4.5) is the run's font, a Type 3 glyph's width goes through its
/// <c>/FontMatrix</c> (§9.6.5), and a placeholder in a vertical font (<c>/WMode 1</c>, §9.7.4.3),
/// whose pen moves down a column the walk does not lay out, is refused by name.
/// </summary>
public class TemplateTextStateSourcesTests
{
    [Fact]
    public void AFontSetThroughAnExtGState_IsTheRunsFont()
    {
        const string content = "BT /GS1 gs 72 700 Td ({{n}}) Tj ( Smith) Tj ET\n";
        byte[] pdf = Pdf(content,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "/Resources << /Font << /F1 5 0 R >> /ExtGState << /GS1 << /Font [5 0 R 12] >> >> >>");

        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceText(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["{{n}}"] = "Jo" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.BuildWithDistinctWidths("Jo Smith") });

        Assert.Contains("{{n}}", result.ReplacedKeys);
        var words = PdfStructuredTextExtractor.Extract(new MemoryStream(output.ToArray()))[0].Lines.SelectMany(l => l.Words).ToList();
        var jo = words.Single(w => w.Text == "Jo");
        var smith = words.Single(w => w.Text == "Smith");
        // The slot of "{{n}}" at 12 pt in Helvetica: measured at that size, Smith stays where it is
        // drawn, after the replacement; measured at size 1 the erasure would have moved it.
        Assert.True(jo.X + jo.Width <= smith.X + 0.5f, $"Jo ends at {jo.X + jo.Width}, Smith starts at {smith.X}");
        Assert.InRange(smith.X, 72 + 12 * 0.5f, 72 + 12 * 3f);
    }

    /// <summary>
    /// A Type 3 font at <c>/FontMatrix [0.01 0 0 0.01 0 0]</c> with <c>/Widths [50 50]</c>: a glyph is
    /// half an em, so the erasure's <c>TJ</c> number is −500, where a width read as 1/1000 em wrote −50
    /// and moved the glyph after it by 4.5 pt.
    /// </summary>
    [Fact]
    public void AType3Width_GoesThroughItsFontMatrix()
    {
        const string glyph = "50 0 d0 0 0 40 40 re f";
        const string toUnicode = "/CIDInit /ProcSet findresource begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                                 "2 beginbfchar <41> <0041> <42> <0042> endbfchar endcmap end end";
        const string content = "BT /F1 10 Tf 72 700 Td (AB) Tj ET\n";
        byte[] pdf = Pdf(content,
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 50 50] /FontMatrix [0.01 0 0 0.01 0 0] /CharProcs << /A 6 0 R /B 6 0 R >> " +
            "/Encoding << /Type /Encoding /Differences [65 /A /B] >> /FirstChar 65 /LastChar 66 /Widths [50 50] /Resources << >> /ToUnicode 7 0 R >>",
            "/Resources << /Font << /F1 5 0 R >> >>",
            "<< /Length " + glyph.Length + " >>\nstream\n" + glyph + "\nendstream",
            "<< /Length " + toUnicode.Length + " >>\nstream\n" + toUnicode + "\nendstream");

        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceText(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["A"] = "" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("x") });
        Assert.Contains("A", result.ReplacedKeys);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string written = Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Assert.Matches(new Regex(@"\[\s*-500(\.0*)?\s"), written);
        Assert.DoesNotMatch(new Regex(@"\[\s*-50(\.0*)?\s"), written);
    }

    [Fact]
    public void APlaceholderInAVerticalFont_IsRefusedByName()
    {
        const string toUnicode = "/CIDInit /ProcSet findresource begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
                                 "5 beginbfchar <0001> <007B> <0002> <007B> <0003> <006E> <0004> <007D> <0005> <007D> endbfchar endcmap end end";
        const string content = "BT /F1 12 Tf 72 700 Td <00010002000300040005> Tj ET\n";
        byte[] pdf = Pdf(content,
            "<< /Type /Font /Subtype /Type0 /BaseFont /Vert /Encoding /Identity-V /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "/Resources << /Font << /F1 5 0 R >> >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Vert /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 " +
            "/FontDescriptor << /Type /FontDescriptor /FontName /Vert /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >> >>",
            "<< /Length " + toUnicode.Length + " >>\nstream\n" + toUnicode + "\nendstream");

        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceText(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["{{n}}"] = "Jo" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Jo") });

        Assert.DoesNotContain("{{n}}", result.ReplacedKeys);
        Assert.Contains(result.Warnings, w => w.Contains("vertical writing mode", System.StringComparison.Ordinal));
    }

    /// <summary>One page; object 5 is the font, further objects follow it.</summary>
    private static byte[] Pdf(string content, string font, string resources, params string[] more)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " + resources + " >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            font,
        };
        bodies.AddRange(more);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
