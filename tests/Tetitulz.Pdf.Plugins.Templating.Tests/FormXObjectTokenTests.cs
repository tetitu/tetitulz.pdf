// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Tokens drawn inside a Form XObject.
///
/// <para>Real templates put body content in a form more often than not — design tools emit one, and
/// so does anything that reuses a letterhead. A token there used to be invisible to the engine and
/// its key came back in <c>UnmatchedKeys</c>, which reads as "you mistyped the key" and sends the
/// caller looking in exactly the wrong place.</para>
///
/// <para>The interesting asymmetry: <b>erasure happens in whichever stream holds the placeholder,
/// but the replacement is always drawn from the page.</b> A glyph position recorded inside a form is
/// already in page space, so the replacement can carry an absolute text matrix — whereas splicing it
/// into the form would draw it once per invocation of that form.</para>
/// </summary>
public class FormXObjectTokenTests
{
    private const string Hebrew = "שלום";

    /// <summary>A page whose only content is <c>/Body Do</c>; the form holds the text.</summary>
    private static byte[] TokenInsideAForm(string text) => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 5 0 R >> >> /Contents 4 0 R >>",
        TemplateFixtures.StreamBody(string.Empty, "/Body Do"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 6 0 R >> >>",
            $"BT /F1 12 Tf 72 700 Td ({text}) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>
    /// The same form drawn from two pages, so it is <b>shared</b> and must not be edited: erasing
    /// the token would erase it from the other page too.
    /// </summary>
    private static byte[] SharedFormOnTwoPages() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 6 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 6 0 R >> >> /Contents 5 0 R >>",
        TemplateFixtures.StreamBody(string.Empty, "/Body Do"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 7 0 R >> >>",
            "BT /F1 12 Tf 72 700 Td (Dear {{name}}, welcome.) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private static (TemplateFillReport report, byte[] output) Fill(
        byte[] template, Dictionary<string, string> values, TemplateOptions options = null)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        var report = PdfTemplateFiller.Fill(input, output, values, options);
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

    // ── the capability ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ATokenInsideAFormIsFoundAndErased()
    {
        var (report, output) = Fill(
            TokenInsideAForm("Dear {{name}}, welcome."), Named("Dana"));

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.UnmatchedKeys);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        Assert.Contains("Dear", text);
        Assert.Contains("welcome", text);
    }

    /// <summary>
    /// Erasure alone would satisfy the test above. The replacement has to reach the page — and it is
    /// drawn from page content, not from inside the form.
    /// </summary>
    [HebrewFontFact]
    public void TheReplacementForAFormTokenIsDrawnAndExtractsBack()
    {
        byte[] font = HebrewFontProbe.Bytes;

        var (report, output) = Fill(
            TokenInsideAForm("Dear {{name}}, welcome."), Named("Dana"),
            new TemplateOptions { FontProvider = _ => font });

        Assert.Empty(report.NotDrawn);
        Assert.Contains("Dana", ExtractText(output));
    }

    /// <summary>Hebrew into a form-hosted slot — the case the whole feature exists for.</summary>
    [HebrewFontFact]
    public void AHebrewReplacementWorksInsideAForm()
    {
        byte[] font = HebrewFontProbe.Bytes;

        var (report, output) = Fill(
            TokenInsideAForm("Name: {{name}}"), Named(Hebrew),
            new TemplateOptions { FontProvider = _ => font, BaseDirection = BaseDirection.RightToLeft });

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.NotDrawn);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        Assert.Contains(Hebrew, text);
    }

    // ── the refusal ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A form drawn from two pages is refused — and reported as <b>found but not editable</b>, not
    /// as unmatched. The distinction is the point: one says "check your key", the other says "the
    /// key is right, the template is shared".
    /// </summary>
    [Fact]
    public void ATokenInASharedFormIsReportedAsNotEditable_NotAsUnmatched()
    {
        byte[] template = SharedFormOnTwoPages();
        var (report, output) = Fill(template, Named("Dana"));

        Assert.Contains("name", report.NotEditableKeys);
        Assert.DoesNotContain("name", report.UnmatchedKeys);
        Assert.Empty(report.FilledTokens);
        Assert.Contains(report.Warnings, w => w.Contains("Form XObject") && w.Contains("drawn more than once"));

        // And the document is untouched — both pages still show the placeholder.
        Assert.Equal(template, output);
        Assert.Contains("{{name}}", ExtractText(output));
    }

    /// <summary>
    /// The control that stops the refusal above from being vacuous: the same token in a form drawn
    /// from exactly one page <i>is</i> replaced. Without this pair, an engine that refused every
    /// form would pass.
    /// </summary>
    [Fact]
    public void TheSameTokenInASingleDrawFormIsReplaced()
    {
        var (report, output) = Fill(
            TokenInsideAForm("Dear {{name}}, welcome."), Named("Dana"));

        Assert.Contains("name", report.FilledTokens);
        Assert.DoesNotContain("{{name}}", ExtractText(output));
    }

    /// <summary>
    /// A key that matches nothing at all still reports as unmatched — so the new not-editable
    /// bucket has not swallowed the ordinary typo case.
    /// </summary>
    [Fact]
    public void AGenuinelyAbsentKeyIsStillReportedAsUnmatched()
    {
        var (report, _) = Fill(
            TokenInsideAForm("Dear {{name}}, welcome."),
            new Dictionary<string, string> { ["nosuchtoken"] = "x" });

        Assert.Contains("nosuchtoken", report.UnmatchedKeys);
    }
}
