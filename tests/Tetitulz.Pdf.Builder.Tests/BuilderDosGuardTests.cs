// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Adversarial regressions from remediation round 37. The earlier zip-bomb / image-cap /
/// nesting-depth hardening was applied to the legacy DocxToPdf renderer but NOT to the parallel
/// Builder-model path. Two holes are covered here through the public <see cref="PdfExporter"/>:
///   • the Builder TableRenderer had no nesting-depth guard at all, while the legacy renderer
///     had one — a deeply-nested table model recursed Render/Measure to an uncatchable
///     StackOverflow. Both engines now cap at <c>PdfLimits.MaxTableNestingDepth</c>.
///   • BuilderImageDecoder had no dimension cap, so an IHDR declaring huge dimensions drove
///     <c>new byte[width*height*3]</c> to overflow int / allocate gigabytes.
/// </summary>
public class BuilderDosGuardTests
{
    private static Document OneSection(IDocElement child)
    {
        var doc = new Document();
        var sec = new Section();
        sec.Children.Add(child);
        doc.Sections.Add(sec);
        return doc;
    }

    [Fact]
    public void DeeplyNestedTables_DoNotStackOverflow()
    {
        // Build the nesting iteratively (inside-out) so the TEST thread doesn't recurse.
        Table inner = null;
        for (int i = 0; i < 2000; i++)
        {
            var t = new Table();
            var row = new TableRow();
            var cell = new TableCell();
            if (inner != null) cell.Blocks.Add(inner); else cell.Blocks.Add(new Paragraph());
            row.Cells.Add(cell);
            t.Rows.Add(row);
            inner = t;
        }

        var doc = OneSection(inner!);
        using var ms = new MemoryStream();
        var ex = Record.Exception(() => new PdfExporter().Export(doc, ms));
        Assert.Null(ex);
        Assert.True(ms.Length > 0);
    }

    /// <summary>
    /// A nested table's column content is read by every table around it, by each of their draws and
    /// row measures; measured afresh each time a chain at the nesting cap cost the cube of its depth.
    /// Measured once per table and measure, the work is linear: at most four measures a table (its
    /// min- and max-content, in the draw's context and in a scratch one), where the uncached chain
    /// of 24 took thousands. The evidence is the count, not a clock.
    /// </summary>
    [Fact]
    public void AChainOfNestedTablesAtTheCap_MeasuresEachTablesColumnsOnce()
    {
        int depth = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTableNestingDepth;
        Table inner = null;
        for (int i = 0; i < depth; i++)
        {
            var t = new Table { ShrinkToContent = true, AutoLayout = true };
            var row = new TableRow();
            for (int c = 0; c < 3; c++)
            {
                var text = new TableCell();
                text.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "a cell beside the nested one" } } });
                row.Cells.Add(text);
            }
            var cell = new TableCell();
            if (inner != null) cell.Blocks.Add(inner);
            row.Cells.Add(cell);
            t.Rows.Add(row);
            inner = t;
        }

        long before = TableRenderer.ColumnContentMeasures;
        OneSection(inner!).ToPdf();
        long measures = TableRenderer.ColumnContentMeasures - before;

        Assert.InRange(measures, 2 * depth, 4 * depth);
    }

    /// <summary>
    /// A baseline cell's nested table is read for its first baseline and for its height, each reading
    /// the rows below it again: measured afresh, a chain whose every other level is baseline-aligned
    /// cost twice as much for each such level (1.3 s at 22 deep, two cells a level). Each row's height
    /// is measured once per context and its widths, so the count stays a small multiple of the rows.
    /// </summary>
    [Fact]
    public void AChainOfBaselineCellsAtTheCap_MeasuresEachRowOnce()
    {
        int depth = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTableNestingDepth;
        Table inner = null;
        for (int i = 0; i < depth; i++)
        {
            var align = i % 2 == 0 ? CellVerticalAlign.Baseline : CellVerticalAlign.Top;
            var t = new Table();
            var row = new TableRow();
            var text = new TableCell { VAlign = align };
            text.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "beside" } } });
            row.Cells.Add(text);
            var cell = new TableCell { VAlign = align };
            if (inner != null) cell.Blocks.Add(inner);
            row.Cells.Add(cell);
            t.Rows.Add(row);
            inner = t;
        }

        long before = TableRenderer.RowHeightMeasures;
        OneSection(inner!).ToPdf();
        long measures = TableRenderer.RowHeightMeasures - before;

        Assert.InRange(measures, depth, 4 * depth);
    }

    [Fact]
    public void ImageWithHugeDeclaredDimensions_IsSkippedNotAllocated()
    {
        var p = new Paragraph();
        p.Runs.Add(new ImageRun { Data = HugePng(60000, 60000), WidthPt = 100, HeightPt = 100 });

        var doc = OneSection(p);
        using var ms = new MemoryStream();
        var ex = Record.Exception(() => new PdfExporter().Export(doc, ms));
        Assert.Null(ex); // 60000×60000 > 40 MP cap → decoder returns null → image skipped, no OOM
        Assert.True(ms.Length > 0);
    }

    // Minimal PNG: 8-byte signature + IHDR (declaring width×height, 8-bit RGB) + tiny IDAT + IEND.
    // CRCs are not validated by BuilderImageDecoder, so they are left zero.
    private static byte[] HugePng(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        WriteChunk(ms, "IHDR", Concat(Be32(width), Be32(height), new byte[] { 8, 2, 0, 0, 0 }));
        WriteChunk(ms, "IDAT", new byte[] { 0x00, 0x00 });
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        s.Write(Be32(data.Length), 0, 4);
        foreach (char c in type) s.WriteByte((byte)c);
        s.Write(data, 0, data.Length);
        s.Write(new byte[4], 0, 4); // CRC placeholder (unchecked)
    }

    private static byte[] Be32(int v) => new[]
    {
        (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
        (byte)((v >> 8) & 0xFF),  (byte)(v & 0xFF),
    };

    private static byte[] Concat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }
}
