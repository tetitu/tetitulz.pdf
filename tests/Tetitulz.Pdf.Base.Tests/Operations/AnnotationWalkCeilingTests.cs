// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Pages may name one <c>/Annots</c> array, so the entries a document's pages name are bounded
/// together, inside the one annotation walk. The walk skipped an entry with no subtype before any
/// caller counted it, and the flattener and the form reader walked pages × entries with no bound.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class AnnotationWalkCeilingTests
{
    [Fact]
    public void AdversarialPagesSharingOneArrayOfSubtypelessEntries_StopAtTheCeiling()
    {
        byte[] pdf = SharedAnnots(pages: 100, entries: 100, annotation: "<< /Rect [0 0 10 10] >>");
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 1_000;
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var report = new AnnotationWalkReport();

            int examined = AnnotationSubtypeWalk.Entries(doc, report).Count();
            Assert.Empty(AnnotationSubtypeWalk.All(doc));

            Assert.Equal(1_000, examined);                 // was 10 000
            Assert.Equal(10, report.CutAtPage);   // pages 0-9 hold the first thousand
            Assert.Contains("more than 1", report.Message, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The flattener goes through the walk: past the ceiling it stops and says so.</summary>
    [Fact]
    public void AdversarialPagesSharingOneArrayOfWidgets_FlattenToTheCeilingAndSaySo()
    {
        byte[] pdf = SharedAnnots(pages: 100, entries: 100,
            annotation: "<< /Type /Annot /Subtype /Widget /FT /Tx /T (f) /Rect [0 0 10 10] >>");
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 1_000;
            string reported = null;
            void Capture(string m) { if (m.Contains("annotation entries", StringComparison.Ordinal)) reported = m; }
            DocumentEditDiagnostics.OnEvent += Capture;
            try
            {
                using var output = new MemoryStream();
                PdfFormFlattener.Flatten(new MemoryStream(pdf), output);
            }
            finally { DocumentEditDiagnostics.OnEvent -= Capture; }

            Assert.NotNull(reported);
            Assert.Contains("not flattened", reported, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: under the ceiling every entry is walked and nothing is cut.</summary>
    [Fact]
    public void EntriesUnderTheCeiling_AreAllWalked()
    {
        byte[] pdf = SharedAnnots(pages: 10, entries: 10, annotation: "<< /Subtype /Text /Rect [0 0 10 10] >>");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var report = new AnnotationWalkReport();

        Assert.Equal(100, AnnotationSubtypeWalk.All(doc, report).Count());
        Assert.Null(report.CutAtPage);
    }

    /// <summary><paramref name="pages"/> pages, each naming one shared array of <paramref name="entries"/> references to one annotation.</summary>
    private static byte[] SharedAnnots(int pages, int entries, string annotation)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(i => (5 + i) + " 0 R")) + "] /Count " + pages + " >>",
            "[" + string.Join(" ", Enumerable.Repeat("4 0 R", entries)) + "]",
            annotation,
        };
        for (int i = 0; i < pages; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots 3 0 R >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
