// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The ordinary render's destination (<see cref="PdfRenderOptions.DestinationProfile"/>): the
    /// finished sRGB surface converted into an RGB or grey ICC profile's device values — a display's, a
    /// printer's RGB, a grey device's — as a colour-managed viewer or a CMM converts an sRGB image for it.
    /// </summary>
    /// <remarks>
    /// <para><b>Which side of the profile.</b> A profile with a <c>B2A</c> table for the relative colorimetric
    /// intent — a printer's, a v4 display's, or a device link from RGB — goes through that table
    /// (<see cref="IccOutputTransform"/>, the separations' and the proof's transform, black-point compensation
    /// on unless asked otherwise), sampled once on a 33-node grid and interpolated, as the proof is
    /// (<see cref="SoftProof"/>). A matrix/TRC profile — most display profiles: Display P3, Adobe RGB, a
    /// monitor's own — and a grey <c>kTRC</c> profile are inverted exactly: sRGB linearised, one 3×3 matrix
    /// (the destination's colorants inverted, times sRGB's), and each channel's tone curve inverted at the
    /// 255 boundaries between its output levels, so a pixel's level is the count of boundaries at or below
    /// its linear value — the usual treatment of a pair of matrix-shaper profiles. A colour outside the
    /// destination's gamut clips per channel.</para>
    /// <para><b>The working space stays sRGB.</b> Every colour reaches the surface as sRGB and is converted at
    /// the end, so a destination wider than sRGB holds sRGB's gamut, not the document's: an Adobe RGB image
    /// rendered for an Adobe RGB display has been through sRGB on the way.</para>
    /// <para>A profile this cannot convert into — not a profile, a CMYK one (separations are that
    /// destination: <see cref="PdfSeparationOptions.OutputIccProfile"/>), a tone curve that is not monotonic,
    /// a singular matrix — reports <see cref="RenderDiagnosticKind.DestinationProfileUnsupported"/> and leaves
    /// the render in sRGB.</para>
    /// </remarks>
    internal sealed class RenderDestination
    {
        private readonly byte[] _profile;
        private readonly bool _grey;

        // Matrix/TRC and grey TRC: linear sRGB → linear destination, then each channel's level boundaries.
        private readonly double[] _matrix;          // 3×3 row-major; for grey, its one row in [0..2]
        private readonly double[][] _boundaries;    // per output channel: 255 ascending linear values
        private static readonly double[] Linear = BuildLinear();

        // A B2A table or a device link, through a grid.
        private readonly IccOutputTransform _table;
        private byte[] _lut;

        private static readonly ConditionalWeakTable<PdfRenderOptions, RenderDestination> Cache = new ConditionalWeakTable<PdfRenderOptions, RenderDestination>();

        private RenderDestination(byte[] profile, bool grey, double[] matrix, double[][] boundaries, IccOutputTransform table)
        {
            _profile = profile;
            _grey = grey;
            _matrix = matrix;
            _boundaries = boundaries;
            _table = table;
        }

        /// <summary>The profile's bytes, as the encoders embed them.</summary>
        internal byte[] Profile => _profile;

        /// <summary>Whether the destination is a grey device: its one channel is written as R = G = B.</summary>
        internal bool IsGrey => _grey;

        /// <summary>Whether the destination is converted through a sampled table rather than exactly.</summary>
        internal bool IsTable => _table != null;

        /// <summary>
        /// The destination <paramref name="profile"/> describes, or null with <paramref name="reason"/>.
        /// </summary>
        internal static RenderDestination TryCreate(byte[] profile, bool blackPointCompensation, out string reason)
        {
            if (!IccProfile.TryReadHeader(profile, out IccProfile.IccHeader header, out reason)) return null;

            IccOutputTransform table = IccOutputTransform.TryCreate(profile, IccRenderingIntent.MediaRelativeColorimetric,
                blackPointCompensation, out string tableReason);
            if (table != null)
            {
                if (table.Channels == 3 || table.Channels == 1)
                {
                    reason = null;
                    return new RenderDestination(profile, table.Channels == 1, null, null, table);
                }
                reason = $"a destination of {table.Channels} channels: a render is converted into an RGB or grey profile"
                         + (table.Channels == 4 ? " (separations take a CMYK one: PdfSeparationOptions.OutputIccProfile)" : "");
                return null;
            }

            IccProfile parsed = IccProfile.Parse(profile);
            double[] srgb = PdfColor.LinearSRgbToXyzD50Matrix();
            switch (parsed.Model)
            {
                case IccModel.MatrixTrc:
                {
                    // XYZ = D · lin, D's columns the colorants: lin = D⁻¹ · S · sRGB-lin.
                    double[] c = parsed.Colorants();
                    var d = new[] { c[0], c[3], c[6], c[1], c[4], c[7], c[2], c[5], c[8] };
                    if (!(Math.Abs(PdfColor.Determinant3x3(d)) > 1e-9))
                    {
                        reason = "its colorant matrix is singular";
                        return null;
                    }
                    double[] matrix = Multiply(PdfColor.Invert3x3(d), srgb);
                    var boundaries = new double[3][];
                    for (int i = 0; i < 3; i++)
                        if ((boundaries[i] = Boundaries(parsed.Curve(i), out reason)) == null) return null;
                    reason = null;
                    return new RenderDestination(profile, false, matrix, boundaries, null);
                }
                case IccModel.GrayTrc:
                {
                    // The grey curve gives Y relative to the PCS white, so the linear value is sRGB's Y.
                    var row = new[] { srgb[3], srgb[4], srgb[5] };
                    double[] boundaries = Boundaries(parsed.Curve(0), out reason);
                    if (boundaries == null) return null;
                    return new RenderDestination(profile, true, row, new[] { boundaries }, null);
                }
                default:
                    reason = header.DataColorSpace == "CMYK"
                        ? "a CMYK profile: a render is converted into an RGB or grey profile (separations take a CMYK one: PdfSeparationOptions.OutputIccProfile)"
                        : $"neither a B2A table this can use ({tableReason}) nor a matrix/TRC or grey curve ({parsed.UnsupportedReason})";
                    return null;
            }
        }

        /// <summary>
        /// The destination <paramref name="options"/> asks for — cached on the options — or null when none is,
        /// or when the profile cannot be used, which is reported.
        /// </summary>
        internal static RenderDestination For(PdfRenderOptions options)
        {
            if (options?.DestinationProfile == null) return null;
            if (Cache.TryGetValue(options, out RenderDestination cached)) return cached;

            RenderDestination destination = TryCreate(options.DestinationProfile, options.DestinationBlackPointCompensation, out string reason);
            if (destination == null)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.DestinationProfileUnsupported,
                    "the page was left in sRGB: " + reason);
                return null;
            }
            try { Cache.Add(options, destination); }
            catch (ArgumentException) { if (Cache.TryGetValue(options, out cached)) return cached; }
            return destination;
        }

        /// <summary>
        /// The destination already built for <paramref name="options"/>, or null — never building or reporting one:
        /// what the encoders ask after the render that built it.
        /// </summary>
        internal static RenderDestination Cached(PdfRenderOptions options)
            => options?.DestinationProfile != null && Cache.TryGetValue(options, out RenderDestination cached) ? cached : null;

        /// <summary>
        /// Converts <paramref name="bitmap"/> in place when <paramref name="options"/> names a destination and
        /// <paramref name="plate"/> is not a separation pass; returns the bitmap either way.
        /// </summary>
        internal static SKBitmap Apply(SKBitmap bitmap, PdfRenderOptions options, SeparationPlate plate = null)
        {
            if (bitmap == null || plate != null) return bitmap;
            For(options)?.Apply(bitmap);
            return bitmap;
        }

        /// <summary>Converts every pixel of <paramref name="bitmap"/>; alpha is kept.</summary>
        internal void Apply(SKBitmap bitmap)
        {
            if (_table != null)
            {
                byte[] lut = _lut ?? (_lut = BuildGrid());
                BitmapPixels.Map(bitmap, straight =>
                {
                    SoftProof.Proof(lut, straight.Red, straight.Green, straight.Blue, out byte r, out byte g, out byte b);
                    return new SKColor(r, g, b, straight.Alpha);
                });
                return;
            }
            BitmapPixels.Map(bitmap, straight => WithAlpha(Exactly(straight), straight.Alpha));
        }

        /// <summary>One colour converted: exactly for a matrix/TRC or grey destination, the table's own evaluation otherwise.</summary>
        internal SKColor Convert(SKColor color)
        {
            if (_table == null) return Exactly(color);
            var device = new float[_table.Channels];
            if (!_table.TryConvert(color, device)) return color;
            return ToColor(device);
        }

        private SKColor Exactly(SKColor color)
        {
            double r = Linear[color.Red], g = Linear[color.Green], b = Linear[color.Blue];
            if (_grey)
            {
                byte k = Level(_boundaries[0], _matrix[0] * r + _matrix[1] * g + _matrix[2] * b);
                return new SKColor(k, k, k);
            }
            return new SKColor(
                Level(_boundaries[0], _matrix[0] * r + _matrix[1] * g + _matrix[2] * b),
                Level(_boundaries[1], _matrix[3] * r + _matrix[4] * g + _matrix[5] * b),
                Level(_boundaries[2], _matrix[6] * r + _matrix[7] * g + _matrix[8] * b));
        }

        private static SKColor WithAlpha(SKColor c, byte alpha) => new SKColor(c.Red, c.Green, c.Blue, alpha);

        private SKColor ToColor(float[] device)
        {
            if (_grey)
            {
                byte k = PdfColor.Channel(device[0]);
                return new SKColor(k, k, k);
            }
            return new SKColor(PdfColor.Channel(device[0]), PdfColor.Channel(device[1]), PdfColor.Channel(device[2]));
        }

        private byte[] BuildGrid()
        {
            int n = SoftProof.Grid;
            var lut = new byte[n * n * n * 3];
            var device = new float[_table.Channels];
            for (int bi = 0; bi < n; bi++)
                for (int gi = 0; gi < n; gi++)
                    for (int ri = 0; ri < n; ri++)
                    {
                        var node = new SKColor(SoftProof.Node(ri), SoftProof.Node(gi), SoftProof.Node(bi));
                        SKColor converted = _table.TryConvert(node, device) ? ToColor(device) : node;
                        int o = ((bi * n + gi) * n + ri) * 3;
                        lut[o] = converted.Red; lut[o + 1] = converted.Green; lut[o + 2] = converted.Blue;
                    }
            return lut;
        }

        /// <summary>
        /// A tone curve's level boundaries: entry <c>j</c> the curve at <c>(j + ½) / 255</c>, the linear value at
        /// and above which the output reaches level <c>j + 1</c>. Null — with the reason — when the curve is
        /// absent or falls anywhere, since a falling curve has no inverse to read.
        /// </summary>
        private static double[] Boundaries(IccCurve curve, out string reason)
        {
            if (curve == null) { reason = "a tone curve is missing"; return null; }
            var b = new double[255];
            for (int j = 0; j < 255; j++)
            {
                b[j] = curve.Evaluate((j + 0.5) / 255);
                if (double.IsNaN(b[j]) || (j > 0 && b[j] < b[j - 1]))
                {
                    reason = "a tone curve that is not monotonic, which has no inverse";
                    return null;
                }
            }
            reason = null;
            return b;
        }

        /// <summary>The output level of a linear value: how many boundaries lie at or below it.</summary>
        private static byte Level(double[] boundaries, double linear)
        {
            if (!(linear >= boundaries[0])) return 0;   // NaN too
            int lo = 0, hi = boundaries.Length;          // boundaries[lo] ≤ linear; find the first above it
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (boundaries[mid] <= linear) lo = mid; else hi = mid;
            }
            return (byte)(lo + 1);
        }

        private static double[] BuildLinear()
        {
            var t = new double[256];
            for (int i = 0; i < 256; i++) t[i] = PdfColor.SRgbLevelToLinear((byte)i);
            return t;
        }

        private static double[] Multiply(double[] a, double[] b)
        {
            var m = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
            return m;
        }
    }
}
