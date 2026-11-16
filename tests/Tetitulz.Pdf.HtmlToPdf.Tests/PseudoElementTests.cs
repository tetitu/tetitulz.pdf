// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>::before</c> and <c>::after</c> in the managed engine.
///
/// <para><b>The decline said "no pseudo-element box".</b> True of CSS's model and not what the
/// common case needs: <c>content: "→ "</c> generates an <i>inline</i> box, which in this document
/// model is a text run. What actually blocked it was the cascade — it dropped <c>::before</c>
/// selectors into the same <c>catch</c> as genuinely unsupported ones, so a rule the engine could
/// act on was indistinguishable from one it could not.</para>
///
/// <para>These assert the extracted text and its <b>order</b>, which is the only thing separating
/// generated content that landed in the right place from generated content that did not.</para>
/// </summary>
public class PseudoElementTests
{
    [Fact]
    public void BeforeContent_IsGeneratedInFrontOfTheElementsText()
    {
        string text = TextOf("<style>p::before{content:\"[\"}</style><p>Body</p>");

        Assert.Contains("[Body", text.Replace(" ", ""));
    }

    [Fact]
    public void AfterContent_IsGeneratedBehindIt()
    {
        string text = TextOf("<style>p::after{content:\"]\"}</style><p>Body</p>");

        Assert.Contains("Body]", text.Replace(" ", ""));
    }

    /// <summary>
    /// Both at once, and on a paragraph that already has a preceding sibling — the case that
    /// catches a before-run inserted at index 0 rather than at the element's own start.
    /// </summary>
    [Fact]
    public void BeforeAndAfter_LandOnTheRightSideOfTheirOwnElement()
    {
        string text = TextOf(
            "<style>.m::before{content:\"<\"} .m::after{content:\">\"}</style>" +
            "<p>One <span class=\"m\">Two</span> Three</p>").Replace(" ", "");

        Assert.Contains("One<Two>Three", text);
    }

    /// <summary>The one-colon CSS2 spelling is the same pseudo-element.</summary>
    [Fact]
    public void TheSingleColonSpelling_WorksToo()
    {
        Assert.Contains("[Body", TextOf("<style>p:before{content:\"[\"}</style><p>Body</p>")
            .Replace(" ", ""));
    }

    /// <summary>
    /// <c>attr()</c> is supported: the value is on the element and needs no machinery. This is the
    /// half that makes generated content useful for things like printing a link's target.
    /// </summary>
    [Fact]
    public void AttrContent_ReadsTheElementsAttribute()
    {
        string text = TextOf(
            "<style>a::after{content:\" (\" attr(href) \")\"}</style>" +
            "<p><a href=\"https://example.invalid/x\">Link</a></p>");

        Assert.Contains("https://example.invalid/x", text);
    }

    /// <summary>A CSS hex escape in the string is decoded, so <c>\2192</c> is an arrow.</summary>
    [Fact]
    public void AHexEscapeInTheContentString_IsDecoded()
    {
        Assert.Contains("→", TextOf("<style>p::before{content:\"\\2192 \"}</style><p>Go</p>"));
    }

    /// <summary>
    /// A hex escape that names no character — a surrogate, zero, a code point past U+10FFFF — is U+FFFD
    /// (CSS Syntax Level 3 §4.3.7), and the escape beside it still decodes. A surrogate reached
    /// <c>char.ConvertFromUtf32</c>, whose <see cref="ArgumentOutOfRangeException"/> left the conversion:
    /// a page's own style sheet ended it with an exception that is not the library's.
    /// </summary>
    [Theory]
    [InlineData("\\D800")]
    [InlineData("\\DFFF ")]
    [InlineData("\\0")]
    [InlineData("\\110000")]
    public void AHostileHexEscapeNamingNoCharacter_IsTheReplacementCharacter(string escape)
    {
        var model = HtmlToBuilderMapper.MapToModel(
            "<style>p::before{content:\"" + escape + "\\2192 \"}</style><p>Go</p>",
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        string text = string.Concat(model.Sections[0].Children.OfType<Tetitulz.Pdf.Builder.Model.Paragraph>()
            .SelectMany(p => p.Runs).OfType<Tetitulz.Pdf.Builder.Model.TextRun>().Select(r => r.Text));
        Assert.Equal("\uFFFD\u2192Go", text);
    }

    /// <summary>
    /// The cascade applies to a pseudo-element like anything else — a later, more specific rule
    /// wins. Without this, "take the first rule that matches" would pass every case above.
    /// </summary>
    [Fact]
    public void ThePseudoElementGoesThroughTheCascade()
    {
        string text = TextOf(
            "<style>p::before{content:\"A\"} p.x::before{content:\"B\"}</style>" +
            "<p class=\"x\">Body</p>").Replace(" ", "");

        Assert.Contains("BBody", text);
        Assert.DoesNotContain("ABody", text);
    }

    /// <summary>
    /// <c>content: none</c> generates nothing and reports nothing — it is how a reset stylesheet
    /// switches a pseudo-element off, so reporting it would fire on documents doing the right
    /// thing.
    /// </summary>
    [Fact]
    public void ContentNone_GeneratesNothingAndIsNotReported()
    {
        var reports = new List<string>();
        string text = TextOf("<style>p::before{content:none}</style><p>Body</p>", reports);

        Assert.Equal("Body", text.Trim());
        Assert.DoesNotContain(reports, r => r.Contains("::before", StringComparison.Ordinal));
    }

    /// <summary>
    /// What cannot be generated is reported rather than half-generated. A marker that silently
    /// loses its number reads as a layout bug, which is worse than one that never appeared.
    /// </summary>
    [Theory]
    [InlineData("counter(chapter)")]
    [InlineData("url(x.png)")]
    public void ContentThisEngineCannotGenerate_IsReported(string value)
    {
        var reports = new List<string>();
        TextOf($"<style>p::before{{content:{value}}}</style><p>Body</p>", reports);

        Assert.Contains(reports, r => r.Contains("cannot generate", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pseudo-element the author made <c>display: block</c> really does need a box, and an
    /// inline run would put its text on the wrong line. Skipped, and said.
    /// </summary>
    [Fact]
    public void ABlockLevelPseudoElement_IsSkippedAndReported()
    {
        var reports = new List<string>();
        string text = TextOf(
            "<style>p::before{content:\"X\";display:block}</style><p>Body</p>", reports);

        Assert.DoesNotContain("X", text);
        Assert.Contains(reports, r => r.Contains("display:block", StringComparison.Ordinal));
    }

    /// <summary>
    /// Other pseudo-elements still fail the selector match and are skipped, exactly as before.
    /// Treating <c>::first-line</c> as if it styled the whole element would be worse than ignoring
    /// it, and this is what stops the new stripping from being over-eager.
    /// </summary>
    [Fact]
    public void OtherPseudoElements_AreStillSkipped()
    {
        string text = TextOf("<style>p::first-line{color:red}</style><p>Body</p>");

        Assert.Contains("Body", text);
    }

    // ------------------------------------------------------------------

    private static string TextOf(string html, List<string> reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null
                ? null
                : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return string.Join(" ", PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .Select(w => w.Text));
    }
}
