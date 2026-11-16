// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Core = Tetitulz.Pdf.Core;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Token replacement on a page whose <c>/Resources</c> is <b>inherited</b> from its <c>/Pages</c>
/// node — the shape GAPS §14 recorded as <b>OPEN, not yet reproduced</b>.
///
/// <para><b>The claim.</b> <c>AttachFontResource</c> builds the page's new <c>/Resources</c> from
/// <c>page.Dictionary[PdfName.Resources]</c> — the page's <i>own</i> key — rather than from the
/// effective <c>page.Resources</c>. A page inheriting from a <c>/Pages</c> node has no own key, so
/// the new dictionary starts empty and is then attached to the page; since an own
/// <c>/Resources</c> <i>replaces</i> the inherited one rather than merging (§7.7.3.4), the page
/// would lose every font and XObject it was drawing with, keeping only the replacement font.</para>
///
/// <para><b>Inheritance is the whole fixture.</b> Every other template test gives the page its own
/// <c>/Resources</c>, which is why none of them can see this: with an own key the copy starts from
/// the right dictionary and the bug cannot fire. §7.7.3.4 makes <c>/Resources</c> one of exactly
/// four inheritable page attributes, and producers do inherit it — a document whose pages all share
/// one font list is the normal case, not a contrived one.</para>
/// </summary>
public class InheritedResourcesTests
{
    private const string Token = "{{name}}";

    /// <summary>
    /// The reproduction. After filling a token, the page must still be able to draw with the font
    /// it was already using.
    ///
    /// <para>Asserted on the <b>extracted text</b> of the untouched line rather than on the
    /// resource dictionary: losing the font is only a defect because the page stops rendering, and
    /// text extraction needs the font dictionary to decode the glyph codes. A structural assertion
    /// would also pass on a page that kept the entry and drew nothing.</para>
    /// </summary>
    [Fact]
    public void FillingATokenOnAPageWithInheritedResources_KeepsTheOtherTextDrawable()
    {
        byte[] pdf = InheritedResourcesTemplate();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfTemplateFiller.Fill(input, output,
            new System.Collections.Generic.Dictionary<string, string> { ["name"] = "Alice" },
            Options());

        output.Position = 0;
        string text = PdfOperations.ExtractText(output);

        Assert.Contains("KEEPTHIS", text);
        Assert.DoesNotContain(Token, text);
    }

    /// <summary>
    /// The same claim read structurally, which is what says <i>why</i> the text survived: the
    /// page's resources must still carry the font it inherited, not only the replacement.
    /// </summary>
    [Fact]
    public void TheInheritedFontEntry_SurvivesOnThePage()
    {
        byte[] pdf = InheritedResourcesTemplate();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfTemplateFiller.Fill(input, output,
            new System.Collections.Generic.Dictionary<string, string> { ["name"] = "Alice" },
            Options());

        // Read the PAGE's OWN /Resources out of the produced document. Searching the raw bytes for
        // "/F1" would pass on the font object still existing, which it does either way -- the
        // /Pages node keeps referencing it. What matters is whether THIS PAGE can still reach it:
        // once the page has an own /Resources, §7.7.3.4 stops the inherited one applying at all.
        output.Position = 0;
        using var produced = Core.Document.PdfDocument.Open(output);
        var page = produced.Pages[0];

        var own = produced.ResolveObject(page.Dictionary[Core.Objects.PdfName.Resources])
                  as Core.Objects.PdfDictionary;
        Assert.True(own != null, "the fill did not attach an own /Resources at all");

        var fonts = produced.ResolveObject(own![Core.Objects.PdfName.Font])
                    as Core.Objects.PdfDictionary;
        Assert.True(fonts != null, "the page's own /Resources carries no /Font");
        Assert.True(fonts![new Core.Objects.PdfName("F1")] != null,
            "the page's own /Resources lost the inherited /F1: everything this page was already " +
            "drawing with is now unreachable from it");
    }

    /// <summary>
    /// The fixture-validity control: the page really does inherit, with no own <c>/Resources</c>.
    /// Without this the two cases above could pass because the fixture quietly gave the page its
    /// own key — which is exactly the shape every other template test has, and the reason this
    /// defect could sit unreproduced.
    /// </summary>
    [Fact]
    public void TheFixturePageHasNoOwnResourcesKey()
    {
        string raw = Encoding.Latin1.GetString(InheritedResourcesTemplate());

        int pageAt = raw.IndexOf("/Type /Page ", StringComparison.Ordinal);
        Assert.True(pageAt > 0, "the fixture has no page object");
        int endObj = raw.IndexOf("endobj", pageAt, StringComparison.Ordinal);
        string pageDict = raw.Substring(pageAt, endObj - pageAt);

        Assert.DoesNotContain("/Resources", pageDict);
        Assert.Contains("/Resources", raw.Substring(0, pageAt));   // …but the /Pages node has one
    }

    /// <summary>
    /// Options carrying a synthetic font, which is what makes the test reach the code at all:
    /// <c>AttachFontResource</c> returns immediately when no font was embedded, so a fixture with
    /// no <c>FontProvider</c> passes without ever touching the method under test. The first version
    /// of this file did exactly that and was vacuous.
    ///
    /// <para>Synthetic rather than a host font, so the result does not depend on what is installed
    /// — the other font-supplying template tests sit behind a host-dependent skip for that reason.</para>
    /// </summary>
    private static TemplateOptions Options() => new TemplateOptions
    {
        FontProvider = _ => MinimalTrueType.Build("Alice0123456789KEPTHIS"),
    };
    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page document whose <c>/Resources</c> lives on the <c>/Pages</c> node. The page draws
    /// two lines: one carrying the token, one that must survive untouched.
    /// </summary>
    private static byte[] InheritedResourcesTemplate()
    {
        string content =
            "BT /F1 12 Tf 72 700 Td (" + Token + ") Tj ET " +
            "BT /F1 12 Tf 72 660 Td (KEEPTHIS) Tj ET";

        return TemplateFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            // The /Resources the page inherits — §7.7.3.4 makes it one of four inheritable keys.
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            StreamBody(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    }

    private static string StreamBody(string content)
        => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
}
