// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The PNG scanline predictor pass.
///
/// <para>Two claims have to be tested and they fail differently. <b>Correctness</b> is that the
/// samples coming back out are the samples that went in — a predictor with the wrong row stride
/// does not throw, it decodes to a plausible smear, so every test here that writes a predicted
/// stream also decodes it and compares. <b>Effectiveness</b> is that the filter is chosen per row;
/// asserting only "the file got smaller" passes for the fixed-<i>Up</i> encoder this replaced, so
/// the size assertions compare against a fixed-Up encoding of the same samples computed here.</para>
/// </summary>
public class PngPredictorTests
{
    private const int Width = 64;
    private const int Height = 64;

    // ------------------------------------------------------------------
    //  Rasters, each chosen so one particular filter is clearly the best
    // ------------------------------------------------------------------

    /// <summary>
    /// Every row is an arithmetic ramp, and the <b>step differs per row</b>. Left-differencing
    /// (<i>Sub</i>) turns each row into one repeated byte; up-differencing turns it into a fresh
    /// ramp per row, which deflates far worse. Rows that shared a step would compress well under
    /// either filter and would not separate the two encoders.
    /// </summary>
    private static byte[] PerRowRampRaster()
    {
        var samples = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            int step = 1 + (y % 7);
            int seed = (y * 53) % 251;
            for (int x = 0; x < Width; x++)
                samples[y * Width + x] = (byte)(seed + step * x);
        }
        return samples;
    }

    /// <summary>
    /// One noisy row repeated down the image: the case <i>Up</i> was written for. The row is noise
    /// rather than a ramp on purpose — a row that predicts horizontally <i>as well</i> would leave
    /// several filters tied at zero and the assertion would be about the tie-break rather than
    /// about the choice.
    /// </summary>
    private static byte[] VerticallyConstantRaster()
    {
        var row = new byte[Width];
        new Random(11).NextBytes(row);

        var samples = new byte[Width * Height];
        for (int y = 0; y < Height; y++) Buffer.BlockCopy(row, 0, samples, y * Width, Width);
        return samples;
    }

    /// <summary>
    /// Alternating character: every even row repeats the row above it, every odd row is a fresh
    /// ramp. No single filter is right for both, which is the whole premise of choosing per row.
    /// </summary>
    private static byte[] MixedRaster()
    {
        var samples = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            if (y > 0 && y % 2 == 0)
            {
                Buffer.BlockCopy(samples, (y - 1) * Width, samples, y * Width, Width);
                continue;
            }

            int step = 1 + (y % 5);
            int seed = (y * 37) % 251;
            for (int x = 0; x < Width; x++)
                samples[y * Width + x] = (byte)(seed + step * x);
        }
        return samples;
    }

    /// <summary>
    /// Deterministic pseudo-noise. Nothing predicts it, so the pass must decline: a predicted,
    /// deflated copy of incompressible data is <i>larger</i> than the data.
    /// </summary>
    private static byte[] NoiseRaster()
    {
        var samples = new byte[Width * Height];
        var random = new Random(20260817);
        random.NextBytes(samples);
        return samples;
    }

    /// <summary>
    /// Samples drawn at random from a three-value palette — the shape of an indexed image, a screen
    /// shot or flat business graphics, and most of the lossless images in a real document.
    ///
    /// <para>Plain Flate codes three symbols in well under two bits each. Differencing turns three
    /// symbols into nine, and the Huffman stage pays for every one, so prediction <b>loses</b> here
    /// while still beating the uncompressed original easily — which is exactly the case the old
    /// "smaller than what is stored" rule got wrong.</para>
    /// </summary>
    private static byte[] SmallPaletteRaster()
    {
        var palette = new byte[] { 0, 17, 34 };
        var samples = new byte[Width * Height];
        var random = new Random(31337);
        for (int i = 0; i < samples.Length; i++) samples[i] = palette[random.Next(palette.Length)];
        return samples;
    }

    // ------------------------------------------------------------------
    //  PngPredictorEncoder — the filter choice itself
    // ------------------------------------------------------------------

    /// <summary>
    /// The improvement in one assertion: on rows that look nothing like the row above them, no row
    /// may be filtered with <i>Up</i> — which is what the encoder this replaced wrote on every row
    /// of every image. Which of the left-predicting filters wins is deliberately not asserted:
    /// <i>Sub</i> and <i>Paeth</i> both reduce a ramp to its step and separate only on the first
    /// byte of each row, so pinning one of them would be pinning a tie-break rather than the
    /// decision.
    /// </summary>
    [Fact]
    public void Encode_NeverFiltersFromAboveWhenTheRowAboveIsNoPredictor()
    {
        byte[] filtered = PngPredictorEncoder.Encode(PerRowRampRaster(), Width, 1, 8, Height);

        Assert.NotNull(filtered);
        for (int row = 0; row < Height; row++)
            Assert.NotEqual(2, filtered[row * (Width + 1)]);      // 2 = Up
    }

    /// <summary>
    /// The counterpart, so the fix cannot be "swap one fixed filter for another": a row that
    /// repeats the row above must be filtered with <i>Up</i>, which zeroes it. Row 0 has no row
    /// above it and is left to whatever wins on its own merits.
    /// </summary>
    [Fact]
    public void Encode_ChoosesUpForRowsThatRepeatTheRowAbove()
    {
        byte[] filtered = PngPredictorEncoder.Encode(VerticallyConstantRaster(), Width, 1, 8, Height);

        Assert.NotNull(filtered);
        for (int row = 1; row < Height; row++)
            Assert.Equal(2, filtered[row * (Width + 1)]);         // 2 = Up
    }

    /// <summary>
    /// The premise of the whole change: one image can need different filters on different rows.
    /// Any fixed-filter encoder fails this whatever filter it fixed on.
    /// </summary>
    [Fact]
    public void Encode_VariesTheFilterFromRowToRowWithinOneImage()
    {
        byte[] filtered = PngPredictorEncoder.Encode(MixedRaster(), Width, 1, 8, Height);

        Assert.NotNull(filtered);

        var used = new System.Collections.Generic.HashSet<byte>();
        for (int row = 0; row < Height; row++) used.Add(filtered[row * (Width + 1)]);

        Assert.True(used.Count > 1,
            "every row was filtered with " + string.Join(",", used));
    }

    /// <summary>
    /// Exact, not "at least". A buffer longer than the geometry describes means the two disagree,
    /// and filtering the part that fits would silently drop the rest while reporting a saving.
    /// </summary>
    [Theory]
    [InlineData(1)]        // one byte short
    [InlineData(-1)]       // one byte long
    public void Encode_RefusesAMalformedSampleCountThatContradictsTheGeometry(int shortfall)
    {
        var samples = new byte[Width * Height - shortfall];

        Assert.Null(PngPredictorEncoder.Encode(samples, Width, 1, 8, Height));
    }

    /// <summary>
    /// §7.4.4.4 permits 1, 2, 4, 8 and 16 bits per component and nothing else. A depth the decoder
    /// would compute a different stride for has to be refused, not encoded through a guess.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(32)]
    public void Encode_RefusesAnUnsupportedBitDepth(int bitsPerComponent)
    {
        long rowBytes = ((long)Width * bitsPerComponent + 7) / 8;
        var samples = new byte[rowBytes * Height];

        Assert.Null(PngPredictorEncoder.Encode(samples, Width, 1, bitsPerComponent, Height));
    }

    /// <summary>
    /// The encoder and <c>Core.IO.Filters.PredictorHelper</c> derive the row length and the pixel
    /// stride independently, and a disagreement between them is invisible — the image decodes,
    /// wrongly. Every permitted depth is therefore pushed through the real decoder.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    [InlineData(8, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 3)]
    public void Encode_RoundTripsThroughTheDecoderAtEveryPermittedDepthAndArity(
        int bitsPerComponent, int colors)
    {
        long rowBytes = ((long)Width * colors * bitsPerComponent + 7) / 8;
        var samples = new byte[rowBytes * Height];
        var random = new Random(7);
        random.NextBytes(samples);

        byte[] filtered = PngPredictorEncoder.Encode(samples, Width, colors, bitsPerComponent, Height);
        Assert.NotNull(filtered);

        Assert.Equal(samples, DecodePredicted(filtered, Width, colors, bitsPerComponent));
    }

    // ------------------------------------------------------------------
    //  PdfOptimizer — the pass end to end
    // ------------------------------------------------------------------

    /// <summary>
    /// The safety claim of the whole pass: storage changes, samples do not.
    /// </summary>
    [Fact]
    public void Optimize_PreservesEverySampleThroughThePredictorRoundTrip()
    {
        byte[] raster = PerRowRampRaster();
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, PredictorOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPredicted);
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// The effectiveness claim, stated so that the encoder this replaced would fail it: the stored
    /// stream must be smaller than the same samples under a fixed <i>Up</i> filter at the same
    /// deflate level. "Smaller than the raw samples" would pass for either encoder.
    /// </summary>
    [Fact]
    public void Optimize_ChoosesPerRowFiltersThatBeatAFixedUpPrediction()
    {
        byte[] raster = PerRowRampRaster();
        byte[] pdf = BuildPdfWithImage(raster);

        Optimize(pdf, PredictorOnly(), out byte[] optimized);

        int fixedUp = FlateEncodeFilter.Encode(
            FixedUpFilter(raster, Width, Height), CompressionLevel.Optimal).Length;

        Assert.True(StoredImageLength(optimized) < fixedUp,
            $"per-row filtering stored {StoredImageLength(optimized)} bytes, fixed-Up {fixedUp}");
    }

    /// <summary>
    /// A 4-bit image is ordinary, spec-conformant input (§7.4.4.4 names the depth explicitly) and
    /// the pass used to require <c>/BitsPerComponent 8</c>, so every 1-, 2-, 4- and 16-bit image in
    /// a document was skipped in silence.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public void Optimize_PredictsAnImageWhoseDepthIsNotEightBits(int bitsPerComponent)
    {
        int rowBytes = (Width * bitsPerComponent + 7) / 8;
        var raster = new byte[rowBytes * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < rowBytes; x++)
                raster[y * rowBytes + x] = (byte)(x * 5 + y);        // predicts from the left

        byte[] pdf = BuildPdfWithImage(raster, bitsPerComponent: bitsPerComponent);

        var result = Optimize(pdf, PredictorOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPredicted);
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// §8.9.6.2: a stencil mask has one bit per sample and <b>no colour space at all</b>. Asking it
    /// for a component count is the wrong question, and the pass used to treat the missing
    /// <c>/ColorSpace</c> as an unreadable one — reporting a tolerated failure, which is the
    /// diagnostic a caller reads as "part of your document was left unoptimized because something
    /// went wrong". Nothing went wrong; a mask simply has no colour space.
    /// </summary>
    [Fact]
    public void Optimize_DoesNotReportAnImageMaskAsAnUnreadableColourSpace()
    {
        int stride = (Width + 7) / 8;
        var raster = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < stride; x++)
                raster[y * stride + x] = x < stride / 2 ? (byte)0x00 : (byte)0xFF;

        byte[] pdf = BuildPdfWithImage(raster, imageMask: true);

        var result = Optimize(pdf, PredictorOnly(), out byte[] optimized);

        Assert.Equal(0, result.ToleratedFailures);
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// And when a mask's samples do predict, it is re-encoded like any other image — the pass used
    /// to refuse every one of them before looking.
    ///
    /// <para>The fixture ramps along each row because that is what separates the rules being
    /// tested: bitonal content usually favours plain Flate, so a mask that merely <i>compresses</i>
    /// would be declined by the plain-Flate comparison and prove nothing about whether masks are
    /// reached at all.</para>
    /// </summary>
    [Fact]
    public void Optimize_PredictsAnImageMaskWhoseSamplesPredict()
    {
        int stride = (Width + 7) / 8;
        var raster = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < stride; x++)
                raster[y * stride + x] = (byte)(y * 4 + x);

        byte[] pdf = BuildPdfWithImage(raster, imageMask: true);

        var result = Optimize(pdf, PredictorOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPredicted);
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// The pass must judge itself against <b>plain Flate</b>, not against the bytes that happen to
    /// be in the file. An uncompressed original is easy to beat, so the old rule committed a
    /// predicted form that was 13 % <i>larger</i> than simply deflating the same samples — measured
    /// across a real corpus, on indexed and flat-graphic images, which is most lossless image
    /// content. The two assertions at the end are what make this a test of the rule rather than of
    /// the encoder: prediction does beat the stored bytes here, and still must be declined.
    /// </summary>
    [Fact]
    public void Optimize_DeclinesPredictionThatLosesToPlainFlateEvenThoughItBeatsTheStoredBytes()
    {
        byte[] raster = SmallPaletteRaster();
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, PredictorOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPredicted);
        Assert.Null(FilterNameOf(optimized));                     // left for CompressStreams
        Assert.Equal(raster, DecodedImageSamples(optimized));

        byte[] filtered = PngPredictorEncoder.Encode(raster, Width, 1, 8, Height);
        int predicted = FlateEncodeFilter.Encode(filtered, CompressionLevel.Optimal).Length;
        int plain = FlateEncodeFilter.Encode(raster, CompressionLevel.Optimal).Length;

        Assert.True(predicted < raster.Length,
            $"the fixture does not exercise the rule: prediction ({predicted}) must beat the stored " +
            $"{raster.Length} bytes, or the pass would have declined for the wrong reason");
        Assert.True(plain < predicted,
            $"the fixture does not exercise the rule: plain Flate ({plain}) must beat prediction ({predicted})");
    }

    /// <summary>
    /// Trial-and-commit: samples that do not predict must be left exactly as they were, not stored
    /// bigger with a predictor attached.
    /// </summary>
    [Fact]
    public void Optimize_LeavesAnImageAloneWhenPredictionWouldNotShrinkIt()
    {
        byte[] raster = NoiseRaster();
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, PredictorOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPredicted);
        Assert.Null(FilterNameOf(optimized));                     // still unfiltered
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// The <see cref="OptimizationOptions.Lossless"/> preset runs this pass <i>and</i>
    /// <see cref="OptimizationOptions.RecompressExistingStreams"/>, which decodes a Flate stream and
    /// re-encodes it — dropping <c>/DecodeParms</c> as it goes, correctly, because it re-encodes the
    /// un-predicted bytes. Nothing structural forces those two to agree about what the stream holds,
    /// so the interaction is pinned rather than left to hold by inspection.
    /// </summary>
    [Fact]
    public void Optimize_KeepsTheSamplesIntactUnderTheFullLosslessPreset()
    {
        byte[] raster = PerRowRampRaster();
        byte[] pdf = BuildPdfWithImage(raster);

        Optimize(pdf, OptimizationOptions.Lossless, out byte[] optimized);

        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Only the predictor pass, so a size or filter change cannot come from Flate, dedup or object
    /// streams.
    /// </summary>
    private static OptimizationOptions PredictorOnly() => new OptimizationOptions
    {
        UsePngPredictorForImages = true,
        CompressStreams = false,
        RemoveDuplicates = false,
        RemoveUnusedObjects = false,
    };

    /// <summary>The encoder this pass replaced: PNG filter type 2 on every row.</summary>
    private static byte[] FixedUpFilter(byte[] samples, int rowBytes, int rows)
    {
        var filtered = new byte[rows * (rowBytes + 1)];
        int src = 0, dst = 0;
        for (int y = 0; y < rows; y++)
        {
            filtered[dst++] = 2;
            for (int x = 0; x < rowBytes; x++)
            {
                byte above = y == 0 ? (byte)0 : samples[src - rowBytes];
                filtered[dst++] = (byte)(samples[src] - above);
                src++;
            }
        }
        return filtered;
    }

    /// <summary>
    /// Runs predicted bytes back through the real Flate + predictor decoder, which is the only
    /// witness that matters — the encoder and the decoder derive the row stride separately.
    /// </summary>
    private static byte[] DecodePredicted(byte[] filtered, int columns, int colors,
        int bitsPerComponent)
    {
        var stream = new PdfStream();
        stream.RawData = FlateEncodeFilter.Encode(filtered, CompressionLevel.Optimal);
        stream.Set(PdfName.Filter, PdfName.FlateDecode);
        stream.Set(PdfName.Length, new PdfNumber(stream.RawData.Length));

        var parms = new PdfDictionary();
        parms.Set(PdfName.Predictor, new PdfNumber(PngPredictorEncoder.PngOptimum));
        parms.Set(PdfName.Colors, new PdfNumber(colors));
        parms.Set(PdfName.BitsPerComponent, new PdfNumber(bitsPerComponent));
        parms.Set(PdfName.Columns, new PdfNumber(columns));
        stream.Set(PdfName.DecodeParms, parms);

        Assert.True(stream.TryGetDecodedData(64L * 1024 * 1024, out byte[] decoded),
            "the predicted stream did not decode");
        return decoded;
    }

    private static byte[] BuildPdfWithImage(byte[] samples, int bitsPerComponent = 8,
        bool imageMask = false)
    {
        const string content = "q 64 0 0 64 100 600 cm /Im0 Do Q";

        string imageKeys = imageMask
            ? "/ImageMask true"
            : $"/ColorSpace /DeviceGray /BitsPerComponent {bitsPerComponent}";

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[6];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");

        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} "
            + $"{imageKeys} /Length {samples.Length} >>\nstream\n");
        ms.Write(samples, 0, samples.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    private static PdfStream ImageStream(PdfDocument document)
    {
        PdfObject Deref(PdfObject o)
        {
            int guard = 0;
            while (o is PdfReference r && guard++ < 32) o = document.GetObject(r.ReferenceObjectNumber);
            return o;
        }

        var catalog = Deref(document.Trailer[PdfName.Root]) as PdfDictionary;
        var pages = Deref(catalog[PdfName.Pages]) as PdfDictionary;
        var kids = Deref(pages[PdfName.Kids]) as PdfArray;
        var page = Deref(kids[0]) as PdfDictionary;
        var resources = Deref(page[PdfName.Resources]) as PdfDictionary;
        var xobjects = Deref(resources[PdfName.XObject]) as PdfDictionary;
        var image = Deref(xobjects[new PdfName("Im0")]) as PdfStream;

        Assert.NotNull(image);
        return image;
    }

    private static byte[] DecodedImageSamples(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var image = ImageStream(document);

        Assert.True(image.TryGetDecodedData(64L * 1024 * 1024, out byte[] samples),
            "the image stream did not decode");
        return samples;
    }

    private static int StoredImageLength(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return ImageStream(document).RawData.Length;
    }

    private static string FilterNameOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var image = ImageStream(document);
        var filter = image[PdfName.Filter];
        if (filter is PdfReference reference) filter = document.GetObject(reference.ReferenceObjectNumber);
        return (filter as PdfName)?.Value;
    }

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }
}
