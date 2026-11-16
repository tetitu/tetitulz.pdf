// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfXfaFormFiller"/>'s own decisions, and the one <see cref="XfaFieldPaths"/> rule that
/// no fixture reached — found by mutating the static-XFA path a decision at a time.
/// </summary>
/// <remarks>
/// <para><b>What the existing suite asserts is the value in each copy of the form</b>, which is the
/// right question for a fill and blind to everything around it: whether an encrypted document was
/// refused before a byte was written, whether a packet that cannot be superseded says so, what
/// happens when two AcroForm names resolve to one datasets element, and whether the replacement
/// stream is coherent.</para>
/// </remarks>
public class XfaFillDecisionTests
{
    private const string Before = "ORIGINAL";

    // ================================================================
    //  Refusals
    // ================================================================

    /// <summary>
    /// An encrypted document is refused before anything is written.
    /// </summary>
    /// <remarks>
    /// The same fail-closed rule <c>PdfFormFiller</c> applies, for the same reason and with more at
    /// stake: the datasets packet <b>and</b> the AcroForm values would be written in clear text
    /// where the reader expects per-object ciphertext. Nothing tested it on this path.
    /// </remarks>
    [Fact]
    public void FillingAnEncryptedDocumentIsRefused()
    {
        using var plain = new MemoryStream(StaticXfaFormPdf());
        using var encrypted = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(plain, encrypted,
            new Tetitulz.Pdf.Base.Encryption.EncryptionParameters
            {
                UserPassword = "u",
                OwnerPassword = "o",
                Algorithm = Tetitulz.Pdf.Base.Encryption.EncryptionAlgorithm.AES128,
            });

        using var input = new MemoryStream(encrypted.ToArray());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfXfaFormFiller.Fill(input, output, new Dictionary<string, string>
            {
                ["form1[0].total[0]"] = "NEW",
            }));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// An <c>/XFA</c> array whose <c>datasets</c> entry does not resolve to a stream is reported,
    /// not silently skipped.
    /// </summary>
    /// <remarks>
    /// <para>"The XFA half was not updated" and "there was nothing to update" are different
    /// answers, and a document whose two copies of the form now disagree is the outcome this type
    /// exists to report.</para>
    /// <para><b>This fixture writes the packet inline, and that is as close as a file can get.</b>
    /// The neighbouring guard — a datasets stream with no object number, which an incremental
    /// update cannot supersede — turns out to be unreachable from any file: §7.3.8 makes every
    /// stream indirect, and this parser only ever materialises a <c>PdfStream</c> from an indirect
    /// object, so a packet that resolves at all has a number. The guard is kept as a bound on its
    /// callee's contract, with that reasoning written where it stands.</para>
    /// </remarks>
    [Fact]
    public void ADatasetsEntryThatDoesNotResolveToAStreamIsReported()
    {
        byte[] pdf = Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> "
            + "/Annots [6 0 R] >>",
            Contents("BT ET"),
            "<< /Fields [6 0 R] /XFA [(template) 7 0 R (datasets) " + Contents(Datasets(Before)) + "] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
            + "/Rect [72 700 300 720] /P 3 0 R >>",
            Contents("<template/>"),
        });

        Fill(pdf, new Dictionary<string, string> { ["form1[0].total[0]"] = "NEW" }, out XfaFillResult result);

        Assert.True(result.XfaPresent);
        Assert.False(result.XfaWritten);
        // The reason, not just the refusal: a refusal test that does not name its own guard passes
        // for whichever reason happens to fire first, which is how a fixture that never reaches the
        // decision looks exactly like one that does. Asserting "not an indirect object" here is
        // what proved this fixture reaches a different branch than its first name claimed.
        Assert.Contains("carries no 'datasets' packet", result.DivergenceWarning ?? "",
                        StringComparison.Ordinal);
    }

    // ================================================================
    //  What the fill does with the names it was given
    // ================================================================

    /// <summary>
    /// Two AcroForm names that resolve to the same datasets element: the first wins and the second
    /// is reported unmatched, rather than one silently overwriting the other.
    /// </summary>
    /// <remarks>
    /// The leaf-name fallback makes this reachable — <c>form1[0].total[0]</c> and a bare
    /// <c>total</c> both address <c>form1.total</c>. Writing both would make the result depend on
    /// dictionary order, which is the one thing a form fill must never do.
    /// </remarks>
    [Fact]
    public void TwoNamesResolvingToOneElementFillItOnceAndReportTheOther()
    {
        Fill(StaticXfaFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = "FIRST",
            ["total"] = "SECOND",
        }, out XfaFillResult result);

        Assert.Single(result.XfaFilledFields);
        Assert.Contains("total", result.XfaUnmatchedFields);
    }

    /// <summary>
    /// When no requested name matches anything in the packet, the packet is left alone — not
    /// rewritten to identical bytes and appended as a revision nobody needs.
    /// </summary>
    [Fact]
    public void AFillThatMatchesNothingLeavesThePacketUntouched()
    {
        Fill(StaticXfaFormPdf(), new Dictionary<string, string>
        {
            ["nothing[0].likeThis[0]"] = "NEW",
        }, out XfaFillResult result);

        Assert.True(result.XfaPresent);
        Assert.False(result.XfaWritten);
        Assert.Contains("nothing[0].likeThis[0]", result.XfaUnmatchedFields);
    }

    // ================================================================
    //  The replacement stream has to be coherent
    // ================================================================

    /// <summary>
    /// A <b>compressed</b> datasets packet is replaced with plain bytes, so the filter entries that
    /// described the old bytes must not travel with it.
    /// </summary>
    /// <remarks>
    /// This is the separating input: with an uncompressed packet there is no <c>/Filter</c> to
    /// carry over, so copying every entry is harmless and the decision is invisible. Kept, the
    /// stream declares <c>FlateDecode</c> over uncompressed XML and the next reader gets nothing.
    /// </remarks>
    [Fact]
    public void ACompressedPacketIsReplacedWithoutItsFilter()
    {
        byte[] filled = Fill(FlateDatasetsFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = "NEW",
        }, out XfaFillResult result);

        Assert.True(result.XfaWritten);

        using var input = new MemoryStream(filled);
        var values = PdfFormReader.ReadXfaValues(input);
        Assert.NotNull(values);
        Assert.Equal("NEW", values!["form1.total"]);
    }

    /// <summary>
    /// The replacement's <c>/Length</c> describes the replacement. A stale length either truncates
    /// the packet or runs it into the bytes that follow.
    /// </summary>
    [Fact]
    public void TheReplacementDeclaresItsOwnLength()
    {
        // The new value is deliberately longer than the old, so a carried-over length is short.
        byte[] filled = Fill(StaticXfaFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = Before + "-MUCH-LONGER-THAN-IT-WAS",
        }, out _);

        using var input = new MemoryStream(filled);
        var values = PdfFormReader.ReadXfaValues(input);

        Assert.NotNull(values);
        Assert.Equal(Before + "-MUCH-LONGER-THAN-IT-WAS", values!["form1.total"]);
    }

    // ================================================================
    //  XfaFieldPaths
    // ================================================================

    /// <summary>
    /// Empty brackets are not an occurrence index. <c>total[]</c> is a name a producer wrote, and
    /// stripping the brackets would address a different element.
    /// </summary>
    /// <remarks>
    /// The theory beside this one covers <c>[0]</c> (stripped) and <c>[x]</c> (kept); the empty
    /// pair is the boundary between them, and the clause that decides it — a bracketed run must be
    /// non-empty to count as an index — was reached by neither.
    /// </remarks>
    [Theory]
    [InlineData("form1[0].total[]", "form1.total[]")]
    [InlineData("form1[].total[0]", "form1[].total")]
    public void EmptyBracketsAreNotAnOccurrenceIndex(string acroName, string expected)
    {
        Assert.Equal(expected, XfaFieldPaths.StripOccurrenceIndices(acroName));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static byte[] Fill(byte[] pdf, IDictionary<string, string> values, out XfaFillResult result)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = PdfXfaFormFiller.Fill(input, output, values);
        return output.ToArray();
    }

    private static string Datasets(string total) =>
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
        + "<xfa:data><form1><total>" + total + "</total></form1></xfa:data></xfa:datasets>";

    private static byte[] StaticXfaFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> "
        + "/Annots [6 0 R] >>",
        Contents("BT ET"),
        "<< /Fields [6 0 R] /XFA [(template) 7 0 R (datasets) 8 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
        + "/Rect [72 700 300 720] /P 3 0 R >>",
        Contents("<template/>"),
        Contents(Datasets(Before)),
    });

    /// <summary>The same form with the datasets packet <c>FlateDecode</c>d, as a producer writes it.</summary>
    private static byte[] FlateDatasetsFormPdf()
    {
        byte[] deflated = Deflate(Encoding.UTF8.GetBytes(Datasets(Before)));
        string stream = "<< /Length " + deflated.Length.ToString(CultureInfo.InvariantCulture)
                        + " /Filter /FlateDecode >>\nstream\n"
                        + Encoding.Latin1.GetString(deflated) + "\nendstream";

        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> "
            + "/Annots [6 0 R] >>",
            Contents("BT ET"),
            "<< /Fields [6 0 R] /XFA [(template) 7 0 R (datasets) 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
            + "/Rect [72 700 300 720] /P 3 0 R >>",
            Contents("<template/>"),
            stream,
        });
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static string Contents(string body) =>
        "<< /Length " + body.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + body + "\nendstream";

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
