// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace Tetitulz.Pdf.Splitter
{
    /// <summary>Renders one page into a cell. Created and used on the grid's worker thread only.</summary>
    internal interface IThumbnailRenderer : IDisposable
    {
        Bitmap Render(int pageIndex, Size cell);
    }

    /// <summary>
    /// A scrolling grid of page thumbnails with Explorer-style multi-selection.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not a <see cref="ListView"/> with an <see cref="ImageList"/>, which this
    /// replaced.</b> <c>ImageList.ImageSize</c> is capped at <b>256x256</b>, and a 256 px page is
    /// still too small to read or to recognise. Nothing about a ListView can raise that, so the cell
    /// size and the drawing are owned here instead.</para>
    /// <para><b>Virtualized, which is what makes large cells affordable.</b> Only visible pages are
    /// rendered, and bitmaps live in a bounded LRU cache. Holding every page at 600x800 would be
    /// 1.9 MB each — a 700-page document would need 1.3 GB, which is why the ListView version had to
    /// cap the strip at 512 pages. That cap is gone: the grid holds a page <i>count</i>, not a
    /// control or a bitmap per page.</para>
    /// <para>Selection is a set of <b>1-based</b> page numbers, matching everything else in the app.</para>
    /// </remarks>
    internal sealed class ThumbnailGrid : Panel
    {
        private const int Gap = 12;
        private const int Frame = 6;
        private const int LabelHeight = 20;

        /// <summary>Roughly what the cache may hold. Cells are large, so this is the real limit.</summary>
        private const long CacheBudgetBytes = 128L * 1024 * 1024;

        private readonly Dictionary<int, Bitmap> _cache = new Dictionary<int, Bitmap>();
        private readonly LinkedList<int> _lru = new LinkedList<int>();
        private readonly SortedSet<int> _selection = new SortedSet<int>();

        private readonly object _sync = new object();
        private readonly List<int> _requests = new List<int>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private Thread _worker;
        private volatile bool _stopping;

        /// <summary>
        /// Bumped whenever the cached bitmaps stop being valid — a new document, or a new cell size.
        /// A render that completes after the bump is discarded rather than drawn at the wrong size.
        /// </summary>
        private int _generation;

        private Func<IThumbnailRenderer> _rendererFactory;
        private int _pageCount;
        private Size _cellSize = new Size(120, 160);
        private int _columns = 1;
        private int _anchor = -1;
        private int _focus = -1;

        public ThumbnailGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.Selectable, true);
            AutoScroll = true;
            TabStop = true;
            BackColor = SystemColors.Window;
        }

        /// <summary>Raised when the user changes the selection. Not raised by <see cref="SetSelection"/>.</summary>
        public event EventHandler SelectionChanged;

        public int PageCount { get { return _pageCount; } }

        /// <summary>The selected page numbers, 1-based and ascending.</summary>
        public IEnumerable<int> SelectedPages { get { return _selection; } }

        public Size CellSize
        {
            get { return _cellSize; }
            set
            {
                if (value == _cellSize) return;
                _cellSize = value;
                DropCachedBitmaps();
                Relayout();
                Invalidate();
                RequestVisible();
            }
        }

        /// <summary>Points the grid at a document. Pass a page count of 0 to empty it.</summary>
        public void SetDocument(int pageCount, Func<IThumbnailRenderer> rendererFactory)
        {
            DropCachedBitmaps();

            _pageCount = Math.Max(0, pageCount);
            _rendererFactory = rendererFactory;
            _selection.Clear();
            _anchor = -1;
            _focus = _pageCount > 0 ? 0 : -1;

            AutoScrollPosition = new Point(0, 0);
            Relayout();
            Invalidate();
            RequestVisible();
        }

        /// <summary>Replaces the selection without raising <see cref="SelectionChanged"/>.</summary>
        public void SetSelection(IEnumerable<int> pages)
        {
            _selection.Clear();
            if (pages != null)
                foreach (int page in pages)
                    if (page >= 1 && page <= _pageCount) _selection.Add(page);

            Invalidate();
        }

        /// <summary>Scrolls <paramref name="page"/> (1-based) into view.</summary>
        public void EnsureVisible(int page)
        {
            if (page < 1 || page > _pageCount) return;

            Rectangle item = ItemBounds(page - 1);          // already scroll-translated
            int top = item.Top - AutoScrollPosition.Y;      // back to content coordinates
            int bottom = top + item.Height;

            int viewTop = -AutoScrollPosition.Y;
            int viewBottom = viewTop + ClientSize.Height;

            if (top < viewTop)
                AutoScrollPosition = new Point(0, Math.Max(0, top - Gap));
            else if (bottom > viewBottom)
                AutoScrollPosition = new Point(0, Math.Max(0, bottom - ClientSize.Height + Gap));
            else
                return;

            Invalidate();
            RequestVisible();
        }

        // ------------------------------------------------------------------
        //  Layout
        // ------------------------------------------------------------------

        private Size ItemSize
        {
            get
            {
                return new Size(_cellSize.Width + (Frame * 2),
                                _cellSize.Height + (Frame * 2) + LabelHeight);
            }
        }

        private void Relayout()
        {
            Size item = ItemSize;
            int usable = Math.Max(1, ClientSize.Width - Gap);
            _columns = Math.Max(1, usable / (item.Width + Gap));

            int rows = _pageCount == 0 ? 0 : ((_pageCount + _columns - 1) / _columns);

            // Width 0: never scroll horizontally — the column count already fits the viewport, and a
            // horizontal scrollbar would change ClientSize and re-enter this.
            AutoScrollMinSize = new Size(0, rows == 0 ? 0 : Gap + (rows * (item.Height + Gap)));
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            int before = _columns;
            Relayout();
            if (_columns != before) { Invalidate(); RequestVisible(); }
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
            RequestVisible();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
            RequestVisible();
        }

        /// <summary>Bounds of one item in <b>client</b> coordinates (scroll already applied).</summary>
        private Rectangle ItemBounds(int index)
        {
            Size item = ItemSize;
            int col = index % _columns;
            int row = index / _columns;

            return new Rectangle(
                Gap + (col * (item.Width + Gap)) + AutoScrollPosition.X,
                Gap + (row * (item.Height + Gap)) + AutoScrollPosition.Y,
                item.Width,
                item.Height);
        }

        private int IndexAt(Point client)
        {
            Size item = ItemSize;
            int x = client.X - AutoScrollPosition.X - Gap;
            int y = client.Y - AutoScrollPosition.Y - Gap;
            if (x < 0 || y < 0) return -1;

            int col = x / (item.Width + Gap);
            int row = y / (item.Height + Gap);
            if (col >= _columns) return -1;

            // Reject the gap between items, so a click in the whitespace is a deselect rather than
            // whichever cell happens to be up and to the left.
            if (x - (col * (item.Width + Gap)) >= item.Width) return -1;
            if (y - (row * (item.Height + Gap)) >= item.Height) return -1;

            int index = (row * _columns) + col;
            return index >= 0 && index < _pageCount ? index : -1;
        }

        private void VisibleRange(out int first, out int last)
        {
            Size item = ItemSize;
            int pitch = item.Height + Gap;
            int viewTop = -AutoScrollPosition.Y;

            int firstRow = Math.Max(0, (viewTop - Gap) / pitch);
            int lastRow = (viewTop + ClientSize.Height - Gap) / pitch;

            first = Math.Max(0, firstRow * _columns);
            last = Math.Min(_pageCount - 1, ((lastRow + 1) * _columns) - 1);
        }

        // ------------------------------------------------------------------
        //  Painting
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (_pageCount == 0) return;

            int first, last;
            VisibleRange(out first, out last);

            // Paint is the one place that knows for certain what is on screen, so it is what asks
            // for the missing renders. OnScroll and OnMouseWheel do NOT cover it: setting
            // AutoScrollPosition (which EnsureVisible does, for a typed range) moves the viewport
            // without raising either, so a jump to page 400 left every visible cell blank.
            bool missing = false;

            using (var labelBrush = new SolidBrush(SystemColors.ControlText))
            using (var selectedFill = new SolidBrush(SystemColors.Highlight))
            using (var selectedText = new SolidBrush(SystemColors.HighlightText))
            using (var pen = new Pen(SystemColors.ControlDark))
            using (var focusPen = new Pen(SystemColors.Highlight, 2))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int index = first; index <= last; index++)
                {
                    Rectangle item = ItemBounds(index);
                    if (!item.IntersectsWith(e.ClipRectangle)) continue;

                    bool selected = _selection.Contains(index + 1);
                    if (selected) e.Graphics.FillRectangle(selectedFill, item);

                    var cell = new Rectangle(item.X + Frame, item.Y + Frame, _cellSize.Width, _cellSize.Height);

                    Bitmap bmp;
                    if (_cache.TryGetValue(index, out bmp) && bmp != null)
                    {
                        Touch(index);
                        int w = Math.Min(bmp.Width, cell.Width);
                        int h = Math.Min(bmp.Height, cell.Height);
                        e.Graphics.DrawImage(bmp, cell.X + ((cell.Width - w) / 2),
                                                  cell.Y + ((cell.Height - h) / 2), w, h);
                    }
                    else
                    {
                        // Placeholder, so scrolling never shows holes and the layout does not jump
                        // when the render lands.
                        e.Graphics.FillRectangle(Brushes.White, cell);
                        missing = true;
                    }

                    e.Graphics.DrawRectangle(pen, cell);
                    if (index == _focus) e.Graphics.DrawRectangle(focusPen, item.X + 1, item.Y + 1, item.Width - 3, item.Height - 3);

                    var label = new Rectangle(item.X, cell.Bottom + 2, item.Width, LabelHeight);
                    e.Graphics.DrawString((index + 1).ToString(CultureInfo.InvariantCulture), Font,
                                          selected ? selectedText : labelBrush, label, format);
                }
            }

            // Terminates: a render either produces a bitmap or a blank placeholder, and both are
            // cached — so a cell is only ever requested again after the LRU evicts it.
            if (missing) RequestVisible();
        }

        // ------------------------------------------------------------------
        //  Selection
        // ------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;

            int index = IndexAt(e.Location);
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;

            if (index < 0)
            {
                if (ctrl || shift) return;
                if (_selection.Count == 0) return;
                _selection.Clear();
                Invalidate();
                OnSelectionChanged();
                return;
            }

            if (shift && _anchor >= 0)
            {
                if (!ctrl) _selection.Clear();
                int from = Math.Min(_anchor, index), to = Math.Max(_anchor, index);
                for (int i = from; i <= to; i++) _selection.Add(i + 1);
            }
            else if (ctrl)
            {
                if (!_selection.Remove(index + 1)) _selection.Add(index + 1);
                _anchor = index;
            }
            else
            {
                _selection.Clear();
                _selection.Add(index + 1);
                _anchor = index;
            }

            _focus = index;
            EnsureVisible(index + 1);
            Invalidate();
            OnSelectionChanged();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            // Arrows and Space reach OnKeyDown instead of being eaten as navigation keys.
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Space)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_pageCount == 0) return;

            bool ctrl = e.Control, shift = e.Shift;

            if (ctrl && e.KeyCode == Keys.A)
            {
                _selection.Clear();
                for (int page = 1; page <= _pageCount; page++) _selection.Add(page);
                Invalidate();
                OnSelectionChanged();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Space && _focus >= 0)
            {
                if (!_selection.Remove(_focus + 1)) _selection.Add(_focus + 1);
                _anchor = _focus;
                Invalidate();
                OnSelectionChanged();
                e.Handled = true;
                return;
            }

            int target = _focus < 0 ? 0 : _focus;
            switch (e.KeyCode)
            {
                case Keys.Left:  target = _focus - 1; break;
                case Keys.Right: target = _focus + 1; break;
                case Keys.Up:    target = _focus - _columns; break;
                case Keys.Down:  target = _focus + _columns; break;
                case Keys.Home:  target = 0; break;
                case Keys.End:   target = _pageCount - 1; break;
                default: return;
            }

            if (target < 0 || target >= _pageCount) { e.Handled = true; return; }

            _focus = target;
            if (shift && _anchor >= 0)
            {
                _selection.Clear();
                int from = Math.Min(_anchor, target), to = Math.Max(_anchor, target);
                for (int i = from; i <= to; i++) _selection.Add(i + 1);
            }
            else if (!ctrl)
            {
                _selection.Clear();
                _selection.Add(target + 1);
                _anchor = target;
            }

            EnsureVisible(target + 1);
            Invalidate();
            RequestVisible();
            OnSelectionChanged();
            e.Handled = true;
        }

        private void OnSelectionChanged()
        {
            EventHandler handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------
        //  Render queue + cache
        // ------------------------------------------------------------------

        private void RequestVisible()
        {
            if (_pageCount == 0 || _rendererFactory == null) return;

            int first, last;
            VisibleRange(out first, out last);

            lock (_sync)
            {
                _requests.Clear();
                // Reverse order: the worker pops from the end, so the topmost visible page renders
                // first and the strip fills the way the eye reads it.
                for (int index = last; index >= first; index--)
                    if (!_cache.ContainsKey(index)) _requests.Add(index);
            }

            EnsureWorker();
            _wake.Set();
        }

        private void EnsureWorker()
        {
            if (_worker != null) return;

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "thumbnail-render";
            _worker.Start();
        }

        private void WorkerLoop()
        {
            IThumbnailRenderer renderer = null;
            int rendererGeneration = -1;

            try
            {
                while (!_stopping)
                {
                    int index;
                    int generation;
                    Size cell;
                    Func<IThumbnailRenderer> factory;

                    lock (_sync)
                    {
                        if (_requests.Count == 0) { index = -1; }
                        else
                        {
                            index = _requests[_requests.Count - 1];
                            _requests.RemoveAt(_requests.Count - 1);
                        }
                        generation = _generation;
                        cell = _cellSize;
                        factory = _rendererFactory;
                    }

                    if (index < 0) { _wake.WaitOne(250); continue; }
                    if (factory == null) continue;

                    // One renderer per generation. It owns a PdfRenderSession, which is not
                    // thread-safe — this single worker is the only thread that ever touches it.
                    if (renderer == null || rendererGeneration != generation)
                    {
                        if (renderer != null) { renderer.Dispose(); renderer = null; }
                        try { renderer = factory(); }
                        catch { renderer = null; }
                        rendererGeneration = generation;
                        if (renderer == null) { _wake.WaitOne(250); continue; }
                    }

                    Bitmap bmp;
                    try { bmp = renderer.Render(index, cell); }
                    catch { bmp = null; }

                    Publish(index, generation, bmp);
                }
            }
            finally
            {
                if (renderer != null) renderer.Dispose();
            }
        }

        private void Publish(int index, int generation, Bitmap bmp)
        {
            if (_stopping || !IsHandleCreated) { if (bmp != null) bmp.Dispose(); return; }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    // A render that finished after a zoom or a document change is the wrong size for
                    // the cell it was requested for. Drop it rather than draw it scaled.
                    if (generation != _generation || index >= _pageCount)
                    {
                        if (bmp != null) bmp.Dispose();
                        return;
                    }

                    if (_cache.ContainsKey(index))
                    {
                        if (bmp != null) bmp.Dispose();
                        return;
                    }

                    _cache[index] = bmp;
                    _lru.AddLast(index);
                    TrimCache();
                    Invalidate(ItemBounds(index));
                }));
            }
            catch (InvalidOperationException)
            {
                if (bmp != null) bmp.Dispose();
            }
        }

        private void Touch(int index)
        {
            LinkedListNode<int> node = _lru.Find(index);
            if (node == null) return;
            _lru.Remove(node);
            _lru.AddLast(node);
        }

        /// <remarks>
        /// <para>The floor is <b>three screenfuls at the current zoom</b>, not a flat count. A flat
        /// floor is wrong at both ends: at 60x80 it is less than one screenful, and at 820x1093 —
        /// where two cells fill the window — a floor of 48 pins ~170 MB of bitmaps nobody can see.
        /// Scaling it keeps "scroll back a row without re-rendering" true at every step.</para>
        /// <para><b>It is not, however, what the process footprint is made of, and the measurement
        /// said so.</b> Dropping the budget 192→128 MB and the floor 48→12 moved the working set of
        /// a 721-page book at the top step by nothing at all: 233 MB after load, 328 MB at that zoom,
        /// 413 MB after 60 scroll steps — identical before and after, off a verified-fresh binary.
        /// The renderer dominates (a page at ~164 dpi is a ~10 MB intermediate), and freed GDI+
        /// bitmaps do not decommit. So treat this as bounding what the cache <i>should</i> hold, not
        /// as a memory optimisation — and if the footprint ever needs to come down, measure the
        /// render path, not this.</para>
        /// </remarks>
        private int CacheCapacity
        {
            get
            {
                long perBitmap = Math.Max(1L, (long)_cellSize.Width * _cellSize.Height * 4L);
                long byBudget = CacheBudgetBytes / perBitmap;

                int first, last;
                VisibleRange(out first, out last);
                long floor = Math.Max(12L, (last - first + 1) * 3L);

                return (int)Math.Max(floor, Math.Min(600L, byBudget));
            }
        }

        private void TrimCache()
        {
            int cap = CacheCapacity;
            while (_lru.Count > cap)
            {
                int oldest = _lru.First.Value;
                _lru.RemoveFirst();

                Bitmap bmp;
                if (_cache.TryGetValue(oldest, out bmp))
                {
                    _cache.Remove(oldest);
                    if (bmp != null) bmp.Dispose();
                }
            }
        }

        private void DropCachedBitmaps()
        {
            _generation++;
            lock (_sync) { _requests.Clear(); }

            foreach (Bitmap bmp in _cache.Values)
                if (bmp != null) bmp.Dispose();

            _cache.Clear();
            _lru.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stopping = true;
                _wake.Set();

                Thread worker = _worker;
                _worker = null;
                if (worker != null) worker.Join(TimeSpan.FromSeconds(5));

                DropCachedBitmaps();
                _wake.Close();
            }

            base.Dispose(disposing);
        }
    }
}
