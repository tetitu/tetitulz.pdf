// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="FontCache"/>'s key, for the shape most producers emit: a <c>/Resources</c> dictionary
/// per page, every one naming the <b>same</b> indirect font object.
/// </summary>
/// <remarks>
/// <para>The key used to be <c>(identity hash of the resources dictionary, resource name)</c>, so
/// each page produced a distinct entry and the font program was decoded once per page. It is now
/// the font's own object number where the resources name it indirectly.</para>
///
/// <para><b>The font has to be embedded for this to be observable.</b> A standard-14 name resolves
/// through <c>SKTypeface.FromFamilyName</c>, and Skia may hand back the same instance for repeated
/// calls — so two cache entries would return reference-equal typefaces and the assertion would hold
/// with the feature absent. <c>SKTypeface.FromData</c> has no such sharing, so an embedded program
/// is what makes "one entry" and "two entries" tell apart.</para>
///
/// <para><b>This is not a performance claim.</b> The measured effect on
/// <c>RenderBenchmarks.FiftyPages_PerPageResources_All</c> was inside the error bars, because that
/// fixture's CFF program is a few hundred bytes. What the key change buys is that the identity-hash
/// collision documented on <c>MakeKey</c> — accepted for "the handful of resource dictionaries
/// alive during a single render" — stops applying to a case that is one dictionary per page.</para>
/// </remarks>
public class FontCacheKeySharingTests
{
    /// <summary>Two pages, two resource dictionaries, one font object — one cache entry.</summary>
    [Fact]
    public void PagesWithTheirOwnResourcesShareOneEntryForTheSameFontObject()
    {
        using var doc = PdfDocument.Open(new MemoryStream(TwoPagesOwnResources(), false));
        using var cache = new FontCache();

        var r0 = doc.Pages[0].Dictionary[PdfName.Resources] as PdfDictionary;
        var r1 = doc.Pages[1].Dictionary[PdfName.Resources] as PdfDictionary;

        Assert.NotNull(r0);
        Assert.NotNull(r1);
        // The premise: they really are distinct dictionary objects naming one font.
        Assert.False(ReferenceEquals(r0, r1), "the fixture did not give each page its own /Resources");

        Assert.NotNull(cache.GetTypeface("F1", r0, doc));
        Assert.NotNull(cache.GetTypeface("F1", r1, doc));

        // The entry count, NOT the returned typeface. SkiaSharp hands back the same instance for
        // identical program bytes, so comparing typefaces passes with the key change reverted —
        // the first version of this test did exactly that and its mutation survived. What
        // duplicate entries cost is the duplicated managed work behind them, and only the count
        // sees it.
        Assert.Equal(1, cache.EntryCount);
    }

    /// <summary>
    /// The control, and the reason the key still carries the resource name in its fallback form: a
    /// second font object must NOT share the first's entry.
    /// </summary>
    [Fact]
    public void ADifferentFontObjectDoesNotShareTheEntry()
    {
        using var doc = PdfDocument.Open(new MemoryStream(TwoPagesOwnResources(secondFontDistinct: true), false));
        using var cache = new FontCache();

        var r0 = doc.Pages[0].Dictionary[PdfName.Resources] as PdfDictionary;
        var r1 = doc.Pages[1].Dictionary[PdfName.Resources] as PdfDictionary;

        Assert.NotNull(cache.GetTypeface("F1", r0, doc));
        Assert.NotNull(cache.GetTypeface("F1", r1, doc));

        Assert.Equal(2, cache.EntryCount);
    }

    /// <summary>
    /// Two pages with their own <c>/Resources</c>. Both name <c>/F1</c>; the second names object 4
    /// as well unless <paramref name="secondFontDistinct"/> asks for a separate font object 7.
    /// </summary>
    private static byte[] TwoPagesOwnResources(bool secondFontDistinct = false)
    {
        byte[] cff = EmbeddedCffFixture.MinimalCffProgram();
        string content = "BT /F1 12 Tf 20 700 Td (A) Tj ET\n";
        byte[] contentBytes = Encoding.ASCII.GetBytes(content);

        var file = new MemoryStream();
        var offsets = new long[secondFontDistinct ? 10 : 8];
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        offsets[1] = file.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [5 0 R 6 0 R] /Count 2 >>\nendobj\n");

        // 3 = descriptor, 4 = font, 7/8/9 = the second font's objects when asked for.
        offsets[3] = file.Position;
        Write("3 0 obj\n<< /Type /FontDescriptor /FontName /BenchCff /Flags 4 "
            + "/FontBBox [0 0 500 400] /ItalicAngle 0 /Ascent 400 /Descent 0 /CapHeight 400 "
            + "/StemV 80 /FontFile3 " + (secondFontDistinct ? "8" : "7") + " 0 R >>\nendobj\n");
        offsets[4] = file.Position;
        Write("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /BenchCff /FirstChar 65 "
            + "/LastChar 65 /Widths [500] /FontDescriptor 3 0 R >>\nendobj\n");

        string secondFontRef = secondFontDistinct ? "9 0 R" : "4 0 R";
        offsets[5] = file.Position;
        Write("5 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>\nendobj\n");
        offsets[6] = file.Position;
        Write("6 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 " + secondFontRef + " >> >> /Contents 7 0 R >>\nendobj\n");

        offsets[7] = file.Position;
        Write($"7 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
        file.Write(contentBytes, 0, contentBytes.Length);
        Write("\nendstream\nendobj\n");

        if (secondFontDistinct)
        {
            offsets[8] = file.Position;
            Write($"8 0 obj\n<< /Subtype /Type1C /Length {cff.Length} >>\nstream\n");
            file.Write(cff, 0, cff.Length);
            Write("\nendstream\nendobj\n");
            offsets[9] = file.Position;
            Write("9 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /BenchCff2 /FirstChar 65 "
                + "/LastChar 65 /Widths [500] /FontDescriptor 3 0 R >>\nendobj\n");
        }

        long xref = file.Position;
        int size = offsets.Length;
        Write($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}
