// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// <c>image</c>, <c>imagemask</c> and <c>colorimage</c> (PLRM §4.10, §8.10), in both the
    /// operand form and the Level 2 dictionary form.
    ///
    /// <para><b>The image matrix is inverted before it is emitted, and that is the single easiest
    /// thing to get wrong here.</b> PostScript's matrix maps <i>user space onto image space</i>,
    /// where the image is a <c>width × height</c> grid with row 0 at the <b>top</b>. PDF's <c>Do</c>
    /// maps the <i>unit square onto user space</i> with the image's row 0 at the <b>top of the unit
    /// square</b>. So the emitted placement is the inverse of the PostScript matrix, composed with
    /// the unit-square-to-grid scale. Get it wrong and every image is mirrored or upside down —
    /// which is why it has a test asserting concrete <c>cm</c> numbers.</para>
    ///
    /// <para><b>Four operand shapes, ONE draw path.</b> <c>image</c>, <c>imagemask</c>,
    /// <c>colorimage</c> and the dictionary form differ only in how they are parsed; everything from
    /// the option gate to the emitted <c>Do</c> is identical, and lives once in
    /// <see cref="DrawSampledImage"/>. A second copy of that tail for one form is the mistake
    /// this shape exists to prevent — see CLAUDE.md on one implementation reached from both
    /// renderers.</para>
    /// </summary>
    internal static class PsImageOps
    {
        // Per conversion, like the operator-level one-shots in PsTextOps: reset by both public
        // entry points through ResetPerConversionState.
        [ThreadStatic] private static bool _approximationReported;

        internal static void ResetPerConversionState() => _approximationReported = false;

        public static void Register(PsSystemDict.Registrar op)
        {
            op("image",      i => Image(i, "image"));
            op("imagemask",  i => Image(i, "imagemask"));
            op("colorimage", i => Image(i, "colorimage"));
            op("shfill",     ShFill);
        }

        /// <summary>
        /// <c>dict shfill</c> (PLRM §4.9.3): the shading painted over the current clip, as PDF's
        /// <c>sh</c> under the same transform (<see cref="PsShading"/>). A dictionary this converter
        /// cannot carry is skipped and said; past <see cref="PsLimits.MaxShadings"/> fills a conversion
        /// paints no more and says so once — the count includes the refused, so the reports are
        /// bounded too.
        /// </summary>
        private static void ShFill(PsInterpreter i)
        {
            PsObject operand = i.Operands.Pop();
            if (operand.Type != PsType.Dictionary) throw new PsErrorException(PsErrorKind.TypeCheck, "shfill");

            PsGraphicsEngine g = i.Graphics;
            if (g == null) return;   // a tint transform's own child: nothing to paint

            if (++i.ShadingFills > PsLimits.MaxShadings)
            {
                if (i.ShadingFills == PsLimits.MaxShadings + 1)
                    PostScriptDiagnostics.Report("PsImageOps.shfill",
                        $"the {PsLimits.MaxShadings}-shading cap was reached; later shfills were skipped. Reported once.");
                return;
            }

            PdfObject shading = PsShading.TryConvert(i, operand.AsDict(), out string refusal,
                out string approximation, out double[] bbox);
            if (shading == null)
            {
                PsShading.DiscardFileSources(i, operand.AsDict());
                PostScriptDiagnostics.Report("PsImageOps.shfill", "a shading was skipped: " + refusal);
                return;
            }
            if (approximation != null && !_approximationReported)
            {
                _approximationReported = true;
                PostScriptDiagnostics.Report("PsImageOps.shfill",
                    $"a shading's colour space was carried as its device analogue: {approximation}. Reported once per conversion.");
            }

            g.Emitter.PaintShading(g.Resources.AddShading(shading));
            g.NoteShadingExtent(bbox);
        }

        /// <summary>
        /// Everything the draw path needs, however it was parsed.
        ///
        /// <para><c>Decode</c> stands for the operand form's polarity too.
        /// PLRM's <c>/Decode</c> and PDF's are the same concept with the same formula, and
        /// <c>imagemask</c>'s polarity is just the two-entry case of it — so carrying the array
        /// lets the dictionary form's <c>/Decode</c> and the operand form's polarity converge
        /// instead of becoming two rules.</para>
        /// </summary>
        private readonly struct PsImageRequest
        {
            public readonly int Width, Height, BitsPerComponent;
            public readonly PsMatrix Matrix;
            public readonly PsObject Source;

            /// <summary>
            /// One data source per component (<c>colorimage</c>'s <c>multi</c>, the dictionary form's
            /// <c>/MultipleDataSources</c>), or null for the one <see cref="Source"/>.
            /// </summary>
            public readonly PsObject[] Sources;
            public readonly bool IsMask;
            public readonly PsImageColorSpace Space;   // null for a mask
            public readonly double[] Decode;           // null = the PDF default for the space
            public readonly bool Interpolate;

            /// <summary>
            /// <c>ImageType 4</c>'s <c>/MaskColor</c> as PDF's colour-key <c>/Mask</c>: a minimum and a
            /// maximum per component, in sample values before <c>/Decode</c> — both sides compare the
            /// samples as read — or null.
            /// </summary>
            public readonly int[] MaskColor;

            /// <summary><c>ImageType 3</c>'s explicit mask, written as the image's stencil <c>/Mask</c>, or null.</summary>
            public readonly ExplicitMask Mask;

            public PsImageRequest(int width, int height, int bpc, in PsMatrix matrix,
                                  PsObject source, bool isMask, PsImageColorSpace space,
                                  double[] decode, bool interpolate = false, PsObject[] sources = null,
                                  int[] maskColor = null, ExplicitMask mask = null)
            {
                Width = width; Height = height; BitsPerComponent = bpc; Matrix = matrix;
                Source = source; IsMask = isMask; Space = space; Decode = decode;
                Interpolate = interpolate; Sources = sources; MaskColor = maskColor; Mask = mask;
            }

            /// <summary>The same image in another space and decode — its sources and masks kept.</summary>
            public PsImageRequest InSpace(PsImageColorSpace space, double[] decode) =>
                new(Width, Height, BitsPerComponent, Matrix, Source, IsMask, space, decode, Interpolate, Sources,
                    MaskColor, Mask);

            /// <summary>The same image's samples converted into <paramref name="space"/> at <paramref name="bpc"/> bits, the PDF default <c>/Decode</c>; its masks kept.</summary>
            public PsImageRequest ConvertedTo(PsImageColorSpace space, int bpc) =>
                new(Width, Height, bpc, Matrix, Source, IsMask, space, null, Interpolate, Sources, MaskColor, Mask);

            /// <summary>The same image with a colour-key or an explicit mask.</summary>
            public PsImageRequest WithMasks(int[] maskColor, ExplicitMask mask) =>
                new(Width, Height, BitsPerComponent, Matrix, Source, IsMask, Space, Decode, Interpolate, Sources,
                    maskColor, mask);

            /// <summary>Whether its samples come from procedures — directly, or through a filter one feeds — or several sources, gathered before they are drawn.</summary>
            public bool NeedsGathering =>
                Sources != null || (Source.Type == PsType.Array && Source.IsExecutable)
                || (Source.Type == PsType.File && Source.AsFile().ProcedureSource != null);

            public int Components => IsMask ? 1 : Space?.Components ?? 1;
        }

        /// <summary>
        /// An <c>ImageType 3</c> image's mask (PLRM §4.10.6): its size, its <c>/Decode</c> — the
        /// stencil's, as <c>imagemask</c> reads it, which is PDF's stencil <c>/Mask</c> reading too:
        /// the image is painted where the stencil would paint — how its samples travel, and its own
        /// source when they travel apart. It is mapped onto the data's rectangle, its own
        /// <c>/ImageMatrix</c> not read: interpreters do the same, and a PDF
        /// <c>/Mask</c> has no matrix of its own.
        /// </summary>
        private sealed class ExplicitMask
        {
            public int Width, Height, BitsPerComponent, Interleave;
            public double[] Decode;
            public PsObject Source = PsObject.Null;

            public long RowBytes => (Width + 7L) / 8;
        }

        // ------------------------------------------------------------------
        //  Parsing
        // ------------------------------------------------------------------

        private static void Image(PsInterpreter i, string op)
        {
            bool isMask = op == "imagemask";

            // Level 2 dictionary form: `dict image`. PLRM defines NO dictionary form of
            // `colorimage`, so the gate excludes it — without that, a dictionary left on the stack
            // under a `colorimage` would be parsed as an ImageType 1 image instead of reaching
            // colorimage's own operand parsing.
            if (op != "colorimage" && i.Operands.Count > 0 &&
                i.Operands.Peek().Type == PsType.Dictionary)
            {
                if (TryParseImageDictionary(i, op, isMask, out var fromDict))
                    DrawSampledImage(i, op, fromDict);
                return;
            }

            // The five-operand form is DeviceGray whatever the current colour space (PLRM §8.2, image:
            // "regardless of the current color space in the graphics state");
            // colorimage states its own count below, and only the dictionary form reads the current space.
            var space = isMask ? null : PsImageColorSpace.DeviceGray;
            int components = space?.Components ?? 1;

            if (op == "colorimage")
            {
                // width height bpc matrix src0 … srcN ncomp colorimage
                int ncomp = i.Operands.PopInt("colorimage");
                if (ncomp != 1 && ncomp != 3 && ncomp != 4)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "colorimage",
                        $"{ncomp} colour components");

                bool multipleSources = i.Operands.Count > 0
                                       && i.Operands.Peek().Type == PsType.Boolean
                                       && i.Operands.Peek().AsBool();
                if (i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.Boolean)
                    i.Operands.Pop();

                // colorimage states its own arity, which OVERRIDES the current colour space: the
                // operand is what the file said about this image.
                components = ncomp;
                space = SpaceForComponentCount(ncomp);

                if (multipleSources)
                {
                    // One data source per component, the last on top; gathered and interleaved
                    // before the samples are drawn (GatherSources).
                    var sources = new PsObject[ncomp];
                    for (int k = ncomp - 1; k >= 0; k--) sources[k] = i.Operands.Pop();
                    PopImageHeader(i, op, out int w, out int h, out int depth, out _, out var m);
                    DrawSampledImage(i, op, new PsImageRequest(w, h, depth, m, PsObject.Null, isMask: false, space,
                        null, sources: sources));
                    return;
                }
            }

            var source = i.Operands.Pop();
            PopImageHeader(i, op, out int width, out int height, out int bpc, out bool polarity,
                           out var matrix);

            if (isMask)
            {
                // For imagemask the "bpc" slot is actually the polarity boolean, read above.
                bpc = 1;
            }
            else if (space != null && space.Components != components)
            {
                space = SpaceForComponentCount(components);
            }

            DrawSampledImage(i, op, new PsImageRequest(
                width, height, bpc, matrix, source, isMask, space,
                isMask ? MaskDecode(polarity) : null));
        }

        /// <summary>
        /// The space a Level 2 <c>image</c> carries: whatever <c>setcolorspace</c> established, or
        /// DeviceGray when the program never called it.
        ///
        /// <para>Written without <c>?.</c> / <c>??</c> on purpose. <c>Graphics</c> being null (an
        /// interpreter with no engine) and <c>ImageSpace</c> being null (no <c>setcolorspace</c>)
        /// both mean DeviceGray here, but a <i>resolved</i> space that could not be carried is a
        /// third state that must survive to <see cref="DrawSampledImage"/> to be reported — a
        /// coalescing chain would turn it into DeviceGray and paint a Separation image as grey.
        /// </para>
        /// </summary>
        private static PsImageColorSpace CurrentImageSpace(PsInterpreter i)
        {
            if (i.Graphics == null) return PsImageColorSpace.DeviceGray;
            return i.Graphics.Gs.ImageSpace ?? PsImageColorSpace.DeviceGray;
        }

        private static PsImageColorSpace SpaceForComponentCount(int components) => PsImageColorSpace.Device(components);

        /// <summary>PLRM polarity as a <c>/Decode</c> array: true paints where the sample is 0.</summary>
        private static double[] MaskDecode(bool polarity) =>
            polarity ? null : new double[] { 1, 0 };

        /// <summary>
        /// <c>&lt;&lt; /ImageType 1 … &gt;&gt; image</c> — PLRM §4.10.5.
        ///
        /// <para>Returns false when the dictionary could not be used, having already popped it and
        /// reported why. The dictionary carries no colour-space key: an <c>ImageType 1</c> image is
        /// painted in the <i>current</i> colour space, which is exactly what makes
        /// <c>setcolorspace</c> load-bearing for this form.</para>
        /// </summary>
        private static bool TryParseImageDictionary(PsInterpreter i, string op, bool isMask,
                                                    out PsImageRequest request)
        {
            request = default;
            var dict = i.Operands.Pop().AsDict();

            // Type 3 is an image with an explicit mask, type 4 one with a colour key; imagemask
            // takes type 1 alone (PLRM §8.2), and any other type is reported.
            int imageType = IntEntry(dict, "ImageType", 1);
            if (imageType == 3 && !isMask) return TryParseExplicitMask(i, op, dict, out request);
            if (imageType != 1 && !(imageType == 4 && !isMask))
            {
                PostScriptDiagnostics.ReportUnimplemented($"{op} (/ImageType {imageType})");
                return false;
            }

            if (!TryParseImageFields(i, op, dict, isMask, out request)) return false;
            if (imageType != 4) return true;

            if (request.IsMask)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' has an ImageType 4 dictionary that is a stencil (/ImageMask true); the image was skipped");
                DiscardFileSource(i, request);
                return false;
            }
            if (!TryReadMaskColor(dict, op, request.Components, request.BitsPerComponent, out int[] ranges))
            {
                DiscardFileSource(i, request);
                return false;
            }
            request = request.WithMasks(ranges, mask: null);
            return true;
        }

        /// <summary>
        /// <c>/MaskColor</c> (PLRM §4.10.6, ImageType 4): one sample value per component, or a
        /// minimum and a maximum per component; a sample whose every component falls in its range is
        /// not painted. Read as PDF's colour-key <c>/Mask</c> array, each value held to the samples'
        /// range — a negative one to 0.
        /// </summary>
        private static bool TryReadMaskColor(PsDict dict, string op, int components, int bpc, out int[] ranges)
        {
            ranges = null;
            if (!dict.TryGetName("MaskColor", out var value) || value.Type != PsType.Array)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' has an ImageType 4 dictionary with no /MaskColor array; the image was skipped");
                return false;
            }

            var array = value.AsArray();
            if (array.Length != components && array.Length != 2 * components)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' /MaskColor has {array.Length} entries but {components} or {2 * components} were needed; the image was skipped");
                return false;
            }

            long max = bpc is > 0 and < 31 ? (1L << bpc) - 1 : int.MaxValue;
            ranges = new int[2 * components];
            for (int k = 0; k < array.Length; k++)
            {
                if (!array[k].IsNumber)
                {
                    PostScriptDiagnostics.Report("PsImageOps",
                        $"'{op}' /MaskColor holds a non-number; the image was skipped");
                    ranges = null;
                    return false;
                }
                int v = (int)Math.Max(0, Math.Min(max, array[k].AsInt()));
                if (array.Length == components) ranges[2 * k] = ranges[2 * k + 1] = v;
                else ranges[k] = v;
            }
            return true;
        }

        /// <summary>
        /// <c>&lt;&lt; /ImageType 3 /DataDict … /MaskDict … /InterleaveType n &gt;&gt; image</c> (PLRM
        /// §4.10.6): the data an <c>ImageType 1</c> dictionary in the current space; the mask its width,
        /// height, depth and <c>/Decode</c>, with a source of its own only under
        /// <c>InterleaveType 3</c>. The checks: under 1 the mask is
        /// the data's size and depth, under 2 and 3 one bit deep, under 2 one height a multiple of the
        /// other — a failure a <c>rangecheck</c>, every inline block consumed first.
        /// </summary>
        private static bool TryParseExplicitMask(PsInterpreter i, string op, PsDict dict, out PsImageRequest request)
        {
            request = default;
            if (!dict.TryGetName("DataDict", out var dataValue) || dataValue.Type != PsType.Dictionary
                || !dict.TryGetName("MaskDict", out var maskValue) || maskValue.Type != PsType.Dictionary)
                throw new PsErrorException(PsErrorKind.TypeCheck, op,
                    "an ImageType 3 dictionary needs a /DataDict and a /MaskDict dictionary");

            PsDict maskDict = maskValue.AsDict();
            int interleave = IntEntry(dict, "InterleaveType", 0);
            bool hasMaskSource = maskDict.TryGetName("DataSource", out var maskSource);
            var mask = new ExplicitMask
            {
                Width = IntEntry(maskDict, "Width", 0),
                Height = IntEntry(maskDict, "Height", 0),
                BitsPerComponent = IntEntry(maskDict, "BitsPerComponent", 1),
                Interleave = interleave,
                Decode = DecodeEntry(maskDict, op, 1),
                Source = interleave == 3 && hasMaskSource ? maskSource : PsObject.Null,
            };

            if (!TryParseImageFields(i, op, dataValue.AsDict(), isMask: false, out PsImageRequest data))
            {
                if (mask.Source.Type == PsType.File) mask.Source.AsFile().ReadInlineBlock(i, out _);
                return false;
            }
            request = data.WithMasks(maskColor: null, mask);

            string why = interleave switch
            {
                1 when mask.Width != data.Width || mask.Height != data.Height || mask.BitsPerComponent != data.BitsPerComponent
                    => "under InterleaveType 1 the mask has the data's width, height and BitsPerComponent",
                1 or 2 when data.Sources != null
                    => $"under InterleaveType {interleave} the mask travels in the data's one source",
                2 or 3 when mask.BitsPerComponent != 1
                    => "the mask's BitsPerComponent must be 1",
                2 when mask.Height <= 0 || data.Height <= 0
                       || (mask.Height % data.Height != 0 && data.Height % mask.Height != 0)
                    => "under InterleaveType 2 one height is a multiple of the other",
                3 when !hasMaskSource
                    => "under InterleaveType 3 the mask has its own /DataSource",
                1 or 2 or 3 => null,
                _ => $"InterleaveType {interleave} is not 1, 2 or 3",
            };
            if (why == null && (mask.Width <= 0 || mask.Height <= 0
                                || mask.Width > PdfLimits.MaxImageDimension || mask.Height > PdfLimits.MaxImageDimension
                                || (long)mask.Width * mask.Height > PdfLimits.MaxImagePixelArea))
                why = $"the mask is {mask.Width}x{mask.Height}";
            if (why != null)
            {
                DiscardFileSource(i, request);
                throw new PsErrorException(PsErrorKind.RangeCheck, op, why);
            }
            return true;
        }

        /// <summary>
        /// An <c>ImageType 1</c> dictionary's entries — the data of every type — as a request in the
        /// current colour space. False when the dictionary could not be used, having reported why.
        /// </summary>
        private static bool TryParseImageFields(PsInterpreter i, string op, PsDict dict, bool isMask,
                                                out PsImageRequest request)
        {
            request = default;

            // /ImageMask true makes an ImageType 1 dictionary a stencil, whichever operator ran.
            bool mask = isMask || BoolEntry(dict, "ImageMask", false);

            int width  = IntEntry(dict, "Width", 0);
            int height = IntEntry(dict, "Height", 0);
            int bpc    = mask ? 1 : IntEntry(dict, "BitsPerComponent", 8);

            if (!dict.TryGetName("ImageMatrix", out var matrixValue))
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' dictionary has no /ImageMatrix; the image was skipped");
                return false;
            }

            // Type-checked here rather than by casting: PsObject.AsArray() is an unchecked cast, so
            // `/ImageMatrix /Foo` would be an InvalidCastException — which escapes to
            // PostScriptConversion's catch-all and discards every mark already emitted, costing the
            // whole fragment instead of one image.
            if (matrixValue.Type != PsType.Array)
                throw new PsErrorException(PsErrorKind.TypeCheck, op, "/ImageMatrix is not an array");

            var matrix = PsMatrix.FromOperands(matrixValue.AsArray(), op);

            bool multiple = BoolEntry(dict, "MultipleDataSources", false);

            if (!dict.TryGetName("DataSource", out var source))
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' dictionary has no /DataSource; the image was skipped");
                return false;
            }

            var space = mask ? null : CurrentImageSpace(i);
            double[] decode = DecodeEntry(dict, op, mask ? 1 : space?.Components ?? 1);

            // /Interpolate is a rendering hint with the same meaning on both sides, so it passes
            // straight through rather than being dropped.
            bool interpolate = BoolEntry(dict, "Interpolate", false);

            // /MultipleDataSources: /DataSource is an array of one source per component.
            PsObject[] sources = null;
            if (multiple && !mask)
            {
                int count = space?.Components ?? 1;
                if (source.Type != PsType.Array || source.IsExecutable || source.AsArray().Length != count)
                    throw new PsErrorException(PsErrorKind.RangeCheck, op,
                        $"/MultipleDataSources needs an array of {count} data sources");
                sources = new PsObject[count];
                for (int k = 0; k < count; k++) sources[k] = source.AsArray()[k];
                source = PsObject.Null;
            }

            // A mask with no /Decode keeps PLRM's default polarity, which is PDF's default too.
            request = new PsImageRequest(width, height, bpc, matrix, source, mask, space, decode,
                                         interpolate, sources);
            return true;
        }

        private static double[] DecodeEntry(PsDict dict, string op, int components)
        {
            if (!dict.TryGetName("Decode", out var value)) return null;
            if (value.Type != PsType.Array)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' /Decode is not an array; the default was used");
                return null;
            }

            var array = value.AsArray();

            // Length-checked BEFORE the array is materialised: it is file-controlled, and a
            // /Decode of the wrong arity is a malformed image rather than one to guess at.
            if (array.Length != 2 * components)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' /Decode has {array.Length} entries but {2 * components} were needed " +
                    $"for {components}-component samples; the default was used");
                return null;
            }

            var result = new double[array.Length];
            for (int k = 0; k < array.Length; k++)
            {
                if (!array[k].IsNumber)
                {
                    PostScriptDiagnostics.Report("PsImageOps",
                        $"'{op}' /Decode holds a non-number; the default was used");
                    return null;
                }
                result[k] = array[k].Num;
            }
            return result;
        }

        private static int IntEntry(PsDict dict, string key, int fallback) =>
            dict.TryGetName(key, out var v) && v.IsNumber ? v.AsInt() : fallback;

        private static bool BoolEntry(PsDict dict, string key, bool fallback) =>
            dict.TryGetName(key, out var v) && v.Type == PsType.Boolean ? v.AsBool() : fallback;

        /// <summary>
        /// Pops <c>width height bpc matrix</c>, which all three operand forms share.
        ///
        /// <para><c>polarity</c> is <c>imagemask</c>'s third operand, occupying the slot the other
        /// two forms use for the bit depth. It falls back to <c>true</c> — PLRM's "0 paints" sense —
        /// so a malformed operand behaves like PDF's own default instead of inverting the mask.</para>
        /// </summary>
        private static void PopImageHeader(PsInterpreter i, string op, out int width, out int height,
                                           out int bpc, out bool polarity, out PsMatrix matrix)
        {
            var matrixArr = i.Operands.PopArray(op);
            matrix = PsMatrix.FromOperands(matrixArr, op);

            // imagemask's third operand is a boolean polarity, not a bit depth.
            var third = i.Operands.Pop();
            bpc = third.Type == PsType.Integer ? third.AsInt() : 1;
            polarity = third.Type != PsType.Boolean || third.AsBool();

            height = i.Operands.PopInt(op);
            width  = i.Operands.PopInt(op);

            // The depth is NOT normalised here: making anything but 1/2/4/8 "8, assumed" would read a
            // 16-bit image as twice the pixels of noise, and a 12-bit one too, each reported as a
            // guess rather than refused. DrawSampledImage judges the depth
            // for every operand shape at once, where the dictionary form's arrives too.
        }

        /// <summary>
        /// The depths PDF carries (ISO 32000-1 §8.9.5: 1, 2, 4, 8, and 16 from PDF 1.5). A PostScript
        /// image is written at its own depth, so any other is refused rather than approximated.
        /// </summary>
        private static bool IsSupportedDepth(int bpc) => bpc is 1 or 2 or 4 or 8 or 16;

        // ------------------------------------------------------------------
        //  The one draw path
        // ------------------------------------------------------------------

        private static void DrawSampledImage(PsInterpreter i, string op, in PsImageRequest request)
        {
            var g = i.Graphics;
            if (g == null) { DiscardFileSource(i, request); return; }

            if (!i.Options.AllowImages)
            {
                PostScriptDiagnostics.ReportUnimplemented(op);
                DiscardFileSource(i, request);
                return;
            }

            // A depth PDF has no spelling for is refused with its data consumed, not read at another
            // depth: 12-bit samples read as 8-bit are noise behind a report that calls them a guess.
            if (!IsSupportedDepth(request.BitsPerComponent))
            {
                PostScriptDiagnostics.ReportUnimplemented(
                    $"{op} ({request.BitsPerComponent} bits per component)");
                DiscardFileSource(i, request);
                return;
            }

            bool special = !request.IsMask && request.Space?.PdfColorSpace == null
                           && (request.Space?.Family == "Separation" || request.Space?.Family == "DeviceN");

            // A one-ink separation image of at most 8 bits is carried as an indexed image over its
            // alternate space, each sample value's tint through the program's transform once
            // (PsSpaceColor.SeparationImageSpace) — exact at every sample — the request redrawn with
            // that space and no /Decode, which the palette has taken in. Not for a JPEG, whose lossy
            // samples are no palette indices.
            if (special && request.Space.Family == "Separation"
                && !(request.Source.Type == PsType.File && request.Source.AsFile().CarriesJpeg)
                && PsSpaceColor.SeparationImageSpace(i, request.Space, request.BitsPerComponent, request.Decode) is PsImageColorSpace indexed)
            {
                DrawSampledImage(i, op, request.InSpace(indexed, decode: null));
                return;
            }

            // Any other separation or DeviceN image travels in its own space, the colorants' plates
            // written and the tint transform sampled into a function (PsSpaceColor.SpecialSpace); its
            // samples and /Decode as they are.
            if (special)
            {
                PdfArray carriedSpace = PsSpaceColor.SpecialSpace(i, request.Space, out string why, out int k);
                if (carriedSpace == null)
                {
                    PostScriptDiagnostics.Report("PsImageOps",
                        $"'{op}' is in the /{request.Space.Family} colour space and was skipped: {why}");
                    DiscardFileSource(i, request);
                    return;
                }
                var space = request.Space.CarriedAs(carriedSpace,
                    $"its tint transform is sampled at {k} tints a colorant and interpolated between them; the plates are written");
                DrawSampledImage(i, op, request.InSpace(space, request.Decode));
                return;
            }

            // A space that resolved but cannot be carried is REFUSED, not approximated. Emitting
            // /DeviceGray for it would paint tint values as grey levels, size the buffer wrongly,
            // and then report only that the payload was short — the wrong picture behind a
            // diagnostic naming the wrong cause. Its data is consumed, so an inline block is never
            // read as program.
            if (!request.IsMask && request.Space != null && request.Space.PdfColorSpace == null)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' is in the /{request.Space.Family} colour space and was skipped: " +
                    request.Space.CarryFailure);
                DiscardFileSource(i, request);
                return;
            }

            // A space carried APPROXIMATELY is drawn and said so — once per conversion, since a
            // page of CalRGB images has one cause. A wrong-ish picture behind a clean report is the
            // outcome this plugin's diagnostics exist to prevent.
            if (!request.IsMask && request.Space?.Approximation != null && !_approximationReported)
            {
                _approximationReported = true;
                string carried = request.Space.PdfColorSpace is PdfName n ? n.Value
                    : request.Space.PdfColorSpace is PdfArray a && a.Count > 0 && a[0] is PdfName family ? family.Value
                    : "a device space";
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' is in the /{request.Space.Family} colour space and was emitted as " +
                    $"/{carried}: {request.Space.Approximation}. Reported once per conversion.");
            }

            // Size-check BEFORE allocating, against Core's own image caps rather than new ones.
            if (request.Width <= 0 || request.Height <= 0)
            {
                DiscardFileSource(i, request);
                throw new PsErrorException(PsErrorKind.RangeCheck, op,
                    $"{request.Width}x{request.Height}");
            }

            if (request.Width > PdfLimits.MaxImageDimension ||
                request.Height > PdfLimits.MaxImageDimension ||
                (long)request.Width * request.Height > PdfLimits.MaxImagePixelArea)
            {
                DiscardFileSource(i, request);
                throw new PsErrorException(PsErrorKind.LimitCheck, op,
                    $"{request.Width}x{request.Height} exceeds the image size limits");
            }

            if (g.Resources.ImageCount >= PsLimits.MaxImageXObjects)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"the {PsLimits.MaxImageXObjects}-image cap was reached; this image was skipped");
                DiscardFileSource(i, request);
                return;
            }

            long expected = ((long)request.Width * request.Components * request.BitsPerComponent + 7)
                            / 8 * request.Height;
            i.ChargeAllocation(expected, op);
            i.Tick(request.Height);

            // An explicit mask's samples travel with the data's or beside them; both are gathered
            // and taken apart before the draw.
            if (request.Mask != null)
            {
                GatherWithMask(i, op, request, expected);
                return;
            }

            // Procedures, and one source per component, are gathered through the step loop first;
            // the draw below runs when the last of their bytes has come.
            if (request.NeedsGathering)
            {
                GatherSources(i, op, request, expected);
                return;
            }

            byte[] samples = ReadSamples(i, request.Source, expected, op, out bool jpeg);
            if (samples == null)
            {
                // A FILE source that could not be read has already said exactly why — unterminated
                // block, undelimited filter, decode failure — and relabelling it here would name the
                // wrong cause with full confidence; any other is no data source at all.
                if (request.Source.Type != PsType.File)
                    PostScriptDiagnostics.Report("PsImageOps",
                        $"'{op}' has a data source that is not a string, a file or a procedure; the image was skipped");
                return;
            }

            DrawSamples(i, op, request, samples, jpeg);
        }

        /// <summary>
        /// The image's samples — or its JPEG codestream — written as an XObject and drawn under the
        /// image matrix: what every data source comes to, read at once or gathered.
        /// </summary>
        // stencil: an explicit mask's samples, one bit deep at its own size, or null.
        private static void DrawSamples(PsInterpreter i, string op, in PsImageRequest given, byte[] samples, bool jpeg,
                                        byte[] stencil = null)
        {
            var g = i.Graphics;
            if (g == null) return;

            // In a CIE-based space the samples become the Lab the space is carried in, pixel by pixel.
            // A JPEG's samples are not here to convert, and a colour key names samples that will not
            // exist, so both are refused rather than drawn unconverted or unmasked.
            PsImageRequest request = given;
            if (!request.IsMask && request.Space?.Cie is PsCieSpace cie)
            {
                if (jpeg || request.MaskColor != null)
                {
                    PostScriptDiagnostics.Report("PsImageOps",
                        $"'{op}' is in the /{request.Space.Family} colour space, which is carried as Lab, and has " +
                        (jpeg ? "a /DCTDecode source whose samples are not decoded here" : "a /MaskColor over its own samples") +
                        "; the image was skipped");
                    return;
                }
                samples = CieToLab(i, op, cie, request, samples);
                request = request.ConvertedTo(PsImageColorSpace.LabOf(cie), 8);
            }

            // A JPEG is carried as the image's /Filter, so what the request says about the samples
            // must be what DCTDecode yields: eight bits per component, never a stencil. The block is
            // already consumed, so refusing here costs the image alone.
            if (jpeg && (request.IsMask || request.BitsPerComponent != 8))
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    $"'{op}' has a /DCTDecode data source but asks for " +
                    (request.IsMask ? "a stencil mask" : $"{request.BitsPerComponent} bits per component") +
                    ", which a JPEG cannot carry; the image was skipped");
                return;
            }

            // The inverse maps image space back to user space; then the unit square is scaled up to
            // the sample grid, because PDF's Do works on the unit square.
            //
            // Resolved BEFORE the XObject is registered, not after. Registering first left the
            // samples in /Resources for an image no `Do` ever named — every byte of a full-size
            // image embedded in the output and invisible, and one slot spent from the
            // MaxImageXObjects cap. A file of singular-matrix images was that bloat times 256.
            var inverse = request.Matrix.Invert();
            if (inverse == null)
            {
                PostScriptDiagnostics.Report("PsImageOps",
                    "the image matrix is singular; the image was skipped and not embedded");
                return;
            }

            var image = BuildImageStream(samples, request, jpeg, stencil);
            string resource = g.Resources.AddImage(image);

            // Unit square → image grid → user space. The middle step is the one worth spelling out:
            // PDF puts image row 0 at the TOP of the unit square (v = 1) while image space counts
            // rows downward from y = 0, so v is flipped and shifted by the height. Scaling by
            // 1/width instead of width here yields a correctly-oriented image at 1/width² of its
            // size — which looks like a placement bug rather than a scale bug.
            var placement = PsMatrix.Scale(request.Width, -request.Height)
                                    .Concat(PsMatrix.Translate(0, request.Height))
                                    .Concat(inverse.Value);

            // A stencil is painted in the current colour; an ordinary image carries its own.
            g.Emitter.DrawImage(resource, placement, request.IsMask ? g.Gs.Color : (PsColor?)null);
            g.NoteImageExtent(placement);
        }

        /// <summary>
        /// The sample bytes, or null when the source is a procedure.
        ///
        /// <para>A procedure source is called repeatedly to yield chunks, which means running the
        /// interpreter from inside an operator — re-entrancy the step loop is not built for. Refused
        /// as a unit and reported, rather than half-read.</para>
        /// </summary>
        /// <param name="i">The interpreter, for its allocation budget.</param>
        /// <param name="source">The image's data source operand: a file, a string, or a procedure.</param>
        /// <param name="expected">The sample bytes the image's geometry calls for; a shorter source is padded to it.</param>
        /// <param name="op">The operator, for the diagnostics.</param>
        /// <param name="jpeg">
        /// True when the bytes are a JPEG codestream to be carried under <c>/DCTDecode</c> rather
        /// than samples — a file source whose filter chain ends in <c>/DCTDecode</c>. Its length has
        /// nothing to do with the sample count, so it is neither padded nor judged short.
        /// </param>
        private static byte[] ReadSamples(PsInterpreter i, PsObject source, long expected, string op,
                                          out bool jpeg)
        {
            jpeg = false;

            // A file source — `currentfile /ASCIIHexDecode filter` and friends — reads the inline
            // block that follows THIS operator. It works only because execution is streamed: the
            // scanner is exactly here, with nothing past this operator tokenised yet.
            if (source.Type == PsType.File)
            {
                byte[] decoded = source.AsFile().ReadInlineBlock(i, out jpeg);
                if (decoded == null) return null;

                return jpeg ? decoded : PadOrReturn(decoded, expected, op);
            }

            if (source.Type != PsType.String) return null;

            return PadOrReturn(source.AsString().ToArray(), expected, op);
        }

        /// <summary>
        /// Consumes an inline data block belonging to an image that will NOT be drawn.
        ///
        /// <para>Every decline path has to do this. The block lives in the program's own byte
        /// stream, so leaving it there means the scanner tokenises image data as PostScript — the
        /// storm of garbage names this whole design exists to avoid — and one option flag or the
        /// 257th image would be enough to trigger it.</para>
        /// </summary>
        private static void DiscardFileSource(PsInterpreter i, in PsImageRequest request)
        {
            if (request.Source.Type == PsType.File)
                request.Source.AsFile().ReadInlineBlock(i, out _);
            if (request.Sources != null)
                foreach (PsObject source in request.Sources)
                    if (source.Type == PsType.File)
                        source.AsFile().ReadInlineBlock(i, out _);
            if (request.Mask != null && request.Mask.Source.Type == PsType.File)
                request.Mask.Source.AsFile().ReadInlineBlock(i, out _);
        }

        /// <summary>
        /// Gathers an image's samples from procedures, or from one source per component, then draws
        /// them (PLRM §4.10.2). Strings and files are read at once; each procedure is run through a
        /// <see cref="PsFrameKind.Callback"/> frame, round the sources in order, until its share is
        /// complete or it answers an empty string — every string it returns copied as it comes, since
        /// <c>{ currentfile s readhexstring pop }</c> answers the same buffer each time. Several sources'
        /// planes are interleaved sample by sample. A short source is padded and said, as a short string
        /// is.
        /// </summary>
        private static void GatherSources(PsInterpreter i, string op, PsImageRequest request, long expected)
        {
            PsObject[] sources = request.Sources ?? new[] { request.Source };
            int n = sources.Length;
            long plane = n == 1 ? expected
                : ((long)request.Width * request.BitsPerComponent + 7) / 8 * request.Height;
            var planes = new long[n];
            var refusals = new string[n];
            // One source may be a JPEG — a driver's procedure feeds one through /DCTDecode — carried as
            // the image's /Filter; among several it would be one component's plane.
            for (int k = 0; k < n; k++)
            {
                planes[k] = plane;
                refusals[k] = n == 1 ? null
                    : $"'{op}' has a /DCTDecode source among several; a JPEG cannot be one component's plane, so the image was skipped";
            }

            Gather(i, op, sources, planes, refusals, (at, data, jpegs) =>
            {
                byte[] samples = data[0];
                if (n > 1)
                {
                    at.ChargeAllocation(expected, op);
                    samples = Interleave(data, request.Width, request.Height, request.BitsPerComponent, expected);
                }
                DrawSamples(at, op, request, samples, jpeg: n == 1 && jpegs[0]);
            });
        }

        /// <summary>
        /// An <c>ImageType 3</c> image's data and mask, gathered and taken apart, then drawn with the
        /// mask as its stencil. <c>InterleaveType 1</c>: each pixel a mask sample at the data's depth —
        /// any value but 0 a 1 — then its components. <c>2</c>: whole rows of
        /// each, the next row the mask's while there is no more data than mask, proportionally — the
        /// usual plane order. <c>3</c>: the mask's own source, gathered before
        /// the data's, a JPEG allowed for the data's one source alone.
        /// </summary>
        private static void GatherWithMask(PsInterpreter i, string op, PsImageRequest request, long expected)
        {
            ExplicitMask mask = request.Mask;
            int n = request.Components, bpc = request.BitsPerComponent;
            long maskBytes = mask.RowBytes * mask.Height;
            i.ChargeAllocation(maskBytes, op);
            const string notAJpeg = "carries the mask, which a JPEG cannot carry; the image was skipped";

            if (mask.Interleave == 1)
            {
                long rows = ((long)request.Width * (n + 1) * bpc + 7) / 8 * request.Height;
                i.ChargeAllocation(rows, op);
                Gather(i, op, new[] { request.Source }, new[] { rows }, new[] { $"'{op}' has a /DCTDecode source that {notAJpeg}" },
                    (at, data, _) =>
                    {
                        SplitChunky(data[0], request.Width, request.Height, n, bpc, expected, out byte[] samples, out byte[] stencil);
                        DrawSamples(at, op, request, samples, jpeg: false, stencil);
                    });
                return;
            }

            if (mask.Interleave == 2)
            {
                long dataRow = ((long)request.Width * n * bpc + 7) / 8;
                long rows = maskBytes + dataRow * request.Height;
                i.ChargeAllocation(rows, op);
                Gather(i, op, new[] { request.Source }, new[] { rows }, new[] { $"'{op}' has a /DCTDecode source that {notAJpeg}" },
                    (at, data, _) =>
                    {
                        SplitRows(data[0], mask.RowBytes, mask.Height, dataRow, request.Height, expected, out byte[] samples, out byte[] stencil);
                        DrawSamples(at, op, request, samples, jpeg: false, stencil);
                    });
                return;
            }

            PsObject[] dataSources = request.Sources ?? new[] { request.Source };
            int count = dataSources.Length;
            var sources = new PsObject[count + 1];
            var planes = new long[count + 1];
            var refusals = new string[count + 1];
            sources[0] = mask.Source;
            planes[0] = maskBytes;
            refusals[0] = $"'{op}' has a /DCTDecode mask source; a stencil cannot be a JPEG, so the image was skipped";
            long plane = count == 1 ? expected : ((long)request.Width * bpc + 7) / 8 * request.Height;
            for (int k = 0; k < count; k++)
            {
                sources[k + 1] = dataSources[k];
                planes[k + 1] = plane;
                refusals[k + 1] = count == 1 ? null
                    : $"'{op}' has a /DCTDecode source among several; a JPEG cannot be one component's plane, so the image was skipped";
            }

            Gather(i, op, sources, planes, refusals, (at, data, jpegs) =>
            {
                byte[] samples = data[1];
                if (count > 1)
                {
                    at.ChargeAllocation(expected, op);
                    var components = new byte[count][];
                    Array.Copy(data, 1, components, 0, count);
                    samples = Interleave(components, request.Width, request.Height, bpc, expected);
                }
                DrawSamples(at, op, request, samples, jpegs[1], data[0]);
            });
        }

        /// <summary>
        /// <c>InterleaveType 1</c>'s rows taken apart: each pixel's first sample the mask's — any
        /// value but 0 sets its bit — and the rest the data's, every sample keeping its bits.
        /// </summary>
        private static void SplitChunky(byte[] chunky, int width, int height, int n, int bpc, long expected,
                                        out byte[] samples, out byte[] stencil)
        {
            long inRow = ((long)width * (n + 1) * bpc + 7) / 8;
            long outRow = ((long)width * n * bpc + 7) / 8;
            long maskRow = (width + 7L) / 8;
            samples = new byte[expected];
            stencil = new byte[maskRow * height];
            for (long y = 0; y < height; y++)
            {
                long inBit = y * inRow * 8, outBit = y * outRow * 8;
                for (long x = 0; x < width; x++)
                {
                    bool set = false;
                    for (int b = 0; b < bpc; b++, inBit++)
                        set |= ((chunky[inBit >> 3] >> (7 - (int)(inBit & 7))) & 1) != 0;
                    if (set) stencil[y * maskRow + (x >> 3)] |= (byte)(0x80 >> (int)(x & 7));
                    for (int b = 0; b < n * bpc; b++, inBit++, outBit++)
                        if (((chunky[inBit >> 3] >> (7 - (int)(inBit & 7))) & 1) != 0)
                            samples[outBit >> 3] |= (byte)(0x80 >> (int)(outBit & 7));
                }
            }
        }

        /// <summary>
        /// <c>InterleaveType 2</c>'s rows taken apart: a mask row next while
        /// <c>(dataRows + 1) × maskHeight &gt; maskRows × dataHeight</c> — the data never ahead of the
        /// mask, in the usual plane order — a data row otherwise, each row whole.
        /// </summary>
        private static void SplitRows(byte[] rows, long maskRow, int maskHeight, long dataRow, int dataHeight, long expected,
                                      out byte[] samples, out byte[] stencil)
        {
            samples = new byte[expected];
            stencil = new byte[maskRow * maskHeight];
            long at = 0;
            int maskY = 0, dataY = 0;
            while (maskY < maskHeight || dataY < dataHeight)
            {
                bool maskNext = dataY >= dataHeight
                                || (maskY < maskHeight && (long)(dataY + 1) * maskHeight > (long)maskY * dataHeight);
                if (maskNext)
                {
                    Array.Copy(rows, at, stencil, maskY * maskRow, maskRow);
                    at += maskRow;
                    maskY++;
                }
                else
                {
                    Array.Copy(rows, at, samples, dataY * dataRow, dataRow);
                    at += dataRow;
                    dataY++;
                }
            }
        }

        /// <summary>
        /// Gathers each source's bytes (PLRM §4.10.2), then hands them on. Strings and files are read
        /// at once, in order; each procedure is run through a <see cref="PsFrameKind.Callback"/> frame,
        /// round the sources in order, until its share is complete or it answers an empty string —
        /// every string it returns copied as it comes, since <c>{ currentfile s readhexstring pop }</c>
        /// answers the same buffer each time. A filter a procedure feeds (PLRM §3.13.1) runs the
        /// procedure the same way, its answers decoded through the filters — a JPEG's until its EOI,
        /// handed on as its codestream, or not at all when no whole one comes. A short source is padded
        /// and said, as a short string is.
        /// A file that is a JPEG is handed on as its codestream where <paramref name="jpegRefusals"/>
        /// has no entry for it; elsewhere the entry is reported and the rest of the files consumed.
        /// </summary>
        private static void Gather(PsInterpreter i, string op, PsObject[] sources, long[] planes, string[] jpegRefusals,
                                   Action<PsInterpreter, byte[][], bool[]> finish)
        {
            int n = sources.Length;
            var data = new byte[n][];
            var filled = new long[n];
            var ended = new bool[n];
            var jpegs = new bool[n];
            bool procedures = false;

            // A filter fed by a procedure: what the procedure answers is kept raw and decoded through
            // the chain, tried each time the raw bytes have doubled — linear in them — until the
            // procedure answers an empty string or the decoded bytes fill the plane. A driver's
            // procedure that cycles through its strings ends so; the allocation budget ends one that
            // never fills it.
            var fed = new PsFile[n];
            var raw = new System.IO.MemoryStream[n];
            var nextTrial = new long[n];

            for (int k = 0; k < n; k++)
            {
                PsObject source = sources[k];
                if (source.Type == PsType.Array && source.IsExecutable)
                {
                    data[k] = new byte[planes[k]];
                    procedures = true;
                    continue;
                }
                if (source.Type == PsType.File && source.AsFile().ProcedureSource != null)
                {
                    if (source.AsFile().CarriesJpeg && jpegRefusals[k] != null)
                    {
                        PostScriptDiagnostics.Report("PsImageOps", jpegRefusals[k]);
                        for (int rest = k + 1; rest < n; rest++)
                            if (sources[rest].Type == PsType.File)
                                sources[rest].AsFile().ReadInlineBlock(i, out _);
                        return;
                    }
                    fed[k] = source.AsFile();
                    raw[k] = new System.IO.MemoryStream();
                    data[k] = new byte[planes[k]];
                    procedures = true;
                    continue;
                }

                byte[] bytes = null;
                if (source.Type == PsType.String) bytes = source.AsString().ToArray();
                else if (source.Type == PsType.File)
                {
                    bytes = source.AsFile().ReadInlineBlock(i, out jpegs[k]);
                    if (jpegs[k] && jpegRefusals[k] != null)
                    {
                        PostScriptDiagnostics.Report("PsImageOps", jpegRefusals[k]);
                        bytes = null;
                    }
                }
                else
                    throw new PsErrorException(PsErrorKind.TypeCheck, op, "a data source is not a string, a file or a procedure");

                if (bytes == null)
                {
                    for (int rest = k + 1; rest < n; rest++)
                        if (sources[rest].Type == PsType.File)
                            sources[rest].AsFile().ReadInlineBlock(i, out _);
                    return;
                }
                if (jpegs[k])
                {
                    data[k] = bytes;
                    filled[k] = planes[k];
                    ended[k] = true;
                    continue;
                }
                data[k] = new byte[planes[k]];
                long take = Math.Min(bytes.Length, planes[k]);
                Array.Copy(bytes, data[k], take);
                filled[k] = take;
                ended[k] = true;
            }

            if (!procedures)
            {
                Finish(i);
                return;
            }

            int turn = -1;
            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Callback,
                Advance = interpreter =>
                {
                    if (turn >= 0)
                    {
                        PsObject answer = interpreter.Operands.Pop();
                        if (answer.Type != PsType.String)
                            throw new PsErrorException(PsErrorKind.TypeCheck, op, "a data source procedure did not answer a string");
                        PsString chunk = answer.AsString();
                        if (chunk.Length == 0) ended[turn] = true;
                        else if (fed[turn] != null)
                        {
                            interpreter.ChargeAllocation(chunk.Length, op);
                            raw[turn].Write(chunk.Bytes, chunk.Offset, chunk.Length);
                            if (raw[turn].Length >= nextTrial[turn])
                            {
                                nextTrial[turn] = 2 * raw[turn].Length;
                                byte[] trial = fed[turn].DecodeProcedureData(raw[turn].ToArray(), interpreter, report: false,
                                                                             out bool codestream, out bool noJpeg);
                                // A JPEG is whole at its EOI, and never one once its start says not;
                                // samples when the plane is full.
                                if (noJpeg || (trial != null && (codestream || trial.Length >= planes[turn]))) ended[turn] = true;
                            }
                        }
                        else
                        {
                            long take = Math.Min(chunk.Length, planes[turn] - filled[turn]);
                            Array.Copy(chunk.Bytes, chunk.Offset, data[turn], filled[turn], take);
                            filled[turn] += take;
                            if (filled[turn] >= planes[turn]) ended[turn] = true;
                        }
                    }
                    for (int step = 0; step < n; step++)
                    {
                        int k = (turn + 1 + step) % n;
                        if (!ended[k] && (sources[k].Type == PsType.Array || fed[k] != null))
                        {
                            turn = k;
                            return fed[k] != null ? fed[k].ProcedureSource : sources[k].AsArray();
                        }
                    }
                    Finish(interpreter);
                    return null;
                },
            });

            void Finish(PsInterpreter at)
            {
                for (int k = 0; k < n; k++)
                {
                    if (fed[k] == null) continue;
                    // Said only when the image comes up short: gathering stops once the samples are all
                    // there, which can be before a stream's own end (Flate's checksum trailer).
                    byte[] whole = raw[k].ToArray();
                    byte[] decoded = fed[k].DecodeProcedureData(whole, at, report: false, out bool codestream, out _);
                    if (decoded != null && codestream)
                    {
                        // Carried as the image's /DCTDecode stream, as an inline JPEG is.
                        data[k] = decoded;
                        filled[k] = planes[k];
                        jpegs[k] = true;
                        continue;
                    }
                    if (fed[k].CarriesJpeg)
                    {
                        // No whole codestream: said, and no image — padding is no picture.
                        fed[k].DecodeProcedureData(whole, at, report: true, out _, out _);
                        return;
                    }
                    if (decoded == null || decoded.Length < planes[k])
                        decoded = fed[k].DecodeProcedureData(whole, at, report: true, out _, out _) ?? decoded;
                    if (decoded == null) continue;
                    long take = Math.Min(decoded.Length, planes[k]);
                    Array.Copy(decoded, data[k], take);
                    filled[k] = take;
                }
                long got = 0, needed = 0;
                for (int k = 0; k < n; k++) { got += filled[k]; needed += planes[k]; }
                if (got < needed)
                    PostScriptDiagnostics.Report("PsImageOps",
                        $"'{op}' data sources gave {got} bytes but {needed} were needed; the remainder was padded");
                finish(at, data, jpegs);
            }
        }

        /// <summary>
        /// Several sources' planes as one image's samples: row by row, each pixel's components in
        /// source order, every sample keeping its bits — so a plane of 1-, 2- or 4-bit samples
        /// interleaves without being widened.
        /// </summary>
        private static byte[] Interleave(byte[][] planes, int width, int height, int bpc, long expected)
        {
            int n = planes.Length;
            long planeRow = ((long)width * bpc + 7) / 8;
            long outRow = ((long)width * n * bpc + 7) / 8;
            var result = new byte[expected];
            if (bpc % 8 == 0)
            {
                int size = bpc / 8;
                long at = 0;
                for (long y = 0; y < height; y++)
                    for (long x = 0; x < width; x++)
                        for (int k = 0; k < n; k++, at += size)
                            Array.Copy(planes[k], y * planeRow + x * size, result, at, size);
                return result;
            }
            for (long y = 0; y < height; y++)
            {
                long outBit = y * outRow * 8;
                for (long x = 0; x < width; x++)
                    for (int k = 0; k < n; k++)
                    {
                        long inBit = y * planeRow * 8 + x * bpc;
                        for (int b = 0; b < bpc; b++, inBit++, outBit++)
                        {
                            int bit = (planes[k][inBit >> 3] >> (7 - (int)(inBit & 7))) & 1;
                            if (bit != 0) result[outBit >> 3] |= (byte)(0x80 >> (int)(outBit & 7));
                        }
                    }
            }
            return result;
        }

        /// <summary>
        /// A CIE-based image's samples as Lab bytes, three a pixel: each component read at its depth,
        /// through the image's <c>/Decode</c> (the space's ranges by default, PLRM §4.10.5), evaluated
        /// to XYZ and taken to Lab (<see cref="PsCieSpace"/>). Charged a tick per 64 pixels and its bytes.
        /// </summary>
        private static byte[] CieToLab(PsInterpreter i, string op, PsCieSpace cie, in PsImageRequest request, byte[] samples)
        {
            int n = cie.Components, bpc = request.BitsPerComponent;
            long pixels = (long)request.Width * request.Height;
            i.ChargeAllocation(pixels * 3, op);
            i.Tick(1 + pixels / 64);
            double[] decode = request.Decode ?? cie.Range;
            double max = (1L << bpc) - 1;
            long row = ((long)request.Width * n * bpc + 7) / 8;
            var lab = new byte[pixels * 3];
            var components = new double[n];
            for (long y = 0; y < request.Height; y++)
            {
                long bit = y * row * 8;
                for (long x = 0; x < request.Width; x++)
                {
                    for (int k = 0; k < n; k++, bit += bpc)
                    {
                        long raw = 0;
                        for (int b = 0; b < bpc; b++)
                        {
                            long at = bit + b;
                            raw = (raw << 1) | (uint)((samples[at >> 3] >> (7 - (int)(at & 7))) & 1);
                        }
                        components[k] = decode[2 * k] + raw / max * (decode[2 * k + 1] - decode[2 * k]);
                    }
                    cie.ToLabBytes(components, lab, (y * request.Width + x) * 3);
                }
            }
            return lab;
        }

        /// <summary>
        /// A short source is a malformed image; pad rather than throw, so the rest of the
        /// illustration survives — and say so.
        /// </summary>
        private static byte[] PadOrReturn(byte[] bytes, long expected, string op)
        {
            if (bytes.Length >= expected) return bytes;

            PostScriptDiagnostics.Report("PsImageOps",
                $"'{op}' data source holds {bytes.Length} bytes but {expected} were needed; " +
                "the remainder was padded");

            var padded = new byte[expected];
            Array.Copy(bytes, padded, bytes.Length);
            return padded;
        }

        /// <param name="samples">The image's sample bytes, or its JPEG codestream.</param>
        /// <param name="request">The image as parsed, whichever operand shape it came in.</param>
        /// <param name="jpeg">
        /// The samples are a JPEG codestream: written as they are under <c>/Filter /DCTDecode</c>,
        /// as a PDF writer embeds a JPEG, so the reader's own DCT decoder yields the samples —
        /// including the Adobe APP14 inversion the PostScript <c>/Decode</c> array was written for.
        /// </param>
        /// <param name="stencil">An explicit mask's samples, written as the image's stencil <c>/Mask</c>, or null.</param>
        private static PdfStream BuildImageStream(byte[] samples, in PsImageRequest request, bool jpeg, byte[] stencil)
        {
            var image = new PdfStream();
            image[PdfName.Type]             = PdfName.XObject;
            image[PdfName.Subtype]          = PdfName.Image;
            image[PdfName.Width]            = new PdfNumber(request.Width);
            image[PdfName.Height]           = new PdfNumber(request.Height);
            image[PdfName.BitsPerComponent] = new PdfNumber(request.BitsPerComponent);

            if (request.IsMask)
            {
                image[PdfName.ImageMask] = PdfBoolean.True;

                // The polarity operand chooses WHICH sample value paints, and PDF spells that
                // choice as /Decode. PLRM: polarity true paints where the sample is 0, which is
                // exactly PDF's default /Decode [0 1] — so true needs no entry. Polarity false
                // paints where the sample is 1, which needs the array reversed. Dropping the
                // operand does not "default"; it inverts every mask written the other way round.
                WriteDecode(image, request.Decode);
            }
            else
            {
                image[PdfName.ColorSpace] =
                    request.Space?.PdfColorSpace ?? PdfName.DeviceGray;

                WriteDecode(image, request.Decode);
            }

            if (request.Interpolate) image[PdfName.Interpolate] = PdfBoolean.True;

            // ISO 32000-2 §8.9.6.4: a colour key is the ranges of pre-decode samples not painted.
            if (request.MaskColor != null)
            {
                var ranges = new PdfArray();
                foreach (int v in request.MaskColor) ranges.Add(new PdfNumber(v));
                image[PdfName.Mask] = ranges;
            }

            // §8.9.6.3: a stencil image whose painted areas are where the image is painted —
            // imagemask's reading, which is the PostScript mask's — mapped onto the image's square.
            if (stencil != null && request.Mask != null)
            {
                var mask = new PdfStream();
                mask[PdfName.Type]             = PdfName.XObject;
                mask[PdfName.Subtype]          = PdfName.Image;
                mask[PdfName.Width]            = new PdfNumber(request.Mask.Width);
                mask[PdfName.Height]           = new PdfNumber(request.Mask.Height);
                mask[PdfName.BitsPerComponent] = new PdfNumber(1);
                mask[PdfName.ImageMask]        = PdfBoolean.True;
                WriteDecode(mask, request.Mask.Decode);
                mask.RawData = stencil;
                image[PdfName.Mask] = mask;
            }

            // No /Filter for raw samples: compressing them here would duplicate what the document
            // writer's own compression already does on the way to the file. A JPEG keeps its own.
            if (jpeg) image[PdfName.Filter] = PdfName.DCTDecode;
            image.RawData = samples;
            return image;
        }

        /// <summary>
        /// Writes <c>/Decode</c> when there is one. A null array means "the PDF default for this
        /// space", which is what the majority of images want and what keeps the emitted dictionary
        /// small.
        /// </summary>
        private static void WriteDecode(PdfStream image, double[] decode)
        {
            if (decode == null || decode.Length == 0) return;

            var array = new PdfArray();
            foreach (double v in decode) array.Add(new PdfNumber(v));
            image[PdfName.Decode] = array;
        }

    }
}
