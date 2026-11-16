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
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Where a replacement is drawn, when the placeholder's text object sat under state the inserted
/// run inherits.
/// </summary>
/// <remarks>
/// <para><b>The run is inserted after the placeholder's <c>ET</c>, inside whatever <c>q</c> nesting the
/// text object sat in</b>, and draws with a text matrix recovered from the placeholder's rendering
/// matrix — which already folds in the CTM, the horizontal scale and the rise. Whatever of those is
/// still in force at the insertion point is therefore applied a second time. Its own comment said the
/// run draws "under our own (identity) CTM"; a <c>q … Q</c> keeps the CTM, it does not reset it.</para>
///
/// <para>Positions come from extraction of the produced file, so the assertion is about where a
/// reader finds the text, not about the operators written.</para>
/// </remarks>
public class TemplateReplacementPlacementTests
{
    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        byte[] head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            byte[] b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static byte[] TemplatePdf(string before, string after = "")
    {
        string content = $"{before}BT /F1 12 Tf 72 700 Td ({{{{name}}}} Smith) Tj ET{after}\n";
        return Assemble(new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(content).Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

    private static PdfTextWord Word(byte[] pdf, string contains)
    {
        var words = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0].Lines.SelectMany(l => l.Words).ToList();
        PdfTextWord word = words.FirstOrDefault(w => w.Text.Contains(contains, StringComparison.Ordinal));
        Assert.True(word != null, $"no word containing '{contains}': " + string.Join(" | ", words.Select(w => w.Text)));
        return word;
    }

    private static byte[] Replace(byte[] pdf)
    {
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.Replace(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["name"] = "Jo" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Jo") });
        Assert.Contains("name", result.ReplacedKeys);
        return output.ToArray();
    }

    /// <summary>
    /// The replacement font must cover what the run will be <b>drawn</b> as: a font built over the
    /// Arabic letters cannot draw their joined forms, so it is not the one to choose. Asked about
    /// the letters, the engine took it and then drew nothing in the placeholder's place.
    /// </summary>
    [Fact]
    public void AFontCoveringTheLettersButNotTheirShapedFormsIsNotTheFontChosen()
    {
        const string arabic = "بيت";
        using var output = new MemoryStream();

        var result = PdfTemplateEngine.Replace(new MemoryStream(TemplatePdf("")), output,
            new Dictionary<string, string> { ["name"] = arabic },
            new TemplateReplaceOptions
            {
                FontProvider = _ => MinimalTrueType.Build(arabic),
                AllowSystemFontSubstitution = true,
            });

        Assert.Contains("name", result.ReplacedKeys);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("", "")]                                    // the control: identity CTM
    [InlineData("q 1 0 0 1 100 50 cm ", " Q")]              // a translation around the text object
    [InlineData("q 0.5 0 0 0.5 0 0 cm ", " Q")]             // a scale
    [InlineData("5 Ts ", "")]                               // a rise, which is text state and survives ET
    public void TheReplacementIsDrawnWhereThePlaceholderWas(string before, string after)
    {
        byte[] pdf = TemplatePdf(before, after);
        PdfTextWord placeholder = Word(pdf, "{{name}}");

        PdfTextWord replacement = Word(Replace(pdf), "Jo");

        Assert.Equal(placeholder.X, replacement.X, 1.0);
        Assert.Equal(placeholder.Y, replacement.Y, 1.0);
    }

    /// <summary>A page whose text sits in a form XObject, drawn under a page-level CTM.</summary>
    private static byte[] FormTemplatePdf(string pageContent, string formExtra = "", string extraObject = null)
    {
        const string form = "BT /F1 12 Tf 72 700 Td ({{name}} Smith) Tj ET";
        var bodies = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {Latin1(pageContent).Length} >>\nstream\n{pageContent}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> {formExtra}" +
                $"/Length {Latin1(form).Length} >>\nstream\n{form}\nendstream\nendobj\n",
        };
        if (extraObject != null) bodies.Add($"7 0 obj\n{extraObject}\nendobj\n");
        return Assemble(bodies);
    }

    /// <summary>
    /// A form's <c>/Matrix</c> whose numbers are references places the replacement where the
    /// placeholder was: the engine reads the elements through the document, as both renderers and
    /// the extractor do, so the run is not placed under the identity a bare element test reads.
    /// </summary>
    [Fact]
    public void AdversarialFormMatrixWithReferencedNumbers_PlacesTheReplacementWhereThePlaceholderWas()
    {
        byte[] pdf = FormTemplatePdf("/Fm0 Do", formExtra: "/Matrix [1 0 0 1 7 0 R 7 0 R] ", extraObject: "50");
        PdfTextWord placeholder = Word(pdf, "{{name}}");
        Assert.Equal(122.0, placeholder.X, 1.0);  // potency: the matrix moves the text by its referenced numbers

        PdfTextWord replacement = Word(Replace(pdf), "Jo");

        Assert.Equal(placeholder.X, replacement.X, 1.0);
        Assert.Equal(placeholder.Y, replacement.Y, 1.0);
    }

    /// <summary>
    /// A token inside a form gets its run at the end of page content, so the CTM divided out there is
    /// the one page content ends with.
    /// </summary>
    /// <remarks>
    /// With the <c>cm</c> inside <c>q … Q</c> the page ends at identity, which is what the run used to
    /// assume; without the <c>Q</c> the translation is still in force where the run is appended.
    /// </remarks>
    [Theory]
    [InlineData("q 1 0 0 1 100 50 cm /Fm0 Do Q")]
    [InlineData("1 0 0 1 100 50 cm /Fm0 Do")]
    public void AFormTokensReplacementAllowsForTheCtmThePageEndsWith(string pageContent)
    {
        byte[] pdf = FormTemplatePdf(pageContent);
        PdfTextWord placeholder = Word(pdf, "{{name}}");

        PdfTextWord replacement = Word(Replace(pdf), "Jo");

        Assert.Equal(placeholder.X, replacement.X, 1.0);
        Assert.Equal(placeholder.Y, replacement.Y, 1.0);
    }

    /// <summary>
    /// The replacement's width follows the placeholder's horizontal scale once, and not its
    /// character spacing at all.
    /// </summary>
    /// <remarks>
    /// Position cannot show either: <c>Tz</c> and <c>Tc</c> leave a run's first glyph where it is. At
    /// <c>50 Tz</c> the matrix already halves the run, so an inherited <c>Tz</c> would quarter it; an
    /// inherited <c>Tc</c> adds its spacing after every glyph of a run measured without it.
    /// </remarks>
    [Theory]
    [InlineData("50 Tz ", 0.5)]
    [InlineData("3 Tc ", 1.0)]
    public void TheReplacementInheritsNoTextStateTheMatrixAlreadyCarries(string state, double expectedRatio)
    {
        float plain = Word(Replace(TemplatePdf("")), "Jo").Width;
        float scaled = Word(Replace(TemplatePdf(state)), "Jo").Width;

        Assert.True(plain > 1f, $"the control replacement has no width ({plain})");
        Assert.Equal(expectedRatio, scaled / plain, 2);
    }
}
