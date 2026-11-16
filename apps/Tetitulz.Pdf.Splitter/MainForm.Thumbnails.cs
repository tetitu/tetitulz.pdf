// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Splitter
{
    internal sealed partial class MainForm
    {
        /// <summary>
        /// The zoom steps, smallest first, in 3:4 — the shape of most portrait pages.
        /// </summary>
        /// <remarks>
        /// The top of this list is the point of it: a <c>ListView</c>'s <c>ImageList</c> would cap a
        /// cell at <b>256x256</b>, too small to read a page or recognise one.
        /// <see cref="ThumbnailGrid"/> draws the cells itself, so the only limit is memory — and it
        /// is virtualized, so the limit is per <i>visible</i> page rather than per page in the
        /// document.
        /// </remarks>
        private static readonly Size[] ThumbnailSizes =
        {
            new Size(60, 80),
            new Size(90, 120),
            new Size(120, 160),
            new Size(160, 213),
            new Size(220, 293),
            new Size(300, 400),
            new Size(420, 560),
            new Size(600, 800),
            new Size(820, 1093),
        };

        private const int DefaultZoomIndex = 2;

        /// <summary>
        /// Render resolution for a 120 px cell. Scaled with the cell below, so a larger thumbnail is
        /// genuinely sharper rather than the same pixels enlarged — which is the whole point of the
        /// upper steps.
        /// </summary>
        private const int ThumbnailDpiAt120 = 24;

        private int _zoomIndex = DefaultZoomIndex;

        private Size ThumbnailSize { get { return ThumbnailSizes[_zoomIndex]; } }

        /// <summary>Every page has a cell now — the grid holds a count, not a control per page.</summary>
        private int ThumbnailCount { get { return _thumbs.PageCount; } }

        private void InitialiseThumbnailStrip()
        {
            _thumbs.CellSize = ThumbnailSize;

            // Ctrl+wheel is the gesture people already have for this, and the grid would otherwise
            // just scroll.
            _thumbs.MouseWheel += (s, e) =>
            {
                if ((Control.ModifierKeys & Keys.Control) == 0) return;
                Zoom(e.Delta > 0 ? 1 : -1);
                ((HandledMouseEventArgs)e).Handled = true;
            };
        }

        /// <summary>Dpi that fills a cell of this width, keeping every step sharp.</summary>
        private static int DpiFor(Size cell)
        {
            return Math.Max(6, (int)Math.Round(ThumbnailDpiAt120 * (cell.Width / 120.0)));
        }

        /// <summary>
        /// Steps the thumbnail size and re-renders what is on screen.
        /// </summary>
        /// <remarks>
        /// The selection lives in the model, not in the grid, so it survives — it is pushed back
        /// afterwards. Only the visible cells are re-rendered, so this costs the same at page 700 of
        /// a 721-page document as it does at page 1.
        /// </remarks>
        private void Zoom(int direction)
        {
            int next = _zoomIndex + direction;
            if (next < 0 || next >= ThumbnailSizes.Length || next == _zoomIndex) return;

            _zoomIndex = next;
            _thumbs.CellSize = ThumbnailSize;

            _syncing = true;
            try
            {
                _thumbs.SetSelection(_selected);
            }
            finally
            {
                _syncing = false;
            }

            UpdateZoomButtons();
            SetStatus("Thumbnail size " +
                      ThumbnailSize.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                      ThumbnailSize.Height.ToString(CultureInfo.InvariantCulture) + ".");
        }

        private void UpdateZoomButtons()
        {
            _zoomOutButton.Enabled = _zoomIndex > 0;
            _zoomInButton.Enabled = _zoomIndex < ThumbnailSizes.Length - 1;
        }

        /// <summary>Points the grid at the loaded document.</summary>
        /// <remarks>
        /// No page cap: an <c>ImageList</c> would hold a bitmap per page, while the grid renders
        /// only what is visible, so a 721-page document costs the same as a 2-page one until you
        /// scroll.
        /// </remarks>
        private void RebuildThumbnailStrip(int pageCount)
        {
            byte[] bytes = _bytes;
            string password = _password;

            _thumbs.CellSize = ThumbnailSize;
            _thumbs.SetDocument(pageCount, () => new PdfThumbnailRenderer(bytes, password));
        }

        /// <summary>Applies the model to the grid. Callers hold <c>_syncing</c>.</summary>
        private void ApplySelectionToStrip()
        {
            _thumbs.SetSelection(_selected);
        }

        private void CancelThumbnails()
        {
            // The grid owns its worker and drops in-flight work when the document changes; pointing
            // it at nothing is the cancel.
            _thumbs.SetDocument(0, null);
        }

        /// <summary>
        /// Renders pages for the grid, holding one <see cref="PdfRenderSession"/> for the document.
        /// </summary>
        /// <remarks>
        /// <para>Constructed and used on the grid's single worker thread. That matters:
        /// <c>PdfRenderSession</c> is <b>not</b> thread-safe, and it is the right handle here because
        /// it keeps the parsed document, the font cache and the content-operation cache across every
        /// page of the strip.</para>
        /// <para>The backing stream must outlive the session, so both are owned by this object and
        /// disposed together.</para>
        /// </remarks>
        private sealed class PdfThumbnailRenderer : IThumbnailRenderer
        {
            private readonly MemoryStream _stream;
            private readonly PdfRenderSession _session;

            public PdfThumbnailRenderer(byte[] bytes, string password)
            {
                _stream = new MemoryStream(bytes, false);
                try
                {
                    var options = password == null ? null : new PdfRenderOptions { Password = password };
                    _session = PdfRenderSession.Open(_stream, options);
                }
                catch
                {
                    _stream.Dispose();
                    throw;
                }
            }

            public Bitmap Render(int pageIndex, Size cell)
            {
                try
                {
                    byte[] png = _session.RenderPageToImage(pageIndex, PdfImageFormat.Png, DpiFor(cell));
                    return Compose(png, cell);
                }
                catch
                {
                    // A page that will not render still gets a cell, so the numbering never shifts.
                    return BlankThumbnail(cell);
                }
            }

            public void Dispose()
            {
                _session.Dispose();
                _stream.Dispose();
            }
        }

        /// <summary>Scales a rendered page into the cell, centred, preserving aspect ratio.</summary>
        private static Bitmap Compose(byte[] png, Size cell)
        {
            using (var ms = new MemoryStream(png, false))
            using (Image src = Image.FromStream(ms))
            {
                double scale = Math.Min((double)cell.Width / src.Width,
                                        (double)cell.Height / src.Height);

                int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                int h = Math.Max(1, (int)Math.Round(src.Height * scale));

                // Sized to the PAGE, not the cell: a landscape page in a portrait cell would
                // otherwise carry white bars that hide where the page actually ends. The grid centres
                // whatever it is given.
                var canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                try
                {
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    return canvas;
                }
                catch
                {
                    canvas.Dispose();
                    throw;
                }
            }
        }

        private static Bitmap BlankThumbnail(Size cell)
        {
            var canvas = new Bitmap(cell.Width, cell.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White);
                g.DrawRectangle(Pens.Silver, 0, 0, cell.Width - 1, cell.Height - 1);
            }
            return canvas;
        }
    }
}
