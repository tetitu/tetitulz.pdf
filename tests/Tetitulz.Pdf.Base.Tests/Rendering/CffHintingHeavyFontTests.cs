// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An embedded CFF program whose glyphs our evaluator finds heavier than
/// <see cref="PdfLimits.MaxCharstringStepsForHinting"/> is not handed to Skia through its OTF wrapper:
/// Skia ran a subroutine bomb at about 300 ms a glyph under no budget of ours, so a 20-glyph page took
/// 7.5 s, against 1.4 s drawn from our own outlines (07's DoS hunt, F15; the owner's choice, b).
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class CffHintingHeavyFontTests
{
    /// <summary>A width of 500, a filled 500-unit box, then a call into the subroutine bomb, then endchar.</summary>
    internal static readonly byte[] BoxThenBomb =
    {
        248, 136,              // width 500 (the advance the OTF draw reads)
        239, 239, 21,          // 100 100 rmoveto
        248, 136, 139, 5,      // 500 0 rlineto
        139, 248, 136, 5,      // 0 500 rlineto
        252, 136, 139, 5,      // -500 0 rlineto
        32, 29,                // callgsubr 0 (the bomb)
        14,                    // endchar
    };

    /// <summary>The box alone.</summary>
    internal static readonly byte[] Box = { 248, 136, 239, 239, 21, 248, 136, 139, 5, 139, 248, 136, 5, 252, 136, 139, 5, 14 };

    private static CffGlyphSource Source(byte[] glyph, int glyphs = 4) =>
        new CffGlyphSource(new CffFont(Enumerable.Repeat(glyph, glyphs).ToArray(), Array.Empty<byte[]>(),
            CharstringPageBudgetTests.BombSubrs(), Enumerable.Range(0, glyphs).ToDictionary(g => g, g => g), 0f, 0f));

    private static IReadOnlyList<CMapCodec.Code> Codes(params int[] cids) =>
        cids.Select(c => new CMapCodec.Code(c, c, 1)).ToArray();

    /// <summary>A pixel's red channel (RGBA8888, an opaque page).</summary>
    private static byte Red(RenderedPage page, int x, int y) => page.Pixels[y * page.Stride + x * 4];

    /// <summary>
    /// A run of heavy glyphs makes the program too heavy for hinting, and says so once however many runs
    /// ask; a run of real glyphs does not.
    /// </summary>
    [Fact]
    public void HeavyGlyphs_MakeTheProgramTooHeavyForHinting_ReportedOnce()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            PdfLimits.MaxCharstringStepsForHinting = 10_000;
            using var heavy = Source(BoxThenBomb);
            using var real = Source(Box);
            var answers = new List<bool>();

            var events = CharstringPageBudgetTests.Collect(() =>
            {
                answers.Add(heavy.IsTooHeavyForHinting(Codes(0, 1)));
                answers.Add(heavy.IsTooHeavyForHinting(Codes(2)));
                answers.Add(real.IsTooHeavyForHinting(Codes(0, 1, 2)));
            }, RenderDiagnosticKind.FontHintingSkipped);

            Assert.Equal(new[] { true, true, false }, answers.ToArray());
            Assert.Single(events);
            Assert.Contains("MaxCharstringStepsForHinting", events[0], StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A glyph the page's budget cut short makes the program too heavy too, though it ran fewer steps than
    /// the hinting ceiling: handed to Skia, the glyph our budget refused would run under none.
    /// </summary>
    [Fact]
    public void AGlyphCutByThePageBudget_KeepsTheProgramOffSkia()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            PdfLimits.MaxCharstringStepsForHinting = 1_000_000;
            using var source = Source(BoxThenBomb);
            bool heavy;
            using (CharstringWork.SpendFrom(new WorkBudget(30_000, "page")))
            {
                Assert.False(source.IsTooHeavyForHinting(Codes(0)));
                heavy = source.IsTooHeavyForHinting(Codes(1));
            }

            Assert.True(heavy);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The ceiling is a boundary: a glyph running fewer steps than it stays hinted, more does not.</summary>
    [Fact]
    public void TheHintingCeiling_IsABoundary()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            using var probe = Source(BoxThenBomb);
            probe.GetOutline(0);
            int steps = Type2CharstringEvaluator.Evaluate(new CffFont(new[] { BoxThenBomb }, Array.Empty<byte[]>(),
                CharstringPageBudgetTests.BombSubrs(), new Dictionary<int, int>(), 0f, 0f), 0).Steps;

            PdfLimits.MaxCharstringStepsForHinting = steps;
            using (var atTheCeiling = Source(BoxThenBomb))
                Assert.False(atTheCeiling.IsTooHeavyForHinting(Codes(0)));

            PdfLimits.MaxCharstringStepsForHinting = steps - 1;
            using (var past = Source(BoxThenBomb))
                Assert.True(past.IsTooHeavyForHinting(Codes(0)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// End to end: a page of three box-then-bomb glyphs, the page's budget a glyph and a half. Drawn from our
    /// outlines, the first box is drawn and the two the budget cut short are not; handed to Skia, all
    /// three would be drawn alike (or none). The fallback is reported.
    /// </summary>
    [Fact]
    public void AHeavyProgram_IsDrawnFromOurOutlines_NotThroughSkia()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            PdfLimits.MaxCharstringStepsForHinting = 10_000;
            PdfLimits.MaxCharstringStepsPerPage = 30_000;
            byte[] pdf = CharstringPageBudgetTests.PagePdf(BoxThenBomb);
            RenderedPage page = null;

            var events = CharstringPageBudgetTests.Collect(
                () => page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72),
                RenderDiagnosticKind.FontHintingSkipped);

            Assert.Single(events);
            // At 24 pt a glyph's box runs from 2.4 pt to 14.4 pt of its 12 pt cell, from y 2.4 pt to 14.4 pt
            // above the baseline at 700: its centre is 8.4 pt in and 8.4 pt up.
            bool Inked(int glyph) => Red(page, 20 + 12 * glyph + 8, 792 - 700 - 8) < 128;
            Assert.True(Inked(0), "the first glyph, inside the page's budget, was not drawn");
            Assert.False(Inked(1), "the second glyph, cut short by the page's budget, was drawn");
            Assert.False(Inked(2), "the third glyph, past the page's budget, was drawn");
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A page showing glyphs 1–3 of an OpenType program (the bare CFF wrapped, embedded as /FontFile3
    /// /OpenType) through a CID-keyed font with Identity-H — a program SkiaSharp loads itself, so Skia
    /// draws it with no CFF fallback of ours (07's DoS hunt, F15: about 15 ms a hostile glyph).
    /// </summary>
    internal static byte[] OpenTypePdf(byte[] glyph, string fontFileKey = "FontFile3")
    {
        byte[] cff = CharstringPageBudgetTests.BareCff(glyph);
        byte[] otf = CffOtfWrapper.TryBuild(cff, CffParser.Parse(cff));
        string content = "BT /F1 24 Tf 20 700 Td <000100020003> Tj ET";
        var body = new MemoryStream();
        var offsets = new List<long>();
        void W(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); body.Write(b, 0, b.Length); }
        void Obj(string head, byte[] stream = null)
        {
            offsets.Add(body.Length);
            W(offsets.Count + " 0 obj\n" + head);
            if (stream != null) { W("\nstream\n"); body.Write(stream, 0, stream.Length); W("\nendstream"); }
            W("\nendobj\n");
        }
        W("%PDF-1.7\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj($"<< /Length {content.Length} >>", System.Text.Encoding.ASCII.GetBytes(content));
        Obj("<< /Type /Font /Subtype /Type0 /BaseFont /Bomb /Encoding /Identity-H /DescendantFonts [6 0 R] >>");
        Obj("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Bomb /CIDSystemInfo << /Registry (Adobe) " +
            "/Ordering (Identity) /Supplement 0 >> /DW 500 /FontDescriptor 7 0 R >>");
        Obj("<< /Type /FontDescriptor /FontName /Bomb /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 " +
            "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /" + fontFileKey + " 8 0 R >>");
        Obj(fontFileKey == "FontFile3" ? $"<< /Subtype /OpenType /Length {otf.Length} >>" : $"<< /Length {otf.Length} >>", otf);
        long xref = body.Length;
        W("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (long o in offsets) W(o.ToString("D10") + " 00000 n \n");
        W("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        return body.ToArray();
    }

    /// <summary>
    /// The direct path: a heavy OpenType program Skia loaded itself is drawn from our outlines too, and
    /// reported. The page's budget is less than one glyph's run, so every glyph is cut short and ours draw
    /// nothing, where Skia would draw every box (the control below) — whichever glyph ids the
    /// typeface's own lookup resolves the codes to, since those are the ids both draw.
    /// </summary>
    [Theory]
    [InlineData("FontFile3")]
    [InlineData("FontFile2")] // an OpenType CFF program under the TrueType key: recognised by its 'OTTO'
    public void AHeavyOpenTypeProgram_IsDrawnFromOurOutlines_NotThroughSkia(string fontFileKey)
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            PdfLimits.MaxCharstringStepsForHinting = 10_000;
            PdfLimits.MaxCharstringStepsPerPage = 15_000;
            byte[] pdf = OpenTypePdf(BoxThenBomb, fontFileKey);
            RenderedPage page = null;

            var events = CharstringPageBudgetTests.Collect(
                () => page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72),
                RenderDiagnosticKind.FontHintingSkipped);

            Assert.Single(events);
            for (int glyph = 0; glyph < 3; glyph++)
                Assert.False(Red(page, 20 + 12 * glyph + 8, 792 - 700 - 8) < 128, "glyph " + glyph + ", cut short by the page's budget, was drawn");

            // And our outlines are drawn: with a budget a glyph and a half, the first box is.
            PdfLimits.MaxCharstringStepsPerPage = 30_000;
            page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
            Assert.True(Red(page, 20 + 8, 792 - 700 - 8) < 128, "the first glyph, inside the page's budget, was not drawn");
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: a real OpenType program stays on Skia, all three boxes drawn, nothing said.</summary>
    [Theory]
    [InlineData("FontFile3")]
    [InlineData("FontFile2")]
    public void ARealOpenTypeProgram_StaysOnSkia(string fontFileKey)
    {
        byte[] pdf = OpenTypePdf(Box, fontFileKey);
        RenderedPage page = null;

        var events = CharstringPageBudgetTests.Collect(
            () => page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72),
            RenderDiagnosticKind.FontHintingSkipped);

        Assert.Empty(events);
        for (int glyph = 0; glyph < 3; glyph++)
            Assert.True(Red(page, 20 + 12 * glyph + 8, 792 - 700 - 8) < 128, "glyph " + glyph + " was not drawn");
    }

    // ---- Type 1: the same rule, asked of every glyph where Skia accepts the PFB wrap (Linux) ----

    /// <summary>An in-memory Type 1 program of <paramref name="glyphs"/> glyphs, each running <paramref name="charstring"/>, over nested subrs.</summary>
    private static Core.Fonts.Type1.Type1Font Type1(int glyphs, byte[] charstring)
    {
        var subrs = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var s = new List<byte>();
            for (int k = 0; k < 64; k++) { s.Add((byte)(i + 1 + 139)); s.Add(10); } // push (i+1); callsubr
            s.Add(11);
            subrs.Add(s.ToArray());
        }
        subrs.Add(new byte[] { 11 });
        var charStrings = Enumerable.Range(0, glyphs).ToDictionary(g => "g" + g, g => charstring);
        return new Core.Fonts.Type1.Type1Font(new string[256], charStrings, subrs);
    }

    private static readonly byte[] Type1Bomb = { 139, 139, 13, 139, 10, 14 };     // 0 0 hsbw; callsubr 0; endchar
    private static readonly byte[] Type1Plain = { 139, 139, 13, 139, 139, 21, 14 }; // 0 0 hsbw; 0 0 rmoveto; endchar

    /// <summary>A program with one heavy glyph is too heavy; the real fixture program is not.</summary>
    [Fact]
    public void AType1ProgramWithAHeavyGlyph_IsTooHeavyForHinting()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 20_000;
            PdfLimits.MaxCharstringStepsForHinting = 10_000;
            (byte[] program, int l1, int l2) = EmbeddedType1Fixture.MinimalProgram();

            Assert.True(Type1GlyphSource.IsTooHeavyForHinting(Type1(1, Type1Bomb)));
            Assert.False(Type1GlyphSource.IsTooHeavyForHinting(Core.Fonts.Type1.Type1Parser.Parse(program, l1, l2)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// Glyphs each under the hinting ceiling that together run past the font's budget make the program too
    /// heavy; the same glyphs under a budget that holds them do not.
    /// </summary>
    [Fact]
    public void AType1ProgramPastTheFontsBudget_IsTooHeavyForHinting()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 5_000;
            PdfLimits.MaxCharstringStepsForHinting = 1_000_000;
            PdfLimits.MaxCharstringStepsPerFont = 40_000;
            Assert.True(Type1GlyphSource.IsTooHeavyForHinting(Type1(10, Type1Bomb)));

            PdfLimits.MaxCharstringStepsPerFont = 60_000;
            Assert.False(Type1GlyphSource.IsTooHeavyForHinting(Type1(10, Type1Bomb)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The check spends the open page's budget too: a page that has none left finds even a plain program
    /// too heavy to check, and so keeps it off Skia; with budget left, the plain program passes.
    /// </summary>
    [Fact]
    public void TheType1Check_SpendsTheOpenPagesBudget()
    {
        var plain = Type1(50, Type1Plain);
        using (CharstringWork.SpendFrom(new WorkBudget(20, "page")))
            Assert.True(Type1GlyphSource.IsTooHeavyForHinting(plain));
        using (CharstringWork.SpendFrom(new WorkBudget(1_000_000, "page")))
            Assert.False(Type1GlyphSource.IsTooHeavyForHinting(plain));
    }

    /// <summary>The control: a real program on the same page stays on Skia, all three boxes drawn, nothing said.</summary>
    [Fact]
    public void ARealProgram_StaysOnSkia()
    {
        byte[] pdf = CharstringPageBudgetTests.PagePdf(Box);
        RenderedPage page = null;

        var events = CharstringPageBudgetTests.Collect(
            () => page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72),
            RenderDiagnosticKind.FontHintingSkipped);

        Assert.Empty(events);
        for (int glyph = 0; glyph < 3; glyph++)
            Assert.True(Red(page, 20 + 12 * glyph + 8, 792 - 700 - 8) < 128, "glyph " + glyph + " was not drawn");
    }
}
