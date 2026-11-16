// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Windows;
using System.Windows.Input;
using Serilog;
using Tetitulz.Pdf.WinForms.PdfViewer;
using MessageBox = System.Windows.MessageBox;

namespace Tetitulz.Pdf.CompareViewer;

public partial class MainWindow : Window
{
    // ── State ───────────────────────────────────────────────────────────────
    private string[] _pdfFiles  = [];
    private int      _fileIndex = -1;
    private int      _pageIndex = 0;
    private int      _pageCount = 0;

    // Left pane: the library's own viewer, forms fillable. The folder, file and page controls are this
    // window's; zoom, find, save, print and the sidebar are the viewer's own toolbar.
    private readonly PdfViewerControl _viewer;

    // Right pane: Acrobat Reader's viewer, or null when Reader is not installed (the pane says so).
    private readonly AcrobatViewer? _acrobat;

    // ── Init ────────────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();

        _viewer = CreateViewer();
        _acrobat = CreateAcrobatViewer();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Right || e.Key == Key.PageDown) { OnNextPage(null!, null!); e.Handled = true; }
            if (e.Key == Key.Left  || e.Key == Key.PageUp)   { OnPrevPage(null!, null!); e.Handled = true; }
        };

        // TetitulzPdfCompare.exe <folder> opens that folder at once.
        Loaded += (_, _) =>
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && Directory.Exists(args[1])) OpenFolder(args[1]);
        };
    }

    private PdfViewerControl CreateViewer()
    {
        var viewer = new PdfViewerControl
        {
            Features               = PdfViewerFeatures.Default | PdfViewerFeatures.FormFilling | PdfViewerFeatures.Comments
                                     | PdfViewerFeatures.Signatures,
            ZoomMode               = PdfZoomMode.FitWidth,   // as Acrobat's FitH beside it
            ShowOpenButton         = false,                  // files come from the folder above
            ShowNavigationControls = false,                  // the page controls above drive both panes
        };
        // Acrobat's control reports no scroll position, so the sync is one way and by page: the page the
        // viewer shows is the page Acrobat is moved to, and nothing follows a scroll inside Acrobat.
        viewer.PageChanged += (_, e) =>
        {
            _pageCount = e.PageCount;
            if (e.PageIndex == _pageIndex) { UpdatePageLabel(); return; }
            _pageIndex = e.PageIndex;
            UpdatePageLabel();
            SyncAcrobatPage();
        };
        ViewerHost.Child = viewer;
        return viewer;
    }

    private AcrobatViewer? CreateAcrobatViewer()
    {
        if (!AcrobatViewer.IsRegistered)
        {
            AcrobatUnavailableText.Text = Environment.Is64BitProcess
                ? "Adobe Acrobat Reader (64-bit) is not installed."
                : "Adobe Acrobat Reader is not installed.";
            AcrobatUnavailableText.Visibility = Visibility.Visible;
            return null;
        }
        try
        {
            var viewer = new AcrobatViewer();
            AcrobatHost.Child = viewer;
            return viewer;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not create the Acrobat Reader control");
            AcrobatUnavailableText.Text       = $"Acrobat Reader control failed: {ex.Message}";
            AcrobatUnavailableText.Visibility = Visibility.Visible;
            return null;
        }
    }

    // ── Toolbar handlers ────────────────────────────────────────────────────

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardFormChanges()) return;
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description         = "Select folder containing PDF files",
            ShowNewFolderButton = false,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        OpenFolder(dlg.SelectedPath);
    }

    /// <summary>Lists the folder's PDFs (subfolders included) and shows the first.</summary>
    public void OpenFolder(string folder)
    {
        _pdfFiles = Directory.GetFiles(folder, "*.pdf", SearchOption.AllDirectories)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .ToArray();

        if (_pdfFiles.Length == 0) { StatusText.Text = "No PDF files found in selected folder"; return; }

        _fileIndex = 0;
        LoadCurrentFile();
    }

    private void OnPrevFile(object sender, RoutedEventArgs e)
    {
        if (_fileIndex <= 0 || !ConfirmDiscardFormChanges()) return;
        _fileIndex--;
        LoadCurrentFile();
    }

    private void OnNextFile(object sender, RoutedEventArgs e)
    {
        if (_fileIndex >= _pdfFiles.Length - 1 || !ConfirmDiscardFormChanges()) return;
        _fileIndex++;
        LoadCurrentFile();
    }

    // The viewer raises PageChanged, which moves the label and Acrobat.
    private void OnPrevPage(object sender, RoutedEventArgs e) => _viewer.PreviousPage();

    private void OnNextPage(object sender, RoutedEventArgs e) => _viewer.NextPage();

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(CurrentPathBox.Text))
            System.Windows.Clipboard.SetText(CurrentPathBox.Text);
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardFormChanges()) e.Cancel = true;
    }

    // Filled-in values live only in the viewer until its Save button writes them; moving to another
    // file forgets them, so ask first.
    private bool ConfirmDiscardFormChanges()
    {
        if (!_viewer.IsModified) return true;
        return MessageBox.Show(
                   "The form has unsaved changes (use Save in the viewer's toolbar to keep them).\n\nDiscard them?",
                   "Unsaved form changes", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
               == MessageBoxResult.OK;
    }

    private void SyncAcrobatPage()
    {
        if (_acrobat == null) return;
        try { _acrobat.GoToPage(_pageIndex); }
        catch (Exception ex) { Log.Warning(ex, "Acrobat setCurrentPage failed"); }
    }

    // ── Core load logic ─────────────────────────────────────────────────────

    private void LoadCurrentFile()
    {
        if (_fileIndex < 0 || _fileIndex >= _pdfFiles.Length) return;
        string path = _pdfFiles[_fileIndex];

        CurrentPathBox.Text = path;
        FileIndexText.Text  = $"{_fileIndex + 1} / {_pdfFiles.Length}";

        // Both panes start at page 1. The page index is reset first so the viewer's PageChanged for
        // page 1 is not taken for a move and sent to Acrobat ahead of its own load.
        _pageIndex = 0;
        ShowInViewer(path);
        ShowInAcrobat(path);
        UpdatePageLabel();
    }

    // Left panel: the library's viewer. An encrypted file opens under an empty user password; one
    // that needs a password is reported, as Acrobat beside it asks for its own.
    private void ShowInViewer(string path)
    {
        try
        {
            _viewer.LoadFile(path);
            StatusText.Text       = $"{Path.GetFileName(path)} — {_viewer.PageCount} pages";
            RenderStatusText.Text = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Viewer failed to open {Path}", path);
            _viewer.CloseDocument();
            _pageCount = 0;
            StatusText.Text       = $"Left pane cannot open {Path.GetFileName(path)}";
            RenderStatusText.Text = ex.Message;
        }
    }

    // Right panel: Adobe Acrobat Reader
    private void ShowInAcrobat(string path)
    {
        if (_acrobat == null) return;
        try
        {
            _acrobat.Open(path);
            RightStatusText.Text = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Acrobat Reader failed to open {Path}", path);
            RightStatusText.Text = $" (error: {ex.Message})";
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void UpdatePageLabel()
        => PageIndexText.Text = _pageCount > 0 ? $"{_pageIndex + 1} / {_pageCount}" : "— / —";
}
