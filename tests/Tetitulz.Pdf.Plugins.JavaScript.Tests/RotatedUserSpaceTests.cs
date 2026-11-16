// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// Rotated user space, as Acrobat defines it for <c>getPageBox</c> and <c>Matrix2D.fromRotated</c>:
/// its origin is the displayed crop box's lower-left corner, so the crop box is always
/// <c>[0, height, width, 0]</c> and a larger media box reaches below zero; <c>fromRotated</c> takes a
/// point of that space into default user space, and its inverse the other way. The figures are
/// Thom Parker's published ones for a letter page rotated 90° and cropped half an inch on every side
/// (Crop <c>[0,540,720,0]</c>, Media <c>[-36,576,756,-36]</c>), the other three rotations derived
/// from the same rule.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class RotatedUserSpaceTests : IDisposable
{
    private readonly JintJavaScriptPlugin _plugin = new();

    public RotatedUserSpaceTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    /// <param name="origin">
    /// The crop box's corner, in default user space, that the viewer shows at the lower left — the
    /// rotated origin: the crop's own lower-left unrotated, its lower-right turned 90°, its upper-right
    /// turned 180°, its upper-left turned 270°.
    /// </param>
    [Theory]
    [InlineData(90, "0,540,720,0", "-36,576,756,-36", "0,1,-1,0,576,36", "576,36")]
    [InlineData(0, "0,720,540,0", "-36,756,576,-36", "1,0,0,1,36,36", "36,36")]
    [InlineData(180, "0,720,540,0", "-36,756,576,-36", "-1,0,0,-1,576,756", "576,756")]
    [InlineData(270, "0,540,720,0", "-36,576,756,-36", "0,-1,1,0,36,756", "36,756")]
    public void GetPageBoxAndFromRotated_PlaceTheOriginAtTheDisplayedCropBoxCorner(int rotate, string crop, string media, string matrix, string origin)
    {
        const string script =
            "var m = new Matrix2D().fromRotated(this, 1);" +
            "var corner = m.transform([0, 0]);" +
            "var back = m.transform(m.invert().transform([100, 200]));" +
            "event.value = [this.getPageBox('Crop', 1).join(','), this.getPageBox('Media', 1).join(','), this.getPageBox('Trim', 1).join(',')," +
            " [m.a, m.b, m.c, m.d, m.h, m.v].join(','), corner.join(','), back.join(','), m !== new Matrix2D()].join('|');";
        PdfJsExecutionContext ctx = Run(Document(script, rotate));

        Assert.Equal($"{crop}|{media}|{crop}|{matrix}|{origin}|100,200|true", ctx.EventValue);
    }

    /// <summary>A page the document lacks: the identity (Acrobat throws) and an empty box; the first page by default.</summary>
    [Fact]
    public void APageTheDocumentLacks_AnswersTheIdentityAndAnEmptyBox()
    {
        const string script =
            "var m = new Matrix2D().fromRotated(this, 9), d = new Matrix2D().fromRotated(this);" +
            "event.value = [[m.a, m.b, m.c, m.d, m.h, m.v].join(','), this.getPageBox('Crop', 9).join(','), [d.a, d.b, d.c, d.d, d.h, d.v].join(',')].join('|');";
        PdfJsExecutionContext ctx = Run(Document(script, 90));

        // Page 0 is the uncropped, unrotated control page: its space is default user space.
        Assert.Equal("1,0,0,1,0,0|0,0,0,0|1,0,0,1,0,0", ctx.EventValue);
    }

    private static PdfJsExecutionContext Run(byte[] pdf)
    {
        var ctx = new PdfJsExecutionContext();
        PdfOperations.ExecuteOpenScripts(new MemoryStream(pdf), ctx);
        Assert.True(ctx.Error == null, ctx.Error?.ToString());
        return ctx;
    }

    /// <summary>
    /// Two letter pages: the first uncropped and unrotated, the second cropped half an inch on every
    /// side and turned by <paramref name="rotate"/>; <paramref name="script"/> as the open action.
    /// </summary>
    private static byte[] Document(string script, int rotate)
    {
        string js = script.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [36 36 576 756] /Rotate " + rotate + " >>",
            "<< /S /JavaScript /JS (" + js + ") >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
