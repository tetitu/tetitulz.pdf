// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Org.BouncyCastle.Security;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.WpfViewer.Models;

/// <summary>Which certificates the viewer trusts when it verifies a signature.</summary>
public enum TrustSource
{
    /// <summary>None: a signature is checked for integrity, and its chain is reported untrusted.</summary>
    None,
    /// <summary>The root certificates Windows trusts, the user's and the machine's.</summary>
    WindowsRoots,
    /// <summary>The certificate files in one folder.</summary>
    Folder,
}

/// <summary>What the viewer remembers between runs: the trust choice and the signing defaults. Never a password.</summary>
public sealed class ViewerSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TetituPdfViewer", "settings.json");

    public TrustSource Trust { get; set; } = TrustSource.WindowsRoots;
    public string TrustFolder { get; set; } = "";
    public string SignReason { get; set; } = "";
    public string SignLocation { get; set; } = "";
    public string TimestampUrl { get; set; } = "";
    public string CertificateThumbprint { get; set; } = "";
    public string PfxPath { get; set; } = "";

    /// <summary>The saved settings, or defaults when there are none or they cannot be read.</summary>
    public static ViewerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(FilePath)) ?? new ViewerSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file must not stop the viewer: the defaults stand in.
        }
        return new ViewerSettings();
    }

    /// <summary>Writes the settings; false when the folder cannot be written.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The verification options the trust choice makes: its anchors, or the library's defaults when
    /// it is <see cref="TrustSource.None"/>. <paramref name="note"/> says what was trusted, or why a
    /// folder could not be read (the options then trust nothing).
    /// </summary>
    public SignatureVerificationOptions VerificationOptions(out string note)
    {
        var options = new SignatureVerificationOptions();
        switch (Trust)
        {
            case TrustSource.WindowsRoots:
            {
                var anchors = WindowsRoots();
                options.TrustAnchors = anchors;
                note = $"trusting Windows' {anchors.Count} root certificates";
                break;
            }
            case TrustSource.Folder:
                if (Tetitulz.Pdf.SignCli.TrustAnchors.TryLoad(TrustFolder, out var fromFolder, out string? why))
                {
                    options.TrustAnchors = fromFolder;
                    note = $"trusting {fromFolder.Count} certificate(s) in {TrustFolder}";
                }
                else
                {
                    note = $"trusting nothing: {why}";
                }
                break;
            default:
                note = "trusting no certificate (integrity only)";
                break;
        }
        return options;
    }

    /// <summary>The root certificates in the current user's and the local machine's Root stores.</summary>
    private static List<Org.BouncyCastle.X509.X509Certificate> WindowsRoots()
    {
        var found = new Dictionary<string, Org.BouncyCastle.X509.X509Certificate>(StringComparer.OrdinalIgnoreCase);
        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (X509Certificate2 c in store.Certificates)
                {
                    using (c)
                    {
                        if (!found.ContainsKey(c.Thumbprint))
                            found[c.Thumbprint] = DotNetUtilities.FromX509Certificate(c);
                    }
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A store this account cannot open adds nothing.
            }
        }
        return found.Values.ToList();
    }
}
