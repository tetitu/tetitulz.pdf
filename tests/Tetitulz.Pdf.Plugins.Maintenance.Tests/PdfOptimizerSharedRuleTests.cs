// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Two spec rules this assembly implements for itself rather than calling the one body Core owns,
/// pinned against Core's answer instead of against a sentence.
///
/// <para><b>Why a test rather than a comment.</b> Both non-merges are deliberate, and a comment
/// asserting <i>agreement</i> is what CLAUDE.md names as a defect marker rather than documentation
/// ("a doc-comment claiming one implementation mirrors another is a defect marker … if you write
/// that comment, write the parity test instead"). For §7.7.3.3's separator in
/// <c>PdfOptimizer.ConcatenateContentStreams</c>, "stays in step by inspection" holds only until a
/// copy emits <c>A\nB</c> where the shared body emits <c>A\nB\n</c>, or returns a one-element
/// array's single part with no separator at all — and nothing but this test would tell
/// anyone.</para>
///
/// <para>Same for §7.2.2 Table 1: <c>PdfOptimizer.IsPdfWhitespace</c> / <c>IsPdfDelimiter</c> are
/// hand copies of <c>Core.PdfConstants</c>, which is <c>internal</c> and whose
/// <c>InternalsVisibleTo</c> names no plugin, so the copy cannot be deleted from inside this
/// assembly. A hand copy of that table drops members — NUL and form feed are the usual losses —
/// so these tests fix the two least-typed members (NUL, 0x00, and form feed, 0x0C) to observable
/// behaviour.</para>
/// </summary>
public class PdfOptimizerSharedRuleTests
{
    // ------------------------------------------------------------------
    //  §7.7.3.3 — the /Contents separator
    // ------------------------------------------------------------------

    /// <summary>
    /// The merged stream a page ends up with must be byte-for-byte what
    /// <c>Core.Document.PdfPageContent.Concatenate</c> — reached here through
    /// <c>PdfPage.GetContentStreamData</c>, which is a one-line call to it — makes of the same page
    /// before the merge. That is the whole of the "stays in step" claim, stated as bytes.
    ///
    /// <para>The second part deliberately does <b>not</b> end in a newline, so the trailing
    /// separator the shared body writes after the last part is visible in the length: the
    /// <c>if (i &gt; 0)</c> spelling the accessibility tagger uses produces an answer one byte
    /// shorter, and a single-part page one separator shorter again.</para>
    /// </summary>
    [Fact]
    public void ConcatenateContentStreams_JoinsPartsByteForByteAsCoreSharedReaderDoes()
    {
        byte[] pdf = TwoPartPagePdf();

        // Core's answer for this page, taken before the optimiser sees it.
        byte[] expected;
        using (var probe = PdfDocument.Open(new MemoryStream(pdf)))
            expected = probe.Pages[0].GetContentStreamData();

        var result = Optimize(pdf, new OptimizationOptions
        {
            ConcatenateContentStreams = true,
            CompressStreams = false,
        }, out byte[] optimized);

        // The fixture has to reach the feature: a skipped merge would leave the array in place and
        // make the byte comparison below a tautology about Core's own reader.
        Assert.Equal(1, result.ContentStreamsMerged);

        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        Assert.IsNotType<PdfArray>(Resolve(doc, doc.Pages[0].Dictionary[PdfName.Contents]));

        Assert.Equal(expected, doc.Pages[0].GetContentStreamData());

        // And the join really is the thing under test: the two parts are separated, not glued.
        Assert.Equal("1 0 0 1 10 20 cm\n0 0 50 50 re f\n",
            Encoding.Latin1.GetString(expected));
    }

    // ------------------------------------------------------------------
    //  §7.2.2 Table 1 — white space
    // ------------------------------------------------------------------

    /// <summary>
    /// NUL and form feed are white space, so <c>q</c> and <c>Q</c> separated by one of them are two
    /// operators and the empty save/restore pair between them is removable. A table missing either
    /// member reads <c>q&lt;sep&gt;Q</c> as one three-byte token, which is not the operator <c>q</c>,
    /// so <c>RemoveEmptyQQPairs</c> finds no pair and hands the stream back unchanged.
    ///
    /// <para>The assertion is the exact decoded content of the rewritten page, not a substring: the
    /// removal replaces the whole pair with a single space, and asserting only "no longer contains
    /// q" would also pass for a pass that deleted the drawing operators with it.</para>
    /// </summary>
    [Theory]
    [InlineData('\0')]     // NUL - the member Base's PdfRepair copy dropped
    [InlineData('\f')]     // form feed - the member Base's PdfRevisions copy dropped
    public void CleanContentStreams_TreatsNulAndFormFeedAsTokenSeparatorsLikeCore(char separator)
    {
        byte[] pdf = SinglePagePdf("q" + separator + "Q\n11 22 33 44 re\nS\n");

        var result = Optimize(pdf, new OptimizationOptions
        {
            CleanContentStreams = true,
            CompressStreams = false,
        }, out byte[] optimized);

        Assert.Equal(1, result.ContentStreamsClean);

        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        Assert.Equal(" \n11 22 33 44 re\nS\n",
            Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()));
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// A page whose <c>/Contents</c> is an array of two parts. The first ends at a token boundary
    /// with no white space of its own and the second ends with none either, so both the join
    /// separator and the trailing one are load-bearing in the byte count.
    /// </summary>
    private static byte[] TwoPartPagePdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [5 0 R] /Count 1 >>",
        StreamObj("1 0 0 1 10 20 cm"),
        StreamObj("0 0 50 50 re f"),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> " +
            "/Contents [3 0 R 4 0 R] >>");

    private static byte[] SinglePagePdf(string content) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
        StreamObj(content),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 3 0 R >>");

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        int guard = 0;
        while (value is PdfReference reference && guard++ < 32)
            value = document.GetObject(reference.ReferenceObjectNumber);
        return value;
    }

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

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
