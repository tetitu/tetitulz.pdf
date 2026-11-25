// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The duplicate merge treats a dictionary's entries as the set ISO 32000-1 §7.3.7 says they are:
/// two dictionaries holding the same entries in a different key order are one object.
/// </summary>
/// <remarks>
/// <para>The merge trusts its content hash alone — nothing compares the two objects afterwards —
/// so the hash's canonical form is where key order is decided: hashing the serialisation in stored
/// order, two producers' copies of one font dictionary would never merge.</para>
/// </remarks>
public sealed class KeyOrderDeduplicationTests
{
    [Fact]
    public void Dedup_MergesTwoDictionariesHoldingTheSameEntriesInDifferentKeyOrders()
    {
        byte[] pdf = BuildWithTwoFonts(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /BaseFont /Helvetica /Subtype /Type1 /Type /Font >>");

        var result = Optimize(pdf, out _);

        Assert.Equal(1, result.DuplicatesRemoved);
    }

    /// <summary>The control: the same key orders, one value different — not a duplicate.</summary>
    [Fact]
    public void Dedup_KeepsTwoDictionariesInDifferentKeyOrdersApartWhenOneValueDiffers()
    {
        byte[] pdf = BuildWithTwoFonts(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /BaseFont /Courier /Subtype /Type1 /Type /Font >>");

        var result = Optimize(pdf, out _);

        Assert.Equal(0, result.DuplicatesRemoved);
    }

    /// <summary>
    /// Two optional-content groups whose entries agree once their <c>/Usage</c> children merge —
    /// and whose keys are stored in different orders — stay two groups: <c>/D /ON</c> names one
    /// and <c>/D /OFF</c> the other, so merging them would hide the shown group's content.
    /// </summary>
    /// <remarks>
    /// The potency half is in the same file: the two <c>/Usage</c> dictionaries, also stored in
    /// different key orders, do merge — so a key-order-insensitive hash ran over this document and
    /// the exclusion is what kept the groups apart.
    /// </remarks>
    [Fact]
    public void Dedup_NeverMergesTwoOptionalContentGroupsStoredInDifferentKeyOrders()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] " +
                "/D << /ON [5 0 R] /OFF [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                "/Resources << /Properties << /OC0 5 0 R /OC1 6 0 R >> >> >>",
            Stream("/OC /OC0 BDC 0 0 10 10 re f EMC /OC /OC1 BDC 20 20 10 10 re f EMC"),
            "<< /Type /OCG /Name (One) /Usage 7 0 R >>",
            "<< /Usage 8 0 R /Name (One) /Type /OCG >>",
            "<< /Print << /PrintState /ON >> /View << /ViewState /ON >> >>",
            "<< /View << /ViewState /ON >> /Print << /PrintState /ON >> >>");

        var result = Optimize(pdf, out byte[] optimized);

        // Potency: the /Usage pair merged across its key orders.
        Assert.Equal(1, result.DuplicatesRemoved);

        using var document = PdfDocument.Open(new MemoryStream(optimized));
        var catalog = (PdfDictionary)Deref(document, document.Trailer[PdfName.Root]);
        var properties = (PdfDictionary)Deref(document, catalog[PdfName.OCProperties]);
        var groups = (PdfArray)Deref(document, properties[PdfName.OCGs]);
        Assert.Equal(2, groups.Count);
        Assert.NotEqual(((PdfReference)groups[0]).ReferenceObjectNumber,
            ((PdfReference)groups[1]).ReferenceObjectNumber);
    }

    private static PdfObject Deref(PdfDocument document, PdfObject o)
    {
        int guard = 0;
        while (o is PdfReference r && guard++ < 32) o = document.GetObject(r.ReferenceObjectNumber);
        return o;
    }

    private static OptimizationResult Optimize(byte[] input, out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, new OptimizationOptions
        {
            RemoveDuplicates = true,
            RemoveUnusedObjects = true,
            CompressStreams = false,
        });
        optimized = o.ToArray();
        return result;
    }

    private static byte[] BuildWithTwoFonts(string first, string second) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>",
        Stream("BT /F1 12 Tf 10 10 Td (A) Tj /F2 12 Tf (B) Tj ET"),
        first,
        second);

    private static string Stream(string content) =>
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";

    /// <summary>Objects numbered from 1 in order, with a classic cross-reference table.</summary>
    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
