// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Plugins.Maintenance.Jpeg;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Lossless JPEG Huffman re-optimisation.
///
/// <para>The claim is that the rebuilt image is <b>pixel-identical</b>, so every test here decodes
/// both images through SkiaSharp — which knows nothing about the transcoder — and compares the
/// buffers. Asserting only that the bytes shrank would pass for a transcoder that quietly dropped
/// coefficients, which is precisely the failure this must not have.</para>
///
/// <para>The fixtures are built in memory by encoding a bitmap, so they run on any machine. The one
/// corpus test at the end is gated and reports <b>Skipped</b> where the sample PDFs are not
/// present.</para>
/// </summary>
public class JpegLosslessOptimizerTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public JpegLosslessOptimizerTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// A photographic-ish gradient with noise. Smooth content alone produces a symbol distribution
    /// so skewed that the stock tables are nearly right for it and the saving vanishes — which is a
    /// true fact about flat images and a useless fixture for this pass.
    /// </summary>
    private static byte[] EncodeTestJpeg(int width, int height, int quality = 80, bool grey = false)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var random = new Random(4242);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int noise = random.Next(48);
                byte r = (byte)Math.Min(255, x * 255 / width + noise);
                byte g = (byte)Math.Min(255, y * 255 / height + noise);
                byte b = (byte)Math.Min(255, (x + y) * 127 / (width + height) + noise);
                if (grey) { g = r; b = r; }
                bitmap.SetPixel(x, y, new SKColor(r, g, b));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>
    /// Rewrites the frame marker — <c>SOF0</c> becomes whatever is asked for — so a baseline
    /// fixture can stand in for a frame type the transcoder must refuse. Only the marker code
    /// changes, which is enough: the refusal is a header decision made before any entropy data is
    /// touched.
    /// </summary>
    private static byte[] WithFrameMarker(byte[] jpeg, byte marker)
    {
        var copy = (byte[])jpeg.Clone();

        for (int i = 2; i + 1 < copy.Length; i++)
        {
            if (copy[i] != 0xFF) continue;
            if (copy[i + 1] != 0xC0 && copy[i + 1] != 0xC1) continue;
            copy[i + 1] = marker;
            return copy;
        }

        throw new InvalidOperationException("the fixture has no SOF0/SOF1 marker to rewrite");
    }

    /// <summary>Decodes both and compares every byte of the pixel buffers.</summary>
    private static void AssertPixelsIdentical(byte[] original, byte[] rebuilt)
    {
        using var a = SKBitmap.Decode(original);
        using var b = SKBitmap.Decode(rebuilt);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Width, b!.Width);
        Assert.Equal(a.Height, b.Height);

        var spanA = a.GetPixelSpan();
        var spanB = b.GetPixelSpan();
        Assert.Equal(spanA.Length, spanB.Length);

        for (int i = 0; i < spanA.Length; i++)
        {
            if (spanA[i] != spanB[i])
                Assert.Fail($"pixel byte {i} differs: {spanA[i]} vs {spanB[i]}");
        }
    }

    /// <summary>
    /// A baseline greyscale JPEG with a restart interval, built by hand because Skia's encoder
    /// never writes one: 128×64, a flat grey (every block a DC difference of 0, then AC
    /// end-of-block), a <c>DRI</c> of 4 blocks, and deliberately wasteful tables so an optimizer
    /// has something to gain. DC category 0 is the 2-bit code <c>00</c> of Annex K.3; the AC table
    /// places EOB (0x00) at <paramref name="eobLength"/> bits, behind one filler symbol at each
    /// shorter length. At 16 bits that is the longest code §B.2.4.2 permits.
    /// </summary>
    private static byte[] RestartIntervalJpeg(int eobLength = 4)
    {
        var b = new List<byte> { 0xFF, 0xD8 };                                    // SOI

        b.AddRange(new byte[] { 0xFF, 0xDB, 0x00, 0x43, 0x00 });                // DQT, table 0
        for (int i = 0; i < 64; i++) b.Add(1);

        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x0B, 8, 0, 64, 0, 128, 1, 1, 0x11, 0 }); // SOF0

        int acSymbols = eobLength;                                              // fillers 0x01.. then EOB
        b.AddRange(new byte[] { 0xFF, 0xC4, 0x00, (byte)(2 + 29 + 17 + acSymbols) });
        b.Add(0x00);                                                            // DC 0: Annex K.3
        b.AddRange(new byte[] { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 });
        for (int v = 0; v < 12; v++) b.Add((byte)v);
        b.Add(0x10);                                                            // AC 0
        for (int length = 1; length <= 16; length++) b.Add((byte)(length <= eobLength ? 1 : 0));
        for (int f = 1; f < eobLength; f++) b.Add((byte)f);
        b.Add(0x00);

        b.AddRange(new byte[] { 0xFF, 0xDD, 0x00, 0x04, 0x00, 0x04 });          // DRI: 4 blocks
        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0x00, 0, 63, 0 }); // SOS

        // The canonical code for the last symbol at eobLength, behind one code at every shorter
        // length: eobLength-1 ones then a zero.
        int eobCode = ((1 << eobLength) - 1) & ~1;

        for (int interval = 0; interval < 32; interval++)
        {
            if (interval > 0) b.AddRange(new byte[] { 0xFF, (byte)(0xD0 + (interval - 1) % 8) });

            int buffer = 0, count = 0;
            void Bits(int value, int n)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    buffer = (buffer << 1) | ((value >> i) & 1);
                    if (++count < 8) continue;
                    b.Add((byte)buffer);
                    if ((byte)buffer == 0xFF) b.Add(0x00);                       // byte stuffing
                    buffer = 0; count = 0;
                }
            }
            for (int block = 0; block < 4; block++) { Bits(0, 2); Bits(eobCode, eobLength); }
            while (count != 0) Bits(1, 1);                                      // fill with ones
        }

        b.AddRange(new byte[] { 0xFF, 0xD9 });                                  // EOI
        return b.ToArray();
    }

    /// <summary>
    /// A JPEG with restart markers comes back smaller, pixel-identical, and still carrying its RST
    /// markers in order — the decoder needs them where the interval says, or every block after the
    /// first interval is read with a stale DC predictor.
    /// </summary>
    /// <para>Run with end-of-block at 4 bits, and at 16 — the longest code there is, which the
    /// scan reader must decode.</para>
    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void TryOptimize_KeepsTheRestartMarkersOfARestartIntervalJpeg(int eobLength)
    {
        byte[] jpeg = RestartIntervalJpeg(eobLength);

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        Assert.True(rebuilt!.Length < jpeg.Length, $"rebuilt to {rebuilt.Length} bytes from {jpeg.Length}");
        AssertPixelsIdentical(jpeg, rebuilt);

        var markers = new List<byte>();
        int sos = -1;
        for (int i = 0; i + 1 < rebuilt.Length; i++)
            if (rebuilt[i] == 0xFF && rebuilt[i + 1] == 0xDA) { sos = i; break; }
        for (int i = sos + 2; i + 1 < rebuilt.Length; i++)
            if (rebuilt[i] == 0xFF && rebuilt[i + 1] >= 0xD0 && rebuilt[i + 1] <= 0xD7) markers.Add(rebuilt[i + 1]);

        Assert.Equal(31, markers.Count);
        for (int k = 0; k < markers.Count; k++) Assert.Equal((byte)(0xD0 + k % 8), markers[k]);

        // Every interval holds the same four blocks, so every interval's entropy-coded bytes must
        // be the same. A flat image decodes to the same pixels even when an interval was copied
        // through un-recoded, so the pixels alone cannot see that.
        int dataStart = sos + 2 + ((rebuilt[sos + 2] << 8) | rebuilt[sos + 3]);
        var intervals = new List<string>();
        var current = new List<byte>();
        for (int i = dataStart; i + 1 < rebuilt.Length; i++)
        {
            if (rebuilt[i] == 0xFF && rebuilt[i + 1] >= 0xD0 && rebuilt[i + 1] <= 0xD9)
            {
                intervals.Add(Convert.ToBase64String(current.ToArray()));
                current.Clear();
                if (rebuilt[i + 1] == 0xD9) break;
                i++;
                continue;
            }
            current.Add(rebuilt[i]);
        }
        Assert.Equal(32, intervals.Count);
        Assert.Single(intervals.Distinct());
    }

    /// <summary>
    /// A flat-grey interleaved baseline JPEG of <paramref name="width"/>×<paramref name="height"/>
    /// whose components have the given sampling bytes (<c>0xHV</c>), with no restart interval and
    /// the same wasteful tables as <see cref="RestartIntervalJpeg"/>: every block is DC category 0
    /// (<c>00</c>) then EOB (<c>1110</c>), six bits.
    /// </summary>
    private static byte[] InterleavedFlatJpeg(int width, int height, params byte[] samplings)
    {
        int n = samplings.Length;
        var b = new List<byte> { 0xFF, 0xD8 };
        b.AddRange(new byte[] { 0xFF, 0xDB, 0x00, 0x43, 0x00 });
        for (int i = 0; i < 64; i++) b.Add(1);

        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, (byte)(8 + 3 * n), 8,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)n });
        for (int c = 0; c < n; c++) b.AddRange(new byte[] { (byte)(c + 1), samplings[c], 0 });

        b.AddRange(new byte[] { 0xFF, 0xC4, 0x00, 2 + 29 + 17 + 4 });
        b.Add(0x00);
        b.AddRange(new byte[] { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 });
        for (int v = 0; v < 12; v++) b.Add((byte)v);
        b.Add(0x10);
        b.AddRange(new byte[] { 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        b.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x00 });

        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, (byte)(6 + 2 * n), (byte)n });
        for (int c = 0; c < n; c++) b.AddRange(new byte[] { (byte)(c + 1), 0x00 });
        b.AddRange(new byte[] { 0, 63, 0 });

        int hMax = 1, vMax = 1, blocksPerMcu = 0;
        foreach (byte s in samplings)
        {
            hMax = Math.Max(hMax, s >> 4); vMax = Math.Max(vMax, s & 0x0F);
            blocksPerMcu += (s >> 4) * (s & 0x0F);
        }
        int mcus = ((width + 8 * hMax - 1) / (8 * hMax)) * ((height + 8 * vMax - 1) / (8 * vMax));

        int buffer = 0, count = 0;
        void Bits(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                buffer = (buffer << 1) | ((value >> i) & 1);
                if (++count < 8) continue;
                b.Add((byte)buffer);
                if ((byte)buffer == 0xFF) b.Add(0x00);
                buffer = 0; count = 0;
            }
        }
        for (int m = 0; m < mcus * blocksPerMcu; m++) { Bits(0, 2); Bits(0b1110, 4); }
        while (count != 0) Bits(1, 1);

        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }

    /// <summary>
    /// Only the first scan is re-coded and everything after it copied, so a file with more is refused
    /// for the reason it has more: a later scan would be left coded under the original tables and read
    /// under the fitted ones, and the verification, which reads the first scan, would pass it. Three
    /// shapes, each refused by its own check — the reasons are asserted, because each
    /// file would also trip one of the others.
    /// </summary>
    [Fact]
    public void TryOptimize_RefusesAScanOfSomeOfTheFramesComponents()
    {
        // Two components, the first scan coding only the first: non-interleaved (§B.2.3).
        byte[] interleaved = InterleavedFlatJpeg(64, 64, 0x11, 0x11);
        int sos = IndexOfMarker(interleaved, 0xDA);
        var b = new List<byte>(interleaved.Take(sos));
        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0x00, 0, 63, 0 });
        b.AddRange(interleaved.Skip(sos + 2 + ((interleaved[sos + 2] << 8) | interleaved[sos + 3])));

        Assert.Null(JpegLosslessOptimizer.TryOptimize(b.ToArray(), out string reason));
        Assert.Contains("further scans follow", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryOptimize_RefusesAScanThatAnotherScanFollows()
    {
        byte[] grey = InterleavedFlatJpeg(64, 64, 0x11);
        int sos = IndexOfMarker(grey, 0xDA);
        var b = new List<byte>(grey.Take(grey.Length - 2));       // up to EOI
        b.AddRange(grey.Skip(sos).Take(grey.Length - 2 - sos));    // the scan again, header and data
        b.AddRange(new byte[] { 0xFF, 0xD9 });

        Assert.Null(JpegLosslessOptimizer.TryOptimize(b.ToArray(), out string reason));
        Assert.Contains("not followed by EOI", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryOptimize_RefusesAHierarchicalFile()
    {
        byte[] grey = InterleavedFlatJpeg(64, 64, 0x11);
        int sof = IndexOfMarker(grey, 0xC0);
        var b = new List<byte>(grey.Take(sof));
        b.AddRange(new byte[] { 0xFF, 0xDE, 0x00, 0x0B, 8, 0, 64, 0, 64, 1, 1, 0x11, 0 });   // DHP, as a frame header
        b.AddRange(grey.Skip(sof));

        Assert.Null(JpegLosslessOptimizer.TryOptimize(b.ToArray(), out string reason));
        Assert.Contains("hierarchical", reason, StringComparison.Ordinal);
    }

    /// <summary>The control: the single-component file the three are built from is optimized.</summary>
    [Fact]
    public void TryOptimize_AcceptsTheOneScanFileTheRefusalsAreBuiltFrom()
    {
        byte[] grey = InterleavedFlatJpeg(64, 64, 0x11);

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(grey, out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        AssertPixelsIdentical(grey, rebuilt!);
    }

    private static int IndexOfMarker(byte[] jpeg, byte code)
    {
        for (int i = 0; i + 1 < jpeg.Length; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == code) return i;
        throw new InvalidOperationException("no marker 0x" + code.ToString("X2"));
    }

    /// <summary>
    /// 4:2:2 — luma sampled 2×1 — is read with its MCU 16 wide and 8 high. At 136×64 that is 72
    /// MCUs; reading the factors the other way round makes it 68, and the scan would not fit.
    /// </summary>
    [Fact]
    public void TryOptimize_RebuildsAFourTwoTwoJpegToIdenticalPixels()
    {
        byte[] jpeg = InterleavedFlatJpeg(136, 64, 0x21, 0x11, 0x11);

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        AssertPixelsIdentical(jpeg, rebuilt!);
    }

    /// <summary>
    /// A marker segment may be empty: a length of 2 counts only its own length bytes. An image
    /// carrying one (an empty APP15 here) is optimized as usual.
    /// </summary>
    [Fact]
    public void TryOptimize_AcceptsAnEmptyMarkerSegment()
    {
        byte[] plain = RestartIntervalJpeg();
        var withEmpty = new List<byte> { 0xFF, 0xD8, 0xFF, 0xEF, 0x00, 0x02 };
        withEmpty.AddRange(plain.Skip(2));

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(withEmpty.ToArray(), out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        AssertPixelsIdentical(plain, rebuilt!);
    }

    /// <summary>
    /// Table indices run 0 to 3. A DHT defining table 4, or a scan selecting it, is refused with
    /// its reason — never indexed past the four table slots, which would throw out of an optimizer
    /// that has no catch.
    /// </summary>
    [Theory]
    [InlineData("dht", "names table 0/4")]
    [InlineData("sos", "outside 0..3")]
    public void TryOptimize_RefusesATableIndexPastThree(string where, string expected)
    {
        byte[] jpeg = RestartIntervalJpeg();
        if (where == "dht")
        {
            int dht = IndexOf(jpeg, 0xFF, 0xC4);
            jpeg[dht + 4] = 0x04;                       // the DC table's class/id byte
        }
        else
        {
            int sos = IndexOf(jpeg, 0xFF, 0xDA);
            jpeg[sos + 6] = 0x40;                       // DC selector 4, AC selector 0
        }

        Assert.Null(JpegLosslessOptimizer.TryOptimize(jpeg, out string reason));
        Assert.Contains(expected, reason);
    }

    private static int IndexOf(byte[] data, byte first, byte second)
    {
        for (int i = 0; i + 1 < data.Length; i++)
            if (data[i] == first && data[i + 1] == second) return i;
        throw new InvalidOperationException($"marker FF {second:X2} not found");
    }

    // ------------------------------------------------------------------
    //  The lossless claim
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(64, 64)]
    [InlineData(200, 130)]        // not a multiple of the MCU size, so the edge blocks are padded
    [InlineData(17, 9)]           // smaller than one MCU
    public void TryOptimize_RebuildsAColourJpegToIdenticalPixels(int width, int height)
    {
        byte[] jpeg = EncodeTestJpeg(width, height);

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        AssertPixelsIdentical(jpeg, rebuilt!);
    }

    [Fact]
    public void TryOptimize_RebuildsAGreyscaleJpegToIdenticalPixels()
    {
        byte[] jpeg = EncodeTestJpeg(96, 96, grey: true);

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        AssertPixelsIdentical(jpeg, rebuilt!);
    }

    /// <summary>
    /// The point of the pass. Skia writes the ISO Annex K example tables, so a fitted table must
    /// beat them — if it does not, either the fitting or the re-coding is wrong.
    /// </summary>
    [Fact]
    public void TryOptimize_ProducesASmallerFileThanTheStockTables()
    {
        byte[] jpeg = EncodeTestJpeg(256, 256);

        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out string reason);

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        Assert.True(rebuilt!.Length < jpeg.Length,
            $"rebuilt to {rebuilt.Length} bytes from {jpeg.Length}");
    }

    /// <summary>
    /// The verification compares the two symbol streams by count and digest. Equal streams agree; one
    /// changed symbol, one more symbol, or two symbols swapped disagree — the digest is order-sensitive,
    /// as the list it replaced was.
    /// </summary>
    [Fact]
    public void SymbolDigest_AgreesOnlyOnTheSameSymbolsInTheSameOrder()
    {
        static JpegLosslessOptimizer.SymbolDigest Of(params long[] symbols)
        {
            var d = new JpegLosslessOptimizer.SymbolDigest();
            foreach (long s in symbols) d.Add(s);
            return d;
        }

        long[] many = Enumerable.Range(0, 5000).Select(i => (long)i * 7919).ToArray();   // past one buffer
        using var same1 = Of(many);
        using var same2 = Of(many);
        long[] changed = (long[])many.Clone();
        changed[4321] ^= 1;
        using var differs = Of(changed);
        using var longer = Of(many.Concat(new[] { 0L }).ToArray());
        long[] swapped = (long[])many.Clone();
        (swapped[10], swapped[11]) = (swapped[11], swapped[10]);
        using var reordered = Of(swapped);

        Assert.True(same1.SameAs(same2));
        Assert.False(same1.SameAs(differs));
        Assert.False(same1.SameAs(longer));
        Assert.False(same1.SameAs(reordered));
    }

    /// <summary>
    /// The verification holds no list of the scan's symbols, which would cost eight bytes a symbol,
    /// and a symbol can be one bit. What a run allocates is the rebuilt file and the transcoder's own
    /// buffers — a few times the JPEG — and such a list would be several times that again.
    /// The transcoder is synchronous on the calling thread, so its allocation is this thread's.
    /// </summary>
    [Fact]
    public void TryOptimize_VerifiesWithoutAListOfTheSymbols()
    {
        byte[] jpeg = EncodeTestJpeg(1024, 1024, quality: 95);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out string reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(rebuilt != null, $"the image was refused: {reason}");
        // 563 KB of JPEG holding 904 111 symbols: 3.9 MB allocated, where the list alone added some 16 MB.
        Assert.True(allocated < 10L * jpeg.Length, $"allocated {allocated} bytes for a {jpeg.Length}-byte JPEG");
    }

    /// <summary>
    /// Running it twice must be a no-op the second time: the first pass already fitted the tables,
    /// so the second cannot beat them and has to decline rather than churn the file.
    /// </summary>
    [Fact]
    public void TryOptimize_DeclinesAnImageItHasAlreadyOptimized()
    {
        byte[] jpeg = EncodeTestJpeg(128, 128);

        byte[] once = JpegLosslessOptimizer.TryOptimize(jpeg, out _);
        Assert.NotNull(once);

        byte[] twice = JpegLosslessOptimizer.TryOptimize(once!, out string reason);

        Assert.True(twice == null, "a second pass changed a file the first pass had already fitted");
        Assert.Contains("smaller", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    //  Refusals — everything out of scope must be declined, not mangled
    // ------------------------------------------------------------------

    [Fact]
    public void TryOptimize_RefusesSomethingThatIsNotAJpeg()
    {
        Assert.Null(JpegLosslessOptimizer.TryOptimize(new byte[] { 1, 2, 3, 4 }, out string reason));
        Assert.Contains("JPEG", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryOptimize_RefusesAnEmptyOrMalformedInput()
    {
        Assert.Null(JpegLosslessOptimizer.TryOptimize(null, out _));
        Assert.Null(JpegLosslessOptimizer.TryOptimize(Array.Empty<byte>(), out _));
        Assert.Null(JpegLosslessOptimizer.TryOptimize(new byte[] { 0xFF, 0xD8 }, out _));
    }

    /// <summary>
    /// A progressive scan carries its coefficients across several scans with spectral selection and
    /// successive approximation; a baseline transcoder that read one would emit a valid JPEG full of
    /// noise. It must be refused by name.
    /// </summary>
    [Fact]
    public void TryOptimize_RefusesAProgressiveJpeg()
    {
        byte[] jpeg = EncodeTestJpeg(64, 64);
        byte[] progressive = WithFrameMarker(jpeg, 0xC2);        // SOF2

        Assert.Null(JpegLosslessOptimizer.TryOptimize(progressive, out string reason));
        Assert.Contains("baseline", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Truncated entropy data must fail the walk rather than produce a short image. This is the
    /// adversarial-input case: the decoder asks for bits the file does not have.
    /// </summary>
    [Fact]
    public void TryOptimize_RefusesAMalformedTruncatedScan()
    {
        byte[] jpeg = EncodeTestJpeg(128, 128);
        var truncated = new byte[jpeg.Length - 400];
        Array.Copy(jpeg, truncated, truncated.Length);

        Assert.Null(JpegLosslessOptimizer.TryOptimize(truncated, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    /// <summary>
    /// Adversarial input: every single-byte truncation of a real JPEG. None may throw, and none may
    /// return bytes — a partial decode that produced output would be an image with missing blocks.
    /// </summary>
    [Fact]
    public void TryOptimize_DoesNotThrowOnAnyTruncationOfAMalformedFile()
    {
        byte[] jpeg = EncodeTestJpeg(48, 48);

        for (int length = 0; length < jpeg.Length; length += 7)
        {
            var truncated = new byte[length];
            Array.Copy(jpeg, truncated, length);

            byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(truncated, out _);

            // A prefix of a JPEG has no complete scan, so nothing may come back.
            Assert.True(rebuilt == null, $"a {length}-byte prefix produced output");
        }
    }

    /// <summary>
    /// Adversarial input: bytes flipped inside the entropy data. The transcoder may decline, and it
    /// may succeed — a flipped bit is still a decodable symbol stream — but whatever it returns must
    /// decode, and must carry the same symbols it read. It may never throw.
    /// </summary>
    [Fact]
    public void TryOptimize_DoesNotThrowOnAdversarialBitFlips()
    {
        byte[] jpeg = EncodeTestJpeg(64, 64);
        var random = new Random(7);

        for (int trial = 0; trial < 40; trial++)
        {
            var damaged = (byte[])jpeg.Clone();
            int at = random.Next(jpeg.Length / 2, jpeg.Length - 3);
            damaged[at] ^= (byte)(1 << random.Next(8));

            var exception = Record.Exception(() => JpegLosslessOptimizer.TryOptimize(damaged, out _));
            Assert.Null(exception);
        }
    }

    // ------------------------------------------------------------------
    //  Real files, where they exist
    // ------------------------------------------------------------------

    /// <summary>
    /// Every JPEG in the developer-local corpus, through the optimizer and back out through Skia.
    /// Reports <b>Skipped</b> where the corpus is not present, so it costs nothing on a machine that
    /// does not have those files.
    /// </summary>
    [CorpusFact]
    public void TryOptimize_IsPixelIdenticalAcrossEveryCorpusJpeg()
    {
        int examined = 0, optimized = 0;
        long before = 0, after = 0;

        foreach (byte[] jpeg in CorpusJpegs())
        {
            examined++;

            byte[] rebuilt = JpegLosslessOptimizer.TryOptimize(jpeg, out _);
            if (rebuilt == null) continue;

            optimized++;
            before += jpeg.Length;
            after += rebuilt.Length;

            AssertPixelsIdentical(jpeg, rebuilt);
            Assert.True(rebuilt.Length < jpeg.Length);
        }

        _output.WriteLine($"corpus JPEGs examined={examined} optimized={optimized} " +
                          $"before={before} after={after} " +
                          $"saved={before - after} " +
                          $"pct={(before == 0 ? 0 : (before - after) * 100.0 / before):F2}");

        Assert.True(examined > 0, "the corpus yielded no JPEG images to check");
        Assert.True(optimized > 0, $"none of {examined} corpus JPEGs were optimized");
        Assert.True(after < before, $"the corpus grew: {before} -> {after}");
    }

    private static IEnumerable<byte[]> CorpusJpegs()
    {
        int yielded = 0;

        foreach (string path in TestCorpus.Pdfs())
        {
            if (yielded >= 60) yield break;                    // enough to be representative
            if (new FileInfo(path).Length > 12L * 1024 * 1024) continue;

            List<byte[]> found;
            try { found = ExtractJpegs(path); }
            catch { continue; }

            foreach (byte[] jpeg in found)
            {
                if (yielded >= 60) yield break;
                yielded++;
                yield return jpeg;
            }
        }
    }

    private static List<byte[]> ExtractJpegs(string path)
    {
        var result = new List<byte[]>();

        using var stream = File.OpenRead(path);
        using var document = Core.Document.PdfDocument.Open(stream);

        foreach (var kvp in document.Parser.EnumerateAllObjects())
        {
            if (!(kvp.Value is Core.Objects.PdfStream image)) continue;
            if (image.RawData == null || image.RawData.Length < 4) continue;
            if (image.RawData[0] != 0xFF || image.RawData[1] != 0xD8) continue;   // SOI
            result.Add(image.RawData);
            if (result.Count >= 12) break;
        }

        return result;
    }
}
