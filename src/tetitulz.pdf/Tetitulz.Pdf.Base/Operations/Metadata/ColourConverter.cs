// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>What <see cref="PdfColorSpaceConverter.ConvertToCmyk"/> converts, and into what.</summary>
    public sealed class CmykConversionOptions
    {
        /// <summary>
        /// The destination: a CMYK output profile with <c>B2A</c> tables (a press characterisation), or a device
        /// link from RGB to CMYK. Required.
        /// </summary>
        public byte[] OutputIccProfile { get; init; }

        /// <summary>Convert DeviceRGB colour. Default <c>true</c>.</summary>
        public bool ConvertDeviceRgb { get; init; } = true;

        /// <summary>Convert CalRGB colour. Default <c>true</c>.</summary>
        public bool ConvertCalRgb { get; init; } = true;

        /// <summary>Convert Lab colour. Default <c>true</c>.</summary>
        public bool ConvertLab { get; init; } = true;

        /// <summary>Convert colour in a three-component <c>/ICCBased</c> (RGB) space. Default <c>true</c>.</summary>
        public bool ConvertIccBasedRgb { get; init; } = true;

        /// <summary>Black-point compensation into the profile. Default <c>true</c>.</summary>
        public bool BlackPointCompensation { get; init; } = true;
    }

    /// <summary>What <see cref="PdfColorSpaceConverter.ConvertToRgb"/> converts, and into what.</summary>
    public sealed class RgbConversionOptions
    {
        /// <summary>
        /// The RGB space the device RGB values are meant in — the output intent's profile, a matrix/TRC or
        /// <c>B2A</c> RGB profile. <c>null</c> (the default) writes sRGB values.
        /// </summary>
        public byte[] OutputIccProfile { get; init; }

        /// <summary>Convert DeviceCMYK colour. Default <c>true</c>.</summary>
        public bool ConvertDeviceCmyk { get; init; } = true;
    }

    /// <summary>What a colour conversion (<see cref="PdfColorSpaceConverter.ConvertToCmyk"/>, <see cref="PdfColorSpaceConverter.ConvertToRgb"/>) converted, and what it left.</summary>
    public sealed class ColourConversionResult
    {
        /// <summary>Colour spaces replaced: resource entries, image and shading spaces, group blending spaces.</summary>
        public int ColourSpacesConverted { get; internal set; }

        /// <summary>Colour-setting operators rewritten in content streams (<c>rg</c>, <c>k</c>, <c>sc</c>, <c>scn</c> …).</summary>
        public int ColoursConverted { get; internal set; }

        /// <summary>Images whose samples were converted (raster and inline alike).</summary>
        public int ImagesConverted { get; internal set; }

        /// <summary>Shadings given a function or a space in the target.</summary>
        public int ShadingsConverted { get; internal set; }

        /// <summary>What was left in its own colour, each with the reason; empty when nothing was.</summary>
        public IReadOnlyList<string> NotConverted => _notConverted;

        internal readonly List<string> _notConverted = new List<string>();
    }

    /// <summary>
    /// The document's colour converted into one device family (for the PDF/X and PDF/A converters): every colour in the chosen spaces — set by content operators, in images, shadings,
    /// <c>/Indexed</c> palettes, spot colours' alternates, patterns' underlying spaces and transparency groups —
    /// replaced by its value in the target. Into <b>CMYK</b> through an output profile's <c>B2A</c> table (a press:
    /// PDF/X, or DeviceRGB under a CMYK PDF/A intent); into <b>RGB</b> — the values of an RGB output intent,
    /// sRGB by default — for DeviceCMYK under an RGB PDF/A intent.
    /// </summary>
    /// <remarks>
    /// <para><b>One conversion.</b> A colour reaches the target as the renderer shows it (its sRGB, through the
    /// same conversions a render takes). Into CMYK it goes by the profile's perceptual table with black-point
    /// compensation — the image conversion's own function (<see cref="PdfColorSpaceConverter.RgbToCmyk"/>); into
    /// RGB, through the intent's profile as a render's destination does (<see cref="RenderDestination"/>). A
    /// converted document holds one rendering of its colour, vector and raster alike.</para>
    /// <para><b>What changes how.</b> A space whose components change — into CMYK: DeviceRGB, CalRGB, Lab, an RGB
    /// <c>/ICCBased</c>; into RGB: DeviceCMYK — becomes the target device space, and the operands set in it are
    /// converted where they are set (<c>rg</c>/<c>RG</c> become <c>k</c>/<c>K</c>, or the reverse; <c>sc</c>/<c>scn</c>
    /// after its <c>cs</c> take the target's count). A space whose operands stay — an <c>/Indexed</c> one (its
    /// palette converted), a <c>/Separation</c> or <c>/DeviceN</c> (its alternate made the target and its tint
    /// transform a sampled function of the old one) — is replaced and its operands left. A shading's function is
    /// sampled into one of the target; an image's samples are decoded as the renderer decodes them and written
    /// back as 8-bit Flate in the target.</para>
    /// <para><b>What is left, and said</b> (<see cref="ColourConversionResult.NotConverted"/>): a mesh shading
    /// whose colours are in its vertex data rather than a function; an image whose colour key names samples in
    /// the old space, or a JPEG 2000 image carrying its opacity in its codestream; an inline image in a named
    /// space the conversion changes. A form drawn in a colour space it does not set itself would take its
    /// caller's, which the conversion cannot know: forms are converted as starting in DeviceGray, as a page does.</para>
    /// <para>The output is one fresh revision (<see cref="PdfFullRewriter"/>), objects renumbered, so a signature
    /// does not survive; an encrypted document is refused.</para>
    /// </remarks>
    internal sealed class ColourConverter
    {
        /// <summary>The device family colour goes into, and how.</summary>
        private sealed class Target
        {
            internal PdfName Space;                   // /DeviceCMYK or /DeviceRGB
            internal int Components;                  // 4 or 3
            internal string FillOperator, StrokeOperator;               // k, K — or rg, RG
            internal string SourceFillOperator, SourceStrokeOperator;   // the device operators that convert: rg, RG — or k, K
            internal string SourceDevice;             // the device family that converts: DeviceRGB or DeviceCMYK
            internal PdfName SourceSpace;             // the same, as the space a device operator sets
            internal bool ConvertSourceDevice;
            internal bool ConvertCalRgb, ConvertLab, ConvertIccBasedRgb;   // device-independent RGB into CMYK
            internal Func<SKColor, float[]> FromSrgb; // components 0 … 1
            internal Func<byte[], int, byte[]> ImageFromSrgb;   // 8-bit sRGB samples → 8-bit target samples, or null on refusal
        }

        private readonly PdfDocument _doc;
        private readonly Target _target;
        private readonly ColourConversionResult _result;
        private readonly Dictionary<int, PdfObject> _extras = new Dictionary<int, PdfObject>();
        private int _nextObject;

        // A space's conversion, once per object; a resource entry's, by its dictionary and name.
        private readonly Dictionary<PdfObject, Conversion> _bySpace = new Dictionary<PdfObject, Conversion>(PdfReferenceEquality.Instance);
        private readonly Dictionary<(PdfDictionary, string), Conversion> _byEntry = new Dictionary<(PdfDictionary, string), Conversion>();
        private readonly HashSet<PdfObject> _done = new HashSet<PdfObject>(PdfReferenceEquality.Instance);
        private readonly Dictionary<uint, float[]> _converted = new Dictionary<uint, float[]>();
        private readonly HashSet<string> _notes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How a space converts: its replacement, and whether operands set in it change too.</summary>
        private sealed class Conversion
        {
            internal PdfObject Replacement;   // what it becomes
            internal bool Direct;             // operands in it are converted (the space became the target device space)
            internal PdfObject OperandSpace;  // the space operands are read in when Direct (a pattern's base, else the space)
        }

        private ColourConverter(PdfDocument doc, Target target, ColourConversionResult result)
        {
            _doc = doc;
            _target = target;
            _result = result;
            _nextObject = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
        }

        internal static ColourConversionResult ToCmyk(Stream input, Stream output, CmykConversionOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (options?.OutputIccProfile == null) throw new ArgumentException("CmykConversionOptions.OutputIccProfile is required.", nameof(options));

            IccOutputTransform transform = IccOutputTransform.TryCreate(options.OutputIccProfile, IccRenderingIntent.Perceptual,
                options.BlackPointCompensation, out string reason);
            if (transform == null || transform.Channels != 4)
                throw new ArgumentException("The output profile cannot take colour into CMYK: "
                    + (reason ?? $"it converts into {transform.Channels} channels, not 4"), nameof(options));

            var target = new Target
            {
                Space = PdfName.DeviceCMYK, Components = 4,
                FillOperator = PdfOperators.SetCmykFill, StrokeOperator = PdfOperators.SetCmykStroke,
                SourceFillOperator = PdfOperators.SetRgbFill, SourceStrokeOperator = PdfOperators.SetRgbStroke,
                SourceDevice = PdfColorSpaceNames.DeviceRGB, SourceSpace = PdfName.DeviceRGB, ConvertSourceDevice = options.ConvertDeviceRgb,
                ConvertCalRgb = options.ConvertCalRgb, ConvertLab = options.ConvertLab, ConvertIccBasedRgb = options.ConvertIccBasedRgb,
                FromSrgb = colour =>
                {
                    var device = new float[4];
                    if (!transform.TryConvert(colour, device))
                        OverprintCompositor.RgbToCmykExactInverse(colour, out device[0], out device[1], out device[2], out device[3]);
                    return device;
                },
                ImageFromSrgb = (rgb, n) => PdfColorSpaceConverter.RgbToCmyk(rgb, n, transform),
            };

            var result = new ColourConversionResult();

            // DeviceRGB images and inline images first, by the conversion that already does them — its one
            // incremental update, then the rest on top.
            Stream source = PdfMetadataWriter.EnsureSeekable(input);
            if (options.ConvertDeviceRgb)
            {
                var images = new MemoryStream();
                result.ImagesConverted += PdfColorSpaceConverter.ConvertRgbToCmyk(source, images, options.OutputIccProfile, 1.0);
                if (PdfColorSpaceConverter.LastConversionWarning is string warning) result._notConverted.Add(warning);
                images.Position = 0;
                source = images;
            }
            return Run(source, output, target, result);
        }

        internal static ColourConversionResult ToRgb(Stream input, Stream output, RgbConversionOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options ??= new RgbConversionOptions();

            RenderDestination destination = null;
            if (options.OutputIccProfile != null)
            {
                destination = RenderDestination.TryCreate(options.OutputIccProfile, true, out string reason);
                if (destination == null || destination.IsGrey)
                    throw new ArgumentException("The output profile cannot take colour into RGB: "
                        + (reason ?? "it is a grey profile"), nameof(options));
            }

            var target = new Target
            {
                Space = PdfName.DeviceRGB, Components = 3,
                FillOperator = PdfOperators.SetRgbFill, StrokeOperator = PdfOperators.SetRgbStroke,
                SourceFillOperator = PdfOperators.SetCmykFill, SourceStrokeOperator = PdfOperators.SetCmykStroke,
                SourceDevice = PdfColorSpaceNames.DeviceCMYK, SourceSpace = PdfName.DeviceCMYK, ConvertSourceDevice = options.ConvertDeviceCmyk,
                FromSrgb = colour =>
                {
                    SKColor rgb = destination?.Convert(colour) ?? colour;
                    return new[] { rgb.Red / 255f, rgb.Green / 255f, rgb.Blue / 255f };
                },
                ImageFromSrgb = (rgb, n) =>
                {
                    if (destination == null) return rgb;
                    var out3 = new byte[rgb.Length];
                    for (int i = 0; i < n; i++)
                    {
                        SKColor c = destination.Convert(new SKColor(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]));
                        out3[i * 3] = c.Red; out3[i * 3 + 1] = c.Green; out3[i * 3 + 2] = c.Blue;
                    }
                    return out3;
                },
            };
            return Run(PdfMetadataWriter.EnsureSeekable(input), output, target, new ColourConversionResult());
        }

        private static ColourConversionResult Run(Stream source, Stream output, Target target, ColourConversionResult result)
        {
            using var doc = PdfDocument.Open(source);
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Cannot convert the colour of an encrypted document: decrypt it first (Tetitulz.Pdf.Encryption).");

            var converter = new ColourConverter(doc, target, result);
            // Each tint transform and shading function is sampled at up to 4 096 points: one budget
            // for the conversion's functions, as a page's rendering has (PdfLimits.MaxFunctionWorkPerPage).
            using (PdfFunctionEvaluator.SpendFrom(new Core.Util.WorkBudget(PdfLimits.MaxFunctionWorkPerPage,
                       "the Type 4 functions one colour conversion samples (PdfLimits.MaxFunctionWorkPerPage)")))
                converter.Walk();
            if (!PdfFullRewriter.TryFullRewrite(doc, output, converter._extras))
                throw new Core.PdfException("The document has no catalog to write the converted colour from.");
            return result;
        }

        // ------------------------------------------------------------------ the walk

        private void Walk()
        {
            var containers = new List<(PdfStream Stream, PdfDictionary Resources)>();
            var index = DocumentImageIndex.Build(_doc,
                onResources: (_, resources) => ConvertResources(resources, containers),
                onContainer: (_, container) =>
                {
                    if (container is PdfStream s && _done.Add(s))
                    {
                        ConvertGroup(s);
                        containers.Add((s, _doc.ResolveObject(s[PdfName.Resources]) as PdfDictionary));
                    }
                });

            for (int p = 0; p < _doc.Pages.Count; p++)
            {
                PdfDictionary page = _doc.Pages[p].Dictionary;
                PdfDictionary resources = _doc.Pages[p].Resources;
                ConvertGroup(page);
                // The page's content streams in order, one colour state across them (§7.8.2: an array's
                // parts are one stream).
                var state = new ColourState();
                foreach (PdfStream part in PdfColorSpaceConverter.PageContentStreams(_doc, page))
                    if (_done.Add(part)) ConvertContent(part, resources, state);
            }
            foreach (var (stream, resources) in containers) ConvertContent(stream, resources, new ColourState());

            foreach (var entry in index.Entries) ConvertImage(entry.Value.Stream, "/" + entry.Value.Names[0]);

            foreach (string note in _notes) _result._notConverted.Add(note);
        }

        /// <summary>A resource dictionary's colour spaces, shadings, patterns' shadings, soft masks and Type 3 glyphs.</summary>
        private void ConvertResources(PdfDictionary resources, List<(PdfStream, PdfDictionary)> containers)
        {
            if (resources == null || !_done.Add(resources)) return;

            if (PdfResolve.Dict(_doc, resources, PdfName.ColorSpace) is PdfDictionary spaces)
                foreach (var name in new List<PdfName>(spaces.Keys))
                {
                    Conversion conversion = ConvertSpace(spaces[name], 0);
                    if (conversion == null) continue;
                    _byEntry[(spaces, name.Value)] = conversion;
                    spaces.Set(name, conversion.Replacement);
                    _result.ColourSpacesConverted++;
                }

            if (PdfResolve.Dict(_doc, resources, PdfName.Shading) is PdfDictionary shadings)
                foreach (var entry in shadings)
                    if (_doc.ResolveObject(entry.Value) is PdfDictionary shading) ConvertShading(shading);

            if (PdfResolve.Dict(_doc, resources, PdfName.Pattern) is PdfDictionary patterns)
                foreach (var entry in patterns)
                    if (_doc.ResolveObject(entry.Value) is PdfDictionary pattern
                        && PdfResolve.Int(_doc, pattern, PdfName.PatternType, 0) == 2
                        && _doc.ResolveObject(pattern[PdfName.Shading]) is PdfDictionary shading)
                        ConvertShading(shading);

            if (PdfResolve.Dict(_doc, resources, PdfName.ExtGState) is PdfDictionary states)
                foreach (var entry in states)
                    if (_doc.ResolveObject(entry.Value) is PdfDictionary state
                        && _doc.ResolveObject(state[PdfName.SMask]) is PdfDictionary mask
                        && _doc.ResolveObject(mask[PdfName.G]) is PdfStream group && _done.Add(group))
                    {
                        Conversion space = ConvertGroup(group);
                        if (space != null && space.Direct && _doc.ResolveObject(mask[PdfName.BC]) is PdfArray backdrop)
                            mask.Set(PdfName.BC, TargetArray(space.OperandSpace, Numbers(backdrop)));
                        var groupResources = _doc.ResolveObject(group[PdfName.Resources]) as PdfDictionary;
                        ConvertResources(groupResources, containers);
                        containers.Add((group, groupResources));
                    }

            if (PdfResolve.Dict(_doc, resources, PdfName.Font) is PdfDictionary fonts)
                foreach (var entry in fonts)
                    if (_doc.ResolveObject(entry.Value) is PdfDictionary font
                        && PdfResolve.Name(_doc, font, PdfName.Subtype)?.Value == "Type3"
                        && _doc.ResolveObject(font[PdfName.CharProcs]) is PdfDictionary procs)
                    {
                        var fontResources = _doc.ResolveObject(font[PdfName.Resources]) as PdfDictionary ?? resources;
                        ConvertResources(fontResources, containers);
                        foreach (var glyph in procs)
                            if (_doc.ResolveObject(glyph.Value) is PdfStream proc && _done.Add(proc))
                                containers.Add((proc, fontResources));
                    }
        }

        /// <summary>A page's or a form's transparency group's blending space (§11.6.6), converted; the conversion, or null.</summary>
        private Conversion ConvertGroup(PdfDictionary owner)
        {
            if (!(PdfResolve.Dict(_doc, owner, PdfName.Group) is PdfDictionary group) || group[PdfName.CS] == null) return null;
            Conversion conversion = ConvertSpace(group[PdfName.CS], 0);
            if (conversion == null) return null;
            group.Set(PdfName.CS, conversion.Replacement);
            _result.ColourSpacesConverted++;
            return conversion;
        }

        // ------------------------------------------------------------------ spaces

        /// <summary>
        /// How <paramref name="value"/>, a colour space, converts — or null when it is not one this run converts.
        /// </summary>
        private Conversion ConvertSpace(PdfObject value, int depth)
        {
            if (value == null || depth > PdfLimits.MaxResourceDepth) return null;
            PdfObject space = _doc.ResolveObject(value) ?? value;

            if (space is PdfName name)
            {
                string family = PdfColorSpaceNames.Expand(name.Value);
                if (family == _target.SourceDevice && _target.ConvertSourceDevice)
                    return new Conversion { Replacement = _target.Space, Direct = true, OperandSpace = name };
                return null;
            }
            if (!(space is PdfArray array) || array.Count == 0) return null;
            if (_bySpace.TryGetValue(array, out Conversion cached)) return cached;

            Conversion conversion = null;
            string kind = (_doc.ResolveObject(array[0]) as PdfName)?.Value;
            switch (kind)
            {
                case PdfColorSpaceNames.CalRGB when _target.ConvertCalRgb:
                case PdfColorSpaceNames.Lab when _target.ConvertLab:
                    conversion = Directly(array);
                    break;
                case PdfColorSpaceNames.ICCBased when _target.ConvertIccBasedRgb
                    && PdfColor.ResolveSpace(array, _doc).Components == PdfColorSpaceNames.RgbComponents:
                    conversion = Directly(array);
                    break;
                case PdfColorSpaceNames.Indexed:
                    conversion = ConvertIndexed(array, depth);
                    break;
                case PdfColorSpaceNames.Separation:
                case PdfColorSpaceNames.DeviceN:
                    conversion = ConvertSpot(array, depth);
                    break;
                case PdfColorSpaceNames.Pattern when array.Count > 1:
                {
                    Conversion basis = ConvertSpace(array[1], depth + 1);
                    if (basis == null) break;
                    var pattern = new PdfArray();
                    pattern.Add(PdfName.Pattern);
                    pattern.Add(basis.Replacement);
                    conversion = new Conversion { Replacement = pattern, Direct = basis.Direct, OperandSpace = basis.OperandSpace };
                    break;
                }
            }
            _bySpace[array] = conversion;
            return conversion;
        }

        private Conversion Directly(PdfArray array) => new Conversion { Replacement = _target.Space, Direct = true, OperandSpace = array };

        /// <summary>[/Indexed base hival lookup]: a converted base's palette in the target; a spot base replaced as a spot is.</summary>
        private Conversion ConvertIndexed(PdfArray array, int depth)
        {
            if (array.Count < 4) return null;
            Conversion basis = ConvertSpace(array[1], depth + 1);
            if (basis == null) return null;
            var indexed = new PdfArray();
            indexed.Add(PdfName.Indexed);
            if (!basis.Direct)
            {
                // The base keeps its components (a spot's tints), so the table stands.
                indexed.Add(basis.Replacement);
                indexed.Add(array[2]);
                indexed.Add(array[3]);
                return new Conversion { Replacement = indexed };
            }
            if (!IndexedPalette.TryReadSpace(_doc, array, out PdfObject baseObj, out int hival, out byte[] table) || table == null)
            {
                Note("an /Indexed colour space whose palette could not be read was left in its own colour");
                return null;
            }
            SKColor[] palette = IndexedPalette.Build(_doc, baseObj, table, hival, IccRenderingIntent.Perceptual);
            int n = _target.Components;
            var converted = new byte[palette.Length * n];
            for (int i = 0; i < palette.Length; i++)
            {
                float[] c = Convert(palette[i]);
                for (int k = 0; k < n; k++) converted[i * n + k] = PdfColor.Channel(c[k]);
            }
            indexed.Add(_target.Space);
            indexed.Add(new PdfNumber(palette.Length - 1));
            indexed.Add(new PdfString(converted, isHex: true));
            return new Conversion { Replacement = indexed };
        }

        /// <summary>
        /// [/Separation name alternate tint] or [/DeviceN names alternate tint attrs]: the alternate made the target,
        /// the tint transform a sampled function of the old one through the old alternate.
        /// </summary>
        private Conversion ConvertSpot(PdfArray array, int depth)
        {
            if (array.Count < 4) return null;
            PdfObject alternate = array[2];
            Conversion basis = ConvertSpace(alternate, depth + 1);
            if (basis == null || !basis.Direct) return null;
            if (!(_doc.ResolveObject(array[3]) is PdfDictionary tint))
            {
                Note("a spot colour space whose tint transform could not be read was left in its own colour");
                return null;
            }
            int inputs = 1;
            if ((_doc.ResolveObject(array[0]) as PdfName)?.Value == PdfColorSpaceNames.DeviceN)
                inputs = (_doc.ResolveObject(array[1]) as PdfArray)?.Count ?? 1;
            if (inputs < 1 || inputs > PdfLimits.MaxColorantCount) return null;
            if (inputs > MaxSampledInputs)
            {
                // Sampled at two points an input, a tint transform of n colorants is 2ⁿ samples:
                // past twelve, more than the 4 096 every other space is sampled at, and at 31 a
                // table no array holds.
                Note($"a DeviceN space of {inputs} colorants was left in its own colour: its tint transform " +
                     $"would be sampled at 2^{inputs} points");
                return null;
            }

            var domain = new float[inputs * 2];
            for (int i = 0; i < inputs; i++) domain[2 * i + 1] = 1;
            PdfReference sampled = SampledOrNote(domain, components =>
            {
                float[] alt = PdfFunctionEvaluator.Evaluate(tint, components, _doc.ResolveObject);
                return Convert(ToSrgb(alternate, alt));
            }, "a spot colour space");
            if (sampled == null) return null;

            var spot = new PdfArray();
            for (int i = 0; i < array.Count; i++) spot.Add(array[i]);
            spot[2] = _target.Space;
            spot[3] = sampled;
            return new Conversion { Replacement = spot };
        }

        // ------------------------------------------------------------------ shadings and images

        private void ConvertShading(PdfDictionary shading)
        {
            if (!_done.Add(shading)) return;
            Conversion conversion = ConvertSpace(shading[PdfName.ColorSpace], 0);
            if (conversion == null) return;
            if (!conversion.Direct)
            {
                shading.Set(PdfName.ColorSpace, conversion.Replacement);
                _result.ShadingsConverted++;
                return;
            }

            int type = PdfResolve.Int(_doc, shading, PdfName.ShadingType, 0);
            PdfObject function = _doc.ResolveObject(shading[PdfName.Function]);
            if (function == null)
            {
                Note($"a type {type} shading whose colours are in its data, not a function, was left in its own colour");
                return;
            }

            // The function's inputs: (x, y) for a type 1 shading over its /Domain; t for the rest, over the
            // shading's /Domain (types 2 and 3) or the function's own (a mesh's parametric t).
            float[] domain;
            if (type == 1) domain = Numbers(_doc.ResolveObject(shading[PdfName.Domain]) as PdfArray) ?? new float[] { 0, 1, 0, 1 };
            else if (type == 2 || type == 3) domain = Numbers(_doc.ResolveObject(shading[PdfName.Domain]) as PdfArray) ?? new float[] { 0, 1 };
            else
            {
                var first = function as PdfDictionary ?? (_doc.ResolveObject((function as PdfArray)?.Count > 0 ? ((PdfArray)function)[0] : null) as PdfDictionary);
                domain = Numbers(_doc.ResolveObject(first?[PdfName.Domain]) as PdfArray) ?? new float[] { 0, 1 };
            }
            if (domain.Length < (type == 1 ? 4 : 2)) return;
            if (type != 1) Array.Resize(ref domain, 2);

            PdfObject space = conversion.OperandSpace;
            PdfReference sampled = SampledOrNote(domain, inputs => Convert(ToSrgb(space, EvaluateShadingFunction(function, inputs))),
                $"a type {type} shading");
            if (sampled == null) return;
            shading.Set(PdfName.Function, sampled);
            shading.Set(PdfName.ColorSpace, _target.Space);
            if (_doc.ResolveObject(shading[PdfName.Background]) is PdfArray background)
                shading.Set(PdfName.Background, TargetArray(space, Numbers(background)));
            _result.ShadingsConverted++;
        }

        /// <summary>A shading's <c>/Function</c>: one function of n outputs, or an array of n one-output functions.</summary>
        private float[] EvaluateShadingFunction(PdfObject function, float[] inputs)
        {
            if (function is PdfDictionary single) return PdfFunctionEvaluator.Evaluate(single, inputs, _doc.ResolveObject);
            if (function is PdfArray several)
            {
                var outputs = new float[several.Count];
                for (int i = 0; i < several.Count; i++)
                    if (_doc.ResolveObject(several[i]) is PdfDictionary f)
                    {
                        float[] o = PdfFunctionEvaluator.Evaluate(f, inputs, _doc.ResolveObject);
                        if (o != null && o.Length > 0) outputs[i] = o[0];
                    }
                return outputs;
            }
            return Array.Empty<float>();
        }

        private void ConvertImage(PdfStream image, string where)
        {
            if (image == null || !_done.Add(image)) return;
            if (PdfResolve.Bool(_doc, image, PdfName.ImageMask)?.Value == true) return;
            Conversion conversion = ConvertSpace(image[PdfName.ColorSpace], 0);
            if (conversion == null) return;
            if (!conversion.Direct)
            {
                image.Set(PdfName.ColorSpace, conversion.Replacement);
                _result.ColourSpacesConverted++;
                return;
            }
            if (_doc.ResolveObject(image[PdfName.Mask]) is PdfArray)
            {
                Note($"{where}: an image whose colour key names samples in its old space was left in its own colour");
                return;
            }
            if (PdfResolve.Int(_doc, image, PdfName.SMaskInData, 0) != 0)
            {
                Note($"{where}: a JPEG 2000 image carrying its opacity in its codestream was left in its own colour");
                return;
            }

            // The colour alone, as the renderer decodes it: the masks stay on the image as they are.
            var colourOnly = (PdfStream)PdfDictionary.CloneShallow(image);   // a stream's clone is a stream, its data shared
            colourOnly.Remove(PdfName.SMask);
            colourOnly.Remove(PdfName.Mask);
            using SKBitmap bitmap = ContentStreamInterpreter.DecodeImageXObject(_doc, colourOnly);
            if (bitmap == null)
            {
                Note($"{where}: an image that could not be decoded was left in its own colour");
                return;
            }
            int width = bitmap.Width, height = bitmap.Height;
            var rgb = new byte[checked(width * height * 3)];
            BitmapPixels.Read(bitmap, (row, y) =>
            {
                for (int x = 0; x < width && x < row.Length; x++)
                {
                    int o = (y * width + x) * 3;
                    rgb[o] = row[x].Red; rgb[o + 1] = row[x].Green; rgb[o + 2] = row[x].Blue;
                }
            });
            byte[] samples = _target.ImageFromSrgb(rgb, width * height);
            if (samples == null)
            {
                Note($"{where}: the output profile's table refused a colour of an image, which was left in its own colour");
                return;
            }
            byte[] compressed = FlateEncodeFilter.Encode(samples);
            image.Set(PdfName.ColorSpace, _target.Space);
            image.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            image.Set(PdfName.Width, new PdfNumber(width));
            image.Set(PdfName.Height, new PdfNumber(height));
            image.Set(PdfName.Filter, PdfName.FlateDecode);
            image.Remove(PdfName.DecodeParms);
            image.Remove(PdfName.Decode);
            image.Remove(PdfName.DL);
            image.Set(PdfName.Length, new PdfNumber(compressed.Length));
            image.RawData = compressed;
            _result.ImagesConverted++;
        }

        // ------------------------------------------------------------------ content

        /// <summary>The colour spaces in force, and the stack <c>q</c>/<c>Q</c> keep them on.</summary>
        private sealed class ColourState
        {
            internal Conversion Fill, Stroke;
            internal readonly Stack<(Conversion, Conversion)> Saved = new Stack<(Conversion, Conversion)>();
        }

        private void ConvertContent(PdfStream stream, PdfDictionary resources, ColourState state)
        {
            byte[] data;
            try { data = stream.GetDecodedData(); }
            catch (Exception ex) when (ex is Core.PdfException || ex is InvalidDataException || ex is IOException)
            {
                Note($"a content stream that could not be decoded was left in its own colour ({ex.Message})");
                return;
            }
            if (data == null || data.Length == 0) return;

            List<ContentStreamOperation> ops = ContentStreamParser.Parse(data);
            bool changed = false;
            for (int i = 0; i < ops.Count; i++)
            {
                ContentStreamOperation op = ops[i];
                string name = op.Operator;
                if (name == PdfOperators.Save)
                {
                    if (state.Saved.Count < PdfLimits.MaxGraphicsStateStackDepth) state.Saved.Push((state.Fill, state.Stroke));
                }
                else if (name == PdfOperators.Restore)
                {
                    if (state.Saved.Count > 0) (state.Fill, state.Stroke) = state.Saved.Pop();
                }
                else if (name == _target.SourceFillOperator || name == _target.SourceStrokeOperator)
                {
                    bool fill = name == _target.SourceFillOperator;
                    if (fill) state.Fill = null; else state.Stroke = null;
                    int count = _target.SourceDevice == PdfColorSpaceNames.DeviceRGB ? 3 : 4;
                    if (!_target.ConvertSourceDevice || op.Operands.Length < count) continue;
                    float[] converted = Convert(ToSrgb(_target.SourceSpace, Numbers(op.Operands, count)));
                    ops[i] = new ContentStreamOperation(fill ? _target.FillOperator : _target.StrokeOperator, Operands(converted, null));
                    changed = true;
                    _result.ColoursConverted++;
                }
                else if (name == PdfOperators.SetGrayFill || name == PdfOperators.SetCmykFill || name == PdfOperators.SetRgbFill) state.Fill = null;
                else if (name == PdfOperators.SetGrayStroke || name == PdfOperators.SetCmykStroke || name == PdfOperators.SetRgbStroke) state.Stroke = null;
                else if (name == PdfOperators.SetColorSpaceFill || name == PdfOperators.SetColorSpaceStroke)
                {
                    bool fill = name == PdfOperators.SetColorSpaceFill;
                    Conversion conversion = null;
                    if (op.Operands.Length > 0 && op.Operands[0] is PdfName space)
                    {
                        if (space.Value == _target.SourceDevice && _target.ConvertSourceDevice)
                        {
                            conversion = ConvertSpace(space, 0);
                            ops[i] = new ContentStreamOperation(op.Operator, new PdfObject[] { _target.Space });
                            changed = true;
                        }
                        else conversion = Lookup(resources, space.Value);
                    }
                    if (fill) state.Fill = conversion; else state.Stroke = conversion;
                }
                else if (name == PdfOperators.SetColorFill || name == PdfOperators.SetColorFillN
                         || name == PdfOperators.SetColorStroke || name == PdfOperators.SetColorStrokeN)
                {
                    bool fill = name == PdfOperators.SetColorFill || name == PdfOperators.SetColorFillN;
                    Conversion conversion = fill ? state.Fill : state.Stroke;
                    if (conversion == null || !conversion.Direct) continue;
                    PdfName pattern = op.Operands.Length > 0 ? op.Operands[op.Operands.Length - 1] as PdfName : null;
                    int count = op.Operands.Length - (pattern != null ? 1 : 0);
                    if (count <= 0) continue;   // a pattern alone: nothing in the base space to convert
                    float[] converted = Convert(ToSrgb(conversion.OperandSpace, Numbers(op.Operands, count)));
                    ops[i] = new ContentStreamOperation(op.Operator, Operands(converted, pattern));
                    changed = true;
                    _result.ColoursConverted++;
                }
                else if (name == PdfOperators.BeginInlineImage
                         && op.Operands.Length > 0 && op.Operands[0] is PdfStream inline
                         && (inline[PdfName.ColorSpace] ?? inline[PdfName.CS]) is PdfName inlineSpace)
                {
                    bool device = PdfColorSpaceNames.Expand(inlineSpace.Value) == _target.SourceDevice;
                    if ((device && _target.SourceDevice != PdfColorSpaceNames.DeviceRGB)
                        || (!device && Lookup(resources, inlineSpace.Value) is Conversion named && named.Direct))
                        Note("an inline image in a colour space the conversion changes was left in its own colour");
                }
            }
            if (!changed) return;

            using var ms = new MemoryStream();
            var writer = new PdfWriter(ms);
            foreach (ContentStreamOperation written in ops) ContentOperationWriter.Write(writer, written);
            byte[] encoded = FlateEncodeFilter.Encode(ms.ToArray());
            stream.Set(PdfName.Filter, PdfName.FlateDecode);
            stream.Remove(PdfName.DecodeParms);
            stream.Remove(PdfName.DL);
            stream.Set(PdfName.Length, new PdfNumber(encoded.Length));
            stream.RawData = encoded;
        }

        /// <summary>The conversion a <c>cs</c> operand's resource name stands for, recorded when its entry was replaced.</summary>
        private Conversion Lookup(PdfDictionary resources, string name)
        {
            if (resources == null) return null;
            PdfDictionary spaces = PdfResolve.Dict(_doc, resources, PdfName.ColorSpace);
            if (spaces == null) return null;
            return _byEntry.TryGetValue((spaces, name), out Conversion conversion) ? conversion : null;
        }

        // ------------------------------------------------------------------ colour

        /// <summary>Components in <paramref name="space"/> as the renderer shows them.</summary>
        private SKColor ToSrgb(PdfObject space, float[] components)
        {
            if (components == null) return SKColors.Black;
            string family = PdfColor.ResolveSpace(space, _doc).DeviceFamily;
            return IccColorTransform.ResolveAlternateColor(_doc, space, components, family, IccRenderingIntent.Perceptual);
        }

        /// <summary>An sRGB colour's components in the target (remembered per colour), rounded to four places.</summary>
        private float[] Convert(SKColor colour)
        {
            colour = colour.WithAlpha(255);
            uint key = (uint)colour;
            if (_converted.TryGetValue(key, out float[] done)) return done;
            float[] device = _target.FromSrgb(colour);
            for (int i = 0; i < device.Length; i++) device[i] = (float)Math.Round(Math.Max(0, Math.Min(1, device[i])), 4);
            _converted[key] = device;
            return device;
        }

        private PdfArray TargetArray(PdfObject space, float[] components)
        {
            var array = new PdfArray();
            foreach (float v in Convert(ToSrgb(space, components))) array.Add(new PdfNumber(v));
            return array;
        }

        /// <summary>The most inputs a function is sampled over: two points an input past it is more than 4 096 samples.</summary>
        private const int MaxSampledInputs = 12;

        /// <summary>
        /// <see cref="Sampled"/>, or null — <paramref name="what"/> noted as left in its own colour — when its
        /// function could not be run at every point: the conversion's function budget spent, or one
        /// evaluation refused.
        /// </summary>
        private PdfReference SampledOrNote(float[] domain, Func<float[], float[]> outputs, string what)
        {
            try { return Sampled(domain, outputs); }
            catch (Exception ex) when (ex is Core.Util.WorkBudgetExceededException || ex is InvalidOperationException)
            {
                Note($"{what} was left in its own colour: its function could not be sampled ({ex.Message})");
                return null;
            }
        }

        /// <summary>
        /// A sampled function (Type 0, §7.10.2) over <paramref name="domain"/> of the target's outputs in [0, 1], each
        /// input sampled at as many points as a 4 096-sample grid allows (256 for one input), 16 bits a sample,
        /// written as a new object.
        /// </summary>
        private PdfReference Sampled(float[] domain, Func<float[], float[]> outputs)
        {
            int inputs = domain.Length / 2, n = _target.Components;
            int size = inputs == 1 ? 256 : Math.Max(2, SaturatingCast.ToInt(Math.Floor(Math.Pow(4096, 1.0 / inputs))));
            long count = 1;
            for (int i = 0; i < inputs; i++) count *= size;
            var data = new byte[checked((int)(count * n * 2))];
            var at = new float[inputs];
            for (long s = 0; s < count; s++)
            {
                long rest = s;
                for (int i = 0; i < inputs; i++)   // the first input varies fastest
                {
                    int step = (int)(rest % size);
                    rest /= size;
                    at[i] = domain[2 * i] + (domain[2 * i + 1] - domain[2 * i]) * step / (size - 1);
                }
                float[] o = outputs(at);
                for (int c = 0; c < n; c++)
                {
                    int v = SaturatingCast.ToInt(Math.Round(Math.Max(0, Math.Min(1, o != null && o.Length > c ? o[c] : 0)) * 65535));
                    data[(s * n + c) * 2] = (byte)(v >> 8);
                    data[(s * n + c) * 2 + 1] = (byte)v;
                }
            }
            byte[] compressed = FlateEncodeFilter.Encode(data);
            var function = new PdfStream();
            function.Set(PdfName.FunctionType, new PdfNumber(0));
            function.Set(PdfName.Domain, Numbers(domain));
            var range = new float[n * 2];
            for (int c = 0; c < n; c++) range[2 * c + 1] = 1;
            function.Set(PdfName.Range, Numbers(range));
            var sizes = new PdfArray();
            for (int i = 0; i < inputs; i++) sizes.Add(new PdfNumber(size));
            function.Set(PdfName.Size, sizes);
            function.Set(PdfName.BitsPerSample, new PdfNumber(16));
            function.Set(PdfName.Filter, PdfName.FlateDecode);
            function.Set(PdfName.Length, new PdfNumber(compressed.Length));
            function.RawData = compressed;
            int number = _nextObject++;
            _extras[number] = function;
            return new PdfReference(number, 0);
        }

        private void Note(string message)
        {
            if (_notes.Count < 64) _notes.Add(message);
        }

        private float[] Numbers(PdfArray array)
        {
            if (array == null) return null;
            var values = new float[array.Count];
            for (int i = 0; i < array.Count; i++) values[i] = (float)((_doc.ResolveObject(array[i]) as PdfNumber)?.DoubleValue ?? 0);
            return values;
        }

        private static float[] Numbers(PdfObject[] operands, int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++) values[i] = (float)((operands[i] as PdfNumber)?.DoubleValue ?? 0);
            return values;
        }

        private static PdfArray Numbers(float[] values)
        {
            var array = new PdfArray();
            foreach (float v in values) array.Add(new PdfNumber(v));
            return array;
        }

        private static PdfObject[] Operands(float[] components, PdfName pattern)
        {
            var operands = new PdfObject[components.Length + (pattern != null ? 1 : 0)];
            for (int i = 0; i < components.Length; i++) operands[i] = new PdfNumber(components[i]);
            if (pattern != null) operands[components.Length] = pattern;
            return operands;
        }
    }
}
