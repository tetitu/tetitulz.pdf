// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Serilog;
using Serilog.Events;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.BatchRenderer;

/// <summary>
/// Renders every page of every PDF under a folder and reports what failed. Each file is opened once,
/// as one <see cref="PdfRenderSession"/> whose caches every page of it shares: a one-shot render per
/// page would parse the whole document again for each page, so the time would grow with the square
/// of the page count.
/// </summary>
internal static class Program
{
    private const int DefaultDpi = 150;
    private const int ExitClean = 0;
    private const int ExitUsage = 1;
    private const int ExitFailures = 2;

    private const string Usage =
        "TetitulzBatchRender <folder> [--dpi N] [--png | --jpg] [--out DIR] [--password-env NAME]\n" +
        "  Renders every page of every PDF under <folder> (subfolders included).\n" +
        "  --dpi N            render resolution (default 150)\n" +
        "  --png, --jpg       write each page as an image, keeping the folder's subfolders\n" +
        "  --out DIR          where the images go (default <folder>/png or <folder>/jpg)\n" +
        "  --password-env N   the password for encrypted files, read from environment variable N;\n" +
        "                     without it an encrypted file opens only when its user password is empty\n" +
        "Exit code: 0 every page rendered, 1 usage error, 2 a file or page failed.";

    private static int Main(string[] args)
    {
        // An encrypted file is decrypted by the Encryption assembly, which a reference alone does not
        // install: without this every encrypted file would render as nothing.
        Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();

        if (!TryParse(args, out Options? options, out string? error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return ExitUsage;
        }

        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TetitulzBatchRender", "logs");
        Directory.CreateDirectory(logDir);
        string logFile = Path.Combine(logDir, $"batch-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            return Run(options!, logFile);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>What the command line asked for.</summary>
    private sealed record Options(string Folder, int Dpi, PdfImageFormat? Format, string? OutDir, string? Password);

    private static bool TryParse(string[] args, out Options? options, out string? error)
    {
        options = null;
        error = null;
        string? folder = null, outDir = null, password = null;
        int dpi = DefaultDpi;
        PdfImageFormat? format = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            switch (a.ToLowerInvariant())
            {
                case "--help": case "-h": case "/?":
                    error = "";
                    return false;
                case "--dpi":
                    if (!hasValue || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out dpi)
                        || dpi < 1 || dpi > 2400)
                    {
                        error = "error: --dpi takes a whole number from 1 to 2400.";
                        return false;
                    }
                    break;
                case "--png":
                case "--jpg":
                    if (format != null) { error = "error: give --png or --jpg, not both."; return false; }
                    format = a.Equals("--png", StringComparison.OrdinalIgnoreCase) ? PdfImageFormat.Png : PdfImageFormat.Jpeg;
                    break;
                case "--out":
                    if (!hasValue) { error = "error: --out takes a folder."; return false; }
                    outDir = args[++i];
                    break;
                case "--password-env":
                    if (!hasValue) { error = "error: --password-env takes the name of an environment variable."; return false; }
                    string name = args[++i];
                    password = Environment.GetEnvironmentVariable(name);
                    if (password == null) { error = $"error: the environment variable {name} is not set."; return false; }
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { error = $"error: unknown option {a}."; return false; }
                    if (folder != null) { error = $"error: one folder only ({folder}, then {a})."; return false; }
                    folder = a;
                    break;
            }
        }

        if (folder == null)
        {
            Console.Write("Folder path: ");
            folder = Console.ReadLine()?.Trim().Trim('"');
        }
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            error = $"error: folder not found: {folder}";
            return false;
        }

        if (outDir != null && format == null)
        {
            error = "error: --out names where images go; give --png or --jpg as well.";
            return false;
        }
        if (format != null)
            outDir = Path.GetFullPath(outDir ?? Path.Combine(folder, format == PdfImageFormat.Png ? "png" : "jpg"));
        options = new Options(Path.GetFullPath(folder), dpi, format, outDir, password);
        return true;
    }

    private static int Run(Options options, string logFile)
    {
        Log.Information("TetitulzBatchRender — log file: {LogFile}", logFile);
        if (options.OutDir != null)
        {
            Directory.CreateDirectory(options.OutDir);
            Log.Information("Writing {Format} pages to: {OutDir}", options.Format, options.OutDir);
        }
        Log.Information("Scanning: {Folder} at {Dpi} dpi", options.Folder, options.Dpi);

        string[] pdfs = Directory.GetFiles(options.Folder, "*.pdf", SearchOption.AllDirectories)
                                 .Where(f => options.OutDir == null || !f.StartsWith(options.OutDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToArray();
        if (pdfs.Length == 0)
        {
            Log.Warning("No PDF files found in {Folder}", options.Folder);
            return ExitClean;
        }
        Log.Information("Found {Count} PDF files", pdfs.Length);

        // Rendering runs on this thread, so every diagnostic an event reports belongs to the file in hand.
        int fileDiagnostics = 0;
        RenderDiagnostics.OnEvent += (kind, detail, ex) =>
        {
            fileDiagnostics++;
            if (ex != null) Log.Warning(ex, "[RenderDiag:{Kind}] {Detail}", kind, detail);
            else Log.Debug("[RenderDiag:{Kind}] {Detail}", kind, detail);
        };

        var totalTimer = Stopwatch.StartNew();
        int totalPages = 0, failedPdfs = 0, failedPages = 0, filesWithDiagnostics = 0;
        var failures = new List<string>();
        var renderOptions = new PdfRenderOptions { Password = options.Password ?? "" };

        for (int fi = 0; fi < pdfs.Length; fi++)
        {
            string path = pdfs[fi];
            string name = Path.GetRelativePath(options.Folder, path);
            Log.Information("[{Num}/{Total}] {Name}", fi + 1, pdfs.Length, name);
            fileDiagnostics = 0;
            var fileTimer = Stopwatch.StartNew();

            PdfRenderSession session;
            try
            {
                session = PdfRenderSession.Open(path, renderOptions);
            }
            catch (Exception ex)
            {
                string why = IsEncrypted(path)
                    ? (options.Password == null
                        ? "ENCRYPTED  {0}: needs a password (--password-env)"
                        : "ENCRYPTED  {0}: the password given does not open it")
                    : "OPEN  {0}: " + ex.GetType().Name + ": " + ex.Message;
                Log.Error(ex, "  OPEN FAILED — {Name}", name);
                failedPdfs++;
                failures.Add(string.Format(CultureInfo.InvariantCulture, why, name));
                continue;
            }

            using (session)
            {
                int pageCount = session.PageCount;
                if (pageCount == 0)
                {
                    // Nothing rendered is not a clean result, whatever the cause.
                    Log.Warning("  NO PAGES — {Name}", name);
                    failedPdfs++;
                    failures.Add($"EMPTY  {name}: page count is 0");
                    continue;
                }
                totalPages += pageCount;

                bool pdfHadError = false;
                for (int pi = 0; pi < pageCount; pi++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        byte[] image = session.RenderPageToImage(pi, options.Format ?? PdfImageFormat.Png, options.Dpi);
                        if (options.OutDir != null)
                        {
                            string ext = options.Format == PdfImageFormat.Jpeg ? ".jpg" : ".png";
                            string stem = Path.Combine(options.OutDir, Path.ChangeExtension(name, null));
                            string outPath = pageCount == 1 ? stem + ext : $"{stem}_p{pi + 1}{ext}";
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                            File.WriteAllBytes(outPath, image);
                        }
                        Log.Debug("  Page {Page}/{Total} OK ({Bytes} bytes, {Ms}ms)",
                            pi + 1, pageCount, image.Length, sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "  Page {Page}/{Total} FAILED — {Name}", pi + 1, pageCount, name);
                        failedPages++;
                        pdfHadError = true;
                        failures.Add($"PAGE  {name} p{pi + 1}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                if (pdfHadError) failedPdfs++;
                if (fileDiagnostics > 0)
                {
                    filesWithDiagnostics++;
                    Log.Information("  {Pages} page(s) in {Ms} ms, {Count} render diagnostic(s) — see the log",
                        pageCount, fileTimer.ElapsedMilliseconds, fileDiagnostics);
                }
                else
                {
                    Log.Information("  {Pages} page(s) in {Ms} ms", pageCount, fileTimer.ElapsedMilliseconds);
                }
            }
        }

        totalTimer.Stop();
        Log.Information("════════════════════════════════════════");
        Log.Information("Done in {Elapsed:F1}s", totalTimer.Elapsed.TotalSeconds);
        Log.Information("PDFs:  {Total} total, {Failed} with errors, {Diag} with render diagnostics",
            pdfs.Length, failedPdfs, filesWithDiagnostics);
        Log.Information("Pages: {Total} total, {Failed} failed", totalPages, failedPages);

        if (failures.Count > 0)
        {
            Log.Warning("════ FAILURES ════");
            foreach (string line in failures) Log.Warning("  {Line}", line);
        }
        else
        {
            Log.Information("All pages rendered successfully.");
        }
        Log.Information("Log saved to: {LogFile}", logFile);
        return failedPages > 0 || failedPdfs > 0 ? ExitFailures : ExitClean;
    }

    /// <summary>Whether a file that would not open is encrypted, so the failure can say so.</summary>
    private static bool IsEncrypted(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = PdfDocument.Open(fs);
            return doc.IsEncrypted;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
