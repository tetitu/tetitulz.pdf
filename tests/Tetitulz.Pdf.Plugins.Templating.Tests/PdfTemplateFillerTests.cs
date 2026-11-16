// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Plugins.Templating;
using Xunit;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// <see cref="PdfTemplateFiller"/> over AcroForm templates.
///
/// <para>These assert on the <b>filled document</b> — the value written into the field object and
/// the glyphs in the generated appearance — rather than on the call returning a report. A filler
/// that wrote nothing at all still returns a valid non-null report over a valid non-empty PDF,
/// which is the shape this repo's banned pattern 3 exists to reject.</para>
/// </summary>
public class PdfTemplateFillerTests
{
    private const string Hebrew = "שלום";

    private static Dictionary<string, string> Values(params (string, string)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    // ── the happy path ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMatchedFieldIsFilled_AndItsValueIsInTheOutput()
    {
        using var input = new MemoryStream(TemplateFixtures.TextField());
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output, Values(("name", "Dana")));

        Assert.Contains("name", report.FilledFields);
        Assert.Empty(report.UnmatchedKeys);
        Assert.True(report.ChangedAnything);
        Assert.Equal("Dana", FieldValue(output));
    }

    /// <summary>
    /// The report's headline contract. A key matching nothing is how a contract ships with
    /// <c>{{signature_date}}</c> still printed on it, so it cannot be silent — and it cannot be
    /// conflated with success.
    /// </summary>
    [Fact]
    public void AnUnmatchedKeyIsReported_AndIsNotCountedAsFilled()
    {
        using var input = new MemoryStream(TemplateFixtures.TextField());
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output, Values(("nosuchfield", "x")));

        Assert.Contains("nosuchfield", report.UnmatchedKeys);
        Assert.Empty(report.FilledFields);
        Assert.False(report.ChangedAnything);
    }

    /// <summary>
    /// Every supplied key must land in exactly one bucket. A key that appears in none of them has
    /// gone missing from the report, which is worse than any individual failure because the caller
    /// has no way to notice.
    /// </summary>
    [Fact]
    public void EverySuppliedKeyAppearsInExactlyOneBucketOfTheReport()
    {
        using var input = new MemoryStream(TemplateFixtures.TextField());
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output,
            Values(("name", "Dana"), ("missing1", "a"), ("missing2", "b")));

        foreach (string key in new[] { "name", "missing1", "missing2" })
        {
            int buckets = 0;
            if (report.FilledFields.Contains(key)) buckets++;
            if (report.FilledTokens.Contains(key)) buckets++;
            if (report.UnmatchedKeys.Contains(key)) buckets++;
            Assert.True(buckets == 1, $"'{key}' appeared in {buckets} buckets, expected exactly 1");
        }
    }

    // ── Hebrew / RTL ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hebrew is stored logically and must be drawn right-to-left. Asserted against the appearance
    /// stream's glyph run, reversed relative to the logical order — the same property the Base-level
    /// form-filler test pins, checked here because the plugin is a separate entry point that could
    /// route around the composer.
    /// </summary>
    [HebrewFontFact]
    public void AHebrewValue_IsDrawnInVisualOrder()
    {
        byte[] font = HebrewFontProbe.Bytes;
        using var input = new MemoryStream(TemplateFixtures.TextField());
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output, Values(("name", Hebrew)),
            new TemplateOptions { FontProvider = _ => font });

        Assert.Empty(report.NotDrawn);

        var drawn = DrawnGlyphIds(output);
        var logical = new List<ushort>();
        var program = Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram.Parse(font);
        foreach (char c in Hebrew)
        {
            Assert.True(program.TryGetGlyph(c, out ushort gid));
            logical.Add(gid);
        }
        logical.Reverse();

        Assert.Equal(logical, drawn);
    }

    /// <summary>
    /// Without a font there is nothing to draw Hebrew with, and the honest outcome is a written
    /// value plus a named omission — never a silently mis-drawn appearance. The default of
    /// <see cref="TemplateOptions.AllowSystemFontSubstitution"/> is false because embedding
    /// redistributes the font.
    /// </summary>
    [Fact]
    public void AHebrewValueWithNoFontSource_WritesTheValueAndReportsTheOmission()
    {
        using var input = new MemoryStream(TemplateFixtures.TextField());
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output, Values(("name", Hebrew)));

        Assert.Contains("name", report.FilledFields);
        Assert.Contains("name", report.NotDrawn);
        Assert.NotEmpty(report.Warnings);
        Assert.True(report.NeedAppearances);
        Assert.Equal(Hebrew, FieldValue(output));
    }

    // ── options plumbing ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The base direction has to reach the glyphs. The value starts with a Latin word, so
    /// first-strong detection says LTR — the case the option exists for.
    /// </summary>
    [HebrewFontFact]
    public void ForcedRightToLeft_ChangesTheDrawnGlyphOrder()
    {
        byte[] font = HebrewFontProbe.Bytes;
        string value = $"Tel {Hebrew}";

        var auto = FillAndGetGlyphs(value, font, BaseDirection.Auto);
        var rtl = FillAndGetGlyphs(value, font, BaseDirection.RightToLeft);

        Assert.NotEqual(auto, rtl);
    }

    [Fact]
    public void SourcesWithoutFormFields_CopiesTheDocumentThroughAndReportsEveryKeyUnmatched()
    {
        byte[] original = TemplateFixtures.TextField();
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();

        var report = PdfTemplateFiller.Fill(input, output, Values(("name", "Dana")),
            new TemplateOptions { Sources = TemplateSources.PageTokens });

        Assert.Contains("name", report.UnmatchedKeys);
        Assert.False(report.ChangedAnything);
        Assert.Equal(original, output.ToArray());
    }

    // ── argument guards ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NullArgumentsThrow()
    {
        using var stream = new MemoryStream(TemplateFixtures.TextField());
        var values = Values(("name", "x"));

        Assert.Throws<System.ArgumentNullException>(
            () => PdfTemplateFiller.Fill(null, stream, values));
        Assert.Throws<System.ArgumentNullException>(
            () => PdfTemplateFiller.Fill(stream, null, values));
        Assert.Throws<System.ArgumentNullException>(
            () => PdfTemplateFiller.Fill(stream, stream, null));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static List<ushort> FillAndGetGlyphs(string value, byte[] font, BaseDirection direction)
    {
        using var input = new MemoryStream(TemplateFixtures.TextField());
        using var output = new MemoryStream();
        PdfTemplateFiller.Fill(input, output, Values(("name", value)),
            new TemplateOptions { FontProvider = _ => font, BaseDirection = direction });
        return DrawnGlyphIds(output);
    }

    private static string FieldValue(MemoryStream output)
    {
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var field = Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(TemplateFixtures.FieldObj));
        return Assert.IsType<PdfString>(doc.ResolveObject(field[PdfName.V])).GetText();
    }

    /// <summary>The glyph ids of the first hex-encoded show operator in the field's appearance.</summary>
    private static List<ushort> DrawnGlyphIds(MemoryStream output)
    {
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var field = Assert.IsAssignableFrom<PdfDictionary>(doc.GetObject(TemplateFixtures.FieldObj));
        var ap = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(field[PdfName.AP]));
        var normal = Assert.IsType<PdfStream>(doc.ResolveObject(ap[PdfName.N]));
        string content = Encoding.GetEncoding("iso-8859-1").GetString(normal.GetDecodedData());

        var match = System.Text.RegularExpressions.Regex.Match(content, @"<([0-9A-Fa-f]+)> Tj");
        Assert.True(match.Success, "no hex-encoded glyph run in the appearance: " + content);

        string hex = match.Groups[1].Value;
        var gids = new List<ushort>();
        for (int i = 0; i + 4 <= hex.Length; i += 4)
            gids.Add(ushort.Parse(hex.Substring(i, 4),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture));
        return gids;
    }
}
