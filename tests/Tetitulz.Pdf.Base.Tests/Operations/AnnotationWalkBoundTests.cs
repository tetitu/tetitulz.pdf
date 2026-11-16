// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Pages may name one <c>/Annots</c> array, so pages × entries is a product the file chooses on both
/// sides. The image index's appearance walk and the attachment listing's annotation walk are bounded
/// by <see cref="PdfLimits.MaxAnnotationsPerDocument"/> across the document, and the cut is said.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class AnnotationWalkBoundTests
{
    private const int Pages = 100, Annotations = 100;

    [Fact]
    public void AdversarialSharedAnnotsArray_StopsTheAppearanceWalkAtTheCeiling_AndSays()
    {
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 1000;
            using var doc = PdfDocument.Open(new MemoryStream(Pdf()));

            var index = DocumentImageIndex.Build(doc);

            Assert.InRange(index.FormsVisited, 1, 1000);
            Assert.NotNull(index.AnnotationCut);
            Assert.Contains("annotation entries", DocumentEditDiagnostics.LastError ?? "", StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: under the default ceiling every page's every appearance is walked.</summary>
    [Fact]
    public void UnderTheCeiling_EveryAppearanceIsWalked()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf()));

        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(Pages * Annotations, index.FormsVisited);
        Assert.Null(index.AnnotationCut);
    }

    [Fact]
    public void AdversarialSharedAnnotsArray_CutsTheAttachmentListing_AndSays()
    {
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 1000;
            var listed = PdfAttachmentManager.ListAttachments(new MemoryStream(Pdf()));

            Assert.Empty(listed);
            Assert.Contains("annotation entries", PdfAttachmentManager.LastListError ?? "", StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }

        PdfAttachmentManager.ListAttachments(new MemoryStream(Pdf()));
        Assert.Null(PdfAttachmentManager.LastListError);
    }

    /// <summary>
    /// <see cref="Pages"/> pages sharing one <c>/Annots</c> array (object 4) of <see cref="Annotations"/>
    /// Square annotations, each with an appearance stream of its own.
    /// </summary>
    private static byte[] Pdf()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, Pages).Select(p => (5 + p) + " 0 R")) + "] /Count " + Pages + " >>",
            "null",
            "[" + string.Join(" ", Enumerable.Range(0, Annotations).Select(a => (5 + Pages + 2 * a) + " 0 R")) + "]",
        };
        for (int p = 0; p < Pages; p++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots 4 0 R >>");
        for (int a = 0; a < Annotations; a++)
        {
            int ap = 5 + Pages + 2 * a + 1;
            bodies.Add($"<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /AP << /N {ap} 0 R >> >>");
            bodies.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream");
        }

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
