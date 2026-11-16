// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// sRGB into an output profile's device space — the destination half of a colour engine: the profile's <c>B2A</c> table for the rendering intent, with black-point
    /// compensation and white kept white, as a CMM does it.
    /// </summary>
    /// <remarks>
    /// <para><b>Black-point compensation is Adobe's method</b>, and checked
    /// against an independent colour engine rather than taken on trust: rendering through
    /// the same profile with compensation on and off is the oracle in
    /// <c>IccOutputTransformTests</c>. The source black point of sRGB is zero, so the compensation is
    /// a per-channel scale of PCS XYZ that lands zero on the destination's black point and keeps the
    /// D50 white fixed: <c>X′ = X·(1 − BPₓ/D50ₓ) + BPₓ</c>. The destination black point is found by
    /// Adobe's destination black-point detection (see
    /// <see cref="IccBlackPoint.TryDetectDestination"/>).</para>
    ///
    /// <para><b>White is kept white</b> (the white fix-up): the source white goes to no ink,
    /// which a relative-colorimetric table can miss by a few levels on its own (RSWOP's <c>B2A1</c>
    /// puts 2/4/5 % of C/M/Y under sRGB white).</para>
    ///
    /// <para><b>Absolute colorimetric</b> reads <c>B2A1</c> without compensation, with the PCS colour
    /// divided first by the destination's media white over D50 (sRGB's is D50) —
    /// full adaptation — and without the white fix-up, which is off for it: the
    /// paper is darker than sRGB white, so white prints ink. An independent separation render through RSWOP
    /// is the oracle (<c>IccAbsoluteColorimetricTests</c>).</para>
    ///
    /// <para><b>A device link from RGB is a destination too</b> (class <c>link</c>, ICC.1 §8.6): its
    /// <c>A2B0</c> maps the colour's components straight to the output's, as its header names them
    /// (the PCS field of a link holds the output space). It is the link's own rendering, made when it
    /// was built, so the intent asked for, black-point compensation and the white fix-up do not apply
    /// to it — the caller who supplies a link supplies the whole conversion. A second independent CMM is the oracle (<c>IccDeviceLinkTests</c>).</para>
    /// </remarks>
    internal sealed class IccOutputTransform
    {
        /// <summary>ICC.1's PCS illuminant, D50 (the compensation's fixed point).</summary>
        private const double D50X = 0.9642, D50Y = 1.0, D50Z = 0.8249;

        private readonly IccLut _bToA;
        private readonly int _channels;
        private readonly double _bpX, _bpY, _bpZ;

        /// <summary>Absolute colorimetric's destination media white over D50, divided out of the PCS colour; null for any other intent or a white at D50.</summary>
        private readonly double[] _mediaWhite;

        /// <summary>Whether <see cref="_bToA"/> is a device link's <c>A2B0</c>, taking the RGB components themselves.</summary>
        private readonly bool _deviceLink;

        private IccOutputTransform(IccLut bToA, int channels, double bpX, double bpY, double bpZ, double[] mediaWhite,
                                   bool deviceLink = false)
        {
            _deviceLink = deviceLink;
            _bToA = bToA;
            _channels = channels;
            _bpX = bpX; _bpY = bpY; _bpZ = bpZ;
            _mediaWhite = mediaWhite;
        }

        /// <summary>The device channels a conversion produces.</summary>
        internal int Channels => _channels;

        /// <summary>The destination black point the compensation lands on, in D50 XYZ; zero without compensation.</summary>
        internal (double X, double Y, double Z) BlackPoint => (_bpX, _bpY, _bpZ);

        /// <summary>
        /// The transform into <paramref name="profile"/> for <paramref name="intent"/>, or null and a
        /// reason when the profile cannot be a destination.
        /// </summary>
        internal static IccOutputTransform TryCreate(byte[] profile, IccRenderingIntent intent,
            bool blackPointCompensation, out string reason)
        {
            if (IccProfile.TryReadHeader(profile, out IccProfile.IccHeader header, out _) && header.DeviceClass == "link")
                return TryCreateLink(profile, header, out reason);

            IccLut bToA = IccProfile.ParseB2A(profile, out int channels, out reason, intent);
            if (bToA == null) return null;

            double x = 0, y = 0, z = 0;
            if (blackPointCompensation && intent != IccRenderingIntent.AbsoluteColorimetric
                && IccBlackPoint.TryDetectDestination(profile, intent, out double l))
                PdfColor.LabToXyzD50(l, 0, 0, out x, out y, out z);

            bool absolute = IccRenderingIntents.Table(intent) == IccRenderingIntent.AbsoluteColorimetric;
            reason = null;
            return new IccOutputTransform(bToA, channels, x, y, z,
                absolute ? IccProfile.MediaWhiteScaleOf(profile) ?? new[] { 1.0, 1.0, 1.0 } : null);
        }

        /// <summary>
        /// The transform a device link from RGB makes, or null and a reason: one that takes another
        /// space, produces one this does not convert into, or whose table disagrees with its header.
        /// </summary>
        private static IccOutputTransform TryCreateLink(byte[] profile, IccProfile.IccHeader header, out string reason)
        {
            if (header.DataColorSpace != "RGB ")
            {
                reason = $"device link from '{header.DataColorSpace.Trim()}': the colour converted is sRGB, so a link must take RGB";
                return null;
            }
            int channels = IccProfile.ChannelsOf(header.Pcs);
            if (channels == 0)
            {
                reason = $"device link to '{header.Pcs.Trim()}', which is not a space this converts into";
                return null;
            }
            IccLut link = IccProfile.ParseDeviceLink(profile, out reason);
            if (link == null) return null;
            if (link.InputChannels != 3 || link.OutputChannels != channels)
            {
                reason = $"the link's A2B0 maps {link.InputChannels} channels to {link.OutputChannels}, where its header "
                       + $"says RGB to {channels}";
                return null;
            }
            reason = null;
            return new IccOutputTransform(link, channels, 0, 0, 0, null, deviceLink: true);
        }

        /// <summary>One sRGB colour into device components, 0 to 1.</summary>
        internal bool TryConvert(SKColor color, float[] device)
        {
            if (device == null || device.Length < _channels) return false;

            if (_deviceLink)
            {
                var rgb = new[] { color.Red / 255f, color.Green / 255f, color.Blue / 255f };
                var linked = new double[_channels];
                if (!_bToA.TryEvaluateRaw(rgb, linked)) return false;
                for (int i = 0; i < _channels; i++) device[i] = (float)linked[i];
                return true;
            }

            // The white fix-up, which does not apply under absolute colorimetric.
            if (_mediaWhite == null && color.Red == 255 && color.Green == 255 && color.Blue == 255)
            {
                // The device's own white: no ink for CMYK, full scale
                // for the additive spaces (RGB, grey).
                float white = _channels == 4 ? 0f : 1f;
                for (int i = 0; i < _channels; i++) device[i] = white;
                return true;
            }

            PdfColor.SRgbToXyzD50(color, out double X, out double Y, out double Z);
            X = X * (1 - _bpX / D50X) + _bpX;
            Y = Y * (1 - _bpY / D50Y) + _bpY;
            Z = Z * (1 - _bpZ / D50Z) + _bpZ;
            if (_mediaWhite != null) { X /= _mediaWhite[0]; Y /= _mediaWhite[1]; Z /= _mediaWhite[2]; }

            var pcs = new float[3];
            if (_bToA.IsLabPcs)
            {
                PdfColor.XyzD50ToLab(X, Y, Z, out double l, out double a, out double b);
                _bToA.EncodeLab(l, a, b, pcs);
            }
            else
            {
                const double Scale = 32768.0 / 65535.0;   // u1Fixed15
                pcs[0] = (float)Clamp01(X * Scale);
                pcs[1] = (float)Clamp01(Y * Scale);
                pcs[2] = (float)Clamp01(Z * Scale);
            }

            var outputs = new double[_channels];
            if (!_bToA.TryEvaluateRaw(pcs, outputs)) return false;
            for (int i = 0; i < _channels; i++) device[i] = (float)outputs[i];
            return true;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
