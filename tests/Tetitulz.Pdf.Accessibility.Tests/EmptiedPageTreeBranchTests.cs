// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// An emptied <c>/Pages</c> branch is not a page, in the checkers' hand-walks as well as in Core.
/// </summary>
/// <remarks>
/// <para><b>Why these walks exist at all, and why the test is encrypted.</b>
/// <c>PdfDocument.Load</c> returns before setting <c>Catalog</c> when <c>/Encrypt</c> is present, so
/// <c>doc.Pages</c> is empty for such a file. Both checkers therefore walk <c>/Root → /Pages</c> by
/// hand, and PDF/UA-1 expressly permits an encrypted document — so those hand-walks are the only
/// page enumeration that runs, and this is the only way to reach them.</para>
///
/// <para><b>The defect.</b> Both matched <c>kids == null</c> alone — the deep one under a comment
/// saying "a node with no /Kids is a leaf whatever its /Type claims". A node declaring
/// <c>/Type /Pages</c> with no <c>/Kids</c> is not a leaf, it is an emptied branch: what a
/// page-removal tool leaves behind. Counting it as a page shifts every later page index and runs
/// the page-scoped checks against a non-page. They were <i>looser</i> than Core's walk, which at
/// least also required <c>/Contents</c> or a <c>/MediaBox</c>.</para>
///
/// <para><b>The assertion is differential</b> — the same document with and without the emptied
/// branch must audit identically. That is the property, and it cannot be satisfied by a checker
/// that has stopped reporting: the codes are compared, not counted to zero.</para>
/// </remarks>
public class EmptiedPageTreeBranchTests
{
    [Fact]
    public void EmptiedBranch_DoesNotChangeTheAudit()
    {
        string[] withOut = AuditCodes(TaggedPdf(withEmptiedBranch: false));
        string[] with    = AuditCodes(TaggedPdf(withEmptiedBranch: true));

        // The messages are in the failure text because this assertion has already caught one
        // regression that a bare Assert.Equal would have reported only as "two lists differ": the
        // first version of the fix let an emptied branch fall through to the /Kids loop with no
        // array, so BOTH walks threw NullReferenceException and reported UA99 "the check could not
        // be completed" — a checker that silently stops checking.
        Assert.True(withOut.SequenceEqual(with),
            "without: " + string.Join(",", withOut) + "\nwith:    " + string.Join(",", with)
            + "\n---\n" + string.Join("\n", Messages(TaggedPdf(withEmptiedBranch: true))));
    }

    /// <summary>
    /// The floor: the audit really did enumerate the real page and scan its resources — otherwise
    /// "the two are identical" would be two empty lists and the differential above would pass with
    /// the page walk deleted outright.
    /// </summary>
    /// <remarks>
    /// Asserts the specific finding that requires a page enumeration to exist: the real page's
    /// <c>/Resources</c> name a non-embedded Helvetica, and UA21 is the deep checker's
    /// font-embedding code, reached only by walking the pages and reading each one's resources.
    /// <c>Assert.NotEmpty</c> would not do — it passes with the walk deleted, since an encrypted
    /// document reports UA99 regardless, and the repository's own guard rejects it.
    /// </remarks>
    [Fact]
    public void TheAuditActuallyEnumeratedThePageAndScannedItsResources()
    {
        Assert.Contains("UA21", AuditCodes(TaggedPdf(withEmptiedBranch: false)));
    }

    // ------------------------------------------------------------------ helpers

    private static string[] Messages(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAccessibility.Audit(ms).Issues
            .Select(i => i.Code + ": " + (i.Message ?? "")).ToArray();
    }

    private static string[] AuditCodes(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        AccessibilityAuditReport report = PdfAccessibility.Audit(ms);
        return report.Issues.Select(i => i.Code).OrderBy(c => c, System.StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A standard-handler <c>/Encrypt</c> dictionary granting accessibility extraction (bit 10).
    /// Nothing below is actually enciphered; its presence is what makes the parser skip page
    /// loading, which is what routes the checkers through their hand-walks.
    /// </summary>
    private const string EncryptionDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3392 "
        + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
        + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40> >>";

    private const string Content =
        "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n";

    private static byte[] TaggedPdf(bool withEmptiedBranch)
    {
        // The emptied branch sits BEFORE the real page, so a phantom would take index 0 and shift
        // the real page — not merely append.
        string kids = withEmptiedBranch ? "[8 0 R 3 0 R]" : "[3 0 R]";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >>"
            + " /Lang (en-GB) >>",
            "<< /Type /Pages /Kids " + kids + " /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> /StructParents 7 >>",
            "<< /Length " + Content.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + Content + "endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [7 0 R] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0] >>",
        };

        // Object 8 exists either way, so both documents have identical object numbering and the
        // only difference between them is whether /Kids names it.
        // The emptied branch carries /Resources naming a NON-EMBEDDED font. That is the
        // decisive observable: if a walk treats this node as a page, the font checks scan its
        // resources and report the unembedded font; if it is correctly skipped, they do not.
        // Without it the fixture proves nothing — a node with no resources changes no finding
        // either way, which is what the first version of this test discovered about itself.
        bodies.Add("<< /Type /Pages /Parent 2 0 R /Count 0 /MediaBox [0 0 612 792]"
                   + " /Resources << /Font << /F9 9 0 R >> >> >>");
        bodies.Add("<< /Type /Font /Subtype /TrueType /BaseFont /Phantom-NotEmbedded"
                   + " /FirstChar 65 /LastChar 65 /Widths [500] >>");

        bodies.Add(EncryptionDictionary);
        string encryptEntry =
            " /Encrypt " + bodies.Count.ToString(CultureInfo.InvariantCulture) + " 0 R"
            + " /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = bodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(encryptEntry).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
