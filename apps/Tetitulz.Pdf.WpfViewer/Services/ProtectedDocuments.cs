// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.WpfViewer.Models;

namespace Tetitulz.Pdf.WpfViewer.Services;

/// <summary>
/// A document opened under a password, from the open to the save: whether a file is encrypted, the
/// plaintext a password gives, whether it is signed, and what a save writes so that the protection
/// it was opened under is put back. Shared by every app that edits a document as plaintext.
/// </summary>
public static class ProtectedDocuments
{
    /// <summary>
    /// True when the document is encrypted. <see cref="PdfDocument.Open(Stream)"/> does not throw on
    /// an encrypted file; it sets <see cref="PdfDocument.IsEncrypted"/>.
    /// </summary>
    public static bool IsEncrypted(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return IsEncrypted(ms);
    }

    /// <summary>True when the document <paramref name="pdf"/> holds is encrypted; false for one that cannot be read.</summary>
    public static bool IsEncrypted(Stream pdf)
    {
        try
        {
            using var d = PdfDocument.Open(pdf);
            return d.IsEncrypted;
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or IOException)
        {
            return false;
        }
    }

    /// <summary>The plaintext <paramref name="password"/> opens <paramref name="encrypted"/> to; false when it does not open it.</summary>
    public static bool TryDecrypt(byte[] encrypted, string password, out byte[]? plain)
    {
        plain = null;
        try
        {
            using var i = new MemoryStream(encrypted, writable: false);
            using var o = new MemoryStream();
            PdfDecryptor.Decrypt(i, o, password);
            plain = o.ToArray();
            return plain.Length > 0;
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Whether the document carries a digital signature.</summary>
    public static bool HasSignature(byte[] pdf)
    {
        try
        {
            using var ms = new MemoryStream(pdf, writable: false);
            using var d = PdfDocument.Open(ms);
            return PdfSignatureDetection.HasSignature(d.Trailer, d.GetObject);
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a save writes for a document that was opened under a password, given the plaintext the
    /// commands have made of it.
    /// </summary>
    /// <remarks>
    /// <para><b>Untouched, it is the file itself</b>, byte for byte, so its signatures and its
    /// owner password are what they were. Changed, the plaintext is encrypted again under the
    /// original's own key and encryption dictionary (<c>KeepOriginal</c>), so the same passwords
    /// open it with the same permissions; where that cannot be done and there is a password to
    /// reuse, under that password. A document that cannot be protected again is refused with the
    /// reason — the caller asks the user before anything is written in the clear.</para>
    /// <para><b>A changed copy of a signed document loses its signatures</b>, since they cover the
    /// file as it was; that is done only when <paramref name="allowSignatureLoss"/> says the user
    /// agreed.</para>
    /// </remarks>
    public static ProtectedSave ProtectForSave(OpenedProtection protection, byte[] plaintext, bool allowSignatureLoss)
    {
        if (ReferenceEquals(plaintext, protection.DecryptedOriginal))
            return new ProtectedSave(protection.EncryptedOriginal, null, false);

        if (!allowSignatureLoss && HasSignature(protection.DecryptedOriginal))
            return new ProtectedSave(null, null, NeedsSignatureConsent: true);

        string? refusal = null;
        foreach (ReEncryptionPolicy policy in new[] { ReEncryptionPolicy.KeepOriginal, ReEncryptionPolicy.ReuseOpenPassword })
        {
            // Reusing the password that opened it protects nothing when that password is empty.
            if (policy == ReEncryptionPolicy.ReuseOpenPassword && protection.Password.Length == 0) continue;

            using var original = new MemoryStream(protection.EncryptedOriginal, writable: false);
            using var output = new MemoryStream();
            RoundTripResult result = PdfEncryptedRoundTrip.Run(original, output,
                new EncryptedRoundTripOptions
                {
                    OpenPassword = protection.Password,
                    Policy = policy,
                    AllowSignatureInvalidation = true,   // asked above, of the user
                },
                (_, produced) => produced.Write(plaintext, 0, plaintext.Length));
            if (result.Succeeded) return new ProtectedSave(output.ToArray(), null, false);
            refusal ??= result.FailureReason;
        }
        return new ProtectedSave(null, refusal ?? "the document's protection could not be written again.", false);
    }
}
