// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// Tests for <see cref="InlineImageLength.ComputeSelfDelimiting"/> — the third of the three ways an
/// inline image's payload length can be <em>known</em> rather than guessed.
///
/// <para>
/// Every assertion here is on the returned byte count, because that number is the whole product: it
/// is what both <c>ContentStreamParser</c> and the accessibility scanner jump, and one byte wrong
/// puts every later offset inside the image's samples. A test that only asserted "not -1" would pass
/// with the arithmetic inverted.
/// </para>
/// </summary>
public class InlineImageLengthTests
{
    /// <summary>Latin-1 round-trips bytes 0x00-0xFF 1:1, so a fixture can express binary inline.</summary>
    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    private static PdfDictionary Filtered(PdfObject filter)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.F, filter);
        return dict;
    }

    // =====================================================================
    //  ASCIIHexDecode — §7.4.2, EOD is '>'
    // =====================================================================

    [Theory]
    [InlineData("AHx")]
    [InlineData("ASCIIHexDecode")]
    public void AsciiHex_LengthRunsThroughTheGreaterThanMarker(string filterName)
    {
        byte[] data = Bytes("00FF00FF> EI Q");

        int length = InlineImageLength.ComputeSelfDelimiting(Filtered(new PdfName(filterName)), data, 0);

        Assert.Equal(9, length);                     // eight hex digits plus the '>' itself
        Assert.Equal("00FF00FF>", Encoding.Latin1.GetString(data, 0, length));
    }

    [Fact]
    public void AsciiHex_WhitespaceInsideThePayloadIsPartOfIt()
    {
        // §7.4.2 lets a producer line-wrap the hex, so whitespace counts towards the encoded
        // length even though it decodes to nothing.
        byte[] data = Bytes("FF0000 00FF00>\nEI");

        Assert.Equal(14, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), data, 0));
    }

    [Fact]
    public void AsciiHex_StartOffsetIsHonouredSoTheLengthIsRelativeToThePayload()
    {
        // The callers pass the whole content stream and the payload's offset within it; a length
        // measured from index 0 instead would overshoot by exactly the header.
        byte[] data = Bytes("BI /F /AHx ID 00FF>");
        int start = "BI /F /AHx ID ".Length;

        Assert.Equal(5, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), data, start));
    }

    [Fact]
    public void AsciiHex_MalformedPayloadHoldingNonHexBytesIsRefusedRatherThanBounded()
    {
        // "qqq EI zzz" is not hex at all — the dictionary is lying, or the scan has already run
        // past the image. Either way a length invented here would end the image at the wrong byte,
        // so the caller must be sent back to the heuristic.
        byte[] data = Bytes("qqq EI zzz> EI");

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), data, 0));
    }

    [Fact]
    public void AsciiHex_UnterminatedPayloadWithNoEodIsRefused()
    {
        byte[] data = Bytes("00FF00FF00FF");

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), data, 0));
    }

    // =====================================================================
    //  ASCII85Decode — §7.4.3, EOD is "~>"
    // =====================================================================

    [Theory]
    [InlineData("A85")]
    [InlineData("ASCII85Decode")]
    public void Ascii85_LengthRunsThroughTheTildeGreaterThanMarker(string filterName)
    {
        byte[] data = Bytes("8;Xa EI 9M~> EI Q");

        int length = InlineImageLength.ComputeSelfDelimiting(Filtered(new PdfName(filterName)), data, 0);

        // The "EI" four bytes in is inside the payload: 'E' and 'I' are both in the base-85
        // alphabet, which is why the EI heuristic cannot survive an ASCII85 image.
        Assert.Equal(12, length);
        Assert.Equal("8;Xa EI 9M~>", Encoding.Latin1.GetString(data, 0, length));
    }

    [Fact]
    public void Ascii85_ZeroShorthandIsPartOfTheAlphabet()
    {
        // 'z' (0x7A) is §7.4.3's shorthand for four zero bytes and sits *above* 'u' (0x75), so a
        // plain "in the range ! to u" test refuses it — and a run of black or transparent samples
        // encodes as nothing else, which is to say exactly the payloads most worth measuring.
        byte[] data = Bytes("8;Xazz9M~>rest");

        int length = InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.A85), data, 0);

        Assert.Equal(10, length);
        Assert.Equal("8;Xazz9M~>", Encoding.Latin1.GetString(data, 0, length));
    }

    [Fact]
    public void Ascii85_MalformedTildeNotFollowedByGreaterThanIsRefused()
    {
        // A lone '~' is not an EOD. Treating it as one would end the image one byte into the
        // marker and leave a '>' to be tokenised as a stray delimiter.
        byte[] data = Bytes("8;Xa~z EI");

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.A85), data, 0));
    }

    [Fact]
    public void Ascii85_MalformedPayloadOutsideTheBase85AlphabetIsRefused()
    {
        byte[] data = Cat(Bytes("8;Xa"), new byte[] { 0x80, 0xFF }, Bytes("~>"));

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.A85), data, 0));
    }

    // =====================================================================
    //  RunLengthDecode — §7.4.5, EOD is the length byte 128
    // =====================================================================

    [Theory]
    [InlineData("RL")]
    [InlineData("RunLengthDecode")]
    public void RunLength_WalksTheRunsInsteadOfSearchingForTheEodByte(string filterName)
    {
        // 0xFF 0x80 is a repeat run: "emit the byte 0x80 twice". The 0x80 is a *data* byte, and a
        // search for the value 128 would stop there and end the image nine bytes early.
        byte[] data = Cat(
            new byte[] { 0xFF, 0x80 },                                     // repeat 0x80 x2
            new byte[] { 0x06, 0x20, 0x45, 0x49, 0x20, 0x61, 0x62, 0x63 }, // literal " EI abc"
            new byte[] { 0x80 });                                          // EOD

        int length = InlineImageLength.ComputeSelfDelimiting(Filtered(new PdfName(filterName)), data, 0);

        Assert.Equal(11, length);
        Assert.Equal(data.Length, length);
    }

    [Fact]
    public void RunLength_MalformedTruncatedRunRunningPastTheEndIsRefused()
    {
        // A literal run claiming 100 bytes with only three present: the walk steps past the end of
        // the data and must report "unknown" rather than clamp to the buffer, which would hand the
        // caller a length the file never declared.
        byte[] data = { 0x63, 0x01, 0x02, 0x03 };

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.RL), data, 0));
    }

    // =====================================================================
    //  DCTDecode — §7.4.7 defers to ISO/IEC 10918-1: SOI … EOI
    // =====================================================================

    /// <summary>
    /// A JPEG whose APP0 segment carries a thumbnail ending in <c>FF D9</c>, which is exactly what a
    /// JFIF or EXIF header looks like. A search for <c>FF D9</c> finds the thumbnail's terminator
    /// first and ends the image inside its own header; only a segment-by-segment walk gets it right.
    /// </summary>
    [Theory]
    [InlineData("DCT")]
    [InlineData("DCTDecode")]
    public void Dct_SkipsAThumbnailEoiInsideAnApp0Segment(string filterName)
    {
        byte[] data = JpegWithThumbnail();
        int thumbnailEoi = 14;
        Assert.Equal(0xFF, data[thumbnailEoi]);      // fixture self-check: the decoy really is there
        Assert.Equal(0xD9, data[thumbnailEoi + 1]);

        int length = InlineImageLength.ComputeSelfDelimiting(Filtered(new PdfName(filterName)), data, 0);

        Assert.Equal(data.Length, length);
        Assert.True(length > thumbnailEoi + 2, "the thumbnail's EOI must not end the codestream");
    }

    [Fact]
    public void Dct_StuffedFfInsideEntropyCodedDataIsNotMistakenForAMarker()
    {
        // §B.1.1.5 stuffs every literal FF in the scan as FF 00. Reading that 00 as a marker code
        // loses marker sync and the length collapses to -1, so this asserts the walk survives it.
        byte[] data = JpegWithThumbnail();
        Assert.Equal(0x00, data[25]);                // fixture self-check: the stuffing byte
        Assert.Equal(0xFF, data[24]);

        Assert.Equal(data.Length, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.DCT), data, 0));
    }

    [Fact]
    public void Dct_MalformedSegmentLengthClaimingMoreBytesThanExistIsRefused()
    {
        // An APP0 declaring 0x7FFF bytes inside a 10-byte payload. Trusting it would run the walk
        // off the end; clamping it would invent a boundary the file never stated.
        byte[] data = { 0xFF, 0xD8, 0xFF, 0xE0, 0x7F, 0xFF, 0x00, 0x01, 0xFF, 0xD9 };

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.DCT), data, 0));
    }

    [Fact]
    public void Dct_MalformedSegmentLengthBelowItsOwnTwoLengthBytesIsRefused()
    {
        // A declared length of 1 is shorter than the two bytes that declare it, so the walk would
        // step backwards and never terminate.
        byte[] data = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9 };

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.DCT), data, 0));
    }

    [Fact]
    public void Dct_MalformedStreamWithNoSoiIsRefused()
    {
        byte[] data = { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xD9 };

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.DCT), data, 0));
    }

    // =====================================================================
    //  Filter selection
    // =====================================================================

    /// <summary>
    /// §7.4.1: a filter array is in the order the filters were <em>applied</em>, so element 0 is the
    /// outermost encoding — the only one whose end-of-data marker is visible in the stored bytes.
    /// Reading the last element instead describes bytes that do not exist until the outer filter has
    /// been decoded.
    /// </summary>
    [Fact]
    public void FilterArray_TheFirstElementBoundsTheBytes()
    {
        var array = new PdfArray();
        array.Add(PdfName.AHx);
        array.Add(PdfName.Fl);
        byte[] data = Bytes("789C0BF1>");

        Assert.Equal(9, InlineImageLength.ComputeSelfDelimiting(Filtered(array), data, 0));
    }

    /// <summary>
    /// An <b>image codec inside a self-delimiting outer filter</b> is measured exactly, by the outer
    /// filter's marker — the inner codec is never consulted and does not need to be.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the real-world spelling of an ASCII-armoured inline JPEG</b>
    /// (<c>/F [/A85 /DCT]</c>), and it is worth pinning because the limitation recorded for image
    /// codecs reads as if it covered every chain containing one. It does not: that limitation
    /// belongs to the <i>decoding</i> strategy, which needs a decoded byte count to compare against
    /// the raster — and <c>/DCT</c> is a passthrough stage there, so the "decoded" length is the
    /// codestream's and matches nothing.</para>
    /// <para>Measuring is a different question from decoding. §7.4.1 puts the outermost encoding
    /// first, so the bytes actually stored here are ASCII85 and end at <c>~&gt;</c> whatever sits
    /// beneath them. The only chains left to the heuristic are those whose <b>outermost</b> filter
    /// is not self-delimiting.</para>
    /// </remarks>
    [Theory]
    [InlineData("A85", "8;Xa EI 9M~>", 12)]
    [InlineData("AHx", "FFD8FFE000>", 11)]
    public void FilterArray_AnImageCodecInsideASelfDelimitingFilterIsStillMeasuredExactly(
        string outerFilter, string payload, int expected)
    {
        var array = new PdfArray();
        array.Add(new PdfName(outerFilter));
        array.Add(PdfName.DCT);
        byte[] data = Bytes(payload + " EI Q");

        Assert.Equal(expected, InlineImageLength.ComputeSelfDelimiting(Filtered(array), data, 0));
    }

    /// <summary>
    /// The complement, and the case that genuinely is still heuristic: the image codec is inside a
    /// filter that carries no end-of-data marker, so nothing measurable is visible in the stored
    /// bytes at all.
    /// </summary>
    [Fact]
    public void FilterArray_AnImageCodecInsideAFlateFilterIsNotMeasurable()
    {
        var array = new PdfArray();
        array.Add(PdfName.Fl);
        array.Add(PdfName.DCT);

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(array), Bytes("789C0BF1>"), 0));
    }

    [Fact]
    public void FilterArray_AnInnerSelfDelimitingFilterDoesNotBoundTheOuterBytes()
    {
        // /Fl outermost, /AHx inside it: the '>' in the stored bytes belongs to nothing that is
        // visible yet, so there is no measurable length here at all.
        var array = new PdfArray();
        array.Add(PdfName.Fl);
        array.Add(PdfName.AHx);

        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(array), Bytes("00FF>"), 0));
    }

    [Fact]
    public void AbbreviatedFilterKeyWinsOverTheFullOne()
    {
        // Table 93's /F is the inline-image spelling of /Filter, and the renderer already resolves
        // the pair this way; measuring must not pick the other one.
        var dict = new PdfDictionary();
        dict.Set(PdfName.F, PdfName.AHx);
        dict.Set(PdfName.Filter, PdfName.A85);

        Assert.Equal(5, InlineImageLength.ComputeSelfDelimiting(dict, Bytes("00FF>~>"), 0));
    }

    [Fact]
    public void FlateIsNotSelfDelimitingAndIsRefusedWithoutInspectingTheBytes()
    {
        // Flate does have an end marker, but reaching it means running the decompressor — a decode,
        // not a measurement, and a decompression bomb's worth of work for a boundary.
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.Fl), Bytes("00FF>"), 0));
    }

    [Fact]
    public void CcittIsRefusedBecauseItsEndOfBlockIsOptional()
    {
        // §7.4.6: /EndOfBlock defaults true but may be false, so a CCITT payload genuinely is not
        // self-delimiting. Guessing here would be the one case with no fallback evidence at all.
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.CCF), Bytes("00FF>"), 0));
    }

    [Fact]
    public void UnfilteredImageIsRefusedSoTheGeometryPathStillOwnsIt()
    {
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(new PdfDictionary(), Bytes("00FF>"), 0));
    }

    // =====================================================================
    //  Argument edges
    // =====================================================================

    [Fact]
    public void EmptyAndOutOfRangeInputsAreRefusedRatherThanThrowing()
    {
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(null, Bytes("00>"), 0));
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), null, 0));
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), new byte[0], 0));
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), Bytes("00>"), -1));
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(Filtered(PdfName.AHx), Bytes("00>"), 3));
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>
    /// A 29-byte JPEG codestream: SOI, an APP0 whose payload holds a decoy " EI abcd" and a
    /// thumbnail's <c>FF D9</c>, a scan header, entropy-coded data containing a stuffed
    /// <c>FF 00</c>, and the real EOI. Both classic shortcuts — searching for <c>FF D9</c>, and the
    /// EI heuristic — end this image inside the APP0 segment.
    /// </summary>
    private static byte[] JpegWithThumbnail() => new byte[]
    {
        0xFF, 0xD8,                                                  // SOI
        0xFF, 0xE0, 0x00, 0x0C,                                      // APP0, segment length 12
        0x20, 0x45, 0x49, 0x20, 0x61, 0x62, 0x63, 0x64,              // " EI abcd" — decoy
        0xFF, 0xD9,                                                  // thumbnail EOI — decoy
        0xFF, 0xDA, 0x00, 0x04, 0x01, 0x00,                          // SOS, segment length 4
        0x12, 0x34, 0xFF, 0x00, 0x56,                                // entropy data, FF stuffed
        0xFF, 0xD9,                                                  // real EOI
    };

    // =====================================================================
    //  The scanners' boundaries — found by the overnight sweep
    // =====================================================================

    /// <summary>
    /// <c>!</c> and <c>u</c> are the first and last characters of the base-85 alphabet (§7.4.3),
    /// so a payload of nothing else is measured. The control is <c>v</c>, the byte just above the
    /// alphabet, which refuses it (the byte just below <c>!</c> is a space, which is allowed).
    /// </summary>
    [Fact]
    public void Ascii85_TheAlphabetsEndCharactersArePartOfIt()
    {
        Assert.Equal(7, InlineImageLength.ComputeSelfDelimiting(
            Filtered(PdfName.A85), Bytes("!!uu!~> EI"), 0));
        Assert.Equal(-1, InlineImageLength.ComputeSelfDelimiting(
            Filtered(PdfName.A85), Bytes("!!vu!~> EI"), 0));
    }

    /// <summary>
    /// A literal run's length byte goes up to 127, meaning 128 bytes follow (§7.4.5). A run of
    /// exactly that length is walked over as data — its bytes include the EOD value 128, which a
    /// walk that took 127 for a repeat would stop at.
    /// </summary>
    [Fact]
    public void RunLength_TheLongestLiteralRunIsWalkedAsData()
    {
        var literal = new byte[128];
        for (int i = 0; i < literal.Length; i++) literal[i] = 128;   // every byte the EOD value
        byte[] payload = Cat(new byte[] { 127 }, literal, new byte[] { 128 });

        Assert.Equal(payload.Length, InlineImageLength.ComputeSelfDelimiting(
            Filtered(PdfName.RL), Cat(payload, Bytes(" EI")), 0));
    }

    /// <summary>
    /// A JPEG segment whose length is exactly 2 holds only its own length bytes (ISO/IEC 10918-1
    /// §B.1.1.4), which is legal: it is stepped over, and the EOI after it ends the image.
    /// </summary>
    [Fact]
    public void Dct_AnEmptySegmentOfLengthTwoIsSteppedOver()
    {
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xD9 };

        Assert.Equal(jpeg.Length, InlineImageLength.ComputeSelfDelimiting(
            Filtered(PdfName.DCT), Cat(jpeg, Bytes(" EI")), 0));
    }

    private static byte[] Cat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            System.Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }
}
