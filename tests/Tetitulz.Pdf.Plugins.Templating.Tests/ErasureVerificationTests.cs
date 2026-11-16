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
/// The engine re-reads what it produced and reports any token it claimed to replace that is still
/// extractable (GAPS.md §14).
///
/// <para><b>Why erasure and not position.</b> Position is already self-checked before anything is
/// written, and structurally rather than by measurement — the placeholder's advance is emitted as
/// one compensating <c>TJ</c>, and <c>Replace</c> throws outright if the rewritten content does not
/// preserve the surrounding text. Erasure is the claim that can plausibly miss, because it depends
/// on having found <i>every</i> occurrence of a token, and a miss ships a document with the
/// placeholder still visible.</para>
///
/// <para><b>The check can only falsify.</b> Extraction is lossy, so not finding a token proves
/// nothing; finding one is proof of a miss. These tests are written to that asymmetry: the positive
/// case asserts a real detection, and the negative case asserts only that an ordinary fill reports
/// no <i>false</i> detection.</para>
/// </summary>
public class ErasureVerificationTests
{
    /// <summary>
    /// A page that draws a <b>shared</b> form containing <c>{{name}}</c> and also has its own copy
    /// in page content. The page copy is replaced; the shared form's cannot be edited, because
    /// erasing it would erase it from the other page too. So the token survives on a page the
    /// engine reported a replacement for — which is exactly the condition the check exists to
    /// surface, and it is a real template shape rather than a contrived one.
    /// </summary>
    private static byte[] TokenOnPageAndInASharedForm() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 6 0 R >> /Font << /F1 7 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 6 0 R >> /Font << /F1 7 0 R >> >> /Contents 5 0 R >>",
        TemplateFixtures.StreamBody(string.Empty,
            "/Body Do BT /F1 12 Tf 72 600 Td (Page says {{name}}.) Tj ET"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 7 0 R >> >>",
            "BT /F1 12 Tf 72 700 Td (Form says {{name}}.) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>An ordinary single-page template with nothing shared.</summary>
    private static byte[] PlainToken() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        TemplateFixtures.StreamBody(string.Empty,
            "BT /F1 12 Tf 72 700 Td (Dear {{name}}, welcome.) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private static (TemplateFillReport report, byte[] output) Fill(
        byte[] template, TemplateOptions options = null)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        var report = PdfTemplateFiller.Fill(
            input, output, new Dictionary<string, string> { ["name"] = "Ada" }, options);
        return (report, output.ToArray());
    }

    private static string ExtractText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc);
    }

    /// <summary>
    /// The detection. The key is reported replaced <i>and</i> unverified, and the token really is
    /// still in the output — asserted independently of the engine's own opinion, so the test states
    /// the fact rather than echoing the report.
    /// </summary>
    [Fact]
    public void ATokenSurvivingInASharedForm_IsReportedAsUnverified()
    {
        var (report, output) = Fill(TokenOnPageAndInASharedForm());

        Assert.Contains("name", report.FilledTokens);          // the page copy was replaced...
        Assert.Contains("{{name}}", ExtractText(output));      // ...and a copy really did survive
        Assert.Contains("name", report.UnverifiedKeys);        // ...and the caller is told so
        Assert.Contains(report.Warnings, w => w.Contains("still") && w.Contains("name"));
    }

    /// <summary>
    /// <b>One key, one warning — however many pages the survivor was found on.</b> The check walks
    /// every page and tests every replaced key against each, so a token surviving in a form that two
    /// pages draw is found more than once. A caller reading <see cref="TemplateFillReport.Warnings"/>
    /// should learn that a key is unverified, not how many pages happened to show it.
    /// </summary>
    [Fact]
    public void AKeySurvivingOnSeveralPages_IsReportedOnce()
    {
        var (report, _) = Fill(TokenOnPageAndInASharedForm());

        Assert.Single(report.UnverifiedKeys, k => k == "name");
        Assert.Single(report.Warnings, w => w.Contains("still") && w.Contains("name"));
    }

    /// <summary>
    /// The control that gives the detection its meaning: an ordinary fill leaves
    /// <see cref="TemplateFillReport.UnverifiedKeys"/> empty. Without it, a check that flagged
    /// every key would satisfy the test above.
    /// </summary>
    [Fact]
    public void AnOrdinaryFill_ReportsNothingUnverified()
    {
        var (report, output) = Fill(PlainToken());

        Assert.Contains("name", report.FilledTokens);
        Assert.Empty(report.UnverifiedKeys);
        Assert.DoesNotContain("{{name}}", ExtractText(output));
    }

    /// <summary>
    /// <b>What the check cannot do, pinned so it is not mistaken for what it can.</b> This fixture
    /// supplies no <c>FontProvider</c>, so the value is erased but never drawn — the extracted text
    /// is <c>"Dear  , welcome."</c>. Verification is silent, correctly: it asks whether the
    /// <i>placeholder</i> survived, not whether the replacement arrived. The key's absence from the
    /// output is reported through <see cref="TemplateFillReport.NotDrawn"/> instead.
    /// </summary>
    /// <remarks>
    /// This is the same lossiness the option's own remarks describe from the other direction. An
    /// empty <c>UnverifiedKeys</c> means "no surviving token was seen", never "the replacement is
    /// correct", and a test suite that conflated the two would read this fixture as a pass for
    /// something it says nothing about.
    /// </remarks>
    [Fact]
    public void VerificationIsSilentWhenTheValueWasErasedButNeverDrawn()
    {
        var (report, output) = Fill(PlainToken());

        Assert.Contains("name", report.NotDrawn);
        Assert.Empty(report.UnverifiedKeys);
        Assert.DoesNotContain("Ada", ExtractText(output));
    }

    /// <summary>
    /// The check must not change the document. Verification re-reads the output; it must not alter
    /// a byte of it, so the same fill with the check off is byte-identical but for the trailer's
    /// <c>/ID</c>, which every rewrite draws afresh (§14.4) and no two fills share.
    /// </summary>
    [Fact]
    public void VerificationDoesNotChangeTheProducedBytes()
    {
        var (_, verified) = Fill(PlainToken(), new TemplateOptions { VerifyErasure = true });
        var (_, unverified) = Fill(PlainToken(), new TemplateOptions { VerifyErasure = false });

        Assert.Equal(WithoutFileId(unverified), WithoutFileId(verified));
    }

    /// <summary>The file as Latin-1 text with the digits of its one <c>/ID</c> array blanked; the array must be there.</summary>
    private static string WithoutFileId(byte[] pdf)
    {
        string text = Latin1Text.Decode(pdf);
        var id = new System.Text.RegularExpressions.Regex(@"/ID\s*\[\s*<[0-9A-Fa-f]+>\s*<[0-9A-Fa-f]+>\s*\]");
        Assert.Single(id.Matches(text));
        return id.Replace(text, "/ID [<> <>]");
    }

    /// <summary>
    /// Turning it off really does turn the reporting off — otherwise the option would be
    /// decorative and the byte-identity test above would pass for the wrong reason.
    /// </summary>
    [Fact]
    public void WithVerificationOff_NothingIsReportedEvenWhenATokenSurvives()
    {
        var (report, output) = Fill(TokenOnPageAndInASharedForm(),
            new TemplateOptions { VerifyErasure = false });

        Assert.Contains("{{name}}", ExtractText(output));   // the survivor is still there...
        Assert.Empty(report.UnverifiedKeys);                // ...and deliberately not looked for
    }
}
