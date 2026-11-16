// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <c>SigningHelpers.ObjectNumberOf</c> — the one reading of "which object does this name, and can a
/// revision supersede it?", shared by <c>PdfSigner</c>, <c>VisualSignatureBuilder</c> and
/// <c>PdfLtvRefresher</c> after each carried its own copy.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> A mutation sweep over CLAUDE.md's shared decision points removed the
/// <c>obj?.ObjectNumber</c> arm — the branch that recovers the number of an object handed over
/// <i>by value</i> rather than by reference — and the whole Signing suite stayed green. Every present
/// caller passes <c>trailer[/Root]</c>, which in a well-formed file is a <see cref="PdfReference"/>,
/// so the reference arm is the only one they reach.</para>
///
/// <para><b>The branch is not dead, and that is the distinction these tests draw.</b> A caller who
/// resolved the catalog first — <c>doc.ResolveObject(trailer[/Root])</c> — holds the parsed object,
/// which the parser has stamped with its own number. Without that arm such a caller gets <c>0</c>,
/// and every caller treats <c>0</c> as "refuse": §7.5.4 reserves object 0 as the head of the free
/// list, so a catalog written there is unreachable. The refusal would be silent and the signature
/// would be dropped rather than misplaced, which is the safe direction — but it would be a refusal
/// for a document that is perfectly signable.</para>
///
/// <para>Asserted directly rather than through a signing round trip, because no round trip reaches
/// the branch: covering the function is not covering the decision, and here it is the reverse — the
/// decision has no consumer yet and the function still has to be right.</para>
/// </remarks>
public sealed class ObjectNumberOfTests
{
    [Fact]
    public void AReference_NamesTheObjectItPointsAt()
        => Assert.Equal(7, SigningHelpers.ObjectNumberOf(new PdfReference(7, 0)));

    /// <summary>
    /// A <b>resolved</b> object carries the number the parser stamped on it, and that is what the
    /// second arm recovers.
    /// </summary>
    [Fact]
    public void AResolvedObject_StillNamesItsOwnNumber()
    {
        using var doc = PdfDocument.Open(new MemoryStream(MinimalPdf()));

        PdfObject rootRef = doc.Trailer[PdfName.Root];
        PdfObject resolved = doc.ResolveObject(rootRef);

        int viaReference = SigningHelpers.ObjectNumberOf(rootRef);
        int viaResolved = SigningHelpers.ObjectNumberOf(resolved);

        Assert.Equal(1, viaReference);
        Assert.Equal(viaReference, viaResolved);
    }

    /// <summary>
    /// A genuinely direct object — one no indirect object ever held — has no number a revision could
    /// supersede, and answers 0 so every caller refuses.
    /// </summary>
    [Fact]
    public void ADirectObjectWithNoNumber_AnswersZeroSoCallersRefuse()
    {
        Assert.Equal(0, SigningHelpers.ObjectNumberOf(new PdfDictionary()));
        Assert.Equal(0, SigningHelpers.ObjectNumberOf(null));
    }

    private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[3];

        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        };

        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
