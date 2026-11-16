// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Plugins.CertStoreSign;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.PrinterTray;

/// <summary>What the user asked for in the save dialog.</summary>
internal sealed record SaveRequest(
    string Destination,
    string Title,
    bool OpenAfterSaving,
    bool PdfA,
    string? Password,
    string? CertificateThumbprint,
    string? Reason,
    string? TimestampUrl = null);

/// <summary>
/// The converted print made into what the user asked for, in the order the standards leave open:
/// PDF/A-2b first (ISO 19005-2 — its identifier, the sRGB output intent from Windows' own profile);
/// then a password (AES-256; PDF/A forbids encryption, so the dialog never asks for both); then a
/// signature, last, because anything written after it would break it — through the encrypted door
/// when there is a password, so the signature verifies under the file's own key. Each step writes a
/// file of its own; the destination is replaced only once every step has succeeded.
/// </summary>
internal static class PdfFinishing
{
    private static readonly string SrgbProfile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");

    /// <summary>Makes <paramref name="converted"/> into <paramref name="request"/>'s file; what the user should hear about it.</summary>
    public static List<string> Finish(string converted, SaveRequest request)
    {
        var notes = new List<string>();
        var steps = new List<string>();
        string current = converted;
        try
        {
            if (request.PdfA)
            {
                string next = Step(current, steps, "pdfa");
                PdfAConversionResult result;
                using (var input = File.OpenRead(current))
                using (var output = File.Create(next))
                    result = PdfAConverter.ConvertToPdfA2b(input, output, new PdfAConversionOptions
                    {
                        DocumentTitle = request.Title,
                        IccProfile = File.Exists(SrgbProfile) ? File.ReadAllBytes(SrgbProfile) : null,
                    });
                if (!result.OutputWritten)
                    throw new InvalidOperationException("PDF/A conversion was refused: " + string.Join("; ", result.Warnings));
                if (!result.IsConformant)
                {
                    notes.Add("Saved without the PDF/A mark — " + (result.Blockers.Count > 0 ? result.Blockers[0] : "it could not be made conformant"));
                    foreach (string blocker in result.Blockers) Log.Warning("  PDF/A blocker: {Blocker}", blocker);
                }
                current = next;
            }

            if (request.Password != null)
            {
                string next = Step(current, steps, "locked");
                using (var input = File.OpenRead(current))
                using (var output = File.Create(next))
                    PdfEncryptor.Encrypt(input, output, new EncryptionParameters
                    {
                        UserPassword = request.Password,
                        OwnerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                        Algorithm = EncryptionAlgorithm.AES256,
                    });
                current = next;
            }

            if (request.CertificateThumbprint != null)
            {
                X509Certificate2 certificate = CertStoreLookup.FindByThumbprint(request.CertificateThumbprint)
                    ?? throw new InvalidOperationException("The signing certificate is no longer in your certificate store.");
                string next = Step(current, steps, "signed");
                using (certificate)
                using (var signer = new CertStoreExternalSigner(certificate))
                using (var input = File.OpenRead(current))
                using (var output = File.Create(next))
                {
                    var parameters = new SignatureParameters
                    {
                        ExternalSigner = signer,
                        Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason,
                        // The user's own authority: a timestamp it does not give fails the signing,
                        // and so the save, rather than leaving the signature without one.
                        TimestampAuthorityUrl = string.IsNullOrWhiteSpace(request.TimestampUrl) ? null : request.TimestampUrl,
                    };
                    if (request.Password != null) PdfEncryptionOperations.Sign(input, output, request.Password, parameters);
                    else new PdfSigner().Sign(input, output, parameters);
                    // The slot is this thread's own; it says what a default verification refuses.
                    if (PdfSigner.LastSignerWarning is { } warning)
                        notes.Add("Signed, but a reader checking the signature will refuse it — " + warning);
                }
                current = next;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(request.Destination)!);
            File.Copy(current, request.Destination, overwrite: true);
            return notes;
        }
        finally
        {
            foreach (string step in steps) TryDelete(step);
            TryDelete(converted);
        }
    }

    private static string Step(string current, List<string> steps, string name)
    {
        string next = Path.Combine(Path.GetDirectoryName(current)!, $"{Path.GetFileNameWithoutExtension(current)}.{name}.pdf");
        steps.Add(next);
        return next;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Log.Warning(ex, "{Path} could not be deleted", path); }
    }
}
