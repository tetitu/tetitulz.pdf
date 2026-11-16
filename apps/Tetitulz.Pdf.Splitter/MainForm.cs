// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;

namespace Tetitulz.Pdf.Splitter
{
    /// <summary>
    /// One window: a thumbnail strip, a printer-style page-range box that stays in sync with it,
    /// and Save As.
    /// </summary>
    /// <remarks>
    /// Hand-coded, no designer file and no <c>.resx</c> — the convention every WinForms surface in
    /// this repository follows (<c>demo/Viewer</c>, and the seven partials of
    /// <c>Tetitulz.Pdf.WinForms.PdfViewer</c>).
    /// </remarks>
    internal sealed partial class MainForm : Form
    {
        /// <summary>
        /// The <b>selection model</b>, 1-based, and the single source of truth.
        /// </summary>
        /// <remarks>
        /// Not <c>ListView.SelectedIndices</c>, because the strip is capped at
        /// <see cref="MaxThumbnails"/> while the range box stays authoritative over every page. A
        /// selection typed as <c>1,900</c> in a 1000-page document has no thumbnail for page 900, so
        /// reading the selection off the strip would silently drop it the moment anyone clicked.
        /// </remarks>
        private readonly SortedSet<int> _selected = new SortedSet<int>();

        /// <summary>Width reserved for the range box and buttons.</summary>
        private const int SidePanelWidth = 260;

        /// <summary>Enough for two thumbnail columns plus the scrollbar.</summary>
        private const int ThumbnailPaneMinWidth = 220;

        private readonly SplitContainer _split;
        private readonly ThumbnailGrid _thumbs;
        private readonly TextBox _rangeBox;
        private readonly Label _countLabel;
        private readonly Label _fileLabel;
        private readonly Button _openButton;
        private readonly Button _zoomOutButton;
        private readonly Button _zoomInButton;
        private readonly Button _selectAllButton;
        private readonly Button _clearButton;
        private readonly Button _saveButton;
        private readonly ComboBox _modeBox;
        private readonly NumericUpDown _perFileBox;
        private readonly Label _perFileLabel;
        private readonly StatusStrip _statusStrip;
        private readonly ToolStripStatusLabel _status;

        /// <summary>The loaded file, held in memory so every operation reads the same bytes.</summary>
        private byte[] _bytes;
        private string _path;
        private int _pageCount;

        /// <summary>Non-null only for an encrypted document that was successfully opened.</summary>
        private string _password;
        private bool _encrypted;

        /// <summary>
        /// Guards the two-way binding. One flag, not two, and not handler detach/attach: both
        /// directions run through here and neither may re-enter the other.
        /// </summary>
        private bool _syncing;

        public MainForm(string initialPath)
        {
            Text = "Tetitulz.Pdf — PDF Splitter";
            Width = 1000;
            Height = 720;
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;

            // ---- top bar -------------------------------------------------------------
            _openButton = new Button
            {
                Text = "&Open PDF...",
                Location = new Point(8, 8),
                Size = new Size(100, 26),
            };
            _openButton.Click += (s, e) => PromptForFile();

            // Thumbnail zoom. Ctrl+wheel over the strip does the same thing (see
            // InitialiseThumbnailStrip); these exist so the feature is discoverable.
            _zoomOutButton = new Button
            {
                Text = "−",
                Location = new Point(116, 8),
                Size = new Size(30, 26),
                UseVisualStyleBackColor = true,
            };
            _zoomOutButton.Click += (s, e) => Zoom(-1);

            _zoomInButton = new Button
            {
                Text = "+",
                Location = new Point(150, 8),
                Size = new Size(30, 26),
                UseVisualStyleBackColor = true,
            };
            _zoomInButton.Click += (s, e) => Zoom(1);

            var zoomTip = new ToolTip();
            zoomTip.SetToolTip(_zoomOutButton, "Smaller thumbnails (Ctrl+mouse wheel)");
            zoomTip.SetToolTip(_zoomInButton, "Larger thumbnails (Ctrl+mouse wheel)");

            _fileLabel = new Label
            {
                AutoSize = true,
                Location = new Point(192, 13),
                Text = "No document open.",
            };

            var topBar = new Panel { Dock = DockStyle.Top, Height = 42 };
            topBar.Controls.Add(_openButton);
            topBar.Controls.Add(_zoomOutButton);
            topBar.Controls.Add(_zoomInButton);
            topBar.Controls.Add(_fileLabel);

            // ---- thumbnail strip -----------------------------------------------------
            //
            // One owner-drawn, virtualized control rather than a ListView or a control per page.
            // A ListView would be free Explorer semantics, but its ImageList caps the cell at
            // 256x256 — too small to read a page or recognise one, which is the whole job here.
            // ThumbnailGrid draws its own cells and renders only what is on screen, so the cell can
            // be as large as a page and a 700-page document costs no more than a 2-page one.
            _thumbs = new ThumbnailGrid { Dock = DockStyle.Fill };
            _thumbs.SelectionChanged += OnThumbSelectionChanged;

            // ---- right-hand panel ----------------------------------------------------
            var pagesLabel = new Label
            {
                Text = "Pages:",
                AutoSize = true,
                Location = new Point(10, 12),
            };

            // No Anchor on anything in this panel, deliberately. Panel2 is the FixedPanel, so it
            // never changes width — and an anchor is measured against the container's size AT
            // CONSTRUCTION, which for a fresh Panel is the default 200 px. Anchoring Right against
            // that pushed every control off the visible edge once the panel reached its real width.
            const int SideMargin = 10;
            const int SideContentWidth = SidePanelWidth - (SideMargin * 2) - 8;   // 8 = panel padding

            _rangeBox = new TextBox
            {
                Location = new Point(SideMargin, 32),
                Size = new Size(SideContentWidth, 23),
            };
            _rangeBox.TextChanged += OnRangeTextChanged;

            var hintLabel = new Label
            {
                AutoSize = false,
                Location = new Point(SideMargin, 60),
                Size = new Size(SideContentWidth, 32),
                ForeColor = SystemColors.GrayText,
                Text = "e.g. 1-3, 7, 12-  — commas and ranges,\nsame as a print dialog.",
            };

            _countLabel = new Label
            {
                AutoSize = false,
                Location = new Point(SideMargin, 98),
                Size = new Size(SideContentWidth, 18),
                Text = string.Empty,
            };

            int halfWidth = (SideContentWidth - 8) / 2;

            _selectAllButton = new Button
            {
                Text = "Select &all",
                Location = new Point(SideMargin, 124),
                Size = new Size(halfWidth, 26),
            };
            _selectAllButton.Click += (s, e) => SelectAll();

            _clearButton = new Button
            {
                Text = "&Clear",
                Location = new Point(SideMargin + halfWidth + 8, 124),
                Size = new Size(halfWidth, 26),
            };
            _clearButton.Click += (s, e) => ClearSelection();

            var modeLabel = new Label
            {
                Text = "Write:",
                AutoSize = true,
                Location = new Point(SideMargin, 162),
            };

            _modeBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(SideMargin, 182),
                Size = new Size(SideContentWidth, 23),
            };
            // In SplitMode's order: the index is the mode.
            _modeBox.Items.AddRange(new object[]
            {
                "The selected pages, one file",
                "Each part of the range, a file each",
                "The whole document, every N pages",
                "The whole document, files under N MB",
            });
            _modeBox.SelectedIndex = 0;
            _modeBox.SelectedIndexChanged += (s, e) => UpdateEnabledState();

            _perFileLabel = new Label
            {
                AutoSize = false,
                Location = new Point(SideMargin, 214),
                Size = new Size(SideContentWidth - 90, 23),
                TextAlign = ContentAlignment.MiddleLeft,
            };

            _perFileBox = new NumericUpDown
            {
                Location = new Point(SideMargin + SideContentWidth - 80, 212),
                Size = new Size(80, 23),
                Minimum = 1,
                Maximum = 100000,
                Value = 10,
            };

            _saveButton = new Button
            {
                Text = "&Save selection as...",
                Location = new Point(SideMargin, 248),
                Size = new Size(SideContentWidth, 32),
            };
            _saveButton.Click += OnSaveClick;

            var side = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            side.Controls.Add(pagesLabel);
            side.Controls.Add(_rangeBox);
            side.Controls.Add(hintLabel);
            side.Controls.Add(_countLabel);
            side.Controls.Add(_selectAllButton);
            side.Controls.Add(_clearButton);
            side.Controls.Add(modeLabel);
            side.Controls.Add(_modeBox);
            side.Controls.Add(_perFileLabel);
            side.Controls.Add(_perFileBox);
            side.Controls.Add(_saveButton);

            // Docked only. Every size on this control is applied in OnLoad — see the remarks there.
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel2,
            };
            _split.Panel1.Controls.Add(_thumbs);
            _split.Panel2.Controls.Add(side);

            // ---- status --------------------------------------------------------------
            _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _statusStrip = new StatusStrip();
            _statusStrip.Items.Add(_status);

            Controls.Add(_split);
            Controls.Add(topBar);
            Controls.Add(_statusStrip);

            InitialiseThumbnailStrip();
            UpdateEnabledState();
            UpdateZoomButtons();

            AllowDrop = true;
            DragEnter += (s, e) =>
                e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            DragDrop += (s, e) =>
            {
                var files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0) LoadDocument(files[0]);
            };

            if (!string.IsNullOrEmpty(initialPath))
                Shown += (s, e) => LoadDocument(initialPath);
        }

        // ------------------------------------------------------------------
        //  Opening
        // ------------------------------------------------------------------

        private void PromptForFile()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
                dialog.Title = "Open a PDF to split";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    LoadDocument(dialog.FileName);
            }
        }

        private void LoadDocument(string path)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                ShowError("Could not read the file.", ex);
                return;
            }

            bool encrypted;
            try
            {
                using (var probe = new MemoryStream(bytes, false))
                using (PdfDocument doc = PdfDocument.Open(probe))
                    encrypted = doc.IsEncrypted;
            }
            catch (Exception ex)
            {
                ShowError("That does not look like a PDF this library can open.", ex);
                return;
            }

            string password = null;
            int pageCount;

            if (encrypted)
            {
                // The empty password first: a document encrypted only to set permissions opens with
                // it, and asking the user for a password they were never given is its own defect.
                if (!TryCountPages(bytes, string.Empty, out pageCount))
                {
                    string reason = null;
                    while (true)
                    {
                        password = PasswordForm.Ask(this, Path.GetFileName(path), reason);
                        if (password == null) return;            // cancelled
                        if (TryCountPages(bytes, password, out pageCount)) break;
                        reason = "That password did not open the document. Try again.";
                    }
                }
                else
                {
                    password = string.Empty;
                }
            }
            else if (!TryCountPages(bytes, null, out pageCount))
            {
                ShowError("The document opened but its page tree could not be read.", null);
                return;
            }

            CancelThumbnails();

            _bytes = bytes;
            _path = path;
            _pageCount = pageCount;
            _password = password;
            _encrypted = encrypted;
            _selected.Clear();

            _fileLabel.Text = Path.GetFileName(path) + "  —  " +
                              pageCount.ToString(CultureInfo.InvariantCulture) +
                              (pageCount == 1 ? " page" : " pages");

            _syncing = true;
            try
            {
                _rangeBox.Text = string.Empty;
                _rangeBox.BackColor = SystemColors.Window;
            }
            finally
            {
                _syncing = false;
            }

            RebuildThumbnailStrip(pageCount);
            UpdateCount();
            UpdateEnabledState();
            UpdateZoomButtons();

            SetStatus(_encrypted
                ? "This document is encrypted: what you extract is protected the same way. " +
                  "Select pages in the grid, or type a range."
                : "Select pages in the grid, or type a range. Ctrl+wheel or −/+ resizes the pages.");
        }

        private static bool TryCountPages(byte[] bytes, string password, out int pageCount)
        {
            pageCount = 0;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                    pageCount = PdfOperations.GetPageCount(stream, password);
            }
            catch
            {
                return false;
            }

            return pageCount > 0;
        }

        // ------------------------------------------------------------------
        //  Two-way binding: strip <-> range box
        // ------------------------------------------------------------------

        private void OnThumbSelectionChanged(object sender, EventArgs e)
        {
            if (_syncing) return;

            _syncing = true;
            try
            {
                // MERGE, not replace. The grid covers every page now, so the second loop is
                // normally the whole story — but a page outside its range must still survive a
                // click, and keeping the merge means the invariant does not depend on that.
                var merged = new SortedSet<int>();
                foreach (int page in _selected)
                    if (page > ThumbnailCount) merged.Add(page);
                foreach (int page in _thumbs.SelectedPages)
                    merged.Add(page);

                _selected.Clear();
                foreach (int page in merged) _selected.Add(page);

                _rangeBox.Text = PageRangeText.Format(_selected);
                _rangeBox.BackColor = SystemColors.Window;
                UpdateCount();
                UpdateEnabledState();
            }
            finally
            {
                _syncing = false;
            }
        }

        private void OnRangeTextChanged(object sender, EventArgs e)
        {
            if (_syncing) return;

            _syncing = true;
            try
            {
                IReadOnlyList<int> pages;
                string error;
                if (!PageRangeText.TryParse(_rangeBox.Text, _pageCount, out pages, out error))
                {
                    // Tint and explain, but LEAVE THE MODEL ALONE: "1-" is a step on the way to
                    // "1-3", and clearing the selection under the user's fingers as they type is
                    // worse than showing a stale-but-correct one for a keystroke.
                    _rangeBox.BackColor = InvalidInputColor;
                    SetStatus(error);
                    return;
                }

                _rangeBox.BackColor = SystemColors.Window;

                _selected.Clear();
                foreach (int page in pages) _selected.Add(page);

                ApplySelectionToStrip();

                // Scroll to what was just typed. Without this, typing "400-405" in a 700-page
                // document highlights cells nobody can see, which is most of the reason a range and
                // a grid feel like two unrelated tools.
                if (_selected.Count > 0) _thumbs.EnsureVisible(_selected.Min);

                UpdateCount();
                UpdateEnabledState();
                SetStatus(string.Empty);
            }
            finally
            {
                _syncing = false;
            }
        }

        private void SelectAll()
        {
            if (_pageCount <= 0) return;

            _syncing = true;
            try
            {
                _selected.Clear();
                for (int page = 1; page <= _pageCount; page++) _selected.Add(page);

                _rangeBox.Text = PageRangeText.Format(_selected);
                _rangeBox.BackColor = SystemColors.Window;
                ApplySelectionToStrip();
                UpdateCount();
                UpdateEnabledState();
            }
            finally
            {
                _syncing = false;
            }
        }

        private void ClearSelection()
        {
            _syncing = true;
            try
            {
                _selected.Clear();
                _rangeBox.Text = string.Empty;
                _rangeBox.BackColor = SystemColors.Window;
                ApplySelectionToStrip();
                UpdateCount();
                UpdateEnabledState();
            }
            finally
            {
                _syncing = false;
            }
        }

        // ------------------------------------------------------------------
        //  Saving
        // ------------------------------------------------------------------

        private SplitMode Mode { get { return (SplitMode)Math.Max(0, _modeBox.SelectedIndex); } }

        private async void OnSaveClick(object sender, EventArgs e)
        {
            if (_bytes == null) return;

            SplitMode mode = Mode;
            IReadOnlyList<IReadOnlyList<int>> groups = new IReadOnlyList<int>[0];
            if (mode == SplitMode.PerGroup)
            {
                string groupError;
                if (!PageRangeText.TryParseGroups(_rangeBox.Text, _pageCount, out groups, out groupError) || groups.Count == 0)
                {
                    SetStatus(groupError ?? "Type the parts in the range box: each comma-separated part becomes a file.");
                    return;
                }
            }
            else if (mode == SplitMode.Selection && _selected.Count == 0)
            {
                return;
            }

            string stem = string.IsNullOrEmpty(_path) ? "extract" : Path.GetFileNameWithoutExtension(_path);
            string target;
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*";
                dialog.FileName = mode == SplitMode.Selection ? SplitJob.FileNameFor(stem, _selected) : stem + ".pdf";
                dialog.Title = mode == SplitMode.Selection
                    ? "Save the selected pages"
                    : "Name the files: each part is saved beside it as <name>_part01_pages_…";
                // A part's name is made from this one, so the dialog's own overwrite question is about the wrong file.
                dialog.OverwritePrompt = mode == SplitMode.Selection;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                target = dialog.FileName;
            }

            var selection = new List<int>(_selected);
            byte[] bytes = _bytes;
            string password = _password;
            int perFile = (int)_perFileBox.Value;

            SetStatus("Planning the files...");
            UseWaitCursor = true;
            _saveButton.Enabled = false;
            try
            {
                string planWarning = null;
                IReadOnlyList<IReadOnlyList<int>> chunks;
                try
                {
                    chunks = await Task.Run(() => SplitJob.Plan(bytes, password, mode, selection, groups, perFile, out planWarning));
                }
                catch (Exception ex)
                {
                    ShowError("The pages could not be divided into files.", ex);
                    return;
                }
                if (chunks.Count == 0) { SetStatus("There are no pages to write."); return; }

                string[] paths = chunks.Count == 1 && mode == SplitMode.Selection
                    ? new[] { target }
                    : SplitJob.PartPaths(target, chunks);

                if (mode != SplitMode.Selection)
                {
                    int existing = 0;
                    foreach (string p in paths) if (File.Exists(p)) existing++;
                    if (existing > 0 && MessageBox.Show(this,
                            existing.ToString(CultureInfo.InvariantCulture) + " of the " +
                            paths.Length.ToString(CultureInfo.InvariantCulture) + " files already exist. Replace them?",
                            "Tetitulz.Pdf — PDF Splitter", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    {
                        SetStatus("Nothing was written.");
                        return;
                    }
                }

                var warnings = new List<string>();
                if (!string.IsNullOrEmpty(planWarning)) warnings.Add(planWarning);
                int written = 0;
                bool allProtected = true;
                for (int i = 0; i < chunks.Count; i++)
                {
                    IReadOnlyList<int> pages = chunks[i];
                    string path = paths[i];
                    SetStatus("Writing " + Path.GetFileName(path) + " (" + (i + 1).ToString(CultureInfo.InvariantCulture) +
                              " of " + chunks.Count.ToString(CultureInfo.InvariantCulture) + ")...");
                    SplitOutcome outcome = await Task.Run(() =>
                    {
                        using (var buffer = new MemoryStream())
                        {
                            SplitOutcome o = SplitJob.Write(bytes, password, pages, buffer);
                            if (o.Error == null) File.WriteAllBytes(path, buffer.ToArray());
                            return o;
                        }
                    });
                    if (outcome.Error != null)
                    {
                        SetStatus("The extract failed after " + written.ToString(CultureInfo.InvariantCulture) + " file(s).");
                        MessageBox.Show(this, Path.GetFileName(path) + ": " + outcome.Error, "Could not extract the pages",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    written++;
                    allProtected &= outcome.Protected;
                    foreach (string w in outcome.Warnings)
                        if (!warnings.Contains(w)) warnings.Add(w);
                }

                var message = new StringBuilder();
                message.Append(written == 1
                    ? "Wrote " + chunks[0].Count.ToString(CultureInfo.InvariantCulture) + " page(s) to " + Path.GetFileName(paths[0])
                    : "Wrote " + written.ToString(CultureInfo.InvariantCulture) + " files beside " + Path.GetFileName(target));
                message.Append(password != null && allProtected ? ", protected as the document is." : ".");
                foreach (string warning in warnings) message.Append("  ").Append(warning);
                SetStatus(message.ToString());
            }
            finally
            {
                UseWaitCursor = false;
                _saveButton.Enabled = true;
                UpdateEnabledState();
            }
        }

        // ------------------------------------------------------------------
        //  Shared UI bits
        // ------------------------------------------------------------------

        /// <summary>A red that stays legible under a high-contrast or dark Windows theme.</summary>
        private static readonly Color InvalidInputColor = Color.FromArgb(255, 235, 235);

        private void UpdateCount()
        {
            _countLabel.Text = _pageCount == 0
                ? string.Empty
                : _selected.Count.ToString(CultureInfo.InvariantCulture) + " of " +
                  _pageCount.ToString(CultureInfo.InvariantCulture) + " pages selected";
        }

        private void UpdateEnabledState()
        {
            bool open = _bytes != null && _pageCount > 0;
            _rangeBox.Enabled = open;
            _selectAllButton.Enabled = open;
            _clearButton.Enabled = open && _selected.Count > 0;
            _modeBox.Enabled = open;

            SplitMode mode = Mode;
            bool perFile = mode == SplitMode.EveryNPages || mode == SplitMode.UnderMegabytes;
            _perFileLabel.Visible = _perFileBox.Visible = perFile;
            _perFileBox.Enabled = open;
            _perFileLabel.Text = mode == SplitMode.UnderMegabytes ? "Megabytes per file:" : "Pages per file:";

            switch (mode)
            {
                case SplitMode.Selection:
                    _saveButton.Text = "&Save selection as...";
                    _saveButton.Enabled = open && _selected.Count > 0;
                    break;
                case SplitMode.PerGroup:
                    _saveButton.Text = "&Save each part as...";
                    _saveButton.Enabled = open && _rangeBox.Text.Trim().Length > 0;
                    break;
                default:
                    _saveButton.Text = "&Split into files...";
                    _saveButton.Enabled = open;
                    break;
            }
        }

        private void SetStatus(string text)
        {
            _status.Text = text ?? string.Empty;
        }

        private void ShowError(string what, Exception ex)
        {
            string detail = ex == null ? what : what + "\n\n" + ex.GetType().Name + ": " + ex.Message;
            SetStatus(what);
            MessageBox.Show(this, detail, "Tetitulz.Pdf — PDF Splitter",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>Applies every <see cref="SplitContainer"/> size, once the control has a real one.</summary>
        /// <remarks>
        /// <para>None of this can happen in the constructor. A <c>SplitContainer</c> starts 150 px
        /// wide, so <c>Panel2MinSize = 260</c> is wider than the whole control; its setter re-applies
        /// <c>SplitterDistance</c>, cannot find a legal value, and throws
        /// <see cref="InvalidOperationException"/> out of the constructor — the window never
        /// appears, and the only evidence is an <c>0xE0434352</c> exit code.</para>
        /// <para>By <c>OnLoad</c> the docked container has the form's real width, so all three
        /// values are satisfiable. Clamped anyway, because a small screen or a high DPI scale can
        /// still make the form narrower than the two minimums together.</para>
        /// </remarks>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int usable = _split.Width - _split.SplitterWidth;
            if (usable <= 0) return;

            int side = Math.Min(SidePanelWidth, Math.Max(1, usable / 2));
            int strip = Math.Min(ThumbnailPaneMinWidth, Math.Max(1, usable - side));

            _split.Panel1MinSize = strip;
            _split.Panel2MinSize = side;
            _split.SplitterDistance = Math.Max(strip, usable - side);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CancelThumbnails();
            base.OnFormClosing(e);
        }
    }
}
