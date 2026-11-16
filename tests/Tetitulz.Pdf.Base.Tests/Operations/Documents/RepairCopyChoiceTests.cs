// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Which copy of an object a repair keeps when the file holds more than one.
/// </summary>
/// <remarks>
/// A later revision's copy follows the earlier one, so without better evidence the last copy in the
/// file is current. A cross-reference entry that names a copy's offset is better evidence — the
/// file's own word — and it wins, but only for a copy that carries the number the entry is for.
/// </remarks>
public sealed class RepairCopyChoiceTests
{
    private const string Objects =
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n";

    private static string ProducerAfterRepair(StringBuilder pdf)
    {
        using var output = new MemoryStream();
        PdfRepair.Repair(new MemoryStream(Encoding.Latin1.GetBytes(pdf.ToString()), writable: false), output);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        return doc.Metadata.Producer;
    }

    /// <summary>
    /// Objects 1–3, then <paramref name="info"/> as object 6, then a table whose entry for object 6
    /// holds <paramref name="infoOffset"/> — or, when null, where object 6 really is.
    /// </summary>
    private static StringBuilder WithTable(string info, int? infoOffset)
    {
        var sb = new StringBuilder("%PDF-1.4\n").Append(Objects);
        string text = sb.ToString();
        int infoAt = sb.Length;
        sb.Append(info);

        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (string header in new[] { "1 0 obj", "2 0 obj", "3 0 obj" })
            sb.AppendFormat("{0:D10} 00000 n \n", text.IndexOf(header, StringComparison.Ordinal));
        sb.Append("6 1\n").AppendFormat("{0:D10} 00000 n \n", infoOffset ?? infoAt);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return sb;
    }

    [Fact]
    public void ACopyTheCrossReferenceTableNames_WinsOverALaterStrayCopy()
    {
        // Bytes appended after %%EOF that no table names: not a revision of the document.
        var pdf = WithTable("6 0 obj\n<< /Producer (Current) >>\nendobj\n", infoOffset: null)
            .Append("6 0 obj\n<< /Producer (Stray) >>\nendobj\n");

        Assert.Equal("Current", ProducerAfterRepair(pdf));
    }

    [Fact]
    public void WhenTheTableNamesAnotherObjectsOffset_TheLastCopyInTheFileWins()
    {
        // The entry for object 6 points at object 1's header, as a damaged file's offsets can, so
        // it names no copy of object 6 and position decides.
        int objectOne = "%PDF-1.4\n".Length;
        var pdf = WithTable("6 0 obj\n<< /Producer (Old) >>\nendobj\n", objectOne)
            .Append("6 0 obj\n<< /Producer (New) >>\nendobj\n");

        Assert.Equal("New", ProducerAfterRepair(pdf));
    }
}
