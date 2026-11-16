// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Forms;

/// <summary>
/// <see cref="WidgetRotation"/> — Table 192's <c>/MK /R</c> read once, and the one turn every
/// appearance generator and both renderers' fallbacks apply.
/// </summary>
/// <remarks>
/// The matrix is asserted by <b>transforming the layout box's corners</b> and comparing the result
/// with the rectangle, not by quoting six numbers: a sign flipped in one coefficient produces a
/// box of the right size in the wrong place, which a literal comparison can only catch if the
/// literal was itself derived correctly. Width and height differ in every row so a swap cannot
/// pass as a turn.
/// </remarks>
public class WidgetRotationTests
{
    [Theory]
    [InlineData("<< /R 90 >>", 90)]
    [InlineData("<< /R 180 >>", 180)]
    [InlineData("<< /R 270 >>", 270)]
    [InlineData("<< /R 0 >>", 0)]
    [InlineData("<< /R 360 >>", 0)]      // a full turn
    [InlineData("<< /R -90 >>", 270)]    // normalised
    [InlineData("<< /R 450 >>", 90)]
    [InlineData("<< /R 45 >>", 0)]       // not a multiple of 90: a producer error, drawn upright
    [InlineData("<< /R 90.5 >>", 0)]
    [InlineData("<< /BC [0 0 0] >>", 0)] // /MK without /R
    [InlineData(null, 0)]                // no /MK
    public void TheTurnIsReadFromMkR_AndAnythingButAMultipleOf90ReadsAsUpright(string mk, int expected)
    {
        var widget = new PdfDictionary();
        if (mk != null) widget.Set(PdfName.MK, Parse(mk));

        Assert.Equal(expected, WidgetRotation.Read(widget, null));
    }

    /// <summary>§7.3.10: both <c>/MK</c> and its <c>/R</c> may be indirect.</summary>
    [Fact]
    public void AnIndirectMkOrR_IsFollowed()
    {
        var mk = new PdfDictionary();
        mk.Set(PdfName.R, new PdfReference(7, 0));
        var widget = new PdfDictionary();
        widget.Set(PdfName.MK, new PdfReference(6, 0));

        PdfObject Resolve(PdfObject o) => o switch
        {
            PdfReference { ReferenceObjectNumber: 6 } => mk,
            PdfReference { ReferenceObjectNumber: 7 } => new PdfNumber(270),
            _ => o,
        };

        Assert.Equal(270, WidgetRotation.Read(widget, Resolve));
        Assert.Equal(0, WidgetRotation.Read(widget, null));   // unresolved references read as absent
    }

    [Theory]
    [InlineData(0, 120, 30, 120, 30)]
    [InlineData(90, 120, 30, 30, 120)]
    [InlineData(180, 120, 30, 120, 30)]
    [InlineData(270, 120, 30, 30, 120)]
    public void TheLayoutBoxIsTheRectangleSwappedForAQuarterTurn(
        int rotation, double w, double h, double boxW, double boxH)
    {
        Assert.Equal((boxW, boxH), WidgetRotation.LayoutBox(rotation, w, h));
    }

    /// <summary>
    /// The matrix turns the layout box onto exactly the rectangle, so §12.5.5's fit is a pure
    /// translation: the transformed <c>/BBox</c> has the rectangle's size and starts at its origin.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TheMatrixTurnsTheLayoutBoxOntoTheRectangle(int rotation)
    {
        const double w = 120, h = 30;
        var (bw, bh) = WidgetRotation.LayoutBox(rotation, w, h);
        PdfArray matrix = WidgetRotation.Matrix(rotation, w, h);
        Assert.NotNull(matrix);
        Assert.Equal(6, matrix.Count);

        double[] m = new double[6];
        for (int i = 0; i < 6; i++) m[i] = ((PdfNumber)matrix[i]).DoubleValue;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in new[] { (0d, 0d), (bw, 0d), (0d, bh), (bw, bh) })
        {
            double tx = m[0] * x + m[2] * y + m[4];
            double ty = m[1] * x + m[3] * y + m[5];
            minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
            minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
        }

        Assert.Equal(0, minX, 9);
        Assert.Equal(0, minY, 9);
        Assert.Equal(w, maxX, 9);
        Assert.Equal(h, maxY, 9);
    }

    /// <summary>
    /// The turn is counterclockwise (Table 192): the layout box's x axis points up the page for
    /// 90, so its origin lands at the rectangle's lower-right corner, and down the page for 270.
    /// </summary>
    [Fact]
    public void TheTurnIsCounterclockwise()
    {
        Assert.Equal((120d, 0d), WidgetRotation.ToRectangle(90, 120, 30, 0, 0));
        Assert.Equal((120d, 30d), WidgetRotation.ToRectangle(90, 120, 30, 30, 0));   // along the box's x: up the page
        Assert.Equal((0d, 30d), WidgetRotation.ToRectangle(270, 120, 30, 0, 0));
        Assert.Equal((120d, 30d), WidgetRotation.ToRectangle(180, 120, 30, 0, 0));
        Assert.Equal((5d, 7d), WidgetRotation.ToRectangle(0, 120, 30, 5, 7));
    }

    [Fact]
    public void NoTurnWritesNoMatrix()
    {
        Assert.Null(WidgetRotation.Matrix(0, 120, 30));
    }

    /// <summary>
    /// The form's matrix and the fallback's coordinates are one set of coefficients, so a point
    /// mapped by hand through the written matrix is where the fallback puts it.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void TheWrittenMatrixAndTheDirectMappingAgree(int rotation)
    {
        PdfArray matrix = WidgetRotation.Matrix(rotation, 120, 30);
        double[] m = new double[6];
        for (int i = 0; i < 6; i++) m[i] = ((PdfNumber)matrix[i]).DoubleValue;

        const double x = 11, y = 4;
        var (px, py) = WidgetRotation.ToRectangle(rotation, 120, 30, x, y);
        Assert.Equal(m[0] * x + m[2] * y + m[4], px, 9);
        Assert.Equal(m[1] * x + m[3] * y + m[5], py, 9);
    }

    private static PdfDictionary Parse(string dictionary)
    {
        var d = new PdfDictionary();
        // Minimal: "<< /R 90 >>" or "<< /BC [0 0 0] >>" — the numeric /R rows are the only ones read.
        string body = dictionary.Trim().TrimStart('<').TrimEnd('>').Trim();
        if (body.StartsWith("/R ", StringComparison.Ordinal))
        {
            string number = body.Substring(3).Trim();
            d.Set(PdfName.R, new PdfNumber(double.Parse(number, System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (body.StartsWith("/BC ", StringComparison.Ordinal))
        {
            var bc = new PdfArray();
            bc.Add(new PdfNumber(0)); bc.Add(new PdfNumber(0)); bc.Add(new PdfNumber(0));
            d.Set(PdfName.BC, bc);
        }
        return d;
    }
}
