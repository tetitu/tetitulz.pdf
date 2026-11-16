// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using Serilog;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Printing;

namespace Tetitulz.Pdf.PrinterTray;

/// <summary>
/// The printer's listener in the notification area. Each job is converted on a worker thread, the
/// save dialog asks about it on this thread — one job at a time: the listener hands the next one on
/// only when this one is done — and the PDF is finished (PDF/A, password, signature) on a worker
/// again; a notification says it is saved, and clicking it opens it. The menu opens the PDF folder,
/// turns the start at logon on and off, installs or removes the printer, and opens the logs.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostart = new("Start at logon");
    private readonly ToolStripMenuItem _install = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TraySettings _settings = TraySettings.Load();
    private readonly JobConverter _converter;
    private readonly Task _listening;
    private string? _lastSaved;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _converter = new JobConverter(Path.Combine(Product.Printer.AppData, "pending"), Path.Combine(Product.Printer.AppData, "failed"),
                                      keepPostScript: false);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open the PDF folder", null, (_, _) => OpenFolder(_settings.Folder));
        menu.Items.Add(new ToolStripSeparator());
        _autostart.Click += (_, _) => ToggleAutostart();
        menu.Items.Add(_autostart);
        _install.Click += (_, _) => InstallOrRemove();
        menu.Items.Add(_install);
        menu.Items.Add("Open the log folder", null, (_, _) => OpenFolder(Path.Combine(Product.Printer.AppData, "logs")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => UpdateMenu();

        _icon = new NotifyIcon
        {
            Icon = PdfIcon(),
            Text = "Tetitulz PDF — print to it to save a PDF",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenFolder(_settings.Folder);
        _icon.BalloonTipClicked += (_, _) => { if (_lastSaved != null && File.Exists(_lastSaved)) Open(_lastSaved); };

        if (!Product.Printer.IsInstalled(Product.Printer.DefaultPrinterName))
            _icon.ShowBalloonTip(8000, "Tetitulz PDF", "The printer is not installed yet. Right-click this icon and choose 'Install the printer'.", ToolTipIcon.Info);

        var listener = new JobListener(Product.Printer.PipeName, Path.Combine(Product.Printer.AppData, "spool"),
                                       new SpoolerJobs(Product.Printer.PortName), HandleAsync);
        _listening = Task.Run(async () =>
        {
            try
            {
                await listener.RunAsync(_stop.Token);
            }
            catch (PipeAlreadyServedException ex)
            {
                // Said, not swallowed: a tray icon that looks alive while another process takes
                // the prints is the state this refusal exists to end.
                Log.Error("{Reason}", ex.Message);
                Notify("Tetitulz PDF is not receiving prints",
                       "Another program already serves the printer's port. See the log folder.", ToolTipIcon.Error);
            }
        });
        Log.Information("Tray listener started on {Port}", Product.Printer.PortName);
    }

    // ── a job ───────────────────────────────────────────────────────────────

    private async Task HandleAsync(ReceivedJob job)
    {
        string? converted = await Task.Run(() => _converter.Convert(job.PostScriptPath, job.Received, job.DocumentName));
        if (converted == null)
        {
            Notify("The print could not be converted", "See the log folder for why.", ToolTipIcon.Error);
            return;
        }

        string documentName = job.DocumentName ?? Path.GetFileNameWithoutExtension(converted);
        SaveRequest? request = await OnUi(() =>
        {
            using var dialog = new SaveDialog(documentName, PageCount(converted), _settings);
            return dialog.ShowDialog() == DialogResult.OK ? dialog.Request : null;
        });
        if (request == null)
        {
            Log.Information("The print of {Document} was not saved", documentName);
            TryDelete(converted);
            return;
        }

        try
        {
            List<string> notes = await Task.Run(() => PdfFinishing.Finish(converted, request));
            Log.Information("Saved {Pdf}", request.Destination);
            _lastSaved = request.Destination;
            Notify($"Saved {Path.GetFileName(request.Destination)}",
                   notes.Count > 0 ? string.Join(Environment.NewLine, notes) : "Click to open it.",
                   notes.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            if (request.OpenAfterSaving) Open(request.Destination);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The PDF could not be saved to {Pdf}", request.Destination);
            Notify("The PDF was not saved", ex.Message, ToolTipIcon.Error);
        }
    }

    /// <summary>Runs <paramref name="ask"/> on this thread and answers what it answered.</summary>
    private Task<T> OnUi<T>(Func<T> ask)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try { answer.SetResult(ask()); }
            catch (Exception ex) { answer.SetException(ex); }
        }, null);
        return answer.Task;
    }

    private static int PageCount(string pdf)
    {
        try
        {
            using var file = File.OpenRead(pdf);
            using var document = PdfDocument.Open(file);
            return document.Pages.Count;
        }
        catch (Exception ex) when (ex is IOException or Core.PdfException)
        {
            Log.Warning(ex, "The pages of {Pdf} could not be counted", pdf);
            return 0;
        }
    }

    private void Notify(string title, string text, ToolTipIcon icon)
        => _ui.Post(_ => _icon.ShowBalloonTip(6000, title, text, icon), null);

    // ── the menu ────────────────────────────────────────────────────────────

    /// <summary>The command a logon starts this program with.</summary>
    private static string AutostartCommand => $"\"{Environment.ProcessPath}\"";

    private void UpdateMenu()
    {
        _autostart.Checked = string.Equals(Product.Printer.Autostart(), AutostartCommand, StringComparison.OrdinalIgnoreCase);
        _install.Text = Product.Printer.IsInstalled(Product.Printer.DefaultPrinterName) ? "Remove the printer…" : "Install the printer…";
    }

    private void ToggleAutostart()
    {
        bool on = !string.Equals(Product.Printer.Autostart(), AutostartCommand, StringComparison.OrdinalIgnoreCase);
        Product.Printer.SetAutostart(on ? AutostartCommand : null);
        Log.Information("Start at logon {State}", on ? "on" : "off");
    }

    private void InstallOrRemove()
    {
        bool installed = Product.Printer.IsInstalled(Product.Printer.DefaultPrinterName);
        if (installed && MessageBox.Show("Remove the Tetitulz PDF printer from this computer?", "Tetitulz PDF",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        int code = installed ? Product.Printer.Uninstall(Product.Printer.DefaultPrinterName) : Product.Printer.Install(Product.Printer.DefaultPrinterName);
        if (code == 0 && !installed) Product.Printer.SetAutostart(AutostartCommand);
        Notify(code == 0 ? (installed ? "The printer is removed" : "The printer is installed") : "That did not work",
               code == 0 ? (installed ? "Tetitulz PDF is no longer a printer here." : "Print to 'Tetitulz PDF' from any program.")
                         : "See the log folder for why.",
               code == 0 ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Open(folder);
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException ex) { Log.Warning(ex, "{Path} could not be deleted", path); }
    }

    /// <summary>The tray's icon: a red page corner with "PDF" on it, drawn at the size the shell asks.</summary>
    private static Icon PdfIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var red = new SolidBrush(Color.FromArgb(200, 32, 40));
            g.FillRectangle(red, 0, 0, size, size);
            using var font = new Font("Segoe UI", size * 0.36f, FontStyle.Bold, GraphicsUnit.Pixel);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("PDF", font, Brushes.White, new RectangleF(0, 0, size, size), format);
        }
        IntPtr handle = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }

    protected override void ExitThreadCore()
    {
        _stop.Cancel();
        try { _listening.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException ex) { Log.Warning(ex, "The listener did not stop cleanly"); }
        _icon.Visible = false;
        _icon.Dispose();
        Log.Information("Tray listener stopped");
        base.ExitThreadCore();
    }
}
