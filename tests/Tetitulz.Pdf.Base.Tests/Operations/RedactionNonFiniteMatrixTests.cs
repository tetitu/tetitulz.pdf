// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Text drawn under a non-finite CTM must not survive redaction.
/// </summary>
/// <remarks>
/// <para><b>The leak.</b> A <c>cm</c> operand large enough to overflow a <c>float</c> makes the
/// composed text render matrix contain <c>0 * Infinity</c> = NaN. <c>CornersAabb</c> seeds its
/// extents from <c>float.MaxValue</c>/<c>float.MinValue</c> and every comparison against NaN is
/// false, so without its NaN box it would return those sentinels untouched — an inverted box the
/// intersection test evaluates as <b>no hit</b>. The glyph would be copied to the output verbatim
/// and the operation report success: the redacted PDF would still contain the text.</para>
/// <para><b>The fixture's spelling matters.</b> The obvious
/// input — <c>1.0e400</c>, an operand that <c>double.TryParse</c> turns into an infinity — never
/// reaches the engine at all: PDF numbers have no exponent syntax (§7.3.3), so
/// <c>PdfTokenizer.ReadNumber</c> stops at the <c>e</c> and the content parser sees the number
/// <c>1.0</c> followed by the operator <c>e400</c>. The <c>cm</c> that follows is then left with
/// five operands and ignored, the CTM stays the identity, and the whole test passes for the wrong
/// reason. The overflow has to be spelled in plain digits, which is what
/// <see cref="TheFixtureReallyPutsANonFiniteValueIntoTheCtm"/> pins.</para>
/// <para><b>Why the pair.</b> "The text is gone" passes on its own with an engine that deletes
/// everything, so the control redacts the same rectangle over the same document with a finite
/// <c>cm</c> and requires the text to stay. The two together say what the rule claims: content that
/// can be placed outside the region is kept, content nobody can place is removed.</para>
/// <para>Asserted on the output page's decoded content stream rather than on extracted text,
/// because a text extractor may itself refuse a non-finite matrix — which would make the assertion
/// pass with the leak still in the file.</para>
/// </remarks>
public sealed class RedactionNonFiniteMatrixTests
{
    /// <summary>1e40 in plain digits: finite as a <c>double</c>, +Infinity as a <c>float</c>.</summary>
    private const string OverflowingOperand = "10000000000000000000000000000000000000000.0";

    // Well clear of the text at (72, 700): if the geometry is computable, this misses.
    private static readonly PdfRedactionArea FarCorner = new PdfRedactionArea
    {
        PageIndex = 0,
        X = 400,
        Y = 80,
        Width = 60,
        Height = 40,
    };

    [Fact]
    public void GlyphUnderANonFiniteCtm_IsRemoved_NotWrittenOutVerbatim()
    {
        byte[] redacted = Redact(Build(OverflowingOperand + " 0 0 1 0 0 cm\n"));

        Assert.DoesNotContain("SECRET", DecodedContent(redacted), System.StringComparison.Ordinal);
    }

    [Fact]
    public void GlyphUnderAFiniteCtm_OutsideTheRegion_Survives()
    {
        byte[] redacted = Redact(Build("1 0 0 1 0 0 cm\n"));

        Assert.Contains("SECRET", DecodedContent(redacted), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The premise: the fixture's <c>cm</c> really arrives with six operands, the first of which is
    /// not finite once narrowed to the <c>float</c> the engine's matrices are made of.
    /// </summary>
    [Fact]
    public void TheFixtureReallyPutsANonFiniteValueIntoTheCtm()
    {
        byte[] pdf = Build(OverflowingOperand + " 0 0 1 0 0 cm\n");
        var ops = ContentStreamParser.Parse(RawContent(pdf));

        var cm = Assert.Single(ops.Where(o => o.Operator == PdfOperators.ConcatMatrix));
        Assert.Equal(6, cm.Operands.Length);

        float a = (float)((PdfNumber)cm.Operands[0]).DoubleValue;
        Assert.True(float.IsInfinity(a), $"expected an infinity in the CTM, got {a}");
        Assert.True(float.IsNaN(0f * a), "0 * Infinity must be NaN for this fixture to bite");
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Redact(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output, new[] { FarCorner });
        return output.ToArray();
    }

    private static byte[] RawContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return doc.Pages[0].GetContentStreamData();
    }

    private static string DecodedContent(byte[] pdf) =>
        Encoding.Latin1.GetString(RawContent(pdf));

    private static byte[] Build(string cm)
    {
        string content =
            "q\n" + cm +
            "BT /F1 12 Tf 72 700 Td\n" +
            "(SECRET) Tj\n" +
            "ET\nQ\n";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
