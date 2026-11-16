// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The parser's own decodes — object streams, cross-reference streams and page content — spend one
/// budget over the document (<see cref="PdfLimits.MaxSyntaxStreamDecodeWork"/>). A syntax stream
/// naming an image codec is refused by name before any decode (<see cref="SyntaxStreamImageFilterTests"/>),
/// so the budget is spent by page content filtered through a codec, whose own budget is sized by the
/// stream's parameters; Flate, which every syntax stream is written with, charges nothing.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class SyntaxStreamDecodeBudgetTests
{
    /// <summary>A 256-pixel square of white, as a JBIG2 generic region: 64 Ki pixels to compose.</summary>
    private static readonly byte[] Jbig2 = Jbig2EncodeFilter.Encode(new byte[32 * 256], 256, 256, 32);

    /// <summary>What one decode of <see cref="Jbig2"/> charges, measured through a page's content with room.</summary>
    private static long OneDecode()
    {
        using var doc = PdfDocument.Open(new MemoryStream(PageWithContent(1, Jbig2, "/JBIG2Decode")));
        doc.Pages[0].GetContentStreamData();
        return doc.Parser.SyntaxStreamWork.Spent;
    }

    [Fact]
    public void One_jbig2_decode_charges_at_least_its_pixels()
    {
        long one = OneDecode();
        Assert.True(one >= 256 * 256, $"one decode charged {one}, less than its pixels");
    }

    /// <summary>Flate, which object streams are written with, charges nothing; the member is read.</summary>
    [Fact]
    public void A_flate_object_stream_spends_none_of_it_and_its_member_is_read()
    {
        byte[] body = Encoding.ASCII.GetBytes("7 0 << /A 1 >>");
        var parser = Parser(ObjectStreams(1, FlateEncodeFilter.Encode(body), "/FlateDecode", first: 4), 1);

        var member = Assert.IsType<PdfDictionary>(parser.GetObject(7));
        Assert.Equal(1, member.GetInteger(PdfName.A));
        Assert.Equal(0, parser.SyntaxStreamWork.Spent);
    }

    /// <summary>The ceiling comes from <see cref="PdfLimits.MaxSyntaxStreamDecodeWork"/>, and a refusal names it.</summary>
    [Fact]
    public void Adversarial_page_content_is_refused_by_the_process_wide_ceiling_naming_it()
    {
        try
        {
            PdfLimits.MaxSyntaxStreamDecodeWork = 1000;
            using var doc = PdfDocument.Open(new MemoryStream(PageWithContent(1, Jbig2, "/JBIG2Decode")));
            Assert.Equal(1000, doc.Parser.MaxSyntaxStreamDecodeWork);

            var ex = Assert.Throws<WorkBudgetExceededException>(() => doc.Pages[0].GetContentStreamData());
            Assert.Contains("PdfLimits.MaxSyntaxStreamDecodeWork", ex.Message, StringComparison.Ordinal);
            Assert.Same(doc.Parser.SyntaxStreamWork, ex.Budget);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A page's content is syntax too: three JBIG2-filtered parts of one page's /Contents, under a
    /// document ceiling of one and a half decodes, are refused by the document's budget as itself.
    /// Each used to spend a whole JBIG2 stream budget, uncounted above it.
    /// </summary>
    [Fact]
    public void Adversarial_jbig2_page_content_is_refused_by_the_documents_budget()
    {
        long one = OneDecode();
        try
        {
            PdfLimits.MaxSyntaxStreamDecodeWork = one + one / 2;
            using var doc = PdfDocument.Open(new MemoryStream(PageWithContent(3, Jbig2, "/JBIG2Decode")));

            var ex = Assert.Throws<WorkBudgetExceededException>(() => doc.Pages[0].GetContentStreamData());
            Assert.Same(doc.Parser.SyntaxStreamWork, ex.Budget);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The control: three parts under a ceiling of four decodes all decode, each charged.</summary>
    [Fact]
    public void Jbig2_page_content_within_the_budget_decodes_and_is_each_charged()
    {
        long one = OneDecode();
        try
        {
            PdfLimits.MaxSyntaxStreamDecodeWork = 4 * one;
            using var doc = PdfDocument.Open(new MemoryStream(PageWithContent(3, Jbig2, "/JBIG2Decode")));

            byte[] content = doc.Pages[0].GetContentStreamData();
            Assert.Equal(3 * (32 * 256 + 1), content.Length);   // three bitmaps, a newline after each part
            Assert.Equal(3 * one, doc.Parser.SyntaxStreamWork.Spent);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The control: Flate content charges nothing, and the parts read back joined.</summary>
    [Fact]
    public void Flate_page_content_spends_none_of_it()
    {
        try
        {
            PdfLimits.MaxSyntaxStreamDecodeWork = 1;
            byte[] part = FlateEncodeFilter.Encode(Encoding.ASCII.GetBytes("0 0 m"));
            using var doc = PdfDocument.Open(new MemoryStream(PageWithContent(2, part, "/FlateDecode")));

            Assert.Equal("0 0 m\n0 0 m\n", Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData()));
            Assert.Equal(0, doc.Parser.SyntaxStreamWork.Spent);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>A one-page file whose /Contents is <paramref name="parts"/> streams of <paramref name="data"/>.</summary>
    internal static byte[] PageWithContent(int parts, byte[] data, string filter)
    {
        var bodies = new List<byte[]>
        {
            Latin("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents [" +
                  string.Join(" ", Enumerable.Range(4, parts).Select(n => n + " 0 R")) + "] >>"),
        };
        for (int i = 0; i < parts; i++)
            bodies.Add(Latin($"<< /Filter {filter} /Length {data.Length} >>\nstream\n")
                .Concat(data).Concat(Latin("\nendstream")).ToArray());

        var file = new List<byte>(Latin("%PDF-1.7\n"));
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(file.Count);
            file.AddRange(Latin($"{i + 1} 0 obj\n"));
            file.AddRange(bodies[i]);
            file.AddRange(Latin("\nendobj\n"));
        }
        int xref = file.Count;
        var table = new StringBuilder($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) table.Append(off.ToString("D10")).Append(" 00000 n \n");
        table.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        file.AddRange(Latin(table.ToString()));
        return file.ToArray();
    }

    internal static PdfParser Parser(byte[] file, long ceiling)
    {
        var parser = new PdfParser(new MemoryStream(file)) { MaxSyntaxStreamDecodeWork = ceiling };
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    /// <summary>
    /// <paramref name="count"/> object streams of <paramref name="data"/> under <paramref name="filter"/>
    /// and no cross-reference section, so the parser finds them by scanning and indexes each.
    /// </summary>
    internal static byte[] ObjectStreams(int count, byte[] data, string filter, int first = 0)
    {
        var file = new List<byte>(Latin("%PDF-1.7\n"));
        for (int i = 0; i < count; i++)
        {
            file.AddRange(Latin($"{3 + i} 0 obj\n<< /Type /ObjStm /N 1 /First {first} /Filter {filter} /Length {data.Length} >>\nstream\n"));
            file.AddRange(data);
            file.AddRange(Latin("\nendstream\nendobj\n"));
        }
        return file.ToArray();
    }

    internal static byte[] Latin(string s) => Encoding.Latin1.GetBytes(s);
}
