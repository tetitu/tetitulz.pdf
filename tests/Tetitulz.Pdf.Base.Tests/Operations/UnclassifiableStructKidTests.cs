// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A <c>/K</c> entry that is none of §14.7.2's three kinds must be skipped, not invented into one.
/// </summary>
/// <remarks>
/// <para><b>The defect was in the survivor of a consolidation.</b> <c>StructKidClassifier</c> was
/// created to give the tree one answer to "what is this <c>/K</c> child?", and its final line
/// returned <c>Element</c> for a dictionary carrying no <c>/S</c>, no <c>/MCID</c>, no <c>/Obj</c>
/// and no <c>/Type</c> — where every other copy in the tree (<c>StructTreeWalker</c>,
/// <c>PdfUaChecker</c>, the deep checker's <c>ClassifyKid</c>) answers not-an-element. Pointing more
/// call sites at it would have spread that, which is why it is fixed before any further merge.</para>
///
/// <para>Two consumers, two different symptoms. <c>PdfStructureTreeReader</c> reported the phantom
/// as an element of type <b><c>"OBJR"</c></b> — the fallback for "deduced kind is not MCR" — so a
/// caller reading the structure tree saw an object reference the file never wrote. The tagged-text
/// extractor recursed into it, spending a visited slot and a depth level, and reaching whatever an
/// adversarial file hung off its <c>/K</c>.</para>
///
/// <para><b>The second case is the ordering one.</b> <c>&lt;&lt; /Type /StructElem /Obj 12 0 R
/// &gt;&gt;</c> — Table 323 makes <c>/S</c> <i>Required</i> on a structure element, so a dictionary
/// with <c>/Obj</c> and no <c>/S</c> is an object reference whose <c>/Type</c> is wrong. Testing
/// <c>/Type /StructElem</c> first made it an <c>Element</c> here while the Conformance plugin's twin
/// called it an <c>ObjectReference</c>, on the same bytes. The classifier now follows that twin's
/// ladder step for step.</para>
/// </remarks>
public class UnclassifiableStructKidTests
{
    /// <summary>
    /// A bare <c>&lt;&lt; &gt;&gt;</c> in a <c>/K</c> array contributes no child at all — in
    /// particular not one calling itself <c>"OBJR"</c>.
    /// </summary>
    [Fact]
    public void BareDictionaryInK_ContributesNoChild()
    {
        using var ms = new MemoryStream(StructTreePdf("<< >>"));
        var roots = PdfStructureTreeReader.ReadStructureTree(ms);

        var para = Single(roots);
        Assert.Empty(para.Children);
    }

    /// <summary>
    /// The control: a real marked-content reference in the same slot <i>is</i> reported, so the
    /// assertion above is about the bare dictionary and not about the fixture failing to build a
    /// tree at all.
    /// </summary>
    [Fact]
    public void MarkedContentReferenceInK_IsStillReported()
    {
        using var ms = new MemoryStream(StructTreePdf("<< /MCID 0 >>"));

        var para = Single(PdfStructureTreeReader.ReadStructureTree(ms));

        var kid = Assert.Single(para.Children);
        Assert.Equal("MCR", kid.ElementType);
    }

    /// <summary>
    /// <c>&lt;&lt; /Type /StructElem /Obj 12 0 R &gt;&gt;</c> is an object reference whose
    /// <c>/Type</c> is wrong — Table 323 makes <c>/S</c> <i>Required</i> on a structure element —
    /// which is what the Conformance plugin's twin has always said about it.
    /// </summary>
    /// <remarks>
    /// <b>Asserted on the classifier, not through <c>PdfStructureTreeReader</c>, because the reader
    /// cannot see this one.</b> It labels a leaf from the file's own <c>/Type</c> when there is one,
    /// and the dictionary has no <c>/K</c>, so "StructElem with no children" is the answer both
    /// before and after the change. A first version of this test went through the reader and passed
    /// under the mutation that restores the old ladder — the name claimed the ordering rule and the
    /// body asserted something true either way, which is the shape CLAUDE.md warns no guard can
    /// catch. The observable consequences are in the <i>other</i> consumers: the tagged-text
    /// extractor recurses into an Element and not into an ObjectReference, and the plugin's twin
    /// disagreed with this one on the same bytes.
    /// </remarks>
    [Fact]
    public void StructElemTypeWithObjAndNoS_ClassifiesAsAnObjectReference()
    {
        var kid = new PdfDictionary();
        kid[PdfName.Type] = new PdfName("StructElem");
        kid[new PdfName("Obj")] = new PdfReference(12, 0);

        Assert.Equal(StructKidKind.ObjectReference, StructKidClassifier.Classify(kid, null));
    }

    /// <summary>
    /// The companion: with <c>/S</c> present it is an element, whatever else it carries. This is
    /// what stops the rule above from being "anything with /Obj is an object reference", and it is
    /// the branch that keeps a real element out of the leaf path.
    /// </summary>
    [Fact]
    public void StructElemWithS_ClassifiesAsAnElement()
    {
        var kid = new PdfDictionary();
        kid[PdfName.Type] = new PdfName("StructElem");
        kid[PdfName.S] = PdfName.P;   // cached; the guard only scans src/, but the convention is tree-wide
        kid[new PdfName("Obj")] = new PdfReference(12, 0);

        Assert.Equal(StructKidKind.Element, StructKidClassifier.Classify(kid, null));
    }

    // ------------------------------------------------------------------

    private static PdfStructElement Single(
        PdfStructElement root)
    {
        Assert.NotNull(root);
        var para = Flatten(root).FirstOrDefault(e => e.ElementType == "P");
        Assert.NotNull(para);
        return para;
    }

    private static System.Collections.Generic.IEnumerable<PdfStructElement>
        Flatten(PdfStructElement e)
    {
        yield return e;
        if (e.Children == null) yield break;
        foreach (var c in e.Children)
            foreach (var d in Flatten(c))
                yield return d;
    }

    /// <summary>
    /// A one-page tagged document whose single <c>P</c> element has <paramref name="kid"/> as its
    /// only <c>/K</c> entry.
    /// </summary>
    private static byte[] StructTreePdf(string kid) => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
        StreamObj("/P <</MCID 0>> BDC\nEMC\n"),
        "<< /Type /StructTreeRoot /K [6 0 R] >>",
        "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K [" + kid + "] >>");

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
