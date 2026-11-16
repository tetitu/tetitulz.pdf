// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Charstring steps are bounded across a page's glyphs (<see cref="PdfLimits.MaxCharstringStepsPerPage"/>) and,
/// for the CFF width and seac scans, across a font program's (<see cref="PdfLimits.MaxCharstringStepsPerFont"/>),
/// beside the per-glyph <see cref="PdfLimits.MaxCharstringSteps"/>: every glyph of a font calling one
/// subroutine that runs to the per-glyph cap cost 35 ms a glyph, so a page of a thousand was 35 s
/// (07's DoS hunt, F14).
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class CharstringPageBudgetTests
{
    private const int K = 64;

    /// <summary>Eight levels of global subroutines, each calling the next <see cref="K"/> times, then a leaf.</summary>
    internal static List<byte[]> BombSubrs()
    {
        var subrs = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var s = new List<byte>();
            for (int k = 0; k < K; k++) { s.Add((byte)(i + 1 - 107 + 139)); s.Add(29); } // callgsubr (i+1)
            s.Add(11);
            subrs.Add(s.ToArray());
        }
        subrs.Add(new byte[] { 11 });
        return subrs;
    }

    /// <summary>A glyph that calls the bomb: push (0 − 107), callgsubr, endchar.</summary>
    private static readonly byte[] BombGlyph = { 32, 29, 14 };

    /// <summary>A glyph that draws a box: 100 100 rmoveto 50 0 rlineto endchar.</summary>
    private static readonly byte[] BoxGlyph = { 239, 239, 21, 189, 139, 5, 14 };

    private static CffFont Font(int glyphs, byte[] glyph) =>
        new CffFont(Enumerable.Repeat(glyph, glyphs).ToArray(), Array.Empty<byte[]>(), BombSubrs(),
            Enumerable.Range(0, glyphs).ToDictionary(g => g, g => g), 0f, 0f);

    private static List<string> Collect(Action run) => Collect(run, RenderDiagnosticKind.GlyphOutlineWorkExceeded);

    internal static List<string> Collect(Action run, RenderDiagnosticKind wanted)
    {
        var events = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != own || kind != wanted) return;
            events.Add(detail);
        }
        RenderDiagnostics.OnEvent += Sink;
        try { run(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return events;
    }

    /// <summary>
    /// Under a page budget of two and a half glyphs' cap, two glyphs are drawn and cached, the third is cut
    /// short: drawn as nothing, not cached, reported once however many glyphs follow; the next page draws it.
    /// </summary>
    [Fact]
    public void ACffGlyphPastThePageBudget_DrawsNothing_IsNotCached_AndIsReportedOnce()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            using var source = new CffGlyphSource(Font(10, BombGlyph));
            var drawn = new List<bool>();

            CharstringWork.StepsRun = 0;
            var events = Collect(() =>
            {
                using (CharstringWork.SpendFrom(new WorkBudget(25_000, "page")))
                    for (int code = 0; code < 10; code++) drawn.Add(source.GetOutline(code).HasValue);
            });

            Assert.Equal(new[] { true, true, false, false, false, false, false, false, false, false }, drawn.ToArray());
            Assert.Single(events);
            Assert.Contains("MaxCharstringStepsPerPage", events[0], StringComparison.Ordinal);
            // The glyphs ran the budget and, past it, a step a call level as each unwound — not the cap
            // each (eight more glyphs × 10 000).
            Assert.InRange(CharstringWork.StepsRun, 25_000, 25_000 + 10 * (PdfLimits.MaxCharstringCallDepth + 2));

            using (CharstringWork.SpendFrom(new WorkBudget(25_000, "next page")))
                Assert.True(source.GetOutline(2).HasValue);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A page drawn inside another (a scope over a budget of its own) reports its own refusal, though the
    /// enclosing page has reported one already; the enclosing page's stays reported after it.
    /// </summary>
    [Fact]
    public void AnInnerPagesRefusal_IsReportedThoughTheOuterPageReportedOne()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            using var outer = new CffGlyphSource(Font(4, BombGlyph));
            using var inner = new CffGlyphSource(Font(4, BombGlyph));

            var events = Collect(() =>
            {
                using (CharstringWork.SpendFrom(new WorkBudget(5_000, "outer page")))
                {
                    outer.GetOutline(0);
                    using (CharstringWork.SpendFrom(new WorkBudget(5_000, "inner page")))
                        inner.GetOutline(0);
                    outer.GetOutline(1);
                }
            });

            Assert.Equal(2, events.Count);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A glyph stopped at the per-glyph cap is too heavy for Skia whatever the cap: with
    /// <see cref="PdfLimits.MaxCharstringSteps"/> set below <see cref="PdfLimits.MaxCharstringStepsForHinting"/>,
    /// a bomb glyph stops at the cap, under the hinting threshold — and was judged light, so the
    /// platform's engine ran it whole (CoreText for hours). The control: boxes under the same cap are light.
    /// </summary>
    [Fact]
    public void AGlyphStoppedAtThePerGlyphCap_IsTooHeavyForSkia_WhateverTheCap()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            Assert.True(PdfLimits.MaxCharstringSteps < PdfLimits.MaxCharstringStepsForHinting);

            using var bombs = new CffGlyphSource(Font(3, BombGlyph));
            using var boxes = new CffGlyphSource(Font(3, BoxGlyph));
            Assert.True(bombs.IsTooHeavyForHinting(new ushort[] { 0, 1, 2 }));
            Assert.False(boxes.IsTooHeavyForHinting(new ushort[] { 0, 1, 2 }));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The Type 1 program's form of the same rule: a glyph stopped at the cap makes the program heavy.</summary>
    [Fact]
    public void AType1GlyphStoppedAtThePerGlyphCap_MakesTheProgramTooHeavyForSkia()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            var subrs = new List<byte[]>();
            for (int i = 0; i < 8; i++)
            {
                var s = new List<byte>();
                for (int k = 0; k < K; k++) { s.Add((byte)(i + 1 + 139)); s.Add(10); }
                s.Add(11);
                subrs.Add(s.ToArray());
            }
            subrs.Add(new byte[] { 11 });
            var encoding = new string[256];
            encoding[65] = "A";
            var bomb = new Type1Font(encoding,
                new Dictionary<string, byte[]> { ["A"] = new byte[] { 139, 139, 13, 139, 10, 14 } }, subrs);
            var box = new Type1Font(encoding,
                new Dictionary<string, byte[]> { ["A"] = new byte[] { 139, 239, 13, 239, 239, 21, 14 } },
                new List<byte[]>());

            Assert.True(Type1GlyphSource.IsTooHeavyForHinting(bomb));
            Assert.False(Type1GlyphSource.IsTooHeavyForHinting(box));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: real glyphs on a page under the budget are all drawn and nothing is said.</summary>
    [Fact]
    public void RealGlyphsUnderThePageBudget_AreAllDrawn()
    {
        using var source = new CffGlyphSource(Font(10, BoxGlyph));
        var outlines = new List<bool>();
        var events = Collect(() =>
        {
            using (CharstringWork.SpendFrom(new WorkBudget(PdfLimits.MaxCharstringStepsPerPage, "page")))
                for (int code = 0; code < 10; code++) outlines.Add(source.GetOutline(code) is { } o && !o.Path.IsEmpty);
        });

        Assert.All(outlines, Assert.True);
        Assert.Empty(events);
    }

    /// <summary>A Type 1 glyph past the page's budget is drawn as nothing and not cached, as a CFF one is.</summary>
    [Fact]
    public void AType1GlyphPastThePageBudget_DrawsNothing_AndIsNotCached()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            var subrs = new List<byte[]>();
            for (int i = 0; i < 8; i++)
            {
                var s = new List<byte>();
                for (int k = 0; k < K; k++) { s.Add((byte)(i + 1 + 139)); s.Add(10); }
                s.Add(11);
                subrs.Add(s.ToArray());
            }
            subrs.Add(new byte[] { 11 });
            var encoding = new string[256];
            var charStrings = new Dictionary<string, byte[]>();
            for (int code = 65; code < 70; code++)
            {
                encoding[code] = ((char)code).ToString();
                charStrings[encoding[code]] = new byte[] { 139, 139, 13, 139, 10, 14 };
            }
            using var source = new Type1GlyphSource(new Type1Font(encoding, charStrings, subrs));
            var drawn = new List<bool>();

            CharstringWork.StepsRun = 0;
            var events = Collect(() =>
            {
                using (CharstringWork.SpendFrom(new WorkBudget(15_000, "page")))
                    for (int code = 65; code < 70; code++) drawn.Add(source.GetOutline(code).HasValue);
            });

            Assert.Equal(new[] { true, false, false, false, false }, drawn.ToArray());
            Assert.Single(events);
            Assert.InRange(CharstringWork.StepsRun, 15_000, 15_000 + 5 * (PdfLimits.MaxCharstringCallDepth + 2));
            using (CharstringWork.SpendFrom(new WorkBudget(15_000, "next page")))
                Assert.True(source.GetOutline(66).HasValue);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// A font's width scans spend one account: once a few bomb glyphs have spent it, a real glyph's width
    /// is no longer read, and the steps spent are the account's, not glyphs × the cap.
    /// </summary>
    [Fact]
    public void TheFontsWidthScans_SpendOneAccount()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            PdfLimits.MaxCharstringStepsPerFont = 25_000;
            var glyphs = Enumerable.Repeat(BombGlyph, 10).Concat(new[] { new byte[] { 189, 139, 139, 21, 14 } }).ToArray();
            var font = new CffFont(glyphs, Array.Empty<byte[]>(), BombSubrs(),
                new Dictionary<int, int>(), 0f, 0f);
            var fresh = new CffFont(glyphs, Array.Empty<byte[]>(), BombSubrs(), new Dictionary<int, int>(), 0f, 0f);

            for (int gid = 0; gid < 10; gid++) Assert.False(font.TryGetAdvance(gid, out _));

            Assert.Equal(0, font.ScanSteps.Left);
            Assert.False(font.TryGetAdvance(10, out _));
            Assert.True(fresh.TryGetAdvance(10, out double advance));
            Assert.Equal(50.0, advance, 3);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    // ---- End to end: a page drawing a bare CFF program (FontFile3 /Type1C) ----

    private static byte[] Index(IReadOnlyList<byte[]> items)
    {
        var o = new List<byte> { (byte)(items.Count >> 8), (byte)items.Count };
        if (items.Count == 0) return o.ToArray();
        o.Add(4);
        int offset = 1;
        void Off(int v) { o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v); }
        Off(offset);
        foreach (var it in items) { offset += it.Length; Off(offset); }
        foreach (var it in items) o.AddRange(it);
        return o.ToArray();
    }

    private static byte[] Int5(int v) => new byte[] { 29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>A bare CFF of 120 bomb glyphs over the ISOAdobe charset and StandardEncoding (so 'A' is glyph 34).</summary>
    internal static byte[] BareCff(byte[] glyph = null)
    {
        byte[] header = { 1, 0, 4, 4 };
        byte[] name = Index(new[] { Encoding.ASCII.GetBytes("Bomb") });
        byte[] strings = Index(Array.Empty<byte[]>());
        byte[] gsubrs = Index(BombSubrs());
        byte[] charStrings = Index(Enumerable.Repeat(glyph ?? BombGlyph, 120).ToArray());
        byte[] priv = { 139, 20 }; // defaultWidthX 0

        int topLength = Index(new[] { new byte[5 + 1 + 5 + 5 + 1] }).Length;
        int charStringsAt = header.Length + name.Length + topLength + strings.Length + gsubrs.Length;
        int privateAt = charStringsAt + charStrings.Length;
        byte[] top = Int5(charStringsAt).Concat(new byte[] { 17 })
            .Concat(Int5(priv.Length)).Concat(Int5(privateAt)).Concat(new byte[] { 18 }).ToArray();

        return header.Concat(name).Concat(Index(new[] { top })).Concat(strings).Concat(gsubrs)
            .Concat(charStrings).Concat(priv).ToArray();
    }

    internal static byte[] PagePdf(byte[] glyph = null)
    {
        byte[] cff = BareCff(glyph);
        string content = "BT /F1 24 Tf 20 700 Td (ABC) Tj ET";
        var body = new MemoryStream();
        var offsets = new List<long>();
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); body.Write(b, 0, b.Length); }
        void Obj(string head, byte[] stream = null)
        {
            offsets.Add(body.Length);
            W(offsets.Count + " 0 obj\n" + head);
            if (stream != null) { W("\nstream\n"); body.Write(stream, 0, stream.Length); W("\nendstream"); }
            W("\nendobj\n");
        }
        W("%PDF-1.4\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj($"<< /Length {content.Length} >>", Encoding.ASCII.GetBytes(content));
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Bomb /FirstChar 65 /LastChar 67 /Widths [500 500 500] /FontDescriptor 6 0 R >>");
        Obj("<< /Type /FontDescriptor /FontName /Bomb /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 " +
            "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 7 0 R >>");
        Obj($"<< /Subtype /Type1C /Length {cff.Length} >>", cff);
        long xref = body.Length;
        W("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (long o in offsets) W(o.ToString("D10") + " 00000 n \n");
        W("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        return body.ToArray();
    }

    /// <summary>
    /// The raster renderer opens the page's budget: a page of three bomb glyphs under a budget of two and a
    /// half glyphs' cap reports the refusal once; under the default budget the same page says nothing.
    /// </summary>
    [Fact]
    public void TheRasterRenderer_SpendsThePageBudget()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            byte[] pdf = PagePdf();

            Assert.Empty(Collect(() => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 36)));

            PdfLimits.MaxCharstringStepsPerPage = 25_000;
            Assert.Single(Collect(() => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 36)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The SVG renderer opens the page's budget too.</summary>
    [Fact]
    public void TheSvgRenderer_SpendsThePageBudget()
    {
        try
        {
            PdfLimits.MaxCharstringSteps = 10_000;
            byte[] pdf = PagePdf();

            Assert.Empty(Collect(() => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0)));

            PdfLimits.MaxCharstringStepsPerPage = 25_000;
            Assert.Single(Collect(() => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
