// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Templating rewrites a page's content stream, so every name it re-emits must be escaped the way
/// the file spelled it.
/// </summary>
/// <remarks>
/// <para><c>PdfTokenizer.ReadName</c> <b>decodes</b> <c>#XX</c> into a name's value, so emitting
/// <c>"/" + n.Value</c> would turn a page containing <c>/Name#20With#20Space BDC</c> — or an
/// ordinary resource name such as <c>/T1#2D0</c> — into <c>/Name With Space</c>: <b>three tokens
/// where the file had one</b>, on every templated page.</para>
///
/// <para><b>The other templating tests do not reach this call site</b>, so without this one a
/// mutation of it survives. A call site a mutation cannot reach is indistinguishable from one
/// that was deleted.</para>
/// </remarks>
public class TemplateNameEscapingTests
{
    /// <summary>
    /// <b>The defect.</b> The marked-content tag must come back as the <i>same single name</i> it
    /// went in as.
    /// </summary>
    /// <remarks>
    /// Asserted on the <c>BDC</c> operand rather than on the whole operator signature: the engine
    /// legitimately rewrites the text-showing operator (<c>Tj</c> becomes <c>TJ</c>) when it
    /// substitutes, so a whole-stream comparison fails for a reason that has nothing to do with
    /// names. The first version of this theory did exactly that.
    /// </remarks>
    [Theory]
    [InlineData("Name#20With#20Space", "Name With Space")]
    [InlineData("T1#2D0", "T1-0")]
    [InlineData("A#23B", "A#B")]
    [InlineData("Weird#28Paren#29", "Weird(Paren)")]
    public void AnEscapedNameSurvivesTemplating(string escapedName, string decoded)
    {
        var ops = Operations(Template(Pdf(escapedName)));

        ContentStreamOperation bdc = null;
        foreach (var op in ops) if (op.Operator == "BDC") bdc = op;

        Assert.True(bdc != null, "the marked-content section did not survive at all");
        Assert.Equal(2, bdc.Operands.Length);

        var tag = bdc.Operands[0] as PdfName;
        Assert.True(tag != null, $"the tag is a {bdc.Operands[0]?.GetType().Name}, not a name — "
                               + "it was split into separate tokens");
        Assert.Equal(decoded, tag.Value);
    }

    /// <summary>
    /// And specifically: the marked-content operator still has exactly its two operands. With the
    /// name split into three tokens, <c>BDC</c> saw four.
    /// </summary>
    [Fact]
    public void TheMarkedContentOperatorKeepsItsOperandCount()
    {
        var ops = Operations(Template(Pdf("Name#20With#20Space")));

        int bdc = 0;
        foreach (var op in ops)
            if (op.Operator == "BDC")
            {
                bdc++;
                Assert.Equal(2, op.Operands.Length);
            }

        Assert.Equal(1, bdc);
    }

    /// <summary>
    /// <b>The control.</b> Templating must still do its job — the token really is replaced — so the
    /// equalities above cannot be satisfied by the engine having passed the page through untouched.
    /// </summary>
    /// <remarks>
    /// Asserted as "the token is gone and the stream changed" rather than "the stream contains
    /// Alice": the engine re-emits the run through a <c>TJ</c> array of <b>hex</b> strings, so the
    /// replacement value never appears literally in the bytes. Two earlier versions of this control
    /// looked for it — once in extracted text, once in the raw stream — and both failed while the
    /// substitution was working perfectly.
    /// </remarks>
    [Fact]
    public void TheTemplateTokenIsStillReplaced()
    {
        byte[] before = Pdf("Name#20With#20Space");
        string original = ContentOf(before);
        string templated = ContentOf(Template(before));

        Assert.Contains("{{name}}", original, StringComparison.Ordinal);
        Assert.DoesNotContain("{{name}}", templated, StringComparison.Ordinal);
        Assert.NotEqual(original, templated);
    }

    private static string ContentOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    // ------------------------------------------------------------------

    private static byte[] Template(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfTemplateEngine.Replace(
            input, output,
            new Dictionary<string, string> { ["name"] = "Alice" },
            new TemplateReplaceOptions());
        byte[] result = output.ToArray();
        Assert.True(result.Length > 0, "templating produced nothing");
        return result;
    }

    private static IReadOnlyList<ContentStreamOperation> Operations(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
    }

    /// <summary>
    /// One page whose content stream carries a marked-content section named with an escaped name,
    /// plus a template token for the engine to replace.
    /// </summary>
    private static byte[] Pdf(string escapedName)
    {
        string content =
            $"/{escapedName} << /MCID 0 >> BDC\n"
            + "BT /F0 12 Tf 50 700 Td ({{name}}) Tj ET\n"
            + "EMC\n";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 800] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
