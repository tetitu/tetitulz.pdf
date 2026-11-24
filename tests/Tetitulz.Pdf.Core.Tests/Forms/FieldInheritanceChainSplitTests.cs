// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <b>The two inheritance chains stay split, on purpose</b> — pinned so that a tidy-up which "unifies"
/// them has to fail a test first.
/// </summary>
/// <remarks>
/// <para><b>What the spec settles, and what it cannot.</b> ISO 32000-2 §12.7.4.1 (lines 44110–44117 of
/// <c>docs/iso/iso32000-2.txt</c>) makes an absent inheritable attribute come from the nearest
/// ancestor, through the whole hierarchy — <i>"an interactive PDF processor shall not limit the range
/// of inheritance"</i> — and <see cref="AcroFieldInheritance"/> does exactly that for every caller.
/// What it cannot settle is which chain is <i>the</i> hierarchy when a file contradicts itself: Table
/// 220 defines <c>/Parent</c> as the field whose <c>/Kids</c> includes this one, so on a conforming
/// file the arrival chain and the <c>/Parent</c> key are two directions of one edge, and only a
/// malformed file can separate them.</para>
///
/// <para><b>The decision: keep them split.</b> Core climbs the chain a field was
/// actually <b>descended</b> through; the Conformance walk and both renderers climb the
/// <c>/Parent</c> key. Two reasons. They are different jobs — a checker reporting on the document and
/// a model describing the structure a caller walked — and reconciling them changes verdicts. And
/// there is no measured case for doing it: <c>CorpusFieldParentAgreementTests</c> found <b>zero</b>
/// nodes reached by a <c>/Kids</c> edge across the corpus it was written against, so a reconciliation
/// would change behaviour only on files nobody has shown exist.</para>
///
/// <para>So this is a characterisation of a deliberate difference, and it asserts both halves. The
/// first alone would pass if Core were switched to <c>/Parent</c> and the fixture happened to agree;
/// the second shows the two chains genuinely answer differently on this file, so the first is a claim
/// about which one Core chose.</para>
/// </remarks>
public class FieldInheritanceChainSplitTests
{
    /// <summary>
    /// A kid reached through the <c>/Kids</c> of a <c>/Tx</c> root, whose own <c>/Parent</c> names a
    /// different node that declares <c>/Btn</c>. The arrival chain answers Text; the
    /// <c>/Parent</c> key answers Button.
    /// </summary>
    [Fact]
    public void AMalformedFieldWhoseParentContradictsItsKidsInheritsDifferentlyOnTheTwoChains()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ContradictingTree()));

        var root = Assert.Single(doc.AcroForm.Fields);
        var kid = Assert.Single(root.Kids);

        // Core's caller: the chain it descended — kid 7 was reached from root 6, which is /Tx.
        Assert.Equal(PdfFieldType.Text, kid.FieldType);

        // The step the Conformance walk and both renderers use — the /Parent key — reaches node 8,
        // which is /Btn. AncestorStep's own remarks give this exact form for a /Parent-key chain.
        PdfObject raw = AcroFieldInheritance.Find(
            kid.Dictionary, PdfName.FT,
            d => doc.ResolveObject(d[PdfName.Parent]) as PdfDictionary,
            o => doc.ResolveObject(o),
            o => o is PdfName,
            maxNodes: 51,
            out _);

        Assert.Equal("Btn", ((PdfName)doc.ResolveObject(raw)).Value);
    }

    /// <summary>
    /// The control: on a conforming file the two chains are one edge, so they agree. Without this, the
    /// test above would also pass on a fixture that was simply broken.
    /// </summary>
    [Fact]
    public void OnAConformingTreeTheTwoChainsAgree()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ConformingTree()));

        var kid = Assert.Single(Assert.Single(doc.AcroForm.Fields).Kids);

        PdfObject raw = AcroFieldInheritance.Find(
            kid.Dictionary, PdfName.FT,
            d => doc.ResolveObject(d[PdfName.Parent]) as PdfDictionary,
            o => doc.ResolveObject(o),
            o => o is PdfName,
            maxNodes: 51,
            out _);

        Assert.Equal(PdfFieldType.Text, kid.FieldType);
        Assert.Equal("Tx", ((PdfName)doc.ResolveObject(raw)).Value);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// Root 6 is <c>/Tx</c> and lists 7 in its <c>/Kids</c>; 7's <c>/Parent</c> is 8, a <c>/Btn</c>
    /// node that is not in <c>/Fields</c>. Table 220 forbids this; it is the shape under test.
    /// </summary>
    private static byte[] ContradictingTree() => Build(
        "<< /T (root) /FT /Tx /Kids [7 0 R] >>",
        "<< /T (child) /Parent 8 0 R >>",
        "<< /T (elsewhere) /FT /Btn >>");

    /// <summary>The same tree with 7's <c>/Parent</c> pointing back at 6, as Table 220 requires.</summary>
    private static byte[] ConformingTree() => Build(
        "<< /T (root) /FT /Tx /Kids [7 0 R] >>",
        "<< /T (child) /Parent 6 0 R >>",
        "<< /T (unused) >>");

    /// <summary>A one-page document whose <c>/AcroForm /Fields</c> is object 6; the three field
    /// bodies become objects 6, 7 and 8.</summary>
    private static byte[] Build(string six, string seven, string eight)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< >>",
            six, seven, eight,
        };

        var sb = new StringBuilder();
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
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture))
          .Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
