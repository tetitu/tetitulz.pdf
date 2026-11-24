// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Tests.Constants;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="CMapCodec"/> at its edges: degenerate code
/// widths, ranges that touch, the word-space code, a predefined CMap's content and the
/// <c>usecmap</c> of Identity.
/// </summary>
public class CMapCodecBoundaryTests
{
    internal static CMapCodec Cmap(string body)
        => CMapCodec.Parse(Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin\nbegincmap\n" + body + "\nendcmap\n"));

    /// <summary>
    /// A codespace range of no bytes or of five is outside §9.7.6.2's one to four, and is skipped
    /// rather than indexed (the codespaces are kept per width, four slots). The CMap's valid
    /// one-byte range still decodes.
    /// </summary>
    [Theory]
    [InlineData("<> <>")]
    [InlineData("<0000000000> <FFFFFFFFFF>")]
    public void ACodespaceOfNoBytesOrFiveIsSkipped(string bad)
    {
        var codec = Cmap($"2 begincodespacerange\n{bad}\n<00> <FF>\nendcodespacerange");

        var codes = codec.Decode(new byte[] { 0x41, 0x42 });
        Assert.Equal(new[] { 1, 1 }, codes.Select(c => c.ByteLength));
        Assert.All(codes, c => Assert.True(c.CodespaceMatched));
    }

    /// <summary>
    /// A CID range five bytes wide is skipped rather than filed under a fifth width that does not
    /// exist; the two-byte range beside it is used.
    /// </summary>
    [Fact]
    public void ACidRangeFiveBytesWideIsSkipped()
    {
        var codec = Cmap("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
                         "2 begincidrange\n<0000000000> <0000000005> 10\n<0100> <01FF> 1000\nendcidrange");

        Assert.Equal(1005, codec.ToCid(0x0105, 2));
    }

    /// <summary>
    /// Two ranges that share one code overlap; the shared code resolves through the range declared
    /// first, and through the sorted index, which keeps each range's
    /// declaration order rather than standing down. Touching is the case a strict comparison misses.
    /// The control is the same ranges a code apart, where the two orders cannot disagree.
    /// </summary>
    [Fact]
    public void RangesSharingOneCodeResolveInDeclarationOrderThroughTheIndex()
    {
        string space = "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n";
        var touching = Cmap(space + "2 begincidrange\n<0100> <0110> 1000\n<0110> <0120> 2000\nendcidrange");
        var reversed = Cmap(space + "2 begincidrange\n<0110> <0120> 2000\n<0100> <0110> 1000\nendcidrange");
        var apart = Cmap(space + "2 begincidrange\n<0100> <0110> 1000\n<0111> <0120> 2000\nendcidrange");

        Assert.True(touching.UsesCidRangeIndex);
        Assert.Equal(1016, touching.ToCid(0x0110, 2));    // the first-declared range owns the shared code
        Assert.Equal(2000, reversed.ToCid(0x0110, 2));    // declared the other way round, the other does
        Assert.True(apart.UsesCidRangeIndex);
        Assert.Equal(1016, apart.ToCid(0x0110, 2));
        Assert.Equal(2000, apart.ToCid(0x0111, 2));
    }

    /// <summary>
    /// §9.3.3: word spacing applies to the single-byte code 32 only, not to every code up to it.
    /// </summary>
    [Fact]
    public void OnlyTheSingleByteCode32IsAWordSpace()
    {
        var codec = Cmap("1 begincodespacerange\n<00> <FF>\nendcodespacerange");

        var codes = codec.Decode(new byte[] { 0x20, 0x1F, 0x09 });
        Assert.Equal(new[] { true, false, false }, codes.Select(c => c.IsWordSpaceCode));
    }

    /// <summary>
    /// A registered predefined CMap is parsed from its own bytes: this one declares one-byte codes,
    /// so two bytes are two codes, not Identity's single two-byte code.
    /// </summary>
    [Fact]
    public void APredefinedCMapIsReadFromItsRegisteredBytes()
    {
        string name = "Test-OneByte-" + Guid.NewGuid().ToString("N");
        CMapParser.RegisterPredefinedCMap(name, Encoding.ASCII.GetBytes(
            "begincmap\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\nendcmap\n"));

        var codes = CMapCodec.FromPredefinedName(name).Decode(new byte[] { 0x41, 0x42 });
        Assert.Equal(2, codes.Count);
    }

    /// <summary>
    /// An embedded <c>/Encoding</c> stream that decodes to nothing falls back to Identity and says
    /// so, rather than being parsed as an empty CMap in silence.
    /// </summary>
    [Fact]
    public void AnEmptyEmbeddedEncodingIsReported()
    {
        var font = new PdfDictionary();
        font.Set(PdfName.Encoding, new PdfStream { RawData = Array.Empty<byte>() });

        var codec = CMapCodec.FromFontEncoding(font, null);

        Assert.True(codec.IsIdentity);
        Assert.Contains("decoded to nothing", CMapCodec.LastEncodingError);
    }

    /// <summary>
    /// <c>/Identity-H usecmap</c> brings Identity's two-byte codespace into a CMap that also
    /// declares a one-byte one, so a byte outside the one-byte range starts a two-byte code.
    /// </summary>
    [Fact]
    public void UsecmapOfIdentityHAddsItsTwoByteCodespace()
    {
        var codec = Cmap("/Identity-H usecmap\n1 begincodespacerange\n<00> <7F>\nendcodespacerange");

        var codes = codec.Decode(new byte[] { 0x80, 0x41 });
        Assert.Single(codes);
        Assert.Equal(2, codes[0].ByteLength);
    }

    /// <summary>
    /// ISO 32000-2 §9.7.5.3: a CMap that uses another gives "only the character mappings that
    /// differ", so its own mappings win — a <c>cidchar</c> of the used CMap neither replaces the
    /// user's <c>cidchar</c> for the same code nor answers before the user's range. A code only the
    /// used CMap maps keeps its mapping.
    /// </summary>
    [Fact]
    public void AUsedCMapsCharactersYieldToTheUsersOwnMappings()
    {
        string parent = "Test-UsedChars-" + Guid.NewGuid().ToString("N");
        CMapParser.RegisterPredefinedCMap(parent, Encoding.ASCII.GetBytes(
            "begincmap\n3 begincidchar\n<0005> 500\n<0006> 600\n<0007> 700\nendcidchar\nendcmap\n"));

        var codec = Cmap($"/{parent} usecmap\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
                         "1 begincidrange\n<0000> <0005> 10\nendcidrange\n1 begincidchar\n<0006> 60\nendcidchar");

        Assert.Equal(15, codec.ToCid(0x0005, 2));    // the user's range, not the used CMap's character
        Assert.Equal(60, codec.ToCid(0x0006, 2));    // the user's character
        Assert.Equal(700, codec.ToCid(0x0007, 2));   // the used CMap's character where the user maps nothing
    }

    /// <summary>
    /// <see cref="EncodingCMap.Parse"/>'s resolver answers a <c>usecmap</c> by name before the
    /// predefined CMaps: the codespace and ranges come from the program it hands back. Without it the
    /// name is unknown and the CMap has no two-byte codespace, so the control splits the same bytes
    /// as two codes.
    /// </summary>
    [Fact]
    public void AnEncodingCMapsUsecmapIsAnsweredByItsResolver()
    {
        byte[] parent = Encoding.ASCII.GetBytes(
            "begincmap\n1 begincodespacerange\n<8000> <FFFF>\nendcodespacerange\n" +
            "1 begincidrange\n<8000> <80FF> 1000\nendcidrange\nendcmap\n");
        byte[] child = Encoding.ASCII.GetBytes(
            "begincmap\n/Program-Parent usecmap\n1 begincodespacerange\n<00> <7F>\nendcodespacerange\n" +
            "1 begincidchar\n<41> 34\nendcidchar\nendcmap\n");
        byte[] text = { 0x41, 0x80, 0x05 };

        var codes = EncodingCMap.Parse(child, name => name == "Program-Parent" ? parent : null).Decode("Japan1", text);
        var control = EncodingCMap.Parse(child).Decode("Japan1", text);

        Assert.Equal(new[] { 1, 2 }, codes.Select(c => c.Length));
        Assert.Equal(new[] { 34, 1005 }, codes.Select(c => c.Cid));
        Assert.Equal(3, control.Count);
    }
}

/// <summary>The two CMap limits at their boundaries; they write process-global settings.</summary>
[Collection(PdfLimitsCollection.Name)]
public class CMapCodecLimitTests
{
    /// <summary>
    /// <c>MaxCMapRangeSpan</c> is the most a range may span: a range spanning exactly that is kept,
    /// one more is dropped (its codes then map to themselves).
    /// </summary>
    [Fact]
    public void ARangeSpanningExactlyTheLimitIsKept()
    {
        try
        {
            PdfLimits.MaxCMapRangeSpan = 0x0F;
            var codec = CMapCodecBoundaryTests.Cmap(
                "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
                "2 begincidrange\n<0100> <010F> 1000\n<0200> <0210> 2000\nendcidrange");

            Assert.Equal(1005, codec.ToCid(0x0105, 2));
            Assert.Equal(0x0205, codec.ToCid(0x0205, 2));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// <c>MaxCMapUsecmapDepth</c> counts the CMaps followed: at 1, the CMap this one uses is
    /// merged and the one that uses in turn is not.
    /// </summary>
    [Fact]
    public void TheUsecmapDepthLimitStopsAtItsCount()
    {
        string tag = Guid.NewGuid().ToString("N");
        string parent = "Test-Parent-" + tag, grandparent = "Test-Grandparent-" + tag;
        CMapParser.RegisterPredefinedCMap(grandparent, Encoding.ASCII.GetBytes(
            "begincmap\n1 begincidrange\n<0002> <0002> 200\nendcidrange\nendcmap\n"));
        CMapParser.RegisterPredefinedCMap(parent, Encoding.ASCII.GetBytes(
            $"begincmap\n/{grandparent} usecmap\n1 begincidrange\n<0001> <0001> 100\nendcidrange\nendcmap\n"));

        try
        {
            PdfLimits.MaxCMapUsecmapDepth = 1;
            var codec = CMapCodecBoundaryTests.Cmap(
                $"/{parent} usecmap\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange");

            Assert.Equal(100, codec.ToCid(0x0001, 2));
            Assert.Equal(0x0002, codec.ToCid(0x0002, 2));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
