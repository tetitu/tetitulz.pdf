// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A resource sub-dictionary written as an <b>indirect reference</b> must be merged into, not
/// replaced.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>MergeResources</c> tested <c>merged[key] is PdfDictionary</c> on the
/// raw value. One <c>/Font</c> object shared by every page — the ordinary way to build a
/// document — is a <see cref="PdfReference"/>, so the test failed and the <c>else</c> branch
/// <b>replaced</b> the page's whole sub-dictionary with the incoming one.</para>
///
/// <para><b>So stamping a watermark removed every font the page already used</b>, leaving its own
/// text operators naming resources that no longer exist. The same shape sat in
/// <c>PdfFormFlattener</c> on <c>/XObject</c>: flattening a form on such a page dropped every image
/// and form XObject it drew.</para>
///
/// <para><b>Asserted on the surviving resource name.</b> The output is a valid, larger PDF with a
/// real watermark in it either way — the page's own font is simply gone from its resource
/// dictionary. Nothing about file size or validity separates the two.</para>
/// </remarks>
public class IndirectResourceSubDictionaryTests
{
    /// <summary>
    /// Appending content that brings its own font keeps the page's existing font. The direct
    /// spelling is the control: it always worked, so a fixture built on it could never go red.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Font 7 0 R
    [InlineData(false)]  // /Font << /PageFont 6 0 R >>
    public void AppendingAFont_KeepsThePagesOwnFont(bool indirect)
    {
        using var input = new MemoryStream(PageWithFontPdf(indirect));
        using var output = new MemoryStream();

        PdfPageContentAppender.AppendContent(
            input, output, 0, b => b.AddStandardFont("Helvetica"));

        PdfDictionary fonts = PageFontDictionary(output.ToArray());

        // The page's own font survived...
        Assert.NotNull(fonts[new PdfName("PageFont")]);

        // ...and something was actually added, so the merge really ran.
        Assert.True(fonts.Count >= 2, "the append contributed no font; the fixture never reached the merge");
    }

    // ------------------------------------------------------------------

    private static PdfDictionary PageFontDictionary(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        Assert.NotNull(resources);

        var fonts = doc.ResolveObject(resources![PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        return fonts!;
    }

    /// <summary>
    /// A page whose <c>/Resources /Font</c> holds one font. When <paramref name="indirect"/> that
    /// sub-dictionary is object 7 and <c>/Font</c> points at it — the only difference between the
    /// two fixtures.
    /// </summary>
    private static byte[] PageWithFontPdf(bool indirect)
    {
        string font = indirect ? "7 0 R" : "<< /PageFont 6 0 R >>";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font " + font + " >> >>",
            StreamObj("BT /PageFont 12 Tf (hi) Tj ET\n"),
            "null",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
            "<< /PageFont 6 0 R >>");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

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
