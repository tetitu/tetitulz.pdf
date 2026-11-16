// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.WinForms.PdfViewer;
using Tetitulz.Pdf.WpfViewer.Commands;
using Tetitulz.Pdf.WpfViewer.Models;
using Tetitulz.Pdf.WpfViewer.Services;
using Tetitulz.Pdf.WpfViewer.Views;

namespace Tetitulz.Pdf.WpfViewer.ViewModels;

/// <summary>
/// The window's commands over one open document. Every change goes through
/// <see cref="DocumentSession"/>: one edit at a time, each an undo step; a command that needs a
/// place on the page asks for it with a tool on the page; a command that rewrites the whole file
/// asks first when that would remove a signature.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string AppName = "Tetitulz PDF Viewer";

    private readonly PdfService _pdf = new();
    private readonly DocumentSession _session = new();
    private readonly RecentFiles _recentFiles = new();
    private readonly ViewerSettings _settings = ViewerSettings.Load();
    private IDocumentView? _view;
    private IReadOnlyList<PageRect> _marks = Array.Empty<PageRect>();
    private TaskCompletionSource<PageRect?>? _pendingTool;
    private SigningChoice? _fieldSigning;

    /// <summary>The password a pending Encrypt will write the file under; the document is protected by it once saved.</summary>
    private string? _pendingPassword;

    public MainViewModel()
    {
        _session.Changed += OnSessionChanged;
        _recentFiles.Load();
        foreach (string f in _recentFiles.Items) RecentItems.Add(f);

        // File
        OpenCmd = Async(DoOpen);
        OpenRecentCmd = new RelayCommand(p => { if (p is string path) _ = Guarded(() => OpenFromPathAsync(path)); });
        SaveCmd = Async(() => SaveAsync(saveAs: false), () => IsOpen);
        SaveAsCmd = Async(() => SaveAsync(saveAs: true), () => IsOpen);
        PrintCmd = Cmd(() => View?.Print(), () => IsOpen);
        DefaultAppCmd = new RelayCommand(_ =>
        {
            try
            {
                Shell.ShellRegistration.OfferToBecomeDefault();
                Status = "Choose Tetitulz PDF Viewer for .pdf in Default apps.";
            }
            catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        });
        MergeCmd = Async(DoMerge);
        ImportSvgCmd = Async(DoImportSvg);
        UndoCmd = Async(DoUndo, () => IsOpen && _session.UndoLabels.Count > 0);
        RedoCmd = Async(DoRedo, () => IsOpen && _session.RedoLabels.Count > 0);
        // View
        PrevCmd = Cmd(() => { if (Page > 0) Page--; }, () => IsOpen && Page > 0);
        NextCmd = Cmd(() => { if (Page < Total - 1) Page++; }, () => IsOpen && Page < Total - 1);
        ZoomInCmd = Cmd(() => ViewZoom(v => v.ZoomIn()), () => IsOpen);
        ZoomOutCmd = Cmd(() => ViewZoom(v => v.ZoomOut()), () => IsOpen);
        FitWidthCmd = Cmd(() => ViewZoom(v => v.FitWidth()), () => IsOpen);
        FitPageCmd = Cmd(() => ViewZoom(v => v.FitPage()), () => IsOpen);
        ActualSizeCmd = Cmd(() => ViewZoom(v => v.ActualSize()), () => IsOpen);
        FullScreenCmd = Cmd(DoFullScreen, () => IsOpen);
        ContinuousScrollCmd = Cmd(() => { if (View != null) { IsContinuousScroll = View.ToggleContinuous(); Status = IsContinuousScroll ? "Continuous scroll" : "One page at a time"; } }, () => IsOpen);
        FindCmd = Cmd(() => View?.Find(null), () => IsOpen);
        ExtractTextCmd = Async(DoExtractText, () => IsOpen);
        ExtractTablesCmd = Async(DoExtractTables, () => IsOpen);
        // Insert
        AddTextCmd = Async(DoAddText, CanEdit);
        InsertImageCmd = Async(DoInsertImage, CanEdit);
        AddBarcodeCmd = Async(DoBarcode, CanEdit);
        WatermarkCmd = Async(DoWatermark, CanEdit);
        HeaderFooterCmd = Async(DoHeaderFooter, CanEdit);
        StampCmd = Async(DoStamp, CanEdit);
        LinkCmd = Async(DoLink, CanEdit);
        AddAttachmentCmd = Async(DoAddAttachment, CanEdit);
        // Annotate
        NoteCmd = Async(DoNote, CanEdit);
        HighlightCmd = Async(() => MarkupEdit(TextMarkup.Highlight, "Highlight", "highlight", (d, r) => _pdf.AddHighlight(d, r)), CanEdit);
        UnderlineCmd = Async(() => MarkupEdit(TextMarkup.Underline, "Underline", "underline", (d, r) => _pdf.AddUnderline(d, r)), CanEdit);
        StrikeOutCmd = Async(() => MarkupEdit(TextMarkup.StrikeOut, "Strike out", "strike out", (d, r) => _pdf.AddStrikeOut(d, r)), CanEdit);
        RectangleCmd = Async(() => BoxEdit("Rectangle", "Drag out the rectangle — Esc cancels", (d, r) => _pdf.AddRectangle(d, r)), CanEdit);
        FreeTextCmd = Async(DoFreeText, CanEdit);
        // Pages
        OrganizeCmd = Async(DoOrganize, CanEdit);
        RotateCmd = Async(DoRotate, CanEdit);
        DeletePageCmd = Async(DoDeletePage, () => CanEdit() && Total > 1);
        InsertPageCmd = Async(() => Edit("Insert a blank page", d => _pdf.InsertBlankPage(d, Page), "Blank page inserted"), CanEdit);
        CropCmd = Async(() => BoxEdit("Crop the page", "Drag out the part of the page to keep — Esc cancels", (d, r) => _pdf.CropPage(d, r)), CanEdit);
        ExtractPageCmd = Async(DoExtractPage, CanEdit);
        NUpCmd = Async(() => Rewrite("4-up", d => _pdf.NUp(d), "Four pages to a sheet"), CanEdit);
        // Review
        A11yCheckCmd = Async(() => Report("Accessibility", d => _pdf.AuditAccessibility(d)), () => IsOpen);
        A11yFixCmd = Async(DoA11yFix, CanEdit);
        ValidatePdfACmd = Async(() => Report("PDF/A", d => _pdf.ValidatePdfA(d)), () => IsOpen);
        ValidatePdfXCmd = Async(() => Report("PDF/X", d => _pdf.ValidatePdfX(d)), () => IsOpen);
        CompareCmd = Async(DoCompare, () => IsOpen);
        MetadataCmd = Async(() => Report("Properties", d => string.Join("\n", _pdf.GetMetadata(d).Select(kv => $"{kv.Key}: {kv.Value}"))), () => IsOpen);
        BookmarksCmd = Async(() => Report("Bookmarks", d => { var b = _pdf.GetBookmarks(d); return b.Length == 0 ? "No bookmarks" : string.Join("\n", b.Select(x => x.page is int p ? $"p{p + 1}: {x.title}" : $"p?: {x.title}")); }), () => IsOpen);
        ListFieldsCmd = Async(() => Report("Form fields", d => { var n = _pdf.GetFormFieldNames(d); return n.Length == 0 ? "No form fields" : string.Join("\n", n); }), () => IsOpen);
        AttachmentsCmd = Async(() => Report("Attachments", d => { var n = _pdf.GetAttachmentNames(d); return n.Length == 0 ? "No attachments" : string.Join("\n", n); }), () => IsOpen);
        FlattenCmd = Async(() => Rewrite("Flatten the form", d => _pdf.FlattenForms(d), "The form's fields are now part of the page"), CanEdit);
        // Security
        SignCmd = Async(DoSign, CanEdit);
        VerifyCmd = Async(DoVerify, () => IsOpen);
        TrustWindowsCmd = Cmd(() => SetTrust(TrustSource.WindowsRoots, ""));
        TrustFolderCmd = Cmd(DoTrustFolder);
        TrustNoneCmd = Cmd(() => SetTrust(TrustSource.None, ""));
        EncryptCmd = Async(DoEncrypt, CanEdit);
        DecryptCmd = Async(DoDecrypt, () => IsOpen);
        MarkRedactionCmd = Async(DoMarkRedaction, CanEdit);
        MarkTextRedactionCmd = Async(DoMarkText, CanEdit);
        ClearRedactionsCmd = Cmd(() => SetMarks(Array.Empty<PageRect>(), "Redaction marks cleared"), () => _marks.Count > 0);
        ApplyRedactionsCmd = Async(DoApplyRedactions, () => CanEdit() && _marks.Count > 0);
        SanitizeCmd = Async(DoSanitize, CanEdit);
        // Export and tools
        OptimizeCmd = Async(DoOptimize, CanEdit);
        RepairCmd = Async(DoRepair, CanEdit);
        LinearizeCmd = Async(() => Rewrite("Linearize", d => _pdf.Linearize(d), "Linearized for the web"), CanEdit);
        ConvertPdfACmd = Async(() => Rewrite("Convert to PDF/A", d => _pdf.ConvertToPdfA(d), "Converted to PDF/A-1b"), CanEdit);
        ExportHtmlCmd = Async(() => ExportText("HTML|*.html", ".html", d => _pdf.ToHtml(d)), () => IsOpen);
        ExportSvgCmd = Async(() => ExportText("SVG|*.svg", ".svg", d => _pdf.ToSvg(d, Page)), () => IsOpen);
        ExportDocxCmd = Async(() => ExportBytes("Word|*.docx", ".docx", d => _pdf.ExportToDocx(d)), () => IsOpen);
        ExportExcelCmd = Async(() => ExportBytes("Excel|*.xlsx", ".xlsx", d => _pdf.ExportToExcel(d)), () => IsOpen);
        ExportPptxCmd = Async(() => ExportBytes("PowerPoint|*.pptx", ".pptx", d => _pdf.ExportToPptx(d)), () => IsOpen);
        ExtractImagesCmd = Async(DoExtractImages, () => IsOpen);
        OcrPageCmd = Async(() => ShowText("OCR", d => _pdf.OcrPage(d, Page)), () => IsOpen);
        OcrDocumentCmd = Async(() => ShowText("OCR", d => _pdf.OcrDocument(d)), () => IsOpen);
        AboutCmd = Cmd(() => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog());
    }

    // ===== The page display =====

    /// <summary>
    /// The page display — the library's viewer control, set by the window. Every document the
    /// commands produce is shown through it, and the page the user moves to there is <see cref="Page"/>.
    /// </summary>
    public IDocumentView? View
    {
        get => _view;
        set
        {
            if (_view != null)
            {
                _view.PageChanged -= OnViewPageChanged;
                _view.FileDropped -= OnViewFileDropped;
                _view.ToolCompleted -= OnToolCompleted;
                _view.ToolCancelled -= OnToolCancelled;
                _view.SignatureRequested -= OnSignatureRequested;
                _view.FieldSigned -= OnFieldSigned;
            }
            _view = value;
            if (_view != null)
            {
                _view.PageChanged += OnViewPageChanged;
                _view.FileDropped += OnViewFileDropped;
                _view.ToolCompleted += OnToolCompleted;
                _view.ToolCancelled += OnToolCancelled;
                _view.SignatureRequested += OnSignatureRequested;
                _view.FieldSigned += OnFieldSigned;
                _view.SignatureVerification = _settings.VerificationOptions(out _);
            }
        }
    }

    private void OnViewPageChanged(int page) => Set(ref _page, page, nameof(Page));

    private async void OnViewFileDropped(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".pdf" && ext != ".docx") { Status = "Only PDF and DOCX files can be dropped"; return; }
        await Guarded(() => OpenFromPathAsync(path));
    }

    private void OnToolCompleted(PageRect r) { var t = _pendingTool; _pendingTool = null; t?.TrySetResult(r); }
    private void OnToolCancelled() { var t = _pendingTool; _pendingTool = null; t?.TrySetResult(null); }

    /// <summary>Asks for a place on the page with a tool; null when the user cancelled.</summary>
    private Task<PageRect?> PickAsync(PageToolShape shape, string hint)
    {
        if (View == null) return Task.FromResult<PageRect?>(null);
        _pendingTool?.TrySetResult(null);
        var tcs = new TaskCompletionSource<PageRect?>();
        _pendingTool = tcs;
        Status = hint;
        View.BeginTool(shape, hint);
        return tcs.Task;
    }

    // ===== State =====

    private int _page, _total;
    private double _zoom = 1.0;
    private string _status = "Ready", _title = AppName, _pageText = "", _infoText = "", _undoText = "Undo", _redoText = "Redo";
    private bool _isOpen, _isModified, _isContinuousScroll;

    public ObservableCollection<string> RecentItems { get; } = new();

    /// <summary>The page the commands act on: the display's, which this moves.</summary>
    public int Page { get => _page; set { if (Set(ref _page, value)) View?.GoToPage(value); } }
    public int Total { get => _total; private set => Set(ref _total, value); }
    public double Zoom { get => _zoom; private set => Set(ref _zoom, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool IsModified { get => _isModified; private set => Set(ref _isModified, value); }
    public bool IsContinuousScroll { get => _isContinuousScroll; private set => Set(ref _isContinuousScroll, value); }
    public string PageText { get => _pageText; private set => Set(ref _pageText, value); }
    public string InfoText { get => _infoText; private set => Set(ref _infoText, value); }
    public string UndoText { get => _undoText; private set => Set(ref _undoText, value); }
    public string RedoText { get => _redoText; private set => Set(ref _redoText, value); }

    // ===== Commands =====
    public ICommand OpenCmd { get; } public ICommand OpenRecentCmd { get; } public ICommand SaveCmd { get; } public ICommand SaveAsCmd { get; }
    public ICommand PrintCmd { get; } public ICommand DefaultAppCmd { get; } public ICommand MergeCmd { get; } public ICommand ImportSvgCmd { get; }
    public ICommand UndoCmd { get; } public ICommand RedoCmd { get; }
    public ICommand PrevCmd { get; } public ICommand NextCmd { get; }
    public ICommand ZoomInCmd { get; } public ICommand ZoomOutCmd { get; } public ICommand FitWidthCmd { get; } public ICommand FitPageCmd { get; }
    public ICommand ActualSizeCmd { get; } public ICommand FullScreenCmd { get; } public ICommand ContinuousScrollCmd { get; }
    public ICommand FindCmd { get; } public ICommand ExtractTextCmd { get; } public ICommand ExtractTablesCmd { get; }
    public ICommand AddTextCmd { get; } public ICommand InsertImageCmd { get; } public ICommand AddBarcodeCmd { get; }
    public ICommand WatermarkCmd { get; } public ICommand HeaderFooterCmd { get; } public ICommand StampCmd { get; } public ICommand LinkCmd { get; }
    public ICommand AddAttachmentCmd { get; }
    public ICommand NoteCmd { get; } public ICommand HighlightCmd { get; } public ICommand UnderlineCmd { get; } public ICommand StrikeOutCmd { get; }
    public ICommand RectangleCmd { get; } public ICommand FreeTextCmd { get; }
    public ICommand OrganizeCmd { get; } public ICommand RotateCmd { get; } public ICommand DeletePageCmd { get; } public ICommand InsertPageCmd { get; }
    public ICommand CropCmd { get; } public ICommand ExtractPageCmd { get; } public ICommand NUpCmd { get; }
    public ICommand A11yCheckCmd { get; } public ICommand A11yFixCmd { get; } public ICommand ValidatePdfACmd { get; } public ICommand ValidatePdfXCmd { get; }
    public ICommand CompareCmd { get; } public ICommand MetadataCmd { get; } public ICommand BookmarksCmd { get; } public ICommand ListFieldsCmd { get; }
    public ICommand AttachmentsCmd { get; } public ICommand FlattenCmd { get; }
    public ICommand SignCmd { get; } public ICommand VerifyCmd { get; }
    public ICommand TrustWindowsCmd { get; } public ICommand TrustFolderCmd { get; } public ICommand TrustNoneCmd { get; }
    public ICommand EncryptCmd { get; } public ICommand DecryptCmd { get; }
    public ICommand MarkRedactionCmd { get; } public ICommand MarkTextRedactionCmd { get; } public ICommand ClearRedactionsCmd { get; } public ICommand ApplyRedactionsCmd { get; }
    public ICommand SanitizeCmd { get; }
    public ICommand OptimizeCmd { get; } public ICommand RepairCmd { get; } public ICommand LinearizeCmd { get; } public ICommand ConvertPdfACmd { get; }
    public ICommand ExportHtmlCmd { get; } public ICommand ExportSvgCmd { get; } public ICommand ExportDocxCmd { get; } public ICommand ExportExcelCmd { get; }
    public ICommand ExportPptxCmd { get; } public ICommand ExtractImagesCmd { get; } public ICommand OcrPageCmd { get; } public ICommand OcrDocumentCmd { get; }
    public ICommand AboutCmd { get; }

    // ===== Opening =====

    private async Task DoOpen()
    {
        var d = new OpenFileDialog { Filter = "PDF and Word|*.pdf;*.docx|PDF|*.pdf|Word|*.docx|All files|*.*" };
        if (d.ShowDialog() != true) return;
        await OpenFromPathAsync(d.FileName);
    }

    /// <summary>
    /// Opens a PDF or a DOCX (converted). An encrypted PDF opens under its empty user password when it
    /// has one, else the one the user types; its protection is kept for the save. A failed open
    /// leaves the document that was open as it was.
    /// </summary>
    public async Task OpenFromPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Status = "File not found"; return; }
        if (!await ConfirmDiscardAsync()) return;
        Status = $"Opening {Path.GetFileName(path)}...";

        PdfDocumentModel? opened;
        OpenedProtection? protection = null;
        try
        {
            if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                opened = await Task.Run(() => _pdf.OpenDocx(path));
            }
            else if (PdfService.IsEncrypted(path))
            {
                (opened, protection) = await OpenEncryptedAsync(path);
                if (opened == null) return;
            }
            else
            {
                opened = await Task.Run(() => _pdf.Open(path));
            }
        }
        catch (Exception ex)
        {
            Status = $"Open failed: {ex.Message}";
            MessageBox.Show($"Could not open {Path.GetFileName(path)}.\n\n{ex.Message}", "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _pendingPassword = null;
        _session.Open(opened, protection, modified: string.IsNullOrEmpty(opened.FilePath));
        SetMarks(Array.Empty<PageRect>(), null);
        _page = -1;
        Page = 0;
        if (!string.IsNullOrEmpty(opened.FilePath)) RememberRecent(opened.FilePath);
        Status = protection != null
            ? $"{opened.FileName} — {Total} pages — password-protected: a save keeps the protection"
            : $"{opened.FileName} — {Total} pages";
    }

    private async Task<(PdfDocumentModel? Doc, OpenedProtection? Protection)> OpenEncryptedAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        // The empty password first: a document that only restricts permissions opens with it, and
        // asking for a password nobody was given is a defect of its own.
        string? password = TryDecrypt(bytes, "", out byte[]? plain) ? "" : null;
        while (password == null)
        {
            var dlg = new PasswordWindow
            {
                Owner = Application.Current.MainWindow,
                Prompt = $"{Path.GetFileName(path)} is password-protected. Enter the password:",
                Title = "Password required",
            };
            if (dlg.ShowDialog() != true) { Status = "Cancelled"; return (null, null); }
            string typed = dlg.EnteredPassword ?? "";
            byte[]? decrypted = null;
            if (await Task.Run(() => TryDecrypt(bytes, typed, out decrypted))) { password = typed; plain = decrypted; break; }
            MessageBox.Show("That password does not open the document.", "Password required", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        PdfDocumentModel doc = PdfService.OpenFromBytes(plain!, Path.GetFileName(path), path);
        return (doc, new OpenedProtection(bytes, password, plain!));
    }

    private static bool TryDecrypt(byte[] encrypted, string password, out byte[]? plain)
        => ProtectedDocuments.TryDecrypt(encrypted, password, out plain);

    private async Task DoMerge()
    {
        var d = new OpenFileDialog { Filter = "PDF|*.pdf", Multiselect = true, Title = "Choose two or more PDFs to merge, in order" };
        if (d.ShowDialog() != true || d.FileNames.Length < 2) return;
        if (!await ConfirmDiscardAsync()) return;
        var pdfs = d.FileNames.Select(File.ReadAllBytes).ToArray();
        PdfDocumentModel merged = await Task.Run(() => _pdf.Merge(pdfs));
        _session.Open(merged, null, modified: true);
        SetMarks(Array.Empty<PageRect>(), null);
        Page = 0;
        Status = $"Merged {d.FileNames.Length} files — save to keep the result";
    }

    private async Task DoImportSvg()
    {
        var d = new OpenFileDialog { Filter = "SVG|*.svg" };
        if (d.ShowDialog() != true) return;
        if (!await ConfirmDiscardAsync()) return;
        PdfDocumentModel doc = await Task.Run(() => _pdf.ImportSvg(d.FileName));
        _session.Open(doc, null, modified: true);
        SetMarks(Array.Empty<PageRect>(), null);
        Page = 0;
        Status = $"Converted {Path.GetFileName(d.FileName)} — save to keep it";
    }

    private void RememberRecent(string path)
    {
        _recentFiles.Add(path);
        RecentItems.Clear();
        foreach (string f in _recentFiles.Items) RecentItems.Add(f);
    }

    // ===== Saving =====

    /// <summary>
    /// True when the document may be replaced: nothing is unsaved, or the user saved it or chose not
    /// to. Unsaved means an edit, or values filled in on the page.
    /// </summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsOpen || (!_session.IsModified && View?.HasFormEdits != true)) return true;
        switch (Prompts.SaveChanges(_session.Current?.FileName ?? "The document"))
        {
            case MessageBoxResult.Yes: return await SaveAsync(saveAs: false);
            case MessageBoxResult.No: return true;
            default: return false;
        }
    }

    /// <summary>Whether closing the window would lose anything.</summary>
    public bool HasUnsavedChanges => IsOpen && (_session.IsModified || View?.HasFormEdits == true);

    /// <summary>
    /// Writes the document: to the file it came from, or to one the user chooses (Save As, or a
    /// document that has no file yet). A document opened under a password is written protected again.
    /// </summary>
    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_session.Current == null) return false;
        if (View != null) await _session.FoldAsync(View.TakeFormEdits);
        PdfDocumentModel doc = _session.Current!;
        OpenedProtection? protection = _session.Protection;

        string path = doc.FilePath;
        if (saveAs || string.IsNullOrEmpty(path) || !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var d = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = Path.ChangeExtension(doc.FileName, ".pdf"), OverwritePrompt = true };
            if (d.ShowDialog() != true) return false;
            path = d.FileName;
        }

        bool pendingEncrypt = doc.SavesOtherBytes;
        var (bytes, kept) = pendingEncrypt ? (doc.Bytes, false) : await ProtectedBytesAsync(doc.Bytes, protection, isExtract: false);
        if (bytes == null) { Status = "Not saved"; return false; }

        try
        {
            // Written beside the target and moved over it, so a failed write leaves the file as it was.
            string temp = path + ".saving-" + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Not saved: {ex.Message}";
            MessageBox.Show($"The file could not be written.\n\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        string name = Path.GetFileName(path);
        if (pendingEncrypt && _pendingPassword != null)
        {
            // The file is now protected by the password just set: from here the document is edited
            // as plaintext and saved under that protection, as one opened with it would be.
            PdfDocumentModel shown = doc.AsShown(name, path);
            await _session.MarkSavedAsync(doc, shown, new OpenedProtection(bytes, _pendingPassword, shown.Bytes));
            _pendingPassword = null;
        }
        else if (kept)
        {
            // The plaintext that was saved is what the next save compares against: unchanged, the
            // file written is written again as it is.
            await _session.MarkSavedAsync(doc, doc.WithLocation(name, path), new OpenedProtection(bytes, protection!.Password, doc.Bytes));
        }
        else
        {
            // Written in the clear: from here a save writes it so, as it now is on disk.
            await _session.MarkSavedAsync(doc, doc.WithLocation(name, path), null);
        }
        RememberRecent(path);
        Status = kept || pendingEncrypt ? $"Saved, password-protected: {path}"
               : protection != null ? $"Saved WITHOUT its password protection: {path}"
               : $"Saved: {path}";
        return true;
    }

    /// <summary>
    /// The bytes a save writes for <paramref name="plaintext"/>, and whether they carry the
    /// protection the document was opened under; null bytes when the user declined.
    /// </summary>
    /// <remarks>
    /// A document opened under a password is shown and edited as plaintext, so writing that
    /// plaintext would turn a protected file into an open one with nothing said. The protection is
    /// put back instead (<see cref="PdfService.ProtectForSave"/>). The two things that cannot be
    /// done quietly are asked first, each defaulting to No: invalidating a signature by saving a
    /// changed copy, and writing in the clear when the protection cannot be written again.
    /// <paramref name="isExtract"/> is a part of the document (a split-off page): it never carried
    /// the signature, so that question is not asked of it.
    /// </remarks>
    private async Task<(byte[]? Bytes, bool Protected)> ProtectedBytesAsync(byte[] plaintext, OpenedProtection? protection, bool isExtract)
    {
        if (protection == null) return (plaintext, false);

        ProtectedSave result = await Task.Run(() => _pdf.ProtectForSave(protection, plaintext, allowSignatureLoss: isExtract));
        if (result.NeedsSignatureConsent)
        {
            if (!Prompts.YesNo("Signed document",
                    "This document is digitally signed, and it has been changed since it was opened.\n\n" +
                    "Saving the changed copy invalidates the signature. Save it anyway?"))
                return (null, false);
            result = await Task.Run(() => _pdf.ProtectForSave(protection, plaintext, allowSignatureLoss: true));
        }
        if (result.Bytes != null) return (result.Bytes, true);

        return Prompts.YesNo("Password protection",
                "This document was opened with a password, and its protection cannot be written back:\n\n" +
                result.Refusal + "\n\nSave a copy WITHOUT password protection instead?")
            ? (plaintext, false)
            : (null, false);
    }

    // ===== The edit machinery =====

    /// <summary>True when a document is open and can be changed: not waiting to be saved encrypted.</summary>
    private bool CanEdit() => IsOpen && _session.Current?.SavesOtherBytes != true;

    private bool RefuseWhilePendingEncryption()
    {
        if (_session.Current?.SavesOtherBytes != true) return false;
        Status = "The document is waiting to be saved encrypted: save it, or undo Encrypt, before changing it";
        return true;
    }

    /// <summary>One edit through the session, the form's typed values folded in first.</summary>
    private async Task<bool> Edit(string label, Func<PdfDocumentModel, PdfDocumentModel> edit, string done)
    {
        if (RefuseWhilePendingEncryption()) return false;
        bool ok = await _session.MutateAsync(label, edit, View == null ? null : View.TakeFormEdits);
        if (ok) Status = done;
        return ok;
    }

    private async Task<(bool Done, T? Result)> Edit<T>(string label, Func<PdfDocumentModel, (PdfDocumentModel, T)> edit)
    {
        if (RefuseWhilePendingEncryption()) return (false, default);
        return await _session.MutateAsync(label, edit, View == null ? null : View.TakeFormEdits);
    }

    /// <summary>
    /// True when a whole-file rewrite may go ahead: the document is unsigned, or the user agreed that
    /// the rewrite removes its signatures.
    /// </summary>
    private bool ConfirmRewrite(string what)
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return false;
        if (!PdfService.HasSignature(doc.Bytes)) return true;
        return Prompts.YesNo("Signed document",
            $"This document is digitally signed. {what} rewrites the whole file, and the signatures do not survive it.\n\nContinue?");
    }

    private async Task Rewrite(string label, Func<PdfDocumentModel, PdfDocumentModel> edit, string done)
    {
        if (!ConfirmRewrite(label)) return;
        await Edit(label, edit, done);
    }

    private async Task BoxEdit(string label, string hint, Func<PdfDocumentModel, PageRect, PdfDocumentModel> edit)
    {
        if (RefuseWhilePendingEncryption()) return;
        if (await PickAsync(PageToolShape.Box, hint) is not PageRect r) { Status = "Cancelled"; return; }
        Page = r.Page;
        await Edit(label, d => edit(d, r), $"{label}: done on page {r.Page + 1}");
    }

    /// <summary>
    /// Marks the text the user selected, line by line, when there is a selection; otherwise asks for
    /// a box dragged over the text.
    /// </summary>
    private async Task MarkupEdit(TextMarkup kind, string label, string verb, Func<PdfDocumentModel, PageRect, PdfDocumentModel> boxEdit)
    {
        IReadOnlyList<SelectedTextPart> selection = View?.SelectedText ?? Array.Empty<SelectedTextPart>();
        if (selection.Count == 0)
        {
            await BoxEdit(label, $"Drag over the text to {verb} (or select it first) — Esc cancels", boxEdit);
            return;
        }
        string pages = selection.Count == 1 ? $"page {selection[0].Page + 1}" : $"{selection.Count} pages";
        await Edit(label, d => _pdf.MarkText(d, kind, selection), $"{label}: the selected text on {pages}");
    }

    private void OnSessionChanged()
    {
        PdfDocumentModel? doc = _session.Current;
        IsOpen = doc != null;
        IsModified = _session.IsModified;
        Total = doc?.PageCount ?? 0;
        if (_page >= Total) _page = Math.Max(0, Total - 1);
        Title = doc == null ? AppName
            : $"{(IsModified ? "• " : "")}{doc.FileName}{(_session.Protection != null ? " (password-protected)" : "")} — {AppName}";
        UndoText = _session.UndoLabels.Count > 0 ? $"Undo {_session.UndoLabels[0]}" : "Undo";
        RedoText = _session.RedoLabels.Count > 0 ? $"Redo {_session.RedoLabels[0]}" : "Redo";
        ShowDocument();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Shows the current document at the current page.</summary>
    private void ShowDocument()
    {
        if (View == null) return;
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) { View.Clear(); return; }
        try
        {
            // A document opened under a password is shown decrypted, and no signature covers a
            // decrypted copy: the display would call a sound signature broken. Verify checks the
            // file as it was opened instead, and signing goes through the encrypted door.
            bool plain = _session.Protection == null && !doc.SavesOtherBytes;
            View.Show(doc.ShownBytes, Page, allowFormFilling: !doc.SavesOtherBytes, showSignatures: _session.Protection == null);
            View.SigningEnabled = plain;
            View.RedactionMarks = _marks;
            Zoom = View.Zoom;
        }
        catch (Exception ex)
        {
            Status = $"Render error: {ex.Message}";
        }
    }

    private async Task DoUndo()
    {
        if (View != null) await _session.FoldAsync(View.TakeFormEdits);
        string? label = await _session.UndoAsync();
        Status = label == null ? "Nothing to undo" : $"Undone: {label}";
    }

    private async Task DoRedo()
    {
        string? label = await _session.RedoAsync();
        Status = label == null ? "Nothing to redo" : $"Redone: {label}";
    }

    // ===== Insert =====

    private async Task DoAddText()
    {
        if (await PickAsync(PageToolShape.Point, "Click where the text starts — Esc cancels") is not PageRect at) { Status = "Cancelled"; return; }
        if (Prompts.Text("Add text", "The text to put on the page:") is not string text) return;
        await Edit("Add text", d => _pdf.AddText(d, at.Page, at.X, at.Y, text, 12), $"Text added on page {at.Page + 1}");
    }

    private async Task DoInsertImage()
    {
        var d = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (d.ShowDialog() != true) return;
        byte[] image = await File.ReadAllBytesAsync(d.FileName);
        if (await PickAsync(PageToolShape.Box, "Drag out where the picture goes — Esc cancels") is not PageRect r) { Status = "Cancelled"; return; }
        await Edit("Insert a picture", doc => _pdf.InsertImage(doc, r, image), $"Picture placed on page {r.Page + 1}");
    }

    private async Task DoBarcode()
    {
        if (Prompts.Text("Barcode", "The text the Code 128 barcode carries:") is not string data) return;
        if (await PickAsync(PageToolShape.Point, "Click where the barcode's bottom-left corner goes — Esc cancels") is not PageRect at) { Status = "Cancelled"; return; }
        await Edit("Barcode", d => _pdf.AddBarcode(d, at.Page, at.X, at.Y, data), $"Barcode added on page {at.Page + 1}");
    }

    private async Task DoWatermark()
    {
        if (Prompts.Text("Watermark", "The watermark's text, drawn across every page:", "CONFIDENTIAL") is not string text) return;
        await Edit("Watermark", d => _pdf.AddWatermark(d, text, 0.3f), "Watermark added to every page");
    }

    private async Task DoHeaderFooter()
    {
        if (Prompts.TwoTexts("Header and footer", "Header (empty for none):", "", "Footer — {page} is the page number, {total} the count:", "Page {page} of {total}") is not { } texts) return;
        var (header, footer) = texts;
        await Edit("Header and footer", d => _pdf.AddHeaderFooter(d, header, footer), "Header and footer added to every page");
    }

    private async Task DoStamp()
    {
        if (Prompts.Choice("Stamp", "The stamp's word:", new[] { "Approved", "Not Approved", "Draft", "Final", "Confidential", "For Comment", "Experimental", "Expired" }, "Approved") is not string name) return;
        if (await PickAsync(PageToolShape.Box, "Drag out the stamp — Esc cancels") is not PageRect r) { Status = "Cancelled"; return; }
        await Edit("Stamp", d => _pdf.AddStamp(d, r, name), $"Stamp \"{name}\" added on page {r.Page + 1}");
    }

    private async Task DoLink()
    {
        if (await PickAsync(PageToolShape.Box, "Drag over what the link covers — Esc cancels") is not PageRect r) { Status = "Cancelled"; return; }
        if (Prompts.Text("Link", "Where the link goes (https://…, mailto:…):", "https://") is not string uri) return;
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out Uri? target) || target.Scheme is not ("http" or "https" or "mailto"))
        {
            Status = "A link goes to a web address (http, https) or an e-mail address (mailto)";
            return;
        }
        await Edit("Link", d => _pdf.AddLink(d, r, target.AbsoluteUri), $"Link added on page {r.Page + 1}");
    }

    private async Task DoAddAttachment()
    {
        var d = new OpenFileDialog { Filter = "All files|*.*" };
        if (d.ShowDialog() != true) return;
        byte[] data = await File.ReadAllBytesAsync(d.FileName);
        string name = Path.GetFileName(d.FileName);
        await Edit("Attach a file", doc => _pdf.AddAttachment(doc, name, data, "application/octet-stream"), $"Attached: {name}");
    }

    // ===== Annotate =====

    private async Task DoNote()
    {
        if (await PickAsync(PageToolShape.Point, "Click where the note goes — Esc cancels") is not PageRect at) { Status = "Cancelled"; return; }
        if (Prompts.Text("Note", "The note:", "", multiline: true) is not string text) return;
        await Edit("Note", d => _pdf.AddNote(d, at.Page, at.X, at.Y, text), $"Note added on page {at.Page + 1}");
    }

    private async Task DoFreeText()
    {
        if (await PickAsync(PageToolShape.Box, "Drag out the text box — Esc cancels") is not PageRect r) { Status = "Cancelled"; return; }
        if (Prompts.Text("Text box", "The text:", "", multiline: true) is not string text) return;
        await Edit("Text box", d => _pdf.AddFreeText(d, r, text), $"Text box added on page {r.Page + 1}");
    }

    // ===== Pages =====

    private async Task DoOrganize()
    {
        if (RefuseWhilePendingEncryption() || _session.Current == null) return;
        if (View != null) await _session.FoldAsync(View.TakeFormEdits);
        PdfDocumentModel doc = _session.Current!;
        var window = new PageOrganizerWindow(doc.ShownBytes, PdfService.PageRotations(doc), pages => ExtractPagesAsync(pages));
        if (window.ShowDialog() != true) return;
        var order = window.Result;
        var inserted = window.Inserted;
        bool unchanged = inserted.Count == 0 && order.Count == doc.PageCount
            && order.Select((p, i) => p.SourcePage == i && p.Rotation == PdfService.PageRotations(doc)[i]).All(x => x);
        if (unchanged) { Status = "The pages are as they were"; return; }
        if (!ConfirmRewrite("Organizing the pages")) return;
        await Edit("Organize pages", d => _pdf.Reorganize(d, order, inserted), $"Pages organized: {order.Count} page(s)");
        Page = 0;
    }

    private async Task DoRotate()
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        int page = Page;
        int next = (PdfService.PageRotations(doc)[page] + 90) % 360;
        await Edit("Rotate page", d => _pdf.RotatePage(d, page, next), $"Page {page + 1} turned to {next}°");
    }

    private async Task DoDeletePage()
    {
        int page = Page;
        await Edit("Delete page", d => _pdf.DeletePage(d, page), $"Page {page + 1} deleted");
    }

    private async Task DoExtractPage() => await ExtractPagesAsync(new[] { Page });

    /// <summary>Writes pages (0-based, in order) to a file the user names, protected as the document is.</summary>
    private async Task ExtractPagesAsync(IReadOnlyList<int> pages)
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null || RefuseWhilePendingEncryption()) return;
        string suggested = $"{Path.GetFileNameWithoutExtension(doc.FileName)}_pages_{string.Join("_", pages.Select(p => p + 1))}.pdf";
        if (suggested.Length > 120) suggested = Path.GetFileNameWithoutExtension(doc.FileName) + "_extract.pdf";
        var sd = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = suggested };
        if (sd.ShowDialog() != true) return;
        byte[] extract = await Task.Run(() =>
        {
            using var i = new MemoryStream(doc.Bytes, writable: false);
            using var o = new MemoryStream();
            Tetitulz.Pdf.Plugins.PageEdits.PdfSplitter.ExtractPages(i, o, pages.Select(p => p + 1).ToList());
            return o.ToArray();
        });
        // A part of a password-protected document is written under the same protection.
        var (bytes, kept) = await ProtectedBytesAsync(extract, _session.Protection, isExtract: true);
        if (bytes == null) { Status = "Not saved"; return; }
        await File.WriteAllBytesAsync(sd.FileName, bytes);
        Status = $"{pages.Count} page(s) saved to {Path.GetFileName(sd.FileName)}{(kept ? ", password-protected as the document is" : "")}";
    }

    // ===== Review =====

    private async Task DoA11yFix()
    {
        if (!ConfirmRewrite("Fixing accessibility")) return;
        var (done, result) = await Edit<Tetitulz.Pdf.Plugins.Conformance.AccessibilityFixResult>("Fix accessibility", d => _pdf.FixAccessibility(d));
        if (!done || result == null) return;
        // The fixer's own verdict: its pass-through exits copy the bytes and say why in Warnings.
        Status = result.Warnings.Count > 0
            ? $"Accessibility: no changes — {string.Join("; ", result.Warnings)}"
            : result.FixesApplied.Count == 0 ? "Accessibility: nothing to fix" : $"Accessibility fixed: {result.FixesApplied.Count} change(s)";
    }

    private async Task DoCompare()
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        var d = new OpenFileDialog { Filter = "PDF|*.pdf", Title = "Compare the text with…" };
        if (d.ShowDialog() != true) return;
        string result = await Task.Run(() => { using var other = _pdf.Open(d.FileName); return _pdf.Compare(doc, other); });
        Status = result;
    }

    private async Task Report(string heading, Func<PdfDocumentModel, string> read)
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        string text = await Task.Run(() => read(doc));
        InfoText = heading + "\n" + new string('─', heading.Length) + "\n" + text;
        Status = $"{heading}: see the Info tab";
    }

    private async Task ShowText(string what, Func<PdfDocumentModel, string> read)
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        Status = $"{what}…";
        PageText = await Task.Run(() => read(doc));
        Status = $"{what}: {PageText.Length} characters in the Text tab";
    }

    private Task DoExtractText() => ShowText("Text of page " + (Page + 1), d => _pdf.ExtractText(d, Page));

    private async Task DoExtractTables()
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        int page = Page;
        var rows = await Task.Run(() => _pdf.ExtractTables(doc, page));
        PageText = rows.Length > 0 ? string.Join("\n", rows.Select(r => string.Join(" | ", r))) : "No table found on this page";
        Status = $"{rows.Length} table row(s) in the Text tab";
    }

    // ===== Signing and trust =====

    private async Task DoSign()
    {
        if (RefuseWhilePendingEncryption() || _session.Current == null) return;
        if (View != null) await _session.FoldAsync(View.TakeFormEdits);
        if (_session.Protection != null) { await SignProtectedAsync(); return; }

        var dialog = new SigningWindow(_settings, visible: true);
        if (dialog.ShowDialog() != true || dialog.Choice == null) return;
        using SigningChoice choice = dialog.Choice;
        if (await PickAsync(PageToolShape.Box, "Drag out where the signature shows — Esc cancels") is not PageRect r) { Status = "Not signed"; return; }
        Status = "Signing… a smart card may ask for its PIN";
        try
        {
            var (done, warning) = await Edit<string?>("Sign", d => _pdf.SignVisible(d, choice.Parameters, choice.Picture, r));
            if (!done) return;
            // The signature is written either way; what other software will refuse it for is said
            // now, to the signer, rather than later by whoever receives the document.
            Status = warning == null ? $"Signed on page {r.Page + 1} — save to keep it" : $"Signed, but a verifier will refuse it: {warning}";
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or System.Security.Cryptography.CryptographicException or IOException)
        {
            Status = $"Not signed: {ex.Message}";
        }
    }

    /// <summary>
    /// Signs a document opened under a password, through the encrypted door: the signature is made
    /// over the file as it was opened, under its own key, so it verifies with the password. Only an
    /// unchanged document can be signed this way — the changes would need saving first.
    /// </summary>
    private async Task SignProtectedAsync()
    {
        PdfDocumentModel doc = _session.Current!;
        OpenedProtection protection = _session.Protection!;
        if (!ReferenceEquals(doc.Bytes, protection.DecryptedOriginal))
        {
            Status = "Save the changes first: a password-protected document is signed as the file it is";
            return;
        }
        var dialog = new SigningWindow(_settings, visible: false);
        if (dialog.ShowDialog() != true || dialog.Choice == null) return;
        using SigningChoice choice = dialog.Choice;
        Status = "Signing… a smart card may ask for its PIN";
        try
        {
            var (signed, plain, warning) = await Task.Run(() =>
            {
                using var i = new MemoryStream(protection.EncryptedOriginal, writable: false);
                using var o = new MemoryStream();
                PdfEncryptionOperations.Sign(i, o, protection.Password, choice.Parameters);
                string? w = PdfSigner.LastSignerWarning;
                byte[] s = o.ToArray();
                TryDecrypt(s, protection.Password, out byte[]? p);
                return (s, p!, w);
            });
            PdfDocumentModel model = PdfService.OpenFromBytes(plain, doc.FileName, doc.FilePath);
            _session.Open(model, new OpenedProtection(signed, protection.Password, plain), modified: true);
            Status = warning == null ? "Signed (invisibly, under the document's password) — save to keep it" : $"Signed, but a verifier will refuse it: {warning}";
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or System.Security.Cryptography.CryptographicException or IOException)
        {
            Status = $"Not signed: {ex.Message}";
        }
    }

    private void OnSignatureRequested(object? sender, PdfViewerSignatureRequestEventArgs e)
    {
        var dialog = new SigningWindow(_settings, visible: false);
        if (dialog.ShowDialog() != true || dialog.Choice == null) return;
        _fieldSigning?.Dispose();
        _fieldSigning = dialog.Choice;
        e.Parameters = dialog.Choice.Parameters;
        Status = "Signing… a smart card may ask for its PIN";
    }

    private async void OnFieldSigned(object? sender, PdfViewerFieldSignedEventArgs e)
    {
        // The signer's warning is read on the thread that signed, which raised this.
        string? warning = e.Error == null ? PdfSigner.LastSignerWarning : null;
        _fieldSigning?.Dispose();
        _fieldSigning = null;
        if (e.Error != null) { Status = $"{e.FieldName} not signed: {e.Error}"; return; }
        if (View != null) await _session.FoldAsync(View.TakeFormEdits, $"Sign {e.FieldName}");
        Status = warning == null ? $"{e.FieldName} signed — save to keep it" : $"{e.FieldName} signed, but a verifier will refuse it: {warning}";
    }

    private async Task DoVerify()
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        OpenedProtection? opened = _session.Protection;
        var options = _settings.VerificationOptions(out string trustNote);
        IReadOnlyList<SignatureVerdict> verdicts;
        try
        {
            verdicts = await Task.Run(() => _pdf.VerifySignatures(doc, opened, options));
        }
        catch (Exception ex) when (ex is Tetitulz.Pdf.Core.PdfException or IOException)
        {
            Status = $"The signatures could not be checked: {ex.Message}";
            return;
        }
        if (verdicts.Count == 0) { Status = "This document is not signed"; InfoText = ""; return; }

        var sb = new StringBuilder($"Signatures — {trustNote}{(opened != null ? "; checked in the file as it was opened" : "")}\n");
        foreach (SignatureVerdict v in verdicts)
        {
            sb.Append($"\n{v.Field}: {(!v.Valid ? "INVALID" : v.Trusted ? "valid and trusted" : "valid, not trusted")} — {v.Signer}\n");
            foreach (string p in v.Problems) sb.Append("    ").Append(p).Append('\n');
        }
        InfoText = sb.ToString();
        int invalid = verdicts.Count(v => !v.Valid), untrusted = verdicts.Count(v => v.Valid && !v.Trusted);
        Status = invalid > 0 ? $"{invalid} of {verdicts.Count} signature(s) INVALID — see the Info tab"
               : untrusted > 0 ? $"All {verdicts.Count} signature(s) intact; {untrusted} not trusted ({trustNote})"
               : $"All {verdicts.Count} signature(s) valid and trusted";
    }

    private void DoTrustFolder()
    {
        var d = new OpenFolderDialog { Title = "A folder of trusted root certificates (.cer, .crt, .pem, .der)" };
        if (d.ShowDialog() != true) return;
        SetTrust(TrustSource.Folder, d.FolderName);
    }

    private void SetTrust(TrustSource source, string folder)
    {
        _settings.Trust = source;
        _settings.TrustFolder = folder;
        _settings.Save();
        var options = _settings.VerificationOptions(out string note);
        if (View != null) View.SignatureVerification = options;
        Status = $"Signatures are verified {note}";
    }

    // ===== Protection =====

    private async Task DoEncrypt()
    {
        if (RefuseWhilePendingEncryption()) return;
        var dlg = new PasswordWindow
        {
            Owner = Application.Current.MainWindow,
            Prompt = "Enter a password to protect this PDF (AES-256):",
            Title = "Set password",
            ShowConfirm = Visibility.Visible,
        };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.EnteredPassword)) return;
        string password = dlg.EnteredPassword;
        if (!ConfirmRewrite("Encrypting")) return;
        bool ok = await Edit("Encrypt", d =>
        {
            using PdfDocumentModel encrypted = _pdf.Encrypt(d, password, password);
            // The ciphertext has no readable catalog, so it does not drive the display; it is what
            // Save writes, and the plaintext stays shown.
            return PdfDocumentModel.WithBytes(d, encrypted.Bytes);
        }, "Encrypted — save to write the protected file");
        if (ok) _pendingPassword = password;
    }

    private async Task DoDecrypt()
    {
        if (_session.Current?.SavesOtherBytes == true) { Status = "Undo Encrypt to take the pending password back"; return; }
        if (_session.Protection == null) { Status = "This document has no password"; return; }
        if (!Prompts.YesNo("Remove password", "Save the document from now on without its password protection?")) return;
        if (await _session.RemoveProtectionAsync("Remove password"))
            Status = "Password protection removed — save to write the unprotected file";
    }

    // ===== Redaction =====

    private void SetMarks(IReadOnlyList<PageRect> marks, string? status)
    {
        _marks = marks;
        if (View != null) View.RedactionMarks = marks;
        if (status != null) Status = status;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DoMarkRedaction()
    {
        if (await PickAsync(PageToolShape.Box, "Drag over what to remove — Esc cancels; Apply removes every marked area") is not PageRect r) { Status = "Cancelled"; return; }
        SetMarks(_marks.Append(r).ToList(), $"{_marks.Count + 1} area(s) marked for redaction — Apply removes them");
    }

    private async Task DoMarkText()
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        if (Prompts.Text("Mark text for redaction", "Every place this text is drawn is marked:") is not string text) return;
        IReadOnlyList<PageRect> found = await Task.Run(() => _pdf.FindForRedaction(doc, text, matchCase: false));
        if (found.Count == 0) { Status = $"\"{text}\" is not in the document's text"; return; }
        SetMarks(_marks.Concat(found).ToList(), $"{found.Count} place(s) marked — review them on the pages, then Apply");
        Page = found[0].Page;
    }

    /// <summary>
    /// Removes every marked area for good, in one pass that writes a new file. A refusal (content the
    /// engine cannot remove without a leak) leaves the document as it was and is said; there is no
    /// fallback to a box drawn over text that is still there.
    /// </summary>
    private async Task DoApplyRedactions()
    {
        if (_marks.Count == 0) return;
        if (!Prompts.YesNo("Apply redactions",
                $"Remove what lies under the {_marks.Count} marked area(s)? The text and pictures there are deleted from the file, " +
                "not covered" + (PdfService.HasSignature(_session.Current!.Bytes) ? ", and the document's signatures do not survive it." : ".")))
            return;
        var marks = _marks.ToList();
        try
        {
            var (done, note) = await Edit<string?>("Redact", d => _pdf.Redact(d, marks));
            if (!done) return;
            SetMarks(Array.Empty<PageRect>(), note == null
                ? $"Redacted {marks.Count} area(s): the content was removed, not covered — save to keep it"
                : $"Redacted {marks.Count} area(s); note: {note}");
        }
        catch (Exception ex)
        {
            Status = $"Redaction refused, and the document is unchanged: {ex.Message}";
        }
    }

    private async Task DoSanitize()
    {
        if (!ConfirmRewrite("Sanitizing")) return;
        var (done, notes) = await Edit<string?>("Sanitize", d => _pdf.Sanitize(d));
        if (done) Status = notes == null ? "Sanitized: scripts, actions and embedded files removed" : $"Sanitized; note: {notes}";
    }

    // ===== Optimize, repair, export =====

    private async Task DoOptimize()
    {
        if (!ConfirmRewrite("Optimizing")) return;
        var (done, result) = await Edit<Tetitulz.Pdf.Plugins.Maintenance.OptimizationResult>("Optimize", d => _pdf.Optimize(d));
        if (!done || result == null) return;
        Status = result.FellBackToInput
            ? "Optimize could not make the file smaller; it is unchanged"
            : $"Optimized: {result.InputBytes / 1024:N0} KB → {result.OutputBytes / 1024:N0} KB ({result.ImagesOptimized} image(s), {result.DuplicatesRemoved} duplicate(s) removed)";
    }

    private async Task DoRepair()
    {
        if (!ConfirmRewrite("Repairing")) return;
        var (done, result) = await Edit<Tetitulz.Pdf.Base.Operations.Documents.PdfRepairResult>("Repair", d => _pdf.Repair(d));
        if (!done || result == null) return;
        Status = result.IsClean
            ? $"Repaired: {result.RecoveredObjectCount} object(s) recovered"
            : $"Repaired with {result.Errors.Count} problem(s): {string.Join("; ", result.Errors.Take(3))}";
    }

    private async Task ExportText(string filter, string extension, Func<PdfDocumentModel, string> export)
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        var d = new SaveFileDialog { Filter = filter, FileName = Path.ChangeExtension(doc.FileName, extension) };
        if (d.ShowDialog() != true) return;
        Status = "Exporting…";
        string text = await Task.Run(() => export(doc));
        await File.WriteAllTextAsync(d.FileName, text);
        Status = $"Exported: {d.FileName}";
    }

    private async Task ExportBytes(string filter, string extension, Func<PdfDocumentModel, byte[]> export)
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        var d = new SaveFileDialog { Filter = filter, FileName = Path.ChangeExtension(doc.FileName, extension) };
        if (d.ShowDialog() != true) return;
        Status = "Exporting…";
        byte[] bytes = await Task.Run(() => export(doc));
        await File.WriteAllBytesAsync(d.FileName, bytes);
        Status = $"Exported: {d.FileName}";
    }

    private async Task DoExtractImages()
    {
        PdfDocumentModel? doc = _session.Current;
        if (doc == null) return;
        var d = new OpenFolderDialog { Title = "The folder the pictures are written to" };
        if (d.ShowDialog() != true) return;
        string dir = d.FolderName;
        await Task.Run(() => _pdf.ExtractImagesToFolder(doc, dir));
        Status = $"Pictures extracted to {dir}";
    }

    // ===== Window =====

    private void DoFullScreen()
    {
        Window? win = Application.Current.MainWindow;
        if (win == null) return;
        bool full = win.WindowStyle == WindowStyle.None;
        win.WindowStyle = full ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        win.WindowState = WindowState.Maximized;
    }

    private void ViewZoom(Action<IDocumentView> zoom)
    {
        if (View == null) return;
        zoom(View);
        Zoom = View.Zoom;
    }

    // ===== Helpers =====

    /// <summary>Runs a command, reporting anything it throws in the status line instead of losing it.</summary>
    private async Task Guarded(Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
    }

    private AsyncRelayCommand Async(Func<Task> exec, Func<bool>? can = null) => new(() => Guarded(exec), can);

    private RelayCommand Cmd(Action exec, Func<bool>? can = null)
        => new(() => { try { exec(); } catch (Exception ex) { Status = $"Error: {ex.Message}"; } }, can);

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
