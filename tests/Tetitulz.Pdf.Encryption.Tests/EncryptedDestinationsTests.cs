// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfEncryptedDestinations"/>: named destinations written into an <b>encrypted</b>
/// document as one revision encrypted with its own key — the route GAPS.md §5a recorded as missing.
/// The raw bytes are read: a parse of the output cannot tell a name tree written in the clear from
/// one encrypted and decrypted again.
/// </summary>
public sealed class EncryptedDestinationsTests
{
    private const string Password = "dest-pw-41";
    private const string Name = "Chapter-9f2c";

    public EncryptedDestinationsTests() => PdfEncryptionOperations.Register();

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void ADestinationIsAddedInPlace_Encrypted(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(TwoPages(), algorithm);

        using var output = new MemoryStream();
        PdfEncryptedDestinations.SetDestination(new MemoryStream(encrypted, writable: false), output, Password, Name, 1, 72, 700, 1.5);
        byte[] updated = output.ToArray();

        // An appended revision: the original bytes are the output's prefix, and it is still encrypted.
        Assert.True(updated.Length > encrypted.Length, "nothing was appended");
        Assert.Equal(encrypted, updated.Take(encrypted.Length).ToArray());
        using (var doc = PdfDocument.Open(new MemoryStream(updated))) Assert.True(doc.IsEncrypted);

        // The name is ciphertext in the file, and the decrypted view has the destination.
        Assert.DoesNotContain(Name, Encoding.Latin1.GetString(updated));
        var dest = Assert.Single(PdfNamedDestinations.ListDestinations(new MemoryStream(updated), Password));
        Assert.Equal(Name, dest.Name);
        Assert.Equal(1, dest.PageIndex);
    }

    [Fact]
    public void ADestinationIsRemovedInPlace()
    {
        byte[] encrypted = Encrypt(TwoPages(), EncryptionAlgorithm.AES128);
        using var added = new MemoryStream();
        PdfEncryptedDestinations.SetDestination(new MemoryStream(encrypted, writable: false), added, Password, Name, 0);
        byte[] withDestination = added.ToArray();

        using var removed = new MemoryStream();
        Assert.True(PdfEncryptedDestinations.RemoveDestination(new MemoryStream(withDestination, writable: false), removed, Password, Name));
        byte[] without = removed.ToArray();

        Assert.Equal(withDestination, without.Take(withDestination.Length).ToArray());
        Assert.Empty(PdfNamedDestinations.ListDestinations(new MemoryStream(without), Password));
    }

    /// <summary>Removing a name the document does not hold writes it unchanged and says so.</summary>
    [Fact]
    public void RemovingAnAbsentDestination_WritesTheDocumentUnchanged()
    {
        byte[] encrypted = Encrypt(TwoPages(), EncryptionAlgorithm.AES128);
        using var output = new MemoryStream();

        Assert.False(PdfEncryptedDestinations.RemoveDestination(new MemoryStream(encrypted, writable: false), output, Password, Name));
        Assert.Equal(encrypted, output.ToArray());
    }

    [Fact]
    public void AWrongPassword_IsRefusedBeforeAnythingIsWritten()
    {
        byte[] encrypted = Encrypt(TwoPages(), EncryptionAlgorithm.AES128);
        using var output = new MemoryStream();

        Assert.ThrowsAny<PdfException>(() =>
            PdfEncryptedDestinations.SetDestination(new MemoryStream(encrypted, writable: false), output, "wrong", Name, 0));
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// Listing without the password refuses: an encrypted document exposes no catalog, and the empty
    /// list returned before claimed a document without destinations. With the password it reads them.
    /// </summary>
    [Fact]
    public void ListingAnEncryptedDocument_RefusesWithoutThePassword()
    {
        byte[] encrypted = Encrypt(TwoPages(), EncryptionAlgorithm.AES128);
        using var output = new MemoryStream();
        PdfEncryptedDestinations.SetDestination(new MemoryStream(encrypted, writable: false), output, Password, Name, 0);

        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfNamedDestinations.ListDestinations(new MemoryStream(output.ToArray())));
        Assert.Single(PdfNamedDestinations.ListDestinations(new MemoryStream(output.ToArray()), Password));
    }

    /// <summary>The Base writers still refuse an encrypted document, now pointing at this route.</summary>
    [Fact]
    public void TheBaseWriter_StillRefusesAnEncryptedDocument()
    {
        byte[] encrypted = Encrypt(TwoPages(), EncryptionAlgorithm.AES128);
        using var output = new MemoryStream();

        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfNamedDestinations.SetDestination(new MemoryStream(encrypted), output, Name, 0));
        Assert.Equal(0, output.Length);
    }

    private static byte[] Encrypt(byte[] plaintext, EncryptionAlgorithm algorithm)
    {
        using var plain = new MemoryStream(plaintext);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = "owner-" + Password,
            Algorithm = algorithm,
        });
        return encrypted.ToArray();
    }

    private static byte[] TwoPages()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
