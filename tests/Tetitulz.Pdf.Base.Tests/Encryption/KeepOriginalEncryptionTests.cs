// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// <see cref="ReEncryptionPolicy.KeepOriginal"/>: the round trip's output is written under the
/// input's own file key, <c>/Encrypt</c> dictionary and first file identifier, so both passwords
/// still open it — the owner's included, which <see cref="ReEncryptionPolicy.ReuseOpenPassword"/>
/// loses. Asserted on the produced file, opened by this library and by an independent checker, which did not write it.
/// </summary>
public sealed partial class KeepOriginalEncryptionTests
{
    private const string User = "user-pw";
    private const string Owner = "owner-pw";

    private static byte[] Encrypt(byte[] plain, EncryptionAlgorithm algorithm, bool pdfMac = false)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = User,
            OwnerPassword = Owner,
            Algorithm = algorithm,
            Permissions = PdfPermissions.All & ~PdfPermissions.Print,
            EnablePdfMac = pdfMac,
        });
        return output.ToArray();
    }

    private static (RoundTripResult Result, byte[] Output) Run(byte[] encrypted, ReEncryptionPolicy policy,
        Action<Stream, Stream> operation, string password = User)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(input, output,
            new EncryptedRoundTripOptions { OpenPassword = password, Policy = policy }, operation);
        return (result, output.ToArray());
    }

    private static byte[] Decrypt(byte[] pdf, string password)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }

    private static EncryptionInfo InfoOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfDecryptor.GetEncryptionInfo(ms);
    }

    private static byte[] Entry(EncryptionInfo info, PdfName key) => ((PdfString)info.EncryptDict[key]).RawBytes;

    private static byte[] FirstId(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return ((PdfString)doc.Trailer.GetArray(PdfName.ID)[0]).RawBytes;
    }

    /// <summary>
    /// Both passwords open the output, its <c>/O</c>, <c>/U</c>, <c>/P</c> and first <c>/ID</c> are
    /// the input's byte for byte, and what is encrypted is the operation's output — the sanitizer's,
    /// with the script gone. The owner is not reported changed.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void KeepOriginal_BothPasswordsOpenTheOperationsOutput(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreatePdfWithJavaScript(), algorithm);

        var (result, produced) = Run(encrypted, ReEncryptionPolicy.KeepOriginal,
            (plain, outp) => PdfSanitizer.Sanitize(plain, outp, new SanitizeOptions { RemoveJavaScript = true, RemoveOpenAction = true }));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(ReEncryptionPolicy.KeepOriginal, result.PolicyApplied);
        Assert.False(result.OwnerPasswordChanged);
        Assert.Equal(algorithm, result.AlgorithmOut);

        EncryptionInfo before = InfoOf(encrypted), after = InfoOf(produced);
        Assert.Equal(Entry(before, PdfName.O), Entry(after, PdfName.O));
        Assert.Equal(Entry(before, PdfName.U), Entry(after, PdfName.U));
        Assert.Equal(before.PValue, after.PValue);
        Assert.Equal(FirstId(encrypted), FirstId(produced));

        foreach (string password in new[] { User, Owner })
        {
            string text = Encoding.ASCII.GetString(Decrypt(produced, password));
            Assert.DoesNotContain("app.alert", text, StringComparison.Ordinal);
            Assert.Contains("/Type /Page", text.Replace("/Type/Page", "/Type /Page"), StringComparison.Ordinal);
        }
        Assert.Throws<PdfException>(() => Decrypt(produced, "neither"));
    }

    /// <summary>The control: the default policy, on the same input, loses the owner password.</summary>
    [Fact]
    public void ReuseOpenPassword_OnTheSameInput_LosesTheOwnerPassword()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("control"), EncryptionAlgorithm.AES256);
        var (result, produced) = Run(encrypted, ReEncryptionPolicy.ReuseOpenPassword, (p, o) => p.CopyTo(o));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Throws<PdfException>(() => Decrypt(produced, Owner));
        Assert.NotEqual(Entry(InfoOf(encrypted), PdfName.O), Entry(InfoOf(produced), PdfName.O));
    }

    /// <summary>
    /// Opened with the owner password, the output still opens with the user's: the credential that
    /// ran the trip does not decide which keys the output answers to.
    /// </summary>
    [Fact]
    public void KeepOriginal_OpenedWithTheOwnerPassword_StillOpensWithTheUsers()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("owner opens"), EncryptionAlgorithm.AES128);
        var (result, produced) = Run(encrypted, ReEncryptionPolicy.KeepOriginal, (p, o) => p.CopyTo(o), password: Owner);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEmpty(Decrypt(produced, User).Where(b => b == (byte)'%'));
        Assert.NotEmpty(Decrypt(produced, Owner).Where(b => b == (byte)'%'));
    }

    /// <summary>
    /// The optimizer writes object streams, so the output takes the compressed layout's writer; it
    /// still opens with both passwords and holds the optimizer's work.
    /// </summary>
    [Fact]
    public void KeepOriginal_ThroughTheOptimizersCompressedLayout_OpensWithBothPasswords()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreatePdfWithUnusedObject(), EncryptionAlgorithm.AES256);
        var (result, produced) = Run(encrypted, ReEncryptionPolicy.KeepOriginal,
            (p, o) => PdfOptimizer.Optimize(p, o, new OptimizationOptions { UseObjectStreams = true }));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains("/ObjStm", Encoding.ASCII.GetString(produced), StringComparison.Ordinal);
        foreach (string password in new[] { User, Owner })
        {
            using var doc = PdfDocument.Open(new MemoryStream(Decrypt(produced, password)));
            Assert.Single(doc.Pages);
            Assert.DoesNotContain("Times-Roman", Encoding.ASCII.GetString(Decrypt(produced, password)), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A document protected by PDF MAC keeps it: the output carries a token that verifies under the
    /// original key and salt.
    /// </summary>
    [Fact]
    public void KeepOriginal_KeepsAndReseals_ThePdfMac()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("mac"), EncryptionAlgorithm.AES256, pdfMac: true);
        var (result, produced) = Run(encrypted, ReEncryptionPolicy.KeepOriginal, (p, o) => p.CopyTo(o));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(Entry(InfoOf(encrypted), PdfName.KDFSalt), Entry(InfoOf(produced), PdfName.KDFSalt));
        using var ms = new MemoryStream(produced);
        var verdict = PdfMacVerifier.Verify(ms, User);
        Assert.True(verdict.IsValid, verdict.ToString());
    }

    /// <summary>
    /// What a caller asking before the trip is told: the input's algorithm, permissions and PDF MAC,
    /// and no passwords — nothing is derived from one.
    /// </summary>
    [Fact]
    public void TryDeriveParameters_UnderKeepOriginal_NamesTheInputsProtection_AndNoPassword()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("derive"), EncryptionAlgorithm.AES256, pdfMac: true);

        Assert.True(PdfEncryptedRoundTrip.TryDeriveParameters(InfoOf(encrypted),
            new EncryptedRoundTripOptions { OpenPassword = User, Policy = ReEncryptionPolicy.KeepOriginal },
            out EncryptionParameters parameters, out string reason), reason);

        Assert.Equal(EncryptionAlgorithm.AES256, parameters.Algorithm);
        Assert.True(parameters.EnablePdfMac);
        Assert.Equal(0, (int)(parameters.Permissions & PdfPermissions.Print));
        Assert.True(string.IsNullOrEmpty(parameters.UserPassword), "a password was derived: " + parameters.UserPassword);
        Assert.True(string.IsNullOrEmpty(parameters.OwnerPassword), "a password was derived: " + parameters.OwnerPassword);
    }

    /// <summary>
    /// An RC4 document with no <c>/ID</c> — its key derived from an empty identifier (ISO 32000-1
    /// Algorithm 2) — cannot be kept, since every written file carries one: refused with the reason,
    /// nothing written. The control: the same file round-trips under the default policy, so the
    /// refusal is KeepOriginal's and not a failure to decrypt.
    /// </summary>
    [Fact]
    public void KeepOriginal_OnAnRc4DocumentWithNoId_IsRefusedForThatReason()
    {
        byte[] noId = Rc4FortyWithoutId("kept?");

        using (var input = new MemoryStream(noId))
        using (var output = new MemoryStream())
        {
            var refused = PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions { OpenPassword = "", Policy = ReEncryptionPolicy.KeepOriginal }, (p, o) => p.CopyTo(o));
            Assert.False(refused.Succeeded);
            Assert.Contains("no /ID", refused.FailureReason, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }

        var (accepted, produced) = Run(noId, ReEncryptionPolicy.ReuseOpenPassword, (p, o) => p.CopyTo(o), password: "");
        Assert.True(accepted.Succeeded, accepted.FailureReason);
        Assert.Contains("kept?", Encoding.ASCII.GetString(Decrypt(produced, "")), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Adversarial: an <c>/Encrypt</c> dictionary whose extra entry fans out.</b> Seven levels of ten
    /// references, each to the next level's array — a few hundred bytes the decryptor never reads, and
    /// about 10⁶ objects once every reference is copied resolved (sixteen levels, inside the depth
    /// bound, were 10¹⁶). The copy is refused past its object ceiling, with the reason; the control, the
    /// same document without the entry, is kept.
    /// </summary>
    [Fact]
    public void AdversarialFanningEncryptDictionary_IsRefusedAtItsObjectCeiling()
    {
        const int levels = 7, width = 10;
        var extra = new string[levels];
        for (int k = 0; k < levels; k++)
            extra[k] = k == levels - 1 ? "[]" : "[" + string.Concat(Enumerable.Repeat($"{8 + k} 0 R ", width)) + "]";
        byte[] id = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        using (var input = new MemoryStream(Rc4Forty("fans", id, "/Junk 7 0 R", extra)))
        using (var output = new MemoryStream())
        {
            var refused = PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions { OpenPassword = "", Policy = ReEncryptionPolicy.KeepOriginal }, (p, o) => p.CopyTo(o));
            Assert.False(refused.Succeeded);
            Assert.Contains("objects", refused.FailureReason, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }

        var (kept, produced) = Run(Rc4Forty("fans", id), ReEncryptionPolicy.KeepOriginal, (p, o) => p.CopyTo(o), password: "");
        Assert.True(kept.Succeeded, kept.FailureReason);
        Assert.Contains("fans", Encoding.ASCII.GetString(Decrypt(produced, "")), StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry of the <c>/Encrypt</c> dictionary naming an object the parser cannot read (a stray
    /// <c>]</c>) is refused for that reason — not copied as an absent entry's null into the output's
    /// dictionary. The control, the same document without the entry, is kept.
    /// </summary>
    [Fact]
    public void KeepOriginal_AnUnreadableObjectTheEncryptDictionaryNames_IsRefusedForThatReason()
    {
        byte[] id = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using (var input = new MemoryStream(Rc4Forty("stray", id, "/Junk 7 0 R", "]")))
        using (var output = new MemoryStream())
        {
            var refused = PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions { OpenPassword = "", Policy = ReEncryptionPolicy.KeepOriginal }, (p, o) => p.CopyTo(o));
            Assert.False(refused.Succeeded);
            Assert.Contains("dictionary names could not be read", refused.FailureReason, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }

        var (kept, _) = Run(Rc4Forty("stray", id), ReEncryptionPolicy.KeepOriginal, (p, o) => p.CopyTo(o), password: "");
        Assert.True(kept.Succeeded, kept.FailureReason);
    }

    private static byte[] Rc4FortyWithoutId(string text) => Rc4Forty(text, null);

    /// <summary>
    /// A one-page RC4 40-bit (V 1, R 2) document with an empty user password, built by ISO 32000-1
    /// §7.6.3.3 Algorithms 2–4 directly, its one content stream encrypted under Algorithm 1's object
    /// key — with no <c>/ID</c> unless <paramref name="id"/> gives one, and <paramref name="extraObjects"/>
    /// numbered from 7, holding no string, which <paramref name="encryptExtra"/> may name.
    /// </summary>
    private static byte[] Rc4Forty(string text, byte[]? id, string encryptExtra = "", params string[] extraObjects)
    {
        byte[] pad = { 0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
                       0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A };
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] Take(byte[] b, int n) => b.Take(n).ToArray();
        byte[] Rc4(byte[] key, byte[] data)
        {
            var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            for (int i = 0, j = 0; i < 256; i++) { j = (j + s[i] + key[i % key.Length]) & 255; (s[i], s[j]) = (s[j], s[i]); }
            var result = new byte[data.Length];
            for (int k = 0, i = 0, j = 0; k < data.Length; k++)
            {
                i = (i + 1) & 255; j = (j + s[i]) & 255; (s[i], s[j]) = (s[j], s[i]);
                result[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 255]);
            }
            return result;
        }

        byte[] ownerPadded = Encoding.ASCII.GetBytes("owner").Concat(pad).Take(32).ToArray();
        byte[] o = Rc4(Take(md5.ComputeHash(ownerPadded), 5), pad);                       // Algorithm 3, the user password empty
        const int p = -4;
        byte[] key = Take(md5.ComputeHash(pad.Concat(o).Concat(BitConverter.GetBytes(p)).Concat(id ?? Array.Empty<byte>()).ToArray()), 5);   // Algorithm 2
        byte[] u = Rc4(key, pad);                                                          // Algorithm 4
        byte[] objectKey = Take(md5.ComputeHash(key.Concat(new byte[] { 4, 0, 0, 0, 0 }).ToArray()), 10);   // Algorithm 1, object 4 0
        byte[] content = Rc4(objectKey, Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (" + text + ") Tj ET"));

        string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("X2")));
        var file = new MemoryStream();
        int size = 7 + extraObjects.Length;
        var offsets = new long[size];
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        offsets[1] = file.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = file.Position; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        offsets[4] = file.Position; W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); file.Write(content, 0, content.Length); W("\nendstream\nendobj\n");
        offsets[5] = file.Position; W("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        offsets[6] = file.Position; W($"6 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <{Hex(o)}> /U <{Hex(u)}> /P {p} {encryptExtra} >>\nendobj\n");
        for (int k = 0; k < extraObjects.Length; k++) { offsets[7 + k] = file.Position; W($"{7 + k} 0 obj\n{extraObjects[k]}\nendobj\n"); }
        long xref = file.Position;
        W($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++) W($"{offsets[i]:D10} 00000 n \n");
        string idEntry = id == null ? "" : $" /ID [<{Hex(id)}> <{Hex(id)}>]";
        W($"trailer\n<< /Size {size} /Root 1 0 R /Encrypt 6 0 R{idEntry} >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}
