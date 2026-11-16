// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// <see cref="IndependentPdfCheck"/> finds what it claims to: each fault it reports, made once in an
/// otherwise clean file, beside the clean file itself. A checker that passes everything would make every
/// writer test that relies on it vacuous.
/// </summary>
public class IndependentPdfCheckTests
{
    [Fact]
    public void TheWriterFixture_IsClean_InBothLayouts()
    {
        var table = IndependentPdfCheck.Run(WriterCheckFixture.Fixture());
        Assert.True(table.Clean, table.ToString());
        var streams = IndependentPdfCheck.Run(WriterCheckFixture.ObjectStreamsLayout());
        Assert.True(streams.Clean, streams.ToString());
    }

    [Fact]
    public void AnObjectStreamLayout_IsReadThroughItsXrefStream()
    {
        byte[] pdf = WriterCheckFixture.ObjectStreamsLayout();
        Assert.Contains("/ObjStm", Encoding.Latin1.GetString(pdf));
        Assert.Contains("/XRef", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void AWrongXrefOffset_IsReported()
    {
        byte[] pdf = Build(objects: Objects(), corruptOffsetOf: 3);
        var outcome = IndependentPdfCheck.Run(pdf);
        Assert.False(outcome.Clean);
        Assert.Contains(outcome.Problems, p => p.StartsWith("object 3:", StringComparison.Ordinal));
        var control = IndependentPdfCheck.Run(Build(Objects()));
        Assert.True(control.Clean, control.ToString());
    }

    [Fact]
    public void AStreamLengthOneShort_IsReported()
    {
        var objects = Objects();
        objects[3] = "<< /Length 10 >>\nstream\n0 0 m 1 1 l\nendstream";   // the data is 11 bytes
        var outcome = IndependentPdfCheck.Run(Build(objects));
        Assert.Contains(outcome.Problems, p => p.Contains("'endstream' does not follow", StringComparison.Ordinal));
    }

    [Fact]
    public void ASizeThatDoesNotCoverEveryObject_IsReported()
    {
        var outcome = IndependentPdfCheck.Run(Build(Objects(), sizeDelta: -1));
        Assert.Contains(outcome.Problems, p => p.StartsWith("/Size", StringComparison.Ordinal));
    }

    [Fact]
    public void APageCountThatDisagreesWithTheKids_IsReported()
    {
        var objects = Objects();
        objects[1] = "<< /Type /Pages /Kids [3 0 R] /Count 2 >>";
        var outcome = IndependentPdfCheck.Run(Build(objects));
        Assert.Contains(outcome.Problems, p => p.Contains("/Count 2 over 1 pages", StringComparison.Ordinal));
    }

    [Fact]
    public void FlateDataThatDoesNotDecode_IsReported()
    {
        var objects = Objects();
        objects[3] = "<< /Length 6 /Filter /FlateDecode >>\nstream\nnot zl\nendstream";
        var outcome = IndependentPdfCheck.Run(Build(objects));
        Assert.Contains(outcome.Problems, p => p.Contains("FlateDecode data does not decode", StringComparison.Ordinal));
    }

    [Fact]
    public void HostileNesting_IsRefusedAtTheDepthLimit()
    {
        var objects = Objects();
        objects[0] = "<< /Type /Catalog /Pages 2 0 R /Deep " + new string('[', 5000) + new string(']', 5000) + " >>";
        var outcome = IndependentPdfCheck.Run(Build(objects));
        Assert.Contains(outcome.Problems, p => p.Contains("nest more than 100 deep", StringComparison.Ordinal));
        var shallow = Objects();
        shallow[0] = "<< /Type /Catalog /Pages 2 0 R /Deep " + new string('[', 50) + new string(']', 50) + " >>";
        var control = IndependentPdfCheck.Run(Build(shallow));
        Assert.True(control.Clean, control.ToString());
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_40R3)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void AnEncryptedFile_OpensWithEitherPassword_AndNotAWrongOne(EncryptionAlgorithm algorithm)
    {
        using var i = new MemoryStream(WriterCheckFixture.Fixture(), writable: false);
        using var o = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(i, o, new EncryptionParameters
        {
            UserPassword = "user", OwnerPassword = "owner", Algorithm = algorithm,
        });
        byte[] pdf = o.ToArray();
        var user = IndependentPdfCheck.Run(pdf, "user");
        Assert.True(user.Clean, user.ToString());
        var owner = IndependentPdfCheck.Run(pdf, "owner");
        Assert.True(owner.Clean, owner.ToString());
        Assert.False(IndependentPdfCheck.Run(pdf, "neither").Opened);
    }

    [Fact]
    public void AnEncryptedStreamUnderTheWrongKey_IsReported()
    {
        using var i = new MemoryStream(WriterCheckFixture.Fixture(), writable: false);
        using var o = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(i, o, new EncryptionParameters
        {
            UserPassword = "user", OwnerPassword = "owner", Algorithm = EncryptionAlgorithm.AES128,
        });
        byte[] pdf = o.ToArray();
        // CBC: the byte 16 before the end of a stream's ciphertext is XORed into the last plaintext byte,
        // which is the padding length — flipped with 0x55 it names more than a block, so the padding is wrong.
        string text = Encoding.Latin1.GetString(pdf);
        int end = text.IndexOf("endstream", text.IndexOf(" obj", StringComparison.Ordinal), StringComparison.Ordinal);
        end -= text[end - 2] == '\r' ? 2 : 1;
        pdf[end - 17] ^= 0x55;
        var outcome = IndependentPdfCheck.Run(pdf, "user");
        Assert.True(outcome.Opened);
        Assert.False(outcome.Clean);
    }

    // ------------------------------------------------------------------ a small file, built by hand

    private static List<string> Objects() => new()
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
        "<< /Length 11 >>\nstream\n0 0 m 1 1 l\nendstream",
    };

    private static byte[] Build(List<string> objects, int corruptOffsetOf = 0, int sizeDelta = 0)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int k = 0; k < objects.Count; k++)
        {
            offsets[k] = sb.Length;
            sb.Append(k + 1).Append(" 0 obj\n").Append(objects[k]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        for (int k = 0; k < objects.Count; k++)
            sb.Append((offsets[k] + (k + 1 == corruptOffsetOf ? 3 : 0)).ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1 + sizeDelta).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
