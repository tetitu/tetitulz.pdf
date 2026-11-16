// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A field whose <c>/FT</c> resolves nowhere in the chain is <see cref="PdfFieldType.Unknown"/> —
/// "the file did not say what this is" — and must not be filled.
/// </summary>
/// <remarks>
/// <para><b>It was filled as variable text.</b> <c>PdfFormFiller.ApplyValue</c>'s switch had arms
/// for <c>Signature</c> (warn and refuse) and <c>Button</c>, and everything else fell into
/// <c>default:</c>, which is the variable-text arm. So <c>Unknown</c> — the one value that means
/// the type is not known — was treated as the one type it is least safe to assume, and a
/// <c>/V</c> string was written onto the node.</para>
///
/// <para><b>That is verbatim the damage the downward widget-kid repair exists to prevent</b>, as
/// <c>ResolveFieldType</c>'s own doc comment says: a text value laid over what may well be a
/// signature field. The refusal arm for <c>Signature</c> was already there; the gap was that a
/// field nobody could type slipped past it.</para>
///
/// <para><b>The population of <c>Unknown</c> grew immediately before this landed.</b> Gating the
/// downward repair on "the kid is a bare widget" (commit 039a7c2) correctly stopped a grouping node
/// taking its type from its own child fields — which moved exactly that node from <c>Signature</c>,
/// where the filler refused it, to <c>Unknown</c>, where the filler wrote to it. The gate is right
/// and this is its other half.</para>
///
/// <para>The assertions are on the written bytes: the field's <c>/V</c> must be absent from the
/// output. A warning alone would pass with the write still happening.</para>
/// </remarks>
public class UntypedFieldFillRefusalTests
{
    [Fact]
    public void AFieldWithNoResolvableTypeIsNotFilled()
    {
        byte[] output = Fill(out FormFillResult result);

        Assert.DoesNotContain("(typed in)", Latin1(output));
        Assert.Empty(result.FilledFields);
    }

    /// <summary>The refusal is reported, not silent.</summary>
    /// <remarks>
    /// Returning nothing and saying nothing is the failure mode this repository's diagnostics
    /// pattern exists to prevent: a caller cannot tell "there was nothing to do" from "something
    /// was refused".
    /// </remarks>
    [Fact]
    public void TheRefusalIsReported()
    {
        Fill(out FormFillResult result);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("unknown", warning);
        Assert.Contains("group", warning);
    }

    /// <summary>
    /// The control: an ordinary text field with a resolvable <c>/FT /Tx</c> is still filled.
    /// </summary>
    /// <remarks>
    /// Without it, "refuse everything" satisfies both cases above.
    /// </remarks>
    [Fact]
    public void AnOrdinaryTextFieldIsStillFilled()
    {
        using var input = new MemoryStream(TypedTextFieldPdf(), writable: false);
        using var output = new MemoryStream();

        FormFillResult result = PdfFormFiller.Fill(
            input, output, new Dictionary<string, string> { ["name"] = "typed in" }, null);

        Assert.Equal(new[] { "name" }, result.FilledFields);
        Assert.Contains("(typed in)", Latin1(output.ToArray()));
    }

    /// <summary>
    /// The floor for the <b>type</b> of refusal: a signature field is refused for its own reason,
    /// not for this one.
    /// </summary>
    /// <remarks>
    /// Without this, folding <c>Unknown</c> and <c>Signature</c> into one arm would satisfy every
    /// case above while telling a caller the wrong thing about why nothing was written.
    /// </remarks>
    [Fact]
    public void ASignatureFieldIsStillRefusedForItsOwnReason()
    {
        using var input = new MemoryStream(TypedSignatureFieldPdf(), writable: false);
        using var output = new MemoryStream();

        FormFillResult result = PdfFormFiller.Fill(
            input, output, new Dictionary<string, string> { ["sig"] = "typed in" }, null);

        string warning = Assert.Single(result.Warnings);
        Assert.Contains("signature fields are not filled here", warning);
        Assert.DoesNotContain("unknown", warning);
    }

    // ------------------------------------------------------------------

    private static byte[] Fill(out FormFillResult result)
    {
        using var input = new MemoryStream(UntypedGroupPdf(), writable: false);
        using var output = new MemoryStream();

        result = PdfFormFiller.Fill(
            input, output, new Dictionary<string, string> { ["group"] = "typed in" }, null);
        return output.ToArray();
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>
    /// A <b>terminal</b> field — no <c>/Kids</c>, so the filler reaches it — carrying no
    /// <c>/FT</c>, and with no ancestor to inherit one from.
    /// </summary>
    /// <remarks>
    /// A group node cannot be the fixture here even though it is the shape that grew the
    /// <c>Unknown</c> population: the filler only writes to terminal fields, so a non-terminal one
    /// never reaches <c>ApplyValue</c> and comes back as <c>UnmatchedFields</c> instead. The hazard
    /// is the same and this is the shape that exercises it.
    /// </remarks>
    private static byte[] UntypedGroupPdf() => Build(
        Catalog(), PageTree(), Page(), EmptyStream(), Resources(),
        "<< /T (group) /Rect [10 10 200 30] >>");

    private static byte[] TypedTextFieldPdf() => Build(
        Catalog(), PageTree(), Page(), EmptyStream(), Resources(),
        "<< /T (name) /FT /Tx /Rect [10 10 200 30] >>");

    private static byte[] TypedSignatureFieldPdf() => Build(
        Catalog(), PageTree(), Page(), EmptyStream(), Resources(),
        "<< /T (sig) /FT /Sig /Rect [10 10 200 30] >>");

    private static string Catalog() =>
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>";
    private static string PageTree() => "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private static string Page() =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources 5 0 R >>";
    private static string EmptyStream() => "<< /Length 0 >>\nstream\n\nendstream";
    private static string Resources() => "<< >>";

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
