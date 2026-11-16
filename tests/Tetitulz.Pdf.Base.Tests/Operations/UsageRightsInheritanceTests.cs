// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>PdfUsageRightsReader</c> must find a Reader-Extension (UR3) signature whose <c>/FT</c> and
/// <c>/V</c> are on <b>different</b> nodes of the field tree — Table 220 makes both inheritable.
/// </summary>
/// <remarks>
/// <b>It required them on the same dictionary, and returned an empty list otherwise.</b> The common
/// shape is <c>&lt;&lt; /T (Sig1) /V 4 0 R /Kids [widget carrying /FT /Sig] &gt;&gt;</c>: the group
/// has no <c>/FT</c>, so the <c>ft == "Sig"</c> gate skipped it; the widget has no <c>/V</c> —
/// deliberately, since a widget showing its parent's value is not a second signature — so it was
/// skipped too. Nothing matched, and <c>GetUsageRights</c> reported <b>no rights at all for a
/// document that grants them</b>.
///
/// <para><b>The file's own comment already described this consequence.</b> Two lines above the gate
/// it says an unresolved <c>/FT</c> means "tooling gated on that answer refuses work the document
/// permits". Resolving <c>/FT</c> closed half the defect; the inheritance half stayed live — the
/// repository's documented "landed one level short" shape, and the fourth instance of it found in
/// this area (<c>PdfSignatureDetection</c>, <c>PdfFontEmbedder</c>'s PUA fold, the <c>/FT</c>
/// climb, and now this).</para>
///
/// <para><b>The assertions are on the extracted rights, not on the list being non-empty.</b> An
/// empty list is the correct answer for most PDFs, which is exactly why a count assertion would
/// pass with the feature deleted.</para>
/// </remarks>
public class UsageRightsInheritanceTests
{
    /// <summary>The group carries <c>/T</c> and <c>/V</c>; its only widget kid carries <c>/FT</c>.</summary>
    [Fact]
    public void RightsAreFoundWhenTheTypeSitsOnTheWidgetAndTheValueOnTheGroup()
    {
        IReadOnlyList<UsageRightsEntry> entries = Read(Build(
            "<< /T (Sig1) /V 4 0 R /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [0 0 10 10] >>"));

        UsageRightsEntry entry = Assert.Single(entries);
        Assert.Equal(UsageRights.FormFillIn, entry.Rights & UsageRights.FormFillIn);
        Assert.Equal("Split Signer", entry.SignerName);
        Assert.Equal("SHA256", entry.DigestMethod);
    }

    /// <summary>
    /// The mirror: the group carries <c>/FT /Sig</c> and the widget kid carries the <c>/V</c>.
    /// </summary>
    /// <remarks>
    /// Both directions are needed and only one of them kills the obvious mutant: with <c>/FT</c> on
    /// the widget, dropping <b>type</b> inheritance changes nothing, because the widget supplies its
    /// own. This is the case that requires <c>/V</c> to travel too.
    /// </remarks>
    [Fact]
    public void RightsAreFoundWhenTheTypeSitsOnTheGroupAndTheValueOnItsWidget()
    {
        IReadOnlyList<UsageRightsEntry> entries = Read(Build(
            "<< /T (Sig1) /FT /Sig /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /V 4 0 R /Rect [0 0 10 10] >>"));

        UsageRightsEntry entry = Assert.Single(entries);
        Assert.Equal(UsageRights.FormFillIn, entry.Rights & UsageRights.FormFillIn);
        Assert.Equal("Split Signer", entry.SignerName);
    }

    /// <summary>
    /// The merged shape — both entries on one dictionary, which is what always worked — must still
    /// yield <b>exactly one</b> entry even though its widget kid now inherits both.
    /// </summary>
    /// <remarks>
    /// This is the floor that makes inheritance safe rather than merely wider. Without the
    /// signature-identity dedup the widget matches too and one signature's rights are reported
    /// twice; a caller counting entries would read a singly-extended document as doubly so.
    /// </remarks>
    [Fact]
    public void AMergedFieldWithAWidgetKidStillYieldsOneEntry()
    {
        IReadOnlyList<UsageRightsEntry> entries = Read(Build(
            "<< /T (Sig1) /FT /Sig /V 4 0 R /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] >>"));

        Assert.Single(entries);
    }

    /// <summary>
    /// A signature field with <b>no</b> value anywhere in the chain grants nothing: an unsigned
    /// placeholder is not a Reader Extension.
    /// </summary>
    /// <remarks>
    /// Without this, "carry something down and always extract" satisfies every case above.
    /// </remarks>
    [Fact]
    public void AnUnsignedPlaceholderGrantsNoRights()
    {
        Assert.Empty(Read(Build(
            "<< /T (Sig1) /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /FT /Sig /Rect [0 0 10 10] >>")));
    }

    /// <summary>
    /// A value inherited under a type that is <b>not</b> <c>/Sig</c> is not a signature: a text
    /// field's value must not be read as a Reader Extension.
    /// </summary>
    [Fact]
    public void AnInheritedValueUnderANonSignatureTypeGrantsNoRights()
    {
        Assert.Empty(Read(Build(
            "<< /T (Name) /FT /Tx /V 4 0 R /Kids [7 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [0 0 10 10] >>")));
    }

    /// <summary>A <c>/Kids</c> cycle must not hang the descent. Legal to write, illegal to follow.</summary>
    [Fact]
    public void ACyclicFieldTreeTerminates()
    {
        Assert.Empty(Read(Build(
            "<< /T (root) /Kids [7 0 R] >>",
            "<< /T (loop) /Parent 6 0 R /Kids [7 0 R] >>")));
    }

    // ------------------------------------------------------------------

    private static IReadOnlyList<UsageRightsEntry> Read(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfUsageRightsReader.Read(ms);
    }

    /// <summary>
    /// Objects 1-3 are the page tree, 4 the UR3 signature dictionary, 5 the AcroForm; the caller
    /// supplies objects 6 onward, which are the field nodes.
    /// </summary>
    private static byte[] Build(params string[] fieldNodes)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
                + "/Name (Split Signer) "
                + "/Reference [<< /Type /SigRef /TransformMethod /UR3 /DigestMethod /SHA256 "
                + "/TransformParams << /Type /TransformParams /V /2.2 /Form [/FillIn] >> >>] >>",
            "<< /Fields [6 0 R] >>",
        };
        bodies.AddRange(fieldNodes);

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
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
