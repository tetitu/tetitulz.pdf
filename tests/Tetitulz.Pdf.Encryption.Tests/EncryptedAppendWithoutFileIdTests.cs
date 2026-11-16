// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A revision-4-or-lower document whose trailer has no <c>/ID</c> derives its file key over zero
/// identifier bytes (§7.6.3.3 Algorithm 2 step e). An appended revision must leave the trailer
/// without one: a synthesised <c>/ID</c> is a key the password no longer derives, and the document
/// stops opening.
/// </summary>
public class EncryptedAppendWithoutFileIdTests
{
    private const string IdEntry = "/ID [<> <>]";

    [Fact]
    public void AppendingToADocumentWithoutAFileId_LeavesItOpenable()
    {
        byte[] original = WithoutTheIdEntry(Rc4DocumentOverAnEmptyIdentifier());
        Assert.True(OpensWithTheEmptyPassword(original));   // the control

        using var input = new MemoryStream(original, writable: false);
        using var output = new MemoryStream();
        PdfEncryptedDestinations.SetDestination(input, output, "", "d", 0);
        byte[] appended = output.ToArray();

        Assert.True(OpensWithTheEmptyPassword(appended));
        Assert.DoesNotContain("/ID", Encoding.Latin1.GetString(appended), StringComparison.Ordinal);

        using var sink = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(appended, writable: false), sink, "");
        using var doc = PdfDocument.Open(new MemoryStream(sink.ToArray()));
        Assert.NotNull(doc);
        var names = doc!.ResolveObject(doc.Catalog!.Dictionary[PdfName.Names]) as PdfDictionary;
        Assert.NotNull(names);
        Assert.NotNull(doc.ResolveObject(names![PdfName.Dests]));
    }

    /// <summary>The same document with its identifier left in: the appended trailer keeps it.</summary>
    [Fact]
    public void AppendingToADocumentWithAFileId_KeepsIt()
    {
        byte[] original = Rc4DocumentOverAnEmptyIdentifier();

        using var input = new MemoryStream(original, writable: false);
        using var output = new MemoryStream();
        PdfEncryptedDestinations.SetDestination(input, output, "", "d", 0);

        string tail = Encoding.Latin1.GetString(output.ToArray()).Substring(original.Length);
        Assert.Contains("/ID", tail, StringComparison.Ordinal);
        Assert.True(OpensWithTheEmptyPassword(output.ToArray()));
    }

    private static bool OpensWithTheEmptyPassword(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        return PdfDecryptor.TryPassword(PdfDecryptor.GetEncryptionInfo(stream), "");
    }

    private static byte[] Rc4DocumentOverAnEmptyIdentifier()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(Array.Empty<byte>());
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        pdf.EncryptDictionary(4);
        pdf.EndRevision(xrefStreamObjectNumber: 5, rootObjectNumber: 1, encryptObjectNumber: 4);
        return pdf.ToArray();
    }

    /// <summary>The trailer's empty <c>/ID</c> blanked to spaces of the same length, so no offset moves.</summary>
    private static byte[] WithoutTheIdEntry(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf(IdEntry, StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture carries no /ID entry to remove");
        return Encoding.Latin1.GetBytes(text.Substring(0, at) + new string(' ', IdEntry.Length) + text.Substring(at + IdEntry.Length));
    }
}
