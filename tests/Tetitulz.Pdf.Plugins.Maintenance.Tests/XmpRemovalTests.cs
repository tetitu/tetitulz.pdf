// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="OptimizationOptions.RemoveXmpMetadata"/>.
///
/// <para>§14.3.2 lets <b>any</b> object carry a <c>/Metadata</c> stream, and Adobe's tools attach
/// one per placed asset. The option used to remove the catalog's and stop; on a 22 MB corpus
/// document that was 3 439 bytes of the 9 679 007 present. The fixture here mirrors that shape —
/// a small catalog packet and a large one hanging off an image — so a test that only checked "the
/// catalog no longer has /Metadata" would pass against the old behaviour and against the new one
/// alike. What separates them is the <b>image's</b> packet and the byte count.</para>
/// </summary>
public class XmpRemovalTests
{
    private const string CatalogXmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta "
                                    + "xmlns:x='adobe:ns:meta/'><rdf:RDF/></x:xmpmeta><?xpacket end='w'?>";

    /// <summary>Deliberately much larger than the catalog's, as it is on a real file.</summary>
    private static string ImageXmp =>
        "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'>"
        + new string('=', 4000) + "</x:xmpmeta><?xpacket end='w'?>";

    [Fact]
    public void Optimize_RemovesTheObjectLevelXmpPacket_NotOnlyTheCatalogs()
    {
        byte[] pdf = BuildPdfWithXmpEverywhere();

        var result = Optimize(pdf, XmpOnly(), out byte[] optimized);

        Assert.Equal(2, result.XmpStreamsRemoved);
        Assert.Null(CatalogMetadata(optimized));
        Assert.Null(ImageMetadata(optimized));
    }

    /// <summary>
    /// The point of the option is the bytes, and they only come back once the unreachable packets
    /// are pruned — so this asserts the output actually shrank by roughly the large packet, not
    /// merely that a key is gone.
    /// </summary>
    [Fact]
    public void Optimize_ReclaimsTheBytesOfEveryRemovedPacket()
    {
        byte[] pdf = BuildPdfWithXmpEverywhere();

        var withXmp = XmpOnly();
        withXmp.RemoveXmpMetadata = false;

        Optimize(pdf, withXmp, out byte[] kept);
        Optimize(pdf, XmpOnly(), out byte[] stripped);

        Assert.True(kept.Length - stripped.Length > 3500,
            $"stripping XMP saved only {kept.Length - stripped.Length} bytes of a ~4 000-byte packet");
    }

    /// <summary>
    /// A document whose only XMP is the catalog's is the case the option always handled, and it
    /// must keep handling it — the change widened the scope, it did not move it.
    /// </summary>
    [Fact]
    public void Optimize_StillRemovesTheCatalogPacketWhenItIsTheOnlyOne()
    {
        byte[] pdf = BuildPdfWithXmpEverywhere(includeImageXmp: false);

        var result = Optimize(pdf, XmpOnly(), out byte[] optimized);

        Assert.Equal(1, result.XmpStreamsRemoved);
        Assert.Null(CatalogMetadata(optimized));
    }

    [Fact]
    public void Optimize_LeavesXmpAloneWhenTheOptionIsOff()
    {
        byte[] pdf = BuildPdfWithXmpEverywhere();

        var options = XmpOnly();
        options.RemoveXmpMetadata = false;

        var result = Optimize(pdf, options, out byte[] optimized);

        Assert.Equal(0, result.XmpStreamsRemoved);
        Assert.NotNull(CatalogMetadata(optimized));
        Assert.NotNull(ImageMetadata(optimized));
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    private static OptimizationOptions XmpOnly() => new OptimizationOptions
    {
        RemoveXmpMetadata = true,
        RemoveUnusedObjects = true,     // the packets are only reclaimed once they are pruned
        CompressStreams = false,
        RemoveDuplicates = false,
    };

    /// <summary>
    /// A catalog packet, an image packet, and an image the page actually draws. The image is raw
    /// samples so no image pass rewrites it and changes the byte comparison out from under us.
    /// </summary>
    private static byte[] BuildPdfWithXmpEverywhere(bool includeImageXmp = true)
    {
        const int width = 8, height = 8;
        var samples = new byte[width * height * 3];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 7);

        byte[] catalogXmp = Encoding.ASCII.GetBytes(CatalogXmp);
        byte[] imageXmp = Encoding.ASCII.GetBytes(ImageXmp);
        const string content = "q 8 0 0 8 100 600 cm /Im0 Do Q";

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[8];

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
            + "/ColorSpace /DeviceRGB /BitsPerComponent 8 "
            + (includeImageXmp ? "/Metadata 7 0 R " : "")
            + $"/Length {samples.Length} >>\nstream\n");
        ms.Write(samples, 0, samples.Length);
        Write("\nendstream\nendobj\n");

        offsets[6] = ms.Position;
        Write($"6 0 obj\n<< /Type /Metadata /Subtype /XML /Length {catalogXmp.Length} >>\nstream\n");
        ms.Write(catalogXmp, 0, catalogXmp.Length);
        Write("\nendstream\nendobj\n");

        offsets[7] = ms.Position;
        Write($"7 0 obj\n<< /Type /Metadata /Subtype /XML /Length {imageXmp.Length} >>\nstream\n");
        ms.Write(imageXmp, 0, imageXmp.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject obj)
    {
        int guard = 0;
        while (obj is PdfReference reference && guard++ < 32)
            obj = document.GetObject(reference.ReferenceObjectNumber);
        return obj;
    }

    private static PdfObject CatalogMetadata(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var catalog = Resolve(document, document.Trailer[PdfName.Root]) as PdfDictionary;
        Assert.NotNull(catalog);
        return catalog[PdfName.Metadata];
    }

    private static PdfObject ImageMetadata(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var catalog = Resolve(document, document.Trailer[PdfName.Root]) as PdfDictionary;
        var pages = Resolve(document, catalog[PdfName.Pages]) as PdfDictionary;
        var kids = Resolve(document, pages[PdfName.Kids]) as PdfArray;
        var page = Resolve(document, kids[0]) as PdfDictionary;
        var resources = Resolve(document, page[PdfName.Resources]) as PdfDictionary;
        var xobjects = Resolve(document, resources[PdfName.XObject]) as PdfDictionary;
        var image = Resolve(document, xobjects[new PdfName("Im0")]) as PdfStream;

        Assert.NotNull(image);
        return image[PdfName.Metadata];
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
