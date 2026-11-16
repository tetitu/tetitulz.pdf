// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;
using Tetitulz.Pdf.Plugins.CertStoreSign;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// The once-only choices, on three tabs: the stamp picture and the size new stamps start at; the
    /// signature picture, its size and its place, shown on an A4 page where it can be dragged; and the
    /// signing certificate. Everything is remembered for the next start.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const decimal MinMm = 5, MaxStampMm = 200, MaxSignatureMm = 200;

        private readonly AppSettings _settings;
        private string _stampPath;
        private string _signaturePath;
        private Bitmap _stampImage;
        private Bitmap _signatureImage;
        private bool _syncing;   // set while code moves the controls, so their events do not echo back

        // Stamp tab
        private readonly ImagePreview _stampPreview = new ImagePreview();
        private readonly NumericUpDown _stampWidth = Mm(MinMm, MaxStampMm);
        private readonly TrackBar _stampSlider = Slider(MaxStampMm);
        private readonly Label _stampSize = SizeLabel();
        private readonly A4Preview _stampPage = new A4Preview { Caption = "Stamp" };

        // Signature tab
        private readonly ImagePreview _signaturePreview = new ImagePreview();
        private readonly NumericUpDown _sigWidth = Mm(MinMm, MaxSignatureMm);
        private readonly TrackBar _sigSlider = Slider(MaxSignatureMm);
        private readonly NumericUpDown _sigHeight = Mm(MinMm, MaxSignatureMm);
        private readonly CheckBox _keepAspect = new CheckBox { Text = "Keep the picture's proportions", AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        private readonly NumericUpDown _offsetX = Mm(0, 300);
        private readonly NumericUpDown _offsetY = Mm(0, 300);
        private readonly ComboBox _anchor = DropDown();
        private readonly ComboBox _page = DropDown();
        private readonly A4Preview _signaturePage = new A4Preview { Caption = "Signature", Draggable = true };

        // Certificate tab
        private readonly ComboBox _certificate = DropDown();
        private readonly Label _certificateNote = new Label { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 12) };
        private readonly TextBox _reason = new TextBox { Width = 420 };
        private readonly TextBox _location = new TextBox { Width = 420 };
        private readonly TextBox _timestampUrl = new TextBox { Width = 420 };

        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        public SettingsForm(AppSettings settings, bool firstRun)
        {
            _settings = settings;
            _stampPath = settings.HasStampImage ? settings.StampImageFile : null;
            _signaturePath = settings.HasSignatureImage ? settings.SignatureImageFile : null;

            Text = firstRun ? "Welcome — set up your stamp and signature" : "Settings";
            Font = Ui.BaseFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 680);
            Padding = new Padding(10);

            _tabs.TabPages.Add(StampTab());
            _tabs.TabPages.Add(SignatureTab());
            _tabs.TabPages.Add(CertificateTab());

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = firstRun ? 48 : 0, Padding = new Padding(2, 0, 2, 8),
                Text = "Set up three things once — they are kept and loaded every time the app starts: your stamp, " +
                       "your signature picture and where it goes, and your signing certificate. Transparent PNG files work best.",
            };
            Controls.Add(_tabs);
            Controls.Add(intro);
            Controls.Add(Buttons());

            // Values in, then the pictures, then the previews follow both.
            _syncing = true;
            SetValue(_stampWidth, settings.StampWidthMm);
            SetValue(_sigWidth, settings.SignatureWidthMm);
            SetValue(_sigHeight, settings.SignatureHeightMm);
            SetValue(_offsetX, settings.OffsetXMm);
            SetValue(_offsetY, settings.OffsetYMm);
            _keepAspect.Checked = settings.SignatureKeepAspect;
            _anchor.Items.AddRange(new object[] { "Bottom right corner", "Bottom left corner", "Top right corner", "Top left corner" });
            _anchor.SelectedIndex = (int)settings.Anchor;
            _page.Items.AddRange(new object[] { "Last page", "First page", "The page I am looking at" });
            _page.SelectedIndex = (int)settings.Page;
            _reason.Text = settings.Reason ?? "";
            _location.Text = settings.Location ?? "";
            _timestampUrl.Text = settings.TimestampUrl ?? "";
            _syncing = false;

            SetStampImage(_stampPath);
            SetSignatureImage(_signaturePath);
            LoadCertificates(settings.CertificateThumbprint);
        }

        // ── Tab 1: the stamp ────────────────────────────────────────────────

        private TabPage StampTab()
        {
            var tab = new TabPage("1. Stamp") { Padding = new Padding(12) };
            var left = Column();
            left.Controls.Add(Note("The picture you place on pages yourself. You can drag it and resize it on every document."));
            left.Controls.Add(Framed(_stampPreview));
            left.Controls.Add(ChooseButton("Choose stamp picture…", path => { _stampPath = path; SetStampImage(path); }));
            left.Controls.Add(Heading("Size of a new stamp"));
            left.Controls.Add(Note("A large picture can start small: this is the width every new stamp starts at. Its height follows the picture."));
            left.Controls.Add(SizeRow("Width:", _stampWidth, _stampSize));
            left.Controls.Add(_stampSlider);

            _stampWidth.ValueChanged += (s, e) => { if (!_syncing) { Sync(_stampSlider, _stampWidth); UpdateStampPreview(); } };
            _stampSlider.ValueChanged += (s, e) => { if (!_syncing) { SetValue(_stampWidth, _stampSlider.Value); } };

            tab.Controls.Add(TwoColumns(left, _stampPage));
            return tab;
        }

        private void SetStampImage(string path)
        {
            _stampImage?.Dispose();
            _stampImage = TryLoad(path);
            _stampPreview.SetImage(_stampImage);
            _stampPage.SetImage(_stampImage);
            UpdateStampPreview();
        }

        private void UpdateStampPreview()
        {
            float w = (float)_stampWidth.Value;
            float h = w * Aspect(_stampImage);
            _stampSize.Text = $"× {h:0.0} mm high";
            _stampPage.ItemMm = new RectangleF((A4Preview.PageWidthMm - w) / 2, (A4Preview.PageHeightMm - h) / 2, w, h);
            Sync(_stampSlider, _stampWidth);
        }

        // ── Tab 2: the signature ────────────────────────────────────────────

        private TabPage SignatureTab()
        {
            var tab = new TabPage("2. Signature") { Padding = new Padding(12) };
            var left = Column();
            left.Controls.Add(Note("Added when you sign — always this size, always in this place. Drag it on the page to move it."));
            left.Controls.Add(Framed(_signaturePreview, 80));
            left.Controls.Add(ChooseButton("Choose signature picture…", path => { _signaturePath = path; SetSignatureImage(path); }));

            left.Controls.Add(Heading("Size"));
            FlowLayoutPanel size = SizeRow("Width:", _sigWidth, null);
            size.Controls.Add(new Label { Text = "Height:", AutoSize = true, Margin = new Padding(12, 6, 8, 0) });
            size.Controls.Add(_sigHeight);
            size.Controls.Add(new Label { Text = "mm", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
            left.Controls.Add(size);
            left.Controls.Add(_sigSlider);
            left.Controls.Add(_keepAspect);

            left.Controls.Add(Heading("Place"));
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
            void Row(string label, Control c)
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) });
                grid.Controls.Add(c);
            }
            Row("Measured from the:", _anchor);
            Row("From the side (mm):", _offsetX);
            Row("From top/bottom (mm):", _offsetY);
            Row("On:", _page);
            left.Controls.Add(grid);

            _sigWidth.ValueChanged += (s, e) => { if (!_syncing) SignatureSizeChanged(fromWidth: true); };
            _sigSlider.ValueChanged += (s, e) => { if (!_syncing) SetValue(_sigWidth, _sigSlider.Value); };
            _sigHeight.ValueChanged += (s, e) => { if (!_syncing) SignatureSizeChanged(fromWidth: false); };
            _keepAspect.CheckedChanged += (s, e) => { if (!_syncing) SignatureSizeChanged(fromWidth: true); };
            _offsetX.ValueChanged += (s, e) => { if (!_syncing) UpdateSignaturePreview(); };
            _offsetY.ValueChanged += (s, e) => { if (!_syncing) UpdateSignaturePreview(); };
            _anchor.SelectedIndexChanged += (s, e) => { if (!_syncing) UpdateSignaturePreview(); };
            _signaturePage.ItemMoved += (s, r) => SignatureDragged(r);

            tab.Controls.Add(TwoColumns(left, _signaturePage));
            return tab;
        }

        private void SetSignatureImage(string path)
        {
            _signatureImage?.Dispose();
            _signatureImage = TryLoad(path);
            _signaturePreview.SetImage(_signatureImage);
            _signaturePage.SetImage(_signatureImage);
            SignatureSizeChanged(fromWidth: true);
        }

        /// <summary>Width or height edited: with proportions kept, the other follows the picture.</summary>
        private void SignatureSizeChanged(bool fromWidth)
        {
            _syncing = true;
            _sigHeight.Enabled = !_keepAspect.Checked;
            if (_keepAspect.Checked && _signatureImage != null)
            {
                if (fromWidth) SetValue(_sigHeight, (double)_sigWidth.Value * Aspect(_signatureImage));
                else SetValue(_sigWidth, (double)_sigHeight.Value / Aspect(_signatureImage));
            }
            Sync(_sigSlider, _sigWidth);
            _syncing = false;
            UpdateSignaturePreview();
        }

        private SignatureAnchor ChosenAnchor => (SignatureAnchor)Math.Max(0, _anchor.SelectedIndex);
        private bool AnchoredLeft => ChosenAnchor == SignatureAnchor.BottomLeft || ChosenAnchor == SignatureAnchor.TopLeft;
        private bool AnchoredTop => ChosenAnchor == SignatureAnchor.TopLeft || ChosenAnchor == SignatureAnchor.TopRight;

        /// <summary>The signature's rectangle on the A4 preview, in mm from its top-left, from the controls.</summary>
        private void UpdateSignaturePreview()
        {
            float w = (float)_sigWidth.Value, h = (float)_sigHeight.Value;
            float dx = (float)_offsetX.Value, dy = (float)_offsetY.Value;
            float x = AnchoredLeft ? dx : A4Preview.PageWidthMm - dx - w;
            float y = AnchoredTop ? dy : A4Preview.PageHeightMm - dy - h;
            _signaturePage.ItemMm = new RectangleF(x, y, w, h);
        }

        /// <summary>The signature dragged on the preview: the offsets from its anchor follow, rounded to a millimetre.</summary>
        private void SignatureDragged(RectangleF r)
        {
            _syncing = true;
            SetValue(_offsetX, Math.Round(AnchoredLeft ? r.X : A4Preview.PageWidthMm - r.Right));
            SetValue(_offsetY, Math.Round(AnchoredTop ? r.Y : A4Preview.PageHeightMm - r.Bottom));
            _syncing = false;
        }

        // ── Tab 3: the certificate ──────────────────────────────────────────

        private TabPage CertificateTab()
        {
            var tab = new TabPage("3. Certificate") { Padding = new Padding(16) };
            var col = Column();
            col.Controls.Add(Note("The certificate on your smart card that signs. Insert the card before you open this tab, or click Refresh."));
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            _certificate.Width = 560;
            var refresh = new Button { Text = "Refresh", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
            refresh.Click += (s, e) => LoadCertificates((_certificate.SelectedItem as CertificateItem)?.Thumbprint);
            row.Controls.Add(_certificate);
            row.Controls.Add(refresh);
            col.Controls.Add(row);
            col.Controls.Add(_certificateNote);
            col.Controls.Add(Heading("Written into the signature (optional)"));
            col.Controls.Add(SizeRow("Reason:", _reason, null));
            col.Controls.Add(SizeRow("Location:", _location, null));
            col.Controls.Add(Heading("Timestamp (optional)"));
            col.Controls.Add(Note("A timestamp authority's address, such as http://timestamp.digicert.com. A timestamp proves when you signed, so the signature still verifies after your certificate expires. Leave empty to sign without one."));
            col.Controls.Add(SizeRow("Timestamp URL:", _timestampUrl, null));
            tab.Controls.Add(col);
            return tab;
        }

        private void LoadCertificates(string selectThumbprint)
        {
            _certificate.Items.Clear();
            try
            {
                X509Certificate2Collection found = CertStoreLookup.FindUsableSigningCertificates(StoreLocation.CurrentUser, DateTime.UtcNow);
                foreach (X509Certificate2 c in found.Cast<X509Certificate2>().OrderBy(c => c.GetNameInfo(X509NameType.SimpleName, false)))
                    _certificate.Items.Add(new CertificateItem(c));
            }
            catch (Exception ex)
            {
                _certificateNote.Text = "The certificate store could not be read: " + ex.Message;
                return;
            }

            if (_certificate.Items.Count == 0)
            {
                _certificateNote.Text = "No signing certificate found. Insert your smart card, wait a few seconds, then click Refresh.";
                return;
            }
            _certificateNote.Text = "Only certificates that can sign today are listed. Your card will ask for its PIN when you sign.";
            CertificateItem match = _certificate.Items.Cast<CertificateItem>()
                .FirstOrDefault(i => string.Equals(i.Thumbprint, selectThumbprint, StringComparison.OrdinalIgnoreCase));
            _certificate.SelectedItem = match ?? _certificate.Items[0];
        }

        private sealed class CertificateItem
        {
            public CertificateItem(X509Certificate2 c)
            {
                Thumbprint = c.Thumbprint;
                _text = c.GetNameInfo(X509NameType.SimpleName, false) + "   —   issued by " +
                        c.GetNameInfo(X509NameType.SimpleName, true) + ", valid until " + c.NotAfter.ToString("d");
            }

            private readonly string _text;
            public string Thumbprint { get; }
            public override string ToString() => _text;
        }

        // ── Save ────────────────────────────────────────────────────────────

        private FlowLayoutPanel Buttons()
        {
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
            var ok = new Button { Text = "Save", AutoSize = true, Padding = new Padding(16, 4, 16, 4), BackColor = Ui.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => SaveAndClose();
            panel.Controls.Add(cancel);
            panel.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;
            return panel;
        }

        private void SaveAndClose()
        {
            if (_stampPath == null) { _tabs.SelectedIndex = 0; Ui.ShowInfo(this, "Please choose the stamp picture."); return; }
            if (_signaturePath == null) { _tabs.SelectedIndex = 1; Ui.ShowInfo(this, "Please choose the signature picture."); return; }
            string tsa = _timestampUrl.Text.Trim();
            if (tsa.Length > 0 && !(Uri.TryCreate(tsa, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            {
                _tabs.SelectedIndex = 2;
                Ui.ShowInfo(this, "The timestamp address must be a web address starting with http:// or https://, or empty.");
                return;
            }
            try
            {
                // The pictures are copied into the app's own folder, so moving or deleting the originals
                // does not lose them. A new name each time, so a stamp already placed keeps its file.
                if (!string.Equals(_stampPath, _settings.StampImageFile, StringComparison.OrdinalIgnoreCase))
                    _settings.StampImageFile = AppSettings.StoreImage(_stampPath, "stamp-" + DateTime.Now.Ticks + Path.GetExtension(_stampPath));
                if (!string.Equals(_signaturePath, _settings.SignatureImageFile, StringComparison.OrdinalIgnoreCase))
                    _settings.SignatureImageFile = AppSettings.StoreImage(_signaturePath, "signature-" + DateTime.Now.Ticks + Path.GetExtension(_signaturePath));

                _settings.StampWidthMm = (double)_stampWidth.Value;
                _settings.SignatureWidthMm = (double)_sigWidth.Value;
                _settings.SignatureHeightMm = (double)_sigHeight.Value;
                _settings.SignatureKeepAspect = _keepAspect.Checked;
                _settings.OffsetXMm = (double)_offsetX.Value;
                _settings.OffsetYMm = (double)_offsetY.Value;
                _settings.Anchor = ChosenAnchor;
                _settings.Page = (SignaturePageChoice)Math.Max(0, _page.SelectedIndex);
                _settings.CertificateThumbprint = (_certificate.SelectedItem as CertificateItem)?.Thumbprint;
                _settings.Reason = _reason.Text.Trim();
                _settings.Location = _location.Text.Trim();
                _settings.TimestampUrl = _timestampUrl.Text.Trim();
                _settings.Save();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Ui.ShowError(this, "The settings could not be saved.", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stampImage?.Dispose();
                _signatureImage?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ── Building blocks ─────────────────────────────────────────────────

        private Button ChooseButton(string text, Action<string> chosen)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 6, 0, 6) };
            b.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Title = text.TrimEnd('…'), Filter = "PNG pictures (*.png)|*.png|Pictures (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        using (Ui.LoadBitmap(dlg.FileName)) { }   // can it be read at all?
                        chosen(dlg.FileName);
                        if (!Ui.IsPng(dlg.FileName))
                            Ui.ShowInfo(this, "This picture is not a PNG, so it has no transparency: its background will cover the page.");
                    }
                    catch (Exception ex) { Ui.ShowError(this, "That file could not be opened as a picture.", ex); }
                }
            };
            return b;
        }

        private static FlowLayoutPanel Column()
            => new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill };

        private static TableLayoutPanel TwoColumns(Control left, A4Preview page)
        {
            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 440));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Dock = DockStyle.Fill;
            t.Controls.Add(left, 0, 0);
            t.Controls.Add(page, 1, 0);
            return t;
        }

        private static Label Note(string text)
            => new Label { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 6) };

        private static Label Heading(string text)
            => new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };

        private static Control Framed(ImagePreview preview, int height = 140)
        {
            preview.Size = new Size(420, height);
            preview.Margin = new Padding(0, 4, 0, 0);
            return preview;
        }

        private static FlowLayoutPanel SizeRow(string label, Control input, Label after)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2), WrapContents = false };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 70, Margin = new Padding(0, 6, 8, 0) });
            row.Controls.Add(input);
            if (input is NumericUpDown) row.Controls.Add(new Label { Text = "mm", AutoSize = true, Margin = new Padding(4, 6, 8, 0) });
            if (after != null) row.Controls.Add(after);
            return row;
        }

        private static NumericUpDown Mm(decimal min, decimal max)
            => new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = 1, Width = 80, Margin = new Padding(0, 3, 0, 3) };

        private static TrackBar Slider(decimal max)
            => new TrackBar { Minimum = (int)MinMm, Maximum = (int)max, TickFrequency = 10, Width = 400, SmallChange = 1, LargeChange = 10, Margin = new Padding(0, 0, 0, 4) };

        private static Label SizeLabel() => new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) };

        private static ComboBox DropDown()
            => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(0, 3, 0, 3) };

        private static void SetValue(NumericUpDown n, double v)
            => n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, (decimal)(double.IsNaN(v) || double.IsInfinity(v) ? 0 : v)));

        private void Sync(TrackBar slider, NumericUpDown from)
        {
            bool was = _syncing;
            _syncing = true;
            slider.Value = (int)Math.Max(slider.Minimum, Math.Min(slider.Maximum, Math.Round(from.Value)));
            _syncing = was;
        }

        /// <summary>Height over width, or a half when there is no picture yet.</summary>
        private static float Aspect(Bitmap b) => b == null || b.Width == 0 ? 0.5f : (float)b.Height / b.Width;

        private static Bitmap TryLoad(string path)
        {
            try { return path != null && File.Exists(path) ? Ui.LoadBitmap(path) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>A picture over a checkerboard, so its transparent parts are visible as such.</summary>
        private sealed class ImagePreview : Control
        {
            private Bitmap _image;

            public ImagePreview()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.ResizeRedraw, true);
            }

            /// <summary>The picture to show; owned by the caller.</summary>
            public void SetImage(Bitmap image)
            {
                _image = image;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                Ui.PaintCheckerboard(e.Graphics, r);
                if (_image != null)
                    e.Graphics.DrawImage(_image, Ui.Fit(_image.Size, Rectangle.Inflate(r, -8, -8)));
                else
                    TextRenderer.DrawText(e.Graphics, "No picture yet", Font, r, Color.Gray,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (var pen = new Pen(Color.Silver)) e.Graphics.DrawRectangle(pen, r);
            }
        }
    }
}
