// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests.Fonts;

/// <summary>
/// A Standard-14 face may only be substituted by a font that is <b>metrically compatible</b> with
/// it, because the document's own <c>/Widths</c> are preserved and every glyph is drawn at the
/// advance the file declares.
/// </summary>
/// <remarks>
/// <para><b>The check was skipped for exactly the Standard-14 case</b>, which is the one case that
/// needs it. <c>ResolveSystemFont</c> guarded every other request against
/// <c>SKTypeface.FromFamilyName</c>'s silent fallback — an unknown family yields the platform
/// default rather than null — and exempted the Standard-14 map on the grounds that its
/// substitution is "a deliberate, metrically safe choice". That holds only if <c>"Arial"</c>
/// resolves to Arial.</para>
///
/// <para><b>On a stock Linux container it does not, and that is the platform CI verifies.</b> Arial
/// is absent, fontconfig answers DejaVu Sans, the guard was skipped, and DejaVu Sans was embedded
/// as Helvetica — under <c>/Widths</c> belonging to Helvetica. Every glyph then draws at an advance
/// from a different typeface, while <c>AllowSystemFontSubstitution</c>'s own documentation promises
/// "no glyph moves".</para>
///
/// <para><b>Why this tests the predicate rather than the remediation.</b> The defect reproduces only
/// where Arial, Times New Roman and Courier New are ABSENT. A test driving the public path would
/// pass on Windows whatever the predicate does — which is precisely how the exemption survived. The
/// rule is platform-independent even though its consequence is not, so the rule is what is pinned.</para>
/// </remarks>
public class MetricCompatibleSubstitutionTests
{
    /// <summary>Liberation is metric-compatible by design, and is what the renderer prefers.</summary>
    [Theory]
    [InlineData(StandardFonts.Helvetica,  "Liberation Sans")]
    [InlineData(StandardFonts.Helvetica,  "Arial")]
    [InlineData(StandardFonts.Helvetica,  "Nimbus Sans")]
    [InlineData(StandardFonts.TimesRoman, "Liberation Serif")]
    [InlineData(StandardFonts.TimesRoman, "Times New Roman")]
    [InlineData(StandardFonts.Courier,    "Liberation Mono")]
    [InlineData(StandardFonts.Courier,    "Courier New")]
    public void AMetricCompatibleFamilyIsAccepted(string standard14, string resolved)
        => Assert.True(PdfFontEmbeddingRemediator.IsMetricCompatible(standard14, resolved));

    /// <summary>
    /// The families that produce the defect: broad-coverage faces that are <b>not</b> metric clones.
    /// </summary>
    /// <remarks>
    /// <c>Core.Fonts.FontFileLocator</c> ranks all three after the metric clones and is right to —
    /// for RENDERING, a face with the glyph beats a face without it. For EMBEDDING under preserved
    /// widths they are wrong answers. This is the case that would have caught the original defect:
    /// DejaVu Sans is what fontconfig hands back for "Arial" on a stock container.
    /// </remarks>
    [Theory]
    [InlineData(StandardFonts.Helvetica,  "DejaVu Sans")]
    [InlineData(StandardFonts.Helvetica,  "Noto Sans")]
    [InlineData(StandardFonts.Helvetica,  "FreeSans")]
    [InlineData(StandardFonts.TimesRoman, "DejaVu Serif")]
    [InlineData(StandardFonts.Courier,    "DejaVu Sans Mono")]
    public void ABroadCoverageFamilyIsNotAMetricSubstitute(string standard14, string resolved)
        => Assert.False(PdfFontEmbeddingRemediator.IsMetricCompatible(standard14, resolved));

    /// <summary>
    /// A substitute for the wrong Standard-14 face is refused: Liberation Serif does not stand in
    /// for Helvetica.
    /// </summary>
    /// <remarks>
    /// The floor that stops the predicate degenerating into "is this any known family". Without it,
    /// a metric set flattened across all three faces would satisfy every case above.
    /// </remarks>
    [Theory]
    [InlineData(StandardFonts.Helvetica,  "Liberation Serif")]
    [InlineData(StandardFonts.Helvetica,  "Courier New")]
    [InlineData(StandardFonts.TimesRoman, "Liberation Sans")]
    [InlineData(StandardFonts.Courier,    "Arial")]
    public void ASubstituteForADifferentStandard14FaceIsRefused(string standard14, string resolved)
        => Assert.False(PdfFontEmbeddingRemediator.IsMetricCompatible(standard14, resolved));

    /// <summary>
    /// A request that is not Standard-14 has no substitution licence at all — it must match the
    /// requested family exactly, which is the other half of the guard.
    /// </summary>
    [Theory]
    [InlineData(null, "Liberation Sans")]
    [InlineData(null, "Arial")]
    public void ANonStandard14RequestHasNoSubstitutionLicence(string standard14, string resolved)
        => Assert.False(PdfFontEmbeddingRemediator.IsMetricCompatible(standard14, resolved));

    /// <summary>Symbol and ZapfDingbats are never substituted, so they are never compatible.</summary>
    /// <remarks>
    /// They address a built-in encoding no text font reproduces, so any substitute changes every
    /// glyph on the page — a far larger error than a wrong advance. They are absent from the
    /// Standard-14 family map for that reason, and this pins it.
    /// </remarks>
    [Theory]
    [InlineData(StandardFonts.Symbol,       "DejaVu Sans")]
    [InlineData(StandardFonts.Symbol,       "Arial")]
    [InlineData(StandardFonts.ZapfDingbats, "Arial")]
    public void SymbolicStandard14FacesAreNeverSubstituted(string standard14, string resolved)
        => Assert.False(PdfFontEmbeddingRemediator.IsMetricCompatible(standard14, resolved));

    /// <summary>Case and spacing are not significant, matching the rest of the family matching.</summary>
    [Theory]
    [InlineData("liberationsans")]
    [InlineData("LIBERATION SANS")]
    [InlineData("Liberation  Sans")]
    public void FamilyComparisonIgnoresCaseAndSpacing(string resolved)
        => Assert.True(PdfFontEmbeddingRemediator.IsMetricCompatible(StandardFonts.Helvetica, resolved));

    // ==================================================================
    //  The DECISION, not just the predicates
    // ==================================================================

    /// <summary>
    /// <b>The case the defect lives in.</b> A Standard-14 request mapped to
    /// <c>"Arial"</c>, on a host where the system answers <c>"DejaVu Sans"</c>, must be refused.
    /// </summary>
    /// <remarks>
    /// The predicate tests do not exercise this branch: exempting the Standard-14 map from the
    /// check at the call site leaves them all green. Pinning the decision rather than its parts is
    /// what makes that mutation land.
    /// </remarks>
    [Fact]
    public void AStandard14RequestAnsweredWithABroadCoverageFaceIsRefused()
        => Assert.False(PdfFontEmbeddingRemediator.IsAcceptableSubstitute(
               StandardFonts.Helvetica, "Arial", "DejaVu Sans"));

    /// <summary>Where the system really has the mapped family, it is accepted.</summary>
    [Fact]
    public void AStandard14RequestAnsweredWithItsMappedFamilyIsAccepted()
        => Assert.True(PdfFontEmbeddingRemediator.IsAcceptableSubstitute(
               StandardFonts.Helvetica, "Arial", "Arial"));

    /// <summary>
    /// And where it has the metric clone instead — a stock Linux container with Liberation
    /// installed — that is accepted too, which is the whole point of substituting at all.
    /// </summary>
    /// <remarks>
    /// The floor against over-correcting: a fix that simply demanded an exact family match would
    /// satisfy the refusal case above and stop remediation working anywhere Arial is absent,
    /// leaving UA21/UA25 permanently open on Linux.
    /// </remarks>
    [Fact]
    public void AStandard14RequestAnsweredWithItsMetricCloneIsAccepted()
        => Assert.True(PdfFontEmbeddingRemediator.IsAcceptableSubstitute(
               StandardFonts.Helvetica, "Arial", "Liberation Sans"));

    /// <summary>
    /// A non-Standard-14 request keeps the exact-match rule it always had.
    /// </summary>
    [Theory]
    [InlineData("Garamond",  "Garamond",    true)]
    [InlineData("Garamond",  "DejaVu Sans", false)]
    [InlineData("Garamond",  "Arial",       false)]
    public void ANonStandard14RequestMustMatchExactly(string requested, string resolved, bool accepted)
        => Assert.Equal(accepted,
               PdfFontEmbeddingRemediator.IsAcceptableSubstitute(null, requested, resolved));

    // ==================================================================
    //  "Exactly" means the same FAMILY, not the same STRING
    // ==================================================================

    /// <summary>
    /// The two sides of the comparison are spelled differently by construction: the request is a
    /// raw PostScript <c>/BaseFont</c>, where <c>-</c>, <c>_</c> and <c>.</c> are the conventional
    /// separators, and the answer is <c>SKTypeface.FamilyName</c>, a spaced display name.
    /// </summary>
    /// <remarks>
    /// <para><b>Every one of these is the same family.</b> Normalisation dropping whitespace only
    /// would make <c>"Liberation-Sans"</c> and <c>"Liberation Sans"</c> different families and
    /// report a font that <em>is</em> installed as <em>"no font matching … is installed on this
    /// machine"</em> — while the renderer, whose predicate folds the separators, draws the page with
    /// that very face.</para>
    ///
    /// <para>Whitespace-only differences (<c>FamilyComparisonIgnoresCaseAndSpacing</c> above) are
    /// deliberately not repeated here: including them would let this theory pass for a
    /// whitespace-only normalisation.</para>
    /// </remarks>
    [Theory]
    [InlineData("Liberation-Sans",    "Liberation Sans")]
    [InlineData("Courier-New",        "Courier New")]
    [InlineData("Frutiger_45_Light",  "Frutiger 45 Light")]
    [InlineData("TimesNewRomanPS.MT", "TimesNewRomanPSMT")]
    public void APostScriptNameMatchesTheSameFamilyWrittenWithOtherSeparators(
        string requested, string resolved)
        => Assert.True(PdfFontEmbeddingRemediator.IsAcceptableSubstitute(null, requested, resolved));

    /// <summary>
    /// The floor: folding the separators must not turn the check into a prefix match or into
    /// "accept anything". A style word is still a different family, and it is the difference
    /// between embedding the font that was asked for and embedding a neighbour of it.
    /// </summary>
    [Theory]
    [InlineData("Liberation-Sans", "DejaVu Sans")]
    [InlineData("Arial",           "Arial Black")]
    [InlineData("Garamond",        "Garamond Premier")]
    [InlineData("Liberation-Sans", "Liberation-Serif")]
    public void FoldingSeparatorsDoesNotMakeDifferentFamiliesMatch(string requested, string resolved)
        => Assert.False(PdfFontEmbeddingRemediator.IsAcceptableSubstitute(null, requested, resolved));
}
