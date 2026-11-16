// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature whose coverage stops just before a file's <c>%%EOF</c> line ending counts as
/// reaching the end of the file, so nothing appended after it can be a change to what it locked.
/// The analyser asks whether coverage reaches the end by passing the signature's coverage end to
/// <c>SignedByteRanges.ReachesEndOfFile</c>; this pins the caller's argument at the two-byte CRLF tail.
/// </summary>
/// <remarks>
/// The fixture is a real <c>PdfSigner</c> output with two byte-level edits, neither of which moves an
/// offset the file's cross-reference depends on: a carriage return is inserted before the final
/// line feed (after every offset the xref names), and the /ByteRange is rewritten to end the
/// coverage two bytes short of the file. The CMS digest is then stale, which is fine: the analyser
/// is pure structure and never consults it.
/// </remarks>
public sealed class FieldMdpCoverageTailTests
{
    private static readonly Lazy<(byte[] Pfx, string Password)> Keys = new(() => SigningTestCrypto.Shared(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Coverage Tail Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Coverage Tail Signer");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }));

    [Fact]
    public void CoverageThatStopsBeforeACrlfAfterEof_StillReachesTheEndOfTheFile()
    {
        byte[] pdf = CrlfTailedSignedFile();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var analysis = Assert.Single(FieldMdpAnalyzer.Analyze(doc, pdf.Length));

        Assert.True(analysis.IsCompliant);
        Assert.Empty(analysis.Violations);
    }

    /// <summary>
    /// The fixture really is what it claims: the file ends in <c>%%EOF\r\n</c> and the coverage ends
    /// exactly two bytes before the end. Without this the test above could pass for any tail.
    /// </summary>
    [Fact]
    public void TheFixtureEndsInCrlfAfterEofAndCoversUpToItsStart()
    {
        byte[] pdf = CrlfTailedSignedFile();
        string text = Encoding.Latin1.GetString(pdf);

        Assert.EndsWith("%%EOF\r\n", text, StringComparison.Ordinal);

        (long _, long b, long c) = ReadByteRange(text);
        Assert.Equal(pdf.Length - 2, b + c);
    }

    // ---------------------------------------------------------------- fixture

    private static byte[] CrlfTailedSignedFile()
    {
        byte[] signed = Sign(FormPdf());

        // Insert a CR before the final LF: every offset the file names is before this point.
        Assert.Equal((byte)'\n', signed[^1]);
        var edited = new byte[signed.Length + 1];
        Array.Copy(signed, edited, signed.Length - 1);
        edited[signed.Length - 1] = (byte)'\r';
        edited[signed.Length] = (byte)'\n';

        // The coverage is [0, a) and [b, b + c). Make the second range end two bytes short of the file.
        string text = Encoding.Latin1.GetString(edited);
        (long a, long b, _) = ReadByteRange(text);
        long c = edited.Length - 2 - b;

        // The array is written compactly, "[0 a b c]", followed by the spaces the placeholder left.
        // Rewrite it in the same span so every later offset holds.
        int open = text.IndexOf("/ByteRange [", StringComparison.Ordinal) + "/ByteRange ".Length;
        int contents = text.IndexOf("/Contents", open, StringComparison.Ordinal);
        string bracket = "[0 " + a.ToString(CultureInfo.InvariantCulture) + " "
                         + b.ToString(CultureInfo.InvariantCulture) + " "
                         + c.ToString(CultureInfo.InvariantCulture) + "]";
        Assert.True(bracket.Length <= contents - open, "the rewritten /ByteRange no longer fits its span");

        text = text.Substring(0, open) + bracket.PadRight(contents - open) + text.Substring(contents);
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>The four numbers of the first /ByteRange in <paramref name="text"/>: 0, a, b and c.</summary>
    private static (long A, long B, long C) ReadByteRange(string text)
    {
        int start = text.IndexOf("/ByteRange [", StringComparison.Ordinal);
        Assert.True(start >= 0, "the signed document has no /ByteRange");
        start += "/ByteRange [".Length;
        int end = text.IndexOf(']', start);
        string[] parts = text.Substring(start, end - start).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, parts.Length);
        return (long.Parse(parts[1], CultureInfo.InvariantCulture),
                long.Parse(parts[2], CultureInfo.InvariantCulture),
                long.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    private static byte[] FormPdf() => SignedPdfFixtures.Assemble(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /SigFlags 3 /DA (/Helv 0 Tf 0 g) >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R 6 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /FT /Tx /T (Name) /V (Alice) /Type /Annot /Subtype /Widget /Rect [50 700 250 720] /F 4 /P 3 0 R >>",
        "<< /FT /Sig /T (Sig) /Type /Annot /Subtype /Widget /Rect [50 600 250 640] /F 4 /P 3 0 R "
        + "/Lock << /Type /SigFieldLock /Action /Include /Fields [(Name)] >> >>",
        "null");

    private static byte[] Sign(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(Keys.Value.Pfx),
            CertificatePassword = Keys.Value.Password,
            SignatureFieldName = "Sig",
        });
        return output.ToArray();
    }
}
