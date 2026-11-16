// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// An <c>/Info</c> entry written as an <b>indirect reference</b> must be read, because a metadata
/// update rewrites <c>/Info</c> from what was read.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>PdfMetadata</c> held only the dictionary — no document — so
/// <c>_infoDict[key]</c> returned the <see cref="Tetitulz.Pdf.Core.Objects.PdfReference"/>, the
/// <c>is PdfString</c> test failed, and every property read <see langword="null"/>. §7.3.10 permits
/// any value to be indirect, and a producer sharing one string object between <c>/Info</c> and its
/// XMP packet writes exactly that.</para>
///
/// <para><b>Why it destroys rather than hides.</b> <c>PdfMetadataWriter.WriteInfo</c> builds a
/// <b>brand new</b> <c>/Info</c> dictionary, repopulates it from these properties, and stamps it
/// with the <b>original object number</b> — so the appended revision supersedes the original.
/// Everything the reader could not see is dropped from the document by an operation that reports
/// success. Setting one field silently erased the others.</para>
///
/// <para>The direct spelling is the control throughout: it always worked, so a fixture built on it
/// could never have gone red.</para>
/// </remarks>
public class IndirectInfoEntryTests
{
    [Theory]
    [InlineData(true)]   // /Title 7 0 R
    [InlineData(false)]  // /Title (Quarterly Report)
    public void Title_IsReadWhicheverWayItIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(InfoPdf(indirect));
        using var doc = PdfDocument.Open(ms);

        Assert.Equal("Quarterly Report", doc.Metadata.Title);
    }

    /// <summary>
    /// The other half of the control: a document with no <c>/Title</c> really does report null, so
    /// the assertion above cannot be satisfied by something that returns a constant — and null is
    /// exactly the answer the defect produced.
    /// </summary>
    [Fact]
    public void NoTitle_ReportsNull()
    {
        using var ms = new MemoryStream(InfoPdf(indirect: false, omitTitle: true));
        using var doc = PdfDocument.Open(ms);

        Assert.Null(doc.Metadata.Title);
    }

    // ------------------------------------------------------------------

    private static byte[] InfoPdf(bool indirect, bool omitTitle = false)
    {
        string title = omitTitle ? "" : (indirect ? " /Title 7 0 R" : " /Title (Quarterly Report)");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            StreamObj("q Q\n"),
            "<< /Author (A. Nalyst)" + title + " >>",
            "null",
            "(Quarterly Report)",
        };

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
          .Append(" /Root 1 0 R /Info 5 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";
}
