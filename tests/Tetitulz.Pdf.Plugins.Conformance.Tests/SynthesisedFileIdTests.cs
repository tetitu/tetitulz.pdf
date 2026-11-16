// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The <c>/ID</c> a remediation writes onto an <b>appended revision</b> must have two
/// <i>different</i> halves (ISO 32000-1 §14.4).
/// </summary>
/// <remarks>
/// <para><b>Both halves were the same 16 bytes</b>, under a comment reasoning that "with no
/// pre-existing permanent identifier there is nothing for the first element to preserve". §14.4
/// does not work that way: the first element is the permanent identifier, fixed at creation, and
/// the second changes whenever the file is modified. Equal halves therefore assert <b>"never
/// modified since creation"</b> — written onto the trailer of an appended revision, which is a
/// modification by construction. The remediated document made a false claim about its own
/// history.</para>
///
/// <para><b>And Core disagreed on the same bytes.</b> <c>PdfFileId.FromDigest</c> splits the digest
/// into <c>[0..16]</c> and <c>[16..32]</c>, so which <c>/ID</c> a reader saw depended on which
/// writer ran. The plugin could not reach that rule while it was <c>internal</c> to Core, which is
/// why it had a copy at all.</para>
/// </remarks>
public class SynthesisedFileIdTests
{
    [Fact]
    public void TheTwoHalvesOfASynthesisedIdDiffer()
    {
        (PdfString first, PdfString second) = RemediatedFileId();

        Assert.Equal(16, first.RawBytes.Length);
        Assert.Equal(16, second.RawBytes.Length);
        Assert.False(first.RawBytes.SequenceEqual(second.RawBytes),
            "equal halves assert 'never modified since creation' on an appended revision");
    }

    /// <summary>
    /// The floor: it is still a <b>deterministic</b> function of the content, so two runs over one
    /// file produce one file.
    /// </summary>
    /// <remarks>
    /// Without this, "make the halves differ" is satisfied by a random second element — which would
    /// break byte-comparison of two remediations of the same input, and is exactly what
    /// <c>PdfFileId</c>'s own doc rules out.
    /// </remarks>
    [Fact]
    public void TheIdIsDeterministicForTheSameInput()
    {
        (PdfString firstA, PdfString secondA) = RemediatedFileId();
        (PdfString firstB, PdfString secondB) = RemediatedFileId();

        Assert.True(firstA.RawBytes.SequenceEqual(firstB.RawBytes));
        Assert.True(secondA.RawBytes.SequenceEqual(secondB.RawBytes));
    }

    /// <summary>
    /// And it is a function of the <b>content</b>: a different document gets a different identifier.
    /// </summary>
    /// <remarks>
    /// The other floor. Without it, a constant would satisfy both cases above and every remediated
    /// document in a corpus would claim the same identity.
    /// </remarks>
    [Fact]
    public void ADifferentDocumentGetsADifferentId()
    {
        (PdfString firstA, _) = RemediatedFileId("Alpha");
        (PdfString firstB, _) = RemediatedFileId("Beta");

        Assert.False(firstA.RawBytes.SequenceEqual(firstB.RawBytes));
    }

    // ------------------------------------------------------------------

    private static (PdfString First, PdfString Second) RemediatedFileId(string title = "Alpha")
    {
        using var input = new MemoryStream(UntaggedPdf(title), writable: false);
        using var output = new MemoryStream();
        PdfAccessibilityFixer.Fix(input, output, new AccessibilityFixOptions());

        byte[] fixedBytes = output.ToArray();
        Assert.True(fixedBytes.Length > 0, "the fixer produced no output");

        using var read = new MemoryStream(fixedBytes, writable: false);
        using var doc = PdfDocument.Open(read);

        var id = doc.ResolveObject(doc.Trailer[PdfName.ID]) as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id.Count);

        var first = doc.ResolveObject(id[0]) as PdfString;
        var second = doc.ResolveObject(id[1]) as PdfString;
        Assert.NotNull(first);
        Assert.NotNull(second);
        return (first, second);
    }

    /// <summary>A document with a form field lacking /TU, so the fixer has something to write.</summary>
    private static byte[] UntaggedPdf(string title)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R "
                + "/Annots [6 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (" + title + ") /Rect [10 10 200 30] >>",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        // No /ID: that is what makes the fixer synthesise one.
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
