// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Decryption of a document this library did not write. The fixture derives its own key (see
/// <see cref="TestPdfs.LegacyRc4"/>) because the defect below is invisible to a round trip:
/// <c>PdfEncryptor</c> never writes an <c>/XRefStm</c>, so no document produced by this library
/// can carry the key into <c>PdfDecryptor</c>'s trailer rewrite.
/// </summary>
public class DecryptorLegacyInteropTests
{
    private static readonly byte[] FileId = { 9, 9, 8, 8, 7, 7, 6, 6, 5, 5, 4, 4, 3, 3, 2, 2 };

    /// <summary>
    /// The control. A document whose <c>/O</c>, <c>/U</c> and object keys came from a second
    /// transcription of ISO 32000-1 Algorithms 1-5 — not from <c>PdfEncryptionPrimitives</c> —
    /// must decrypt, or the test below would be measuring the fixture rather than the decryptor.
    /// </summary>
    [Fact]
    public void Decrypt_OfAnIndependentlyKeyedDocument_RecoversTheContent()
    {
        byte[] pdf = TestPdfs.LegacyRc4.Build(fileId: FileId);

        Assert.Contains(TestPdfs.ContentMarker, TestPdfs.Ascii(Decrypt(pdf)));
    }

    /// <summary>
    /// <c>/XRefStm</c> is a byte offset into the ORIGINAL file layout. Decryption rewrites every
    /// object at a new offset, so carrying the key through leaves the output telling a reader that
    /// prefers the cross-reference stream (a common viewer does) to seek into unrelated data — while the
    /// classic table this method writes is correct, so whether the file opens depends on which
    /// table the reader trusts. <c>PdfEncryptor.CloneTrailerForEncryption</c> already excluded it;
    /// the decryptor's copy of the same decision did not.
    /// </summary>
    [Fact]
    public void Decrypt_OfAHybridReferenceDocument_DropsTheXRefStmOffsetFromTheOutputTrailer()
    {
        byte[] probe = TestPdfs.LegacyRc4.Build(fileId: FileId);
        byte[] pdf = TestPdfs.LegacyRc4.Build(
            fileId: FileId,
            extraTrailerEntries: $"/XRefStm {TestPdfs.LegacyRc4.XrefOffsetOf(probe)}");

        // The fixture must actually reach the feature: the INPUT has to carry the key.
        using (var input = new MemoryStream(pdf))
        using (var parsed = PdfDocument.Open(input))
            Assert.NotNull(parsed.Trailer[PdfName.XRefStm]);

        byte[] decrypted = Decrypt(pdf);

        using var output = new MemoryStream(decrypted);
        using var doc = PdfDocument.Open(output);
        Assert.Null(doc.Trailer[PdfName.XRefStm]);
        Assert.Contains(TestPdfs.ContentMarker, TestPdfs.Ascii(decrypted));
    }

    private static byte[] Decrypt(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "");
        return output.ToArray();
    }
}
