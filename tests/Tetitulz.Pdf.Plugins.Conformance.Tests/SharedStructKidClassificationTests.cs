// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// This checker's <c>/K</c> child classification (§14.7.2, Tables 323–325) is
/// <c>Base.Operations.StructKidClassifier</c>'s, shared with the accessibility feature's nine
/// former copies.
/// </summary>
/// <remarks>
/// <para><b>The shared ladder is this checker's own</b> — the classifier was corrected to match
/// <c>ClassifyStructKid</c> rather than the other way round, because this was the copy that had
/// thought hardest about the two Optional <c>/Type</c>s. So these tests are the ones that go red if
/// a future edit moves the shared ladder away from what this file established, which is exactly the
/// coverage the merge had to add: before it, mutating the classifier reddened Base alone.</para>
///
/// <para>Both shapes are legal or near-legal PDF a real producer emits, not adversarial input: a
/// typeless object reference is what Table 325 permits, and a mistyped one is what a producer that
/// stamps <c>/Type /StructElem</c> on everything writes.</para>
/// </remarks>
public class SharedStructKidClassificationTests
{
    /// <summary>
    /// <c>&lt;&lt; /Type /StructElem /Obj 4 0 R &gt;&gt;</c> — Table 323 makes <c>/S</c> Required
    /// on a structure element, so a dictionary with <c>/Obj</c> and no <c>/S</c> is an object
    /// reference whose <c>/Type</c> is wrong. The <c>&lt;Link&gt;</c> holding it therefore reaches
    /// content and must not be counted as unconnected.
    /// </summary>
    /// <remarks>
    /// The assertion is on the <b>ISO 14289-1 §7.18.1 escape hatch</b>, not on UA28/UA29: a
    /// <c>&lt;Link&gt;</c> whose annotation carries no <c>/Contents</c> is described only if an
    /// object-reference kid registers that annotation against the element's <c>/Alt</c>. Classifying
    /// the kid as an <i>Element</i> instead leaves the annotation undescribed — whereas UA28/UA29
    /// stay quiet either way, because an element kid is content-reaching too. <b>That is why the
    /// first draft of this test could not fail:</b> mutating the ladder so <c>/Type /StructElem</c>
    /// outranks <c>/Obj</c> reddened Base and Accessibility and left this suite green.
    /// </remarks>
    [Fact]
    public void AMistypedObjectReferenceStillDescribesItsAnnotation()
    {
        var result = Validate(LinkPdf("<< /Type /StructElem /Pg 3 0 R /Obj 4 0 R >>"));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA29");
    }

    /// <summary>The typeless form, which is the one the spec actually blesses.</summary>
    [Fact]
    public void ATypelessObjectReferenceDescribesItsAnnotation()
    {
        var result = Validate(LinkPdf("<< /Pg 3 0 R /Obj 4 0 R >>"));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA27");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA29");
    }

    /// <summary>
    /// The control that makes the two above mean something: with no object-reference kid at all the
    /// annotation has nothing describing it, and the escape hatch does not apply.
    /// </summary>
    [Fact]
    public void AnAnnotationWithNoObjectReferenceIsUndescribed()
    {
        var result = Validate(LinkPdf("<< /Pg 3 0 R /MCID 0 >>"));

        Assert.Contains(result.Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// A dictionary carrying nothing is not a kid at all, so the element holding it really does
    /// reach no content. Without this the two tests above would pass with the classifier answering
    /// "content" for everything.
    /// </summary>
    [Fact]
    public void ABareDictionaryInKReachesNothing()
    {
        var result = Validate(LinkPdf("<< >>"));

        Assert.Contains(result.Violations, v => v.Code == "UA28" || v.Code == "UA29");
    }

    // ------------------------------------------------------------------

    private static PdfUaValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms);
    }

    /// <summary>
    /// A conformant one-page tagged document whose <c>&lt;Link&gt;</c> element has
    /// <paramref name="kid"/> as its only <c>/K</c> child.
    /// </summary>
    private static byte[] LinkPdf(string kid) => Build(
        " /Info 10 0 R",
        "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /Lang (en-US) "
            + "/ViewerPreferences << /DisplayDocTitle true >> /StructTreeRoot 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] "
            + "/Tabs /S /StructParents 0 /Contents 9 0 R >>",
        "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] >>",
        "<< /Type /StructTreeRoot /K 6 0 R /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /K [7 0 R 11 0 R] >>",
        "<< /Type /StructElem /S /P /P 6 0 R /K << /Pg 3 0 R /MCID 0 >> >>",
        "<< /Nums [0 [7 0 R 11 0 R]] >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Hello) Tj ET\nEMC"),
        "<< /Title (Shared classification) >>",
        "<< /Type /StructElem /S /Link /P 6 0 R /Alt (Link to the annual report) /K "
            + kid + " >>");

    private static string StreamObject(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(string extraTrailerEntries, params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} 0 obj\n{1}\nendobj\n", i + 1, objects[i]);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((objects.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(extraTrailerEntries).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
