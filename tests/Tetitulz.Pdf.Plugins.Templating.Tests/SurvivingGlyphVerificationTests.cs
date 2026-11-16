// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// The engine re-walks every rewritten stream and refuses to write at all if any glyph that was not
/// part of a token has moved. These pin the two inputs that decide <i>where it thinks the glyphs
/// are</i> — and both failures are in the direction of a <b>false alarm</b>.
/// </summary>
/// <remarks>
/// <para><b>A false alarm here is not a warning, it is a refusal.</b> The check throws
/// <c>PdfTemplateException</c> and nothing is written, so getting either of these wrong makes
/// ordinary documents impossible to template — with a message blaming the document for drift the
/// engine invented. That is why both tests simply assert the fill <i>succeeds</i>: the exception is
/// the observable.</para>
///
/// <para><b>Why the existing form fixtures could not reach this.</b> They draw a form onto a page
/// whose own content is nothing but the <c>Do</c>, so there is no second stream to carry over and
/// no form matrix to re-walk under. Each test below adds exactly one of those.</para>
/// </remarks>
public class SurvivingGlyphVerificationTests
{
    /// <summary>
    /// A second form that the fill does <b>not</b> touch. Its glyphs are unchanged by definition, so
    /// they are carried over from the original walk rather than re-derived — dropping them instead
    /// makes every one of them look as though it stopped being drawn.
    /// </summary>
    [Fact]
    public void TextInAFormTheFillDoesNotTouchIsNotReportedAsHavingMoved()
    {
        var (report, output) = Fill(TwoForms(), Named("Dana"));

        Assert.Contains("name", report.FilledTokens);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        // The untouched form's text is still there, which is what the check was asked about.
        Assert.Contains("Second form text", text);
    }

    /// <summary>
    /// A form with a non-identity <c>/Matrix</c>. The re-walk has to use the CTM the stream was
    /// first walked with; re-deriving it as the identity compares form-local coordinates against
    /// page coordinates, and every surviving glyph in the form looks displaced.
    /// </summary>
    [Fact]
    public void TextInAFormWithANonIdentityMatrixIsNotReportedAsHavingMoved()
    {
        var (report, output) = Fill(FormWithMatrix(), Named("Dana"));

        Assert.Contains("name", report.FilledTokens);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        // The words either side of the token are the ones the check compares.
        Assert.Contains("Dear", text);
        Assert.Contains("welcome", text);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Two forms on one page: object 5 carries the token, object 6 is left alone.</summary>
    private static byte[] TwoForms() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /A 5 0 R /B 6 0 R >> >> /Contents 4 0 R >>",
        TemplateFixtures.StreamBody(string.Empty, "/A Do /B Do"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 7 0 R >> >>",
            "BT /F1 12 Tf 72 700 Td (Dear {{name}}, welcome.) Tj ET"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 7 0 R >> >>",
            "BT /F1 12 Tf 72 400 Td (Second form text.) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>One form, drawn under a halving, translating <c>/Matrix</c>.</summary>
    private static byte[] FormWithMatrix() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 5 0 R >> >> /Contents 4 0 R >>",
        TemplateFixtures.StreamBody(string.Empty, "/Body Do"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Matrix [0.5 0 0 0.5 100 100] " +
            "/Resources << /Font << /F1 6 0 R >> >>",
            "BT /F1 12 Tf 72 700 Td (Dear {{name}}, welcome.) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    // ------------------------------------------------------------------ helpers

    private static (TemplateFillReport report, byte[] output) Fill(
        byte[] template, Dictionary<string, string> values)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        var report = PdfTemplateFiller.Fill(input, output, values);
        return (report, output.ToArray());
    }

    private static string ExtractText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc);
    }

    private static Dictionary<string, string> Named(string value)
        => new Dictionary<string, string> { ["name"] = value };
}
