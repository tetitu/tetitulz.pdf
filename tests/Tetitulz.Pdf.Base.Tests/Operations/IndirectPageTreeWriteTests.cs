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
/// Page-tree edits on a document whose <c>/Kids</c> or <c>/Count</c> is an <b>indirect
/// reference</b>.
/// </summary>
/// <remarks>
/// <para><b>Three reads, three different ways of going wrong</b> — none of them a clean failure:</para>
/// <list type="bullet">
///   <item><description><c>DeletePage</c>'s <c>/Count</c> fell to <c>0</c> and the method then
///   <i>writes</i> <c>count - 1</c>, putting <b><c>/Count -1</c></b> on a <c>/Pages</c> node.
///   Readers navigate by <c>/Count</c>.</description></item>
///   <item><description><c>DeletePage</c>'s <c>/Kids</c> fell to <c>null</c>, which skips the
///   removal block — so the clone kept the original array and <b>the page stayed listed while
///   <c>/Count</c> was decremented anyway</b>. The tree then claims one fewer page than it
///   lists.</description></item>
///   <item><description><c>InsertBlankPage</c>'s <c>/Kids</c> fell to <c>null</c> and the very next
///   line is <c>for (int i = 0; i &lt; kids.Count; i++)</c> — a
///   <b><see cref="System.NullReferenceException"/></b>.</description></item>
/// </list>
///
/// <para>§7.3.10 permits both spellings, and a <c>/Pages</c> node whose <c>/Kids</c> array is a
/// shared object is the ordinary way a producer emits one.</para>
///
/// <para>Asserted on the page count read back through <c>PdfDocument</c>, which is what a caller
/// does next — not on "the operation returned", which is true for all three failures except the
/// crash.</para>
/// </remarks>
public class IndirectPageTreeWriteTests
{
    /// <summary>
    /// Deleting one of two pages leaves exactly one, whichever way <c>/Kids</c> and <c>/Count</c>
    /// are spelled. The direct spelling is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Kids 8 0 R  /Count 9 0 R
    [InlineData(false)]  // /Kids [3 0 R 5 0 R]  /Count 2
    public void DeletePage_LeavesAConsistentTree(bool indirect)
    {
        using var input = new MemoryStream(TwoPagePdf(indirect));
        using var output = new MemoryStream();

        PdfPageManipulator.DeletePage(input, output, 0);

        using var ms = new MemoryStream(output.ToArray());
        using var doc = PdfDocument.Open(ms);

        Assert.Single(doc.Pages);

        // And the node's own /Count agrees with what it lists — the defect wrote -1, or
        // decremented while leaving the kid in place.
        var pages = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Pages]) as PdfDictionary;
        Assert.NotNull(pages);
        int count = (doc.ResolveObject(pages![PdfName.Count]) as PdfNumber)?.IntValue ?? -999;
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Inserting a blank page yields three, whichever way <c>/Kids</c> is spelled. Under the defect
    /// the indirect case threw <see cref="System.NullReferenceException"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InsertBlankPage_AddsOnePage(bool indirect)
    {
        using var input = new MemoryStream(TwoPagePdf(indirect));
        using var output = new MemoryStream();

        PdfPageManipulator.InsertBlankPage(input, output, 1, 200, 200);

        using var ms = new MemoryStream(output.ToArray());
        using var doc = PdfDocument.Open(ms);

        Assert.Equal(3, doc.Pages.Count);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Two pages under one <c>/Pages</c> node. Objects 8 and 9 carry the <c>/Kids</c> array and the
    /// <c>/Count</c> when <paramref name="indirect"/> — the only difference between the fixtures.
    /// </summary>
    private static byte[] TwoPagePdf(bool indirect)
    {
        string kids  = indirect ? "8 0 R" : "[3 0 R 5 0 R]";
        string count = indirect ? "9 0 R" : "2";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids " + kids + " /Count " + count + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            StreamObj("q Q\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R >>",
            StreamObj("q Q\n"),
            "null",
            "[3 0 R 5 0 R]",
            "2");
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
