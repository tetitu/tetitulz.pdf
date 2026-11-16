// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decisions in the palette, stencil, pattern, reporting and ExtGState-font helpers that the
/// second Rendering sweep found nothing asserting.
/// </summary>
public class RenderingBatch2DecisionTests
{
    private static PdfArray Arr(params PdfObject[] items)
    {
        var a = new PdfArray();
        foreach (var o in items) a.Add(o);
        return a;
    }

    /// <summary>An <c>/Indexed</c> space with no base is not one.</summary>
    [Fact]
    public void IndexedPalette_ASpaceWithNoBaseIsRefused()
    {
        Assert.False(IndexedPalette.TryReadSpace(null, Arr(PdfName.Indexed), out _, out _, out _));
        Assert.True(IndexedPalette.TryReadSpace(null, Arr(PdfName.Indexed, PdfName.DeviceRGB), out _, out int hival, out _));
        Assert.Equal(255, hival);
    }

    /// <summary>
    /// An entry the lookup table does not hold in full is left black — never read past the table's
    /// end. Two RGB entries over five bytes: the first is whole, the second has two of its three.
    /// </summary>
    [Fact]
    public void IndexedPalette_AShortTableLeavesTheMissingEntryBlack()
    {
        byte[] table = { 255, 0, 0, 0, 255 };
        SKColor[] palette = IndexedPalette.Build(null, PdfName.DeviceRGB, table, 1, default);
        Assert.Equal(new SKColor(255, 0, 0), palette[0]);
        Assert.Equal(SKColors.Black, palette[1]);
    }

    /// <summary>
    /// A Lab base's entries span the space's own <c>/Range</c> for a* and b*, not the default
    /// ±100 — a* is read inside the space's range, and byte 0 is the bottom of b*'s.
    /// </summary>
    [Fact]
    public void IndexedPalette_ALabBaseUsesItsOwnRange()
    {
        var whitePoint = Arr(new PdfNumber(0.9642), new PdfNumber(1), new PdfNumber(0.8249));
        var cie = new PdfDictionary();
        cie[PdfName.WhitePoint] = whitePoint;
        cie[PdfName.Range] = Arr(new PdfNumber(-20), new PdfNumber(20), new PdfNumber(-30), new PdfNumber(30));
        var lab = Arr(new PdfName(Tetitulz.Pdf.Core.Constants.PdfColorSpaceNames.Lab), cie);

        SKColor[] palette = IndexedPalette.Build(null, lab, new byte[] { 128, 64, 0 }, 0, default);
        Assert.Equal(PdfColor.LabToSRgb(128 / 255f * 100f, -20f + 64 / 255f * 40f, -30f, cie), palette[0]);
        // An a* inside the range: read over the default ±100 the same byte is −49.8, which the
        // conversion clamps to the range's −20 — a different colour. (A byte at the range's edge
        // cannot tell the two apart: the conversion clamps either reading to the same bound.)
        Assert.NotEqual(PdfColor.LabToSRgb(128 / 255f * 100f, -100f + 64 / 255f * 200f, -30f, cie), palette[0]);
    }

    /// <summary>A document to resolve through — the palette's tint path needs one.</summary>
    private static Tetitulz.Pdf.Core.Document.PdfDocument MinimalDocument()
    {
        string body = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n";
        int xref = body.Length;
        string pdf = body + "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
                     $"trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n";
        return Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes(pdf)));
    }

    /// <summary>A DeviceN space of two inks, shown through a transform keeping the second as grey.</summary>
    private static PdfArray TwoInksSecondAsGrey()
    {
        var tint = new PdfStream { RawData = System.Text.Encoding.ASCII.GetBytes("{ exch pop }") };
        tint[PdfName.FunctionType] = new PdfNumber(4);
        tint[PdfName.Domain] = Arr(new PdfNumber(0), new PdfNumber(1), new PdfNumber(0), new PdfNumber(1));
        tint[PdfName.Range] = Arr(new PdfNumber(0), new PdfNumber(1));
        return Arr(new PdfName(Tetitulz.Pdf.Core.Constants.PdfColorSpaceNames.DeviceN), Arr(new PdfName("Ink1"), new PdfName("Ink2")), PdfName.DeviceGray, tint);
    }

    /// <summary>
    /// A DeviceN base names its inks, and each palette entry holds one byte per ink: entry 0
    /// (0, 255) shows white and entry 1 (255, 0) black.
    /// </summary>
    [Fact]
    public void IndexedPalette_ADeviceNBaseReadsOneBytePerInk()
    {
        using var doc = MinimalDocument();
        SKColor[] palette = IndexedPalette.Build(doc, TwoInksSecondAsGrey(), new byte[] { 0, 255, 255, 0 }, 1, default);
        Assert.Equal(SKColors.White, palette[0]);
        Assert.Equal(SKColors.Black, palette[1]);
    }

    /// <summary>
    /// Through a tint transform too, an entry the table does not hold in full is left black and
    /// never read past the end: three bytes hold one two-ink entry and half of another.
    /// </summary>
    [Fact]
    public void IndexedPalette_AShortTintTableLeavesTheMissingEntryBlack()
    {
        using var doc = MinimalDocument();
        SKColor[] palette = IndexedPalette.Build(doc, TwoInksSecondAsGrey(), new byte[] { 0, 255, 255 }, 1, default);
        Assert.Equal(SKColors.White, palette[0]);
        Assert.Equal(SKColors.Black, palette[1]);
    }

    /// <summary>
    /// An inline image says it is a stencil with <c>/IM</c> (§8.9.7 Table 91), as an XObject does with
    /// <c>/ImageMask</c>; either is read.
    /// </summary>
    [Fact]
    public void ImageStencil_TheInlineAbbreviationIsRead()
    {
        var inline = new PdfDictionary();
        inline[PdfName.IM] = PdfBoolean.True;
        Assert.True(ImageStencil.IsStencilMask(inline, null));
        Assert.False(ImageStencil.IsStencilMask(new PdfDictionary(), null));
    }

    /// <summary>
    /// A stencil's decode inverts only when its first value exceeds its second — <c>[1 0]</c>, or
    /// <c>/D</c> as an inline image writes it; <c>[0 1]</c> and a degenerate <c>[1 1]</c> do not.
    /// </summary>
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, false)]
    public void ImageStencil_DecodeInverts(int d0, int d1, bool inverts)
    {
        var inline = new PdfDictionary();
        inline[PdfName.D] = Arr(new PdfNumber(d0), new PdfNumber(d1));
        Assert.Equal(inverts, ImageStencil.DecodeInverts(inline, null));
    }

    /// <summary>
    /// A decode array's elements may be indirect (§7.3.10 applies to every value): <c>[3 0 R 0]</c>
    /// with object 3 holding <c>1</c> is <c>[1 0]</c>, and inverts.
    /// </summary>
    [Fact]
    public void ImageStencil_AnIndirectDecodeElementIsResolved()
    {
        string body = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n3 0 obj\n1\nendobj\n";
        int xref = body.Length;
        string pdf = body + "xref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000110 00000 n \n" +
                     $"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n";
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes(pdf)));

        var stencil = new PdfDictionary();
        stencil[PdfName.Decode] = Arr(new PdfReference(3, 0), new PdfNumber(0));
        Assert.True(ImageStencil.DecodeInverts(stencil, doc));
    }

    /// <summary>
    /// A mask within the per-side limit can still exceed the pixel-area limit, and is refused for
    /// it; one pixel narrower in area is accepted.
    /// </summary>
    [Fact]
    public void ImageStencil_TheAreaLimitIsItsOwn()
    {
        int w = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxImageDimension;
        long area = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxImagePixelArea;
        int h = (int)(area / w) + 1;
        Assert.True(h <= w, "the fixture needs a shape inside the side limit and over the area limit");

        Assert.False(ImageStencil.MaskGeometryWithinLimits(w, h));
        Assert.True(ImageStencil.MaskGeometryWithinLimits(w, h - 1));
    }

    /// <summary>The reports this thread makes while <paramref name="act"/> runs, with their details.</summary>
    private static System.Collections.Generic.List<string> ReportsDuring(System.Action act)
    {
        var seen = new System.Collections.Generic.List<string>();
        int own = System.Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (System.Environment.CurrentManagedThreadId == own) seen.Add(detail);
        }
        RenderDiagnostics.OnEvent += Sink;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return seen;
    }

    /// <summary>
    /// An ignored graphics-state parameter is reported once per content stream, however many
    /// ExtGStates carry it; a different parameter is reported in its own right.
    /// </summary>
    [Fact]
    public void IgnoredParameter_IsReportedOncePerParameter()
    {
        var reported = IgnoredGraphicsStateParameter.None;
        var reports = ReportsDuring(() =>
        {
            IgnoredParameterReporting.ReportOnce(ref reported, IgnoredGraphicsStateParameter.Halftone, "probe: ");
            IgnoredParameterReporting.ReportOnce(ref reported, IgnoredGraphicsStateParameter.Halftone, "probe: ");
            IgnoredParameterReporting.ReportOnce(ref reported, IgnoredGraphicsStateParameter.StrokeAdjustment, "probe: ");
        });
        Assert.Equal(2, reports.Count);
        Assert.Contains("Halftone", reports[0]);
        Assert.Contains("Stroke adjustment", reports[1]);
    }

    /// <summary>An ExtGState whose <c>/Font</c> is the given entry, and resources naming F1 → object 5.</summary>
    private static (PdfDictionary ExtGs, PdfDictionary Resources) FontState(PdfArray entry)
    {
        var gs = new PdfDictionary();
        gs[PdfName.Font] = entry;
        var fonts = new PdfDictionary();
        fonts[new PdfName("F1")] = new PdfReference(5, 0);
        var resources = new PdfDictionary();
        resources[PdfName.Font] = fonts;
        return (gs, resources);
    }

    /// <summary>
    /// An ExtGState <c>/Font</c> is <c>[font size]</c> (Table 57): a one-element entry is not read; a
    /// reference to a font the resources name selects that name, with its size; a size alone is
    /// still a size; and a font the resources do not name leaves only the size.
    /// </summary>
    [Fact]
    public void ExtGStateFont_TheEntryAndItsResourceName()
    {
        var (shortGs, res) = FontState(Arr(new PdfReference(5, 0)));
        Assert.False(ExtGStateFont.TryRead(null, shortGs, res, "", out _, out _));

        var (named, res2) = FontState(Arr(new PdfReference(5, 0), new PdfNumber(14)));
        Assert.True(ExtGStateFont.TryRead(null, named, res2, "", out string name, out float? size));
        Assert.Equal("F1", name);
        Assert.Equal(14f, size);

        var (unnamed, res3) = FontState(Arr(new PdfReference(9, 0), new PdfNumber(10)));
        Assert.True(ExtGStateFont.TryRead(null, unnamed, res3, "", out string none, out float? onlySize));
        Assert.Null(none);
        Assert.Equal(10f, onlySize);
    }

    /// <summary>
    /// A font given as the very dictionary the resources hold — not a reference to it — selects
    /// the resource that holds it.
    /// </summary>
    [Fact]
    public void ExtGStateFont_TheSameDictionarySelectsItsResource()
    {
        var font = new PdfDictionary();
        font[PdfName.Type] = PdfName.Font;
        var fonts = new PdfDictionary();
        fonts[new PdfName("F2")] = font;
        var resources = new PdfDictionary();
        resources[PdfName.Font] = fonts;
        var gs = new PdfDictionary();
        gs[PdfName.Font] = Arr(font, new PdfNumber(9));

        Assert.True(ExtGStateFont.TryRead(null, gs, resources, "", out string name, out _));
        Assert.Equal("F2", name);
    }

    /// <summary>
    /// A CalGray base keeps its gamma — it is not folded into DeviceGray, which would discard it.
    /// </summary>
    [Fact]
    public void IndexedPalette_ACalGrayBaseKeepsItsGamma()
    {
        var cie = new PdfDictionary();
        cie[PdfName.WhitePoint] = Arr(new PdfNumber(0.9505), new PdfNumber(1), new PdfNumber(1.089));
        cie[PdfName.Gamma] = new PdfNumber(2.2);

        SKColor[] palette = IndexedPalette.Build(null, Arr(new PdfName(Tetitulz.Pdf.Core.Constants.PdfColorSpaceNames.CalGray), cie), new byte[] { 128 }, 0, default);
        Assert.Equal(PdfColor.CalGrayToSRgb(128 / 255f, cie), palette[0]);
        Assert.NotEqual(new SKColor(128, 128, 128), palette[0]);
    }
}
