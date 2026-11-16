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
/// Replacing a literal <c>{{token}}</c> drawn on a page, with the placeholder <b>erased</b> from the
/// content stream rather than covered.
///
/// <para><b>The assertions that matter are about the extracted text of the output</b>, checked
/// through <c>PdfTextExtractor</c> — an independent code path from the one that did the editing, and
/// exactly what a user would run to falsify the claim. A covered-but-present placeholder still
/// extracts, so "the token string is absent" is the only assertion that distinguishes erasure from
/// concealment.</para>
/// </summary>
public class PageTokenReplacementTests
{
    private const string Hebrew = "שלום";

    private static Dictionary<string, string> Values(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

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

    // ── erasure ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline property. A cover box would leave the placeholder extractable; only genuine
    /// removal from the content stream makes it absent.
    /// </summary>
    [Fact]
    public void ThePlaceholderIsGoneFromTheOutputText()
    {
        var (report, output) = Fill(
            TemplateFixtures.PageWithText("Dear {{name}}, welcome."),
            Values(("name", "Dana")));

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.UnmatchedKeys);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        Assert.DoesNotContain("{{", text);
    }

    /// <summary>
    /// Erasure alone is not enough — the surrounding text has to survive. Without this, an
    /// implementation that blanked the whole page would pass the test above.
    /// </summary>
    [Fact]
    public void TheSurroundingTextSurvives()
    {
        var (_, output) = Fill(
            TemplateFixtures.PageWithText("Dear {{name}}, welcome."),
            Values(("name", "Dana")));

        string text = ExtractText(output);
        Assert.Contains("Dear", text);
        Assert.Contains("welcome", text);
    }

    /// <summary>
    /// A key matching no token in the page leaves the document alone and is reported — the
    /// counterpart of the form-field case, and the thing that stops a caller shipping a contract
    /// with a placeholder still on it.
    /// </summary>
    [Fact]
    public void AKeyMatchingNoTokenIsReportedAndChangesNothing()
    {
        byte[] template = TemplateFixtures.PageWithText("Dear {{name}}, welcome.");
        var (report, output) = Fill(template, Values(("nosuchtoken", "x")));

        Assert.Contains("nosuchtoken", report.UnmatchedKeys);
        Assert.Empty(report.FilledTokens);
        Assert.False(report.ChangedAnything);
        Assert.Equal(template, output);
    }

    /// <summary>
    /// A token whose delimiters are split across separate show operators — which is the common case,
    /// not the exotic one, because <c>{</c>/<c>}</c> kern in most Latin fonts and producers break
    /// runs at kerning pairs.
    /// </summary>
    [Fact]
    public void ATokenSplitAcrossThreeShowOperatorsIsStillMatched()
    {
        string content = "BT /F1 12 Tf 72 700 Td (Dear {) Tj ({name) Tj (}}, welcome.) Tj ET";
        byte[] template = TemplateFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            TemplateFixtures.StreamBody(string.Empty, content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        var (report, output) = Fill(template, Values(("name", "Dana")));

        Assert.Contains("name", report.FilledTokens);
        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        Assert.Contains("Dear", text);
        Assert.Contains("welcome", text);
    }

    /// <summary>
    /// The token delimiters are configurable, and a template that controls its own authoring should
    /// prefer a pair that does not kern.
    /// </summary>
    [Fact]
    public void CustomDelimitersAreHonoured()
    {
        var (report, output) = Fill(
            TemplateFixtures.PageWithText("Dear [[name]], welcome."),
            Values(("name", "Dana")),
            new TemplateOptions { TokenOpen = "[[", TokenClose = "]]" });

        Assert.Contains("name", report.FilledTokens);
        Assert.DoesNotContain("[[name]]", ExtractText(output));
    }

    // ── the replacement text ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Erasing without drawing would satisfy every assertion above. This is the one that requires
    /// the replacement to actually reach the page, read back through the embedded font's
    /// <c>/ToUnicode</c>.
    /// </summary>
    [HebrewFontFact]
    public void TheReplacementValueIsDrawnAndExtractsBack()
    {
        byte[] font = HebrewFontProbe.Bytes;

        var (report, output) = Fill(
            TemplateFixtures.PageWithText("Dear {{name}}, welcome."),
            Values(("name", "Dana")),
            new TemplateOptions { FontProvider = _ => font });

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.NotDrawn);
        Assert.Contains("Dana", ExtractText(output));
    }

    /// <summary>
    /// The point of the exercise: a Hebrew value drawn into a template whose own font has no Hebrew
    /// glyphs. The value must round-trip through the embedded font's <c>/ToUnicode</c> in logical
    /// order, even though the glyphs are drawn right-to-left.
    /// </summary>
    [HebrewFontFact]
    public void AHebrewReplacementIsEmbeddedAndRoundTripsThroughToUnicode()
    {
        byte[] font = HebrewFontProbe.Bytes;

        var (report, output) = Fill(
            TemplateFixtures.PageWithText("Name: {{name}}"),
            Values(("name", Hebrew)),
            new TemplateOptions { FontProvider = _ => font, BaseDirection = BaseDirection.RightToLeft });

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.NotDrawn);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        Assert.Contains(Hebrew, text);
    }

    /// <summary>
    /// With no font source, a Hebrew value cannot be drawn. The honest outcome is an erased
    /// placeholder plus a named omission — never a silently mis-drawn or partially-drawn value.
    /// </summary>
    [Fact]
    public void AHebrewReplacementWithNoFontSource_ErasesAndReportsRatherThanGuessing()
    {
        var (report, output) = Fill(
            TemplateFixtures.PageWithText("Name: {{name}}"),
            Values(("name", Hebrew)));

        Assert.Contains("name", report.FilledTokens);
        Assert.Contains("name", report.NotDrawn);
        Assert.NotEmpty(report.Warnings);

        string text = ExtractText(output);
        Assert.DoesNotContain("{{name}}", text);
        Assert.Contains("Name:", text);
    }

    /// <summary>
    /// An empty value means "just take the placeholder out", which must not be reported as an
    /// undrawn failure — nothing was supposed to be drawn.
    /// </summary>
    [Fact]
    public void AnEmptyValueRemovesThePlaceholderWithoutReportingAFailure()
    {
        var (report, output) = Fill(
            TemplateFixtures.PageWithText("Dear {{name}}, welcome."),
            Values(("name", "")));

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.NotDrawn);
        Assert.DoesNotContain("{{name}}", ExtractText(output));
    }
}
