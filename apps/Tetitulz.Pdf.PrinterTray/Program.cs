// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Serilog;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Printing;

namespace Tetitulz.Pdf.PrinterTray;

/// <summary>
/// <c>TetitulzPdfPrinterTray</c>: the Tetitulz PDF printer's listener in the notification area, a save
/// dialog for every print. One listener serves the printer at a time; when another holds it — the
/// console <c>TetitulzPdfPrinter listen</c> — this one says so and does not start.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(Product.Printer.AppData, "logs", "tray-.txt"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        try
        {
            ApplicationConfiguration.Initialize();
            using var single = new Mutex(initiallyOwned: true, Product.Printer.ListenerMutexName, out bool first);
            if (!first)
            {
                MessageBox.Show("Another Tetitulz PDF listener is already serving the printer — this app, or the console " +
                                "'TetitulzPdfPrinter listen'. Close it, then start this app again.",
                                "Tetitulz PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }

            JobConverter.SizeForPrintJobs();
            PdfEncryptionOperations.Register();
            Application.Run(new TrayContext());
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The tray app stopped");
            MessageBox.Show(ex.Message, "Tetitulz PDF stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
