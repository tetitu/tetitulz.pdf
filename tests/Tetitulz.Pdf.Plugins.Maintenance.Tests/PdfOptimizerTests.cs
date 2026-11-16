// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

public class PdfOptimizerTests
{
    /// <summary>
    /// Optimizing must preserve the document: same page count, same visible text. The predecessor
    /// asserted only that the output parsed and had at least one page, which is equally true of a
    /// document whose content the optimizer had thrown away.
    /// </summary>
    [Fact]
    public void Optimize_PreservesThePageCountAndTheText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Optimize Me");

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions());

        using var before = PdfDocument.Open(new MemoryStream(pdfBytes));
        using var after  = PdfDocument.Open(new MemoryStream(optimized));

        Assert.Equal(before.Pages.Count, after.Pages.Count);
        Assert.Contains("Optimize Me", PdfTextExtractor.ExtractText(after.Pages[0]));
    }

    /// <summary>
    /// Four destructive-sounding options at once must still round-trip every page <i>with its
    /// text</i>. The page count alone was the original assertion, and it holds for an optimizer that
    /// emptied all three pages.
    ///
    /// <para>Note what is deliberately <b>not</b> asserted: that anything was compressed.
    /// <c>CompressStreams</c> is a trial-and-commit pass that keeps the original bytes unless Flate
    /// beats them, and these content streams are a few dozen bytes — small enough that the zlib
    /// header alone makes the "compressed" form larger. Demanding <c>StreamsCompressed &gt; 0</c>
    /// here fails against correct behaviour; the pass has its own fixture in
    /// <see cref="Optimize_CompressStreams_AppliesFlateFilter"/>.</para>
    /// </summary>
    [Fact]
    public void Optimize_WithAllOptions_PreservesEveryPageAndItsText()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(3);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var options = new OptimizationOptions
        {
            CompressStreams = true,
            RemoveDuplicates = true,
            RemoveUnusedObjects = true,
            RemoveMetadata = true
        };

        var result = PdfOptimizer.Optimize(input, output, options);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(3, doc.Pages.Count);
        Assert.Equal(output.Length, result.OutputBytes);

        // Every page still carries its own text, so none was blanked or duplicated by the dedup pass.
        for (int i = 0; i < doc.Pages.Count; i++)
            Assert.Contains("Page " + (i + 1), PdfTextExtractor.ExtractText(doc.Pages[i]));

        // …and the extraction really is per page. Three pages whose content streams had been
        // deduplicated into one would satisfy the loop above, since every page would then yield
        // every marker.
        Assert.DoesNotContain("Page 2", PdfTextExtractor.ExtractText(doc.Pages[0]));
    }

    /// <summary>
    /// The path overload is not a thin wrapper: it measures <see cref="OptimizationResult.InputBytes"/>
    /// from a <c>FileInfo</c> and <see cref="OptimizationResult.OutputBytes"/> from the written file,
    /// where the stream overload reads them off the streams. So the sizes it reports are asserted
    /// against the real files — <c>Pages.Count &gt; 0</c> alone would hold for an overload that wrote
    /// the input straight through and reported nothing.
    /// </summary>
    [Fact]
    public void Optimize_FilePaths_ReportTheRealFileSizesAndPreserveTheDocument()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("File Optimize");
        var inputFile = TempFiles.NewPath();
        var outputFile = TempFiles.NewPath();

        try
        {
            File.WriteAllBytes(inputFile, pdfBytes);
            var result = PdfOptimizer.Optimize(inputFile, outputFile);

            Assert.True(File.Exists(outputFile));
            Assert.Equal(pdfBytes.Length, result.InputBytes);
            Assert.Equal(new FileInfo(outputFile).Length, result.OutputBytes);

            using var before = PdfDocument.Open(new MemoryStream(pdfBytes));
            using var after = PdfDocument.Open(outputFile);
            Assert.Equal(before.Pages.Count, after.Pages.Count);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
            if (File.Exists(outputFile)) File.Delete(outputFile);
        }
    }

    /// <summary>
    /// Re-encoding at quality 50 must actually shrink the image stream, while leaving its pixel
    /// dimensions alone. Asserting the byte count of the produced image XObject is what a
    /// "produces a valid PDF" check cannot do — the optimizer doing nothing also produces a valid
    /// PDF.
    /// </summary>
    [Fact]
    public void JpegRecompression_ShrinksTheImageStreamAndKeepsItsDimensions()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(200, 200);

        var original = FirstImage(pdfBytes, out var originalDoc);
        int originalBytes = original.RawData.Length;
        originalDoc.Dispose();

        byte[] optimized = Optimize(pdfBytes,
            new OptimizationOptions { ImageOptimization = Jpeg(50) });

        var image = FirstImage(optimized, out var doc);
        using (doc)
        {
            Assert.True(image.RawData.Length < originalBytes,
                $"expected recompression to shrink the stream, {originalBytes} -> {image.RawData.Length}");
            Assert.Equal("DCTDecode", NameOf(image[PdfName.Filter]));
            Assert.Equal(200, IntOf(image[PdfName.Width]));
            Assert.Equal(200, IntOf(image[PdfName.Height]));
        }
    }

    /// <summary>
    /// An optimizer must never make a file bigger. Flate-encoding the pixels of an
    /// already-JPEG photo does exactly that, so the re-encode is computed and then <b>discarded</b>
    /// (<c>PdfOptimizer</c> only swaps the data in when it came out smaller) — the image keeps its
    /// original <c>/DCTDecode</c> bytes.
    ///
    /// <para>Worth pinning rather than reading as a bug: the predecessor's name promised
    /// "PngFormat produces a valid PDF" and it passed without the target format having any effect
    /// whatsoever.</para>
    /// </summary>
    [Fact]
    public void PngTarget_OnAnAlreadyJpegImage_LeavesItUnchangedRatherThanGrowingIt()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(100, 100);

        var original = FirstImage(pdfBytes, out var originalDoc);
        byte[] originalData = (byte[])original.RawData.Clone();
        originalDoc.Dispose();

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Png,
                SkipAlreadyOptimized = false,
            }
        });

        var image = FirstImage(optimized, out var doc);
        using (doc)
        {
            Assert.Equal("DCTDecode", NameOf(image[PdfName.Filter]));
            Assert.Equal(originalData, image.RawData);
        }
    }

    /// <summary>
    /// MaxWidth/MaxHeight must downsample the image and write the new pixel dimensions back into
    /// the XObject — a resize that shrank the data but left /Width and /Height stale would produce
    /// a corrupt image, and "produces a valid PDF" could not tell the difference.
    /// </summary>
    [Fact]
    public void Resize_DownsamplesToTheMaxBoundsAndUpdatesWidthAndHeight()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(300, 300);

        var original = FirstImage(pdfBytes, out var originalDoc);
        int originalBytes = original.RawData.Length;
        originalDoc.Dispose();

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Jpeg,
                Quality = 75,
                MaxWidth = 150,
                MaxHeight = 150,
                SkipAlreadyOptimized = false,
            }
        });

        var image = FirstImage(optimized, out var doc);
        using (doc)
        {
            Assert.Equal(150, IntOf(image[PdfName.Width]));
            Assert.Equal(150, IntOf(image[PdfName.Height]));
            Assert.True(image.RawData.Length < originalBytes,
                $"a quarter of the pixels should be fewer bytes, {originalBytes} -> {image.RawData.Length}");
        }
    }

    /// <summary>
    /// WebPreset sets <c>SkipAlreadyOptimized</c>, so an image that is already DCT-compressed is
    /// left exactly as it is rather than being re-encoded (which would lose quality for no size
    /// win). The preset's own values are asserted alongside the outcome, so the test still means
    /// something if the preset is ever retuned.
    /// </summary>
    [Fact]
    public void WebPreset_SkipsAnImageThatIsAlreadyJpegCompressed()
    {
        var preset = ImageOptimizationOptions.WebPreset;
        Assert.True(preset.Enabled);
        Assert.True(preset.SkipAlreadyOptimized);
        Assert.Equal(ImageFormat.Jpeg, preset.TargetFormat);

        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(200, 200);

        var original = FirstImage(pdfBytes, out var originalDoc);
        byte[] originalData = (byte[])original.RawData.Clone();
        originalDoc.Dispose();

        byte[] optimized = Optimize(pdfBytes,
            new OptimizationOptions { ImageOptimization = preset });

        var image = FirstImage(optimized, out var doc);
        using (doc)
            Assert.Equal(originalData, image.RawData);
    }

    [Fact]
    public void Optimize_WithImageOptimization_Disabled_DoesNotChangeImages()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(100, 100);

        using var input1 = new MemoryStream(pdfBytes);
        using var output1 = new MemoryStream();
        PdfOptimizer.Optimize(input1, output1, new OptimizationOptions
        {
            ImageOptimization = ImageOptimizationOptions.Default // Disabled
        });

        using var input2 = new MemoryStream(pdfBytes);
        using var output2 = new MemoryStream();
        PdfOptimizer.Optimize(input2, output2);

        // Both outputs should be the same since image optimization is disabled in both
        Assert.Equal(output1.Length, output2.Length);
    }

    /// <summary>
    /// ConvertToGrayscale must rewrite the image's <c>/ColorSpace</c> to <c>/DeviceGray</c>. The
    /// control is the same optimization without the flag, which must stay <c>/DeviceRGB</c> —
    /// without it, a renderer that wrote DeviceGray unconditionally would also pass.
    /// </summary>
    [Fact]
    public void Grayscale_ConvertsTheImageColourSpaceToDeviceGray()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(100, 100);

        byte[] gray = Optimize(pdfBytes, new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Jpeg,
                Quality = 75,
                ConvertToGrayscale = true,
                SkipAlreadyOptimized = false,
            }
        });
        byte[] colour = Optimize(pdfBytes,
            new OptimizationOptions { ImageOptimization = Jpeg() });

        var grayImage = FirstImage(gray, out var grayDoc);
        using (grayDoc)
            Assert.Equal("DeviceGray", NameOf(grayImage[PdfName.ColorSpace]));

        var colourImage = FirstImage(colour, out var colourDoc);
        using (colourDoc)
            Assert.Equal("DeviceRGB", NameOf(colourImage[PdfName.ColorSpace]));
    }

    /// <summary>
    /// A deliberate change-detector: it pins the preset numbers so retuning one is a visible edit.
    ///
    /// <para><b>It is not behavioural cover, and reading it as such cost this repo a feature.</b>
    /// These <c>TargetDpi</c> assertions were for years the only ones naming the property, and they
    /// pass with the downsampling code deleted — which it effectively was, since the source
    /// resolution was read from a <c>/XResolution</c> key ISO 32000-1 does not define. The behaviour
    /// lives in <see cref="ImageDownsamplingTests"/>; if you change a number here, change the
    /// expectation there.</para>
    /// </summary>
    [Fact]
    public void ImageOptimizationOptions_Presets_HaveCorrectValues()
    {
        var web = ImageOptimizationOptions.WebPreset;
        Assert.True(web.Enabled);
        Assert.Equal(ImageFormat.Jpeg, web.TargetFormat);
        Assert.Equal(60, web.Quality);
        Assert.Equal(150, web.TargetDpi);
        Assert.Equal(1920, web.MaxWidth);

        var balanced = ImageOptimizationOptions.BalancedPreset;
        Assert.True(balanced.Enabled);
        Assert.Equal(75, balanced.Quality);
        Assert.Equal(200, balanced.TargetDpi);

        var print = ImageOptimizationOptions.PrintPreset;
        Assert.True(print.Enabled);
        Assert.Equal(90, print.Quality);
        Assert.Equal(300, print.TargetDpi);
    }

    [Fact]
    public void ImageOptimizationOptions_Default_IsDisabled()
    {
        var defaults = ImageOptimizationOptions.Default;
        Assert.False(defaults.Enabled);
        Assert.Equal(ImageFormat.Original, defaults.TargetFormat);
        Assert.Equal(75, defaults.Quality);
        Assert.Equal(0, defaults.TargetDpi);
        Assert.Equal(0, defaults.MaxWidth);
        Assert.Equal(0, defaults.MaxHeight);
        Assert.Equal(System.IO.Compression.CompressionLevel.Optimal, defaults.PngCompressionLevel);
        Assert.False(defaults.ConvertToGrayscale);
        Assert.Equal(1024, defaults.MinImageSizeBytes);
    }

    [Fact]
    public void ImageFormat_HasExpectedValues()
    {
        Assert.Equal(0, (int)ImageFormat.Original);
        Assert.Equal(1, (int)ImageFormat.Jpeg);
        Assert.Equal(2, (int)ImageFormat.Png);
        Assert.Equal(3, (int)ImageFormat.Gif);
        Assert.Equal(4, (int)ImageFormat.WebP);
    }

    [Fact]
    public void Optimize_PreservesTextContent()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Hello Optimization World");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        string text = PdfTextExtractor.ExtractText(doc);

        Assert.Contains("Hello Optimization World", text);
    }

    [Fact]
    public void Optimize_MultiPage_PreservesAllPages()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(5);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(5, doc.Pages.Count);

        string text = PdfTextExtractor.ExtractText(doc);
        for (int i = 1; i <= 5; i++)
        {
            Assert.Contains($"Page {i}", text);
        }
    }

    [Fact]
    public void Optimize_XrefHasCorrectFreeEntry()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Xref Test");

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output);

        // Verify the output xref has object 0 as free entry with gen 65535
        string pdfText = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("0000000000 65535 f ", pdfText);
    }

    // -------------------------------------------------------------------
    //  Default options
    // -------------------------------------------------------------------

    [Fact]
    public void OptimizationOptions_Default_HasExpectedValues()
    {
        var opts = OptimizationOptions.Default;

        Assert.True(opts.CompressStreams);
        Assert.True(opts.RemoveDuplicates);
        Assert.True(opts.RemoveUnusedObjects);
        Assert.False(opts.RemoveMetadata);
        Assert.False(opts.RemoveAnnotations);
        Assert.False(opts.RemoveDocumentStructure);
        Assert.False(opts.RecompressExistingStreams);
        Assert.False(opts.ImageOptimization.Enabled);
    }

    // -------------------------------------------------------------------
    //  Null-argument guards
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_NullInputStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfOptimizer.Optimize((Stream)null!, new MemoryStream()));
    }

    [Fact]
    public void Optimize_NullOutputStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfOptimizer.Optimize(new MemoryStream(), (Stream)null!));
    }

    [Fact]
    public void Optimize_NullInputPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfOptimizer.Optimize((string)null!, "out.pdf"));
    }

    [Fact]
    public void Optimize_NullOutputPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfOptimizer.Optimize("in.pdf", (string)null!));
    }

    // -------------------------------------------------------------------
    //  Deduplication
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_RemoveDuplicates_ReducesObjectCount()
    {
        // PDF has 8 objects; two of them are byte-identical font dicts.
        byte[] input = MinimalPdfBuilder.CreatePdfWithDuplicateObjects();
        int inputCount = CountObjectDeclarations(input);

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDuplicates = true,
            RemoveUnusedObjects = false,
            CompressStreams = false
        });

        int outputCount = CountObjectDeclarations(output.ToArray());

        // One duplicate font was merged → one fewer object
        Assert.True(outputCount < inputCount, $"Expected fewer objects after dedup but got {outputCount} vs {inputCount}");

        // PDF must still be valid with both pages intact
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Equal(2, doc.Pages.Count);
    }

    [Fact]
    public void Optimize_RemoveDuplicates_False_KeepsBothCopies()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithDuplicateObjects();
        int inputCount = CountObjectDeclarations(input);

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDuplicates = false,
            RemoveUnusedObjects = false,
            CompressStreams = false
        });

        int outputCount = CountObjectDeclarations(output.ToArray());

        Assert.Equal(inputCount, outputCount);
    }

    // -------------------------------------------------------------------
    //  Unused-object removal
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_RemoveUnusedObjects_ReducesObjectCount()
    {
        // PDF has 6 objects; object 6 is an orphan not reachable from the catalog.
        byte[] input = MinimalPdfBuilder.CreatePdfWithUnusedObject();
        int inputCount = CountObjectDeclarations(input);

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveUnusedObjects = true,
            RemoveDuplicates = false,
            CompressStreams = false
        });

        int outputCount = CountObjectDeclarations(output.ToArray());

        Assert.True(outputCount < inputCount, $"Expected orphan pruned but got {outputCount} vs {inputCount}");

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Optimize_RemoveUnusedObjects_False_KeepsOrphanedObjects()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithUnusedObject();
        int inputCount = CountObjectDeclarations(input);

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveUnusedObjects = false,
            RemoveDuplicates = false,
            CompressStreams = false
        });

        int outputCount = CountObjectDeclarations(output.ToArray());

        Assert.Equal(inputCount, outputCount);
    }

    // -------------------------------------------------------------------
    //  Metadata removal
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_RemoveMetadata_RemovesInfoFromTrailer()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithMetadata("Test Document");

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions { RemoveMetadata = true });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        // /Info was removed from the trailer, so Title should not be resolvable
        Assert.Null(doc.Metadata?.Title);
    }

    [Fact]
    public void Optimize_RemoveMetadata_False_PreservesInfoInTrailer()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithMetadata("Test Document");

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions { RemoveMetadata = false });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);

        Assert.Equal("Test Document", doc.Metadata?.Title);
    }

    // -------------------------------------------------------------------
    //  Stream compression
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_CompressStreams_AppliesFlateFilter()
    {
        // Use repetitive content so FlateDecode compression actually reduces size
        byte[] input = MinimalPdfBuilder.CreatePdfWithRepetitiveContent();

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            CompressStreams = true,
            RemoveDuplicates = false,
            RemoveUnusedObjects = false
        });

        string outputText = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("FlateDecode", outputText);

        // Output must be smaller than input (repetitive content compresses well)
        Assert.True(output.Length < input.Length,
            $"Expected compressed output ({output.Length}) < input ({input.Length})");

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Optimize_CompressStreams_False_LeavesStreamsUncompressed()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithRepetitiveContent();

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            CompressStreams = false,
            RemoveDuplicates = false,
            RemoveUnusedObjects = false
        });

        string outputText = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("FlateDecode", outputText);
    }

    [Fact]
    public void Optimize_RecompressExistingStreams_ProducesValidPdf()
    {
        // First pass: compress normally
        byte[] input = MinimalPdfBuilder.CreatePdfWithRepetitiveContent();
        using var pass1In = new MemoryStream(input);
        using var pass1Out = new MemoryStream();
        PdfOptimizer.Optimize(pass1In, pass1Out, new OptimizationOptions { CompressStreams = true });

        // Second pass: recompress already-compressed streams
        pass1Out.Position = 0;
        using var pass2Out = new MemoryStream();
        PdfOptimizer.Optimize(pass1Out, pass2Out, new OptimizationOptions
        {
            CompressStreams = true,
            RecompressExistingStreams = true
        });

        pass2Out.Position = 0;
        using var doc = PdfDocument.Open(pass2Out);
        Assert.Single(doc.Pages);

        string outputText = Encoding.ASCII.GetString(pass2Out.ToArray());
        Assert.Contains("FlateDecode", outputText);
    }

    // -------------------------------------------------------------------
    //  Annotation removal
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_RemoveAnnotations_RemovesAnnotsFromPages()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithAnnotations();
        Assert.Contains("/Annots", Encoding.ASCII.GetString(input));

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveAnnotations = true,
            RemoveUnusedObjects = true
        });

        string outputText = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("/Annots", outputText);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// A merged field and widget holding a value, a parent field holding one, and a free-text comment: the
    /// structure tree still names the comment and the parent's widget, whose /Parent reaches the parent.
    /// Removing the annotations discards each value and word, though object references keep those
    /// dictionaries in the file. The control keeps the annotations and their words.
    /// </summary>
    [Fact]
    public void Optimize_RemoveAnnotations_DiscardsFormValuesAndCommentsReachedOtherwise()
    {
        const string marker = "SECRET-77";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 7 0 R] >> /StructTreeRoot 9 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [5 0 R 6 0 R 8 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (merged) /V (" + marker + "-A) /Rect [10 10 50 30] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Parent 7 0 R /Rect [10 40 50 60] /P 3 0 R >>",
            "<< /FT /Tx /T (parent) /V (" + marker + "-B) /Kids [6 0 R] >>",
            "<< /Type /Annot /Subtype /FreeText /Contents (" + marker + "-C) /Rect [10 70 50 90] /P 3 0 R >>",
            "<< /Type /StructTreeRoot /K [<< /S /Annot /K << /Type /OBJR /Obj 8 0 R /Pg 3 0 R >> >> " +
            "<< /S /Form /K << /Type /OBJR /Obj 6 0 R /Pg 3 0 R >> >>] >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] input = Encoding.ASCII.GetBytes(sb.ToString());

        string Optimized(bool remove)
        {
            using var output = new MemoryStream();
            PdfOptimizer.Optimize(new MemoryStream(input), output, new OptimizationOptions { RemoveAnnotations = remove, RemoveUnusedObjects = true });
            return Encoding.ASCII.GetString(output.ToArray());
        }

        string removed = Optimized(true);
        Assert.DoesNotContain("/Annots", removed);
        Assert.DoesNotContain(marker, removed);
        Assert.DoesNotContain("/AcroForm", removed);
        Assert.Contains("/OBJR", removed);

        string kept = Optimized(false);
        Assert.Contains(marker + "-A", kept);
        Assert.Contains(marker + "-B", kept);
        Assert.Contains(marker + "-C", kept);
    }

    [Fact]
    public void Optimize_RemoveAnnotations_False_PreservesAnnots()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithAnnotations();

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveAnnotations = false
        });

        string outputText = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("/Annots", outputText);
    }

    // -------------------------------------------------------------------
    //  Document-structure removal
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_RemoveDocumentStructure_RemovesStructureEntries()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithStructureTree();
        Assert.Contains("/MarkInfo", Encoding.ASCII.GetString(input));
        Assert.Contains("/StructTreeRoot", Encoding.ASCII.GetString(input));

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDocumentStructure = true,
            RemoveUnusedObjects = true
        });

        string outputText = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("/MarkInfo", outputText);
        Assert.DoesNotContain("/StructTreeRoot", outputText);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Optimize_RemoveDocumentStructure_False_PreservesStructure()
    {
        byte[] input = MinimalPdfBuilder.CreatePdfWithStructureTree();

        using var ms = new MemoryStream(input);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(ms, output, new OptimizationOptions
        {
            RemoveDocumentStructure = false
        });

        string outputText = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("/MarkInfo", outputText);
        Assert.Contains("/StructTreeRoot", outputText);
    }

    // -------------------------------------------------------------------
    //  OptimizationResult stats
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_Returns_OptimizationResult_With_Stats()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithRepetitiveContent();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            CompressStreams = true,
            RemoveDuplicates = true,
            RemoveUnusedObjects = true
        });

        Assert.NotNull(result);
        Assert.True(result.InputBytes > 0);
        Assert.True(result.OutputBytes > 0);
    }

    [Fact]
    public void Optimize_DuplicatesRemoved_CountedInResult()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithDuplicateObjects();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            RemoveDuplicates = true
        });

        Assert.True(result.DuplicatesRemoved > 0);
    }

    [Fact]
    public void Optimize_ObjectsRemoved_CountedInResult()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithUnusedObject();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            RemoveUnusedObjects = true,
            RemoveDuplicates = false
        });

        Assert.True(result.ObjectsRemoved > 0);
    }

    [Fact]
    public void Optimize_ImagesOptimized_CountedInResult()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(300, 300);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Jpeg,
                Quality = 30,
                SkipAlreadyOptimized = false
            }
        });

        Assert.True(result.ImagesOptimized > 0);
    }

    /// <summary>
    /// On a document whose single image optimizes cleanly, the report must say exactly that: one
    /// image optimized, no failures. "ImageFailures is not null" — the predecessor's assertion —
    /// is satisfied by an empty collection on a run that silently failed at everything.
    /// </summary>
    [Fact]
    public void ImageFailures_IsEmptyAndImagesOptimizedIsOne_WhenTheOnlyImageSucceeds()
    {
        // 300x300, not 100x100: at the smaller size the q75 re-encode comes out *larger* than the
        // original, so the optimizer correctly declines it and ImagesOptimized is 0. Discovered by
        // writing the assertion — the predecessor could not have noticed either way.
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(300, 300);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output,
            new OptimizationOptions { ImageOptimization = Jpeg() });

        Assert.Empty(result.ImageFailures);
        Assert.Equal(1, result.ImagesOptimized);
        Assert.Equal(pdfBytes.Length, result.InputBytes);
        Assert.Equal(output.Length, result.OutputBytes);
    }

    // -------------------------------------------------------------------
    //  Content-stream cleanup
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_CleanContentStreams_RemovesEmptyQQPairs()
    {
        // Content stream with empty save/restore pairs and actual drawing content.
        string contentStream = "q\nQ\nBT\n/F1 12 Tf\n72 720 Td\n(Hello) Tj\nET\nq\nQ";
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Hello", contentStream);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            CleanContentStreams = true,
            CompressStreams = false  // keep uncompressed so we can inspect raw bytes
        });

        Assert.True(result.ContentStreamsClean > 0);

        // The raw output bytes must not contain the isolated q…Q pattern.
        // (There may still be q/Q as part of other tokens like /F1, etc. — we
        // check the specific whitespace-surrounded single-character form.)
        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("q\nQ", raw);
    }

    [Fact]
    public void Optimize_CleanContentStreams_PreservesTextContent()
    {
        string contentStream = "q\nQ\nBT\n/F1 12 Tf\n72 720 Td\n(HelloWorld) Tj\nET\nq\nQ";
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("HelloWorld", contentStream);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            CleanContentStreams = true
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.Pages.Count > 0);
        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("HelloWorld", text);
    }

    [Fact]
    public void Optimize_CleanContentStreams_DeepNesting_DoesNotHang()
    {
        // 1000 nested empty q Q pairs — must complete quickly without hanging.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append("q\n");
        for (int i = 0; i < 1000; i++) sb.Append("Q\n");
        sb.Append("BT\n/F1 12 Tf\n72 720 Td\n(X) Tj\nET");
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("X", sb.ToString());

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        // Should complete without timeout — adversarial test.
        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            CleanContentStreams = true
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void Optimize_CleanContentStreams_False_LeavesStreamsUntouched()
    {
        string contentStream = "q\nQ\nBT\n/F1 12 Tf\n72 720 Td\n(Hello) Tj\nET";
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Hello", contentStream);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            CleanContentStreams = false,
            CompressStreams = false
        });

        Assert.Equal(0, result.ContentStreamsClean);
    }

    /// <summary>
    /// A hang-catcher for the per-pixel <c>SetPixel</c> loop the bulk <c>Marshal.Copy</c> path
    /// replaced, on a 1500×1500 image.
    ///
    /// <para><b>The timing is the weakest part of this test, so it is not the only part.</b> A
    /// skipped image is instant, so a stopwatch alone passes fastest exactly when the fixture never
    /// reached the feature — the failure CLAUDE.md describes as a benchmark whose fixture misses its
    /// target. So the re-encode is asserted first, and only then the ceiling.</para>
    ///
    /// <para>The ceiling is deliberately loose. It is a hang-catcher, not a performance bound: a
    /// tight limit turns CPU contention during a parallel run into a false regression. Real timing
    /// belongs in the BenchmarkDotNet harness, not here. See GAPS.md §8a.</para>
    /// </summary>
    [Fact]
    public void Optimize_ALargeImage_DoesNotHang()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreatePdfWithImage(1500, 1500);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Jpeg,
                Quality = 75,
                SkipAlreadyOptimized = false,
                MinImageSizeBytes = 0
            }
        });
        sw.Stop();

        // Without this the stopwatch measures nothing: an image the optimizer declined to touch
        // finishes immediately and the ceiling below is met by doing no work at all.
        Assert.Equal(1, result.ImagesOptimized);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.Single(doc.Pages);

        Assert.True(sw.ElapsedMilliseconds < 120_000,
            $"Image optimization took {sw.ElapsedMilliseconds} ms (ceiling 120 000 ms)");
    }

    // -------------------------------------------------------------------
    //  New features: XMP removal, Thumbnail removal, ASCII filter strip,
    //  LZW→Flate, PNG predictor, content-stream concatenation
    // -------------------------------------------------------------------

    [Fact]
    public void Optimize_RemoveXmpMetadata_StripsMetadataStream()
    {
        byte[] pdfBytes = BuildPdfWithXmpMetadata();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            RemoveXmpMetadata = true,
            CompressStreams = false,
            RemoveUnusedObjects = true   // prunes the now-orphaned /Metadata stream object
        });

        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("/Metadata", raw);
    }

    [Fact]
    public void Optimize_RemoveXmpMetadata_False_PreservesMetadataStream()
    {
        byte[] pdfBytes = BuildPdfWithXmpMetadata();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            RemoveXmpMetadata = false,
            CompressStreams = false
        });

        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("/Metadata", raw);
    }

    [Fact]
    public void Optimize_RemoveThumbnails_StripsThumbFromPages()
    {
        byte[] pdfBytes = BuildPdfWithThumbnail();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            RemoveThumbnails = true,
            CompressStreams = false,
            RemoveUnusedObjects = false
        });

        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("/Thumb", raw);
    }

    [Fact]
    public void Optimize_RemoveThumbnails_False_PreservesThumbs()
    {
        byte[] pdfBytes = BuildPdfWithThumbnail();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            RemoveThumbnails = false,
            CompressStreams = false
        });

        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("/Thumb", raw);
    }

    /// <summary>
    /// §7.7.3.2 requires <c>/Type</c> on an intermediate page-tree node, but producers omit it and
    /// Core reads those files anyway — <c>PdfPageTree</c> recognises a leaf <i>structurally</i>, by
    /// the absence of <c>/Kids</c>. <c>PdfOptimizer.CollectPageObjectNumbers</c> classified every node
    /// that did not say <c>/Type /Pages</c> as a page, so an untyped node <i>was</i> the page it
    /// found — and an intermediate node has no <c>/Thumb</c> — while its <c>/Kids</c> were never
    /// pushed. The real page below it kept its thumbnail through a <c>RemoveThumbnails = true</c> run
    /// that reported success.
    ///
    /// <para>The assertion is on the page dictionary rather than on a <c>/Thumb</c> substring,
    /// because the substring is also absent from a file whose page the optimizer lost outright.</para>
    /// </summary>
    [Fact]
    public void Optimize_RemoveThumbnails_StripsThumbFromAPageBehindAnUntypedPageTreeNode()
    {
        byte[] pdfBytes = WithUntypedPageTreeRoot(BuildPdfWithThumbnail());

        // The premise: Core still reaches the page and it really does carry a thumbnail, so anything
        // the optimizer misses here is its own doing.
        using (var before = PdfDocument.Open(new MemoryStream(pdfBytes)))
        {
            Assert.Single(before.Pages);
            Assert.NotNull(before.Pages[0].Dictionary[PdfName.Thumb]);
        }

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            RemoveThumbnails = true,
            CompressStreams = false,
            RemoveUnusedObjects = false
        });

        using var after = PdfDocument.Open(new MemoryStream(optimized));
        Assert.Single(after.Pages);
        Assert.Null(after.Pages[0].Dictionary[PdfName.Thumb]);
    }

    /// <summary>
    /// The optimizer and the scanners must agree about which nodes are pages, because they live in
    /// one assembly and one of them decides what the other's evidence is applied to. They did not:
    /// <c>PageTreeWalk</c> walks the <i>objects</i>, <c>CollectPageObjectNumbers</c> walked the
    /// <i>object numbers</i>, and a directly-written page-tree root has none.
    ///
    /// <para>The assertion is on the page dictionary, not on a <c>/Thumb</c> substring: the
    /// substring is also absent from a file whose page the optimizer lost outright, which is the
    /// failure being pinned.</para>
    /// </summary>
    [Fact]
    public void Optimize_RemoveThumbnails_StripsThumbFromAPageBehindADirectPageTreeRoot()
    {
        byte[] pdfBytes = BuildPdfWithDirectPageTreeRoot();

        // The premise: Core reaches the page and it really does carry a thumbnail, so anything the
        // optimizer misses here is its own doing.
        using (var before = PdfDocument.Open(new MemoryStream(pdfBytes)))
        {
            Assert.Single(before.Pages);
            Assert.NotNull(before.Pages[0].Dictionary[PdfName.Thumb]);
        }

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            RemoveThumbnails = true,
            CompressStreams = false,
            RemoveUnusedObjects = false
        });

        using var after = PdfDocument.Open(new MemoryStream(optimized));
        Assert.Single(after.Pages);
        Assert.Null(after.Pages[0].Dictionary[PdfName.Thumb]);
    }

    /// <summary>
    /// Table 29 makes <c>/Kids</c> an array of indirect references, and producers write a direct
    /// page dictionary anyway. The optimizer's private walk pushed only <c>kids[i] is
    /// PdfReference</c>, so such a page was invisible to it while the scanners beside it saw it.
    /// </summary>
    [Fact]
    public void Optimize_RemoveThumbnails_StripsThumbFromAPageWrittenDirectlyInKids()
    {
        byte[] pdfBytes = BuildPdfWithDirectPageKid();

        using (var before = PdfDocument.Open(new MemoryStream(pdfBytes)))
        {
            Assert.Single(before.Pages);
            Assert.NotNull(before.Pages[0].Dictionary[PdfName.Thumb]);
        }

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            RemoveThumbnails = true,
            CompressStreams = false,
            RemoveUnusedObjects = false
        });

        using var after = PdfDocument.Open(new MemoryStream(optimized));
        Assert.Single(after.Pages);
        Assert.Null(after.Pages[0].Dictionary[PdfName.Thumb]);
    }

    /// <summary>
    /// The page-walk carries each page's object number so the shared-fragment refusal can name it.
    /// Nothing asserted that number until this test: the walk could have reported every page as
    /// "no object number" and the suite stayed green, which makes the field a deletion waiting to
    /// happen rather than a carried fact.
    /// </summary>
    [Fact]
    public void ConcatenateContentStreams_RefusingASharedFragment_NamesThePageObject()
    {
        byte[] pdfBytes = BuildPdfWithTwoPagesSharingAContentStream();
        DocumentEditDiagnostics.Clear();

        // Collected rather than read off LastError, which keeps only the most recent message and so
        // cannot see a refusal a later stage reports over. Filtered to this thread: the sink is
        // process-wide and an unfiltered handler asserts something about the whole test run.
        int thisThread = Environment.CurrentManagedThreadId;
        var reported = new List<string>();
        void Collect(string message)
        {
            if (Environment.CurrentManagedThreadId == thisThread)
                lock (reported) reported.Add(message);
        }

        byte[] optimized;
        DocumentEditDiagnostics.OnEvent += Collect;
        try
        {
            optimized = Optimize(pdfBytes, new OptimizationOptions
            {
                ConcatenateContentStreams = true,
                CompressStreams = false,
                RemoveUnusedObjects = false,
                RemoveDuplicates = false
            });
        }
        finally
        {
            DocumentEditDiagnostics.OnEvent -= Collect;
        }

        // The refusal really happened: both pages keep their separate fragments, because merging
        // either would change what the other draws.
        using (var after = PdfDocument.Open(new MemoryStream(optimized)))
        {
            Assert.Equal(2, after.Pages.Count);
            Assert.IsType<PdfArray>(after.ResolveObject(after.Pages[0].Dictionary[PdfName.Contents]));
            Assert.IsType<PdfArray>(after.ResolveObject(after.Pages[1].Dictionary[PdfName.Contents]));
        }

        // …and it says which pages, by the object numbers the walk carried — both of them, because
        // both refusals are separate findings and a slot that kept one would lose the other.
        var refusals = reported
            .Where(m => m.IndexOf("shares at least one /Contents stream", StringComparison.Ordinal) >= 0)
            .ToList();

        Assert.Equal(2, refusals.Count);
        Assert.Contains(refusals, m => m.IndexOf("object 3", StringComparison.Ordinal) >= 0);
        Assert.Contains(refusals, m => m.IndexOf("object 4", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The same missed page, reached through a second consumer of the page walk, so the fix is
    /// pinned at the walk rather than at one option: the page's two <c>/Contents</c> fragments
    /// survived a <c>ConcatenateContentStreams = true</c> run that reported success.
    /// </summary>
    [Fact]
    public void ConcatenateContentStreams_MergesAPageBehindADirectPageTreeRoot()
    {
        byte[] pdfBytes = BuildPdfWithDirectPageTreeRoot(multipleContentStreams: true);

        // The premise: Core reaches the page, and its /Contents really is an array to merge.
        using (var before = PdfDocument.Open(new MemoryStream(pdfBytes)))
        {
            Assert.Single(before.Pages);
            Assert.IsType<PdfArray>(
                before.ResolveObject(before.Pages[0].Dictionary[PdfName.Contents]));
        }

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            ConcatenateContentStreams = true,
            CompressStreams = false,
            RemoveUnusedObjects = false,
            RemoveDuplicates = false
        });

        using var after = PdfDocument.Open(new MemoryStream(optimized));
        var merged = Assert.IsAssignableFrom<PdfStream>(
            after.ResolveObject(after.Pages[0].Dictionary[PdfName.Contents]));

        string body = Encoding.ASCII.GetString(merged.GetDecodedData());
        Assert.Contains("StreamA", body);
        Assert.Contains("StreamB", body);
    }

    /// <summary>
    /// The same missed subtree, seen through the other consumer of the page walk: a page below an
    /// untyped node kept its two separate <c>/Contents</c> fragments through a
    /// <c>ConcatenateContentStreams = true</c> run.
    /// </summary>
    [Fact]
    public void ConcatenateContentStreams_MergesAPageBehindAnUntypedPageTreeNode()
    {
        byte[] pdfBytes = WithUntypedPageTreeRoot(BuildPdfWithMultipleContentStreams());

        // The premise: Core reaches the page, and its /Contents really is an array to merge.
        using (var before = PdfDocument.Open(new MemoryStream(pdfBytes)))
        {
            Assert.Single(before.Pages);
            Assert.IsType<PdfArray>(
                before.ResolveObject(before.Pages[0].Dictionary[PdfName.Contents]));
        }

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            ConcatenateContentStreams = true,
            CompressStreams = false,
            RemoveUnusedObjects = false,
            RemoveDuplicates = false
        });

        using var after = PdfDocument.Open(new MemoryStream(optimized));
        var merged = Assert.IsAssignableFrom<PdfStream>(
            after.ResolveObject(after.Pages[0].Dictionary[PdfName.Contents]));

        string body = Encoding.ASCII.GetString(merged.GetDecodedData());
        Assert.Contains("StreamA", body);
        Assert.Contains("StreamB", body);
    }

    /// <summary>
    /// The JPEG pass takes a bare <c>/DCTDecode</c> only, and the ASCII peel is the stream pass's,
    /// so the JPEG pass runs after it: a JPEG filtered <c>[/ASCII85Decode /DCTDecode]</c> has its
    /// tables re-fitted on the same call. The control is the same JPEG with the pass switched off.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Optimize_Lossless_RefitsAJpegBehindAnAscii85LayerOnTheSameCall(bool refit, int expectedRecompressed)
    {
        byte[] pdfBytes = BuildPdfWithAscii85Jpeg();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();
        var options = OptimizationOptions.Lossless;
        options.OptimizeJpegHuffmanTables = refit;
        var result = PdfOptimizer.Optimize(input, output, options);

        Assert.Equal(expectedRecompressed, result.JpegImagesRecompressed);
        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("ASCII85Decode", raw, StringComparison.Ordinal);
        Assert.Contains("/DCTDecode", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Optimize_ReplaceAsciiFilters_RemovesAscii85Layer()
    {
        byte[] pdfBytes = BuildPdfWithAscii85Stream();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            ReplaceAsciiFilters = true,
            CompressStreams = false,
            RemoveDuplicates = false,
            RemoveUnusedObjects = false
        });

        // The ASCII85Decode filter name must be gone.
        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("ASCII85Decode", raw);

        // Output must still be a valid PDF.
        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void Optimize_ReplaceLzwWithFlate_ReencodesLzwStreams()
    {
        byte[] pdfBytes = BuildPdfWithLzwStream();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            ReplaceLzwWithFlate = true,
            CompressStreams = true,
            RemoveDuplicates = false,
            RemoveUnusedObjects = false
        });

        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("LZWDecode", raw);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.Pages.Count > 0);
    }

    [Fact]
    public void Optimize_UsePngPredictorForImages_AppliesPredictor()
    {
        // Build a PDF with a raw (unfiltered) DeviceRGB image — PNG predictor targets these.
        byte[] pdfBytes = BuildPdfWithRawImage(20, 20);

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            UsePngPredictorForImages = true,
            CompressStreams = false
        });

        // /Predictor 12 (PNG Up) must appear in the output.
        string raw = Encoding.ASCII.GetString(output.ToArray());
        Assert.Contains("/Predictor", raw);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        Assert.True(doc.Pages.Count > 0);
    }

    /// <summary>
    /// The fixture's page has <c>/Contents [4 0 R 5 0 R]</c>. After concatenation it must be a
    /// <b>single</b> stream carrying both fragments — the merge is the entire point of the option,
    /// and the predecessor's "the output is still a valid PDF" holds just as well when the option
    /// is ignored.
    /// </summary>
    [Fact]
    public void ConcatenateContentStreams_ReplacesTheArrayWithOneStreamHoldingBothFragments()
    {
        byte[] pdfBytes = BuildPdfWithMultipleContentStreams();

        // The fixture really is an array — otherwise there is nothing to merge.
        using (var beforeDoc = PdfDocument.Open(new MemoryStream(pdfBytes)))
            Assert.IsType<PdfArray>(
                beforeDoc.ResolveObject(beforeDoc.Pages[0].Dictionary[PdfName.Contents]));

        byte[] optimized = Optimize(pdfBytes, new OptimizationOptions
        {
            ConcatenateContentStreams = true,
            CompressStreams = false,
            RemoveUnusedObjects = false,
            RemoveDuplicates = false
        });

        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        var contents = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]);

        var merged = Assert.IsAssignableFrom<PdfStream>(contents);
        string body = Encoding.ASCII.GetString(merged.GetDecodedData());
        Assert.Contains("StreamA", body);
        Assert.Contains("StreamB", body);
    }

    [Fact]
    public void Optimize_ConcatenateContentStreams_PreservesText()
    {
        byte[] pdfBytes = BuildPdfWithMultipleContentStreams();

        using var input = new MemoryStream(pdfBytes);
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            ConcatenateContentStreams = true
        });

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        string text = PdfTextExtractor.ExtractText(doc);
        Assert.Contains("StreamA", text);
        Assert.Contains("StreamB", text);
    }

    // -------------------------------------------------------------------
    //  PDF builders for new-feature tests
    // -------------------------------------------------------------------

    private static byte[] BuildPdfWithXmpMetadata()
    {
        const string xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'></x:xmpmeta><?xpacket end='w'?>";
        const string content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int off4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content.Length, content);
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int off6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Type /Metadata /Subtype /XML /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", xmp.Length, xmp);

        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.AppendFormat("{0:D10} 00000 n \n", off4);
        sb.AppendFormat("{0:D10} 00000 n \n", off5);
        sb.AppendFormat("{0:D10} 00000 n \n", off6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The same document as <see cref="BuildPdfWithThumbnail"/>, except that the catalog holds its
    /// page-tree root as a <b>direct</b> dictionary rather than <c>2 0 R</c>.
    ///
    /// <para>ISO 32000-1 Table 28 says <c>/Pages</c> "shall be an indirect reference", so this is
    /// malformed — and reading malformed files the way Core reads them is the house rule
    /// (<see cref="PageTreeShape"/> exists for the same reason one key further in). Core's own page
    /// tree resolves the entry rather than requiring a reference, so it finds the page; the
    /// optimizer's private walk pushed <i>object numbers</i> and began with
    /// <c>catalog[/Pages] is PdfReference</c>, so it found nothing at all and every page-scoped
    /// option reported a successful run over a document it had not touched. The scanners' shared
    /// <c>PageTreeWalk</c> — in this same assembly — never had that gap.</para>
    /// </summary>
    /// <summary>
    /// The other half of "the walk visits objects, not object numbers": an <b>indirect</b> root
    /// whose single kid is a page dictionary written <b>directly</b> inside <c>/Kids</c>. Table 29
    /// says <c>/Kids</c> holds indirect references, and Core reads this spelling anyway.
    ///
    /// <para>The page therefore has no object number, which is the case
    /// <see cref="PageTreeWalk.PageEntry.ObjectNumber"/> answers with <c>-1</c>: the optimizer
    /// mutates the dictionary in place — it is serialised as part of object 2 — so the option
    /// still works without one.</para>
    /// </summary>
    private static byte[] BuildPdfWithDirectPageKid()
    {
        const string content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        const string thumbData = "RGB";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new List<int>();
        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Count 1 /Kids [ << /Type /Page /MediaBox [0 0 612 792] "
                  + "/Contents 3 0 R /Resources << /Font << /F1 4 0 R >> >> /Thumb 5 0 R >> ] >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.AppendFormat("3 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content.Length, content);
        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.AppendFormat("5 0 obj\n<< /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", thumbData.Length, thumbData);

        int xrefOff = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Two pages that <b>share</b> a <c>/Contents</c> fragment, so
    /// <c>ConcatenateContentStreams</c> must refuse both and name each one. The refusal message is
    /// the only reader of <see cref="PageTreeWalk.PageEntry.ObjectNumber"/>, which is why this
    /// fixture exists: without it the number the walk carries is unasserted.
    /// </summary>
    private static byte[] BuildPdfWithTwoPagesSharingAContentStream()
    {
        const string shared = "BT /F1 12 Tf 100 700 Td (Shared) Tj ET";
        const string onlyA = "BT /F1 12 Tf 100 600 Td (OnlyA) Tj ET";
        const string onlyB = "BT /F1 12 Tf 100 500 Td (OnlyB) Tj ET";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new List<int>();
        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [5 0 R 6 0 R] /Resources << /Font << /F1 8 0 R >> >> >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [5 0 R 7 0 R] /Resources << /Font << /F1 8 0 R >> >> >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.AppendFormat("5 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", shared.Length, shared);
        offsets.Add(sb.Length);
        sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", onlyA.Length, onlyA);
        offsets.Add(sb.Length);
        sb.AppendFormat("7 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", onlyB.Length, onlyB);
        offsets.Add(sb.Length);
        sb.Append("8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <param name="multipleContentStreams">
    /// When true the page's <c>/Contents</c> is a two-element array, so the fixture also reaches
    /// <c>ConcatenateContentStreams</c> — a second consumer of the same walk.
    /// </param>
    private static byte[] BuildPdfWithDirectPageTreeRoot(bool multipleContentStreams = false)
    {
        const string csA = "BT /F1 12 Tf 100 700 Td (StreamA) Tj ET";
        const string csB = "BT /F1 12 Tf 100 600 Td (StreamB) Tj ET";
        const string thumbData = "RGB";

        string contentsEntry = multipleContentStreams ? "[3 0 R 6 0 R]" : "3 0 R";
        int objectCount = multipleContentStreams ? 6 : 5;

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new List<int>();
        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages << /Type /Pages /Kids [2 0 R] /Count 1 >> >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Page /MediaBox [0 0 612 792] /Contents " + contentsEntry
                  + " /Resources << /Font << /F1 4 0 R >> >> /Thumb 5 0 R >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.AppendFormat("3 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", csA.Length, csA);
        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.AppendFormat("5 0 obj\n<< /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", thumbData.Length, thumbData);
        if (multipleContentStreams)
        {
            offsets.Add(sb.Length);
            sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", csB.Length, csB);
        }

        int xrefOff = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n", objectCount + 1);
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", objectCount + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithThumbnail()
    {
        // A tiny fake thumbnail stream (raw pixel data — the test only checks /Thumb is gone).
        const string content = "BT /F1 12 Tf 100 700 Td (Hello) Tj ET";
        const string thumbData = "RGB"; // 3-byte placeholder
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /Thumb 6 0 R >>\nendobj\n");
        int off4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content.Length, content);
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int off6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", thumbData.Length, thumbData);

        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.AppendFormat("{0:D10} 00000 n \n", off4);
        sb.AppendFormat("{0:D10} 00000 n \n", off5);
        sb.AppendFormat("{0:D10} 00000 n \n", off6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A page drawing one image XObject whose JPEG — Skia's, so under the Annex K stock tables the
    /// re-fit improves — is wrapped in an ASCII85 layer: <c>/Filter [/ASCII85Decode /DCTDecode]</c>.
    /// </summary>
    private static byte[] BuildPdfWithAscii85Jpeg()
    {
        byte[] jpeg;
        using (var bitmap = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque))
        {
            var random = new Random(4242);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    int noise = random.Next(48);
                    bitmap.SetPixel(x, y, new SkiaSharp.SKColor((byte)Math.Min(255, x * 4 + noise), (byte)Math.Min(255, y * 4 + noise), 128));
                }
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
            jpeg = data.ToArray();
        }
        string a85 = EncodeAscii85(jpeg) + "~>";

        const string content = "q 64 0 0 64 100 600 cm /Im0 Do Q";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>");
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Obj($"<< /Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8 " +
            $"/Filter [/ASCII85Decode /DCTDecode] /Length {a85.Length} >>\nstream\n{a85}\nendstream");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithAscii85Stream()
    {
        // The extra object 6 carries an ASCII85-encoded stream so the optimizer
        // can strip the ASCII85Decode filter.
        const string plainText = "Hello from ASCII85";
        byte[] plain = Encoding.ASCII.GetBytes(plainText);
        string a85 = EncodeAscii85(plain) + "~>";

        const string content = "BT /F1 12 Tf 100 700 Td (Hi) Tj ET";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int off4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content.Length, content);
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int off6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Filter /ASCII85Decode /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", a85.Length, a85);

        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.AppendFormat("{0:D10} 00000 n \n", off4);
        sb.AppendFormat("{0:D10} 00000 n \n", off5);
        sb.AppendFormat("{0:D10} 00000 n \n", off6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithLzwStream()
    {
        const string plainText = "Hello from LZW stream. AAAAAAAAAAAAA.";
        byte[] plain = Encoding.ASCII.GetBytes(plainText);
        byte[] lzwData = EncodeLzw(plain);

        const string content = "BT /F1 12 Tf 100 700 Td (Hi) Tj ET";
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        w.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        var offsets = new long[7];

        offsets[1] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));
        offsets[2] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));
        offsets[3] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n"));
        offsets[4] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));
        offsets[5] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));
        offsets[6] = ms.Position;
        w.Write(Encoding.ASCII.GetBytes($"6 0 obj\n<< /Filter /LZWDecode /Length {lzwData.Length} >>\nstream\n"));
        w.Write(lzwData);
        w.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        long xrefOff = ms.Position;
        w.Write(Encoding.ASCII.GetBytes("xref\n0 7\n"));
        w.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
        for (int i = 1; i <= 6; i++)
            w.Write(Encoding.ASCII.GetBytes($"{offsets[i]:D10} 00000 n \n"));
        w.Write(Encoding.ASCII.GetBytes("trailer\n<< /Size 7 /Root 1 0 R >>\n"));
        w.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOff}\n%%EOF\n"));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Renames a fixture's page-tree root <c>/Type</c> key to <c>/Junk</c>, leaving a node that
    /// carries no <c>/Type</c> at all — the malformed-but-readable shape §7.7.3.2 forbids and real
    /// producers emit. <c>/Junk</c> is the same length as <c>/Type</c>, so every xref offset in the
    /// fixture stays valid; rebuilding each file by hand would duplicate its builder for one key's
    /// difference.
    /// </summary>
    private static byte[] WithUntypedPageTreeRoot(byte[] pdf)
    {
        byte[] needle = Encoding.ASCII.GetBytes("/Type /Pages");
        int at = IndexOfBytes(pdf, needle, 0);
        Assert.True(at >= 0, "the fixture no longer contains '/Type /Pages'");
        Assert.Equal(-1, IndexOfBytes(pdf, needle, at + 1));   // and only the one node carries it

        foreach (char c in "/Junk") pdf[at++] = (byte)c;
        return pdf;
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int from)
    {
        for (int i = from; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static byte[] BuildPdfWithMultipleContentStreams()
    {
        // Page (obj 3) has /Contents [4 0 R 5 0 R] — two separate content streams.
        const string cs1 = "BT /F1 12 Tf 100 700 Td (StreamA) Tj ET";
        const string cs2 = "BT /F1 12 Tf 100 600 Td (StreamB) Tj ET";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        // Page dict — /Contents is an array of two stream refs
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [4 0 R 5 0 R] /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");
        int off4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs1.Length, cs1);
        int off5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs2.Length, cs2);
        int off6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.AppendFormat("{0:D10} 00000 n \n", off4);
        sb.AppendFormat("{0:D10} 00000 n \n", off5);
        sb.AppendFormat("{0:D10} 00000 n \n", off6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOff);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithRawImage(int w, int h)
    {
        // A raw (unfiltered) DeviceRGB image — the PNG predictor targets these.
        byte[] pixels = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 3;
                pixels[idx]     = (byte)(x * 10);
                pixels[idx + 1] = (byte)(y * 10);
                pixels[idx + 2] = 128;
            }

        const string content = "q 20 0 0 20 100 600 cm /Im0 Do Q";
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        var offsets = new long[7];

        offsets[1] = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));
        offsets[2] = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));
        offsets[3] = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            $"/Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n"));
        offsets[4] = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));
        offsets[5] = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes(
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {pixels.Length} >>\nstream\n"));
        bw.Write(pixels);
        bw.Write(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        offsets[6] = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n"));

        long xrefOff = ms.Position;
        bw.Write(Encoding.ASCII.GetBytes("xref\n0 7\n"));
        bw.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
        for (int i = 1; i <= 6; i++)
            bw.Write(Encoding.ASCII.GetBytes($"{offsets[i]:D10} 00000 n \n"));
        bw.Write(Encoding.ASCII.GetBytes("trailer\n<< /Size 7 /Root 1 0 R >>\n"));
        bw.Write(Encoding.ASCII.GetBytes($"startxref\n{xrefOff}\n%%EOF\n"));
        bw.Flush();
        return ms.ToArray();
    }

    // -------------------------------------------------------------------
    //  Encoding helpers for test PDFs
    // -------------------------------------------------------------------

    private static string EncodeAscii85(byte[] data)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < data.Length)
        {
            int remaining = data.Length - i;
            uint group = 0;
            int take = Math.Min(4, remaining);
            for (int k = 0; k < 4; k++)
                group = (group << 8) | (k < take ? data[i + k] : 0u);
            if (take == 4 && group == 0)
            {
                sb.Append('z');
            }
            else
            {
                char[] chars = new char[5];
                for (int k = 4; k >= 0; k--)
                {
                    chars[k] = (char)('!' + group % 85);
                    group /= 85;
                }
                for (int k = 0; k <= take; k++)
                    sb.Append(chars[k]);
            }
            i += take;
        }
        return sb.ToString();
    }

    private static byte[] EncodeLzw(byte[] input)
    {
        const int ClearTable = 256, EndOfData = 257, EarlyChange = 1;
        var bits = new LzwBitWriter();
        var dict = new System.Collections.Generic.Dictionary<string, int>();
        void ResetDict() { dict.Clear(); for (int i = 0; i < 256; i++) dict[((char)i).ToString()] = i; }
        int nextCode = 258, codeSize = 9;
        ResetDict();
        bits.Write(ClearTable, codeSize);
        if (input.Length == 0) { bits.Write(EndOfData, codeSize); return bits.ToArray(); }
        string cur = ((char)input[0]).ToString();
        for (int i = 1; i < input.Length; i++)
        {
            string combined = cur + (char)input[i];
            if (dict.ContainsKey(combined)) { cur = combined; continue; }
            bits.Write(dict[cur], codeSize);
            if (nextCode < (1 << 12))
            {
                dict[combined] = nextCode++;
                if (nextCode - 1 >= (1 << codeSize) - EarlyChange && codeSize < 12) codeSize++;
            }
            cur = ((char)input[i]).ToString();
        }
        bits.Write(dict[cur], codeSize);
        bits.Write(EndOfData, codeSize);
        return bits.ToArray();
    }

    private sealed class LzwBitWriter
    {
        private readonly System.Collections.Generic.List<byte> _bytes = new();
        private int _buf, _cnt;
        public void Write(int value, int numBits)
        {
            _buf = (_buf << numBits) | (value & ((1 << numBits) - 1));
            _cnt += numBits;
            while (_cnt >= 8) { _cnt -= 8; _bytes.Add((byte)(_buf >> _cnt)); }
        }
        public byte[] ToArray() { if (_cnt > 0) _bytes.Add((byte)(_buf << (8 - _cnt))); return _bytes.ToArray(); }
    }

    // -------------------------------------------------------------------
    //  Helper
    // -------------------------------------------------------------------

    private static int CountObjectDeclarations(byte[] pdfBytes)
    {
        string text = Encoding.ASCII.GetString(pdfBytes);
        int count = 0;
        int pos = 0;
        while ((pos = text.IndexOf(" obj\n", pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos++;
        }
        return count;
    }


    // ------------------------------------------------------------------
    //  Read-back helpers
    //
    //  These tests used to assert `output.Length > 0` and `doc.Pages.Count > 0`, which hold for
    //  any parseable PDF — including one where the optimizer did nothing at all. Reading the
    //  produced image dictionary back out is what makes them able to fail.
    // ------------------------------------------------------------------

    /// <summary>The first image XObject on page 0 of a produced document.</summary>
    private static PdfStream FirstImage(byte[] pdf, out PdfDocument doc)
    {
        doc = PdfDocument.Open(new MemoryStream(pdf));
        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects  = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);

        foreach (var key in xobjects!.Keys)
            if (doc.ResolveObject(xobjects[key]) is PdfStream img &&
                (img[PdfName.Subtype] as PdfName)?.Value == "Image")
                return img;

        throw new Xunit.Sdk.XunitException("the produced document has no image XObject");
    }

    private static byte[] Optimize(byte[] input, OptimizationOptions options)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        PdfOptimizer.Optimize(i, o, options);
        return o.ToArray();
    }

    private static string NameOf(PdfObject o) => (o as PdfName)?.Value;
    private static int IntOf(PdfObject o) => (int)((PdfNumber)o).LongValue;

    private static ImageOptimizationOptions Jpeg(int quality = 75) => new()
    {
        Enabled = true,
        TargetFormat = ImageFormat.Jpeg,
        Quality = quality,
        SkipAlreadyOptimized = false,
    };
}
