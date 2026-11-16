// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO;
using Serilog;
using Serilog.Events;
using Tetitulz.Pdf.Printing;

namespace Tetitulz.Pdf.VirtualPrinter;

/// <summary>
/// <c>TetitulzPdfPrinter</c>: a printer every Windows program can print to, each job saved as a PDF.
/// <c>install</c> adds it (elevated), <c>listen</c> receives its jobs, <c>convert</c> turns one
/// PostScript file into a PDF the same way, <c>uninstall</c> removes it.
/// </summary>
internal static class Program
{
    /// <summary>Tetitulz PDF's printer: its names, folders, install and logon start.</summary>
    private static readonly PrinterSetup Printer = new(PrinterIdentity.TetitulzPdf);
    private static readonly string AppData = Printer.AppData;
    private static readonly string DefaultOutput = Printer.DefaultOutput;

    private const string Usage = """
        TetitulzPdfPrinter — a virtual printer that saves each print job as a PDF.

          install   [--name "Tetitulz PDF"] [--autostart]   add the printer (asks for elevation)
          uninstall [--name "Tetitulz PDF"]                 remove the printer and stop autostart
          autostart [--off]                                 start this listener at every logon, or stop
          listen    [--out <folder>] [--open] [--keep-ps]   receive jobs and write the PDFs
          convert   <job.ps> [<out.pdf>]                    convert one PostScript file

        PDFs go to Documents\Tetitulz PDF unless --out names another folder. The listener must be
        running while you print: --autostart starts it, windowless, at every logon.
        Logs: %LOCALAPPDATA%\TetitulzPdfPrinter\logs
        """;

    static async Task<int> Main(string[] args)
    {
        // Document titles name the PDFs, in any script; the console's code page would garble them.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        var options = Options.Parse(args.AsSpan(1));
        if (options.Error != null)
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        StartLog(command);
        if (command is "listen" or "convert") JobConverter.SizeForPrintJobs();
        try
        {
            return command switch
            {
                "install"   => Install(options),
                "uninstall" => Uninstall(options),
                "autostart" => Autostart(!options.Off),
                "listen"    => await ListenAsync(options),
                "convert"   => Convert(options),
                _           => UnknownCommand(command),
            };
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine(Usage);
        return 1;
    }

    private static void StartLog(string command)
    {
        string logDirectory = Path.Combine(AppData, "logs");
        Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(Path.Combine(logDirectory, $"{command}-.txt"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    // ── listen ──────────────────────────────────────────────────────────────

    private static async Task<int> ListenAsync(Options options)
    {
        using var single = new Mutex(initiallyOwned: true, Printer.ListenerMutexName, out bool first);
        if (!first)
        {
            Log.Error("A listener is already running; the printer's pipe is served by one listener at a time");
            return 2;
        }

        string output = Path.GetFullPath(options.Output ?? DefaultOutput);
        var converter = new JobConverter(output, Path.Combine(AppData, "failed"), options.KeepPostScript);
        var spooler = new SpoolerJobs(Printer.PortName);
        var listener = new JobListener(Printer.PipeName, Path.Combine(AppData, "spool"), spooler, job =>
        {
            string? pdf = converter.Convert(job.PostScriptPath, job.Received, job.DocumentName);
            if (pdf != null && options.Open)
                Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true })?.Dispose();
            return Task.CompletedTask;
        });

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        Log.Information("Listening on \\\\.\\pipe\\{Pipe}; PDFs go to {Output}. Ctrl+C stops.", Printer.PipeName, output);
        try
        {
            await listener.RunAsync(stop.Token);
        }
        catch (PipeAlreadyServedException ex)
        {
            Log.Error("{Reason}", ex.Message);
            return 2;
        }
        Log.Information("Stopped");
        return 0;
    }

    // ── convert ─────────────────────────────────────────────────────────────

    private static int Convert(Options options)
    {
        if (options.Positional.Count is 0 or > 2)
        {
            Console.Error.WriteLine("convert takes a PostScript file and, optionally, the PDF to write.");
            return 1;
        }
        string source = Path.GetFullPath(options.Positional[0]);
        if (!File.Exists(source))
        {
            Log.Error("No such file: {Source}", source);
            return 1;
        }

        // A copy is converted, so the caller's file is left where it is whatever the outcome.
        string output = options.Positional.Count == 2
            ? Path.GetDirectoryName(Path.GetFullPath(options.Positional[1]))!
            : Path.GetDirectoryName(source)!;
        string copy = Path.Combine(Path.GetTempPath(), $"tetitulz-convert-{Guid.NewGuid():N}.ps");
        File.Copy(source, copy);
        var converter = new JobConverter(output, Path.Combine(AppData, "failed"), keepPostScript: false);
        string? pdf = converter.Convert(copy, File.GetLastWriteTime(source));
        if (pdf == null) return 1;
        if (options.Positional.Count == 2)
        {
            string wanted = Path.GetFullPath(options.Positional[1]);
            File.Move(pdf, wanted, overwrite: true);
            Log.Information("Written: {Pdf}", wanted);
        }
        return 0;
    }

    // ── install / uninstall ─────────────────────────────────────────────────

    private static int Install(Options options)
    {
        string name = options.Name ?? Printer.DefaultPrinterName;
        int code = Printer.Install(name);
        if (code != 0)
        {
            Log.Error("The printer was not installed (exit code {Code}); see the log above", code);
            return code;
        }
        Log.Information("The printer \"{Name}\" is installed", name);
        if (options.Autostart) return Autostart(on: true);
        Log.Information("Start the listener before printing: TetitulzPdfPrinter listen");
        return 0;
    }

    /// <summary>
    /// Starts this executable's listener at every logon — windowless, through <c>conhost --headless</c>
    /// — or stops doing so: the user's own Run key, no elevation.
    /// </summary>
    private static int Autostart(bool on)
    {
        if (!on)
        {
            Printer.SetAutostart(null);
            Log.Information("The listener no longer starts at logon");
            return 0;
        }
        Printer.SetAutostart($"conhost.exe --headless \"{Environment.ProcessPath}\" listen");
        Log.Information("The listener starts at every logon, from {Exe}; start it now with: TetitulzPdfPrinter listen", Environment.ProcessPath);
        return 0;
    }

    private static int Uninstall(Options options)
    {
        int code = Printer.Uninstall(options.Name ?? Printer.DefaultPrinterName);
        if (code != 0) Log.Error("The printer was not removed (exit code {Code})", code);
        return code;
    }

    // ── options ─────────────────────────────────────────────────────────────

    private sealed class Options
    {
        public string? Name;
        public string? Output;
        public bool Open;
        public bool KeepPostScript;
        public bool Autostart;
        public bool Off;
        public readonly List<string> Positional = new();
        public string? Error;

        public static Options Parse(ReadOnlySpan<string> args)
        {
            var o = new Options();
            for (int k = 0; k < args.Length; k++)
            {
                switch (args[k].ToLowerInvariant())
                {
                    case "--name" when k + 1 < args.Length: o.Name = args[++k]; break;
                    case "--out" when k + 1 < args.Length: o.Output = args[++k]; break;
                    case "--open": o.Open = true; break;
                    case "--keep-ps": o.KeepPostScript = true; break;
                    case "--autostart": o.Autostart = true; break;
                    case "--off": o.Off = true; break;
                    default:
                        if (args[k].StartsWith("--", StringComparison.Ordinal))
                        {
                            o.Error = $"Unknown option, or one missing its value: {args[k]}";
                            return o;
                        }
                        o.Positional.Add(args[k]);
                        break;
                }
            }
            return o;
        }
    }
}
