// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// <see cref="PdfFontSubsetter.TryCompact"/> — the CFF-capable half of font size reduction, and the
/// caller-facing entry point for it.
/// </summary>
/// <remarks>
/// <para><c>Subset</c> is TrueType-only, so every CFF font this library embeds is embedded whole.
/// <c>TryCompact</c> removes the tables a PDF reader never consults, which for a modern OpenType
/// face is usually the largest thing after the outlines — and it cannot change a rendered glyph,
/// because it never rewrites one.</para>
/// <para><b>It is deliberately NOT applied by <c>PdfFontEmbedder</c> automatically</b>, because
/// that would move the output bytes of every existing caller. These pin the opt-in contract.</para>
/// </remarks>
public class FontCompactionTests
{
    /// <summary>
    /// The headline: a CFF font — which <c>Subset</c> refuses outright — is compacted.
    /// </summary>
    [Fact]
    public void TryCompact_ACffFont_Succeeds_WhereSubsetRefuses()
    {
        byte[] font = OpenTypeCffFont();

        // The contrast is the point: this is the case that previously had no size reduction at all.
        Assert.Throws<NotSupportedException>(() => PdfFontSubsetter.Subset(font, "A"));

        Assert.True(PdfFontSubsetter.TryCompact(font, out byte[] compacted, out string reason));
        Assert.Null(reason);
        Assert.True(compacted!.Length < font.Length,
            $"compacted font is {compacted.Length} bytes against the original's {font.Length}");
    }

    /// <summary>The outlines survive: this removes tables, never glyphs.</summary>
    [Fact]
    public void TryCompact_KeepsTheOutlinesAndDropsTheLayoutTables()
    {
        PdfFontSubsetter.TryCompact(OpenTypeCffFont(), out byte[] compacted, out _);

        string[] tags = Tags(compacted!);
        Assert.Contains("CFF ", tags);   // the outlines
        Assert.Contains("head", tags);
        Assert.DoesNotContain("GSUB", tags);
        Assert.DoesNotContain("GPOS", tags);
        Assert.DoesNotContain("DSIG", tags);
    }

    /// <summary>
    /// A font with nothing droppable reports that rather than returning a "compacted" copy — so a
    /// caller can tell "already minimal" from "compacted", without comparing byte arrays.
    /// </summary>
    [Fact]
    public void TryCompact_AFontWithNothingToDrop_ReportsInsteadOfSucceeding()
    {
        byte[] minimal = Font(("CFF ", 400), ("head", 54), ("cmap", 40));

        Assert.False(PdfFontSubsetter.TryCompact(minimal, out byte[] compacted, out string reason));
        Assert.Null(compacted);
        Assert.NotNull(reason);
    }

    [Fact]
    public void TryCompact_WithANullFont_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfFontSubsetter.TryCompact(null, out _, out _));
    }

    /// <summary>
    /// <c>Subset</c>'s refusal names the alternative. A caller who hits the exception has to be
    /// able to find <c>TryCompact</c> from it, or the capability is invisible.
    /// </summary>
    [Fact]
    public void Subset_RefusingACffFont_NamesTryCompactInTheMessage()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => PdfFontSubsetter.Subset(OpenTypeCffFont(), "A"));

        Assert.Contains("TryCompact", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// An OpenType/CFF font carrying the layout tables the compaction is about. Built rather than
    /// loaded, per the project's self-contained rule — and carrying GSUB/GPOS/DSIG on purpose,
    /// because a font without them would prove nothing about dropping them.
    /// </summary>
    private static byte[] OpenTypeCffFont() => Font(
        ("CFF ", 2000), ("GSUB", 6000), ("GPOS", 5000), ("DSIG", 400),
        ("head", 54), ("hhea", 36), ("hmtx", 40), ("maxp", 6),
        ("OS/2", 96), ("post", 32), ("name", 80), ("cmap", 120));

    private static byte[] Font(params (string tag, int size)[] tables)
    {
        var built = new List<(string, byte[])>(tables.Length);
        foreach ((string tag, int size) in tables)
        {
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)(tag[0] + i);
            built.Add((tag, data));
        }
        return Tetitulz.Pdf.Core.Fonts.SfntBuilder.Build(
            Tetitulz.Pdf.Core.Fonts.SfntBuilder.VersionOpenTypeCff, built);
    }

    private static string[] Tags(byte[] font)
    {
        int n = (font[4] << 8) | font[5];
        var tags = new string[n];
        for (int i = 0; i < n; i++) tags[i] = Encoding.ASCII.GetString(font, 12 + i * 16, 4);
        return tags;
    }
}
