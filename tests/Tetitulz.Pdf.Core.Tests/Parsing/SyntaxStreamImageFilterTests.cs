// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// An object stream or cross-reference stream whose <c>/Filter</c> names an image codec is refused
/// by name before any decode: a codec's output is samples, never syntax, and asking the codec cost
/// the document's whole syntax-stream budget once for every open. The coverage-guided fuzzer's
/// <c>open</c> target found the 1 538-byte file under <c>Fixtures/Fuzz</c>, whose recovered object
/// stream names JBIG2 among sixteen filters: the scorecard's forty opens of it took 30 s.
/// </summary>
/// <remarks>
/// <b>The bodies below are well-formed as they are</b> — an object stream's member, a cross-reference
/// table — so a codec that passed its bytes through (DCT does; JBIG2 and CCITT fall back to the raw
/// bytes when they cannot decode) would index them: the refusal is the only reason they are unread,
/// which is what lets the guard's absence be seen.
/// </remarks>
public sealed class SyntaxStreamImageFilterTests
{
    [Theory]
    [InlineData("/JBIG2Decode", "JBIG2Decode")]
    [InlineData("/JPXDecode", "JPXDecode")]
    [InlineData("/DCTDecode", "DCTDecode")]
    [InlineData("/CCITTFaxDecode", "CCITTFaxDecode")]
    [InlineData("/DCT", "DCT")]
    [InlineData("/CCF", "CCF")]
    [InlineData("[/FlateDecode /JBIG2Decode]", "JBIG2Decode")]
    public void An_object_stream_naming_an_image_codec_is_refused_unread(string filter, string codec)
    {
        byte[] body = SyntaxStreamDecodeBudgetTests.Latin("7 0 << /A 1 >>");
        var parser = SyntaxStreamDecodeBudgetTests.Parser(
            SyntaxStreamDecodeBudgetTests.ObjectStreams(1, body, filter, first: 4), long.MaxValue);

        Assert.Contains("image codec", PdfParser.LastXrefSectionError ?? "", StringComparison.Ordinal);
        Assert.Contains("/" + codec, PdfParser.LastXrefSectionError, StringComparison.Ordinal);
        Assert.Equal(0, parser.ObjectStreamIndexReads);
        Assert.Equal(0, parser.SyntaxStreamWork.Spent);
        Assert.IsType<PdfNull>(parser.GetObject(7));
    }

    /// <summary>The control: the same body under Flate is indexed and its member read.</summary>
    [Fact]
    public void An_object_stream_under_flate_is_indexed_and_read()
    {
        byte[] body = SyntaxStreamDecodeBudgetTests.Latin("7 0 << /A 1 >>");
        var parser = SyntaxStreamDecodeBudgetTests.Parser(
            SyntaxStreamDecodeBudgetTests.ObjectStreams(1, FlateEncodeFilter.Encode(body), "/FlateDecode", first: 4), long.MaxValue);

        Assert.DoesNotContain("image codec", PdfParser.LastXrefSectionError ?? "", StringComparison.Ordinal);
        Assert.Equal(1, parser.ObjectStreamIndexReads);
        Assert.Equal(1, Assert.IsType<PdfDictionary>(parser.GetObject(7)).GetInteger(PdfName.A));
    }

    [Fact]
    public void A_cross_reference_stream_naming_an_image_codec_is_refused_unread()
    {
        var parser = SyntaxStreamDecodeBudgetTests.Parser(XrefStream("/JBIG2Decode"), long.MaxValue);

        Assert.Contains("image codec", PdfParser.LastXrefSectionError ?? "", StringComparison.Ordinal);
        Assert.Contains("/JBIG2Decode", PdfParser.LastXrefSectionError, StringComparison.Ordinal);
        Assert.Equal(0, parser.SyntaxStreamWork.Spent);
    }

    /// <summary>The control: the same table under Flate is the section, read without a skip.</summary>
    [Fact]
    public void A_cross_reference_stream_under_flate_is_read()
    {
        var parser = SyntaxStreamDecodeBudgetTests.Parser(XrefStream("/FlateDecode"), long.MaxValue);

        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.IsType<PdfStream>(parser.GetObject(1));
    }

    [Fact]
    public void Adversarial_fuzzer_file_with_a_jbig2_object_stream_opens_without_spending_the_budget()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fuzz", "open-image-filtered-syntax-stream.pdf"));
        using var doc = PdfDocument.Open(new MemoryStream(file));

        Assert.Equal(0, doc.Parser.SyntaxStreamWork.Spent);
        Assert.Contains("image codec", PdfParser.LastXrefSectionError ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// A file whose only cross-reference section is a stream under <paramref name="filter"/>: one
    /// type-1 entry naming the stream itself at offset 9, as a 3-byte row (W [1 1 1]).
    /// </summary>
    private static byte[] XrefStream(string filter)
    {
        var file = new List<byte>(SyntaxStreamDecodeBudgetTests.Latin("%PDF-1.7\n"));
        int at = file.Count;
        byte[] rows = { 0, 0, 0, 1, (byte)at, 0 };
        byte[] data = filter == "/FlateDecode" ? FlateEncodeFilter.Encode(rows) : rows;
        file.AddRange(SyntaxStreamDecodeBudgetTests.Latin(
            $"1 0 obj\n<< /Type /XRef /Size 2 /W [1 1 1] /Root 1 0 R /Filter {filter} /Length {data.Length} >>\nstream\n"));
        file.AddRange(data);
        file.AddRange(SyntaxStreamDecodeBudgetTests.Latin($"\nendstream\nendobj\nstartxref\n{at}\n%%EOF\n"));
        return file.ToArray();
    }
}
