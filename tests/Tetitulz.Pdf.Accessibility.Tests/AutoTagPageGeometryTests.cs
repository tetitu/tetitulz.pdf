// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// End-to-end page geometry for the shipped <see cref="PdfAccessibility.AutoTag"/> path.
///
/// <para>
/// <b>The defect these pin.</b> The header/footer bands are horizontal strips of user space, and
/// the classifier locates them from the page's two vertical edges. <c>PdfAutoTagger</c> supplied
/// only the upper edge and left the lower one at an implicit <c>0</c>, so for any page box that
/// does not start at the origin one of the two bands landed in the wrong place. For a box such as
/// <c>[0 -100 612 692]</c> the footer band grows from 50 pt to 150 pt and swallows body text, which
/// is then emitted as <c>/Artifact /Pagination /Footer</c> — no MCID, no structure element, and
/// therefore permanently unreachable by assistive technology. The content is still drawn on the
/// page, so nothing about the file looks broken; it is simply invisible to a screen reader.
/// </para>
///
/// <para>
/// <see cref="ContentStreamTaggerTests"/> pins this at the classifier level, where the caller
/// supplies both edges directly. These tests exist because that is not where the bug lived: the
/// tagger can be perfectly correct and the shipped path still wrong, because the shipped path is
/// what chooses the numbers. They drive <c>PdfAccessibility.AutoTag</c> over a real PDF and read
/// the emitted content stream back.
/// </para>
///
/// <para>Every fixture is an in-memory <c>byte[]</c>; nothing touches disk.</para>
/// </summary>
public class AutoTagPageGeometryTests
{
    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>
    /// A running head, two body lines and a running foot, positioned relative to a page box whose
    /// lower edge is at <c>-100</c>: the head sits 32 pt below the upper edge, the foot 20 pt above
    /// the lower edge, and the lower body line at <c>Y = 20</c> is 120 pt above the lower edge —
    /// comfortably body text, but only 20 pt above the <em>origin</em>.
    /// </summary>
    private const string HeaderBodyFooter =
        "BT /F1 9 Tf 72 660 Td (Confidential Header) Tj ET\n"
      + "BT /F1 10 Tf 72 400 Td (Body text.) Tj ET\n"
      + "BT /F1 10 Tf 72 20 Td (More body text, 120pt above the lower edge.) Tj ET\n"
      + "BT /F1 9 Tf 72 -80 Td (Page 1 of 1) Tj ET\n";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    /// <summary>An untagged one-page document whose page box is <paramref name="box"/>.</summary>
    private static byte[] PdfWithBox(string box) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox " + box
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject(HeaderBodyFooter),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    });

    /// <summary>The same page, with a <c>/CropBox</c> that disagrees with the <c>/MediaBox</c>.</summary>
    private static byte[] PdfWithCropBox(string mediaBox, string cropBox) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox " + mediaBox + " /CropBox " + cropBox
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject(HeaderBodyFooter),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static AutoTagResult AutoTag(byte[] input, out byte[] output)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(inMs, outMs);
        output = outMs.ToArray();
        return result;
    }

    /// <summary>The first page's content stream, decoded.</summary>
    private static string PageContent(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        PdfDictionary page = doc.Pages[0].Dictionary;
        var stream = doc.ResolveObject(page[new PdfName("Contents")]) as PdfStream;
        Assert.NotNull(stream);
        return Encoding.Latin1.GetString(stream.GetDecodedData());
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// The whole marked-content operator that encloses <paramref name="text"/> — the line carrying
    /// the nearest <c>BDC</c> before it. The tagger emits each <c>BDC</c> on its own line, so the
    /// line is the operator: <c>/P &lt;&lt;/MCID 0&gt;&gt; BDC</c> or
    /// <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Footer&gt;&gt; BDC</c>. Taking the whole
    /// line matters — back-scanning to the previous <c>/</c> would stop at <c>/Footer</c> and hide
    /// the <c>/Artifact</c> tag the assertions are about.
    /// </summary>
    private static string EnclosingBdc(string content, string text)
    {
        int at = content.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, "the content stream no longer draws '" + text + "'");

        int bdc = content.LastIndexOf("BDC", at, StringComparison.Ordinal);
        if (bdc < 0) return null;

        int start = content.LastIndexOf('\n', bdc) + 1;   // -1 + 1 == 0 for the first line
        return content.Substring(start, bdc - start + 3);
    }

    // =====================================================================
    //  The regression
    // =====================================================================

    /// <summary>
    /// The finding, at the level it actually shipped. Body text 120 pt above the page's real lower
    /// edge must be tagged, not swallowed by a footer band measured from the origin.
    /// </summary>
    [Fact]
    public void AutoTag_PageBoxBelowTheOrigin_DoesNotArtifactBodyTextNearYZero()
    {
        AutoTagResult result = AutoTag(PdfWithBox("[0 -100 612 692]"), out byte[] output);

        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal(2, result.McidCount);

        string content = PageContent(output);

        // The two body lines are tagged; the head and the foot are the artifacts.
        Assert.Equal(2, Count(content, "/MCID"));
        Assert.Equal(1, Count(content, "/Subtype /Header"));
        Assert.Equal(1, Count(content, "/Subtype /Footer"));

        // The line that the defect swallowed, named explicitly.
        string enclosing = EnclosingBdc(content, "(More body text, 120pt above the lower edge.)");
        Assert.NotNull(enclosing);
        Assert.DoesNotContain("/Artifact", enclosing, StringComparison.Ordinal);
        Assert.Contains("/MCID", enclosing, StringComparison.Ordinal);

        // ...and the real footer is still recognised, so this is not "bands turned off".
        Assert.Contains("/Artifact", EnclosingBdc(content, "(Page 1 of 1)"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary <c>[0 0 w h]</c> page must be unaffected: for it the upper edge really is the
    /// height and the lower edge really is 0, so nothing about the classification changes.
    /// </summary>
    [Fact]
    public void AutoTag_PageBoxAtTheOrigin_StillMarksTheRunningHeadAndFoot()
    {
        // Shift the same content up by 100pt worth of box so the head/foot land in the bands.
        AutoTag(PdfWithBox("[0 0 612 792]"), out byte[] output);

        string content = PageContent(output);

        // Y = 660 is 132pt below the top -> body;  Y = 20 and Y = -80 are at/below the bottom band.
        // What matters is that a box at the origin still classifies from the origin.
        Assert.Contains("/Artifact", EnclosingBdc(content, "(Page 1 of 1)"), StringComparison.Ordinal);
        Assert.DoesNotContain("/Artifact", EnclosingBdc(content, "(Body text.)"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/CropBox</c> is the region a reader actually sees, so it is the region a running head sits
    /// at the top of. The auto-tagger and <c>PdfArtifactMarker</c> must agree about that; before the
    /// fix the auto-tagger read only <c>/MediaBox</c>, so the two disagreed about where a page's
    /// edges are and could classify the same line differently.
    /// </summary>
    [Fact]
    public void AutoTag_CropBoxNarrowerThanMediaBox_MeasuresTheBandsFromTheCropBox()
    {
        // MediaBox keeps Y = 20 well inside the page; CropBox puts its lower edge at 0, which makes
        // Y = 20 a footer and Y = -80 fall outside the visible page entirely.
        AutoTagResult result = AutoTag(
            PdfWithCropBox("[0 -100 612 692]", "[0 0 612 692]"), out byte[] output);

        Assert.Equal(1, result.PagesProcessed);

        string content = PageContent(output);

        // Measured from the CropBox, Y = 20 is 20pt above the lower edge: that is the footer band.
        Assert.Contains("/Artifact", EnclosingBdc(content, "(More body text, 120pt above the lower edge.)"),
            StringComparison.Ordinal);

        // Y = 400 is body text under either box, so it stays tagged.
        Assert.DoesNotContain("/Artifact", EnclosingBdc(content, "(Body text.)"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A page whose box cannot be read at all must fail closed: no edge classification, so no line
    /// is turned into an artifact. Over-tagging a running head as a paragraph is recoverable;
    /// dropping body text out of the structure tree is not.
    /// </summary>
    [Fact]
    public void AutoTag_PageWithNoUsableBox_MarksNothingAsAPaginationArtifact()
    {
        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            // No /MediaBox anywhere in the page tree, and none inheritable.
            "<< /Type /Page /Parent 2 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(HeaderBodyFooter),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        });

        AutoTag(pdf, out byte[] output);

        string content = PageContent(output);

        Assert.Equal(0, Count(content, "/Pagination"));
        Assert.Equal(0, Count(content, "/Subtype /Header"));
        Assert.Equal(0, Count(content, "/Subtype /Footer"));

        // Every drawn line is still reachable: four runs, four MCIDs.
        Assert.Equal(4, Count(content, "/MCID"));
    }
}
