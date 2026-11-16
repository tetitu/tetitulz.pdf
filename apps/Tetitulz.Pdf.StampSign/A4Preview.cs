// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// An A4 page drawn to scale with one picture on it, at its real size in millimetres. When
    /// <see cref="Draggable"/>, the picture can be dragged to a new place, and <see cref="ItemMoved"/>
    /// reports where it went.
    /// </summary>
    internal sealed class A4Preview : Control
    {
        public const float PageWidthMm = 210f, PageHeightMm = 297f;

        private Bitmap _image;
        private RectangleF _itemMm;        // x, y from the page's top-left, in mm
        private bool _dragging;
        private PointF _grabOffsetMm;

        public A4Preview()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.FromArgb(96, 96, 96);
        }

        /// <summary>The caption written above the picture ("Signature", "Stamp").</summary>
        public string Caption { get; set; } = "";

        /// <summary>Whether the picture can be dragged to a new place.</summary>
        public bool Draggable { get; set; }

        /// <summary>Raised while the picture is dragged, with its new rectangle in mm.</summary>
        public event EventHandler<RectangleF> ItemMoved;

        public void SetImage(Bitmap image)
        {
            _image = image;   // owned by the caller
            Invalidate();
        }

        public RectangleF ItemMm
        {
            get => _itemMm;
            set { _itemMm = value; Invalidate(); }
        }

        /// <summary>The page in pixels: fitted, centred, with room for its shadow and label.</summary>
        private RectangleF PageRect()
        {
            float availW = Width - 24, availH = Height - 40;
            float scale = Math.Min(availW / PageWidthMm, availH / PageHeightMm);
            float w = PageWidthMm * scale, h = PageHeightMm * scale;
            return new RectangleF((Width - w) / 2, 12, w, h);
        }

        private float PageScale => PageRect().Width / PageWidthMm;

        private RectangleF ItemPx()
        {
            RectangleF page = PageRect();
            float s = PageScale;
            return new RectangleF(page.X + _itemMm.X * s, page.Y + _itemMm.Y * s, _itemMm.Width * s, _itemMm.Height * s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            RectangleF page = PageRect();

            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillRectangle(shadow, page.X + 4, page.Y + 4, page.Width, page.Height);
            g.FillRectangle(Brushes.White, page);
            // A few grey "text lines", so the page reads as a page and the picture's size against text is clear.
            using (var line = new SolidBrush(Color.FromArgb(232, 232, 232)))
                for (float mm = 30; mm < PageHeightMm - 30; mm += 9)
                    g.FillRectangle(line, page.X + 20 * PageScale, page.Y + mm * PageScale, (PageWidthMm - 40) * PageScale, 2.5f * PageScale);

            RectangleF item = ItemPx();
            if (_image != null && item.Width > 0 && item.Height > 0)
                g.DrawImage(_image, item);
            using (var pen = new Pen(Ui.Accent, 1.5f) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, item.X, item.Y, item.Width, item.Height);
            using (var small = new Font("Segoe UI", 8f))
            using (var accent = new SolidBrush(Ui.Accent))
            {
                string label = $"{Caption}  {_itemMm.Width:0} × {_itemMm.Height:0} mm";
                SizeF size = g.MeasureString(label, small);
                float ly = item.Y - size.Height - 1 < page.Y ? item.Bottom + 1 : item.Y - size.Height - 1;
                float lx = Math.Max(page.X, Math.Min(item.X, page.Right - size.Width));
                g.DrawString(label, small, accent, lx, ly);
                string footer = Draggable ? "A4 page — drag the picture to place it" : "A4 page — the picture at its real size";
                TextRenderer.DrawText(g, footer, small, new Rectangle(0, (int)page.Bottom + 6, Width, 20), Color.WhiteSmoke,
                    TextFormatFlags.HorizontalCenter);
            }
            using (var border = new Pen(Color.FromArgb(160, 160, 160)))
                g.DrawRectangle(border, page.X, page.Y, page.Width, page.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Draggable || e.Button != MouseButtons.Left) return;
            RectangleF item = ItemPx();
            if (!item.Contains(e.Location)) return;
            _dragging = true;
            _grabOffsetMm = new PointF((e.X - item.X) / PageScale, (e.Y - item.Y) / PageScale);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
            {
                Cursor = Draggable && ItemPx().Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            RectangleF page = PageRect();
            float x = (e.X - page.X) / PageScale - _grabOffsetMm.X;
            float y = (e.Y - page.Y) / PageScale - _grabOffsetMm.Y;
            x = Math.Max(0, Math.Min(x, PageWidthMm - _itemMm.Width));
            y = Math.Max(0, Math.Min(y, PageHeightMm - _itemMm.Height));
            _itemMm = new RectangleF(x, y, _itemMm.Width, _itemMm.Height);
            Invalidate();
            ItemMoved?.Invoke(this, _itemMm);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }
    }
}
