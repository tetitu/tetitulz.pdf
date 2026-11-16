// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.IO;
using System.Windows;
using Fluent;
using Tetitulz.Pdf.WinForms.PdfViewer;
using Tetitulz.Pdf.WpfViewer.ViewModels;

namespace Tetitulz.Pdf.WpfViewer;

public partial class MainWindow : RibbonWindow
{
    private readonly MainViewModel _vm;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        // GlassFrameThickness="0" in the XAML is what keeps the hosted viewer readable: under the
        // ribbon window's default glass frame, what the viewer draws with GDI — its tab names,
        // status line, sidebar — is composed washed out over the glass.
        var viewer = new PdfViewerControl
        {
            Features = PdfViewerFeatures.Default | PdfViewerFeatures.Comments | PdfViewerFeatures.Signatures,
        };
        ViewerHost.Child = viewer;
        _vm = new MainViewModel { View = new Views.WinFormsDocumentView(viewer) };
        DataContext = _vm;

        // TetitulzPdfViewer.exe <file> opens it at once.
        Loaded += async (_, _) =>
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) await _vm.OpenFromPathAsync(args[1]);
            OfferToBecomeDefaultOnce();
        };
    }

    /// <summary>
    /// When the installer's "Make it the default PDF app" was chosen: on this user's first start, unless
    /// the viewer already opens PDFs, ask once, and on yes open Default apps at the viewer.
    /// </summary>
    private void OfferToBecomeDefaultOnce()
    {
        try
        {
            if (!Shell.ShellRegistration.ShouldOfferDefault(Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser))
                return;
            Shell.ShellRegistration.MarkDefaultOffered(Microsoft.Win32.Registry.CurrentUser);
            MessageBoxResult answer = System.Windows.MessageBox.Show(this,
                "Make Tetitulz PDF Viewer the app that opens PDF files?\n\nWindows' Default apps opens at the viewer, where you choose it for .pdf.",
                "Tetitulz PDF Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) Shell.ShellRegistration.OfferToBecomeDefault();
        }
        catch (Exception ex)
        {
            _vm.Status = $"Could not offer to become the default PDF app: {ex.Message}";
        }
    }

    private void OnRecentOpened(object? sender, EventArgs e)
    {
        RecentButton.Items.Clear();
        if (_vm.RecentItems.Count == 0)
        {
            RecentButton.Items.Add(new Fluent.MenuItem { Header = "(no recent files)", IsEnabled = false });
            return;
        }
        foreach (string path in _vm.RecentItems)
        {
            var item = new Fluent.MenuItem { Header = path, IsEnabled = File.Exists(path) };
            item.Click += (_, _) => _vm.OpenRecentCmd.Execute(path);
            RecentButton.Items.Add(item);
        }
    }

    // Closing asks before unsaved changes are lost; the answer may save, which is asynchronous, so
    // the first close is cancelled and the window closes itself once the answer is in.
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_vm.HasUnsavedChanges) return;
        e.Cancel = true;
        if (!await _vm.ConfirmDiscardAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        string ext = Path.GetExtension(files[0]).ToLowerInvariant();
        if (ext != ".pdf" && ext != ".docx")
        {
            _vm.Status = "Only PDF and Word files can be dropped";
            return;
        }
        await _vm.OpenFromPathAsync(files[0]);
    }
}
