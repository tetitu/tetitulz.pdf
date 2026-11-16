// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The boundaries of UA24's reporting and of the occurrence helpers it shares with the other
/// checks, which the 2026-10-10 overnight sweep found unpinned: the single-page detail, the
/// individual-to-aggregate threshold, what an aggregate names and where it points, and the join
/// cap of the coverage notice.
///
/// <para>Image-only pages are built in memory: <c>n</c> pages, each drawing one shared image and
/// no text. Page <c>i</c> (0-based) is object <c>3 + 2i</c>, so the first page's object number is 3.</para>
/// </summary>
public class ImageOnlyPagesBoundaryTests
{
    // IndividualReportLimit (8) and ObjectNumbersInMessage (5) in PdfDeepAccessibilityChecker.
    private const int IndividualReportLimit = 8;

    [Fact]
    public void OneImagePageInvokingTwoImages_NamesTheCount()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(ImageOnlyPdf(1, "/Im0 Do\n/Im0 Do\n"));

        var issue = Assert.Single(Ua24(issues));
        Assert.Contains("2 image XObject(s)", issue.Message, StringComparison.Ordinal);
        Assert.Equal(1, issue.PageNumber);
    }

    [Fact]
    public void EightImageOnlyPages_EachGetsItsOwnIssue()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(ImageOnlyPdf(IndividualReportLimit));

        var found = Ua24(issues);
        Assert.Equal(IndividualReportLimit, found.Count);
        Assert.All(found, i => Assert.Null(i.InstanceCount));
        Assert.Equal(Enumerable.Range(1, IndividualReportLimit), found.Select(i => i.PageNumber!.Value).OrderBy(p => p));
    }

    [Fact]
    public void NineImageOnlyPages_CollapseIntoOneAggregateIssueThatPointsAtTheFirst()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(ImageOnlyPdf(IndividualReportLimit + 1));

        var issue = Assert.Single(Ua24(issues));
        Assert.Equal(IndividualReportLimit + 1, issue.InstanceCount);
        Assert.Equal(1, issue.PageNumber);
        Assert.Equal(3, issue.ObjectNumber);
        Assert.Contains("9 page(s) draw images", issue.Message, StringComparison.Ordinal);

        // The first five object numbers, in document order, then the ellipsis that says there are more.
        Assert.Contains("(e.g. obj 3, obj 5, obj 7, obj 9, obj 11, …)", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The join cap of the coverage notice: ten values are all named, and the eleventh is the first
    /// to be elided. Reached by reflection because the helper is private; it is pure, and no
    /// document of a practical size produces exactly this list through the public surface.
    /// </summary>
    [Fact]
    public void JoinCapped_NamesTenValuesWithoutAnEllipsis_AndElidesTheEleventh()
    {
        var join = typeof(PdfDeepAccessibilityChecker).GetMethod("JoinCapped",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(IReadOnlyList<string>) }, null);
        Assert.NotNull(join);

        string[] ten = Enumerable.Range(0, 10).Select(i => ((char)('a' + i)).ToString()).ToArray();
        Assert.Equal("a, b, c, d, e, f, g, h, i, j", join!.Invoke(null, new object[] { ten }));

        string[] eleven = Enumerable.Range(0, 11).Select(i => ((char)('a' + i)).ToString()).ToArray();
        Assert.Equal("a, b, c, d, e, f, g, h, i, j, … (1 more)", join.Invoke(null, new object[] { eleven }));
    }

    /// <summary>
    /// A figure written inline in the structure tree has no object number, so the issue it raises
    /// must carry none either. Read as "not an object" (<c>&gt; 0</c>), not "object 0".
    /// </summary>
    [Fact]
    public void AnInlineFigureWithNoAlternateText_IssuesWithNoObjectNumber()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(StructuredPdf(
            "<< /Type /StructTreeRoot /K [<< /Type /StructElem /S /Figure /Alt () >>] >>"));

        var issue = Assert.Single(issues, i => string.Equals(i.Code, "UA15", StringComparison.Ordinal));
        Assert.Null(issue.ObjectNumber);
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static List<AccessibilityIssue> Ua24(IReadOnlyList<AccessibilityIssue> issues)
        => issues.Where(i => string.Equals(i.Code, "UA24", StringComparison.Ordinal)).ToList();

    /// <summary><paramref name="pages"/> pages, each invoking the shared image and drawing no text.</summary>
    private static byte[] ImageOnlyPdf(int pages, string content = "/Im0 Do\n")
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages)
                .Select(i => (3 + 2 * i).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] /Count "
                + pages.ToString(CultureInfo.InvariantCulture) + " >>",
        };

        int imageObject = 3 + 2 * pages;
        string image = imageObject.ToString(CultureInfo.InvariantCulture) + " 0 R";
        for (int i = 0; i < pages; i++)
        {
            int contents = 4 + 2 * i;
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                       + "/Resources << /XObject << /Im0 " + image + " >> >> /Contents "
                       + contents.ToString(CultureInfo.InvariantCulture) + " 0 R >>");
            bodies.Add(StreamObject(content));
        }

        bodies.Add(StreamObjectWith("/Type /XObject /Subtype /Image /Width 1 /Height 1 "
                                    + "/ColorSpace /DeviceGray /BitsPerComponent 8", "A"));
        return BuildPdf(bodies);
    }

    /// <summary>One page with an empty content stream and the given structure tree root.</summary>
    private static byte[] StructuredPdf(string structTreeRoot) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /StructParents 0 >>",
        structTreeRoot,
        StreamObject(""),
    });

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private static string StreamObjectWith(string entries, string content)
        => "<< " + entries + " /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(objectBodies.Count);
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objectBodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objectBodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
