// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An inline image's declared length survives a content-stream rewrite, so the payload boundary
/// stays exact on re-parse.
/// </summary>
/// <remarks>
/// <para><b>Both rewriters keep <c>/Length</c></b>; were one to drop it, the same page would come
/// out of the two as two different byte streams with two different re-parse boundaries. "The
/// parser does not add one" is true only in the sense that it does not <i>invent</i> one. It
/// stores every key the file wrote
/// verbatim and copies the lot onto the <c>PdfStream</c>, and
/// <c>InlineImageLength.GetDeclared</c> reads <c>/L ?? /Length</c> and is consulted <b>first</b>,
/// ahead of the whitespace-<c>EI</c> scan whose own comment calls it "provably wrong on real
/// files".</para>
///
/// <para><b>What is demonstrated, and what is not.</b> The divergence is asserted: a rewriter
/// skipping <c>/Length</c> emits different bytes for one page, and the mutant that skips it
/// reddens the case below. A <i>downstream truncation</i> does <b>not</b> follow against this
/// parser, and that is a fact about the parser rather than a gap in the test. Its fallback
/// <c>EI</c> scan validates the candidate — it requires the byte after
/// <c>EI</c> to end a token — so a decoy followed by a sample byte is rejected. The hazard its own
/// comment describes ("a dark grayscale image has no byte above 0x7F anywhere") needs a payload
/// whose <i>whole tail</i> looks like operators, not merely one embedded <c>EI</c>. Keeping the
/// length is still right: the declared length is the authoritative answer.</para>
/// </remarks>
public sealed class InlineImageLengthRoundTripTests
{
    /// <summary>
    /// The template engine's rewrite must preserve the declared length — asserted on the re-parsed
    /// image dictionary, not on the file's bytes, because every stream object carries a
    /// <c>/Length</c> of its own and a substring search finds those instead.
    /// </summary>
    [Fact]
    public void TemplateRewritePreservesTheDeclaredInlineImageLength()
    {
        PdfStream image = InlineImage(FillToken(InlineImagePdf()));

        // The VALUE, not merely its presence: a preserved key carrying the wrong number would
        // move the boundary just as surely as a dropped one.
        Assert.Equal(Payload().Length, DeclaredLength(image));
    }

    /// <summary>
    /// <b>The consequence.</b> The rewritten stream must re-parse to the same single inline image
    /// with the same payload — not to a truncated one followed by garbage operators.
    /// </summary>
    [Fact]
    public void TheRewrittenInlineImageReParsesToTheSamePayload()
    {
        byte[] original = InlineImagePdf();
        byte[] rewritten = FillToken(original);

        byte[] before = InlineImagePayload(original);
        byte[] after = InlineImagePayload(rewritten);

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// The redaction engine already round-tripped this correctly — asserted so the two rewriters
    /// are held to one answer rather than each to its own.
    /// </summary>
    [Fact]
    public void RedactionAlsoPreservesTheDeclaredInlineImageLength()
    {
        byte[] original = InlineImagePdf();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 1, Height = 1 },
        });

        Assert.Equal(Payload().Length, DeclaredLength(InlineImage(output.ToArray())));
        Assert.Equal(InlineImagePayload(original), InlineImagePayload(output.ToArray()));
    }

    // ------------------------------------------------------------------

    /// <summary>The page's single inline image, as the parser reads it back.</summary>
    private static PdfStream InlineImage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        byte[] content = doc.Pages[0].GetContentStreamData();
        Assert.NotNull(content);

        var ops = ContentStreamParser.Parse(content);
        var inline = ops.FirstOrDefault(o => o.Operator == "BI");
        Assert.True(inline != null, "the rewritten stream no longer contains an inline image");

        return Assert.IsType<PdfStream>(inline.Operands[0]);
    }

    private static byte[] InlineImagePayload(byte[] pdf)
        => InlineImage(pdf).RawData ?? Array.Empty<byte>();

    /// <summary>The image's declared payload length, or <c>-1</c> when it carries none.</summary>
    private static int DeclaredLength(PdfStream image)
        => image[PdfName.Length] is PdfNumber n ? (int)n.LongValue : -1;

    private static byte[] FillToken(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfTemplateEngine.Replace(input, output,
            new System.Collections.Generic.Dictionary<string, string> { ["k"] = "V" },
            new TemplateReplaceOptions());
        return output.ToArray();
    }

    /// <summary>
    /// A <b>Flate-compressed</b> payload carrying a whitespace-delimited <c>EI</c> in the middle.
    /// </summary>
    /// <remarks>
    /// <para><b>The filter is what makes this test possible, and its absence is what made the first
    /// version of it useless.</b> For an <i>unfiltered</i> inline image the parser computes the
    /// exact payload size from <c>/W × /H × /BPC</c> and never consults the declared length at all —
    /// so dropping <c>/Length</c> changed nothing and the mutant survived. Only a filtered payload,
    /// whose decoded size says nothing about its encoded size, forces the boundary question.</para>
    ///
    /// <para>Stored (uncompressed) deflate blocks are used so the decoy appears <b>literally</b> in
    /// the compressed bytes: with real compression the sequence would have to be found by luck.
    /// That is not cheating — compressed bytes are effectively random, so a large enough real
    /// payload contains every short sequence eventually, and this makes the same case reachable in
    /// a fixture.</para>
    /// </remarks>
    private static byte[] Payload()
    {
        var raw = new byte[64];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(0x80 + (i % 0x40));

        byte[] decoy = Encoding.ASCII.GetBytes(" EI ");
        Array.Copy(decoy, 0, raw, 24, decoy.Length);

        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
                   ms, System.IO.Compression.CompressionLevel.NoCompression, leaveOpen: true))
            z.Write(raw, 0, raw.Length);

        return ms.ToArray();
    }

    /// <summary>
    /// A one-page document whose content stream draws an inline image that declares its own
    /// <c>/Length</c>, followed by a template token so the rewriter has a reason to run.
    /// </summary>
    private static byte[] InlineImagePdf()
    {
        byte[] payload = Payload();

        var content = new StringBuilder();
        content.Append("q 64 0 0 1 10 10 cm\n");
        content.Append($"BI /W 64 /H 1 /BPC 8 /CS /G /F /Fl /Length {payload.Length} ID ");
        content.Append(Encoding.Latin1.GetString(payload));
        content.Append("\nEI Q\n");
        content.Append("BT /F1 12 Tf 20 100 Td ({{k}}) Tj ET\n");
        string contentText = content.ToString();

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {contentText.Length} >>\nstream\n{contentText}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
