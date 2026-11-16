// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The accessibility writers refuse an encrypted document — every one of them appends a
/// <b>clear-text</b> incremental update that would not match the encryption dictionary, so
/// producing one would corrupt the file. That refusal is correct and stays.
///
/// <para>What changed is that the refusal is no longer a dead end:
/// <see cref="PdfEncryptedRoundTrip"/> decrypts, runs the pipeline over the plaintext, and
/// re-encrypts. These tests prove both halves — the guard still holds on a direct call, and the
/// round trip really does produce a <i>tagged</i> document that still opens with the password.</para>
///
/// <para><b>Note what this does not do.</b> It does not make the writers encryption-aware.
/// Encrypting an appended incremental update in place — which is what would let a remediated
/// document keep an existing signature — needs the file key threaded into a Core writer that has
/// no crypto today. Still open; see GAPS.md §5c.</para>
/// </summary>
public sealed class EncryptedRemediationRoundTripTests
{
    private const string Password = "a11y-pw";

    [Fact]
    public void Remediate_ThroughTheRoundTrip_TagsAnEncryptedDocument()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        // Sanity-pin the fixture: it starts untagged, so a /StructTreeRoot below can only have
        // come from the pipeline.
        using (var check = new MemoryStream(plain))
        using (var doc = PdfDocument.Open(check))
            Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot));

        byte[] encrypted;
        using (var input = new MemoryStream(plain))
        using (var output = new MemoryStream())
        {
            PdfEncryptor.Encrypt(input, output, new EncryptionParameters
            {
                UserPassword  = Password,
                OwnerPassword = Password,
                Algorithm     = EncryptionAlgorithm.AES128,
                Permissions   = PdfPermissions.All
            });
            encrypted = output.ToArray();
        }

        // The pipeline on its own passes an encrypted document through untouched and says so.
        using (var direct = new MemoryStream(encrypted))
        using (var untouched = new MemoryStream())
        {
            RemediationReport refused = PdfAccessibility.RemediateWithReport(
                direct, untouched, new RemediationOptions { Title = "Encrypted" });

            Assert.False(refused.IsConformingAfter);
            using var stillEncrypted = PdfDocument.Open(new MemoryStream(untouched.ToArray()));
            Assert.True(stillEncrypted.IsEncrypted);
            Assert.False(stillEncrypted.Catalog?.Dictionary.ContainsKey(PdfName.StructTreeRoot) ?? false);
        }

        // Through the round trip it really is remediated.
        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            RoundTripResult result = PdfEncryptedRoundTrip.Run(
                input, output,
                new EncryptedRoundTripOptions { OpenPassword = Password },
                (p, o) => PdfAccessibility.RemediateWithReport(
                    p, o, new RemediationOptions { Title = "Quarterly Report" }));

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Equal(EncryptionAlgorithm.AES128, result.AlgorithmOut);
            produced = output.ToArray();
        }

        // Still encrypted, still opens with the password.
        using (var stillEncrypted = PdfDocument.Open(new MemoryStream(produced)))
            Assert.True(stillEncrypted.IsEncrypted);

        byte[] decrypted;
        using (var input = new MemoryStream(produced))
        using (var output = new MemoryStream())
        {
            PdfDecryptor.Decrypt(input, output, Password);
            decrypted = output.ToArray();
        }

        // And the pipeline's work is in there: a structure tree, marked content, and the title.
        using var final = PdfDocument.Open(new MemoryStream(decrypted));
        Assert.True(final.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot));

        string text = Encoding.ASCII.GetString(decrypted);
        Assert.Contains("/StructTreeRoot", text);
        Assert.Contains("/MarkInfo", text);
    }
}
