// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A repaired file's second line must be the §7.5.2 binary marker: a comment whose four bytes are
/// all <b>greater than 127</b>.
/// </summary>
/// <remarks>
/// <para><b>Every repaired document carried <c>%????</c> instead.</b> The source literal
/// <c>"%\xE2\xE3\xCF\xD3\n"</c> is correct — those are the four high chars §7.5.2 wants — but it
/// went through <c>WriteRaw</c>, which encodes with <see cref="System.Text.Encoding.ASCII"/>, and
/// ASCII maps every char above 0x7F to <c>'?'</c> (0x3F). The marker reaching the file therefore had
/// four bytes all UNDER 128, saying the opposite of what it exists to say.</para>
///
/// <para><b>The chars were right and the encoder discarded them</b>, which is why it survived
/// review: the literal reads correctly at the call site, and nothing downstream re-reads the
/// marker. §7.5.2's purpose is telling a transfer tool the file is binary so it does not translate
/// newlines — so the damage lands outside this library, on a code path whose whole job is producing
/// a readable file.</para>
///
/// <para>The assertion is on the emitted bytes and their numeric value, because that is the entire
/// content of the requirement. A test that looked for the <i>characters</i> would pass against the
/// broken output in any encoding that round-trips <c>'?'</c>.</para>
/// </remarks>
public class RepairedFileBinaryMarkerTests
{
    [Fact]
    public void ARepairedFileCarriesFourBytesAboveOneTwentySeven()
    {
        byte[] repaired = Repair();

        // Line 1 is "%PDF-x.y"; line 2 is the marker.
        int firstNewline = System.Array.IndexOf(repaired, (byte)'\n');
        Assert.True(firstNewline > 0, "the repaired file has no header line");

        Assert.Equal((byte)'%', repaired[firstNewline + 1]);

        byte[] marker = repaired.Skip(firstNewline + 2).Take(4).ToArray();
        Assert.All(marker, b => Assert.True(b > 127,
            $"§7.5.2 requires four bytes above 127; got 0x{b:X2}"));
    }

    /// <summary>
    /// And specifically not the question marks ASCII substitution produced.
    /// </summary>
    /// <remarks>
    /// Named separately from the numeric assertion because it is the observed failure rather than
    /// the rule: <c>'?'</c> is 0x3F, so the case above already covers it — but a reader diagnosing a
    /// regression wants to see the actual bytes that shipped.
    /// </remarks>
    [Fact]
    public void TheMarkerIsNotAsciiQuestionMarks()
    {
        Assert.DoesNotContain("%????", Encoding.Latin1.GetString(Repair()));
    }

    /// <summary>
    /// The control: the rest of the file is still plain ASCII PDF syntax, so the fix did not change
    /// how anything else is written.
    /// </summary>
    /// <remarks>
    /// <c>WriteRaw</c> keeps its ASCII encoder deliberately — everything else it writes is PDF
    /// syntax, and an encoder that silently substituted there would hide a different bug. Without
    /// this, "switch WriteRaw to Latin-1" satisfies the cases above while widening what can be
    /// written unnoticed.
    /// </remarks>
    [Fact]
    public void TheHeaderAndTrailerRemainAscii()
    {
        string text = Encoding.Latin1.GetString(Repair());

        Assert.StartsWith("%PDF-", text);
        Assert.Contains("trailer", text);
        Assert.Contains("startxref", text);
        Assert.EndsWith("%%EOF\n", text);
    }

    // ------------------------------------------------------------------

    private static byte[] Repair()
    {
        using var input = new MemoryStream(DamagedPdf(), writable: false);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);
        return output.ToArray();
    }

    /// <summary>A file whose startxref offset is wrong, so repair rebuilds it by scanning.</summary>
    private static byte[] DamagedPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.Append("startxref\n999999\n%%EOF\n");   // deliberately wrong
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
