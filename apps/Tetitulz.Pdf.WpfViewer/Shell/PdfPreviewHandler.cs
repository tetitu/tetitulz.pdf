// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tetitulz.Pdf.WinForms.PdfViewer;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Tetitulz.Pdf.WpfViewer.Shell;

/// <summary>
/// File Explorer's preview of a PDF: the library's viewer control, read-only, in the preview pane —
/// pages fitted to the pane's width, the floating page and zoom controls, the pane's light or dark
/// colours.
/// </summary>
/// <remarks>
/// <para>Explorer hands the file over as a stream (<see cref="IInitializeWithStream"/>), never a path,
/// and the bytes are read once, up to <see cref="MaxPreviewBytes"/>. The pane's window belongs to
/// another process: the viewer is created as its child, which Windows allows across processes.</para>
/// <para>The viewer is read-only here: no Open, no printing, no form filling, no links followed — a
/// click in a preview must not start anything.</para>
/// </remarks>
[ComVisible(true)]
[Guid(ShellIdentity.PreviewHandlerClsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PdfPreviewHandler : IPreviewHandler, IPreviewHandlerVisuals, IInitializeWithStream, IObjectWithSite, IOleWindow
{
    /// <summary>The largest file previewed; a larger one is described, not read.</summary>
    internal const long MaxPreviewBytes = 256L * 1024 * 1024;

    private readonly Action<PdfPreviewHandler, bool>? _activity;
    private byte[]? _bytes;
    private string? _refusal;
    private IntPtr _parent;
    private Rectangle _bounds;
    private object? _site;
    private Color? _background;
    private PreviewHost? _host;

    public PdfPreviewHandler() { }

    /// <summary>A handler that tells its server when it shows and stops showing a preview.</summary>
    internal PdfPreviewHandler(Action<PdfPreviewHandler, bool> activity) => _activity = activity;

    /// <summary>The viewer showing the preview, or null before one is shown — what a test reads.</summary>
    internal PdfViewerControl? Viewer => _host?.Viewer;

    /// <summary>The message shown in place of the pages, or null — what a test reads.</summary>
    internal string? Message => _host?.MessageText;

    /// <summary>Whether a preview is on screen.</summary>
    internal bool IsShowing => _host != null;

    // ── IInitializeWithStream ─────────────────────────────────────────────────────────────

    public void Initialize(IStream stream, uint mode)
    {
        if (_bytes != null || _refusal != null) throw new COMException("Already initialised.", unchecked((int)0x8007139F)); // ERROR_INVALID_STATE
        stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
        if (stat.cbSize > MaxPreviewBytes)
        {
            _refusal = $"This PDF is too large to preview ({stat.cbSize / (1024 * 1024)} MB).";
            return;
        }
        _bytes = ReadAll(stream, stat.cbSize);
    }

    /// <summary>The stream's bytes, read in blocks, never more than <see cref="MaxPreviewBytes"/>.</summary>
    internal static byte[] ReadAll(IStream stream, long sizeHint)
    {
        using var buffer = new MemoryStream(sizeHint > 0 && sizeHint <= MaxPreviewBytes ? (int)sizeHint : 0);
        byte[] block = new byte[81920];
        IntPtr read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(block, block.Length, read);
                int n = Marshal.ReadInt32(read);
                if (n <= 0) break;
                if (buffer.Length + n > MaxPreviewBytes) throw new InvalidDataException("The PDF is too large to preview.");
                buffer.Write(block, 0, n);
            }
        }
        finally { Marshal.FreeHGlobal(read); }
        return buffer.ToArray();
    }

    // ── IPreviewHandler ───────────────────────────────────────────────────────────────────

    public void SetWindow(IntPtr hwnd, ref NativeRect rect)
    {
        _parent = hwnd;
        _bounds = rect.ToRectangle();
        if (_host != null && _host.IsHandleCreated)
        {
            PreviewNative.SetParent(_host.Handle, hwnd);
            _host.Bounds = _bounds;
        }
    }

    public void SetRect(ref NativeRect rect)
    {
        _bounds = rect.ToRectangle();
        if (_host != null) _host.Bounds = _bounds;
    }

    public void DoPreview()
    {
        if (_host != null) return;
        _host = new PreviewHost(_parent, _bounds);
        _host.CreateControl();
        if (_refusal != null) _host.ShowMessage(_refusal);
        else if (_bytes == null) _host.ShowMessage("Nothing to preview.");
        else _host.ShowDocument(_bytes, _background);
        _activity?.Invoke(this, true);
    }

    public void Unload()
    {
        PreviewHost? host = _host;
        _host = null;
        host?.Dispose();
        _bytes = null;
        _refusal = null;
        _activity?.Invoke(this, false);
    }

    public void SetFocus() => _host?.Viewer?.Focus();

    public void QueryFocus(out IntPtr hwnd) => hwnd = PreviewNative.GetFocus();

    public int TranslateAccelerator(ref NativeMsg msg)
        // The frame (Explorer) gets the keys the preview does not use, its Tab and its shortcuts.
        => _site is IPreviewHandlerFrame frame ? frame.TranslateAccelerator(ref msg) : PreviewNative.SFalse;

    // ── IPreviewHandlerVisuals ────────────────────────────────────────────────────────────

    public void SetBackgroundColor(uint colorref)
    {
        _background = Color.FromArgb((int)(colorref & 0xFF), (int)((colorref >> 8) & 0xFF), (int)((colorref >> 16) & 0xFF));
        _host?.ApplyBackground(_background.Value);
    }

    public void SetFont(IntPtr logfont) { }

    public void SetTextColor(uint colorref) { }

    // ── IObjectWithSite, IOleWindow ───────────────────────────────────────────────────────

    public void SetSite(object? site) => _site = site;

    public void GetSite(ref Guid riid, out object? site)
    {
        site = null;
        if (_site == null) throw new COMException("No site.", unchecked((int)0x80004005)); // E_FAIL
        IntPtr unknown = Marshal.GetIUnknownForObject(_site);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in riid, out IntPtr asked));
            try { site = Marshal.GetObjectForIUnknown(asked); }
            finally { Marshal.Release(asked); }
        }
        finally { Marshal.Release(unknown); }
    }

    public void GetWindow(out IntPtr hwnd) => hwnd = _host?.Handle ?? _parent;

    public void ContextSensitiveHelp(bool enterMode) => throw new NotImplementedException();

    /// <summary>The theme a pane of this background reads best on: dark below the middle of the grey scale.</summary>
    internal static PdfViewerTheme ThemeFor(Color background)
        => (background.R * 299 + background.G * 587 + background.B * 114) / 1000 < 128 ? PdfViewerTheme.Dark : PdfViewerTheme.Light;

    /// <summary>
    /// The window in the preview pane: a child of the pane's window, holding the viewer or a message.
    /// </summary>
    private sealed class PreviewHost : UserControl
    {
        private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000;
        private readonly IntPtr _parentWindow;
        private Label? _message;

        public PreviewHost(IntPtr parent, Rectangle bounds)
        {
            _parentWindow = parent;
            Bounds = bounds;
        }

        public PdfViewerControl? Viewer { get; private set; }

        public string? MessageText => _message?.Text;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (_parentWindow != IntPtr.Zero)
                {
                    cp.Parent = _parentWindow;                    // the pane's window, in Explorer's process
                    cp.Style |= WsChild | WsVisible | WsClipChildren | WsClipSiblings;
                }
                return cp;
            }
        }

        public void ShowDocument(byte[] bytes, Color? background)
        {
            var viewer = new PdfViewerControl
            {
                Dock = DockStyle.Fill,
                ShowToolbar = false,
                ShowSidebar = false,
                ShowStatusBar = false,
                ShowContextMenu = false,
                ShowFloatingControls = true,
                Theme = background.HasValue ? ThemeFor(background.Value) : PdfViewerTheme.System,
                // Read-only: nothing opened, printed, filled, followed or signed from a preview.
                Features = PdfViewerFeatures.Default & ~(PdfViewerFeatures.OpenFile | PdfViewerFeatures.Print | PdfViewerFeatures.Links),
                ZoomMode = PdfZoomMode.FitWidth,
            };
            Controls.Add(viewer);
            Viewer = viewer;
            try
            {
                viewer.LoadBytes(bytes);
                viewer.ZoomMode = PdfZoomMode.FitWidth;
            }
            catch (PdfPasswordRequiredException)
            {
                ShowMessage("This PDF is protected by a password. Open it to read it.");
            }
            catch (Exception ex)
            {
                ShowMessage("This PDF cannot be previewed: " + ex.Message);
            }
        }

        public void ShowMessage(string text)
        {
            if (Viewer != null) { Controls.Remove(Viewer); Viewer.Dispose(); Viewer = null; }
            _message ??= new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Padding = new Padding(16) };
            _message.Text = text;
            if (!Controls.Contains(_message)) Controls.Add(_message);
        }

        public void ApplyBackground(Color background)
        {
            if (Viewer != null) Viewer.Theme = ThemeFor(background);
            BackColor = background;
            if (_message != null) _message.ForeColor = ThemeFor(background) == PdfViewerTheme.Dark ? Color.White : Color.Black;
        }
    }
}
