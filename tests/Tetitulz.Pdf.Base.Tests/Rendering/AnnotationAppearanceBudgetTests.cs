// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An annotation's appearance stream is an invocation like a form's, charged to the page's budget by
/// both renderers: a page whose <c>/Annots</c> repeats one entry runs its stream that many times, and
/// nothing else counts the entries. Under a budget of ten, ten of forty appearances are drawn; under
/// the default, all of them.
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class AnnotationAppearanceBudgetTests
{
    private const int Annotations = 40;

    [Fact]
    public void AppearancesPastTheInvocationBudget_AreNotDrawn_InEitherRenderer()
    {
        try
        {
            byte[] pdf = Pdf();
            PdfLimits.MaxFormXObjectInvocationsPerPage = 10;
            int raster = Count(() => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 36));
            int svg = Count(() => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
            Assert.Equal(10, raster);
            Assert.Equal(10, svg);

            PdfLimits.ResetToDefaults();
            Assert.Equal(Annotations, Count(() => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 36)));
            Assert.Equal(Annotations, Count(() => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>How many appearances the run drew: the completion event each renderer reports after one.</summary>
    private static int Count(Action run)
    {
        int drawn = 0;
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == own && kind == RenderDiagnosticKind.AnnotationRendered) drawn++;
        }
        RenderDiagnostics.OnEvent += Sink;
        try { run(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return drawn;
    }

    /// <summary>One page whose <c>/Annots</c> names one Square annotation, with a filled appearance, forty times.</summary>
    private static byte[] Pdf()
    {
        const string ap = "1 0 0 rg 0 0 10 10 re f";
        var refs = new StringBuilder();
        for (int i = 0; i < Annotations; i++) refs.Append("4 0 R ");
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [" + refs + "] >>",
            "<< /Type /Annot /Subtype /Square /Rect [10 10 20 20] /F 4 /AP << /N 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length " + ap.Length + " >>\nstream\n" + ap + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
