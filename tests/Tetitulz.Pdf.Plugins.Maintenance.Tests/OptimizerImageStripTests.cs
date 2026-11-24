// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="OptimizationOptions.MergeImageStrips"/>: four or more image strips a page draws edge to
/// edge become one image and one <c>Do</c> (GAPS §19h). Each fixture is a
/// 200 × 200 page drawing 8-pixel-wide grey strips two rows high, each 80 points wide and 10 high at
/// x = 20, stacked from y = 170 downwards; every strip's samples are its own, so the merged image's
/// rows say which strip they came from and in what order.
/// </summary>
public class OptimizerImageStripTests
{
    private const int StripWidth = 8, StripRows = 2;

    /// <summary>
    /// Six strips become one 8 × 12 image whose rows are the strips' from the top down, drawn by one
    /// group over the run's extent; the page renders to the same pixels; the strips' own images and
    /// resource names are gone.
    /// </summary>
    [Fact]
    public void FourOrMoreStackedStrips_BecomeOneImage_DrawnWhereTheyWere()
    {
        byte[] input = Page(Strips(6));
        OptimizationResult result = Optimize(input, Options(), out byte[] output);

        Assert.Equal(1, result.ImageStripsMerged);
        string content = Content(output);
        Assert.Equal(1, CountDo(content));
        Assert.Contains("q 80 0 0 60 20 120 cm /MergedStrips1 Do Q", content, StringComparison.Ordinal);
        Assert.Equal(new[] { "MergedStrips1" }, XObjectNames(output));

        (PdfStream image, byte[] samples) = TheOneImage(output);
        Assert.Equal(StripWidth, image.GetInteger(PdfName.Width));
        Assert.Equal(6 * StripRows, image.GetInteger(PdfName.Height));
        Assert.Equal(PdfName.DeviceGray, image.GetName(PdfName.ColorSpace));
        Assert.Equal(Concat(Enumerable.Range(0, 6).Select(StripSamples)), samples);

        Assert.Equal(Render(input), Render(output));
    }

    /// <summary>Strips written bottom to top in the content are one run too, and the merged rows are still top first.</summary>
    [Fact]
    public void StripsWrittenUpwards_MergeWithTheTopStripFirst()
    {
        byte[] input = Page(Strips(5).Reverse().ToArray());
        OptimizationResult result = Optimize(input, Options(), out byte[] output);

        Assert.Equal(1, result.ImageStripsMerged);
        Assert.Contains("q 80 0 0 50 20 130 cm /MergedStrips1 Do Q", Content(output), StringComparison.Ordinal);
        Assert.Equal(Concat(Enumerable.Range(0, 5).Select(StripSamples)), TheOneImage(output).Samples);
        Assert.Equal(Render(input), Render(output));
    }

    /// <summary>Three strips are not a run: the page is left as it is, with its three images and three <c>Do</c>s.</summary>
    [Fact]
    public void ThreeStrips_AreLeftAsTheyAre()
    {
        byte[] input = Page(Strips(3));
        OptimizationResult result = Optimize(input, Options(), out byte[] output);

        Assert.Equal(0, result.ImageStripsMerged);
        Assert.Equal(3, CountDo(Content(output)));
        Assert.Equal(new[] { "S0", "S1", "S2" }, XObjectNames(output));
    }

    /// <summary>
    /// A run of four with one strip that is not a band of the same picture is left as it is, for the
    /// reason named: another width, a gap, another scale, a shear, a JPEG 2000 strip, a soft mask, or
    /// another colour space. The reason is the merge's, not an exception's.
    /// </summary>
    [Theory]
    [InlineData("width", null)]
    [InlineData("gap", null)]
    [InlineData("scale", "is drawn at another scale")]
    [InlineData("shear", null)]
    [InlineData("jpx", "which a lossless merge does not re-encode")]
    [InlineData("smask", "carries a mask")]
    [InlineData("rgb", "differs from the first in pixel width or colour space")]
    public void ARunWithOneStripThatIsNotABand_IsLeftAsItIs(string deviation, string reason)
    {
        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Sink(string message) { if (Environment.CurrentManagedThreadId == thread) reports.Add(message); }
        DocumentEditDiagnostics.OnEvent += Sink;
        OptimizationResult result;
        byte[] input = Page(Strips(4), deviation), output;
        try { result = Optimize(input, Options(), out output); }
        finally { DocumentEditDiagnostics.OnEvent -= Sink; }

        Assert.Equal(0, result.ImageStripsMerged);
        Assert.Equal(4, CountDo(Content(output)));
        Assert.DoesNotContain("MergedStrips", Content(output), StringComparison.Ordinal);
        if (reason != null)
            Assert.Contains(reports, r => r.Contains("Optimize.MergeImageStrips", StringComparison.Ordinal) && r.Contains(reason, StringComparison.Ordinal));
        else
            Assert.DoesNotContain(reports, r => r.Contains("Optimize.MergeImageStrips", StringComparison.Ordinal));
    }

    /// <summary>Two runs on one page, parted by a stroke, become two images.</summary>
    [Fact]
    public void TwoRunsPartedByAnotherOperation_BecomeTwoImages()
    {
        string[] strips = Strips(8);
        string content = string.Join("\n", strips.Take(4)) + "\n0 0 m 10 10 l S\n" + string.Join("\n", strips.Skip(4));
        byte[] input = Page(new[] { content }, imageCount: 8);
        OptimizationResult result = Optimize(input, Options(), out byte[] output);

        Assert.Equal(2, result.ImageStripsMerged);
        Assert.Equal(2, CountDo(Content(output)));
        Assert.Equal(new[] { "MergedStrips1", "MergedStrips2" }, XObjectNames(output));
        Assert.Equal(Render(input), Render(output));
    }

    /// <summary>A page whose content is an array of streams is rewritten as one, the run across the join merged.</summary>
    [Fact]
    public void AContentArray_IsRewrittenAsOneStream_WithTheRunMerged()
    {
        string[] strips = Strips(6);
        byte[] input = PageWithTwoContentStreams(string.Join("\n", strips.Take(3)), string.Join("\n", strips.Skip(3)));
        OptimizationResult result = Optimize(input, Options(), out byte[] output);

        Assert.Equal(1, result.ImageStripsMerged);
        Assert.Equal(1, CountDo(Content(output)));
        Assert.Equal(Render(input), Render(output));
    }

    /// <summary>
    /// A run whose merged image would pass the re-encode ceiling (64 million samples) is refused from
    /// the strips' dictionaries, before a strip is decoded. Five 4000 × 4000 grey strips, each 16 MB of
    /// samples and under the ceiling alone, would otherwise be decoded and held — 80 MB of samples,
    /// 161 MB allocated — before the merge compared
    /// their total with it. The merge is synchronous on the calling thread,
    /// so its allocation is this thread's.
    /// </summary>
    [Fact]
    public void ARunOverTheReencodeCeiling_IsRefusedBeforeAnyStripIsDecoded()
    {
        const int Side = 4000;
        byte[] deflated = Tetitulz.Pdf.Core.IO.Filters.FlateEncodeFilter.Encode(new byte[Side * Side]);
        var objects = new List<(int Number, string Dict, byte[] Stream)>
        {
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /XObject << "
                + string.Join(" ", Enumerable.Range(0, 5).Select(i => $"/S{i} {5 + i} 0 R")) + " >> >>", null),
            (4, "", Encoding.ASCII.GetBytes(string.Join("\n", Strips(5)))),
        };
        for (int i = 0; i < 5; i++)
            objects.Add((5 + i, $"/Type /XObject /Subtype /Image /Width {Side} /Height {Side} /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8 /Filter /FlateDecode", deflated));
        byte[] input = Build(objects.ToArray());

        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Sink(string message) { if (Environment.CurrentManagedThreadId == thread) reports.Add(message); }
        DocumentEditDiagnostics.OnEvent += Sink;
        OptimizationResult result;
        long allocated;
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            result = Optimize(input, Options(), out _);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        finally { DocumentEditDiagnostics.OnEvent -= Sink; }

        Assert.Equal(0, result.ImageStripsMerged);
        Assert.Contains(reports, r => r.Contains("exceed the re-encode ceiling", StringComparison.Ordinal));
        // Under one strip's 16 MB of samples: none was decoded (0.55 MB measured; 161 MB before).
        Assert.True(allocated < 8L * 1024 * 1024, "allocated " + allocated + " bytes");
    }

    /// <summary>
    /// The merged image's name is the first <c>MergedStrips</c> number the dictionary lacks — one the
    /// file already uses is passed over — and each dictionary keeps its own count.
    /// </summary>
    [Fact]
    public void TheMergedName_SkipsTheFilesOwn_AndCountsPerDictionary()
    {
        var shared = new PdfDictionary();
        shared.Set(new PdfName("MergedStrips2"), new PdfReference(99, 0));
        var other = new PdfDictionary();
        var cursors = new Dictionary<object, int>(PdfReferenceEquality.Instance);

        string[] names = new string[4];
        for (int i = 0; i < 3; i++)
        {
            PdfName name = PdfOptimizer.UnusedResourceName(shared, cursors);
            shared.Set(name, new PdfReference(100 + i, 0));
            names[i] = name.Value;
        }
        names[3] = PdfOptimizer.UnusedResourceName(other, cursors).Value;

        Assert.Equal(new[] { "MergedStrips1", "MergedStrips3", "MergedStrips4", "MergedStrips1" }, names);
    }

    /// <summary>
    /// Pages sharing one <c>/XObject</c> dictionary each add their runs to it, and a search starting at
    /// <c>MergedStrips1</c> every time would be quadratic in the runs: two thousand names in one dictionary
    /// would make two million candidates — some 200 MB of names — where they make two thousand.
    /// </summary>
    [Fact]
    public void TwoThousandMergedNamesInOneDictionary_AreFoundInLinearWork()
    {
        var shared = new PdfDictionary();
        var cursors = new Dictionary<object, int>(PdfReferenceEquality.Instance);
        var reference = new PdfReference(1, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfName last = null;
        for (int i = 0; i < 2000; i++)
        {
            last = PdfOptimizer.UnusedResourceName(shared, cursors);
            shared.Set(last, reference);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("MergedStrips2000", last.Value);
        Assert.True(allocated < 4L * 1024 * 1024, "allocated " + allocated + " bytes");
    }

    /// <summary>Off by default; on in every preset that promises savings.</summary>
    [Fact]
    public void Presets_EnableThePass_AndDefaultDoesNot()
    {
        Assert.False(OptimizationOptions.Default.MergeImageStrips);
        Assert.True(OptimizationOptions.Lossless.MergeImageStrips);
        Assert.True(OptimizationOptions.Balanced.MergeImageStrips);
        Assert.True(OptimizationOptions.Aggressive.MergeImageStrips);

        OptimizationResult result = Optimize(Page(Strips(6)), Options(on: false), out byte[] output);
        Assert.Equal(0, result.ImageStripsMerged);
        Assert.Equal(6, CountDo(Content(output)));
    }

    /// <summary>
    /// A strip image whose <c>/Filter</c> is a string — neither a name nor an array of names — makes
    /// <c>GetFilterChain</c> return null; <c>ReadStripImage</c> iterated it directly and threw
    /// <c>NullReferenceException</c>, which abandoned the whole optimization (later pages never examined).
    /// It is skipped now, and the document survives.
    /// </summary>
    [Fact]
    public void AStripImageWithAMalformedFilter_DoesNotCrashTheOptimizer()
    {
        byte[] input = Page(Strips(6), deviation: "badfilter");
        OptimizationResult result = Optimize(input, Options(), out byte[] output);

        Assert.NotEmpty(output);
        Assert.Contains("Do", Content(output), StringComparison.Ordinal);   // the page's content survived
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Strip i: <c>q 80 0 0 10 20 (170 - 10 i) cm /Si Do Q</c>.</summary>
    private static string[] Strips(int count)
        => Enumerable.Range(0, count).Select(i => Strip(i, 80, 10, 20, 170 - 10 * i)).ToArray();

    private static string Strip(int i, int a, int d, int e, int f, string shear = "0 0")
        => $"q {a} {shear.Split(' ')[0]} {shear.Split(' ')[1]} {d} {e} {f} cm /S{i} Do Q";

    /// <summary>Strip i's samples: row r of the 8 × 2 image holds 20 i + r in every pixel.</summary>
    private static byte[] StripSamples(int i)
    {
        var samples = new byte[StripWidth * StripRows];
        for (int r = 0; r < StripRows; r++)
            for (int x = 0; x < StripWidth; x++) samples[r * StripWidth + x] = (byte)(20 * i + r);
        return samples;
    }

    private static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>
    /// The page with the groups as its content and one image object per strip (<paramref name="imageCount"/>
    /// of them, one per group by default) — or, with a deviation, the second strip altered: wider, a
    /// point lower, twice as many rows in the same height (another scale), sheared, JPEG 2000 encoded,
    /// under a soft mask, or RGB.
    /// </summary>
    private static byte[] Page(string[] groups, string deviation = null, int imageCount = -1)
    {
        int count = imageCount > 0 ? imageCount : groups.Length;
        if (deviation != null)
        {
            groups = (string[])groups.Clone();
            groups[1] = deviation switch
            {
                "width" => Strip(1, 81, 10, 20, 160),
                "gap" => Strip(1, 80, 10, 20, 159),
                "shear" => Strip(1, 80, 10, 20, 160, shear: "5 0"),
                _ => groups[1],
            };
        }
        var objects = new List<(int Number, string Dict, byte[] Stream)>
        {
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /XObject << "
                + string.Join(" ", Enumerable.Range(0, count).Select(i => $"/S{i} {5 + i} 0 R")) + " >> >>", null),
            (4, "", Encoding.ASCII.GetBytes(string.Join("\n", groups))),
        };
        for (int i = 0; i < count; i++)
        {
            string dict = $"/Type /XObject /Subtype /Image /Width {StripWidth} /Height {StripRows} /ColorSpace /DeviceGray /BitsPerComponent 8";
            byte[] data = StripSamples(i);
            if (i == 1 && deviation == "scale")
            {
                // Four rows drawn in the height the others draw two: twice the rows per point.
                dict = dict.Replace($"/Height {StripRows}", $"/Height {2 * StripRows}");
                data = Concat(new[] { StripSamples(1), StripSamples(1) });
            }
            else if (i == 1 && deviation == "jpx")
            {
                data = JpxImageEncoding.Encode(data, StripWidth, StripRows, 1, 8, new JpxEncodingOptions());
                dict += " /Filter /JPXDecode";
            }
            else if (i == 1 && deviation == "smask")
            {
                dict += $" /SMask {5 + count} 0 R";
            }
            else if (i == 1 && deviation == "rgb")
            {
                dict = dict.Replace("/DeviceGray", "/DeviceRGB");
                data = data.SelectMany(v => new[] { v, v, v }).ToArray();
            }
            else if (i == 1 && deviation == "badfilter")
            {
                // A /Filter that is a string, not a name or an array of names: GetFilterChain returns
                // null, which ReadStripImage must guard (it iterated the result directly).
                dict += " /Filter (FlateDecode)";
            }
            objects.Add((5 + i, dict, data));
        }
        if (deviation == "smask")
            objects.Add((5 + count, $"/Type /XObject /Subtype /Image /Width {StripWidth} /Height {StripRows} /ColorSpace /DeviceGray /BitsPerComponent 8",
                Enumerable.Repeat((byte)128, StripWidth * StripRows).ToArray()));
        return Build(objects.ToArray());
    }

    private static byte[] PageWithTwoContentStreams(string first, string second)
    {
        var objects = new List<(int Number, string Dict, byte[] Stream)>
        {
            (1, "/Type /Catalog /Pages 2 0 R", null),
            (2, "/Type /Pages /Kids [3 0 R] /Count 1", null),
            (3, "/Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents [4 0 R 11 0 R] /Resources << /XObject << "
                + string.Join(" ", Enumerable.Range(0, 6).Select(i => $"/S{i} {5 + i} 0 R")) + " >> >>", null),
            (4, "", Encoding.ASCII.GetBytes(first)),
        };
        for (int i = 0; i < 6; i++)
            objects.Add((5 + i, $"/Type /XObject /Subtype /Image /Width {StripWidth} /Height {StripRows} /ColorSpace /DeviceGray /BitsPerComponent 8", StripSamples(i)));
        objects.Add((11, "", Encoding.ASCII.GetBytes(second)));
        return Build(objects.ToArray());
    }

    /// <summary>Objects numbered 1..n in order; a non-null payload makes the object a stream.</summary>
    private static byte[] Build(params (int Number, string Dict, byte[] Stream)[] objects)
    {
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.7\n");
        var offsets = new long[objects.Length + 1];
        foreach (var (number, dict, stream) in objects)
        {
            offsets[number] = ms.Position;
            if (stream == null)
            {
                Write($"{number} 0 obj\n<< {dict} >>\nendobj\n");
                continue;
            }
            Write($"{number} 0 obj\n<< {dict} /Length {stream.Length} >>\nstream\n");
            ms.Write(stream, 0, stream.Length);
            Write("\nendstream\nendobj\n");
        }

        long xref = ms.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private static OptimizationOptions Options(bool on = true) => new OptimizationOptions
    {
        MergeImageStrips = on,
        RemoveUnusedXObjectResources = true,
        RemoveUnusedObjects = true,
        CompressStreams = false,
        RemoveDuplicates = false,
    };

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options, out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }

    private static string Content(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static int CountDo(string content)
        => content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Count(t => t == "Do");

    private static string[] XObjectNames(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var resources = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]));
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.XObject]));
        return xobjects.Keys.Select(k => k.Value).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The one image object the output holds, with its decoded samples.</summary>
    private static (PdfStream Image, byte[] Samples) TheOneImage(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int size = Assert.IsType<PdfNumber>(doc.Trailer[PdfName.Size]).IntValue;
        var images = new List<PdfStream>();
        for (int n = 1; n < size; n++)
            if (doc.GetObject(n) is PdfStream stream && stream.GetName(PdfName.Subtype) == PdfName.Image)
                images.Add(stream);
        PdfStream image = Assert.Single(images);
        return (image, image.GetDecodedData());
    }

    private static byte[] Render(byte[] pdf)
        => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72).Pixels;
}
