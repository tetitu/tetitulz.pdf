// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// The one drawing of a <see cref="BoxShadow"/> — behind a paragraph's box and behind a block
    /// box alike: a filled path at the shadow's offset when its edge is hard, and a soft-masked image
    /// when it is blurred, since a content stream has no blur.
    /// </summary>
    /// <remarks>
    /// <para><b>The blur is a raster.</b> CSS Backgrounds 3 §7.1 asks for a Gaussian whose standard
    /// deviation is half the blur radius; the box's coverage is rasterised with its corners, blurred
    /// separably, and drawn as an RGB image of the shadow's colour under that coverage as its soft
    /// mask, one blur radius past the box on every side (the Gaussian's tail beyond it is under 2.3 %).
    /// A caller's fill alpha applies to the image as it applied to the path.</para>
    /// <para><b>The mask's size is bounded.</b> The box and the blur are author-supplied, so the
    /// pixel count is capped at <see cref="MaxShadowMaskPixels"/> and a larger shadow is rendered
    /// coarser rather than refused — a coarser soft edge is still the decoration asked for.</para>
    /// <para>A blur and a spread are drawn, not reported; the two sites share one drawing.</para>
    /// </remarks>
    public sealed partial class PdfExporter
    {
        /// <summary>Pixels a point in a blurred shadow's mask: two, so the soft edge shows no steps at print size.</summary>
        private const double ShadowMaskResolution = 2;

        /// <summary>The most pixels a blurred shadow's mask may hold; a larger one is rendered at a coarser resolution.</summary>
        internal const int MaxShadowMaskPixels = 2_000_000;

        /// <summary>
        /// The widest Gaussian, in pixels of deviation, the mask is blurred with; a wider blur lowers
        /// the resolution instead, since the kernel's cost is the deviation times the pixel count and
        /// a wide blur is a smooth gradient that a coarse raster carries as well. Sixteen is a 97-tap kernel.
        /// </summary>
        private const double MaxBlurSigmaPixels = 16;

        /// <summary>
        /// A box's shadows in the order they are painted — the reverse of CSS's, whose first is on
        /// top — keeping those that are <paramref name="inset"/> or those that are not.
        /// </summary>
        internal static IEnumerable<BoxShadow> PaintOrder(BoxShadow? first, List<BoxShadow> more, bool inset)
        {
            for (int i = more.Count - 1; i >= 0; i--)
                if (more[i].Inset == inset) yield return more[i];
            if (first != null && first.Inset == inset) yield return first;
        }

        /// <summary>
        /// Draws an inset <paramref name="shadow"/> inside the padding box whose bottom-left corner
        /// is (<paramref name="x"/>, <paramref name="bottom"/>), <paramref name="w"/> ×
        /// <paramref name="h"/>, with corners <paramref name="pad"/>: everything inside it but outside
        /// its hole — the padding box shrunk by the spread and moved by the offset — in the shadow's
        /// colour, the hole's edge softened by the blur (CSS Backgrounds 3 §7.1).
        /// </summary>
        private static void DrawInsetShadow(Ctx ctx, BoxShadow shadow, double x, double bottom, double w, double h, Corners pad, double alpha)
        {
            if (w <= 0 || h <= 0) return;
            double spread = shadow.SpreadPt;
            double blur = shadow.BlurPt > 0 ? shadow.BlurPt : 0;
            var (r, g, b) = ParseColorD(shadow.Color);

            ctx.Page.SaveState();
            ApplyFillAlpha(ctx, alpha);
            if (blur <= 0)
            {
                // Clipped to the padding box, then to all but the hole, and the padding box filled.
                AppendBoxPath(ctx, x, bottom, w, h, pad);
                ctx.Page.ClipNonzero();
                double hw = w - 2 * spread, hh = h - 2 * spread;
                if (hw > 0 && hh > 0)
                {
                    ctx.Page.Rectangle(x - 1, bottom - 1, w + 2, h + 2);
                    AppendBoxPath(ctx, x + spread + shadow.OffsetXPt, bottom + spread - shadow.OffsetYPt, hw, hh, pad.Grown(-spread));
                    ctx.Page.ClipEvenOdd();
                }
                ctx.Page.SetFillColorRgb((float)r, (float)g, (float)b);
                AppendBoxPath(ctx, x, bottom, w, h, pad);
                ctx.Page.Fill();
            }
            else
            {
                string image = InsetShadowImage(ctx, shadow, w, h, pad, blur, r, g, b);
                ctx.Page.DrawImage(image, x, bottom, w, h);
            }
            ctx.Page.RestoreState();
        }

        /// <summary>
        /// The image of a blurred inset shadow over the padding box: the colour under a mask that is
        /// the padding box's coverage times what the blurred hole leaves uncovered. The hole is
        /// rasterised on a canvas one blur radius larger each way, so its blurred edge is whole where
        /// the padding box cuts it.
        /// </summary>
        private static string InsetShadowImage(Ctx ctx, BoxShadow shadow, double w, double h, Corners pad, double blur, double r, double g, double b)
        {
            double fullW = w + 2 * blur, fullH = h + 2 * blur;
            double res = Math.Min(ShadowMaskResolution, Math.Sqrt(MaxShadowMaskPixels / (fullW * fullH)));
            res = Math.Min(res, MaxBlurSigmaPixels / (blur / 2));
            int ew = Math.Max(1, SaturatingCast.ToInt(Math.Floor(fullW * res))), eh = Math.Max(1, SaturatingCast.ToInt(Math.Floor(fullH * res)));
            int pw = Math.Max(1, SaturatingCast.ToInt(Math.Floor(w * res))), ph = Math.Max(1, SaturatingCast.ToInt(Math.Floor(h * res)));
            int margin = SaturatingCast.ToInt(Math.Round(blur * res));

            // The hole, on the larger canvas: CSS's offset is down for a positive y, as the raster's rows are.
            var hole = new byte[ew * eh];
            double spread = shadow.SpreadPt;
            double hw = w - 2 * spread, hh = h - 2 * spread;
            if (hw > 0 && hh > 0)
                RasteriseBox(hole, ew, eh, (blur + spread + shadow.OffsetXPt) * res, (blur + spread + shadow.OffsetYPt) * res,
                             hw * res, hh * res, pad.Grown(-spread).Scaled(res));
            GaussianBlur(hole, ew, eh, blur / 2 * res);

            var mask = new byte[pw * ph];
            RasteriseBox(mask, pw, ph, 0, 0, w * res, h * res, pad.Scaled(res));
            for (int py = 0; py < ph; py++)
                for (int px = 0; px < pw; px++)
                {
                    int ex = Math.Min(ew - 1, px + margin), ey = Math.Min(eh - 1, py + margin);
                    mask[py * pw + px] = (byte)(mask[py * pw + px] * (255 - hole[ey * ew + ex]) / 255);
                }

            byte cr = SaturatingCast.ToByte(Math.Round(r * 255)), cg = SaturatingCast.ToByte(Math.Round(g * 255)), cb = SaturatingCast.ToByte(Math.Round(b * 255));
            var rgb = new byte[pw * ph * 3];
            for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = cr; rgb[i + 1] = cg; rgb[i + 2] = cb; }
            return ctx.Page.AddImage(rgb, pw, ph, null, PdfColorSpaceNames.DeviceRGB, 8, null, mask);
        }

        /// <summary>
        /// Draws <paramref name="shadow"/> behind a box whose bottom-left corner is
        /// (<paramref name="x"/>, <paramref name="bottom"/>), <paramref name="w"/> × <paramref name="h"/>,
        /// with corners of <paramref name="radius"/>: the box grown by the spread on every side, moved by
        /// the offset (CSS states a positive Y as downward; PDF counts up), at <paramref name="alpha"/>.
        /// </summary>
        private static void DrawShadow(Ctx ctx, BoxShadow shadow, double x, double bottom, double w, double h, Corners radius, double alpha)
        {
            double spread = shadow.SpreadPt;
            x -= spread; bottom -= spread; w += 2 * spread; h += 2 * spread;
            if (w <= 0 || h <= 0) return;                 // a negative spread past the box: nothing to cast
            // A rounded corner grows with the box; a sharp one stays sharp (§7.1).
            radius = radius.Grown(spread);
            double blur = shadow.BlurPt > 0 ? shadow.BlurPt : 0;
            double sx = x + shadow.OffsetXPt, sy = bottom - shadow.OffsetYPt;

            var (r, g, b) = ParseColorD(shadow.Color);
            ctx.Page.SaveState();
            ApplyFillAlpha(ctx, alpha);
            if (blur <= 0)
            {
                ctx.Page.SetFillColorRgb((float)r, (float)g, (float)b);
                AppendBoxPath(ctx, sx, sy, w, h, radius);
                ctx.Page.Fill();
            }
            else
            {
                string image = BlurredShadowImage(ctx, w, h, radius, blur, r, g, b);
                ctx.Page.DrawImage(image, sx - blur, sy - blur, w + 2 * blur, h + 2 * blur);
            }
            ctx.Page.RestoreState();
        }

        /// <summary>
        /// The image of a blurred shadow: the colour under a soft mask that is the box's coverage,
        /// with its corners, through a Gaussian of half the blur radius; the image reaches one blur
        /// radius past the box on every side. Registered on the page and named.
        /// </summary>
        private static string BlurredShadowImage(Ctx ctx, double w, double h, Corners radius, double blur, double r, double g, double b)
        {
            double fullW = w + 2 * blur, fullH = h + 2 * blur;
            // The resolution: two pixels a point, lowered so the mask stays under the pixel cap and
            // the Gaussian under its widest kernel. Floored sizes keep the product under the cap.
            double res = Math.Min(ShadowMaskResolution, Math.Sqrt(MaxShadowMaskPixels / (fullW * fullH)));
            res = Math.Min(res, MaxBlurSigmaPixels / (blur / 2));
            int pw = Math.Max(1, SaturatingCast.ToInt(Math.Floor(fullW * res))), ph = Math.Max(1, SaturatingCast.ToInt(Math.Floor(fullH * res)));

            var mask = new byte[pw * ph];
            RasteriseBox(mask, pw, ph, blur * res, blur * res, w * res, h * res, radius.Scaled(res));
            GaussianBlur(mask, pw, ph, blur / 2 * res);

            // The hex colour's channels are eighths of 255 already, so the byte comes back exact.
            byte cr = SaturatingCast.ToByte(Math.Round(r * 255)), cg = SaturatingCast.ToByte(Math.Round(g * 255)), cb = SaturatingCast.ToByte(Math.Round(b * 255));
            var rgb = new byte[pw * ph * 3];
            for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = cr; rgb[i + 1] = cg; rgb[i + 2] = cb; }
            return ctx.Page.AddImage(rgb, pw, ph, null, PdfColorSpaceNames.DeviceRGB, 8, null, mask);
        }

        /// <summary>
        /// Full coverage (255) at every pixel whose centre is inside the rounded box at
        /// (<paramref name="ox"/>, <paramref name="oy"/>), <paramref name="bw"/> × <paramref name="bh"/>
        /// pixels with each corner rounded by its own radii of <paramref name="rad"/> — row 0 is the
        /// image's top, so the top corners are the first rows'; the blur softens the edge, so no
        /// anti-aliasing. The radii come clamped to half the shorter side (<see cref="Corners.Of"/>,
        /// and a spread grows a radius and the box alike), so none is clamped here.
        /// </summary>
        private static void RasteriseBox(byte[] mask, int pw, int ph, double ox, double oy, double bw, double bh, Corners rad)
        {
            for (int py = 0; py < ph; py++)
            {
                double cy = py + 0.5 - oy;
                if (cy < 0 || cy > bh) continue;
                bool top = cy < bh / 2;
                for (int px = 0; px < pw; px++)
                {
                    double cx = px + 0.5 - ox;
                    if (cx < 0 || cx > bw) continue;
                    bool left = cx < bw / 2;
                    double rx = top ? (left ? rad.TopLeft : rad.TopRight) : (left ? rad.BottomLeft : rad.BottomRight);
                    double ry = top ? (left ? rad.TopLeftY : rad.TopRightY) : (left ? rad.BottomLeftY : rad.BottomRightY);
                    if (rx > 0)
                    {
                        // Inside the corner's rectangle but outside its quarter ellipse: not covered.
                        double dx = (left ? rx - cx : cx - (bw - rx)) / rx;
                        double dy = (top ? ry - cy : cy - (bh - ry)) / ry;
                        if (dx > 0 && dy > 0 && dx * dx + dy * dy > 1) continue;
                    }
                    mask[py * pw + px] = 255;
                }
            }
        }

        /// <summary>
        /// A separable Gaussian of deviation <paramref name="sigma"/> pixels over <paramref name="mask"/>,
        /// in place: the kernel reaches three deviations and is normalised, so a flat region keeps its value.
        /// </summary>
        private static void GaussianBlur(byte[] mask, int pw, int ph, double sigma)
        {
            if (sigma <= 0) return;
            int reach = SaturatingCast.ToInt(Math.Ceiling(3 * sigma));
            var kernel = new double[2 * reach + 1];
            double sum = 0;
            for (int i = -reach; i <= reach; i++) sum += kernel[i + reach] = Math.Exp(-(i * (double)i) / (2 * sigma * sigma));
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

            var pass = new float[pw * ph];
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    double v = 0;
                    for (int k = -reach; k <= reach; k++)
                    {
                        int sxp = x + k;
                        if (sxp >= 0 && sxp < pw) v += kernel[k + reach] * mask[y * pw + sxp];
                    }
                    pass[y * pw + x] = (float)v;
                }
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    double v = 0;
                    for (int k = -reach; k <= reach; k++)
                    {
                        int syp = y + k;
                        if (syp >= 0 && syp < ph) v += kernel[k + reach] * pass[syp * pw + x];
                    }
                    mask[y * pw + x] = SaturatingCast.ToByte(Math.Round(Math.Min(255, Math.Max(0, v))));
                }
        }
    }
}
