// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Per-pixel coverage maps for a page's <c>/Separation</c> and <c>/DeviceN</c> (spot) inks,
    /// for print preview and ink-cost estimation. Filled by
    /// <c>PdfPageRenderer.ExtractInkCoverage</c>.
    ///
    /// <list type="bullet">
    ///   <item>Each channel is identified by its colorant name (e.g. <c>"SPOT 185 C"</c>).</item>
    ///   <item>A channel exists as soon as a colour is <i>set</i> in a space naming that colorant,
    ///   whether or not anything is then painted with it — so an all-zero map is a real answer
    ///   ("selected, never used") and not a gap.</item>
    ///   <item>Per-pixel coverage is one byte [0..255], carrying <b>tint × area coverage</b>: a
    ///   40% tint over a fully covered pixel and a full tint over a 40%-covered edge pixel both
    ///   read 102, which is the quantity an ink estimate sums.</item>
    ///   <item>Deposits combine by <b>maximum</b>, not by addition. Two overlapping 50% paints of
    ///   one ink read 50%, not 100%: the model is what the plate carries, and re-inking a covered
    ///   area does not lay down twice the ink.</item>
    /// </list>
    ///
    /// <para><b>What is measured, and what is not.</b> Path fills, path strokes (under the real pen
    /// width, §8.4.3.2) and filled text are rasterised through the same coverage rasteriser the
    /// overprint compositor uses, honouring the clip. <b>Images are not</b> — a
    /// <c>/Separation</c>-space image's tints vary per sample, and the samples are gone by paint
    /// time — and neither is the stroking half of a text render mode that also strokes, nor a
    /// pattern paint. All three are reported through
    /// <see cref="RenderDiagnosticKind.InkCoverageIncomplete"/> rather than left to read as zero
    /// ink.</para>
    ///
    /// <para><b>One case over-reports instead, and is deliberately silent.</b> A paint under a soft
    /// mask or a constant alpha below 1 is recorded at its <i>specified</i> tint, not at what
    /// survives compositing — the right answer for "how much ink did the file ask for on this
    /// plate" and the wrong one for "how much reaches the paper". It carries no diagnostic because,
    /// unlike the three above, the region genuinely is measured, and a channel that fired on every
    /// transparent spot paint is one a caller learns to filter out.</para>
    /// </summary>
    public sealed class InkAccumulator
    {
        private readonly int _width;
        private readonly int _height;
        private readonly Dictionary<string, byte[]> _channels
            = new Dictionary<string, byte[]>(System.StringComparer.Ordinal);

        public int Width  => _width;
        public int Height => _height;
        public IReadOnlyDictionary<string, byte[]> Channels => _channels;

        /// <summary>
        /// A map of <paramref name="width"/> × <paramref name="height"/> cells per channel. Either
        /// may be 0 (every deposit then falls outside); neither may be negative, and the plane must
        /// fit one array — <c>width * height</c> multiplied unchecked, 70 000 × 70 000 would wrap to a
        /// 605 MB plane and the first deposit near the far corner index past it.
        /// </summary>
        /// <param name="width">Cells across; at least 0.</param>
        /// <param name="height">Cells down; at least 0.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// A dimension is negative, or the plane would exceed <see cref="int.MaxValue"/> cells.
        /// </exception>
        public InkAccumulator(int width, int height)
        {
            if (width  < 0) throw new System.ArgumentOutOfRangeException(nameof(width),  width,  "Width must be at least 0.");
            if (height < 0) throw new System.ArgumentOutOfRangeException(nameof(height), height, "Height must be at least 0.");
            long plane = (long)width * height;
            if (plane > int.MaxValue)
                throw new System.ArgumentOutOfRangeException(nameof(width), plane,
                    $"{width}x{height} cells do not fit one coverage plane (at most {int.MaxValue}).");
            _width  = width;
            _height = height;
            _plane  = (int)plane;
        }

        private readonly int _plane;

        /// <summary>Returns the coverage map for a given channel, allocating one on first use.</summary>
        public byte[] GetOrCreateChannel(string colorantName)
        {
            if (!_channels.TryGetValue(colorantName, out var ch))
            {
                ch = new byte[_plane];
                _channels[colorantName] = ch;
            }
            return ch;
        }

        /// <summary>Clears all ink data.  Called when a new transparency group starts.</summary>
        public void Reset() { _channels.Clear(); }

        /// <summary>
        /// Deposits <paramref name="tint"/> (0-255) into the pixel at (x, y) for the given channel.
        /// </summary>
        public void Deposit(string colorantName, int x, int y, byte tint)
        {
            if ((uint)x >= (uint)_width || (uint)y >= (uint)_height) return;
            var ch = GetOrCreateChannel(colorantName);
            int idx = y * _width + x;
            ch[idx] = Combine(ch[idx], tint);
        }

        /// <summary>
        /// The <b>one</b> rule by which two deposits of the same ink at the same pixel combine:
        /// the greater tint wins.
        /// </summary>
        /// <remarks>
        /// <para>Max-coverage rather than addition, because the model is what the plate carries:
        /// painting over an already-inked area does not lay down twice the ink. Real lay-down
        /// simulation would be additive with screen modulation, and this is the approximation that
        /// keeps a spot colour's identity through a stack of overlapping paints.</para>
        /// <para><b>It is a method rather than one line inside <see cref="Deposit"/> because there
        /// are two callers.</b> The render path folds a whole rasterised coverage window in at
        /// once — it resolves the channel array and the row stride once instead of per pixel — so
        /// it cannot go through <see cref="Deposit"/>, and for a while it carried its own copy of
        /// this comparison. That is two places for the model to change in, and a mutation flipping
        /// one of them left the other, and every render, untouched.</para>
        /// </remarks>
        internal static byte Combine(byte existing, byte deposited)
            => deposited > existing ? deposited : existing;
    }
}
