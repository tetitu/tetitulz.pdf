// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <c>SanitizeOptions.RemoveXfa</c> must remove the XFA <b>payload</b>, not only the
/// <c>/AcroForm /XFA</c> key.
/// </summary>
/// <remarks>
/// <para><b>The same defect the embedded-file sweep had, one key over.</b>
/// <c>PdfSanitizer.RemoveXfaFromCatalog</c> dropped the key and stopped. Step 5 of
/// <c>Sanitize</c> writes every object the parser enumerated — it is not a reachability walk — so
/// the XFA packet streams were still emitted, every byte intact, merely unreferenced. The option's
/// own documentation says XFA "can execute arbitrary script", and the script went out in the
/// "clean" file; a scanner reading the output still hits on it, and a caller who removed the key
/// because of what the packet contains did not remove what the packet contains.</para>
///
/// <para><b>Asserted on the output bytes, not the object graph.</b> An object-graph assertion is
/// exactly the assertion that passed while the defect was live: the key really was gone. Only
/// reading the emitted bytes can tell "unreferenced" from "not there".</para>
/// </remarks>
public class SanitizerXfaPayloadTests
{
    private const string DatasetsPayload = "XFA-DATASETS-SECRET-ANSWER";
    private const string TemplatePayload = "app.alert('xfa-template-script')";

    /// <summary>
    /// The packet bytes do not reach the sanitized output.
    /// </summary>
    [Fact]
    public void SanitizeRemovesTheXfaPacketBytesAndNotJustTheKey()
    {
        byte[] pdf = XfaFormPdf();

        // The premise: both packets are in the input. Without this the test would pass against a
        // fixture that never carried them.
        Assert.Contains(DatasetsPayload, Latin1(pdf));
        Assert.Contains(TemplatePayload, Latin1(pdf));

        byte[] cleaned = Sanitize(pdf, new SanitizeOptions { RemoveXfa = true });

        Assert.DoesNotContain(DatasetsPayload, Latin1(cleaned));
        Assert.DoesNotContain(TemplatePayload, Latin1(cleaned));
    }

    /// <summary>The key goes too — the behaviour that was already correct stays correct.</summary>
    [Fact]
    public void SanitizeStillRemovesTheXfaKeyFromTheAcroForm()
    {
        byte[] cleaned = Sanitize(XfaFormPdf(), new SanitizeOptions { RemoveXfa = true });

        using var output = new MemoryStream(cleaned);
        using var doc = PdfDocument.Open(output);
        var catalog = doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary;
        var acroForm = doc.ResolveObject(catalog![PdfName.AcroForm]) as PdfDictionary;

        Assert.NotNull(acroForm);
        Assert.False(XfaPacket.IsPresent(acroForm));
    }

    /// <summary>
    /// The acceptance control: with the option off, the packet survives untouched.
    /// </summary>
    /// <remarks>
    /// Without this, "the payload is gone" would be satisfied by a sanitizer that blanked XFA
    /// streams unconditionally — which is a different, worse behaviour wearing the same green tick.
    /// </remarks>
    [Fact]
    public void WithRemoveXfaOffThePacketIsLeftAlone()
    {
        byte[] cleaned = Sanitize(XfaFormPdf(), new SanitizeOptions { RemoveXfa = false });

        Assert.Contains(DatasetsPayload, Latin1(cleaned));

        using var output = new MemoryStream(cleaned);
        using var doc = PdfDocument.Open(output);
        var catalog = doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary;
        var acroForm = doc.ResolveObject(catalog![PdfName.AcroForm]) as PdfDictionary;

        Assert.True(XfaPacket.IsPresent(acroForm));
    }

    /// <summary>Dropping the packet is reported, so a caller can say the document was an XFA form.</summary>
    /// <remarks>
    /// The removal is destructive and silent otherwise: after sanitizing, nothing in the output
    /// distinguishes "this was never an XFA form" from "this was one and its form definition was
    /// discarded". <c>PdfSplitter.LastXfaWarning</c> was the only such channel in the tree, and only
    /// after a split.
    /// </remarks>
    [Fact]
    public void DroppingTheXfaPacketIsReported()
    {
        Sanitize(XfaFormPdf(), new SanitizeOptions { RemoveXfa = true });

        Assert.NotNull(PdfSanitizer.LastXfaWarning);
        Assert.Contains("/XFA", PdfSanitizer.LastXfaWarning!, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A document with no XFA reports nothing — and reports nothing even after a run that did.
    /// </summary>
    /// <remarks>
    /// The second half is the reset: a slot a caller reads after the operation must be cleared at
    /// the start of it, or the previous document's verdict is returned for this one.
    /// </remarks>
    [Fact]
    public void AFormWithNoXfaReportsNothingEvenAfterOneThatDid()
    {
        Sanitize(XfaFormPdf(), new SanitizeOptions { RemoveXfa = true });
        Assert.NotNull(PdfSanitizer.LastXfaWarning);

        Sanitize(NoXfaPdf(), new SanitizeOptions { RemoveXfa = true });

        Assert.Null(PdfSanitizer.LastXfaWarning);
    }

    // ------------------------------------------------------------------

    private static byte[] Sanitize(byte[] pdf, SanitizeOptions options)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, options);
        return output.ToArray();
    }

    private static string Latin1(byte[] data) => Encoding.GetEncoding("iso-8859-1").GetString(data);

    /// <summary>
    /// A one-page form whose <c>/AcroForm /XFA</c> is the <b>array</b> form — the shape a common viewer
    /// writes, and the one nothing in this tree resolved before <see cref="XfaPacket"/>.
    /// </summary>
    private static byte[] XfaFormPdf()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        const string content = "BT /F1 12 Tf 100 700 Td (form) Tj ET";
        string datasets = "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
                          + "<xfa:data><form1><answer>" + DatasetsPayload
                          + "</answer></form1></xfa:data></xfa:datasets>";
        string template = "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\">"
                          + "<script>" + TemplatePayload + "</script></template>";

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                   + "/Contents 7 0 R /Resources << >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Fields [] /XFA [(datasets) 5 0 R (template) 6 0 R] >>\nendobj\n");

        int off5 = pdf.Length;
        pdf.AppendFormat("5 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            datasets.Length, datasets);

        int off6 = pdf.Length;
        pdf.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            template.Length, template);

        int off7 = pdf.Length;
        pdf.AppendFormat("7 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);

        int xref = pdf.Length;
        pdf.Append("xref\n0 8\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.AppendFormat("{0:D10} 00000 n \n", off6);
        pdf.AppendFormat("{0:D10} 00000 n \n", off7);
        pdf.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>The same form with the <c>/XFA</c> entry and its two streams removed.</summary>
    private static byte[] NoXfaPdf()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        const string content = "BT /F1 12 Tf 100 700 Td (form) Tj ET";

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>\nendobj\n");

        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                   + "/Contents 5 0 R /Resources << >> >>\nendobj\n");

        int off4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Fields [] >>\nendobj\n");

        int off5 = pdf.Length;
        pdf.AppendFormat("5 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);

        int xref = pdf.Length;
        pdf.Append("xref\n0 6\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", off1);
        pdf.AppendFormat("{0:D10} 00000 n \n", off2);
        pdf.AppendFormat("{0:D10} 00000 n \n", off3);
        pdf.AppendFormat("{0:D10} 00000 n \n", off4);
        pdf.AppendFormat("{0:D10} 00000 n \n", off5);
        pdf.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
