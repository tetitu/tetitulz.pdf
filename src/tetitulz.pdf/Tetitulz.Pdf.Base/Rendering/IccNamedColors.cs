// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// An ICC named-colour profile (class <c>nmcl</c>): its <c>ncl2</c> tag's colours, each a name, a
    /// PCS colour and, where the profile gives them, device coordinates (ICC.1:2010 §10.17).
    /// </summary>
    /// <remarks>
    /// <para><b>The full name is the prefix, the root and the suffix</b> (<c>SPOT </c> + <c>485</c> +
    /// <c> C</c>); a name is found by that, or by its root alone, exactly first and then ignoring case.</para>
    /// <para><b>The PCS values are the 16-bit encodings</b>: Lab in the legacy form even in a version 4
    /// profile (L* 100 at <c>0xFF00</c>, a* and b* +128 at <c>0x80</c> in the high byte), XYZ as
    /// u1Fixed15 (1.0 at <c>0x8000</c>) — as independent profile dumpers decode them.</para>
    /// </remarks>
    internal sealed class IccNamedColors
    {
        /// <summary>Bytes of a name field (ICC.1 §10.17: 32, NUL-terminated 7-bit ASCII).</summary>
        private const int NameBytes = 32;

        /// <summary>The most device coordinates a colour may carry (ICC.1 §10.17: 0 to 15).</summary>
        private const int MaxDeviceCoordinates = 15;

        private readonly List<IccNamedColor> _colours;
        private readonly Dictionary<string, IccNamedColor> _exact = new Dictionary<string, IccNamedColor>(StringComparer.Ordinal);
        private readonly Dictionary<string, IccNamedColor> _folded = new Dictionary<string, IccNamedColor>(StringComparer.OrdinalIgnoreCase);

        private IccNamedColors(List<IccNamedColor> colours)
        {
            _colours = colours;
            // Full names before roots, so a root that is another colour's full name loses.
            foreach (var c in colours) Add(c.Name, c);
            foreach (var c in colours) Add(c.Root, c);
        }

        private void Add(string key, IccNamedColor colour)
        {
            if (!_exact.ContainsKey(key)) _exact[key] = colour;
            if (!_folded.ContainsKey(key)) _folded[key] = colour;
        }

        /// <summary>The profile's colours in the order it lists them.</summary>
        internal IReadOnlyList<IccNamedColor> Colours => _colours;

        /// <summary>The colour <paramref name="name"/> names — its full name or its root — or null.</summary>
        internal IccNamedColor Find(string name)
        {
            if (name == null) return null;
            if (_exact.TryGetValue(name, out var c)) return c;
            return _folded.TryGetValue(name, out c) ? c : null;
        }

        /// <summary>
        /// The colour whose full name (prefix, root, suffix) is <paramref name="name"/>, exactly or ignoring case,
        /// or null — never by its root alone, as a spot's colorant name is always the full one.
        /// </summary>
        internal IccNamedColor FindFullName(string name)
        {
            if (name == null) return null;
            IccNamedColor folded = null;
            foreach (var c in _colours)
            {
                if (string.Equals(c.Name, name, StringComparison.Ordinal)) return c;
                if (folded == null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) folded = c;
            }
            return folded;
        }

        /// <summary>The profile's named colours, or null and the reason it is not a readable named-colour profile.</summary>
        internal static IccNamedColors TryParse(byte[] data, out string reason)
        {
            if (!IccProfile.TryReadHeader(data, out IccProfile.IccHeader header, out reason)) return null;
            if (header.DeviceClass != "nmcl")
            {
                reason = $"device class is '{header.DeviceClass}', not a named-colour profile ('nmcl')";
                return null;
            }
            bool lab = header.Pcs == "Lab ";
            if (!lab && header.Pcs != "XYZ ")
            {
                reason = $"PCS is '{header.Pcs}', which is neither 'XYZ ' nor 'Lab '";
                return null;
            }
            if (!IccProfile.TryFindTag(data, "ncl2", out int offset, out int size, out reason)) return null;

            // Type signature, reserved, vendor flag, count, device coordinates, prefix, suffix.
            const int HeaderBytes = 4 + 4 + 4 + 4 + 4 + NameBytes + NameBytes;
            if (size < HeaderBytes || !Signature(data, offset, "ncl2"))
            {
                reason = "the ncl2 tag is not a namedColor2Type";
                return null;
            }
            long count = ReadU32(data, offset + 12);
            long deviceCount = ReadU32(data, offset + 16);
            if (deviceCount > MaxDeviceCoordinates)
            {
                reason = $"the ncl2 tag gives {deviceCount} device coordinates a colour; ICC.1 allows 15";
                return null;
            }
            long recordBytes = NameBytes + 6 + 2 * deviceCount;
            if (count > (size - HeaderBytes) / recordBytes)
            {
                reason = $"the ncl2 tag declares {count} colours, more than its {size} bytes hold";
                return null;
            }

            string prefix = Name(data, offset + 20);
            string suffix = Name(data, offset + 20 + NameBytes);
            string device = header.DataColorSpace.Trim();
            var colours = new List<IccNamedColor>((int)count);
            int p = offset + HeaderBytes;
            for (long i = 0; i < count; i++, p += (int)recordBytes)
            {
                string root = Name(data, p);
                int v0 = ReadU16(data, p + NameBytes), v1 = ReadU16(data, p + NameBytes + 2), v2 = ReadU16(data, p + NameBytes + 4);
                double l, a, b;
                SKColor srgb;
                if (lab)
                {
                    l = v0 * 100.0 / 0xFF00;
                    a = v1 / 256.0 - 128;
                    b = v2 / 256.0 - 128;
                    PdfColor.LabToXyzD50(l, a, b, out double x, out double y, out double z);
                    srgb = PdfColor.XyzD50ToSRgb(x, y, z);
                }
                else
                {
                    double x = v0 / 32768.0, y = v1 / 32768.0, z = v2 / 32768.0;
                    PdfColor.XyzD50ToLab(x, y, z, out l, out a, out b);
                    srgb = PdfColor.XyzD50ToSRgb(x, y, z);
                }
                var coordinates = new float[deviceCount];
                for (int k = 0; k < deviceCount; k++)
                    coordinates[k] = ReadU16(data, p + NameBytes + 6 + 2 * k) / 65535f;
                colours.Add(new IccNamedColor(prefix + root + suffix, root, l, a, b, device, coordinates,
                                              srgb.Red / 255f, srgb.Green / 255f, srgb.Blue / 255f));
            }
            reason = null;
            return new IccNamedColors(colours);
        }

        /// <summary>A name field: the bytes before its NUL, as ASCII (ICC.1 says 7-bit; a byte above 127 becomes '?').</summary>
        private static string Name(byte[] data, int offset)
        {
            int end = offset;
            while (end < offset + NameBytes && data[end] != 0) end++;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static bool Signature(byte[] data, int offset, string sig)
        {
            for (int i = 0; i < 4; i++) if (data[offset + i] != sig[i]) return false;
            return true;
        }

        private static long ReadU32(byte[] d, int o) => Core.IO.BigEndian.U32(d, o);
        private static int ReadU16(byte[] d, int o) => Core.IO.BigEndian.U16(d, o);
    }
}
