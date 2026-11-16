// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// One colour of an ICC named-colour profile (class <c>nmcl</c>, ICC.1 §10.17): its name, its PCS
    /// colour as CIE L*a*b* (D50), its device coordinates where the profile gives them, and the sRGB
    /// the colour converts to — relative colorimetric, D50 to D65 by Bradford, clipped to the gamut.
    /// </summary>
    public sealed class IccNamedColor
    {
        internal IccNamedColor(string name, string root, double l, double a, double b, string deviceColorSpace,
                               IReadOnlyList<float> deviceComponents, float red, float green, float blue)
        {
            Name = name; Root = root; L = l; A = a; B = b;
            DeviceColorSpace = deviceColorSpace; DeviceComponents = deviceComponents;
            Red = red; Green = green; Blue = blue;
        }

        /// <summary>The full name: the profile's prefix, the colour's root and the profile's suffix (<c>SPOT 485 C</c>).</summary>
        public string Name { get; }

        /// <summary>The colour's own part of its name (<c>485</c>).</summary>
        public string Root { get; }

        /// <summary>CIE L* (D50), 0 to 100.</summary>
        public double L { get; }

        /// <summary>CIE a* (D50).</summary>
        public double A { get; }

        /// <summary>CIE b* (D50).</summary>
        public double B { get; }

        /// <summary>The profile's device colour space signature, trimmed (<c>CMYK</c>), or empty when it names none.</summary>
        public string DeviceColorSpace { get; }

        /// <summary>The device coordinates the profile gives the colour, 0 to 1 each; empty when it gives none.</summary>
        public IReadOnlyList<float> DeviceComponents { get; }

        /// <summary>sRGB red, 0 to 1.</summary>
        public float Red { get; }

        /// <summary>sRGB green, 0 to 1.</summary>
        public float Green { get; }

        /// <summary>sRGB blue, 0 to 1.</summary>
        public float Blue { get; }
    }
}
