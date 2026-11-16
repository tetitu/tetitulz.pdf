// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// A damaged cross-reference section that is no longer the newest one: the objects only it named
/// are found by scanning, as they are when it is the file's only section.
/// </summary>
/// <remarks>
/// Found by the another reader corpus scorecard (filled-background.pdf): its one table is interrupted by a
/// comment line, so the parser skips it and recovers the whole file by scanning — three pages. An
/// incremental save appends an intact section; on reopening, that one parses, the damaged one is
/// skipped as an older section, and the objects it alone listed were simply missing: the saved file
/// had two pages.
/// </remarks>
public class SkippedSectionGapFillTests
{
    /// <summary>
    /// The control: with the damaged table as the only section, full recovery finds every page.
    /// </summary>
    [Fact]
    public void DamagedOnlySection_IsRecoveredWhole()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Damaged(withUpdate: false)));

        Assert.Equal(2, doc.Pages.Count);
    }

    /// <summary>
    /// With an intact update appended after it, the damaged section's objects are still found —
    /// the pages it alone named included — and the update's own object resolves from its own
    /// section.
    /// </summary>
    [Fact]
    public void DamagedOlderSection_HasItsObjectsFoundByScanning()
    {
        byte[] pdf = Damaged(withUpdate: true);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        Assert.Equal(2, doc.Pages.Count);
        Assert.Contains("was skipped", PdfParser.LastXrefSectionError ?? "");
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.Equal("updated", (info?[PdfName.Title] as PdfString)?.GetText());
    }

    /// <summary>
    /// Gaps only: an object a newer section deleted (a free entry) stays deleted, though its header
    /// is still in the file.
    /// </summary>
    [Fact]
    public void ObjectANewerSectionFreed_IsNotBroughtBack()
    {
        byte[] pdf = Damaged(withUpdate: true, freeInUpdate: 6);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        CrossReferenceEntry entry = doc.CrossReferenceTable.GetEntry(6);
        Assert.NotNull(entry);
        Assert.Equal(CrossReferenceEntry.EntryType.Free, entry.Type);
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// Objects 1–6: catalogue, page tree, two pages, their shared content, and an unused object.
    /// The table lists 0–2 and then breaks off at a comment line, so objects 3–6 are named by no
    /// readable entry. With <paramref name="withUpdate"/>, object 7 (/Info) is appended in an
    /// intact section whose /Prev is the damaged one.
    /// </summary>
    private static byte[] Damaged(bool withUpdate, int freeInUpdate = 0)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Unused true >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(N(i + 1)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.Append(offsets[0].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append(offsets[1].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("% 1415926535897932384626433832795028841971693993751058209749445923078164062862\n");
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n").Append(N(xref)).Append("\n%%EOF\n");

        if (withUpdate)
        {
            int info = sb.Length;
            sb.Append("7 0 obj\n<< /Title (updated) >>\nendobj\n");
            int update = sb.Length;
            sb.Append("xref\n");
            if (freeInUpdate > 0)
                sb.Append(N(freeInUpdate)).Append(" 1\n0000000000 00001 f \n");
            sb.Append("7 1\n").Append(info.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size 8 /Root 1 0 R /Info 7 0 R /Prev ").Append(N(xref))
              .Append(" >>\nstartxref\n").Append(N(update)).Append("\n%%EOF\n");
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
