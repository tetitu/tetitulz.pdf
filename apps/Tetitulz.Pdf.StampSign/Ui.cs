// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>Small shared pieces of the user interface.</summary>
    internal static class Ui
    {
        public const string AppTitle = "Tetitulz Stamp & Sign";

        public static readonly Font BaseFont = new Font("Segoe UI", 10f);
        public static readonly Font BigFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        public static readonly Color Accent = Color.FromArgb(0, 102, 204);
        public static readonly Color HintBack = Color.FromArgb(255, 248, 220);

        public static void ShowError(IWin32Window owner, string what, Exception ex)
        {
            string detail = ex == null ? "" : Environment.NewLine + Environment.NewLine + ex.Message;
            MessageBox.Show(owner, what + detail, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static void ShowInfo(IWin32Window owner, string text)
            => MessageBox.Show(owner, text, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

        /// <summary>An image read into memory, so the file stays free to be replaced while the app runs.</summary>
        public static Bitmap LoadBitmap(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        /// <summary>True when the file starts with the PNG signature.</summary>
        public static bool IsPng(string path)
        {
            try
            {
                using (var f = File.OpenRead(path))
                {
                    var sig = new byte[8];
                    return f.Read(sig, 0, 8) == 8 && sig[0] == 0x89 && sig[1] == 0x50 && sig[2] == 0x4E && sig[3] == 0x47;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>A light checkerboard, so a transparent image's clear parts show as clear.</summary>
        public static void PaintCheckerboard(Graphics g, Rectangle r)
        {
            using (var light = new SolidBrush(Color.White))
            using (var dark = new SolidBrush(Color.FromArgb(225, 225, 225)))
            {
                g.FillRectangle(light, r);
                const int cell = 8;
                for (int y = r.Top; y < r.Bottom; y += cell)
                    for (int x = r.Left + ((y - r.Top) / cell % 2) * cell; x < r.Right; x += 2 * cell)
                        g.FillRectangle(dark, x, y, Math.Min(cell, r.Right - x), Math.Min(cell, r.Bottom - y));
            }
        }

        /// <summary>An image fitted inside a rectangle, centred, its proportions kept.</summary>
        public static Rectangle Fit(Size image, Rectangle into)
        {
            if (image.Width <= 0 || image.Height <= 0) return into;
            double scale = Math.Min((double)into.Width / image.Width, (double)into.Height / image.Height);
            int w = (int)(image.Width * scale), h = (int)(image.Height * scale);
            return new Rectangle(into.X + (into.Width - w) / 2, into.Y + (into.Height - h) / 2, w, h);
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
