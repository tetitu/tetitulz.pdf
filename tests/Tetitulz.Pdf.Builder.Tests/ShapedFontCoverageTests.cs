// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Which code points the exporter demands a candidate face cover: the ones the run will be
/// <b>drawn</b> as, not the ones the document stores.
/// </summary>
/// <remarks>
/// <para>The Builder is the fifth member of the cluster behind
/// <c>Core.Text.TextRunComposer.CodePointsToCover</c> — the form appearance generator, the
/// signature appearance generator, the template engine and the DOCX renderer are the others.
/// Every one of them resolved a font by the code points of the <i>source</i> text and then drew
/// the <b>shaped</b> run, so a face covering ب ي ت and not their joined forms was accepted and
/// then had no glyph for a single character of the word.</para>
/// <para>Arabic is the only script in the tree where the two answers differ: shaping substitutes
/// the U+FE70 presentation forms, and Latin and Hebrew are not substituted at all — which is why
/// nothing caught this until a mutation was run against the refusal that could not be killed.</para>
/// </remarks>
public class ShapedFontCoverageTests
{
    /// <summary>"house" — three letters that all join.</summary>
    private const string Arabic = "بيت";

    /// <summary>
    /// What <c>OpenTypeShaper</c> draws <see cref="Arabic"/> as: initial beh, medial yeh, final
    /// teh. Written out rather than taken from the shaper, so the fixture states the expectation
    /// instead of agreeing with the code under test by construction.
    /// </summary>
    private const string ArabicShaped = "ﺑﻴﺖ";

    private static Document ArabicDoc() =>
        new DocumentBuilder()
            .AddSection(s => s
                .Rtl()
                .AddParagraph(p => p.Rtl().AddText(Arabic).FontSize(20)))
            .Build();

    /// <summary>
    /// A face covering exactly <paramref name="covered"/> and nothing else, with the host search
    /// switched off — so the only font this export can reach is the one the test names, on a
    /// developer's Windows box and a bare Linux container alike.
    /// </summary>
    private static PdfExportOptions Only(string covered, ICollection<BuilderDiagnostic> log) =>
        new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(covered),
            OnDiagnostic = d => log.Add(d),
        };

    private static bool EmbedsCompositeFont(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        return raw.Contains("Type0") && raw.Contains("Identity-H") && raw.Contains("FontFile2");
    }

    /// <summary>
    /// The refusal: a font that covers the letters but not the forms cannot draw the word, so it
    /// is not the font chosen — and the export says so rather than embedding it and drawing three
    /// spaces.
    /// </summary>
    [Fact]
    public void AFontCoveringTheLettersButNotTheirShapedFormsIsNotTheFontChosen()
    {
        var log = new List<BuilderDiagnostic>();

        byte[] pdf = ArabicDoc().ToPdf(Only(Arabic, log));

        Assert.False(EmbedsCompositeFont(pdf), "the letters-only face was embedded");
        BuilderDiagnostic unresolved = log.FirstOrDefault(d => d.Kind == BuilderDiagnosticKind.FontNotResolved);
        Assert.True(unresolved != null,
            "no FontNotResolved diagnostic: " + string.Join(" | ", log.Select(d => d.ToString())));
        // The message names what was actually needed, which is the whole point of the distinction:
        // "U+0628 is missing" would send a reader looking for a font that was right there.
        Assert.Contains("U+FE91", unresolved!.Detail, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The acceptance control, without which the refusal above would pass for any reason at all —
    /// the same document, the same provider shape, and a face that does cover the forms.
    /// </summary>
    [Fact]
    public void AFontCoveringTheShapedFormsIsEmbeddedAndDrawsTheRun()
    {
        var log = new List<BuilderDiagnostic>();

        byte[] pdf = ArabicDoc().ToPdf(Only(Arabic + ArabicShaped, log));

        Assert.True(EmbedsCompositeFont(pdf), "no composite font was embedded for a face that covers the run");
        Assert.DoesNotContain(log, d => d.Kind == BuilderDiagnosticKind.FontNotResolved
                                     || d.Kind == BuilderDiagnosticKind.GlyphMissing);
    }
}
