// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Readers that decide on a file's word: the DocMDP comparison past its depth, a <c>/ByteRange</c>'s numbers, the
/// cross-reference stream an offset lands on, the names a content stream writes, and a form entry that does not parse.
/// </summary>
public class ScanCoreReadersTests
{
    // ------------------------------------------------------------------ F-025: the canonical form fails closed

    private static PdfArray Nested(int depth, int bottom)
    {
        PdfObject inner = new PdfNumber(bottom);
        for (int i = 0; i < depth; i++) inner = new PdfArray { inner };
        return (PdfArray)inner;
    }

    /// <summary>
    /// Two values that differ only below the canonical form's depth compare unequal — and so does one such value
    /// with itself: what the comparison cannot read is a change, never "the same". The control, inside the depth,
    /// compares by content.
    /// </summary>
    [Fact]
    public void AValueNestedPastTheCanonicalDepth_ComparesUnequal()
    {
        int past = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxObjectGraphDepth + 2;
        Assert.NotEqual(DocMdpAnalyzer.Canonical(Nested(past, 1)), DocMdpAnalyzer.Canonical(Nested(past, 2)));
        var one = Nested(past, 1);
        Assert.NotEqual(DocMdpAnalyzer.Canonical(one), DocMdpAnalyzer.Canonical(one));

        Assert.Equal(DocMdpAnalyzer.Canonical(Nested(70, 1)), DocMdpAnalyzer.Canonical(Nested(70, 1)));
        Assert.NotEqual(DocMdpAnalyzer.Canonical(Nested(70, 1)), DocMdpAnalyzer.Canonical(Nested(70, 2)));
    }

    // ------------------------------------------------------------------ F-028: a /ByteRange element is an integer

    [Theory]
    [InlineData("0 100 200 300", true)]
    [InlineData("0 100.5 200 300", false)]
    public void AByteRangeOfReals_IsRefusedNamingTheElement(string values, bool read)
    {
        var sig = new PdfDictionary();
        var array = new PdfArray();
        foreach (string v in values.Split(' '))
            array.Add(v.Contains('.') ? new PdfNumber(double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)) : new PdfNumber(long.Parse(v, System.Globalization.CultureInfo.InvariantCulture)));
        sig.Set(PdfName.ByteRange, array);

        Assert.Equal(read, SignedByteRanges.TryRead(sig, null, out var ranges, out string error));
        if (read) Assert.Equal(300, ranges.Length2);
        else Assert.Contains("index 1: expected integer, got the real", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ F-029: /Type /XRef in the object's own dictionary

    [Theory]
    [InlineData("1 0 obj\n<< /Type /XRef /W [1 2 1] /Size 3 >>\nstream\n", true)]
    [InlineData("1 0 obj<</Type/XRef/Size 3>>stream\n", true)]
    [InlineData("1 0 obj\n<< /Type /XRefStm >>\nendobj\n", false)]
    [InlineData("1 0 obj\n<< /Length 5 >>\nstream\n/Type /XRef\nendstream\nendobj\n", false)]
    [InlineData("1 0 obj\n<< /Length 0 >>\nendobj\n2 0 obj\n<< /Type /XRef >>\nstream\n", false)]
    [InlineData("1 0 obj\n<< /Note /XRef >>\nendobj\n", false)]
    public void ACrossReferenceStream_IsAnObjectWhoseDictionaryDeclaresTypeXRef(string body, bool isStream)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("%PDF-1.7\n" + body);
        Assert.Equal(isStream ? IncrementalUpdateWriter.XrefSectionKind.Stream : IncrementalUpdateWriter.XrefSectionKind.None,
                     IncrementalUpdateWriter.ClassifyXrefSectionAt(bytes, 9));
    }

    // ------------------------------------------------------------------ F-031: names through the writer's escape

    /// <summary>
    /// A name past Latin-1 reaches the content stream as the writer writes it — its UTF-8 bytes, each escaped — so
    /// the tag and the dictionary key it names are the same bytes; it was escaped by each character's low byte.
    /// </summary>
    [Fact]
    public void AContentStreamName_IsWrittenAsTheWriterWritesIt()
    {
        const string subtype = "כותרת";
        string content = Encoding.ASCII.GetString(new PdfContentStreamBuilder().BeginArtifact("Layout", subtype).EndMarkedContent().Build());

        Assert.Contains("/Subtype /" + PdfNameEscape.Escape(new PdfName(subtype)), content, StringComparison.Ordinal);
        Assert.StartsWith("#D7#9B", PdfNameEscape.Escape(new PdfName(subtype)), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ F-035: an unreadable field is recorded

    /// <summary>
    /// A form of two signature fields, the second's object unreadable (a stray <c>]</c>): the walk lists the first,
    /// and the form records the second as unreadable rather than absent. The control has both fields readable.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnreadableFieldEntry_IsRecorded_NotReadAsAbsent(bool broken)
    {
        string second = broken ? "]" : "<< /FT /Sig /T (b) >>";
        var sb = new StringBuilder("%PDF-1.7\n");
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [3 0 R 4 0 R] >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /FT /Sig /T (a) >>",
            second,
        };
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++) { offsets[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        using var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        var fields = doc.AcroForm.GetSignatureFields();

        Assert.Equal(broken ? 1 : 2, fields.Count);
        Assert.Equal(broken ? 1 : 0, doc.AcroForm.UnreadableNodes.Count);
        if (broken) Assert.Contains("/Fields entry 1 (object 4)", doc.AcroForm.UnreadableNodes[0], StringComparison.Ordinal);
    }
}
