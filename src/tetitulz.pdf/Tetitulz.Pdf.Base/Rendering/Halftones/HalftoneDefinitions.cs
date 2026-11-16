// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering.Halftones
{
    /// <summary>
    /// One halftone screen as a document defines it (ISO 32000-2 §10.6.5) — device-independent until a
    /// resolution is known — with its transfer function.
    /// </summary>
    internal sealed class HalftoneDefinition
    {
        // A Type 1 screen: frequency, angle and spot function.
        private double _frequency, _angle;
        private Func<double, double, double> _spot;

        // A threshold array: one rectangle, or two (Types 10 and 16).
        private int _width, _height, _width2, _height2, _maximum;
        private int[] _values;

        private PdfObject _transfer;
        private PdfDocument _doc;

        /// <summary>Whether <c>/AccurateScreens</c> asked for a precision ordinary screening does not give.</summary>
        internal bool AccurateScreensRequested { get; private set; }

        /// <summary>A Type 1 screen of <paramref name="frequency"/> cells per inch at <paramref name="angle"/> degrees.</summary>
        internal static HalftoneDefinition Screen(double frequency, double angle, Func<double, double, double> spot)
            => new HalftoneDefinition { _frequency = frequency, _angle = angle, _spot = spot };

        /// <summary>The screen at <paramref name="dpi"/>, or null with <paramref name="problem"/> saying why it cannot be built.</summary>
        internal HalftoneScreen Build(double dpi, out string problem)
        {
            problem = null;
            long max = PdfLimits.MaxHalftoneCellPixels;
            if (_spot != null)
            {
                // The spot function runs once a cell pixel — a million of them under the cap — so the
                // screen spends one function budget, as a page's rendering does.
                HalftoneScreen screen;
                try
                {
                    using (PdfFunctionEvaluator.SpendFrom(new Core.Util.WorkBudget(PdfLimits.MaxFunctionWorkPerPage,
                               "a halftone screen's spot function (PdfLimits.MaxFunctionWorkPerPage)")))
                        screen = HalftoneScreen.FromSpotFunction(_frequency, _angle, dpi, _spot, max);
                }
                catch (Exception ex) when (ex is Core.Util.WorkBudgetExceededException || ex is InvalidOperationException)
                {
                    problem = "the screen's spot function could not be run over its cell: " + ex.Message;
                    return null;
                }
                if (screen == null)
                    problem = $"a screen of {_frequency.ToString(System.Globalization.CultureInfo.InvariantCulture)} cells per inch at {dpi} dpi is not a cell this builds (larger than PdfLimits.MaxHalftoneCellPixels, or not a positive frequency)";
                return screen;
            }
            return HalftoneScreen.FromThresholdArray(_width, _height, _width2, _height2, _values, _maximum);
        }

        /// <summary>
        /// The halftone's transfer function (§10.6.5.2) as a table over tints — ink 0 … 255 in, ink out — or null
        /// for none (absent, or the name <c>/Identity</c>). The function maps the additive level, <c>1 − tint</c>.
        /// </summary>
        internal byte[] TransferTable()
        {
            if (_transfer == null || _doc == null || !(_doc.ResolveObject(_transfer) is PdfDictionary function)) return null;
            var table = new byte[256];
            var input = new float[1];
            for (int tint = 0; tint < 256; tint++)
            {
                input[0] = 1f - tint / 255f;
                float[] output = PdfFunctionEvaluator.Evaluate(function, input, _doc.ResolveObject);
                float level = output != null && output.Length > 0 ? output[0] : input[0];
                table[tint] = (byte)(255 - PdfColor.Channel(Math.Max(0f, Math.Min(1f, level))));
            }
            return table;
        }

        /// <summary>
        /// Reads one component halftone (Types 1, 6, 10 and 16), or null with <paramref name="problem"/> saying why
        /// this one cannot be used.
        /// </summary>
        internal static HalftoneDefinition Read(PdfDocument doc, PdfDictionary dict, out string problem)
        {
            problem = null;
            int type = SaturatingCast.ToInt(Number(doc, dict, PdfName.HalftoneType, -1));
            var definition = new HalftoneDefinition { _doc = doc, _transfer = dict[PdfName.TransferFunction] };
            long cap = PdfLimits.MaxHalftoneCellPixels;
            switch (type)
            {
                case 1:
                {
                    definition._frequency = Number(doc, dict, PdfName.Frequency, double.NaN);
                    definition._angle = Number(doc, dict, PdfName.Angle, double.NaN);
                    if (double.IsNaN(definition._frequency) || double.IsNaN(definition._angle))
                    {
                        problem = "a Type 1 halftone without a /Frequency and an /Angle";
                        return null;
                    }
                    definition._spot = SpotFunction(doc, doc.ResolveObject(dict[PdfName.SpotFunction]));
                    if (definition._spot == null)
                    {
                        problem = "a Type 1 halftone whose /SpotFunction names no spot function this knows";
                        return null;
                    }
                    definition.AccurateScreensRequested = doc.ResolveObject(dict[PdfName.AccurateScreens]) is PdfBoolean accurate && accurate.Value;
                    return definition;
                }
                case 6:
                case 10:
                case 16:
                {
                    if (!(dict is PdfStream stream))
                    {
                        problem = $"a Type {type} halftone that is not a stream";
                        return null;
                    }
                    int w, h, w2 = 0, h2 = 0;
                    if (type == 10)
                    {
                        w = h = SaturatingCast.ToInt(Number(doc, dict, PdfName.Xsquare, 0));
                        w2 = h2 = SaturatingCast.ToInt(Number(doc, dict, PdfName.Ysquare, 0));
                    }
                    else
                    {
                        w = SaturatingCast.ToInt(Number(doc, dict, PdfName.Width, 0));
                        h = SaturatingCast.ToInt(Number(doc, dict, PdfName.Height, 0));
                        if (type == 16)
                        {
                            w2 = SaturatingCast.ToInt(Number(doc, dict, PdfName.Width2, 0));
                            h2 = SaturatingCast.ToInt(Number(doc, dict, PdfName.Height2, 0));
                        }
                    }
                    if (w <= 0 || h <= 0 || w2 < 0 || h2 < 0 || (w2 == 0) != (h2 == 0))
                    {
                        problem = $"a Type {type} halftone whose rectangles have no area";
                        return null;
                    }
                    long samples = (long)w * h + (long)w2 * h2;
                    if (samples > cap)
                    {
                        problem = $"a Type {type} halftone of {samples} samples, past PdfLimits.MaxHalftoneCellPixels";
                        return null;
                    }
                    int bytesPerSample = type == 16 ? 2 : 1;
                    byte[] data = stream.GetDecodedData();
                    if (data == null || data.Length < samples * bytesPerSample)
                    {
                        problem = $"a Type {type} halftone whose stream holds fewer than its {samples} thresholds";
                        return null;
                    }
                    var values = new int[samples];
                    for (long i = 0; i < samples; i++)
                        values[i] = bytesPerSample == 2 ? (data[2 * i] << 8) | data[2 * i + 1] : data[i];
                    definition._width = w; definition._height = h; definition._width2 = w2; definition._height2 = h2;
                    definition._values = values;
                    definition._maximum = type == 16 ? 65535 : 255;
                    return definition;
                }
                default:
                    problem = type == 5 ? "a Type 5 halftone inside a Type 5 halftone" : $"a halftone of type {type}, which ISO 32000-2 does not define";
                    return null;
            }
        }

        /// <summary>A Type 1 halftone's spot function: a name from Table 126, the first such name in an array, or a function.</summary>
        private static Func<double, double, double> SpotFunction(PdfDocument doc, PdfObject value)
        {
            switch (value)
            {
                case PdfName name:
                    return SpotFunctions.Named(name.Value);
                case PdfArray names:
                    foreach (var item in names)
                        if (doc.ResolveObject(item) is PdfName candidate && SpotFunctions.IsPredefined(candidate.Value))
                            return SpotFunctions.Named(candidate.Value);
                    return null;
                case PdfDictionary function:
                    return SpotFunctions.Of(function, doc.ResolveObject);
                default:
                    return null;
            }
        }

        private static double Number(PdfDocument doc, PdfDictionary dict, PdfName key, double fallback)
        {
            if (!(doc.ResolveObject(dict[key]) is PdfNumber n)) return fallback;
            double v = n.DoubleValue;
            return double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;
        }
    }

    /// <summary>
    /// A halftone as the graphics state names it (<c>/HT</c>, §10.6.5): one screen for every colorant, or a Type 5
    /// halftone's screen per colorant with its default. A colorant with no screen of its own takes the default.
    /// </summary>
    /// <remarks>
    /// <b>A Type 5 halftone's components are read when a colorant asks for one.</b> Each is a threshold
    /// array of up to <see cref="PdfLimits.MaxHalftoneCellPixels"/> samples, and nothing bounds how many
    /// colorant keys a dictionary holds: ten thousand keys naming one shared stream were ten thousand
    /// arrays of four megabytes, read before the page rendered. A page asks for its plates' colorants
    /// (or <c>Gray</c>), each once.
    /// </remarks>
    internal sealed class HalftoneSet
    {
        private readonly Dictionary<string, HalftoneDefinition> _byColorant = new Dictionary<string, HalftoneDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfDictionary> _components = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
        private PdfDocument _doc;

        /// <summary>The screen of a colorant the halftone does not name.</summary>
        internal HalftoneDefinition Default { get; private set; }

        /// <summary>The screen for <paramref name="colorant"/>.</summary>
        internal HalftoneDefinition For(string colorant) => For(colorant, out _);

        /// <summary>
        /// The screen for <paramref name="colorant"/>: its own, read now if it has not been, or the default —
        /// <paramref name="problem"/> then saying why its own could not be used, the first time it is asked.
        /// </summary>
        internal HalftoneDefinition For(string colorant, out string problem)
        {
            problem = null;
            if (colorant == null) return Default;
            if (_byColorant.TryGetValue(colorant, out var known)) return known ?? Default;
            if (!_components.TryGetValue(colorant, out PdfDictionary component)) return Default;

            var definition = HalftoneDefinition.Read(_doc, component, out string componentProblem);
            _byColorant[colorant] = definition;
            if (definition == null) problem = $"/{colorant}: {componentProblem}";
            return definition ?? Default;
        }

        /// <summary>Whether any screen asked for <c>/AccurateScreens</c> — the components' read from their dictionaries.</summary>
        internal bool AccurateScreensRequested
        {
            get
            {
                if (Default != null && Default.AccurateScreensRequested) return true;
                foreach (var component in _components.Values)
                    if (_doc.ResolveObject(component[PdfName.AccurateScreens]) is PdfBoolean accurate && accurate.Value
                        && _doc.ResolveObject(component[PdfName.HalftoneType]) is PdfNumber type && type.IsInteger && type.LongValue == 1)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// The halftone <paramref name="value"/> names, or null — the device's default — when it is the name
        /// <c>/Default</c> or cannot be used, <paramref name="problem"/> then saying why.
        /// </summary>
        internal static HalftoneSet Read(PdfDocument doc, PdfObject value, out string problem)
        {
            problem = null;
            var resolved = doc.ResolveObject(value);
            if (resolved is PdfName name)
            {
                if (name.Value != PdfName.Default.Value) problem = $"a halftone named /{name.Value}, which this device does not have";
                return null;
            }
            if (!(resolved is PdfDictionary dict))
            {
                problem = "a halftone that is neither a dictionary nor a stream";
                return null;
            }

            var set = new HalftoneSet { _doc = doc };
            if (doc.ResolveObject(dict[PdfName.HalftoneType]) is PdfNumber t && t.IsInteger && t.LongValue == 5)
            {
                foreach (var entry in dict)
                {
                    string key = entry.Key.Value;
                    if (key == PdfName.Type.Value || key == PdfName.HalftoneType.Value || key == PdfName.HalftoneName.Value) continue;
                    if (!(doc.ResolveObject(entry.Value) is PdfDictionary component)) continue;
                    if (key != PdfName.Default.Value)
                    {
                        set._components[key] = component;   // read when its colorant asks (For)
                        continue;
                    }
                    set.Default = HalftoneDefinition.Read(doc, component, out string defaultProblem);
                    if (set.Default == null) problem ??= $"/{key}: {defaultProblem}";
                }
                if (set.Default == null && set._components.Count == 0) return null;
                return set;
            }

            set.Default = HalftoneDefinition.Read(doc, dict, out problem);
            return set.Default == null ? null : set;
        }
    }
}
