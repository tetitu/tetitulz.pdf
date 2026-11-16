// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Plugins.CertStoreSign;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.WinForms.PdfViewer;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// The one window: the document, the stamps the user places on it, and the two actions — save, or
    /// sign and save. A dashed box shows where the fixed signature will go.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const int HandleSize = 10;
        private const int MinStampPx = 16;

        private readonly AppSettings _settings = AppSettings.Load();
        private readonly PdfViewerControl _viewer = new PdfViewerControl { Dock = DockStyle.Fill };
        private readonly Label _hint = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(12, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.HintBack };
        private readonly ToolStripLabel _status = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right, ForeColor = Color.DimGray, Padding = new Padding(0, 0, 12, 0) };
        private readonly ToolStripButton _open, _addStamp, _remove, _save, _sign, _settingsButton;
        private readonly ContextMenuStrip _stampMenu = new ContextMenuStrip();

        private readonly List<StampItem> _stamps = new List<StampItem>();
        private StampItem _selected;
        private string _path;
        private byte[] _bytes;
        private Bitmap _signaturePreview;
        private bool _alreadySigned;

        // The gesture in progress: a move, or a resize from one corner (0 TL, 1 TR, 2 BR, 3 BL).
        private enum Drag { None, Move, Resize }
        private Drag _drag;
        private int _corner;
        private PointF _dragStartPdf;
        private RectangleF _dragStartRect;
        private RectangleF _dragStartClient;

        /// <summary>A file to open once the window is shown (the command line).</summary>
        public string OpenOnStart { get; set; }

        public MainForm()
        {
            Text = Ui.AppTitle;
            Font = Ui.BaseFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            AllowDrop = true;
            MinimumSize = new Size(800, 560);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(8, 4, 8, 4), ImageScalingSize = new Size(24, 24), Font = Ui.BaseFont, RenderMode = ToolStripRenderMode.System };
            _open = Button("📂  Open PDF…", "Open a PDF file (Ctrl+O). You can also drag a PDF onto the window.", (s, e) => ChooseAndOpen());
            _addStamp = Button("🖼  Add stamp", "Put your stamp on the page you are looking at (Ctrl+T). You can also drag a PNG onto the page.", (s, e) => AddStamp(null));
            _remove = Button("✖  Remove stamp", "Remove the selected stamp (Delete).", (s, e) => RemoveSelected());
            _save = Button("💾  Save without signing…", "Save a copy with the stamps, not signed.", (s, e) => SaveStampedOnly());
            _sign = Button("✍  Sign && Save…", "Add your signature with the smart card and save (Ctrl+S).", (s, e) => SignAndSave());
            _sign.Font = Ui.BigFont;
            _sign.ForeColor = Ui.Accent;
            _settingsButton = Button("⚙  Settings…", "Change the stamp, the signature picture, its place, or the certificate.", (s, e) => OpenSettings(firstRun: false));
            tools.Items.AddRange(new ToolStripItem[] { _open, new ToolStripSeparator(), _addStamp, _remove, new ToolStripSeparator(), _save, _sign });
            _settingsButton.Alignment = ToolStripItemAlignment.Right;
            tools.Items.Add(_settingsButton);
            tools.Items.Add(_status);
            _viewer.ShowOpenButton = false;   // documents open through this window, which keeps their state
            // The signatures' verdicts: the one a document arrived with, and the one just made.
            _viewer.Features |= PdfViewerFeatures.Signatures;
            _viewer.AllowDrop = true;
            _viewer.PaintOverlay += Viewer_PaintOverlay;
            _viewer.PageMouseDown += Viewer_PageMouseDown;
            _viewer.PageMouseMove += Viewer_PageMouseMove;
            _viewer.PageMouseUp += Viewer_PageMouseUp;
            _viewer.DragEnter += OnFileDragEnter;
            _viewer.DragOver += OnFileDragEnter;
            _viewer.DragDrop += OnFileDrop;
            _viewer.PageChanged += (s, e) => UpdateUi();
            DragEnter += OnFileDragEnter;
            DragDrop += OnFileDrop;

            _stampMenu.Items.Add("Remove this stamp", null, (s, e) => RemoveSelected());
            _stampMenu.Items.Add("Back to its original proportions", null, (s, e) => RestoreProportions());

            Controls.Add(_viewer);
            Controls.Add(_hint);
            Controls.Add(tools);

            RestoreWindow();
            LoadSignaturePreview();
            UpdateUi();
        }

        private ToolStripButton Button(string text, string tip, EventHandler click)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip, Padding = new Padding(6, 2, 6, 2), AutoToolTip = false };
            b.Click += click;
            return b;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_settings.HasStampImage || !_settings.HasSignatureImage)
                OpenSettings(firstRun: true);
            if (!string.IsNullOrEmpty(OpenOnStart) && File.Exists(OpenOnStart))
                OpenPdf(OpenOnStart);
        }

        // ── Documents ───────────────────────────────────────────────────────

        private void ChooseAndOpen()
        {
            using (var dlg = new OpenFileDialog { Title = "Open a PDF", Filter = "PDF files (*.pdf)|*.pdf", InitialDirectory = Folder() })
                if (dlg.ShowDialog(this) == DialogResult.OK) OpenPdf(dlg.FileName);
        }

        private void OpenPdf(string path)
        {
            if (!ConfirmDiscardStamps()) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                UseWaitCursor = true;
                _viewer.LoadBytes(bytes);
                ClearStamps();
                _bytes = bytes;
                _path = path;
                _settings.LastFolder = Path.GetDirectoryName(path);
                _alreadySigned = IsSigned(bytes);
                _status.Text = $"Opened {Path.GetFileName(path)} — {_viewer.PageCount} page(s)." +
                               (_alreadySigned ? " It is already signed." : "");
                if (_alreadySigned)
                    Ui.ShowInfo(this,
                        "This document is already signed.\n\n" +
                        "Your stamps and signature are added after that signature. It stays valid for what it " +
                        "signed, and a reader checking it will list your additions as changes made after it; " +
                        "if the document was certified against any change, they are reported as a violation.");
            }
            catch (Exception ex)
            {
                Ui.ShowError(this, "This PDF could not be opened.", ex);
            }
            finally
            {
                UseWaitCursor = false;
                UpdateUi();
            }
        }

        /// <summary>Whether the document carries a digital signature already.</summary>
        private static bool IsSigned(byte[] pdf)
        {
            try
            {
                using (var ms = new MemoryStream(pdf, writable: false))
                using (var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms))
                    return Tetitulz.Pdf.Core.Document.PdfSignatureDetection.HasSignature(doc.Trailer, doc.GetObject);
            }
            catch (Exception)
            {
                return false;   // the viewer opened it; a detection that cannot read it says nothing
            }
        }

        private bool ConfirmDiscardStamps()
        {
            if (_stamps.Count == 0) return true;
            return MessageBox.Show(this, "The stamps you placed have not been saved. Discard them?", Ui.AppTitle,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        // ── Stamps ──────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a stamp: the configured image (or <paramref name="dropped"/>), centred on the page shown,
        /// or on the point it was dropped at.
        /// </summary>
        private void AddStamp(string dropped, int page = -1, PointF? at = null)
        {
            if (_bytes == null) { Ui.ShowInfo(this, "Open a PDF first."); return; }
            string image = dropped;
            if (image == null)
            {
                if (!_settings.HasStampImage && !OpenSettings(firstRun: false)) return;
                if (!_settings.HasStampImage) return;
                image = _settings.StampImageFile;
            }
            if (page < 0) page = Math.Max(0, _viewer.CurrentPage);

            try
            {
                RectangleF box = _viewer.GetPageBox(page);
                int rotation = _viewer.GetPageRotation(page);
                SizeF shown = Placement.DisplaySize(box, rotation);
                var item = new StampItem(image, page, RectangleF.Empty);

                float w = (float)Math.Min(_settings.StampWidthMm * Placement.PointsPerMm, shown.Width * 0.8);
                float h = w / item.Aspect;
                if (h > shown.Height * 0.8f) { h = shown.Height * 0.8f; w = h * item.Aspect; }
                // In PDF terms a page turned by 90 or 270 degrees swaps the stamp's width and height.
                bool swap = rotation == 90 || rotation == 270;
                float pw = swap ? h : w, ph = swap ? w : h;
                PointF centre = at ?? new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);
                item.PdfRect = ClampToBox(new RectangleF(centre.X - pw / 2, centre.Y - ph / 2, pw, ph), box);

                _stamps.Add(item);
                _selected = item;
                _status.Text = "Stamp added. Drag it into place; drag a corner to resize.";
                _viewer.InvalidatePages();
                _viewer.Focus();
            }
            catch (Exception ex)
            {
                Ui.ShowError(this, "The stamp picture could not be used.", ex);
            }
            UpdateUi();
        }

        private void RemoveSelected()
        {
            if (_selected == null) return;
            _stamps.Remove(_selected);
            _selected.Dispose();
            _selected = null;
            _viewer.InvalidatePages();
            UpdateUi();
        }

        private void RestoreProportions()
        {
            if (_selected == null) return;
            int rotation = _viewer.GetPageRotation(_selected.PageIndex);
            bool swap = rotation == 90 || rotation == 270;
            RectangleF r = _selected.PdfRect;
            float shownW = swap ? r.Height : r.Width;
            float shownH = shownW / _selected.Aspect;
            float pw = swap ? shownH : shownW, ph = swap ? shownW : shownH;
            _selected.PdfRect = ClampToBox(new RectangleF(r.X + (r.Width - pw) / 2, r.Y + (r.Height - ph) / 2, pw, ph),
                _viewer.GetPageBox(_selected.PageIndex));
            _viewer.InvalidatePages();
        }

        private void ClearStamps()
        {
            foreach (var s in _stamps) s.Dispose();
            _stamps.Clear();
            _selected = null;
        }

        private static RectangleF ClampToBox(RectangleF r, RectangleF box)
        {
            float w = Math.Min(r.Width, box.Width), h = Math.Min(r.Height, box.Height);
            float x = Math.Max(box.Left, Math.Min(r.X, box.Right - w));
            float y = Math.Max(box.Top, Math.Min(r.Y, box.Bottom - h));
            return new RectangleF(x, y, w, h);
        }

        // ── Drawing on the pages ────────────────────────────────────────────

        private void Viewer_PaintOverlay(object sender, PdfViewerOverlayPaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Where the signature will go: its own picture, faded, inside a dashed frame.
            if (_signaturePreview != null && SignaturePage() == e.PageIndex)
            {
                RectangleF sig = e.ToCanvas(SignatureRectOn(e.PageIndex));
                using (var faded = new ImageAttributes())
                {
                    faded.SetColorMatrix(new ColorMatrix { Matrix33 = 0.45f });
                    Rectangle into = Rectangle.Round(sig);
                    g.DrawImage(_signaturePreview, into, 0, 0, _signaturePreview.Width, _signaturePreview.Height, GraphicsUnit.Pixel, faded);
                }
                using (var pen = new Pen(Ui.Accent, 1.5f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, sig.X, sig.Y, sig.Width, sig.Height);
                using (var font = new Font("Segoe UI", 8f))
                using (var brush = new SolidBrush(Ui.Accent))
                    g.DrawString("Signature goes here", font, brush, sig.X, Math.Max(e.PageBounds.Top, sig.Y - font.Height - 1));
            }

            foreach (var stamp in _stamps.Where(s => s.PageIndex == e.PageIndex))
            {
                RectangleF r = e.ToCanvas(stamp.PdfRect);
                g.DrawImage(stamp.Bitmap, r);
                if (stamp != _selected) continue;
                using (var pen = new Pen(Ui.Accent, 1.5f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                using (var fill = new SolidBrush(Color.White))
                using (var edge = new Pen(Ui.Accent, 1.5f))
                    foreach (PointF c in Corners(r))
                    {
                        var h = new RectangleF(c.X - HandleSize / 2f, c.Y - HandleSize / 2f, HandleSize, HandleSize);
                        g.FillRectangle(fill, h);
                        g.DrawRectangle(edge, h.X, h.Y, h.Width, h.Height);
                    }
            }
        }

        private static PointF[] Corners(RectangleF r)
            => new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };

        // ── Pointer: select, move, resize ───────────────────────────────────

        private RectangleF ClientRect(StampItem s)
            => _viewer.TryPageToClient(s.PageIndex, s.PdfRect, out RectangleF r) ? r : RectangleF.Empty;

        private static int CornerAt(RectangleF r, Point p)
        {
            PointF[] cs = Corners(r);
            for (int i = 0; i < 4; i++)
                if (Math.Abs(p.X - cs[i].X) <= HandleSize && Math.Abs(p.Y - cs[i].Y) <= HandleSize) return i;
            return -1;
        }

        /// <summary>The stamp under a point, topmost first; and which corner, if a handle of the selected one.</summary>
        private StampItem HitTest(Point p, out int corner)
        {
            corner = -1;
            if (_selected != null)
            {
                corner = CornerAt(ClientRect(_selected), p);
                if (corner >= 0) return _selected;
            }
            for (int i = _stamps.Count - 1; i >= 0; i--)
                if (ClientRect(_stamps[i]).Contains(p)) return _stamps[i];
            return null;
        }

        private void Viewer_PageMouseDown(object sender, PdfViewerPageMouseEventArgs e)
        {
            StampItem hit = HitTest(e.Location, out int corner);
            if (hit == null)
            {
                if (_selected != null) { _selected = null; _viewer.InvalidatePages(); UpdateUi(); }
                return;   // the viewer's own: select text, follow a link
            }

            e.Handled = true;
            if (_selected != hit)
            {
                _selected = hit;
                // The stamp the user touches comes to the front.
                _stamps.Remove(hit);
                _stamps.Add(hit);
                UpdateUi();
            }
            _viewer.InvalidatePages();

            if (e.Button == MouseButtons.Right)
            {
                _stampMenu.Show(_viewer, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            _dragStartRect = hit.PdfRect;
            _dragStartClient = ClientRect(hit);
            _viewer.TryClientToPage(hit.PageIndex, e.Location, out _dragStartPdf);
            _drag = corner >= 0 ? Drag.Resize : Drag.Move;
            _corner = corner;
            e.Cursor = corner >= 0 ? CornerCursor(corner) : Cursors.SizeAll;
        }

        private void Viewer_PageMouseMove(object sender, PdfViewerPageMouseEventArgs e)
        {
            if (_drag == Drag.None)
            {
                // Hover: show what a press would do.
                StampItem over = HitTest(e.Location, out int corner);
                if (over != null)
                {
                    e.Handled = true;
                    e.Cursor = corner >= 0 ? CornerCursor(corner) : Cursors.SizeAll;
                }
                return;
            }

            e.Handled = true;
            StampItem s = _selected;
            if (s == null) return;
            RectangleF box = _viewer.GetPageBox(s.PageIndex);

            if (_drag == Drag.Move)
            {
                if (!_viewer.TryClientToPage(s.PageIndex, e.Location, out PointF now)) return;
                var moved = new RectangleF(_dragStartRect.X + now.X - _dragStartPdf.X, _dragStartRect.Y + now.Y - _dragStartPdf.Y,
                    _dragStartRect.Width, _dragStartRect.Height);
                s.PdfRect = ClampToBox(moved, box);
                e.Cursor = Cursors.SizeAll;
            }
            else
            {
                // The opposite corner stays put; the pointer is the new corner. The proportions are
                // kept unless Shift is held.
                PointF[] start = Corners(_dragStartClient);
                PointF anchor = start[(_corner + 2) % 4];
                float w = Math.Max(MinStampPx, Math.Abs(e.Location.X - anchor.X));
                float h = Math.Max(MinStampPx, Math.Abs(e.Location.Y - anchor.Y));
                if ((e.Modifiers & Keys.Shift) == 0)
                {
                    float aspect = _dragStartClient.Height <= 0 ? 1 : _dragStartClient.Width / _dragStartClient.Height;
                    if (w / aspect >= h) h = w / aspect; else w = h * aspect;
                }
                float left = e.Location.X < anchor.X ? anchor.X - w : anchor.X;
                float top = e.Location.Y < anchor.Y ? anchor.Y - h : anchor.Y;
                if (_viewer.TryClientToPage(s.PageIndex, Point.Round(new PointF(left, top)), out PointF a) &&
                    _viewer.TryClientToPage(s.PageIndex, Point.Round(new PointF(left + w, top + h)), out PointF b))
                {
                    var r = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    s.PdfRect = ClampToBox(r, box);
                }
                e.Cursor = CornerCursor(_corner);
            }
            _viewer.InvalidatePages();
        }

        private void Viewer_PageMouseUp(object sender, PdfViewerPageMouseEventArgs e)
        {
            if (_drag == Drag.None) return;
            _drag = Drag.None;
            e.Handled = true;
            _viewer.InvalidatePages();
        }

        private static Cursor CornerCursor(int corner) => corner == 0 || corner == 2 ? Cursors.SizeNWSE : Cursors.SizeNESW;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.O) { ChooseAndOpen(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.T) { AddStamp(null); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.S) { SignAndSave(); e.Handled = true; return; }
            if (_selected == null || ActiveControl is TextBoxBase) return;

            switch (e.KeyCode)
            {
                case Keys.Delete:
                case Keys.Back:
                    RemoveSelected(); break;
                case Keys.Escape:
                    _selected = null; _viewer.InvalidatePages(); UpdateUi(); break;
                case Keys.Left: Nudge(-1, 0, e.Shift); break;
                case Keys.Right: Nudge(1, 0, e.Shift); break;
                case Keys.Up: Nudge(0, -1, e.Shift); break;
                case Keys.Down: Nudge(0, 1, e.Shift); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        /// <summary>Moves the selected stamp one point (ten with Shift) in the direction shown on screen.</summary>
        private void Nudge(int du, int dv, bool big)
        {
            float step = big ? 10f : 1f;
            float u = du * step, v = dv * step, dx, dy;
            switch (_viewer.GetPageRotation(_selected.PageIndex))
            {
                case 90: dx = v; dy = u; break;
                case 180: dx = -u; dy = v; break;
                case 270: dx = -v; dy = -u; break;
                default: dx = u; dy = -v; break;
            }
            RectangleF r = _selected.PdfRect;
            r.Offset(dx, dy);
            _selected.PdfRect = ClampToBox(r, _viewer.GetPageBox(_selected.PageIndex));
            _viewer.InvalidatePages();
        }

        // ── Drag and drop ───────────────────────────────────────────────────

        private static string DroppedFile(DragEventArgs e)
        {
            if (!(e.Data?.GetData(DataFormats.FileDrop) is string[] files) || files.Length != 1) return null;
            string ext = Path.GetExtension(files[0]).ToLowerInvariant();
            return ext == ".pdf" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" ? files[0] : null;
        }

        private void OnFileDragEnter(object sender, DragEventArgs e)
        {
            string file = DroppedFile(e);
            bool image = file != null && !file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            e.Effect = file == null || (image && _bytes == null) ? DragDropEffects.None : DragDropEffects.Copy;
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            string file = DroppedFile(e);
            if (file == null) return;
            if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                BeginInvoke(new Action(() => OpenPdf(file)));   // after the drop completes, so a dialog does not hold Explorer
                return;
            }
            Point client = _viewer.PointToClient(new Point(e.X, e.Y));
            if (_viewer.TryClientToPage(client, out int page, out PointF at))
                AddStamp(file, page, at);
            else
                AddStamp(file);
        }

        // ── Saving and signing ──────────────────────────────────────────────

        /// <summary>The document with every stamp drawn into its page, in one appended revision.</summary>
        private byte[] StampedBytes()
        {
            // One sample set per picture file, so a stamp placed on several pages is written once.
            var pictures = new Dictionary<string, StampPixels>(StringComparer.OrdinalIgnoreCase);
            var placements = new List<StampPlacement>();
            foreach (StampItem stamp in _stamps)
            {
                if (!pictures.TryGetValue(stamp.ImagePath, out StampPixels pixels))
                    pictures[stamp.ImagePath] = pixels = Samples(stamp.Bitmap);
                placements.Add(new StampPlacement(stamp.PageIndex, stamp.PdfRect, pixels));
            }
            return StampWriter.Apply(_bytes, placements, _viewer.GetPageRotation);
        }

        /// <summary>A bitmap's straight RGB samples and its alpha, row by row from the top.</summary>
        private static StampPixels Samples(Bitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            var rgb = new byte[w * h * 3];
            var alpha = new byte[w * h];
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < w; x++)
                    {
                        // Format32bppArgb is B, G, R, A in memory, not premultiplied.
                        rgb[(y * w + x) * 3] = row[x * 4 + 2];
                        rgb[(y * w + x) * 3 + 1] = row[x * 4 + 1];
                        rgb[(y * w + x) * 3 + 2] = row[x * 4];
                        alpha[y * w + x] = row[x * 4 + 3];
                    }
                }
            }
            finally { bitmap.UnlockBits(data); }
            return new StampPixels(w, h, rgb, alpha);
        }

        private void SaveStampedOnly()
        {
            if (_bytes == null) { Ui.ShowInfo(this, "Open a PDF first."); return; }
            if (_stamps.Count == 0) { Ui.ShowInfo(this, "There is no stamp to save. Click Add stamp first."); return; }
            string target = AskWhereToSave(" - stamped");
            if (target == null) return;
            try
            {
                UseWaitCursor = true;
                byte[] stamped = StampedBytes();
                File.WriteAllBytes(target, stamped);
                AfterSave(target, stamped, $"Saved with {_stamps.Count} stamp(s) to {target}.");
            }
            catch (Exception ex) { Ui.ShowError(this, "The file could not be saved.", ex); }
            finally { UseWaitCursor = false; }
        }

        private void SignAndSave()
        {
            if (_bytes == null) { Ui.ShowInfo(this, "Open a PDF first."); return; }
            if (!_settings.HasSignatureImage && !OpenSettings(firstRun: false)) return;
            if (!_settings.HasSignatureImage) return;

            X509Certificate2 certificate = string.IsNullOrEmpty(_settings.CertificateThumbprint) ? null
                : CertStoreLookup.FindByThumbprint(_settings.CertificateThumbprint, StoreLocation.CurrentUser);
            if (certificate == null)
            {
                Ui.ShowInfo(this, "Choose the signing certificate first. If it is on a smart card, insert the card, then pick it in Settings.");
                if (!OpenSettings(firstRun: false)) return;
                certificate = string.IsNullOrEmpty(_settings.CertificateThumbprint) ? null
                    : CertStoreLookup.FindByThumbprint(_settings.CertificateThumbprint, StoreLocation.CurrentUser);
                if (certificate == null) return;
            }
            if (!certificate.HasPrivateKey)
            {
                Ui.ShowInfo(this, "The certificate's key cannot be reached. Insert the smart card it belongs to and try again.");
                return;
            }

            string target = AskWhereToSave(" - signed");
            if (target == null) return;

            int page = SignaturePage();
            RectangleF rect = SignatureRectOn(page);
            try
            {
                UseWaitCursor = true;
                _status.Text = "Signing… your smart card may ask for its PIN.";
                Refresh();

                byte[] stamped = _stamps.Count > 0 ? StampedBytes() : _bytes;
                byte[] signature = File.ReadAllBytes(_settings.SignatureImageFile);
                byte[] signed;
                string warning;
                using (var signer = new CertStoreExternalSigner(certificate) { Silent = false })
                using (var input = new MemoryStream(stamped, writable: false))
                using (var output = new MemoryStream())
                {
                    var parameters = new SignatureParameters
                    {
                        ExternalSigner = signer,
                        Reason = string.IsNullOrEmpty(_settings.Reason) ? null : _settings.Reason,
                        Location = string.IsNullOrEmpty(_settings.Location) ? null : _settings.Location,
                        // A timestamp proves when the document was signed, after the certificate expires.
                        TimestampAuthorityUrl = string.IsNullOrWhiteSpace(_settings.TimestampUrl) ? null : _settings.TimestampUrl.Trim(),
                    };
                    VisualSignatureBuilder.Sign(input, output, signature, parameters, page + 1, rect.X, rect.Y, rect.Width, rect.Height);
                    // The slot is the signing thread's own, and the next signing call clears it.
                    warning = PdfSigner.LastSignerWarning;
                    signed = output.ToArray();
                }
                File.WriteAllBytes(target, signed);
                AfterSave(target, signed, warning == null
                    ? "Signed and saved to " + target + "."
                    : "Signed and saved to " + target + "." + Environment.NewLine + Environment.NewLine
                      + "A reader checking this signature will refuse it: " + warning);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // A cancelled PIN prompt, a wrong PIN, or a card removed mid-way.
                Ui.ShowError(this, "Signing was cancelled, or the smart card refused. Nothing was saved.", ex);
                _status.Text = "Not signed.";
            }
            catch (Exception ex)
            {
                Ui.ShowError(this, "The document could not be signed. Nothing was saved.", ex);
                _status.Text = "Not signed.";
            }
            finally { UseWaitCursor = false; }
        }

        private void AfterSave(string target, byte[] saved, string message)
        {
            ClearStamps();
            _viewer.LoadBytes(saved);
            _bytes = saved;
            _path = target;
            _settings.LastFolder = Path.GetDirectoryName(target);
            _status.Text = message;
            UpdateUi();
            if (MessageBox.Show(this, message + Environment.NewLine + Environment.NewLine + "Show the file in its folder?", Ui.AppTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start("explorer.exe", "/select,\"" + target + "\"");
        }

        private string AskWhereToSave(string suffix)
        {
            string name = _path == null ? "document" : Path.GetFileNameWithoutExtension(_path);
            using (var dlg = new SaveFileDialog
            {
                Title = "Save as", Filter = "PDF files (*.pdf)|*.pdf", FileName = name + suffix + ".pdf",
                InitialDirectory = Folder(), OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                if (_path != null && string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                {
                    Ui.ShowInfo(this, "Please choose a new name: the original file stays as it is.");
                    return null;
                }
                return dlg.FileName;
            }
        }

        // ── The signature's place ───────────────────────────────────────────

        private int SignaturePage()
        {
            if (_viewer.PageCount == 0) return -1;
            switch (_settings.Page)
            {
                case SignaturePageChoice.FirstPage: return 0;
                case SignaturePageChoice.PageShown: return Math.Max(0, _viewer.CurrentPage);
                default: return _viewer.PageCount - 1;
            }
        }

        private RectangleF SignatureRectOn(int page)
            => Placement.SignatureRect(_settings, _viewer.GetPageBox(page), _viewer.GetPageRotation(page));

        // ── Settings, window, state ─────────────────────────────────────────

        private bool OpenSettings(bool firstRun)
        {
            using (var dlg = new SettingsForm(_settings, firstRun))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            }
            LoadSignaturePreview();
            _viewer.InvalidatePages();
            UpdateUi();
            return true;
        }

        private void LoadSignaturePreview()
        {
            _signaturePreview?.Dispose();
            _signaturePreview = null;
            try { if (_settings.HasSignatureImage) _signaturePreview = Ui.LoadBitmap(_settings.SignatureImageFile); }
            catch (Exception) { _signaturePreview = null; }
        }

        private void UpdateUi()
        {
            bool doc = _bytes != null;
            _addStamp.Enabled = doc;
            _remove.Enabled = _selected != null;
            _save.Enabled = doc && _stamps.Count > 0;
            _sign.Enabled = doc;
            Text = doc ? $"{Path.GetFileName(_path)} — {Ui.AppTitle}" : Ui.AppTitle;

            if (!doc)
                _hint.Text = "Step 1 — Open a PDF: click “Open PDF…”, or drag a PDF file onto this window.";
            else if (_stamps.Count == 0)
                _hint.Text = "Step 2 — Click “Add stamp” (or drag a PNG onto the page) and drag it into place.   Step 3 — “Sign & Save”.";
            else
                _hint.Text = "Drag a stamp to move it  •  drag a corner to resize (hold Shift to stretch)  •  arrow keys nudge  •  Delete removes.   Ready? Click “Sign & Save”.";
        }

        private string Folder()
            => !string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder)
                ? _settings.LastFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        private void RestoreWindow()
        {
            StartPosition = FormStartPosition.Manual;
            var bounds = new Rectangle(_settings.WindowX, _settings.WindowY, Math.Max(800, _settings.WindowWidth), Math.Max(560, _settings.WindowHeight));
            if (_settings.WindowX < 0 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
            {
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                bounds = new Rectangle(work.X + (work.Width - bounds.Width) / 2, work.Y + (work.Height - bounds.Height) / 2,
                    Math.Min(bounds.Width, work.Width), Math.Min(bounds.Height, work.Height));
            }
            Bounds = bounds;
            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscardStamps()) { e.Cancel = true; return; }
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _settings.WindowX = b.X; _settings.WindowY = b.Y; _settings.WindowWidth = b.Width; _settings.WindowHeight = b.Height;
            _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
            try { _settings.Save(); } catch (Exception) { /* the window's place is not worth an error at exit */ }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearStamps();
                _signaturePreview?.Dispose();
                _stampMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
