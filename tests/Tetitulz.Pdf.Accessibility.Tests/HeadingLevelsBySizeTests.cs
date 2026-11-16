// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="AutoTagOptions.HeadingLevelsBySize"/>: headings levelled by the rank of their size, so
/// a document with more than three heading sizes gets H4–H6 and skips no level. The fixed ratios put
/// everything between 1.15 and 1.4 × the median at H3, and nothing below H3 at all (GAPS.md §5c:
/// "Not inferred: … H4–H6").
/// </summary>
public class HeadingLevelsBySizeTests
{
    /// <summary>Seven heading sizes over a 10-point body: 24, 20, 17, 14, 13, 12 and 11.5.</summary>
    private static readonly float[] HeadingSizes = { 24f, 20f, 17f, 14f, 13f, 12f, 11.5f };

    [Fact]
    public void RankedLevels_GiveEachSizeItsOwnLevel_DownToH6()
    {
        AutoTagResult ranked = Tag(new AutoTagOptions { HeadingLevelsBySize = true });

        Assert.Equal(1, ranked.HeadingsByLevel[1]);
        Assert.Equal(1, ranked.HeadingsByLevel[2]);
        Assert.Equal(1, ranked.HeadingsByLevel[3]);
        Assert.Equal(1, ranked.HeadingsByLevel[4]);
        Assert.Equal(1, ranked.HeadingsByLevel[5]);
        Assert.Equal(2, ranked.HeadingsByLevel[6]);   // 12 and 11.5 share the last level
    }

    /// <summary>The control: the same page under the fixed ratios has H1–H3 only.</summary>
    [Fact]
    public void TheFixedRatios_StopAtH3()
    {
        AutoTagResult fixedRatios = Tag(new AutoTagOptions());

        Assert.Equal(2, fixedRatios.HeadingsByLevel[1]);   // 24 and 20: at least 1.8 × 10
        Assert.Equal(2, fixedRatios.HeadingsByLevel[2]);   // 17 and 14: at least 1.4 ×
        Assert.Equal(3, fixedRatios.HeadingsByLevel[3]);   // 13, 12 and 11.5: at least 1.15 ×
        Assert.False(fixedRatios.HeadingsByLevel.ContainsKey(4));
    }

    /// <summary>
    /// A running header in a larger face than any heading is an artifact, not H1: the ranking counts
    /// only lines the classifier can make headings, so the largest heading in the body is still H1.
    /// </summary>
    [Fact]
    public void ARunningHeaderInALargeFace_DoesNotTakeTheFirstLevel()
    {
        AutoTagResult ranked = Tag(new AutoTagOptions { HeadingLevelsBySize = true }, runningHeader: true);

        Assert.Equal(1, ranked.HeadingsByLevel[1]);
        Assert.Equal(2, ranked.HeadingsByLevel[6]);
        Assert.True(ranked.ArtifactCount >= 1, "the running header was not tagged as an artifact");
    }

    /// <summary>
    /// The ranking: sizes rounded to half a point (13.98 and 14.02 are one size), largest first, only
    /// those at or above the H3 ratio × the median.
    /// </summary>
    [Fact]
    public void TheRanking_RoundsToHalfAPoint_AndKeepsOnlyHeadingSizes()
    {
        var sizes = new List<float> { 10f, 10f, 10.2f, 13.98f, 14.02f, 11.4f, 11.6f, 20f };
        float[] ranked = PdfAutoTagger.RankHeadingSizes(sizes, median: 10f, h3Ratio: 1.15f);
        Assert.Equal(new[] { 20f, 14f, 11.5f }, ranked);
    }

    private static AutoTagResult Tag(AutoTagOptions options, bool runningHeader = false)
    {
        var content = new StringBuilder();
        if (runningHeader) content.Append("BT /F1 30 Tf 72 772 Td (Running header) Tj ET\n");
        // Inside the page body: the header and footer bands are tagged as artifacts, not headings.
        float y = 690f;
        foreach (float size in HeadingSizes)
        {
            content.AppendFormat(CultureInfo.InvariantCulture, "BT /F1 {0} Tf 72 {1} Td (Heading at {0}) Tj ET\n", size, y);
            y -= size + 14f;
            for (int line = 0; line < 3; line++)
            {
                content.AppendFormat(CultureInfo.InvariantCulture, "BT /F1 10 Tf 72 {0} Td (Body text line {1} under the heading.) Tj ET\n", y, line);
                y -= 12f;
            }
            y -= 16f;
        }

        using var input = new MemoryStream(SinglePagePdf(content.ToString()));
        using var output = new MemoryStream();
        return PdfAccessibility.AutoTag(input, output, options);
    }

    private static byte[] SinglePagePdf(string contentStream)
    {
        var sb = new StringBuilder();
        var offsets = new int[6];
        sb.Append("%PDF-1.7\n");
        offsets[1] = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        offsets[4] = sb.Length; sb.Append("4 0 obj\n<< /Length ").Append(contentStream.Length.ToString(CultureInfo.InvariantCulture)).Append(" >>\nstream\n").Append(contentStream).Append("endstream\nendobj\n");
        offsets[5] = sb.Length; sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[n]);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n").AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
