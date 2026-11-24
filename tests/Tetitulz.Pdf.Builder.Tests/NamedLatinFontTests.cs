// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A run that names its own Latin family gets that font, rather than one of the three standard-14
/// buckets.
///
/// <para><b>What it guards against.</b> Mapping every WinAnsi run onto Helvetica, Times or Courier
/// by keyword would give a document asking for Garamond Helvetica's shapes <i>and</i> Helvetica's
/// metrics, with nothing anywhere saying so. The embedding machinery serves Latin text as it serves
/// text the standard-14 fonts cannot draw at all (anything above U+00FF).</para>
///
/// <para><b>The assertion is on how the glyphs reached the page</b>, not on the presence of a font
/// object. A standard-14 run is drawn as a literal string; an embedded composite font is drawn as
/// glyph ids. Those are different operators in the content stream, so they cannot be confused —
/// whereas "the PDF contains an embedded font" would pass for a font that nothing draws with.</para>
/// </summary>
public class NamedLatinFontTests
{
    private const string Text = "Hamburgefonstiv";

    /// <summary>The claim.</summary>
    [Fact]
    public void ANamedFamily_IsEmbeddedAndUsedForLatinText()
    {
        string content = Content("Garamond", withProvider: true);

        Assert.NotEmpty(PdfContentProbe.DrawnGlyphRuns(content));
        Assert.DoesNotContain(PdfContentProbe.DrawnTexts(content), t => t.Text.Contains("Ham"));
    }

    /// <summary>
    /// The control, and the one that keeps the whole change inert for everyone else: a run left on
    /// the default family is drawn exactly as before, as a literal in a standard-14 face, even
    /// though a font provider is available.
    /// </summary>
    [Fact]
    public void TheDefaultFamily_StillDrawsAsAStandard14Literal()
    {
        string content = Content(StandardFonts.Helvetica, withProvider: true);

        Assert.Contains(PdfContentProbe.DrawnTexts(content), t => t.Text.Contains("Ham"));
        Assert.Empty(PdfContentProbe.DrawnGlyphRuns(content));
    }

    /// <summary>
    /// <c>Arial</c>, <c>Times New Roman</c> and <c>Courier New</c> stay on the standard-14 faces —
    /// the one judgement call in the predicate. They are not standard-14 names, but the
    /// standard-14 faces are metric-compatible with them, so the layout is right either way, and
    /// treating them as "named" would make every ordinary Word or HTML document start embedding
    /// fonts to fix a difference nobody asked about.
    /// </summary>
    [Theory]
    [InlineData("Arial")]
    [InlineData("Times New Roman")]
    [InlineData("Courier New")]
    [InlineData("sans-serif")]
    [InlineData("Georgia, serif")]
    public void MetricCompatibleAndGenericNames_DoNotForceEmbedding(string family)
    {
        // "Georgia, serif" is here for the CSS-stack half: a stack is named by its FIRST family,
        // so this one IS specific and must embed — the others must not. One theory, because the
        // rule is a single predicate and splitting it would let the two halves drift.
        string content = Content(family, withProvider: true);
        bool embedded = PdfContentProbe.DrawnGlyphRuns(content).Count > 0;

        Assert.Equal(family.StartsWith("Georgia", StringComparison.Ordinal), embedded);
    }

    /// <summary>
    /// With no font source the named family cannot be honoured, and the run falls back to a
    /// standard-14 face <b>quietly</b> — but reports.
    ///
    /// <para>Quietly matters: the <c>UnrenderableText</c> policy exists for text the standard-14
    /// fonts genuinely cannot draw, where omitting or throwing is the honest response. Latin text
    /// in the wrong face is a cosmetic substitution, and escalating it would turn "Garamond is not
    /// installed" into a failed conversion.</para>
    /// </summary>
    [Fact]
    public void WithNoFontSource_ItFallsBackQuietlyAndReports()
    {
        var reports = new List<string>();
        string content = Content("Garamond", withProvider: false,
                                 unrenderable: UnrenderableTextPolicy.Throw,
                                 reports: reports);

        Assert.Contains(PdfContentProbe.DrawnTexts(content), t => t.Text.Contains("Ham"));
        Assert.Empty(PdfContentProbe.DrawnGlyphRuns(content));
        Assert.Contains(reports, r => r.Contains("Garamond", StringComparison.Ordinal));
    }

    /// <summary>
    /// A named Latin family is resolved from the caller's <c>FontProvider</c> and <b>never</b> from
    /// the host — even with <c>AllowSystemFontSubstitution</c> on, which is the default in
    /// <c>HtmlToPdfOptions</c>.
    ///
    /// <para><b>This is the rule the whole design turns on, and it was found by breaking it.</b>
    /// The first version resolved a named family like any other, and an HtmlToPdf test went red:
    /// <c>font-family:Georgia,serif</c> started embedding Georgia, because the developer's Windows
    /// box has it. On a Linux CI runner it would fall back to Times. Same input, same options, two
    /// different PDFs — determinism traded away for a cosmetic gain.</para>
    ///
    /// <para>Text above U+00FF keeps the host scan, and the asymmetry is the point: there the
    /// fallback is <c>'?'</c>, so any covering face beats none. A Latin run already has a good
    /// fallback in the standard-14 faces.</para>
    ///
    /// <para><b>Host-dependent, and deliberately so.</b> On a machine with no Georgia this passes
    /// without exercising anything. It is still the right assertion — it states an invariant, so a
    /// bare host cannot make it pass for the <i>wrong</i> reason — and it is mutation-verified on a
    /// host that does have the font. Same reasoning as the font-cache ceiling tests in Core.</para>
    /// </summary>
    [Fact]
    public void ANamedFamily_IsNeverResolvedFromTheHost()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        // A family a Windows host is very likely to have installed, and no provider to supply it.
        p.Runs.Add(new TextRun { Text = Text, FontName = "Georgia" });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = true,
            FontProvider = null,
        }));

        Assert.Empty(PdfContentProbe.DrawnGlyphRuns(content));
        Assert.Contains(PdfContentProbe.DrawnTexts(content), t => t.Text.Contains("Ham"));
    }

    // ----------------------------------------------------------------

    private static string Content(string family, bool withProvider,
                                  UnrenderableTextPolicy unrenderable = UnrenderableTextPolicy.Report,
                                  List<string> reports = null)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = Text, FontName = family });
        section.Children.Add(p);
        doc.Sections.Add(section);

        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            UnrenderableText = unrenderable,
            FontProvider = withProvider ? _ => MinimalTrueType.Build(Text) : null,
        };

        if (reports != null)
        {
            // Filtered to the calling thread: the sink is process-wide, so an unfiltered handler
            // collects a concurrent test's reports and makes this one order-dependent.
            int mine = Environment.CurrentManagedThreadId;
            void Sink(BuilderDiagnostic d)
            {
                if (Environment.CurrentManagedThreadId != mine) return;
                lock (reports) reports.Add(d.Detail ?? "");
            }
            BuilderDiagnostics.OnEvent += Sink;
            try { return PdfContentProbe.DecodeStreams(doc.ToPdf(options)); }
            finally { BuilderDiagnostics.OnEvent -= Sink; }
        }

        return PdfContentProbe.DecodeStreams(doc.ToPdf(options));
    }
}
