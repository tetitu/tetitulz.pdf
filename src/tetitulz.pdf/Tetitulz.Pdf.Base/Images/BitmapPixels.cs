// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Remaps a bitmap's pixels in place, as straight (non-premultiplied) colours.
    /// </summary>
    /// <remarks>
    /// <para><b>This exists so the tree has one fallback, not one per call site.</b>
    /// <see cref="StraightPixelLayout"/> declines a colour type whose byte offsets it cannot trust,
    /// and every caller then needs the allocating <c>SKBitmap.Pixels</c> path for that answer —
    /// which is a fast path and a slow path that agree, in nine places. <b>An optimisation whose
    /// two paths agree cannot be pinned by asserting the answer</b>, so nine of them would need
    /// nine liveness probes; one of them needs <see cref="BufferWalks"/> and
    /// <see cref="PropertyFallbacks"/> — which promptly found the whole SVG half of the
    /// conversion falling back on every image, because the layout did not yet handle the pixel
    /// formats that export produces.</para>
    ///
    /// <para><b>The delegate is not free and is still the right trade.</b> One call per pixel
    /// against a full-size <see cref="SKColor"/> array plus two conversions of every pixel: a
    /// 2000×2000 image allocates 16 MB on the route this replaces, to change however few pixels
    /// the caller cares about. Where the per-pixel work is genuinely trivial and the bitmap is
    /// genuinely huge, write the row loop by hand against <see cref="StraightPixelLayout"/> — the
    /// soft-mask luminosity pass does, and says why.</para>
    /// </remarks>
    internal static class BitmapPixels
    {
        /// <summary>Maps one pixel's straight colour to its replacement.</summary>
        internal delegate SKColor Remap(SKColor straight);

        /// <summary>Maps a target pixel against the matching pixel of a second bitmap.</summary>
        internal delegate SKColor Combine(SKColor target, SKColor other);

        /// <summary>Whether the pixel at these coordinates is to be cleared.</summary>
        internal delegate bool ClearsAt(int x, int y);

        /// <summary>
        /// Produces row <paramref name="y"/> of a bitmap being filled, as straight colour.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="row"/> is exactly the bitmap's width, arrives <b>cleared</b>, and
        /// must not escape the call. Write-only in practice: a producer that wants to read its
        /// destination wants <see cref="Remap"/>, which is a different operation.</para>
        /// <para>A row rather than a pixel because a per-pixel delegate is 8.4 million
        /// non-inlinable calls on a page-sized image — see <see cref="Fill(SKBitmap, FillRow)"/>.</para>
        /// </remarks>
        internal delegate void FillRow(Span<SKColor> row, int y);

        /// <summary>
        /// Reads row <paramref name="y"/> of a bitmap as straight colour. <paramref name="row"/> is
        /// exactly the bitmap's width and must not escape the call.
        /// </summary>
        internal delegate void ReadRow(ReadOnlySpan<SKColor> row, int y);

        /// <summary>Produces row <paramref name="y"/> of two same-sized bitmaps from one pass.</summary>
        internal delegate void FillRows(Span<SKColor> first, Span<SKColor> second, int y);

        /// <summary>
        /// Maps a target pixel against the pixel at the same <b>coordinates</b> of a second bitmap.
        /// </summary>
        internal delegate SKColor Blend(SKColor target, SKColor other, int x, int y);

        /// <summary>How many calls have walked a bitmap's own buffer on this thread.</summary>
        /// <remarks>
        /// <b>A liveness probe on the precedent of <c>CMapCodec.UsesCidRangeIndex</c>.</b> The two
        /// paths produce the same pixels by construction, so nothing asserting the output can tell
        /// whether the buffer walk ran at all — deleting it would leave every rendering test
        /// green and quietly restore the allocation this type exists to remove.
        ///
        /// <para><b>A count rather than a flag, because a render makes many of these calls.</b> A
        /// "did the last one" flag can only be read by a test that knows which call was last, which
        /// a render-level assertion does not; a pair of counts lets one say "several, and none of
        /// them fell back" — which is the claim worth making.</para>
        ///
        /// <para>Per-thread, because batch rendering is parallel across pages and a shared counter
        /// would report another page's work.</para>
        /// </remarks>
        [ThreadStatic]
        internal static int BufferWalks;

        /// <summary>
        /// How many calls have taken the allocating <c>SKBitmap.Pixels</c> path on this thread.
        /// </summary>
        /// <remarks>
        /// Expected to stay zero for every surface these renderers create. It is counted rather
        /// than assumed because "unreachable today" is a claim about the present, and this is what
        /// would notice it stopping being true.
        /// </remarks>
        [ThreadStatic]
        internal static int PropertyFallbacks;

        /// <summary>Zeroes both counters on the calling thread.</summary>
        internal static void ResetCounters()
        {
            BufferWalks = 0;
            PropertyFallbacks = 0;
        }

        /// <summary>
        /// Clears to transparent every pixel <paramref name="clears"/> names, leaving the rest
        /// untouched.
        /// </summary>
        /// <remarks>
        /// <b>This one never reads a pixel, so it is exact.</b> A stencil or colour-key mask writes
        /// the few positions it names and leaves everything else alone — where the
        /// <c>SKBitmap.Pixels</c> route read, converted, and re-converted <i>every</i> pixel of the
        /// image to do it. Because nothing is read, the one-part-in-255 residue
        /// <see cref="StraightPixelLayout"/> documents cannot arise here at all.
        /// </remarks>
        internal static void Clear(SKBitmap bitmap, ClearsAt clears)
        {
            if (bitmap == null || clears == null) return;

            if (StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout)
                && layout.CanStore)
            {
                BufferWalks++;
                Span<byte> buffer = bitmap.GetPixelSpan();
                var transparent = new SKColor(0, 0, 0, 0);
                for (int y = 0; y < layout.Height; y++)
                {
                    Span<byte> row = layout.Row(buffer, y);
                    for (int x = 0; x < layout.Width; x++)
                        if (clears(x, y)) layout.Set(row, x, transparent);
                }
                bitmap.NotifyPixelsChanged();
                return;
            }

            PropertyFallbacks++;
            SKColor[] pixels = bitmap.Pixels;
            if (pixels == null) return;
            int width = bitmap.Width;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    if (i < pixels.Length && clears(x, y)) pixels[i] = new SKColor(0, 0, 0, 0);
                }
            bitmap.Pixels = pixels;
        }

        /// <summary>
        /// Maps the pixels of <paramref name="target"/> inside <paramref name="region"/> against
        /// the pixels of <paramref name="other"/> at the same coordinates.
        /// </summary>
        /// <remarks>
        /// <para><b>Coordinates, not indexes, and that is why this is a separate method rather
        /// than an argument to the one above.</b> The two bitmaps may be different sizes and the
        /// caller means "the same place on the page" — the overprint compositor's coverage
        /// surface is the page bitmap's size and its target need not be. Pairing by position in
        /// the buffer would be a different operation wearing the same name.</para>
        ///
        /// <para><b>The region is the win here.</b> Overprint touches the bounding box of one
        /// path; the <c>SKBitmap.Pixels</c> route read and re-converted the whole page surface to
        /// do it, twice, and skipped only the write-back when nothing was covered.</para>
        ///
        /// <para>Every pixel in the region is written back, including one the blend returns
        /// unchanged. That is exact rather than merely close: a read followed by a store is the
        /// identity, which <c>StraightPixelLayoutTests</c> pins over every colour and alpha.</para>
        /// </remarks>
        internal static void MapRegion(
            SKBitmap target, SKBitmap other, SKRectI region, Blend blend)
        {
            if (target == null || other == null || blend == null) return;

            int x0 = Math.Max(0, region.Left);
            int y0 = Math.Max(0, region.Top);
            int x1 = Math.Min(Math.Min(target.Width, other.Width), region.Right);
            int y1 = Math.Min(Math.Min(target.Height, other.Height), region.Bottom);
            if (x1 <= x0 || y1 <= y0) return;

            if (StraightPixelLayout.TryRead(target, out StraightPixelLayout tl) && tl.CanStore &&
                StraightPixelLayout.TryRead(other, out StraightPixelLayout ol))
            {
                BufferWalks++;
                Span<byte> tb = target.GetPixelSpan();
                Span<byte> ob = other.GetPixelSpan();

                for (int y = y0; y < y1; y++)
                {
                    Span<byte> trow = tl.Row(tb, y);
                    Span<byte> orow = ol.Row(ob, y);
                    for (int x = x0; x < x1; x++)
                        tl.Set(trow, x, blend(tl.Get(trow, x), ol.Get(orow, x), x, y));
                }
                target.NotifyPixelsChanged();
                return;
            }

            PropertyFallbacks++;
            SKColor[] tp = target.Pixels;
            SKColor[] op = other.Pixels;
            if (tp == null || op == null) return;

            int tw = target.Width, ow = other.Width;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int ti = (y * tw) + x, oi = (y * ow) + x;
                    if (ti >= tp.Length || oi >= op.Length) continue;
                    tp[ti] = blend(tp[ti], op[oi], x, y);
                }
            target.Pixels = tp;
        }

        /// <summary>
        /// Hands every row of <paramref name="bitmap"/> to <paramref name="read"/> as straight colour,
        /// leaving the bitmap untouched.
        /// </summary>
        /// <remarks>
        /// <para>Through the bitmap's own buffer wherever <see cref="StraightPixelLayout"/> can read
        /// it — writing is not needed, so a <c>Gray8</c> surface qualifies too — and otherwise through
        /// <c>GetPixel</c>, one pixel at a time: slow, but allocating nothing, where
        /// <c>SKBitmap.Pixels</c> would copy the whole image to read it once.</para>
        /// <para><b>The buffer read is not quite <c>GetPixel</c></b> on a pixel that is neither
        /// opaque nor invisible: it un-premultiplies through Skia's integer table, where
        /// <c>GetPixel</c> rounds in float, so the two differ by at most one level on 621 of the
        /// 65 536 (colour, alpha) pairs (<see cref="StraightPixelLayout"/> states and pins it). On
        /// an opaque surface they are identical.</para>
        /// </remarks>
        internal static void Read(SKBitmap bitmap, ReadRow read)
        {
            if (bitmap == null || read == null) return;

            int width = bitmap.Width, height = bitmap.Height;
            if (width <= 0 || height <= 0) return;

            var scratch = new SKColor[width];

            if (StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout))
            {
                BufferWalks++;
                ReadOnlySpan<byte> buffer = bitmap.GetPixelSpan();
                int rows = Math.Min(height, layout.Height);
                int cols = Math.Min(width, layout.Width);
                for (int y = 0; y < rows; y++)
                {
                    ReadOnlySpan<byte> row = buffer.Slice(y * layout.Stride, cols * layout.BytesPerPixel);
                    for (int x = 0; x < cols; x++) scratch[x] = layout.Get(row, x);
                    read(scratch, y);
                }
                return;
            }

            PropertyFallbacks++;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) scratch[x] = bitmap.GetPixel(x, y);
                read(scratch, y);
            }
        }

        /// <summary>
        /// Writes every pixel of <paramref name="bitmap"/> from <paramref name="fill"/>, one row at
        /// a time, without allocating an image-sized colour array.
        /// </summary>
        /// <remarks>
        /// <para><b>A fill is not a remap, which is why this could not reuse
        /// <see cref="Map(SKBitmap, Remap)"/>.</b>
        /// <see cref="Remap"/> hands back the pixel that is already there. A decoder computing pixel
        /// <i>i</i> from an external sample array has nothing to hand back and needs a
        /// <i>position</i> — so roughly twenty-five decode sites each built a full
        /// <c>SKColor[w*h]</c> and assigned <c>SKBitmap.Pixels</c>, paying the setter's whole-image
        /// walk and a <c>w*h*4</c> allocation.</para>
        ///
        /// <para><b>Measured.</b> Filling a page-sized 2550×3300 image, warmed, three runs: the
        /// colour-array route takes 96.8–106.7 ms and a byte route 62.7–65.6.
        /// About 1.6×, and <b>the win is mostly the allocation rather than the loop</b> — with both
        /// buffers reused the gap falls to ~1.1×. The 32 MB spike is the part that matters beyond
        /// the clock, because it undercuts the banded renderer's bounded-peak-memory guarantee.</para>
        ///
        /// <para><b>A row, not a pixel, and that is the whole design.</b> The obvious shape is
        /// <c>SKColor F(int x, int y)</c>, which costs one non-inlinable indirect call per pixel:
        /// 8.4 million of them on that fixture, 13–21 ms, a third of the win handed straight back.
        /// One call per row is 3 300, which is microseconds, and it leaves each caller's inner loop
        /// as the straight-line code the JIT already optimises.</para>
        ///
        /// <para><b>The row arrives cleared, and that is load-bearing rather than tidy.</b> Several
        /// callers <c>break</c> out of a fill early — a truncated sample buffer, a function that
        /// gives up mid-mesh — and relied on the unwritten tail of a fresh <c>SKColor[]</c> being
        /// transparent black. The scratch row is reused across rows, so without the clear row
        /// <i>n+1</i> would inherit row <i>n</i>'s colours wherever the producer stopped short.
        /// (A freshly allocated bitmap's own buffer <i>is</i> zeroed — measured for Rgba8888,
        /// Bgra8888, Rgb888x and Gray8 — so it is only the reuse that needs this.)</para>
        ///
        /// <para>Both paths write through the same scratch row, so the fallback stays byte-identical
        /// to the fast path by construction rather than by argument.</para>
        ///
        /// <para><b>The staging row costs a second touch of every pixel, and that was the live
        /// objection to this shape.</b> A write-through lend — handing the caller something that
        /// stores into the bitmap's buffer directly — touches each pixel once. It was rejected on
        /// two grounds. The row is one row wide, about 10 KB for a page-sized image, so the second
        /// touch is L1-resident and the win being chased is the 32 MB allocation rather than the
        /// copy. And a write-through lend puts <see cref="StraightPixelLayout"/> in front of every
        /// call site, which is what this type's own opening remarks exist to refuse: one fallback
        /// in the tree, not one per caller.</para>
        /// </remarks>
        internal static void Fill(SKBitmap bitmap, FillRow fill)
        {
            if (bitmap == null || fill == null) return;

            int width = bitmap.Width, height = bitmap.Height;
            if (width <= 0 || height <= 0) return;

            var scratch = new SKColor[width];

            if (StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout)
                && layout.CanStore)
            {
                BufferWalks++;
                Span<byte> buffer = bitmap.GetPixelSpan();
                int rows = Math.Min(height, layout.Height);
                int cols = Math.Min(width, layout.Width);
                for (int y = 0; y < rows; y++)
                {
                    Array.Clear(scratch, 0, width);
                    fill(scratch, y);
                    Span<byte> row = layout.Row(buffer, y);
                    for (int x = 0; x < cols; x++) layout.Set(row, x, scratch[x]);
                }
                bitmap.NotifyPixelsChanged();
                return;
            }

            // The same fallback the rest of this type keeps, for a pixel format the layout cannot
            // address or can read but not write. Counted, not assumed: an earlier layout handled
            // only the two 8888 orders and every SVG image fell through here with identical pixels.
            PropertyFallbacks++;
            SKColor[] pixels = bitmap.Pixels;
            if (pixels == null) return;
            for (int y = 0; y < height; y++)
            {
                Array.Clear(scratch, 0, width);
                fill(scratch, y);
                int start = y * width;
                int n = Math.Min(width, pixels.Length - start);
                for (int x = 0; x < n; x++) pixels[start + x] = scratch[x];
            }
            bitmap.Pixels = pixels;
        }

        /// <summary>
        /// Writes every pixel of <b>two</b> same-sized bitmaps from one pass, for a caller whose
        /// single loop over the source produces both.
        /// </summary>
        /// <remarks>
        /// <b>Not a convenience.</b> The CMYK overprint path fills a CMY raster and a K raster from
        /// one walk of the samples, and splitting that into two <see cref="Fill(SKBitmap, FillRow)"/> calls would
        /// decode every sample twice. Converting one of the pair and not the other would leave a
        /// buffer walk and an allocating store inside a single loop body, which is worse than
        /// converting neither. The two rows are the same width and both arrive cleared.
        /// </remarks>
        internal static void Fill(SKBitmap first, SKBitmap second, FillRows fill)
        {
            if (first == null || second == null || fill == null) return;
            if (first.Width != second.Width || first.Height != second.Height) return;

            int width = first.Width, height = first.Height;
            if (width <= 0 || height <= 0) return;

            var a = new SKColor[width];
            var b = new SKColor[width];

            bool fastA = StraightPixelLayout.TryRead(first, out StraightPixelLayout la) && la.CanStore;
            bool fastB = StraightPixelLayout.TryRead(second, out StraightPixelLayout lb) && lb.CanStore;

            // Both or neither: a mixed pair would put a buffer walk and an allocating store in one
            // loop body, which is the shape this method exists to prevent.
            if (fastA && fastB)
            {
                BufferWalks++;
                Span<byte> bufA = first.GetPixelSpan();
                Span<byte> bufB = second.GetPixelSpan();
                for (int y = 0; y < height; y++)
                {
                    Array.Clear(a, 0, width);
                    Array.Clear(b, 0, width);
                    fill(a, b, y);
                    Span<byte> rowA = la.Row(bufA, y);
                    Span<byte> rowB = lb.Row(bufB, y);
                    for (int x = 0; x < width; x++)
                    {
                        la.Set(rowA, x, a[x]);
                        lb.Set(rowB, x, b[x]);
                    }
                }
                first.NotifyPixelsChanged();
                second.NotifyPixelsChanged();
                return;
            }

            PropertyFallbacks++;
            SKColor[] pa = first.Pixels, pb = second.Pixels;
            if (pa == null || pb == null) return;
            for (int y = 0; y < height; y++)
            {
                Array.Clear(a, 0, width);
                Array.Clear(b, 0, width);
                fill(a, b, y);
                int start = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = start + x;
                    if (i < pa.Length) pa[i] = a[x];
                    if (i < pb.Length) pb[i] = b[x];
                }
            }
            first.Pixels = pa;
            second.Pixels = pb;
        }

        /// <summary>Replaces every pixel of <paramref name="bitmap"/> with <paramref name="remap"/>'s answer.</summary>
        internal static void Map(SKBitmap bitmap, Remap remap)
        {
            if (bitmap == null || remap == null) return;

            if (StraightPixelLayout.TryRead(bitmap, out StraightPixelLayout layout)
                && layout.CanStore)
            {
                BufferWalks++;
                Span<byte> buffer = bitmap.GetPixelSpan();
                for (int y = 0; y < layout.Height; y++)
                {
                    Span<byte> row = layout.Row(buffer, y);
                    for (int x = 0; x < layout.Width; x++)
                        layout.Set(row, x, remap(layout.Get(row, x)));
                }
                bitmap.NotifyPixelsChanged();
                return;
            }

            // A pixel format StraightPixelLayout cannot address, or one it can read but not
            // write: take the allocating path rather than reinterpret the buffer.
            //
            // NOT unreachable. A layout that handled only the two 8888 orders would send every SVG
            // image (Rgb888x, Gray8) through here, with identical pixels and a green suite; the
            // counters say so where a comment cannot.
            PropertyFallbacks++;
            SKColor[] pixels = bitmap.Pixels;
            if (pixels == null) return;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = remap(pixels[i]);
            bitmap.Pixels = pixels;
        }

        /// <summary>
        /// Replaces every pixel of <paramref name="target"/> with <paramref name="combine"/>'s
        /// answer for it and the pixel at the same index of <paramref name="other"/>.
        /// </summary>
        /// <remarks>
        /// <para>Pixels are matched by <b>linear index</b> and the walk stops at the shorter of the
        /// two, which is what the callers already did.</para>
        ///
        /// <para><b>Which is why unequal dimensions take the allocating path.</b> A row walk pairs
        /// by (x, y) and a linear index pairs by position in the buffer, and the two agree only
        /// when the bitmaps are the same size — so a fast path that quietly re-paired them would
        /// be a different answer, not a faster one. It is reachable: the soft-mask caller resizes
        /// its mask to the base image, and <c>SKBitmap.Resize</c> can return null.</para>
        /// </remarks>
        internal static void Map(SKBitmap target, SKBitmap other, Combine combine)
        {
            if (target == null || other == null || combine == null) return;

            if (target.Width == other.Width && target.Height == other.Height &&
                StraightPixelLayout.TryRead(target, out StraightPixelLayout tl) && tl.CanStore &&
                StraightPixelLayout.TryRead(other, out StraightPixelLayout ol))
            {
                BufferWalks++;
                Span<byte> tb = target.GetPixelSpan();
                Span<byte> ob = other.GetPixelSpan();

                for (int y = 0; y < tl.Height; y++)
                {
                    Span<byte> trow = tl.Row(tb, y);
                    Span<byte> orow = ol.Row(ob, y);
                    for (int x = 0; x < tl.Width; x++)
                        tl.Set(trow, x, combine(tl.Get(trow, x), ol.Get(orow, x)));
                }
                target.NotifyPixelsChanged();
                return;
            }

            PropertyFallbacks++;
            SKColor[] tp = target.Pixels;
            SKColor[] op = other.Pixels;
            if (tp == null || op == null) return;

            int n = Math.Min(tp.Length, op.Length);
            for (int i = 0; i < n; i++) tp[i] = combine(tp[i], op[i]);
            target.Pixels = tp;
        }
    }
}
