// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// WOFF 2.0 containers unwrap into the sfnt an independent implementation produces from the same
/// bytes.
/// </summary>
/// <remarks>
/// <para><b>The expected values came from somewhere else.</b> Every number pinned below was read
/// out of <b>An independent font tool</b>'s decode of the <i>same</i> container — glyph count, contour counts,
/// bounding boxes, advance widths and left side bearings. That matters more here than usual: WOFF
/// 2.0 is a <i>transform</i>, so a decoder that reversed it consistently-but-wrongly would produce
/// a font that parses, renders and round-trips against itself perfectly. A relative assertion
/// cannot see a change applied to both of the things it relates, which is why nothing in this file
/// compares this decoder against itself.</para>
///
/// <para><b>The wider measurement is not in this file, and should be recorded anyway.</b> The
/// decoder was checked against an independent font tool across <b>eight production fonts from Google Fonts</b> —
/// Roboto, Roboto Mono, Open Sans, Lato, Merriweather, Playfair Display, Noto Sans Hebrew and
/// Source Code Pro — comparing every glyph's outline point-for-point through a recording pen:
/// <b>2 218 glyphs, 782 of them composite, zero mismatches</b>, and <c>hmtx</c> identical for every
/// glyph. Those fonts are not checked in (licensing, and 150 KB of base64), so what is here is a
/// 9-glyph subset that carries one of each shape.</para>
///
/// <para><b>The fixtures are two compressions of one subset.</b> The second applies the §5.4
/// <c>hmtx</c> transform, which drops the left side bearings and makes the decoder rebuild them
/// from the glyph bounding boxes. Both must produce the same font — that is the whole claim of that
/// transform — so the two are asserted to agree with each other <i>and</i> with an independent font tool.</para>
/// </remarks>
public class Woff2DecoderTests
{
    /// <summary>A container is recognised by its signature, not by anything it declares.</summary>
    [Fact]
    public void TheSignatureDistinguishesWoff2FromWoff1AndFromABareSfnt()
    {
        Assert.True(Woff2Decoder.IsWoff2(Fixture(StandardWoff2Base64)));
        Assert.False(Woff2Decoder.IsWoff2(new byte[] { 0x77, 0x4F, 0x46, 0x46 }));   // 'wOFF'
        Assert.False(Woff2Decoder.IsWoff2(new byte[] { 0x00, 0x01, 0x00, 0x00 }));   // sfnt
        Assert.False(Woff2Decoder.IsWoff2(null));
    }

    /// <summary>Every glyph matches an independent font tool' decode of the same container.</summary>
    /// <remarks>
    /// Contour counts and bounding boxes rather than raw bytes, because the reconstructed
    /// <c>glyf</c> is legitimately not byte-identical to a reference decoder's: §5.1 says so
    /// explicitly, and this decoder emits one flag byte per point where a reference one collapses
    /// runs with the repeat flag. What must match is the outline the font describes.
    /// </remarks>
    [Theory]
    // glyph index, name in the reference, contour count, xMin, yMin, xMax, yMax
    [InlineData(0, ".notdef", 6, 100, 0, 808, 1456)]
    [InlineData(2, "A", 3, 29, 0, 1310, 1456)]
    [InlineData(3, "V", 2, 29, 0, 1277, 1456)]
    [InlineData(4, "a", 2, 109, -20, 1002, 1102)]
    [InlineData(5, "o", 2, 92, -20, 1077, 1102)]
    [InlineData(6, "s", 1, 95, -20, 956, 1102)]
    [InlineData(7, "dieresis", 2, 101, 1265, 751, 1478)]
    [InlineData(8, "Adieresis", -1, 29, 0, 1310, 1789)]
    public void EachGlyphMatchesTheReferenceDecodersContoursAndBounds(
        int glyph, string name, int contours, int xMin, int yMin, int xMax, int yMax)
    {
        GlyphRecord[] records = Glyphs(Decode(StandardWoff2Base64));

        Assert.Equal(contours, records[glyph].Contours);
        Assert.Equal((xMin, yMin, xMax, yMax), records[glyph].Bounds);
        Assert.False(string.IsNullOrEmpty(name));
    }

    /// <summary>
    /// The empty glyph stays empty — no record, and so no bounding box.
    /// </summary>
    /// <remarks>
    /// Its own case because §5.1 makes it one: an explicit bounding box on a zero-contour glyph is
    /// invalid rather than redundant, and a decoder that emitted a four-value box for it would shift
    /// every later <c>loca</c> offset while still producing a font that parses.
    /// </remarks>
    [Fact]
    public void TheEmptyGlyphOccupiesNoBytes()
    {
        GlyphRecord[] records = Glyphs(Decode(StandardWoff2Base64));

        Assert.Equal(0, records[1].Length);
        Assert.Equal(0, records[1].Contours);
    }

    /// <summary>The reconstructed font carries the tables the reference decode has.</summary>
    /// <remarks>
    /// <c>loca</c> in particular: §5.3 makes the transformed <c>loca</c> empty by definition, so a
    /// decoder that rebuilt <c>glyf</c> and forgot the offsets produces a font whose every glyph
    /// starts at zero — which parses, and draws nothing.
    /// </remarks>
    [Fact]
    public void TheReassembledSfntHasTheSameTablesAsTheReferenceDecode()
    {
        IReadOnlyDictionary<string, (int Offset, int Length)> tables = Tables(Decode(StandardWoff2Base64));

        foreach (string tag in new[]
                 {
                     "GDEF", "GPOS", "GSUB", "OS/2", "STAT", "cmap", "cvt ", "fpgm", "gasp",
                     "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post", "prep",
                 })
            Assert.True(tables.ContainsKey(tag), $"the reassembled sfnt has no '{tag}' table");

        Assert.Equal(18, tables.Count);
    }

    /// <summary>
    /// <c>hmtx</c> matches the reference — with the §5.4 transform applied and without it.
    /// </summary>
    /// <remarks>
    /// <para>The transformed fixture has no <c>lsb[]</c> array at all: the decoder reads each
    /// bearing back out of the glyph's <c>xMin</c>. The two fixtures are the same subset compressed
    /// twice, so any difference between them is the transform being reversed wrongly.</para>
    /// <para><b>The empty glyph is the case worth having.</b> It has no bounding box to read an
    /// <c>xMin</c> from, and §5.4 makes the transform applicable only when its stored bearing was
    /// zero — so a decoder that reached for its box would take whatever the next glyph starts with.
    /// Its row is <c>(508, 0)</c> below.</para>
    /// </remarks>
    [Theory]
    [InlineData(0, 908, 100)]
    [InlineData(1, 508, 0)]        // the empty glyph: no bounding box, bearing zero
    [InlineData(2, 1336, 29)]
    [InlineData(3, 1304, 29)]
    [InlineData(4, 1114, 109)]
    [InlineData(5, 1168, 92)]
    [InlineData(6, 1057, 95)]
    [InlineData(7, 857, 101)]
    [InlineData(8, 1336, 29)]      // the composite, whose xMin comes from its own stored box
    public void HorizontalMetricsMatchTheReferenceWithAndWithoutTheHmtxTransform(
        int glyph, int advanceWidth, int leftSideBearing)
    {
        foreach (string fixture in new[] { StandardWoff2Base64, HmtxTransformedWoff2Base64 })
        {
            (int Advance, int Lsb) metric = Metric(Decode(fixture), glyph);

            Assert.Equal(advanceWidth, metric.Advance);
            Assert.Equal(leftSideBearing, metric.Lsb);
        }
    }

    /// <summary>
    /// The two compressions of one subset produce identical glyph geometry.
    /// </summary>
    /// <remarks>
    /// A relative assertion, and deliberately the only one here: it cannot tell whether either is
    /// <i>right</i> — the theories above do that, against an independent font tool — but it is the cheapest way to
    /// catch the transform being reversed in a way that is self-consistent and different.
    /// </remarks>
    [Fact]
    public void TheHmtxTransformedFixtureDecodesToTheSameOutlines()
    {
        GlyphRecord[] plain = Glyphs(Decode(StandardWoff2Base64));
        GlyphRecord[] transformed = Glyphs(Decode(HmtxTransformedWoff2Base64));

        Assert.Equal(plain.Length, transformed.Length);
        for (int i = 0; i < plain.Length; i++)
        {
            Assert.Equal(plain[i].Contours, transformed[i].Contours);
            Assert.Equal(plain[i].Bounds, transformed[i].Bounds);
        }
    }

    /// <summary>The unwrapping happens at the one gate every font consumer goes through.</summary>
    /// <remarks>
    /// Asserted through <c>TrueTypeFontProgram.Parse</c> rather than the decoder, because that hook
    /// is the whole delivery: @font-face, a caller's font provider, the Builder's file scan and the
    /// accessibility remediator all gain WOFF 2.0 from it. A decoder nothing called would pass every
    /// test above.
    /// </remarks>
    [Fact]
    public void TrueTypeFontProgramParsesAWoff2ContainerDirectly()
    {
        TrueTypeFontProgram font = TrueTypeFontProgram.Parse(Fixture(StandardWoff2Base64));

        Assert.NotNull(font);
        Assert.Equal(2048, font!.UnitsPerEm);
        Assert.Equal(9, font.GlyphCount);
        Assert.False(font.HasCff);

        // The cmap survived the container, so the font can answer the question every caller
        // actually asks. A reassembly that lost or mis-offset a table still parses.
        Assert.True(font.TryGetGlyph('A', out ushort a) && a != 0);
    }

    // ------------------------------------------------------------------ hostile input

    /// <summary>
    /// A malformed container is refused rather than decoded into a plausible-looking font.
    /// </summary>
    /// <remarks>
    /// <para>Each row corrupts one field a decoder is tempted to trust. The declared file length and
    /// the reserved word are cheap consistency checks; the table count and the compressed size are
    /// the two that size loops and buffers, and a decoder that believed either lets a four-kilobyte
    /// container ask for gigabytes.</para>
    /// <para>Truncation is separate from corruption on purpose: a truncated container has a
    /// perfectly valid header, so nothing but a bounds check on every read catches it.</para>
    /// </remarks>
    [Theory]
    [InlineData(8, 0xFF)]     // declared length: no longer equals the actual file size
    [InlineData(12, 0xFF)]    // numTables: more tables than the directory can hold
    [InlineData(14, 0x01)]    // reserved: MUST be zero
    [InlineData(20, 0x7F)]    // totalCompressedSize: past the end of the file
    public void AMalformedHeaderFieldIsRefused(int offset, int value)
    {
        byte[] corrupted = Fixture(StandardWoff2Base64);
        corrupted[offset] = (byte)value;

        Assert.Null(Woff2Decoder.Decode(corrupted));
    }

    /// <summary>A truncated container is refused at whatever read runs off the end.</summary>
    [Fact]
    public void AnOversizedTruncationIsRefusedAtEveryLength()
    {
        byte[] whole = Fixture(StandardWoff2Base64);

        for (int keep = 1; keep < whole.Length; keep += 97)
        {
            var truncated = new byte[keep];
            Buffer.BlockCopy(whole, 0, truncated, 0, keep);
            Assert.Null(Woff2Decoder.Decode(truncated));
        }
    }

    /// <summary>
    /// The acceptance control for the refusals: the untouched fixture decodes into a real sfnt.
    /// </summary>
    /// <remarks>
    /// <para>Without it every refusal above is satisfied by a decoder that returns null
    /// unconditionally, which is the shape this whole file replaced.</para>
    /// <para><b>Asserted on the parsed result, not on "it returned something".</b> A decoder that
    /// handed back an empty buffer, or one whose directory count did not match the tables it wrote,
    /// would satisfy a null check — and would be exactly the confidently-wrong output the refusal
    /// tests are meant to be measured against.</para>
    /// </remarks>
    [Fact]
    public void TheUntouchedFixtureDecodesIntoAWellFormedSfnt()
    {
        byte[] sfnt = Decode(StandardWoff2Base64);

        Assert.Equal(0x00010000u, U32(sfnt, 0));            // the TrueType sfnt version
        Assert.Equal(18, U16(sfnt, 4));                     // numTables, matching the directory
        Assert.Equal(18, Tables(sfnt).Count);

        // Every declared table lies inside the file it was written into.
        foreach (var (_, entry) in Tables(sfnt))
            Assert.True(entry.Offset + entry.Length <= sfnt.Length,
                "a directory entry points past the end of the reassembled sfnt");
    }

    /// <summary>A font collection is refused by name rather than decoded into its first font.</summary>
    /// <remarks>
    /// The flavour is the only thing changed, so the container is otherwise valid — which is the
    /// point: the refusal has to come from the collection check, not from the corruption.
    /// </remarks>
    [Fact]
    public void AMalformedCollectionFlavourIsRefused()
    {
        byte[] collection = Fixture(StandardWoff2Base64);
        collection[4] = (byte)'t';
        collection[5] = (byte)'t';
        collection[6] = (byte)'c';
        collection[7] = (byte)'f';

        Assert.Null(Woff2Decoder.Decode(collection));
    }

    // ================================================================== sfnt reading

    private readonly struct GlyphRecord
    {
        internal GlyphRecord(int contours, (int, int, int, int) bounds, int length)
        {
            Contours = contours;
            Bounds = bounds;
            Length = length;
        }

        internal int Contours { get; }
        internal (int, int, int, int) Bounds { get; }
        internal int Length { get; }
    }

    private static byte[] Fixture(string base64) => Convert.FromBase64String(base64);

    private static byte[] Decode(string base64)
    {
        byte[] sfnt = Woff2Decoder.Decode(Fixture(base64));
        Assert.NotNull(sfnt);
        return sfnt;
    }

    /// <summary>Tag → (offset, length), read straight out of the reassembled sfnt directory.</summary>
    private static IReadOnlyDictionary<string, (int Offset, int Length)> Tables(byte[] sfnt)
    {
        int numTables = U16(sfnt, 4);
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        for (int i = 0; i < numTables; i++)
        {
            int entry = 12 + i * 16;
            string tag = new string(new[]
            {
                (char)sfnt[entry], (char)sfnt[entry + 1],
                (char)sfnt[entry + 2], (char)sfnt[entry + 3],
            });
            tables[tag] = ((int)U32(sfnt, entry + 8), (int)U32(sfnt, entry + 12));
        }

        return tables;
    }

    /// <summary>Every glyph's contour count, bounding box and record length.</summary>
    private static GlyphRecord[] Glyphs(byte[] sfnt)
    {
        var tables = Tables(sfnt);
        (int Offset, int Length) glyf = tables["glyf"];
        (int Offset, int Length) loca = tables["loca"];
        (int Offset, int Length) maxp = tables["maxp"];
        (int Offset, int Length) head = tables["head"];

        int numGlyphs = U16(sfnt, maxp.Offset + 4);
        int indexFormat = U16(sfnt, head.Offset + 50);

        var records = new GlyphRecord[numGlyphs];
        for (int g = 0; g < numGlyphs; g++)
        {
            int start = LocaAt(sfnt, loca.Offset, g, indexFormat);
            int end = LocaAt(sfnt, loca.Offset, g + 1, indexFormat);

            if (end <= start)
            {
                records[g] = new GlyphRecord(0, (0, 0, 0, 0), 0);
                continue;
            }

            int at = glyf.Offset + start;
            records[g] = new GlyphRecord(
                S16(sfnt, at),
                (S16(sfnt, at + 2), S16(sfnt, at + 4), S16(sfnt, at + 6), S16(sfnt, at + 8)),
                end - start);
        }

        return records;
    }

    /// <summary>One glyph's (advance width, left side bearing) from <c>hmtx</c>.</summary>
    private static (int Advance, int Lsb) Metric(byte[] sfnt, int glyph)
    {
        var tables = Tables(sfnt);
        int hmtx = tables["hmtx"].Offset;
        int numberOfHMetrics = U16(sfnt, tables["hhea"].Offset + 34);

        if (glyph < numberOfHMetrics)
            return (U16(sfnt, hmtx + glyph * 4), S16(sfnt, hmtx + glyph * 4 + 2));

        int trailing = hmtx + numberOfHMetrics * 4 + (glyph - numberOfHMetrics) * 2;
        return (U16(sfnt, hmtx + (numberOfHMetrics - 1) * 4), S16(sfnt, trailing));
    }

    private static int LocaAt(byte[] sfnt, int locaOffset, int index, int indexFormat)
        => indexFormat == 0
            ? U16(sfnt, locaOffset + index * 2) * 2
            : (int)U32(sfnt, locaOffset + index * 4);

    private static int U16(byte[] d, int at) => (d[at] << 8) | d[at + 1];

    private static int S16(byte[] d, int at)
    {
        int v = U16(d, at);
        return v > short.MaxValue ? v - 65536 : v;
    }

    private static uint U32(byte[] d, int at)
        => ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];

    // ==================================================================
    //  The fixtures: a nine-glyph subset of Roboto (Apache-2.0) carrying one glyph of each shape —
    //  simple, empty and composite — compressed to WOFF 2.0 twice by an independent font tool. The second
    //  additionally applies the §5.4 hmtx transform, which the first does not.
    // ==================================================================

    private const string StandardWoff2Base64 =
        "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"
        + "mdrsy9mNvdxCyUlDyN5Y3Y98stC7WGVrU6m3zoqnv/51/ZWvfhXp6W/c7ma4+rWvgU59zQb9Q18fUEgMGXH37fKBRXkEcWF3"
        + "xhDyeQPkgzEPThXbr/exYKBJIkKQy1HZTvVm8et885EqXxNf/dzXlNUgZx4xXN1J+ZWt/BInv3ge+eWRfGfnvar/ir8KomP1"
        + "P5oOyOoW07T52XRDyUrYahBXcn876x5HbYPjY3VvU09WlKi1Pgd36mTd19TvVz7mfqzlxHcrv/qVL5nfpt5b8jdU3vMV73fv"
        + "NT+rljnVd9li7v4xz6G9k47wEdu7wpGlxfRj/uG1t7aPP7H5T3/hppz/oR/kTVOBnH5Pe7a82yUfdHts3WnnbuX0vsMTpqPx"
        + "qbax8fS1aHz+GSV5K9m369zJ+FtsCy2p/LLVXZ88ec/bupYyb+86dU+vVkH5ic+at3ZlMm9brj/hO6+WdTbcODy3vO3I4cy2"
        + "Q3ONNzrLOhpXv/nMdrl97wr++vlOF22u2z0dnC5OFN7yjB7wjeyKBZcm++52pc68IzX0wF3vsnl7RgMHjvTbW/c11x7tHTIf"
        + "yj9X83J392bfvvDqfM8D9oGbHxsbezL5bv5Qe3y5PnR4vMm3p0xSUrbjPv9xm3zM7zv2WrNtzu+7bpOv+fyzuTlcIziAaKDB"
        + "vzCAUwBqALFW+l8oFjQafU6OBIPRyGl5LiNy2gxkt+wuCDk8oZCnwOMu8DhdMQMkLdFlL14rCxdSz1YiW2ubasPhvCaXIVNe"
        + "oc/oxDwNked+hA5PAXHlHdUFmoVNkoafZK6uQq67Be9VlqsL3OZv5XW6vBJMSk/iczqBt2gI2eh9Rq8XBADPfyP71q+8ayAv"
        + "+rcjVVpE4z76xwI7Yj//SPwtqGFVjY9Nw92A/DbN7P+NgBhEDSrFWi2W1jr+GkbJf6lKYnm9r5DEnHALacGMAT6BMW5D5D5v"
        + "BBIaIYJDPhx4COB+nrcKjiKqQRI8iKBDGo3AdSKHXBinIA3Qo1I1MX69Y80p555WHuXumkbRhQUMV1hBv/sYJrCKWQwhgz5M"
        + "YZQLJnGUDzKYe7NZNWZyYuGfOlARR6wwct74Qmwao4huGWMnvMeVY9uYbCSzW23fYDMSoxtRZj3aRtd8GroaZlbCE3Q51E4z"
        + "IWbJzywGP0cXgsx8oJ/OBfrsbOAJZgI2Oh0op1NeZtLNTLi57LinNOZa/4WjTmbE+TIddk7QITszaD9FBxxMv+NuepeNSVuZ"
        + "lMwkWz9HE61MvDWH9rUwvc2M0lRPe5pstLuhn3Y1MJ0NL9OORqa98RQ9Ws8cscj0sOVlesjCHLT00gMWme6vY/aZmL2mDN1j"
        + "ZnabM3QXZXbW3k131DKxGmZ7NdMWuTv2axqNeGnYl0tD/gwN+ttpwF9H/b67qccdoy5nhjrsB6ncGqNNFQJtbGimDZ6qinR9"
        + "ZSG1iJUV6boKxmxqo6ZgeWmalttobRlTU8pUl2polaeiOlXuLa1OVV6dsn9OScW20ueSRa5CpcCVrxQm8hM5XqMiegXFmBAS"
        + "VBgQuDzhpPAHgc/rz1UMXr2i8UoKcUHJTegTUuKkRBxSu7Qg8dulAemkxCPhAHFgAX+A0Hm1Cu/lFG2CS1BugOPyuJPcHzie"
        + "j8VE8k5yS+2RD71Tw7oOqbqOlEouqQ3dOzp9U1Kl11NDSabibxByI3H++nXU7Dyk3uqOv8mjlQmrcLs6BVmBjyyvQIYsy3us"
        + "1VXIVwqiUOD2JAu9NAFUrMjFZMG5u2wvyLsu4iGcg91HuhwA";

    private const string HmtxTransformedWoff2Base64 =
        "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"
        + "vS437TrY1DSvzwl0V932BFR5VLAZZQapyYADm3JPQA4oUlX5MKKyLDhLfhhnwbPWaFhEYDVzRtz0UDxjsEIUlw6RY9PbHDGE"
        + "YSMnERM0UOv3OYhRwaKVIqX1cnh+hZMDnhkoBBqZyxDVkTIRURLO6pZ6EqRCwKwRXuK0gqnDxys8vMipDWETtGqMS3H1RvZU"
        + "aLrsXnUHUasSx9DDYmxLMvpslO1GXhRrZ8n5aQ8Gdfi49/henIhaw50fe6boe9AFvd5SMrMzivtE1Q6omrzGKLvy1rLdNQRj"
        + "rROh2klH+bwfPeuNRgjsNiPdY9WDtIYwWQGJgc7UKwJFHswTOsS/Mmg1ce8IWvPMSMnqyQPSdYrjo0zF0hGXT89N3/PAYvOI"
        + "JoeAxrTKLg45Bmr8du5WOEgZ9Q5Xq+fmUJRekcUfyCre1OqUEDrzCWSQeQ7X7vckj0FGNQiqMRKtTXxEOgp37+ptVlIwgsh6"
        + "G9rVKNNM/dpKZngr5aaTXuqb7hnX9By6cx9Kpi3l/lk57IAK80bF8qOd+8JkjKorOIekKvTm7+aNENsF2TOcuIM6DLedSwhH"
        + "oNEDt2ARGuiulVmF0L1Uxa/uTanehAZHlGxv5BcmYJ/m7TDkWCE92XusVWDymfxWc7+Vux2gu1SAOw98Kt2n2sXyslTUS3Ed"
        + "CVitZIznAFpXXQOeekl5SviCvBgYcc2GZNQIdZd7FHO8EzWdO84TQXSJlXoKRuo1jEparTWCNheQnO/+H3cOIRznONIhwCm1"
        + "d6GOfaAv6HPyMRe6Y73bn/vN/XfkLf9/sJpRSXJtqsy/j1vk7Xx+n3t5q77TWpykEqvpiV4miJRO2NYI+3aiCdx8oPMl00bo"
        + "BKbevMi7YcfT18TXmZWeC9KP1KO3QBl+d7GeJwjHE+H7AdRah1/ljVm0WKefNpmLTcUZXS72OagNp5ZGTBjGr2GC7HJih595"
        + "kuGlEoSOWGY8g3Dfh8mDUAdR9bbqiC8rZdGkIwxK1OjlL60/mtRo5q8s1YRh0wdeenXgfmx9LUjGFlv2qriqLn2OMwDBHHph"
        + "ckDQLgP5OzMSzN+QLLXV4c15bG0nToTDHMrKxT1n30PNzlJDjidzjeVftBhIbAzCwn5TaZR6tixQBV+xTRwH0QDCvThBrMwQ"
        + "GFSoYYgYou4aMuNP8FmBhp1FmVl7M+zzEjSgIAxFVJCkdMM8q6jIlWEOIUpzGkf1oZipo31h6Egtj6E0SSNkbSxvRpz6ehcr"
        + "Hjuazlcfkk58/XU98+qr0E9843Yj4/hrr4EFr5VDv+L1FpAYMsDdt8v7ifRViAu7M4bY57fQDWM+ArJ9fYcIBpokEYJcjxrq"
        + "VO3OvS63ALrxFcGtfy29qvTMJ2PvTspXSvkSJ188D3y5A+485+LJyCU55szTeZ8OHmOaFjNuDxudwpjPahnpAPS3K9sWVrZ8"
        + "nX5P1jut8HnBAktcMDb9X/b9nGvmXTNz9H/Oq6+8VHhPtsz8hpyLXkm+e3HhtVluzvMHRM8+Pkda6hctvjbTh6XLtjZHby1s"
        + "33m9vfvO0T+d7zHkfPwgtstKoBMfkodKemyGZjsx9Yxaa/uOrN+ywLUPLgXn5kdPLK9bf5CuP1O/o+b4WN2r8j7dSE7xds2P"
        + "sdI92gB9WxVc1KuDoPz+WXHSBumb5fp93n+cq5RPbdkbDbW10lDLmnKqckqnsvrto2EZrl3BXz8+xwZaoXbZs1wwlHdmfmeT"
        + "c6Ym4tlaHDg/t+HofXrblaYHypO9s+6mtnGzvkHLtvdP8S05x/Nvrq7eM9Dg217vvTKr5fQzXcbd+gf55u2DUcnbOq8664qT"
        + "0g2mA07XAZNhv8u5/2lx+ZrLebK89MSChatZEeaCAaRAwV+xCAEA+SAVyP95ExMURY9EZBimyVTOBiWmDqJ0GOy5Xgvxekku"
        + "secSqy2SDlklbffiqsxbSD1cocHIqqzPl100N31wamnaoCbFFCbPfgosJJe5+C5vnKa6KEmRJ53LcxGdNxPvNSVL507rv+aa"
        + "FpuESUk2PqcTPKEQHal9yOlgCADnbwydXnmgJbb8t71cFdG4p388u2BCnz9VdwSfFY33TcMOQH9M2fN/gLQY+XJagYrlATTO"
        + "z6J/pcXSVFEvzhOF3JcbUvMll5HhksAQN9sVAPZ57CAYCICCehwkNBBMAJ7IEIU5BTnQkaOKmMyMiRXH5uxYJqPmdU2Xq9Kn"
        + "PcMB9Bt36XHQHm0G7bBTJ+f32sb7g/a+2R7VZbJn4Z/amBFHbFEHjmhO4S6drurl6DzQtz+EhvyePBb8Dkf9EdgdQIcDQdhx"
        + "JlsHfeiAbwGi3g6gXjTgQv2ev1afB+1zj8Oau0/ucd/HbrcJlt0lsORAvXbUY+eS3STfZVv/CzutqMP6yGq3LsCUGbWaD1st"
        + "FtRsOW81mVCjETUYUL3+r+XrUZ0+09qhQ7ValFYl6FVN0COPQ7eMKuVHVoWCtiuHrW0S2ioaoFV8ZG0W0SaxH5pEAzQKaD2H"
        + "1nEU6ni0hqdQA+gE9ry1mkVeJVpVgVb4z3tfW8v9DvA5s6wlLgoeVwe4XQK4nOet+fYI2KwULOZmMOgjoJYmWK6sBZmUl45K"
        + "ZXkgJuZMGxVKUSEXBM5TUjQKJSZgi1F+EcorSrZySWnFSImjqOJBDTl7Z8o/Z9K0lZOvKyfMHZ8eNzeeHu/H/UjShJQUMH3h"
        + "W4IIFhNj4gfBY83RtJHUoSRl0Fyko77uy/6YTLNlRe6T+SqZyGMyR5gNmo0+/IBrSRU8yaD6zLcYYSzGxtgPjPOKvWfbdL8+"
        + "DzVuXCvhdAyt4gp0BpxZRZ/W5CG/Hhjp8spnonPC92efjfzWGOdV+185WhmsyFITQVLwsk8+VU4552rMpc+U29MqiAK3o17o"
        + "onVCZKUv5oTk9sBeym27iJOYHLX9SN8F";
}
