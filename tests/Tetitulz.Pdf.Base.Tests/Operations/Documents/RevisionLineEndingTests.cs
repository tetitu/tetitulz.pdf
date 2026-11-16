// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// A revision ends where the writer left it, <b>including the end-of-line after <c>%%EOF</c></b> —
/// and that end-of-line may be <c>CR LF</c>.
/// </summary>
/// <remarks>
/// <para><b>Every other revision fixture in this suite writes <c>LF</c>,</b> so the <c>CR</c> half of
/// the two-byte swallow was reached by no test: deleting it left the whole suite green while the
/// <c>LF</c> half was killed by eighteen tests. ISO 32000-2 §7.5.5 lets a trailer end with either,
/// and a Windows producer writes <c>CR LF</c>, so this is the ordinary case rather than an
/// adversarial one.</para>
///
/// <para><b>What it costs when the CR is not swallowed.</b> The revision's <c>Length</c> comes out
/// two bytes short of the file. A signature whose <c>/ByteRange</c> covers the whole document then
/// ends <i>after</i> the boundary the reader believes in, so the shortfall
/// <c>Length - end</c> is negative, no revision qualifies, and the signature is reported under
/// <c>SignaturesCoveringNoRevision</c> — a document that is correctly signed reported as one whose
/// signature covers nothing. The two-byte tolerance in <c>RevisionCovering</c> cannot absorb it,
/// because it only forgives a range that stops <i>short</i>.</para>
/// </remarks>
public class RevisionLineEndingTests
{
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ARevisionEndsPastTheEndOfLineWhicheverOneTheWriterUsed(string eol)
    {
        byte[] pdf = SingleRevision(eol);

        var only = Assert.Single(Revisions(pdf));

        // The whole file, end-of-line included — which is exactly what a signature covering the
        // document declares, and what Extract must reproduce byte for byte.
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>
    /// The same claim for an appended revision, where a two-byte error would also move the boundary
    /// that separates the two.
    /// </summary>
    [Fact]
    public void BothRevisionsOfACrLfDocumentEndPastTheirOwnTerminator()
    {
        byte[] pdf = TwoRevisions("\r\n");

        var revisions = Revisions(pdf);

        Assert.Equal(2, revisions.Count);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Equal(text.IndexOf("%%EOF", System.StringComparison.Ordinal) + 7, revisions[0].Length);
        Assert.Equal(pdf.Length, revisions[1].Length);
    }

    /// <summary>
    /// <c>Extract</c> of a <c>CR LF</c> document reproduces the original bytes exactly. A revision
    /// measured two bytes short still opens as a PDF, so only a byte comparison can see it.
    /// </summary>
    [Fact]
    public void ExtractingTheFirstRevisionOfACrLfDocumentReproducesItsBytes()
    {
        byte[] pdf = TwoRevisions("\r\n");
        int firstEnd = Encoding.Latin1.GetString(pdf)
            .IndexOf("%%EOF", System.StringComparison.Ordinal) + 7;

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRevisions.Extract(input, 0, output);

        Assert.Equal(pdf[..firstEnd], output.ToArray());
    }

    // ------------------------------------------------------------------ helpers

    private static System.Collections.Generic.IReadOnlyList<PdfRevisionInfo> Revisions(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfRevisions.List(ms);
    }

    private static byte[] SingleRevision(string eol)
    {
        var sb = new StringBuilder();
        AppendBody(sb, eol);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] TwoRevisions(string eol)
    {
        var sb = new StringBuilder();
        AppendBody(sb, eol);

        int objectOffset = sb.Length;
        sb.Append("3 0 obj").Append(eol)
          .Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>").Append(eol)
          .Append("endobj").Append(eol);
        int xref = sb.Length;
        sb.Append("xref").Append(eol).Append("3 1").Append(eol)
          .Append(objectOffset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n ").Append(eol)
          .Append("trailer").Append(eol).Append("<< /Size 4 /Prev 9 >>").Append(eol)
          .Append("startxref").Append(eol)
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append(eol)
          .Append("%%EOF").Append(eol);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A well-formed one-revision document written entirely with <paramref name="eol"/>.</summary>
    private static void AppendBody(StringBuilder sb, string eol)
    {
        sb.Append("%PDF-1.7").Append(eol);
        sb.Append("1 0 obj").Append(eol).Append("<< /Type /Catalog /Pages 2 0 R >>").Append(eol).Append("endobj").Append(eol);
        sb.Append("2 0 obj").Append(eol).Append("<< /Type /Pages /Kids [3 0 R] /Count 1 >>").Append(eol).Append("endobj").Append(eol);
        sb.Append("3 0 obj").Append(eol).Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>").Append(eol).Append("endobj").Append(eol);

        int xref = sb.Length;
        sb.Append("xref").Append(eol).Append("0 1").Append(eol)
          .Append("0000000000 65535 f ").Append(eol)
          .Append("trailer").Append(eol).Append("<< /Size 4 /Root 1 0 R >>").Append(eol)
          .Append("startxref").Append(eol)
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append(eol)
          .Append("%%EOF").Append(eol);
    }
}
