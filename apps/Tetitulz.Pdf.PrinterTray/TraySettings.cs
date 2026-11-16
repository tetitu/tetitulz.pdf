// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text.Json;
using Serilog;
using Tetitulz.Pdf.Printing;

namespace Tetitulz.Pdf.PrinterTray;

/// <summary>
/// What the save dialog remembers between prints: the folder, whether to open the PDF, PDF/A, and the
/// certificate it signed with (by thumbprint), the reason and the timestamp authority. Never a password. Kept as JSON beside the
/// listener's logs; a file that will not read is logged and the defaults used.
/// </summary>
internal sealed class TraySettings
{
    public string Folder { get; set; } = Product.Printer.DefaultOutput;
    public bool OpenAfterSaving { get; set; } = true;
    public bool PdfA { get; set; }
    public bool Sign { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? Reason { get; set; }

    /// <summary>The timestamp authority a signature asks, or none.</summary>
    public string? TimestampUrl { get; set; }

    private static string FilePath => Path.Combine(Product.Printer.AppData, "tray-settings.json");

    public static TraySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(FilePath)) ?? new TraySettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "The settings in {Path} could not be read; the defaults are used", FilePath);
        }
        return new TraySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Product.Printer.AppData);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "The settings could not be saved to {Path}", FilePath);
        }
    }
}
