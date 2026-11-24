// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Tests for <see cref="CMapCodec"/> — the encoding CMap that splits show-string bytes into
/// character codes and maps them to CIDs.
/// </summary>
/// <remarks>
/// Deriving the code width from <c>/ToUnicode</c> and assuming a single fixed stride is wrong
/// three ways, each covered below: <c>/ToUnicode</c> is not the encoding,
/// a fixed stride cannot express a mixed-width codespace, and code == CID holds only for
/// Identity encodings.
/// </remarks>
public sealed class CMapCodecTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    // ------------------------------------------------------------------
    //  Identity
    // ------------------------------------------------------------------

    [Fact]
    public void Identity_SplitsIntoTwoByteCodes_WithCidEqualToCode()
    {
        var codec = CMapCodec.Identity(0);
        var codes = codec.Decode(new byte[] { 0x00, 0x41, 0x30, 0x42 });

        Assert.Equal(2, codes.Count);
        Assert.Equal(0x0041, codes[0].Value);
        Assert.Equal(0x0041, codes[0].Cid);
        Assert.Equal(2, codes[0].ByteLength);
        Assert.Equal(0x3042, codes[1].Value);
        Assert.Equal(0x3042, codes[1].Cid);
    }

    [Fact]
    public void IdentityV_ReportsVerticalWritingMode()
    {
        Assert.Equal(1, CMapCodec.Identity(1).WMode);
        Assert.Equal(1, CMapCodec.FromPredefinedName("Identity-V")!.WMode);
        Assert.Equal(0, CMapCodec.FromPredefinedName("Identity-H")!.WMode);
    }

    [Fact]
    public void FromPredefinedName_ReturnsNull_ForAnUnregisteredName()
        => Assert.Null(CMapCodec.FromPredefinedName("UniJIS-UCS2-H-not-registered"));

    // ------------------------------------------------------------------
    //  Word spacing (PDF §9.3.3)
    // ------------------------------------------------------------------

    /// <summary>
    /// Tw applies to the single-byte code 32 only. Under Identity-H the 2-byte code 0x0020 is
    /// glyph 32 — rarely a space — so a draw loop testing
    /// <c>bytes[i] == 0 &amp;&amp; bytes[i+1] == 32</c> must not apply Tw to it.
    /// </summary>
    [Fact]
    public void TwoByteCode0020_IsNotAWordSpaceCode()
    {
        var codes = CMapCodec.Identity(0).Decode(new byte[] { 0x00, 0x20 });

        Assert.Single(codes);
        Assert.Equal(0x0020, codes[0].Value);
        Assert.False(codes[0].IsWordSpaceCode,
            "Tw must not apply to byte value 32 inside a multiple-byte code.");
    }

    [Fact]
    public void SingleByteCode32_IsAWordSpaceCode()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<00> <FF>\nendcodespacerange\n"));
        var codes = codec.Decode(new byte[] { 0x41, 0x20, 0x42 });

        Assert.Equal(3, codes.Count);
        Assert.False(codes[0].IsWordSpaceCode);
        Assert.True(codes[1].IsWordSpaceCode);
        Assert.False(codes[2].IsWordSpaceCode);
    }

    // ------------------------------------------------------------------
    //  Codespace splitting
    // ------------------------------------------------------------------

    /// <summary>
    /// A mixed-width CMap (the shape of the RKSJ family: 1-byte ASCII plus 2-byte CJK) cannot be
    /// represented by a single stride at all — the old code decoded such strings as all-1-byte or
    /// all-2-byte and produced garbage either way.
    /// </summary>
    [Fact]
    public void MixedWidthCodespace_SplitsPerRange()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n" +
            "<00> <80>\n" +          // 1-byte range
            "<8140> <9FFC>\n" +      // 2-byte range
            "endcodespacerange\n"));

        // 'A' (1 byte), 0x8140 (2 bytes), 'B' (1 byte)
        var codes = codec.Decode(new byte[] { 0x41, 0x81, 0x40, 0x42 });

        Assert.Equal(3, codes.Count);
        Assert.Equal((0x41, 1), (codes[0].Value, codes[0].ByteLength));
        Assert.Equal((0x8140, 2), (codes[1].Value, codes[1].ByteLength));
        Assert.Equal((0x42, 1), (codes[2].Value, codes[2].ByteLength));
    }

    [Fact]
    public void CodespaceMatching_PrefersTheShortestMatchingWidth()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<00> <FF>\n<0000> <FFFF>\nendcodespacerange\n"));
        var codes = codec.Decode(new byte[] { 0x41, 0x42 });

        Assert.Equal(2, codes.Count);
        Assert.All(codes, c => Assert.Equal(1, c.ByteLength));
    }

    /// <summary>A CMap with no codespacerange is malformed; composite fonts are 2-byte.</summary>
    [Fact]
    public void MissingCodespaceRange_DefaultsToTwoByteCodes()
    {
        var codes = CMapCodec.Parse(Ascii("/CIDInit /ProcSet findresource begin\nend\n"))
                             .Decode(new byte[] { 0x00, 0x41, 0x00, 0x42 });
        Assert.Equal(2, codes.Count);
        Assert.All(codes, c => Assert.Equal(2, c.ByteLength));
    }

    /// <summary>
    /// A trailing byte that cannot complete a code must still be consumed — otherwise a truncated
    /// string would loop forever.
    /// </summary>
    [Fact]
    public void TrailingPartialCode_TerminatesAndIsConsumed()
    {
        var codes = CMapCodec.Identity(0).Decode(new byte[] { 0x00, 0x41, 0x30 });
        Assert.Equal(2, codes.Count);
        Assert.Equal(1, codes[1].ByteLength);
    }

    /// <summary>Bytes matching no declared codespace must not stall the decoder.</summary>
    [Fact]
    public void BytesOutsideEveryCodespace_StillAdvance()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<20> <7E>\nendcodespacerange\n"));
        var codes = codec.Decode(new byte[] { 0xFF, 0xFE, 0x41 });

        Assert.Equal(3, codes.Count);
        Assert.Equal(0x41, codes[2].Value);
    }

    // ------------------------------------------------------------------
    //  code → CID
    // ------------------------------------------------------------------

    /// <summary>
    /// For a non-Identity CMap the code is NOT the CID. Glyph and /W lookups must go through
    /// <c>cidrange</c>; treating the code as a CID picked the wrong glyph for every predefined
    /// CJK encoding.
    /// </summary>
    [Fact]
    public void CidRange_MapsCodesOntoTheCidSpace()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "begincidrange\n<0020> <007E> 1\nendcidrange\n"));

        var codes = codec.Decode(new byte[] { 0x00, 0x20, 0x00, 0x41 });

        Assert.Equal(0x20, codes[0].Value);
        Assert.Equal(1, codes[0].Cid);                    // first code in the range → CID 1
        Assert.Equal(0x41, codes[1].Value);
        Assert.Equal(1 + (0x41 - 0x20), codes[1].Cid);    // linear within the range
    }

    [Fact]
    public void CidChar_MapsASingleCode()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "begincidchar\n<3042> 843\nendcidchar\n"));

        var codes = codec.Decode(new byte[] { 0x30, 0x42 });
        Assert.Equal(843, codes[0].Cid);
    }

    /// <summary>
    /// A 1-byte code 0x20 and a 2-byte code 0x0020 are different codes and may map to different
    /// CIDs, so the cidchar table must key on width as well as value.
    /// </summary>
    [Fact]
    public void CidChar_DistinguishesCodesOfDifferentByteWidths()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<00> <80>\n<8140> <9FFC>\nendcodespacerange\n" +
            "begincidchar\n<20> 5\n<8140> 99\nendcidchar\n"));

        var codes = codec.Decode(new byte[] { 0x20, 0x81, 0x40 });
        Assert.Equal(5, codes[0].Cid);
        Assert.Equal(99, codes[1].Cid);
    }

    [Fact]
    public void UnmappedCode_FallsBackToTheCodeValue()
    {
        var codec = CMapCodec.Parse(Ascii(
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "begincidrange\n<0020> <002F> 1\nendcidrange\n"));

        var codes = codec.Decode(new byte[] { 0x12, 0x34 });
        Assert.Equal(0x1234, codes[0].Cid);
    }

    // ------------------------------------------------------------------
    //  WMode and usecmap
    // ------------------------------------------------------------------

    [Fact]
    public void EmbeddedCMap_WModeIsRead()
    {
        var codec = CMapCodec.Parse(Ascii(
            "/WMode 1 def\nbegincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"));
        Assert.Equal(1, codec.WMode);
    }

    /// <summary>usecmap against Identity-H must inherit its 2-byte codespace.</summary>
    [Fact]
    public void Usecmap_AgainstIdentity_InheritsTheTwoByteCodespace()
    {
        var codec = CMapCodec.Parse(Ascii(
            "/Identity-H usecmap\nbegincidchar\n<0041> 7\nendcidchar\n"));

        var codes = codec.Decode(new byte[] { 0x00, 0x41 });
        Assert.Single(codes);
        Assert.Equal(2, codes[0].ByteLength);
        Assert.Equal(7, codes[0].Cid);
    }

    /// <summary>
    /// A self-referential usecmap is reachable through the public
    /// <see cref="CMapParser.RegisterPredefinedCMap"/> surface and must not recurse to a
    /// StackOverflow.
    /// </summary>
    [Fact]
    public void Usecmap_SelfReference_IsDepthBounded()
    {
        const string name = "CMapCodecTests-SelfRef";
        CMapParser.RegisterPredefinedCMap(name, Ascii(
            $"/{name} usecmap\nbegincodespacerange\n<0000> <FFFF>\nendcodespacerange\n"));

        var ex = Record.Exception(() => CMapCodec.FromPredefinedName(name)!.Decode(new byte[] { 0, 65 }));
        Assert.Null(ex);
    }

    [Fact]
    public void EmptyInput_ProducesNoCodes()
    {
        Assert.Empty(CMapCodec.Identity(0).Decode(new byte[0]));
        Assert.Empty(CMapCodec.Identity(0).Decode(null!));
    }

    // ------------------------------------------------------------------
    //  /UseCMap — the stream a usecmap in an embedded CMap names (§9.7.5.2, Table 120)
    // ------------------------------------------------------------------

    /// <summary>
    /// An embedded CMap's <c>usecmap</c> names its stream's <c>/UseCMap</c>: the parent's codespace
    /// splits the codes and its characters fill in behind the child's own. Read without it the child
    /// fell back to a two-byte codespace and read <c>AB</c> as one code.
    /// </summary>
    [Fact]
    public void FromFontEncoding_UsecmapNamingTheStreamsUseCMap_InheritsItsCodespaceAndCharacters()
    {
        var parent = new Tetitulz.Pdf.Core.Objects.PdfStream
        {
            RawData = Ascii("1 begincodespacerange <00> <FF> endcodespacerange 2 begincidchar <41> 30 <42> 10 endcidchar"),
        };
        var child = new Tetitulz.Pdf.Core.Objects.PdfStream
        {
            RawData = Ascii("/Test-Parent usecmap 1 begincidchar <41> 20 endcidchar"),
        };
        child.Set(Tetitulz.Pdf.Core.Objects.PdfName.UseCMap, parent);
        var font = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        font.Set(Tetitulz.Pdf.Core.Objects.PdfName.Encoding, child);

        var codes = CMapCodec.FromFontEncoding(font, null).Decode(Ascii("AB"));

        Assert.Null(CMapCodec.LastEncodingError);
        Assert.Equal(2, codes.Count);
        Assert.Equal(1, codes[0].ByteLength);
        Assert.Equal(20, codes[0].Cid);   // the child's own character wins
        Assert.Equal(10, codes[1].Cid);   // the parent's fills in
    }

    /// <summary>Each stream's <c>/UseCMap</c> answers the next <c>usecmap</c> up the chain.</summary>
    [Fact]
    public void FromFontEncoding_UsecmapChain_FollowsEachStreamsUseCMap()
    {
        var grandparent = new Tetitulz.Pdf.Core.Objects.PdfStream
        {
            RawData = Ascii("1 begincodespacerange <00> <FF> endcodespacerange 1 begincidchar <43> 7 endcidchar"),
        };
        var parent = new Tetitulz.Pdf.Core.Objects.PdfStream { RawData = Ascii("/Grand usecmap 1 begincidchar <41> 30 endcidchar") };
        parent.Set(Tetitulz.Pdf.Core.Objects.PdfName.UseCMap, grandparent);
        var child = new Tetitulz.Pdf.Core.Objects.PdfStream { RawData = Ascii("/Parent usecmap 1 begincidchar <42> 20 endcidchar") };
        child.Set(Tetitulz.Pdf.Core.Objects.PdfName.UseCMap, parent);
        var font = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        font.Set(Tetitulz.Pdf.Core.Objects.PdfName.Encoding, child);

        var codes = CMapCodec.FromFontEncoding(font, null).Decode(Ascii("ABC"));

        Assert.Null(CMapCodec.LastEncodingError);
        Assert.Equal(new[] { 30, 20, 7 }, new[] { codes[0].Cid, codes[1].Cid, codes[2].Cid });
    }

    /// <summary>
    /// A <c>usecmap</c> naming nothing the stream or the registry supplies is reported, and the
    /// CMap's own mappings are kept.
    /// </summary>
    [Fact]
    public void FromFontEncoding_UsecmapNamingNothingKnown_IsReportedAndTheOwnMappingsKept()
    {
        var child = new Tetitulz.Pdf.Core.Objects.PdfStream
        {
            RawData = Ascii("/Nowhere-H usecmap 1 begincodespacerange <0000> <FFFF> endcodespacerange 1 begincidchar <0041> 20 endcidchar"),
        };
        var font = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        font.Set(Tetitulz.Pdf.Core.Objects.PdfName.Encoding, child);

        var codec = CMapCodec.FromFontEncoding(font, null);

        Assert.Contains("Nowhere-H", CMapCodec.LastEncodingError, System.StringComparison.Ordinal);
        Assert.Equal(20, codec.Decode(new byte[] { 0x00, 0x41 })[0].Cid);
    }

    /// <summary>
    /// A <c>usecmap</c> of Identity-H maps every two-byte code the referencing CMap does not — to the
    /// CID of its value, as Identity does — so an inherited code is mapped, not fallen back on; the
    /// child's own range still comes first. The control, a CMap with the codespace alone, falls back
    /// and says so.
    /// </summary>
    [Fact]
    public void Usecmap_IdentityParent_MapsEveryInheritedTwoByteCode()
    {
        var codec = CMapCodec.Parse(Ascii("/Identity-H usecmap 1 begincidrange <0005> <0005> 99 endcidrange"));

        codec.ReadCode(new byte[] { 0x00, 0x01 }, 0, out var inherited);
        Assert.True(inherited.CidMapped);
        Assert.Equal(1, inherited.Cid);
        codec.ReadCode(new byte[] { 0x00, 0x05 }, 0, out var own);
        Assert.True(own.CidMapped);
        Assert.Equal(99, own.Cid);

        var bare = CMapCodec.Parse(Ascii("1 begincodespacerange <0000> <FFFF> endcodespacerange"));
        bare.ReadCode(new byte[] { 0x00, 0x01 }, 0, out var fallback);
        Assert.False(fallback.CidMapped);
        Assert.Equal(1, fallback.Cid);
    }
}
