// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.WinForms.PdfViewer;
using Tetitulz.Pdf.WpfViewer.Services;
using Cursors = System.Windows.Forms.Cursors;
using MouseButtons = System.Windows.Forms.MouseButtons;

namespace Tetitulz.Pdf.WpfViewer.Views;

/// <summary>What a tool on the page asks the user for.</summary>
public enum PageToolShape
{
    /// <summary>A rectangle dragged out on one page.</summary>
    Box,
    /// <summary>A point clicked on a page.</summary>
    Point,
}

/// <summary>What the view model asks of the page display.</summary>
public interface IDocumentView
{
    /// <summary>
    /// Shows <paramref name="pdf"/> at <paramref name="page"/>, forms fillable when
    /// <paramref name="allowFormFilling"/>, and its signatures verified and reported when
    /// <paramref name="showSignatures"/> — off for bytes that are not the signed file (a decrypted
    /// copy), whose signatures would be reported broken.
    /// </summary>
    void Show(byte[] pdf, int page, bool allowFormFilling, bool showSignatures);

    void Clear();
    void GoToPage(int page);
    void ZoomIn();
    void ZoomOut();
    void FitWidth();
    void FitPage();
    void ActualSize();

    /// <summary>The zoom factor shown (1 = 100%).</summary>
    double Zoom { get; }

    /// <summary>Switches between one page at a time and every page in one scroll; true when now continuous.</summary>
    bool ToggleContinuous();

    void Print();

    /// <summary>Opens the display's own find bar, with <paramref name="term"/> when given.</summary>
    void Find(string? term);

    /// <summary>
    /// The shown document with the values the user filled in appended as one revision, or null when
    /// nothing was filled since it was shown.
    /// </summary>
    byte[]? TakeFormEdits();

    /// <summary>True when values have been filled in on the page since it was shown.</summary>
    bool HasFormEdits { get; }

    /// <summary>The text the user has selected, page by page; empty when nothing is selected.</summary>
    IReadOnlyList<SelectedTextPart> SelectedText { get; }

    /// <summary>
    /// Starts a tool on the page: the next drag (or click) is reported through
    /// <see cref="ToolCompleted"/>, Esc, a right click or <see cref="CancelTool"/> through
    /// <see cref="ToolCancelled"/>. <paramref name="hint"/> is shown while it waits.
    /// </summary>
    void BeginTool(PageToolShape shape, string hint);

    /// <summary>Stops the tool in progress, if any.</summary>
    void CancelTool();

    /// <summary>The regions marked for redaction, drawn outlined in red until applied or cleared.</summary>
    IReadOnlyList<PageRect> RedactionMarks { get; set; }

    /// <summary>The trust anchors and rules the signatures banner verifies under; null is the library's defaults.</summary>
    SignatureVerificationOptions? SignatureVerification { get; set; }

    /// <summary>Whether a click on an empty signature field asks for a signature (<see cref="SignatureRequested"/>).</summary>
    bool SigningEnabled { get; set; }

    /// <summary>The page the user moved to inside the display.</summary>
    event Action<int>? PageChanged;

    /// <summary>A file dropped onto the display.</summary>
    event Action<string>? FileDropped;

    /// <summary>The rectangle (a zero-size one for a point tool) the tool asked for, in page coordinates.</summary>
    event Action<PageRect>? ToolCompleted;

    /// <summary>The tool ended without a result.</summary>
    event Action? ToolCancelled;

    /// <summary>A click on an empty signature field; set <see cref="PdfViewerSignatureRequestEventArgs.Parameters"/> to sign it.</summary>
    event EventHandler<PdfViewerSignatureRequestEventArgs>? SignatureRequested;

    /// <summary>A field signed from the display, or the reason it was not.</summary>
    event EventHandler<PdfViewerFieldSignedEventArgs>? FieldSigned;
}

/// <summary>The display: the library's own viewer control, its toolbar left to the ribbon.</summary>
public sealed class WinFormsDocumentView : IDocumentView
{
    private readonly PdfViewerControl _viewer;
    private bool _showing;

    // The tool in progress.
    private PageToolShape? _tool;
    private string _toolHint = "";
    private int _dragPage = -1;
    private PointF _dragStart, _dragNow;
    private IReadOnlyList<PageRect> _marks = Array.Empty<PageRect>();

    public WinFormsDocumentView(PdfViewerControl viewer)
    {
        _viewer = viewer;
        _viewer.ShowToolbar = false;                     // navigation, zoom and print are the ribbon's
        _viewer.ZoomMode = PdfZoomMode.FitWidth;
        _viewer.PageChanged += (_, e) => { if (!_showing) PageChanged?.Invoke(e.PageIndex); };
        _viewer.SignatureRequested += (s, e) => SignatureRequested?.Invoke(s, e);
        _viewer.FieldSigned += (s, e) => FieldSigned?.Invoke(s, e);

        _viewer.PaintOverlay += OnPaintOverlay;
        _viewer.PageMouseDown += OnPageMouseDown;
        _viewer.PageMouseMove += OnPageMouseMove;
        _viewer.PageMouseUp += OnPageMouseUp;
        _viewer.KeyDown += (_, e) =>
        {
            if (e.KeyCode == System.Windows.Forms.Keys.Escape && _tool != null) { CancelTool(); e.Handled = true; }
        };

        _viewer.AllowDrop = true;
        _viewer.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop) == true
                ? System.Windows.Forms.DragDropEffects.Copy : System.Windows.Forms.DragDropEffects.None;
        _viewer.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] { Length: > 0 } files)
                FileDropped?.Invoke(files[0]);
        };
    }

    public event Action<int>? PageChanged;
    public event Action<string>? FileDropped;
    public event Action<PageRect>? ToolCompleted;
    public event Action? ToolCancelled;
    public event EventHandler<PdfViewerSignatureRequestEventArgs>? SignatureRequested;
    public event EventHandler<PdfViewerFieldSignedEventArgs>? FieldSigned;

    public void Show(byte[] pdf, int page, bool allowFormFilling, bool showSignatures)
    {
        const PdfViewerFeatures forms = PdfViewerFeatures.FormFilling | PdfViewerFeatures.FormScripts;
        PdfViewerFeatures features = allowFormFilling ? _viewer.Features | forms : _viewer.Features & ~forms;
        _viewer.Features = showSignatures ? features | PdfViewerFeatures.Signatures : features & ~PdfViewerFeatures.Signatures;
        // A reload moves the viewer to page 1 on its way to the page asked for; neither is the user's move.
        _showing = true;
        try
        {
            _viewer.LoadBytes(pdf);
            if (page > 0 && page < _viewer.PageCount) _viewer.GoToPage(page);
        }
        finally { _showing = false; }
    }

    public void Clear() => _viewer.CloseDocument();

    public void GoToPage(int page)
    {
        if (page >= 0 && page < _viewer.PageCount && page != _viewer.CurrentPage) _viewer.GoToPage(page);
    }

    public void ZoomIn() => _viewer.ZoomIn();
    public void ZoomOut() => _viewer.ZoomOut();
    public void FitWidth() => _viewer.FitWidth();
    public void FitPage() => _viewer.FitPage();
    public void ActualSize() => _viewer.ActualSize();
    public double Zoom => _viewer.Zoom;

    public bool ToggleContinuous()
    {
        _viewer.LayoutMode = _viewer.LayoutMode == PdfViewLayoutMode.Continuous
            ? PdfViewLayoutMode.SinglePage : PdfViewLayoutMode.Continuous;
        return _viewer.LayoutMode == PdfViewLayoutMode.Continuous;
    }

    public void Print() => _viewer.Print();

    public void Find(string? term) => _viewer.Find(term);

    public byte[]? TakeFormEdits()
    {
        if (!_viewer.IsModified) return null;
        using var ms = new MemoryStream();
        _viewer.SaveAs(ms);
        return ms.ToArray();
    }

    public bool HasFormEdits => _viewer.IsModified;

    public IReadOnlyList<SelectedTextPart> SelectedText
        => _viewer.Selection
            .Select(s => new SelectedTextPart(s.PageIndex, s.Text,
                s.GlyphBoxes.Select(b => new PageRect(s.PageIndex, b.X, b.Y, b.Width, b.Height)).ToList()))
            .ToList();

    public SignatureVerificationOptions? SignatureVerification
    {
        get => _viewer.SignatureVerification;
        set => _viewer.SignatureVerification = value;
    }

    public bool SigningEnabled
    {
        get => (_viewer.Features & PdfViewerFeatures.Signing) != 0;
        set => _viewer.Features = value ? _viewer.Features | PdfViewerFeatures.Signing : _viewer.Features & ~PdfViewerFeatures.Signing;
    }

    public IReadOnlyList<PageRect> RedactionMarks
    {
        get => _marks;
        set { _marks = value ?? Array.Empty<PageRect>(); _viewer.InvalidatePages(); }
    }

    // ── tools ────────────────────────────────────────────────────────────

    public void BeginTool(PageToolShape shape, string hint)
    {
        CancelToolQuietly();
        _tool = shape;
        _toolHint = hint;
        _viewer.InvalidatePages();
        _viewer.Focus();
    }

    public void CancelTool()
    {
        if (_tool == null) return;
        CancelToolQuietly();
        ToolCancelled?.Invoke();
    }

    private void CancelToolQuietly()
    {
        _tool = null;
        _dragPage = -1;
        _viewer.InvalidatePages();
    }

    private void OnPageMouseDown(object? sender, PdfViewerPageMouseEventArgs e)
    {
        if (_tool == null || e.PageIndex < 0) return;
        e.Handled = true;
        if (e.Button == MouseButtons.Right) { CancelTool(); return; }
        if (e.Button != MouseButtons.Left) return;

        if (_tool == PageToolShape.Point)
        {
            var point = new PageRect(e.PageIndex, e.PagePoint.X, e.PagePoint.Y, 0, 0);
            CancelToolQuietly();
            ToolCompleted?.Invoke(point);
            return;
        }
        _dragPage = e.PageIndex;
        _dragStart = _dragNow = e.PagePoint;
        e.Cursor = Cursors.Cross;
    }

    private void OnPageMouseMove(object? sender, PdfViewerPageMouseEventArgs e)
    {
        if (_tool == null) return;
        e.Handled = true;
        e.Cursor = Cursors.Cross;
        if (_dragPage < 0) return;
        // The drag stays on the page it started on, the pointer read in that page's coordinates.
        if (_viewer.TryClientToPage(_dragPage, e.Location, out PointF p)) _dragNow = p;
        _viewer.InvalidatePages();
    }

    private void OnPageMouseUp(object? sender, PdfViewerPageMouseEventArgs e)
    {
        if (_tool == null || _dragPage < 0) return;
        e.Handled = true;
        if (_viewer.TryClientToPage(_dragPage, e.Location, out PointF p)) _dragNow = p;
        PageRect box = Normalised(_dragPage, _dragStart, _dragNow);
        CancelToolQuietly();
        // A click with no drag is not a box.
        if (box.Width < 2 || box.Height < 2) { ToolCancelled?.Invoke(); return; }
        ToolCompleted?.Invoke(box);
    }

    private static PageRect Normalised(int page, PointF a, PointF b)
        => new(page, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void OnPaintOverlay(object? sender, PdfViewerOverlayPaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Redaction marks: what Apply will remove, outlined in red over a light red wash.
        using (var wash = new SolidBrush(Color.FromArgb(60, 220, 0, 0)))
        using (var edge = new Pen(Color.FromArgb(220, 0, 0), 1.5f))
            foreach (PageRect m in _marks.Where(m => m.Page == e.PageIndex))
            {
                RectangleF r = e.ToCanvas(ToRectangle(m));
                g.FillRectangle(wash, r);
                g.DrawRectangle(edge, r.X, r.Y, r.Width, r.Height);
            }

        if (_tool == null) return;
        if (_dragPage == e.PageIndex)
        {
            RectangleF r = e.ToCanvas(ToRectangle(Normalised(_dragPage, _dragStart, _dragNow)));
            using var pen = new Pen(Color.FromArgb(0, 120, 212), 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
        // The hint on every page in view, at its top-left.
        using var font = new Font("Segoe UI", 9f);
        SizeF size = g.MeasureString(_toolHint, font);
        var hintBox = new RectangleF(e.PageBounds.Left + 6, e.PageBounds.Top + 6, size.Width + 12, size.Height + 6);
        using (var back = new SolidBrush(Color.FromArgb(230, 255, 248, 200)))
            g.FillRectangle(back, hintBox);
        g.DrawString(_toolHint, font, Brushes.Black, hintBox.X + 6, hintBox.Y + 3);
    }

    // The control's page rectangles are System.Drawing ones in page coordinates (Top the smaller y).
    private static RectangleF ToRectangle(PageRect r) => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
}
