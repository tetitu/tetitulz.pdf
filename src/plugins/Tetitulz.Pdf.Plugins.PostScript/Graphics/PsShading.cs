// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// A Level 3 shading dictionary (PLRM §4.9.3) as the PDF shading <c>sh</c> paints (ISO 32000-2
    /// §8.7.4.5): the two were drawn up together and agree key for key, and so do their function
    /// dictionaries (PLRM §3.10, ISO 32000-2 §7.10). What PostScript says differently is carried:
    /// a <c>/DataSource</c> string — a sampled function's, a mesh's — becomes the stream's data; a mesh
    /// given as an array of numbers is packed (8-bit flags, 32-bit coordinates, 16-bit colour
    /// components, a <c>/Decode</c> spanning the values); a <c>/Separation</c> or <c>/DeviceN</c> space
    /// is written as one, its tint transform sampled into a FunctionType 0
    /// (<see cref="PsSpaceColor.SpecialSpace"/>), so the plates are written — unlike a separation
    /// fill's, which is painted in its alternate.
    ///
    /// <para>Refused, with the reason: a separation space whose transform cannot be sampled, a
    /// space the image path cannot carry, a data source that is a procedure, a FunctionType other than
    /// 0, 2 and 3, more than <see cref="PsLimits.MaxShadingFunctions"/> function dictionaries, and an
    /// entry of the wrong shape. <c>/Background</c> is not carried: <c>shfill</c> ignores it, and so
    /// does <c>sh</c>.</para>
    /// </summary>
    internal static class PsShading
    {
        /// <summary>
        /// The PDF shading for <paramref name="shading"/> — a dictionary, or a stream for a mesh — or
        /// null with <paramref name="refusal"/> saying why. <paramref name="approximation"/> names a
        /// space carried as its device analogue, or a several-ink transform sampled; <paramref name="bbox"/> is the shading's own
        /// <c>/BBox</c>, for the page's extent.
        /// </summary>
        internal static PdfObject TryConvert(PsInterpreter i, PsDict shading,
            out string refusal, out string approximation, out double[] bbox)
        {
            approximation = null;
            bbox = null;

            if (!shading.TryGetName("ShadingType", out PsObject typeObject) || !IsInteger(typeObject)
                || typeObject.Num < 1 || typeObject.Num > 7)
                return Refuse(out refusal, "its /ShadingType is missing or not an integer from 1 to 7");
            int type = typeObject.AsInt();

            if (!shading.TryGetName("ColorSpace", out PsObject spaceObject))
                return Refuse(out refusal, "it has no /ColorSpace");
            PsImageColorSpace space = PsImageColorSpace.Resolve(spaceObject, i);
            PdfObject pdfSpace = SpaceFor(i, space, out refusal, out string sampled);
            if (pdfSpace == null) return null;
            approximation = space.Approximation ?? space.Alternate?.Approximation ?? sampled;

            PdfDictionary target = type >= 4 ? new PdfStream() : new PdfDictionary();
            target[PdfName.ShadingType] = new PdfNumber(type);
            target[PdfName.ColorSpace] = pdfSpace;

            if (!Numbers(i, shading, "BBox", 4, out bbox)) return Refuse(out refusal, "its /BBox is not four numbers");
            if (bbox != null) target[PdfName.BBox] = Array(bbox);
            if (shading.TryGetName("AntiAlias", out PsObject antiAlias))
            {
                if (antiAlias.Type != PsType.Boolean) return Refuse(out refusal, "its /AntiAlias is not a boolean");
                target[PdfName.AntiAlias] = antiAlias.AsBool() ? PdfBoolean.True : PdfBoolean.False;
            }

            bool hasFunction = shading.TryGetName("Function", out PsObject functionObject);
            if (hasFunction)
            {
                int budget = PsLimits.MaxShadingFunctions;
                PdfObject function = Function(i, functionObject, topLevel: true, ref budget, out refusal);
                if (function == null) return null;
                target[PdfName.Function] = function;
            }
            else if (type <= 3)
                return Refuse(out refusal, $"a type {type} shading needs a /Function");

            switch (type)
            {
                case 1:
                    if (!Copy(i, shading, "Domain", PdfName.Domain, 4, target)) return Refuse(out refusal, "its /Domain is not four numbers");
                    if (!Copy(i, shading, "Matrix", PdfName.Matrix, 6, target)) return Refuse(out refusal, "its /Matrix is not six numbers");
                    return target;
                case 2:
                case 3:
                {
                    int coords = type == 2 ? 4 : 6;
                    if (!shading.TryGetName("Coords", out _) || !Copy(i, shading, "Coords", PdfName.Coords, coords, target))
                        return Refuse(out refusal, $"its /Coords is not {coords} numbers");
                    if (!Copy(i, shading, "Domain", PdfName.Domain, 2, target)) return Refuse(out refusal, "its /Domain is not two numbers");
                    if (shading.TryGetName("Extend", out PsObject extend))
                    {
                        if (extend.Type != PsType.Array || extend.AsArray().Length != 2
                            || extend.AsArray()[0].Type != PsType.Boolean || extend.AsArray()[1].Type != PsType.Boolean)
                            return Refuse(out refusal, "its /Extend is not two booleans");
                        var pair = new PdfArray();
                        pair.Add(extend.AsArray()[0].AsBool() ? PdfBoolean.True : PdfBoolean.False);
                        pair.Add(extend.AsArray()[1].AsBool() ? PdfBoolean.True : PdfBoolean.False);
                        target[PdfName.Extend] = pair;
                    }
                    return target;
                }
                default:
                    return Mesh(i, type, shading, (PdfStream)target, hasFunction ? 1 : space.SetColorArity, out refusal);
            }
        }

        // ------------------------------------------------------------------
        //  Colour space
        // ------------------------------------------------------------------

        /// <summary>
        /// The shading's space as PDF writes it; <paramref name="sampled"/> says when a several-ink
        /// transform was sampled at fewer than <see cref="PsSpaceColor.SeparationSamples"/> points a colorant.
        /// </summary>
        private static PdfObject SpaceFor(PsInterpreter i, PsImageColorSpace space, out string refusal, out string sampled)
        {
            refusal = null;
            sampled = null;
            if (space == null) return Refuse(out refusal, "its /ColorSpace is not a colour space");

            // A CIE-based space is carried as Lab, which its function's outputs are not: they are the
            // space's own components, and a shading has no samples to convert one by one.
            if (space.Cie != null)
                return Refuse(out refusal, $"its /{space.Family} space is carried as Lab, and a shading's function answers its own components");
            switch (space.Family)
            {
                case PdfColorSpaceNames.Separation:
                case PdfColorSpaceNames.DeviceN:
                {
                    PdfArray carried = PsSpaceColor.SpecialSpace(i, space, out refusal, out int k);
                    if (carried != null && k < PsSpaceColor.SeparationSamples)
                        sampled = $"a /DeviceN space's tint transform is sampled at {k} points a colorant and interpolated between them";
                    return carried;
                }
                case PdfColorSpaceNames.Pattern:
                    return Refuse(out refusal, "a shading cannot be in a Pattern space");
            }
            if (space.PdfColorSpace == null)
                return Refuse(out refusal, $"its /{space.Family} colour space cannot be carried: {space.CarryFailure}");
            return space.PdfColorSpace;
        }

        // ------------------------------------------------------------------
        //  Functions
        // ------------------------------------------------------------------

        /// <summary>
        /// A function dictionary — or, at the shading's own <c>/Function</c>, an array of them —
        /// as the PDF function it is, every dictionary spending one of <paramref name="budget"/>, so a
        /// stitching function's nesting is bounded by the same count as its breadth.
        /// </summary>
        private static PdfObject Function(PsInterpreter i, PsObject function, bool topLevel, ref int budget, out string refusal)
        {
            refusal = null;
            if (topLevel && function.Type == PsType.Array && !function.IsExecutable)
            {
                PsArray functions = function.AsArray();
                if (functions.Length == 0) return Refuse(out refusal, "its /Function array is empty");
                var array = new PdfArray();
                for (int k = 0; k < functions.Length; k++)
                {
                    PdfObject one = Function(i, functions[k], topLevel: false, ref budget, out refusal);
                    if (one == null) return null;
                    array.Add(one);
                }
                return array;
            }

            if (function.Type != PsType.Dictionary)
                return Refuse(out refusal, "a /Function is not a function dictionary");
            if (--budget < 0)
                return Refuse(out refusal, $"it holds more than {PsLimits.MaxShadingFunctions} function dictionaries");

            PsDict d = function.AsDict();
            if (!d.TryGetName("FunctionType", out PsObject typeObject) || !IsInteger(typeObject))
                return Refuse(out refusal, "a function has no integer /FunctionType");
            int type = typeObject.AsInt();
            if (type != 0 && type != 2 && type != 3)
                return Refuse(out refusal, $"a function is of FunctionType {type}; PostScript defines 0, 2 and 3");

            PdfDictionary target = type == 0 ? new PdfStream() : new PdfDictionary();
            target[PdfName.FunctionType] = new PdfNumber(type);
            if (!d.TryGetName("Domain", out _) || !CopyEven(i, d, "Domain", PdfName.Domain, target))
                return Refuse(out refusal, "a function's /Domain is missing or not pairs of numbers");
            if (!CopyEven(i, d, "Range", PdfName.Range, target))
                return Refuse(out refusal, "a function's /Range is not pairs of numbers");

            switch (type)
            {
                case 0:
                {
                    if (!d.TryGetName("Range", out _)) return Refuse(out refusal, "a sampled function has no /Range");
                    if (!d.TryGetName("Size", out PsObject size) || !AllPositiveIntegers(size))
                        return Refuse(out refusal, "a sampled function's /Size is missing or not positive integers");
                    target[PdfName.Size] = Array(Charged(i, PsNumeric.Finite(size.AsArray())));
                    if (!d.TryGetName("BitsPerSample", out PsObject bits) || !IsInteger(bits)
                        || System.Array.IndexOf(SampleWidths, bits.AsInt()) < 0)
                        return Refuse(out refusal, "a sampled function's /BitsPerSample is not 1, 2, 4, 8, 12, 16, 24 or 32");
                    target[PdfName.BitsPerSample] = new PdfNumber(bits.AsInt());
                    if (d.TryGetName("Order", out PsObject order))
                    {
                        if (!IsInteger(order) || (order.AsInt() != 1 && order.AsInt() != 3))
                            return Refuse(out refusal, "a sampled function's /Order is not 1 or 3");
                        target[PdfName.Order] = new PdfNumber(order.AsInt());
                    }
                    if (!CopyEven(i, d, "Encode", PdfName.Encode, target) || !CopyEven(i, d, "Decode", PdfName.Decode, target))
                        return Refuse(out refusal, "a sampled function's /Encode or /Decode is not pairs of numbers");
                    if (!d.TryGetName("DataSource", out PsObject source))
                        return Refuse(out refusal, "a sampled function has no /DataSource");
                    byte[] data = DataOf(i, source, out refusal);
                    if (data == null) return null;
                    ((PdfStream)target).RawData = data;
                    return target;
                }
                case 2:
                    if (!Copy(i, d, "C0", PdfName.C0, -1, target) || !Copy(i, d, "C1", PdfName.C1, -1, target))
                        return Refuse(out refusal, "an exponential function's /C0 or /C1 is not numbers");
                    if (!d.TryGetName("N", out PsObject exponent) || !exponent.IsNumber)
                        return Refuse(out refusal, "an exponential function has no numeric /N");
                    target[PdfName.N] = new PdfNumber(exponent.Num);
                    return target;
                default:
                {
                    if (!d.TryGetName("Functions", out PsObject parts) || parts.Type != PsType.Array || parts.AsArray().Length == 0)
                        return Refuse(out refusal, "a stitching function's /Functions is missing or empty");
                    var array = new PdfArray();
                    PsArray list = parts.AsArray();
                    for (int k = 0; k < list.Length; k++)
                    {
                        PdfObject one = Function(i, list[k], topLevel: false, ref budget, out refusal);
                        if (one == null) return null;
                        array.Add(one);
                    }
                    target[PdfName.Functions] = array;
                    if (!d.TryGetName("Bounds", out _) || !Copy(i, d, "Bounds", PdfName.Bounds, list.Length - 1, target))
                        return Refuse(out refusal, "a stitching function's /Bounds is not one number fewer than its functions");
                    if (!d.TryGetName("Encode", out _) || !Copy(i, d, "Encode", PdfName.Encode, 2 * list.Length, target))
                        return Refuse(out refusal, "a stitching function's /Encode is not two numbers a function");
                    return target;
                }
            }
        }

        private static readonly int[] SampleWidths = { 1, 2, 4, 8, 12, 16, 24, 32 };

        // ------------------------------------------------------------------
        //  Meshes
        // ------------------------------------------------------------------

        private static PdfObject Mesh(PsInterpreter i, int type, PsDict shading, PdfStream target, int components,
            out string refusal)
        {
            refusal = null;
            if (components <= 0) return Refuse(out refusal, "its colour space's component count is not known");
            if (type == 5)
            {
                if (!shading.TryGetName("VerticesPerRow", out PsObject perRow) || !IsInteger(perRow) || perRow.Num < 2)
                    return Refuse(out refusal, "a lattice mesh's /VerticesPerRow is missing or below 2");
                target[PdfName.VerticesPerRow] = new PdfNumber(perRow.AsInt());
            }

            if (!shading.TryGetName("DataSource", out PsObject source))
                return Refuse(out refusal, "a mesh has no /DataSource");

            if (source.Type == PsType.String || source.Type == PsType.File)
            {
                if (!Width(shading, "BitsPerCoordinate", CoordinateWidths, target, PdfName.BitsPerCoordinate)
                    || !Width(shading, "BitsPerComponent", ComponentWidths, target, PdfName.BitsPerComponent)
                    || (type != 5 && !Width(shading, "BitsPerFlag", FlagWidths, target, PdfName.BitsPerFlag)))
                    return Refuse(out refusal, "a mesh's /BitsPerCoordinate, /BitsPerComponent or /BitsPerFlag is missing or not a width the type allows");
                if (!shading.TryGetName("Decode", out _) || !Copy(i, shading, "Decode", PdfName.Decode, 4 + 2 * components, target))
                    return Refuse(out refusal, $"a mesh's /Decode is not {4 + 2 * components} numbers");
                byte[] data = DataOf(i, source, out refusal);
                if (data == null) return null;
                target.RawData = data;
                return target;
            }

            if (source.Type != PsType.Array || source.IsExecutable)
                return Refuse(out refusal, "a mesh's /DataSource is a procedure, which a mesh cannot be read from");

            return Packed(i, type, source.AsArray(), target, components, out refusal);
        }

        /// <summary>
        /// A <c>/DataSource</c>'s bytes: a string's, or the inline block a file <c>currentfile</c> made
        /// reads — its end in the data, as an image's is (a filter's marker, a compressed stream's own
        /// end), so a mesh or sampled function can follow its <c>shfill</c> in the program — or a
        /// reusable stream's whole data, from position 0 wherever the stream stands: a sampled
        /// function's data begins there (PLRM §3.10.1), a shading pattern reads its mesh again (§4.9.3),
        /// as other interpreters do. A JPEG is no such data. Charged to the allocation budget.
        /// </summary>
        private static byte[] DataOf(PsInterpreter i, PsObject source, out string refusal)
        {
            refusal = null;
            byte[] data;
            if (source.Type == PsType.String) data = source.AsString().ToArray();
            else if (source.Type == PsType.File)
            {
                PsFile file = source.AsFile();
                if (file.IsReusable) file.Seek(0);
                data = file.ReadInlineBlock(i, out bool jpeg);
                if (data == null) { refusal = "its /DataSource file could not be read"; return null; }
                if (jpeg) { refusal = "its /DataSource file is a JPEG, which is no shading data"; return null; }
                return data;
            }
            else { refusal = "a /DataSource is not a string or a file"; return null; }
            i.ChargeAllocation(data.Length, "shfill");
            return data;
        }

        /// <summary>
        /// Consumes every file data source a refused shading names — its own and its functions' — so an
        /// inline block it never read is not read as program. A block already read answers empty.
        /// </summary>
        /// <remarks>
        /// A dictionary and an array can hold themselves and each other, so the walk keeps a stack of its
        /// own and enters each once — a recursion here is CLR frames nothing can stop, and without the
        /// visited set a list of 64 dictionaries each naming that list is 64^n visits — and charges each
        /// array's length to the interpreter's budget.
        /// </remarks>
        internal static void DiscardFileSources(PsInterpreter i, PsDict shading)
        {
            var visited = new System.Collections.Generic.HashSet<object>(PdfReferenceEquality.Instance);
            var pending = new System.Collections.Generic.Stack<PsObject>();
            pending.Push(PsObject.Dict(shading));
            while (pending.Count > 0)
            {
                PsObject item = pending.Pop();
                if (item.Type == PsType.Dictionary)
                {
                    PsDict d = item.AsDict();
                    if (!visited.Add(d)) continue;
                    DiscardVisits++;
                    i.Tick();
                    if (d.TryGetName("DataSource", out PsObject source) && source.Type == PsType.File)
                        source.AsFile().ReadInlineBlock(i, out _);
                    if (d.TryGetName("Function", out PsObject function)) pending.Push(function);
                    if (d.TryGetName("Functions", out PsObject parts)) pending.Push(parts);
                }
                else if (item.Type == PsType.Array && !item.IsExecutable)
                {
                    PsArray list = item.AsArray();
                    if (!visited.Add(list)) continue;
                    DiscardVisits++;
                    i.Tick(1 + list.Length);
                    for (int k = list.Length - 1; k >= 0; k--)
                        if (list[k].Type is PsType.Dictionary or PsType.Array) pending.Push(list[k]);
                }
            }
        }

        /// <summary>Dictionaries and arrays <see cref="DiscardFileSources"/> has entered on the calling thread: the evidence that a shared one is entered once.</summary>
        [ThreadStatic] internal static long DiscardVisits;

        private static readonly int[] CoordinateWidths = { 1, 2, 4, 8, 12, 16, 24, 32 };
        private static readonly int[] ComponentWidths = { 1, 2, 4, 8, 12, 16 };
        private static readonly int[] FlagWidths = { 2, 4, 8 };

        /// <summary>
        /// A mesh given as numbers (PLRM §4.9.3: flag, coordinates and colour in order) packed as PDF
        /// requires its data — a flag in 8 bits, each coordinate in 32 and each colour component in
        /// 16, every one scaled into the <c>/Decode</c> range its values span. Each vertex and patch is
        /// a whole number of bytes, so no padding is needed (§8.7.4.5.5).
        /// </summary>
        private static PdfObject Packed(PsInterpreter i, int type, PsArray values, PdfStream target, int components,
            out string refusal)
        {
            refusal = null;
            int length = values.Length;
            i.Tick(length);
            var v = new double[length];
            for (int k = 0; k < length; k++)
            {
                if (!values[k].IsNumber) return Refuse(out refusal, "a mesh's /DataSource array holds something that is not a number");
                v[k] = values[k].Num;
                if (double.IsNaN(v[k]) || double.IsInfinity(v[k])) return Refuse(out refusal, "a mesh's /DataSource array holds a non-finite number");
            }

            // Each value's role: Flag, X, Y, or a colour component from Colour up.
            const sbyte Flag = -1, X = 0, Y = 1, Colour = 2;
            var role = new sbyte[length];
            int at = 0;
            bool flagged = type != 5;
            while (at < length)
            {
                int points, colours;
                if (type == 4 || type == 5) { points = 1; colours = 1; }
                else
                {
                    double f = v[at];
                    if (f != Math.Floor(f) || f < 0 || f > 3) return Refuse(out refusal, "a patch mesh's edge flag is not 0 to 3");
                    bool first = f == 0;
                    points = type == 6 ? (first ? 12 : 8) : (first ? 16 : 12);
                    colours = first ? 4 : 2;
                }
                if (type == 4 && (v[at] != Math.Floor(v[at]) || v[at] < 0 || v[at] > 2))
                    return Refuse(out refusal, "a triangle mesh's edge flag is not 0 to 2");

                int needed = (flagged ? 1 : 0) + 2 * points + colours * components;
                if (at + needed > length) return Refuse(out refusal, "a mesh's /DataSource array ends inside a vertex or patch");
                if (flagged) role[at++] = Flag;
                for (int p = 0; p < points; p++) { role[at++] = X; role[at++] = Y; }
                for (int c = 0; c < colours; c++)
                    for (int j = 0; j < components; j++) role[at++] = (sbyte)(Colour + j);
            }
            if (length == 0) return Refuse(out refusal, "a mesh's /DataSource array is empty");

            int ranges = 2 + components;
            var min = new double[ranges];
            var max = new double[ranges];
            for (int r = 0; r < ranges; r++) { min[r] = double.PositiveInfinity; max[r] = double.NegativeInfinity; }
            long bytes = 0;
            for (int k = 0; k < length; k++)
            {
                if (role[k] == Flag) { bytes += 1; continue; }
                min[role[k]] = Math.Min(min[role[k]], v[k]);
                max[role[k]] = Math.Max(max[role[k]], v[k]);
                bytes += role[k] < Colour ? 4 : 2;
            }
            i.ChargeAllocation(bytes, "shfill");

            var decode = new double[2 * ranges];
            for (int r = 0; r < ranges; r++)
            {
                // A role no value had (a mesh of flags alone cannot happen; a constant one can).
                if (min[r] > max[r]) { min[r] = 0; max[r] = 1; }
                if (max[r] == min[r]) max[r] = min[r] + 1;
                decode[2 * r] = min[r];
                decode[2 * r + 1] = max[r];
            }

            var data = new byte[bytes];
            int w = 0;
            for (int k = 0; k < length; k++)
            {
                if (role[k] == Flag) { data[w++] = SaturatingCast.ToByte(v[k]); continue; }
                int r = role[k];
                double unit = (v[k] - min[r]) / (max[r] - min[r]);
                if (r < Colour)
                {
                    uint q = SaturatingCast.ToUInt(Math.Round(unit * uint.MaxValue));
                    data[w++] = (byte)(q >> 24); data[w++] = (byte)(q >> 16); data[w++] = (byte)(q >> 8); data[w++] = (byte)q;
                }
                else
                {
                    ushort q = SaturatingCast.ToUShort(Math.Round(unit * ushort.MaxValue));
                    data[w++] = (byte)(q >> 8); data[w++] = (byte)q;
                }
            }

            target[PdfName.BitsPerCoordinate] = new PdfNumber(32);
            target[PdfName.BitsPerComponent] = new PdfNumber(16);
            if (flagged) target[PdfName.BitsPerFlag] = new PdfNumber(8);
            target[PdfName.Decode] = Array(decode);
            target.RawData = data;
            return target;
        }

        // ------------------------------------------------------------------
        //  Entries
        // ------------------------------------------------------------------

        private static PdfObject Refuse(out string refusal, string why)
        {
            refusal = why;
            return null;
        }

        private static bool IsInteger(PsObject o) => o.IsNumber && o.Num == Math.Floor(o.Num)
            && !double.IsInfinity(o.Num);

        private static bool AllPositiveIntegers(PsObject o)
        {
            if (o.Type != PsType.Array || o.AsArray().Length == 0) return false;
            PsArray a = o.AsArray();
            for (int k = 0; k < a.Length; k++)
                if (!IsInteger(a[k]) || a[k].Num < 1) return false;
            return true;
        }

        /// <summary>
        /// An optional array of finite numbers: true with null when absent, false when present but of
        /// another shape — <paramref name="count"/> numbers, or any number of them for -1.
        /// </summary>
        private static bool Numbers(PsInterpreter i, PsDict d, string key, int count, out double[] values)
        {
            values = Charged(i, PsNumeric.FiniteArray(d, key, count, out bool present));
            return !present || values != null;
        }

        /// <summary>
        /// <paramref name="values"/>, charged for the walk that read them and the PDF numbers they become:
        /// a shading is kept to the end of the conversion, and one array of the program's can stand in
        /// every function of every shading.
        /// </summary>
        private static double[] Charged(PsInterpreter i, double[] values)
        {
            if (values == null) return null;
            i.Tick(values.Length);
            i.ChargeAllocation(values.LongLength * PsAllocationBudget.BytesPerPdfNumber, "shfill");
            return values;
        }

        private static bool Copy(PsInterpreter i, PsDict d, string key, PdfName name, int count, PdfDictionary target)
        {
            if (!Numbers(i, d, key, count, out double[] values)) return false;
            if (values != null) target[name] = Array(values);
            return true;
        }

        private static bool CopyEven(PsInterpreter i, PsDict d, string key, PdfName name, PdfDictionary target)
        {
            if (!Numbers(i, d, key, -1, out double[] values)) return false;
            if (values == null) return true;
            if (values.Length % 2 != 0) return false;
            target[name] = Array(values);
            return true;
        }

        private static bool Width(PsDict d, string key, int[] allowed, PdfDictionary target, PdfName name)
        {
            if (!d.TryGetName(key, out PsObject o) || !IsInteger(o) || System.Array.IndexOf(allowed, o.AsInt()) < 0) return false;
            target[name] = new PdfNumber(o.AsInt());
            return true;
        }

        private static PdfArray Array(double[] values) => Emit.PdfValues.Numbers(values);
    }
}
