// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Review-fix regression: PdfMetadataWriter.EnsureSeekable previously only rewound
/// MemoryStream inputs. A seekable non-MemoryStream input (e.g. a FileStream)
/// positioned mid-way was CopyTo'd from its current offset, truncating the base
/// document and corrupting every incremental-update writer that flows through it
/// (metadata, named-dest, bookmark, link). The fix rewinds ANY seekable stream.
/// </summary>
public class ReviewFixOps_EnsureSeekableTests
{
    private static bool IsPdf(MemoryStream ms)
    {
        if (ms.Length < 5) return false;
        long pos = ms.Position;
        ms.Position = 0;
        var h = new byte[5];
        ms.Read(h, 0, 5);
        ms.Position = pos;
        return h[0] == '%' && h[1] == 'P' && h[2] == 'D' && h[3] == 'F' && h[4] == '-';
    }

    // ----------------------------------------------------------------
    //  EnsureSeekable directly: rewinds a seekable non-MemoryStream
    // ----------------------------------------------------------------

    [Fact]
    public void EnsureSeekable_FileStreamPositionedMidway_ProducesFullCopy()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Seek me");
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, pdf);
            using var fs = File.OpenRead(path);
            fs.Position = pdf.Length / 2;   // mid-way — pre-fix this truncated the copy

            using var result = PdfMetadataWriter.EnsureSeekable(fs);
            Assert.Equal(pdf.Length, result.Length);   // full document, not truncated
            Assert.Equal(0, result.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ----------------------------------------------------------------
    //  WriteInfo on a mid-positioned FileStream → valid PDF
    // ----------------------------------------------------------------

    [Fact]
    public void WriteInfo_FileStreamPositionedMidway_ProducesValidPdf()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Body");
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, pdf);
            using var fs = File.OpenRead(path);
            fs.Position = 10;   // simulate a mid-way positioned source

            using var output = new MemoryStream();
            PdfMetadataWriter.WriteInfo(fs, output,
                new PdfMetadataUpdate { Title = "Hello", Author = "Tester" });

            Assert.True(IsPdf(output), "Output must start with the %PDF- header.");
            // The original page-1 content must survive (base doc was not truncated).
            output.Position = 0;
            string text = new StreamReader(output, Encoding.ASCII).ReadToEnd();
            Assert.Contains("/Catalog", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ----------------------------------------------------------------
    //  Named-destination write on a mid-positioned stream → valid PDF
    // ----------------------------------------------------------------

    [Fact]
    public void SetDestination_FileStreamPositionedMidway_RoundTrips()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Body");
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, pdf);
            using var fs = File.OpenRead(path);
            fs.Position = 20;

            using var output = new MemoryStream();
            PdfNamedDestinations.SetDestination(fs, output, "top", pageIndex: 0);

            Assert.True(IsPdf(output));

            // Re-read: the new destination must be listable.
            output.Position = 0;
            var dests = PdfNamedDestinations.ListDestinations(output);
            Assert.Contains(dests, d => d.Name == "top");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ----------------------------------------------------------------
    //  MemoryStream with non-zero Position is rewound (already worked,
    //  guard against regression).
    // ----------------------------------------------------------------

    [Fact]
    public void WriteInfo_MemoryStreamNonZeroPosition_ProducesValidPdf()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Body");
        using var input = new MemoryStream();
        input.Write(pdf, 0, pdf.Length);
        input.Position = 37;   // non-zero on entry

        using var output = new MemoryStream();
        PdfMetadataWriter.WriteInfo(input, output, new PdfMetadataUpdate { Title = "X" });

        Assert.True(IsPdf(output));
    }
}
