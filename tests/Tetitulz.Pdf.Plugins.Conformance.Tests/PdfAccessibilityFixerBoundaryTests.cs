// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAccessibilityFixer"/>'s pass-throughs, what its log says it did, the cap on
/// per-field warnings, and the trailer of the revision it appends.
/// </summary>
public class PdfAccessibilityFixerBoundaryTests
{
    // ------------------------------------------------------------------ pass-throughs

    /// <summary>
    /// An encrypted document is passed through byte for byte with a warning: the fixer has no key,
    /// and objects appended in the clear would be unreadable next to the encrypted ones. The
    /// control is the same document unencrypted, which is fixed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncryptedDocument_IsPassedThroughUntouched(bool encrypted)
    {
        string trailerExtra = encrypted
            ? " /Encrypt << /Filter /Standard /V 2 /R 3 /Length 128 /P -4 /O <" + new string('0', 64) +
              "> /U <" + new string('0', 64) + "> >> /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]"
            : "";
        byte[] input = Pdf(trailerExtra,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        (AccessibilityFixResult result, byte[] output) = Fix(input);

        if (encrypted)
        {
            Assert.Equal(input, output);
            Assert.Contains("Document is encrypted; no fixes applied.", result.Warnings);
            Assert.Empty(result.FixesApplied);
        }
        else
        {
            Assert.True(output.Length > input.Length, "the unencrypted control must be fixed");
            Assert.NotEmpty(result.FixesApplied);
        }
    }

    // ------------------------------------------------------------------ what the log says

    /// <summary>
    /// The log tells a repaired <c>/Lang</c> from an added one: an empty <c>/Lang ()</c> was
    /// replaced, a missing one was added.
    /// </summary>
    [Theory]
    [InlineData(" /Lang ()", "Replaced an empty /Lang with \"en-US\" in the catalog.")]
    [InlineData("", "Added /Lang = \"en-US\" to catalog.")]
    public void LanguageFix_IsLoggedAsWhatItWas(string catalogLang, string expected)
    {
        byte[] input = Pdf("",
            "<< /Type /Catalog /Pages 2 0 R" + catalogLang + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        (AccessibilityFixResult result, _) = Fix(input);

        Assert.Contains(expected, result.FixesApplied);
    }

    /// <summary>
    /// A document already carrying <c>/DisplayDocTitle true</c> is not "fixed" again — nothing is
    /// logged and, with nothing else to do, nothing is appended. The control is <c>false</c>.
    /// </summary>
    [Theory]
    [InlineData("true", false)]
    [InlineData("false", true)]
    public void DisplayDocTitle_AlreadySet_IsLeftAlone(string value, bool fixedUp)
    {
        byte[] input = Pdf("",
            "<< /Type /Catalog /Pages 2 0 R /Lang (en) /ViewerPreferences << /DisplayDocTitle " + value + " >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        (AccessibilityFixResult result, byte[] output) = Fix(input);

        Assert.Equal(fixedUp, result.FixesApplied.Contains("Set /ViewerPreferences /DisplayDocTitle = true."));
        Assert.Equal(fixedUp, output.Length > input.Length);
    }

    /// <summary>
    /// Per-field warnings stop at 25: with 25 fields every one is listed and nothing is
    /// suppressed; with 26 the 26th is counted as suppressed instead.
    /// </summary>
    [Theory]
    [InlineData(25, 0)]
    [InlineData(26, 1)]
    public void FieldWarnings_AreCappedAtTwentyFive(int fields, int suppressed)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en) /ViewerPreferences << /DisplayDocTitle true >> /AcroForm << /Fields ["
                + string.Join(" ", Enumerable.Range(0, fields).Select(i => (4 + i).ToString(CultureInfo.InvariantCulture) + " 0 R"))
                + "] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };
        for (int i = 0; i < fields; i++)
            bodies.Add("<< /FT /Tx /T (f" + i.ToString(CultureInfo.InvariantCulture) + ") >>");

        (AccessibilityFixResult result, _) = Fix(Pdf("", bodies.ToArray()));

        int perField = result.Warnings.Count(w => w.StartsWith("Form field \"f", StringComparison.Ordinal));
        Assert.Equal(25, perField);
        Assert.Equal(suppressed == 0 ? 0 : 1,
            result.Warnings.Count(w => w == suppressed + " further form-field tooltip warning(s) were suppressed after the first 25."));
        Assert.DoesNotContain(result.Warnings, w => suppressed == 0 && w.Contains("suppressed"));
    }

    // ------------------------------------------------------------------ the appended trailer

    /// <summary>
    /// The appended revision's trailer names the section it supersedes — <c>/Prev</c> is the
    /// original <c>startxref</c> — and its <c>/Size</c> is one past the highest object number.
    /// </summary>
    [Fact]
    public void AppendedTrailer_ChainsToTheOriginalSectionAndCountsEveryObject()
    {
        byte[] input = Pdf("",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Producer (five objects) >>",
            "<< /Unrelated true >>");
        long originalStartXref = LastStartXref(input);

        (_, byte[] output) = Fix(input);

        string tail = Encoding.Latin1.GetString(output, input.Length, output.Length - input.Length);
        Match trailer = Regex.Match(tail, @"trailer\s*<<(.*?)>>\s*startxref", RegexOptions.Singleline);
        Assert.True(trailer.Success, "the appended revision has a classic trailer");

        Assert.Equal(originalStartXref, Number(trailer.Groups[1].Value, "Prev"));
        Assert.Equal(6, Number(trailer.Groups[1].Value, "Size"));
    }

    /// <summary>
    /// A synthesised <c>/ID</c> is exactly the Core writers': SHA-256 of the original bytes, split
    /// by <see cref="PdfFileId.FromDigest"/>. Pinned as a value so the fixer cannot drift from
    /// the writer it now leaves the job to.
    /// </summary>
    [Fact]
    public void SynthesisedId_IsTheCoreWritersId()
    {
        byte[] input = Pdf("",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        (_, byte[] output) = Fix(input);

        PdfArray expected;
        using (var sha = SHA256.Create())
            expected = PdfFileId.FromDigest(sha.ComputeHash(input));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var id = (PdfArray)doc.ResolveObject(doc.Trailer[PdfName.ID]);
        Assert.Equal(((PdfString)expected[0]).RawBytes, ((PdfString)id[0]).RawBytes);
        Assert.Equal(((PdfString)expected[1]).RawBytes, ((PdfString)id[1]).RawBytes);
    }

    // ------------------------------------------------------------------ fixtures

    private static (AccessibilityFixResult Result, byte[] Output) Fix(byte[] input)
    {
        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        AccessibilityFixResult result = PdfAccessibilityFixer.Fix(inStream, outStream, new AccessibilityFixOptions());
        return (result, outStream.ToArray());
    }

    private static long LastStartXref(byte[] pdf)
    {
        MatchCollection all = Regex.Matches(Encoding.Latin1.GetString(pdf), @"startxref\s+(\d+)");
        return long.Parse(all[all.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static long Number(string dictionary, string key)
    {
        Match m = Regex.Match(dictionary, "/" + key + @"\s+(\d+)");
        Assert.True(m.Success, "/" + key + " is missing from the appended trailer");
        return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Object <c>i + 1</c> is <paramref name="bodies"/>[i]; object 1 is the root.</summary>
    private static byte[] Pdf(string trailerExtra, params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(trailerExtra).Append(" >>\n");
        sb.Append("startxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
