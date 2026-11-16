// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Pins the bytes <see cref="PdfSigner.Sign(Stream, Stream, SignatureParameters)"/> appends to a
/// plaintext document — every byte of the revision except the CMS hex, which a fresh key changes
/// every run.
/// </summary>
/// <remarks>
/// <para><b>A characterisation test, not a correctness test.</b> The signer's raw writer was split
/// into helpers shared with the planned form (<see cref="PdfSigner.Plan"/>) — certificate loading,
/// the CMS generation, the catalog refusal — and a pure move must leave the revision byte for
/// byte as it was. Nothing that parses the output can see a stray byte the parser tolerates;
/// this compares the bytes. It is the "unencrypted document still signs as before" control the
/// encrypted signing work was required to keep.</para>
///
/// <para>The <c>/Contents</c> hex run is replaced by a marker before comparing, and the CMS is the
/// only thing in the revision a fixed <see cref="SignatureParameters.SigningTime"/> does not make
/// deterministic. Every offset, the <c>/ByteRange</c>, the trailer's synthesised <c>/ID</c> and the
/// field are pure functions of the fixture.</para>
/// </remarks>
public sealed class SignRevisionCharacterisationTests
{
    private static readonly DateTime FixedSigningTime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// The revision as the raw writer emits it, hex run replaced by <c>&lt;CMS&gt;</c>. The
    /// <c>/ByteRange</c> is padded to the 53-character placeholder it overwrote in place; the xref
    /// re-declares object 0 because the raw writer's table always has.
    /// </summary>
    private static readonly string ExpectedRevision =
        "6 0 obj\n" +
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange " +
        "[0 741 33511 439]".PadRight(53) +
        " /Contents <CMS> /Reason (test) /Location (Tel Aviv) /M (D:20260102030405Z) >>\n" +
        "endobj\n" +
        "7 0 obj\n" +
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /V 6 0 R /F 132 /Rect [0 0 0 0] /P 3 0 R >>\n" +
        "endobj\n" +
        "1 0 obj\n" +
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] /SigFlags 3 >> >>\n" +
        "endobj\n" +
        "xref\n" +
        "0 2\n" +
        "0000000000 65535 f \n" +
        "0000033698 00000 n \n" +
        "6 2\n" +
        "0000000589 00000 n \n" +
        "0000033581 00000 n \n" +
        "trailer\n" +
        "<< /Size 8 /Root 1 0 R /Prev 406 >>\n" +
        "startxref\n" +
        "33791\n" +
        "%%EOF\n";

    [Fact]
    public void TheAppendedRevisionOfAPlainSignIsPinnedByteForByte()
    {
        var (pfx, pw) = Certificate();
        byte[] input = SignedPdfFixtures.MinimalPdf();

        using var inStream = new MemoryStream(input);
        using var output = new MemoryStream();
        new PdfSigner().Sign(inStream, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
            Reason = "test",
            Location = "Tel Aviv",
            SigningTime = FixedSigningTime,
        });
        byte[] signed = output.ToArray();

        string revision = Encoding.Latin1.GetString(signed, input.Length, signed.Length - input.Length);
        string actual = Regex.Replace(revision, @"/Contents <[0-9A-Fa-f]+>", "/Contents <CMS>");

        Assert.True(ExpectedRevision == actual,
            "The revision Sign appends changed. Actual (hex run replaced by <CMS>):\n" + actual);
    }

    private static (byte[] Pfx, string Password) Certificate()
    {
        var root = SigningTestCrypto.Ca("CN=Characterisation Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Characterisation Leaf");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }
}
