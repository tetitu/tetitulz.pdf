// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// The adjust handles of a preset shape — <c>a:prstGeom/a:avLst/a:gd</c>.
    ///
    /// <para>
    /// ECMA-376 §20.1.9.11 lets a guide carry an arbitrary formula, but an <c>avLst</c> written by
    /// Word contains only <c>fmla="val N"</c> — a literal. Reading that one form is what makes
    /// adjust handles work without a guide-formula interpreter, which is the expensive half of
    /// general preset support and the half this renderer deliberately does not have.
    /// A guide with any other formula is ignored, so the preset falls back to its default.
    /// </para>
    ///
    /// <para>
    /// Values are in ECMA-376's percentage units: 100000 = 100%. What the percentage is *of*
    /// differs per preset (the shape's width, height, or the smaller of the two), so each builder
    /// applies its own reference and its own clamp.
    /// </para>
    /// </summary>
    internal sealed class AdjustValues
    {
        private const double PercentScale = 100000.0;

        private static readonly AdjustValues EmptyInstance = new AdjustValues(null);

        private readonly Dictionary<string, double> _values;

        private AdjustValues(Dictionary<string, double> values) => _values = values;

        /// <summary>An <c>avLst</c> with no usable guides; every lookup returns its default.</summary>
        internal static AdjustValues Empty => EmptyInstance;

        /// <summary>
        /// Reads the <c>a:avLst</c> under a <c>a:prstGeom</c>. Located by local name, like the rest
        /// of the DrawingML reading in this renderer, so it does not depend on SDK class coverage.
        /// </summary>
        internal static AdjustValues Read(OpenXmlElement prstGeom)
        {
            var avLst = prstGeom?.ChildElements.FirstOrDefault(e => e.LocalName == "avLst");
            if (avLst == null) return EmptyInstance;

            Dictionary<string, double> values = null;
            foreach (var gd in avLst.ChildElements.Where(e => e.LocalName == "gd"))
            {
                string name = Attr(gd, "name");
                string fmla = Attr(gd, "fmla");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(fmla)) continue;

                // Only "val N" — a literal. Anything else needs the formula VM we do not have.
                if (!fmla.StartsWith("val ", StringComparison.Ordinal)) continue;
                if (!double.TryParse(fmla.Substring(4).Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double v))
                    continue;

                values ??= new Dictionary<string, double>(StringComparer.Ordinal);
                values[name] = v;
            }

            return values == null ? EmptyInstance : new AdjustValues(values);
        }

        /// <summary>
        /// The named guide as a fraction (100000 → 1.0), clamped to
        /// <paramref name="min"/>..<paramref name="max"/>, falling back to
        /// <paramref name="defaultRaw"/> when the shape does not override it.
        /// </summary>
        internal double Fraction(string name, double defaultRaw, double min = 0.0, double max = 1.0)
        {
            double raw = defaultRaw;
            if (_values != null && _values.TryGetValue(name, out double v)) raw = v;

            double f = raw / PercentScale;
            if (double.IsNaN(f)) f = defaultRaw / PercentScale;
            return f < min ? min : f > max ? max : f;
        }

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
