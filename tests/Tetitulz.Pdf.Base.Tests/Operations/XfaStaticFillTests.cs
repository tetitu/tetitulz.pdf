// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Filling a <b>static</b> XFA form updates both copies the document carries, and says so when it
/// could not.
/// </summary>
/// <remarks>
/// <para><b>The failure this suite exists to catch is a self-contradicting document.</b> A static
/// XFA form (§12.7.8) holds its fields twice: as XFA XML, which an XFA-aware viewer believes, and
/// as an AcroForm, which everything else draws. Filling one and not the other produces a file that
/// renders one value and reports another, with both readers confident and neither wrong.</para>
///
/// <para><b>So every assertion here reads both copies.</b> Asserting only the AcroForm is what a
/// plain <c>PdfFormFiller</c> test already does and would pass with the XFA half deleted.</para>
/// </remarks>
public class XfaStaticFillTests
{
    private const string Before = "ORIGINAL-XFA-VALUE";
    private const string After = "FILLED-XFA-VALUE";

    /// <summary>Both copies of the form carry the new value.</summary>
    [Fact]
    public void FillingAStaticXfaFormUpdatesTheDatasetsPacketAndTheAcroForm()
    {
        byte[] filled = Fill(StaticXfaFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = After,
        }, out XfaFillResult result);

        Assert.True(result.XfaPresent);
        Assert.True(result.XfaWritten);

        // The XFA copy.
        using var input = new MemoryStream(filled);
        var values = PdfFormReader.ReadXfaValues(input);
        Assert.NotNull(values);
        Assert.Equal(After, values!["form1.total"]);

        // The AcroForm copy.
        input.Position = 0;
        var fields = PdfFormReader.GetFields(input);
        Assert.Equal(After, Value(fields, "form1[0].total[0]"));
    }

    /// <summary>
    /// The two copies agreeing is reported as agreement, not as an absent warning nobody set.
    /// </summary>
    /// <remarks>
    /// The acceptance control for <see cref="AFieldMissingFromTheDatasetsPacketIsReportedAsDivergence"/>:
    /// without it, a <c>DivergenceWarning</c> that was never assigned would pass that test too.
    /// </remarks>
    [Fact]
    public void WhenBothCopiesTookTheValueNoDivergenceIsReported()
    {
        Fill(StaticXfaFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = After,
        }, out XfaFillResult result);

        Assert.Contains("form1[0].total[0]", result.AcroForm.FilledFields);
        Assert.Contains("form1[0].total[0]", result.XfaFilledFields);
        Assert.Null(result.DivergenceWarning);
    }

    /// <summary>
    /// A field the AcroForm has and the datasets packet does not is named in the warning.
    /// </summary>
    /// <remarks>
    /// This is the case that used to be silent, and it is the common one: a form edited in a
    /// non-XFA tool grows an AcroForm field with no XFA counterpart. The value is still written to
    /// the AcroForm — refusing the whole fill over it would be worse — but the caller is told which
    /// field the two copies now disagree about.
    /// </remarks>
    [Fact]
    public void AFieldMissingFromTheDatasetsPacketIsReportedAsDivergence()
    {
        Fill(StaticXfaFormPdf(), new Dictionary<string, string>
        {
            ["acroOnly[0]"] = After,
        }, out XfaFillResult result);

        Assert.Contains("acroOnly[0]", result.AcroForm.FilledFields);
        Assert.Contains("acroOnly[0]", result.XfaUnmatchedFields);

        Assert.NotNull(result.DivergenceWarning);
        Assert.Contains("acroOnly[0]", result.DivergenceWarning!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>/XFA</c> that is a single XDP stream rather than named packets is reported, not
    /// silently half-filled.
    /// </summary>
    /// <remarks>
    /// §12.7.8 allows both shapes. Splitting an XDP into its packets is an XML problem this filler
    /// does not take on — but the AcroForm half still runs, so without the report the caller would
    /// receive a document whose XFA copy still holds the old values and no indication of it.
    /// </remarks>
    [Fact]
    public void AnUnpackagedXdpStreamIsReportedRatherThanQuietlySkipped()
    {
        Fill(UnpackagedXdpFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = After,
        }, out XfaFillResult result);

        Assert.True(result.XfaPresent);
        Assert.False(result.XfaWritten);
        Assert.NotNull(result.DivergenceWarning);
        Assert.Contains("XDP", result.DivergenceWarning!, System.StringComparison.Ordinal);
    }

    /// <summary>A document with no XFA fills exactly as before and reports no divergence.</summary>
    /// <remarks>
    /// One copy of the form cannot disagree with itself, so a warning here would be noise a caller
    /// learns to ignore — and a channel a caller ignores is one that cannot report the real case.
    /// </remarks>
    [Fact]
    public void ADocumentWithNoXfaFillsTheAcroFormAndReportsNoDivergence()
    {
        Fill(NoXfaFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = After,
        }, out XfaFillResult result);

        Assert.False(result.XfaPresent);
        Assert.Contains("form1[0].total[0]", result.AcroForm.FilledFields);
        Assert.Null(result.DivergenceWarning);
    }

    /// <summary>
    /// <b>A datasets packet past the ceiling is not read, and the divergence says why.</b> A packet is
    /// built into a tree at many times its bytes, and a few kilobytes of a double deflate reach the
    /// general decode ceiling. The control is <see cref="StaticXfaFormPdf"/>, read and rewritten above.
    /// </summary>
    [Fact]
    public void AdversarialDatasetsPastThePacketCeiling_AreNotReadAndTheFillSaysSo()
    {
        byte[] pdf = StaticXfaFormPdfWithDatasetsStream(
            HexFlateStream(64 * 1024 * 1024 + 1));

        Fill(pdf, new Dictionary<string, string> { ["form1[0].total[0]"] = After }, out XfaFillResult result);

        Assert.True(result.XfaPresent);
        Assert.Empty(result.XfaFilledFields);
        Assert.Contains("datasets packet could not be read", result.DivergenceWarning, StringComparison.Ordinal);
        Assert.Contains("limit", result.DivergenceWarning, StringComparison.Ordinal);

        // A packet that fits goes on to the reader, which says something else about it.
        Fill(StaticXfaFormPdfWithDatasetsStream(HexFlateStream(1024)),
             new Dictionary<string, string> { ["form1[0].total[0]"] = After }, out XfaFillResult small);
        Assert.DoesNotContain("could not be read", small.DivergenceWarning, StringComparison.Ordinal);
        Assert.Contains("not well-formed", small.DivergenceWarning, StringComparison.Ordinal);
    }

    private static string HexFlateStream(int spaces)
    {
        using var deflated = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(deflated, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            var buffer = new byte[65536];
            Array.Fill(buffer, (byte)' ');
            for (int left = spaces; left > 0; left -= buffer.Length) z.Write(buffer, 0, Math.Min(left, buffer.Length));
        }

        string hex = Convert.ToHexString(deflated.ToArray()) + ">";
        return $"<< /Filter [/ASCIIHexDecode /FlateDecode] /Length {hex.Length} >>\nstream\n{hex}\nendstream";
    }

    private static byte[] StaticXfaFormPdfWithDatasetsStream(string datasetsObject) => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        + "/Resources << >> /Annots [5 0 R 6 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
        + "/Rect [72 700 300 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (acroOnly[0]) /V () "
        + "/Rect [72 650 300 670] /P 3 0 R >>",
        "<< /Fields [5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> "
        + "/XFA [(template) 9 0 R (datasets) 10 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Contents("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/>"),
        datasetsObject,
    });

    // ------------------------------------------------------------------ reading

    /// <summary><c>HasXfa</c> separates an XFA form from a plain AcroForm.</summary>
    [Fact]
    public void HasXfaAnswersTrueOnlyForADocumentCarryingAPacket()
    {
        using var xfa = new MemoryStream(StaticXfaFormPdf());
        using var plain = new MemoryStream(NoXfaFormPdf());

        Assert.True(PdfFormReader.HasXfa(xfa));
        Assert.False(PdfFormReader.HasXfa(plain));
    }

    /// <summary>The datasets values come back by dotted path, blanks included.</summary>
    /// <remarks>
    /// The empty field matters: an XFA field that was cleared is an empty element, not a missing
    /// one, and a reader that skipped it would report a blank field as absent from the form.
    /// </remarks>
    [Fact]
    public void ReadXfaValuesReturnsEveryLeafIncludingTheEmptyOnes()
    {
        using var input = new MemoryStream(StaticXfaFormPdf());

        var values = PdfFormReader.ReadXfaValues(input);

        Assert.NotNull(values);
        Assert.Equal(Before, values!["form1.total"]);
        Assert.Equal(string.Empty, values["form1.blank"]);
    }

    /// <summary>A document with no XFA has no datasets to read, which is not an empty form.</summary>
    [Fact]
    public void ReadXfaValuesIsNullWhenThereIsNoPacket()
    {
        using var input = new MemoryStream(NoXfaFormPdf());

        Assert.Null(PdfFormReader.ReadXfaValues(input));
    }

    // ------------------------------------------------------------------ hostile input

    /// <summary>
    /// A malformed <c>datasets</c> packet declaring an external entity is neither expanded nor
    /// fetched, and the fill reports that the packet was not updated.
    /// </summary>
    /// <remarks>
    /// <para>The packet is attacker-controlled — it arrives inside whatever PDF the caller was
    /// handed. <c>DtdProcessing.Prohibit</c> makes the doctype itself a parse error, so the reader
    /// never reaches the entity reference; <b>Prohibit rather than Ignore</b>, because silently
    /// dropping part of a form's data produces a fill that looks like it worked.</para>
    /// <para>The absolute path in the entity is a literal, not a checkout path, and is never
    /// asserted to exist — reaching it at all is the failure.</para>
    /// </remarks>
    [Fact]
    public void AMalformedDatasetsPacketDeclaringAnExternalEntityIsRefusedNotExpanded()
    {
        Fill(XxeDatasetsFormPdf(), new Dictionary<string, string>
        {
            ["form1[0].total[0]"] = After,
        }, out XfaFillResult result);

        Assert.True(result.XfaPresent);
        Assert.False(result.XfaWritten);
        Assert.NotNull(result.DivergenceWarning);
        Assert.Contains("well-formed", result.DivergenceWarning!, System.StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ name mapping

    /// <summary>SOM occurrence indices are stripped; anything else inside brackets is not.</summary>
    /// <remarks>
    /// <c>total[x]</c> is not an occurrence index, and stripping it would address a different
    /// field — which is the mapping mistake that writes a value into the wrong element.
    /// </remarks>
    [Theory]
    [InlineData("form1[0].total[0]", "form1.total")]
    [InlineData("form1.total", "form1.total")]
    [InlineData("form1[12].rows[3].cell[0]", "form1.rows.cell")]
    [InlineData("total[x]", "total[x]")]
    [InlineData("total[", "total[")]
    public void OccurrenceIndicesAreStrippedAndOtherBracketsAreNot(string acroName, string expected)
        => Assert.Equal(expected, XfaFieldPaths.StripOccurrenceIndices(acroName));

    /// <summary>
    /// A leaf-name fallback applies only when it is unambiguous.
    /// </summary>
    /// <remarks>
    /// Two leaves called <c>total</c> make the answer a coin toss, so there is no answer. A filler
    /// that guessed would write the caller's value into a subform they never named.
    /// </remarks>
    [Fact]
    public void TheLeafNameFallbackIsRefusedWhenTwoLeavesShareTheName()
    {
        var ambiguous = new[] { "form1.a.total", "form1.b.total" };
        var unique = new[] { "form1.a.total", "form1.b.other" };

        Assert.Null(XfaFieldPaths.Resolve("total[0]", ambiguous));
        Assert.Equal("form1.a.total", XfaFieldPaths.Resolve("total[0]", unique));
    }

    /// <summary>
    /// A later occurrence of repeated siblings has no element in the paths model, which keeps the
    /// first: it resolves to nothing rather than to the first instance. An unnamed subform's index
    /// (a designer's second page) is not such an occurrence.
    /// </summary>
    [Fact]
    public void AdversarialSecondOccurrence_ResolvesToNothing_NotTheFirstInstance()
    {
        var paths = new[] { "form1.TextField1" };

        Assert.Null(XfaFieldPaths.Resolve("form1[0].TextField1[1]", paths));
        Assert.Null(XfaFieldPaths.Resolve("form1[0].row[3].cell[0]", new[] { "form1.row.cell" }));
        Assert.Equal("form1.TextField1", XfaFieldPaths.Resolve("form1[0].TextField1[0]", paths));
        Assert.Equal("form1.TextField1", XfaFieldPaths.Resolve("form1[0].#subform[1].TextField1[0]", paths));
    }

    /// <summary>
    /// Filled end to end: the value for the second of two same-named fields leaves the first
    /// datasets element alone and is reported as not written to the XFA copy.
    /// </summary>
    [Fact]
    public void AdversarialRepeatedFieldsSecondValue_IsNotWrittenIntoTheFirstElement()
    {
        byte[] pdf = Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << >> /Annots [5 0 R 6 0 R] >>",
            Contents(PageContent),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].TextField1[0]) /V (a) "
            + "/Rect [72 700 300 720] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].TextField1[1]) /V (b) "
            + "/Rect [72 650 300 670] /P 3 0 R >>",
            "<< /Fields [5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> "
            + "/XFA [(template) 9 0 R (datasets) 10 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Contents("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/>"),
            Contents("<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
                     + "<xfa:data><form1><TextField1>a</TextField1><TextField1>b</TextField1></form1>"
                     + "</xfa:data></xfa:datasets>"),
        });

        byte[] filled = Fill(pdf, new Dictionary<string, string> { ["form1[0].TextField1[1]"] = "X" }, out var result);

        var xfa = PdfFormReader.ReadXfaValues(new MemoryStream(filled));
        Assert.Equal("a", xfa!["form1.TextField1"]);
        Assert.Equal("X", Value(PdfFormReader.GetFields(new MemoryStream(filled)), "form1[0].TextField1[1]"));
        Assert.Contains("form1[0].TextField1[1]", result.XfaUnmatchedFields);
        Assert.NotNull(result.DivergenceWarning);
    }

    /// <summary>An exact path beats a leaf-name match elsewhere in the tree.</summary>
    [Fact]
    public void AnExactPathWinsOverALeafNameMatch()
    {
        var paths = new[] { "form1.total", "other.total" };

        Assert.Equal("form1.total", XfaFieldPaths.Resolve("form1[0].total[0]", paths));
    }

    // ==================================================================

    private static byte[] Fill(byte[] pdf, IDictionary<string, string> values, out XfaFillResult result)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = PdfXfaFormFiller.Fill(input, output, values);
        return output.ToArray();
    }

    private static string Value(IReadOnlyList<PdfFormFieldInfo> fields, string name)
    {
        foreach (var f in fields)
            if (f.FullyQualifiedName == name) return f.Value;
        return null;
    }

    // ------------------------------------------------------------------ fixtures

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (form) Tj ET";

    private static string Contents(string text) =>
        $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

    /// <summary>
    /// A datasets packet with one filled leaf, one blank leaf, and no counterpart for the
    /// AcroForm's <c>acroOnly</c> field.
    /// </summary>
    private static string Datasets(string total) =>
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
        + "<xfa:data><form1><total>" + total + "</total><blank></blank></form1>"
        + "</xfa:data></xfa:datasets>";

    /// <summary>
    /// A one-page static XFA form: <c>/XFA</c> in §12.7.8's array shape, plus the AcroForm shadow
    /// whose field names carry SOM occurrence indices the way a common viewer writes them.
    /// </summary>
    private static byte[] StaticXfaFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        + "/Resources << >> /Annots [5 0 R 6 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
        + "/Rect [72 700 300 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (acroOnly[0]) /V () "
        + "/Rect [72 650 300 670] /P 3 0 R >>",
        "<< /Fields [5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> "
        + "/XFA [(template) 9 0 R (datasets) 10 0 R] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Contents("<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/>"),
        Contents(Datasets(Before)),
    });

    /// <summary>The same form with <c>/XFA</c> as one XDP stream rather than named packets.</summary>
    private static byte[] UnpackagedXdpFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        + "/Resources << >> /Annots [5 0 R 6 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
        + "/Rect [72 700 300 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (acroOnly[0]) /V () "
        + "/Rect [72 650 300 670] /P 3 0 R >>",
        "<< /Fields [5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> "
        + "/XFA 9 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        Contents("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">" + Datasets(Before) + "</xdp:xdp>"),
    });

    /// <summary>A datasets packet whose doctype declares an external entity.</summary>
    private static byte[] XxeDatasetsFormPdf()
    {
        string hostile =
            "<!DOCTYPE datasets [ <!ENTITY leak SYSTEM \"file:///C:/Windows/win.ini\"> ]>"
            + "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
            + "<xfa:data><form1><total>&leak;</total></form1></xfa:data></xfa:datasets>";

        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << >> /Annots [5 0 R 6 0 R] >>",
            Contents(PageContent),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
            + "/Rect [72 700 300 720] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (acroOnly[0]) /V () "
            + "/Rect [72 650 300 670] /P 3 0 R >>",
            "<< /Fields [5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> "
            + "/XFA [(datasets) 9 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Contents(hostile),
        });
    }

    /// <summary>The same AcroForm with no <c>/XFA</c> at all.</summary>
    private static byte[] NoXfaFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
        + "/Resources << >> /Annots [5 0 R 6 0 R] >>",
        Contents(PageContent),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (form1[0].total[0]) /V (" + Before + ") "
        + "/Rect [72 700 300 720] /P 3 0 R >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (acroOnly[0]) /V () "
        + "/Rect [72 650 300 670] /P 3 0 R >>",
        "<< /Fields [5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    });

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
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
