// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Text written into a content stream as a §7.3.4.2 literal string is escaped completely.
///
/// <para><b>Why this exists.</b> Literal-string escaping has one home,
/// <c>Core.Objects.PdfStringEscape.EscapeLiteral</c>, created because eight hand-rolled copies had
/// drifted into two classes — four that escaped control bytes and four that did not. Four of the
/// weak ones outlived that consolidation: they escaped <c>\</c>, <c>(</c> and <c>)</c> and emitted
/// every control byte raw.</para>
///
/// <para><b>The value written to <c>/V</c> was never at risk</b> — that goes through
/// <see cref="PdfString"/>, which the object writer escapes itself. The exposure is the
/// <b>appearance stream</b>, which these writers build as content-stream text by hand: a raw CR
/// inside a literal string is defined to read back as a line feed, so the visible text of a field,
/// a signature appearance, a barcode caption or a timestamp block differed from the text the caller
/// asked for — while <c>/V</c> still held the original, so the two disagreed inside one file.</para>
/// </summary>
public class LiteralStringEscapeRoundTripTests
{
    /// <summary>
    /// A carriage return in the field text reaches the appearance stream as the escape sequence
    /// <c>\r</c>, never as a raw 0x0D byte inside the literal.
    /// </summary>
    [Fact]
    public void FieldAppearanceStream_EscapesControlCharacters()
    {
        byte[] appearance = AppearanceStreamFor("line1\rline2\tend");
        string text = Encoding.Latin1.GetString(appearance);

        Assert.Contains(@"\r", text);
        Assert.Contains(@"\t", text);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\t', text);
    }

    /// <summary>
    /// The ordinary escapes still work — the control, so the test above cannot pass by escaping
    /// having been dropped altogether.
    /// </summary>
    [Fact]
    public void FieldAppearanceStream_EscapesParenthesesAndBackslash()
    {
        byte[] appearance = AppearanceStreamFor(@"a(b)c\d");
        string text = Encoding.Latin1.GetString(appearance);

        Assert.Contains(@"a\(b\)c\\d", text);
    }

    // ------------------------------------------------------------------

    /// <summary>Builds a text field with <paramref name="value"/> and returns its /AP /N bytes.</summary>
    private static byte[] AppearanceStreamFor(string value)
    {
        using var withField = new MemoryStream();
        PdfFormFieldBuilder.AddTextField(new MemoryStream(OnePagePdf()), withField, new FormTextField
        {
            FieldName    = "T1",
            PageIndex    = 0,
            X = 10, Y = 10, Width = 190, Height = 30,
            DefaultValue = value,
        });

        using var doc = PdfDocument.Open(new MemoryStream(withField.ToArray()));
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        Assert.NotNull(annots);

        foreach (var a in annots)
        {
            var annot = doc.ResolveObject(a) as PdfDictionary;
            var ap    = doc.ResolveObject(annot?[PdfName.AP]) as PdfDictionary;
            var n     = doc.ResolveObject(ap?[PdfName.N]) as PdfStream;
            if (n != null) return n.GetDecodedData();
        }
        throw new Xunit.Sdk.XunitException("the field should carry an /AP /N appearance stream");
    }

    private static byte[] OnePagePdf()
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R "
            + "/Resources << >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
