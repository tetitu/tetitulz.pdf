// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An embedded file's <c>/Params /CreationDate</c>, as <c>PdfPortfolioManager.ListItems</c> reports
/// it.
///
/// <para><b>This reader had its own §7.9.4 parser and it read eight characters</b> — year, month,
/// day — then returned midnight, dropping the time of day and the time zone without a word. The
/// complete parser sat in <c>PdfMetadata</c> the whole time; both now call
/// <c>Core.Util.PdfDate.TryParse</c>.</para>
///
/// <para>The assertion is the <b>instant</b>, not the date. A truncating parser returns the right
/// day with the wrong time, which looks correct in every listing and is why it survived: nothing in
/// the tree exercised <c>ListItems</c> at all.</para>
/// </summary>
public sealed class PdfPortfolioDateTests
{
    /// <summary>
    /// <c>D:20260819143005+03'00'</c> is 11:30:05 UTC. The old reader answered 2026-08-19 00:00.
    /// </summary>
    [Fact]
    public void EmbeddedFileCreationDate_KeepsTheTimeOfDayAndTheOffset()
    {
        using var ms = new MemoryStream(PortfolioPdf("D:20260819143005+03'00'"));
        var items = PdfPortfolioManager.ListItems(ms);

        var item = Assert.Single(items);
        Assert.Equal("report.txt", item.FileName);

        DateTime created = Assert.NotNull(item.CreationDate);
        Assert.Equal(new DateTime(2026, 8, 19, 11, 30, 5, DateTimeKind.Utc), created);
    }

    /// <summary>
    /// The same wall-clock reading under a different offset is a different instant, which is the
    /// half a date-only parser cannot express at all.
    /// </summary>
    [Fact]
    public void TheOffsetChangesTheReportedInstant()
    {
        using var east = new MemoryStream(PortfolioPdf("D:20260819143005+03'00'"));
        using var west = new MemoryStream(PortfolioPdf("D:20260819143005-05'00'"));

        DateTime a = Assert.NotNull(Assert.Single(PdfPortfolioManager.ListItems(east)).CreationDate);
        DateTime b = Assert.NotNull(Assert.Single(PdfPortfolioManager.ListItems(west)).CreationDate);

        Assert.Equal(TimeSpan.FromHours(8), b - a);
    }

    /// <summary>A date with no time at all still reads as that date at midnight UTC.</summary>
    [Fact]
    public void DateOnlyValue_StillReadsAsThatDate()
    {
        using var ms = new MemoryStream(PortfolioPdf("D:20260819"));
        DateTime created = Assert.NotNull(Assert.Single(PdfPortfolioManager.ListItems(ms)).CreationDate);

        Assert.Equal(new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc), created);
    }

    /// <summary>A malformed date leaves the field empty rather than failing the whole listing.</summary>
    [Fact]
    public void MalformedDate_LeavesTheFieldNullAndStillListsTheFile()
    {
        using var ms = new MemoryStream(PortfolioPdf("D:20261332999999"));
        var item = Assert.Single(PdfPortfolioManager.ListItems(ms));

        Assert.Null(item.CreationDate);
        Assert.Equal("report.txt", item.FileName);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A one-file portfolio: catalog → <c>/Names /EmbeddedFiles</c> → a file-spec whose <c>/EF /F</c>
    /// stream carries <c>/Params /CreationDate</c>.
    /// </summary>
    private static byte[] PortfolioPdf(string creationDate)
    {
        const string payload = "hello";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 5 0 R >>" +
            " /Collection << /View /D >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            Stream("", ""),
            "<< /Names [(report.txt) 6 0 R] >>",
            "<< /Type /Filespec /F (report.txt) /Desc (A report) /EF << /F 7 0 R >> >>",
            Stream($"/Type /EmbeddedFile /Params << /Size {payload.Length}" +
                   $" /CreationDate ({creationDate}) >>", payload));
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
