// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The <c>/K</c> child classification (§14.7.2, Tables 323–325) this feature applies is
/// <c>Base.Operations.StructKidClassifier</c>'s, shared with the Conformance plugin.
/// </summary>
/// <remarks>
/// <para>Nine copies of that ladder were open-coded here, in six files, and none of them was
/// covered by a test that would notice the shared one changing. These two shapes are the ones the
/// copies actually disagreed about, driven through the public audit so a mutation of the shared
/// classifier reddens this suite and not only Base's.</para>
///
/// <para><b>The <c>&lt;&lt; /Type /StructElem /Obj 12 0 R &gt;&gt;</c> case is a real verdict
/// change.</b> Table 323 makes <c>/S</c> Required on a structure element, so a dictionary carrying
/// <c>/Obj</c> and no <c>/S</c> is an object reference whose <c>/Type</c> is wrong. This feature's
/// copies asked "is the resolved <c>/Type</c> <c>OBJR</c>?" first and answered no, so the element
/// holding it was reported as a leaf referencing nothing — while <c>PdfUaChecker</c>, on the same
/// bytes, called it an object reference.</para>
/// </remarks>
public class SharedStructKidClassificationTests
{
    /// <summary>
    /// An object reference whose <c>/Type</c> says <c>StructElem</c> is still an object reference,
    /// so the element holding it references content and is not hollow.
    /// </summary>
    [Fact]
    public void AMistypedObjectReferenceStillCountsAsContent()
    {
        string[] codes = IssueCodes(LinkPdf("<< /Type /StructElem /Obj 8 0 R >>"));

        Assert.DoesNotContain("UA16", codes);   // the <Link> is not a leaf pointing at nothing
        Assert.DoesNotContain("UA30", codes);   // nor is its subtree hollow
    }

    /// <summary>
    /// The control, and the one the spec cares about: a <i>typeless</i> object reference — the only
    /// required entry is <c>/Obj</c> (Table 325) — counts as content too.
    /// </summary>
    [Fact]
    public void ATypelessObjectReferenceCountsAsContent()
    {
        string[] codes = IssueCodes(LinkPdf("<< /Obj 8 0 R >>"));

        Assert.DoesNotContain("UA16", codes);
        Assert.DoesNotContain("UA30", codes);
    }

    /// <summary>
    /// A dictionary carrying nothing is not a kid at all: the element holding it really does
    /// reference no content, and saying so is what separates this from "anything in /K counts".
    /// </summary>
    [Fact]
    public void ABareDictionaryInKIsNotContent()
    {
        string[] codes = IssueCodes(LinkPdf("<< >>"));

        Assert.Contains("UA16", codes);
    }

    // ------------------------------------------------------------------

    private static string[] IssueCodes(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var report = PdfAccessibility.Audit(ms);
        Assert.NotNull(report);
        return report.Issues.Select(i => i.Code).Distinct()
            .OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// A one-page tagged document whose single <c>&lt;Link&gt;</c> element has
    /// <paramref name="kid"/> as its only <c>/K</c> child, and a Link annotation at object 8 for
    /// that child to name.
    /// </summary>
    private static byte[] LinkPdf(string kid) => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> " +
            "/Annots [8 0 R] /StructParents 0 >>",
        StreamObj("/P <</MCID 0>> BDC\n10 10 50 50 re\nf\nEMC\n"),
        "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 7 0 R >>",
        "<< /Type /StructElem /S /Link /P 5 0 R /Pg 3 0 R /K [" + kid + "] >>",
        "<< /Nums [0 [6 0 R]] >>",
        "<< /Type /Annot /Subtype /Link /Rect [10 10 60 60] /Contents (a link) " +
            "/StructParent 1 /Border [0 0 0] >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
