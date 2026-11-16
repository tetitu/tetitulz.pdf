// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Layout;

/// <summary>
/// <see cref="TableCellStyle.PaddingTop"/> changes the table's geometry.
/// </summary>
/// <remarks>
/// <para><b>It was declared, documented as "vertical padding inside the cell", and read by
/// nothing.</b> The row height came from a private <c>RowTextPadding = 8f</c> — exactly twice this
/// property's default of 4, which is the tell: the intent was there and the literal replaced it. So
/// a caller raising the padding to open the rows up changed no pixel. <c>PaddingLeft</c> beside it
/// <i>was</i> read, so horizontal padding worked and vertical padding silently did not.</para>
///
/// <para><b>The test it had asserted <c>PaddingLeft &gt;= 0</c></b>, which a default of 4 satisfies
/// for ever. These measure the rendered table instead: the inked height of the drawn rows, which is
/// what a caller adjusting padding is trying to change.</para>
///
/// <para>At the default the row height is still <c>FontSize + 8</c>, so existing output does not
/// move — the first test below is what pins that.</para>
/// </remarks>
public class TableCellPaddingTests
{
    /// <summary>The default padding leaves the historical row height unchanged.</summary>
    /// <remarks>
    /// The compatibility half. Replacing a literal with a property is only safe if the property's
    /// default reproduces the literal, and this is the assertion that says so.
    /// </remarks>
    [Fact]
    public void TheDefaultPaddingKeepsTheHistoricalRowHeight()
    {
        int inked = InkedRowSpan(paddingTop: null);

        // FontSize defaults to 14 and the historical padding was 8, so a one-row table inks about
        // 22 points of height. The range allows for the border stroke either side.
        Assert.InRange(inked, 19, 26);
    }

    /// <summary>A larger padding makes the row taller.</summary>
    [Fact]
    public void ALargerPaddingMakesTheRowTaller()
    {
        int tight = InkedRowSpan(paddingTop: 2f);
        int loose = InkedRowSpan(paddingTop: 20f);

        Assert.True(tight > 0, "the control drew no table at all");
        Assert.True(loose > tight + 20,
            $"PaddingTop did not change the row height: {tight} points at 2 pt padding against "
          + $"{loose} at 20 pt");
    }

    /// <summary>
    /// One row has one height, so the tallest cell's padding wins rather than the last cell's.
    /// </summary>
    /// <remarks>
    /// Without this, reading the padding from whichever cell the loop happened to end on would
    /// satisfy the test above and clip the cell that asked for more.
    /// </remarks>
    [Fact]
    public void TheLargestPaddingInARowWins()
    {
        int lastCellSmall = InkedRowSpan(paddingTop: 20f, secondCellPadding: 2f);
        int bothSmall     = InkedRowSpan(paddingTop: 2f,  secondCellPadding: 2f);

        Assert.True(lastCellSmall > bothSmall + 20,
            $"a row took its height from the LAST cell rather than the tallest request: "
          + $"{lastCellSmall} against {bothSmall} when both are small");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>
    /// Height in points of the inked band the table occupies, measured on the rendered page.
    /// </summary>
    private static int InkedRowSpan(float? paddingTop, float? secondCellPadding = null)
    {
        var table = new PdfTableBuilder { StartX = 20f, StartY = 700f };
        table.AddColumn(80f);
        table.AddColumn(80f);
        table.AddRow()
             .AddCell("A", StyleWith(paddingTop))
             .AddCell("B", StyleWith(secondCellPadding ?? paddingTop));

        var doc = new PdfLayoutDocument();
        var page = doc.AddPage();
        table.Build(page, doc);

        using var buffer = new MemoryStream();
        doc.Save(buffer);
        byte[] pdf = buffer.ToArray();
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int top = int.MaxValue, bottom = -1;
        for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha <= 8 || (c.Red >= 240 && c.Green >= 240 && c.Blue >= 240)) continue;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
                break;
            }
        return bottom < 0 ? 0 : bottom - top + 1;
    }

    private static TableCellStyle StyleWith(float? paddingTop)
    {
        var s = new TableCellStyle();
        if (paddingTop.HasValue) s.PaddingTop = paddingTop.Value;
        return s;
    }
}
