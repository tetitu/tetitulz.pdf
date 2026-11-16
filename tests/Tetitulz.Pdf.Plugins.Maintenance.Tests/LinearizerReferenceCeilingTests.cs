// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Plugins.Maintenance.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The linearizer is held to the reader's ceiling on page-to-object references
/// (<see cref="PdfLimits.MaxLinearizationSharedReferences"/>): every page's hint entry lists the objects it reaches,
/// so pages naming one <c>/Resources</c> of n entries cost, and wrote, pages × n — 800 pages sharing 3 200 small
/// objects took 9.5 s and wrote 2.56 million references, which the reader then refused.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class LinearizerReferenceCeilingTests
{
    /// <summary>Ten pages sharing a <c>/Resources</c> of ten fonts: each page reaches its own object and the ten.</summary>
    private static byte[] SharedResources()
    {
        const int pages = 10, fonts = 10;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
        };
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++) kids.Append(4 + fonts + p).Append(" 0 R ");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] /Resources 3 0 R >>");
        var f = new StringBuilder();
        for (int i = 0; i < fonts; i++) f.Append("/F").Append(i).Append(' ').Append(4 + i).Append(" 0 R ");
        bodies.Add($"<< /Font << {f}>> >>");
        for (int i = 0; i < fonts; i++) bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (int p = 0; p < pages; p++) bodies.Add("<< /Type /Page /Parent 2 0 R >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Under a ceiling of 60 the ten pages, reaching some 110 objects in all, are refused for that reason and
    /// nothing is written; the control, a ceiling of 1 000, linearizes the same document.
    /// </summary>
    [Fact]
    public void PagesReachingMoreObjectsThanTheReaderAccepts_AreRefused_AndAtTheCeilingTheyAreNot()
    {
        try
        {
            PdfLimits.MaxLinearizationSharedReferences = 60;
            using var refused = new MemoryStream();
            var ex = Assert.Throws<PdfException>(() => PdfLinearizer.Linearize(new MemoryStream(SharedResources()), refused));
            Assert.Contains("MaxLinearizationSharedReferences", ex.Message);
            Assert.Equal(0, refused.Length);

            PdfLimits.MaxLinearizationSharedReferences = 1000;
            using var written = new MemoryStream();
            PdfLinearizer.Linearize(new MemoryStream(SharedResources()), written);
            Assert.Contains("/Linearized", Encoding.ASCII.GetString(written.ToArray(), 0, (int)System.Math.Min(written.Length, 1024)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
