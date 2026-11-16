// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>The device colour families a page paints in, for A16–A18.</summary>
        private sealed class DeviceFamilies
        {
            internal bool Rgb, Cmyk, Gray;
            internal bool All => Rgb && Cmyk && Gray;
        }

        /// <summary>
        /// Which device colour families <paramref name="scan"/>'s page <b>paints</b> in, as ISO 19005-1
        /// §6.2.3.3 asks: the streams drawn from the page's content and its annotations' appearances,
        /// each colour remapped through the <c>DefaultGray</c>, <c>DefaultRGB</c> or <c>DefaultCMYK</c>
        /// of the resource dictionary it is painted with.
        /// </summary>
        /// <remarks>
        /// <para><b>Drawn, not named.</b> ISO 19005-1 Corrigendum 2: "Any named resource present in a
        /// resources dictionary, but whose name is not referenced from the associated content stream,
        /// is not considered used." The scan holds every stream a resource dictionary names; this
        /// starts at the page's own content and the appearance streams and follows what each drawn
        /// stream draws — <c>Do</c> a form, <c>scn</c>/<c>SCN</c> a tiling pattern, <c>Tf</c> a Type 3
        /// font's glyph procedures, <c>gs</c> a soft mask's group — over the streams the scan already
        /// decoded. A colour space, an image or a shading counts when an operator names it.</para>
        /// <para><b>Remapped, then classified.</b> ISO 32000-2 §8.6.5.6: when a device space is
        /// selected — by an operator, an image, an inline image or a shading — the current resource
        /// dictionary's <c>/ColorSpace</c> is checked for the matching default, which is used instead;
        /// the same holds for a Pattern space's underlying space, an Indexed space's base and a
        /// Separation or DeviceN alternate. "Provisions in this standard that apply specifically to
        /// device colour spaces are then not applicable" — the NOTE beside it. the conformance corpus
        /// passes each case (6-2-3-3-t03-pass-b to -k, 6-2-4-t03-pass-a), and its unreferenced form
        /// (6-2-3-3-t03-pass-a).</para>
        /// </remarks>
        private static void CollectPaintedDeviceFamilies(PdfDocument doc, PdfXValidator.PageScan scan,
            DeviceFamilies found, bool groupSpaces = false)
        {
            PdfDictionary resources = null, colourSpaces = null;
            InitialColourState initialColour = null;
            WalkDrawnOperations(doc, scan,
                (i, initialState) =>
                {
                    resources = scan.ContentStreamResources[i];
                    colourSpaces = Resolve<PdfDictionary>(doc, resources?[PdfName.ColorSpace]);
                    // PDF/A-2 onwards: a drawn group's blending space is a colour space used (6.2.4.3).
                    if (groupSpaces && scan.ContentStreamSources[i] is PdfStream source)
                        ClassifyPainted(doc, Resolve<PdfDictionary>(doc, source[PdfName.Group])?[PdfName.CS], colourSpaces, 0, found);
                    initialColour = initialState ? new InitialColourState() : null;
                    return !found.All;
                },
                (i, op) =>
                {
                    if (initialColour != null && initialColour.PaintsInInitialGray(op))
                        ClassifyPainted(doc, PdfName.DeviceGray, colourSpaces, 0, found);

                    switch (op.Operator)
                    {
                        case PdfOperators.SetRgbFill:
                        case PdfOperators.SetRgbStroke:
                            ClassifyPainted(doc, PdfName.DeviceRGB, colourSpaces, 0, found);
                            break;
                        case PdfOperators.SetCmykFill:
                        case PdfOperators.SetCmykStroke:
                            ClassifyPainted(doc, PdfName.DeviceCMYK, colourSpaces, 0, found);
                            break;
                        case PdfOperators.SetGrayFill:
                        case PdfOperators.SetGrayStroke:
                            ClassifyPainted(doc, PdfName.DeviceGray, colourSpaces, 0, found);
                            break;
                        case PdfOperators.SetColorSpaceFill:
                        case PdfOperators.SetColorSpaceStroke:
                            if (LastName(op) is PdfName space)
                                ClassifyPainted(doc, SelectedSpace(doc, space, colourSpaces, inlineImage: false), colourSpaces, 0, found);
                            break;
                        case PdfOperators.SetColorFillN:
                        case PdfOperators.SetColorStrokeN:
                            // A pattern is named last (§8.7.3.2); an uncoloured one's underlying space
                            // was classified when `cs` selected [/Pattern base]. A tiling pattern is
                            // drawn by the walk; a shading pattern's space is classified here.
                            if (LastName(op) is PdfName patternName)
                            {
                                var pattern = Resolve<PdfDictionary>(doc,
                                    Resolve<PdfDictionary>(doc, resources?[PdfName.Pattern])?[patternName]);
                                if (pattern != null && !(pattern is PdfStream))
                                    ClassifyShading(doc, pattern[PdfName.Shading], colourSpaces, found);
                            }
                            break;
                        case PdfOperators.ShadingFill:
                            if (LastName(op) is PdfName shadingName)
                                ClassifyShading(doc,
                                    Resolve<PdfDictionary>(doc, resources?[PdfName.Shading])?[shadingName],
                                    colourSpaces, found);
                            break;
                        case PdfOperators.InvokeXObject:
                            // A form is drawn by the walk; an image's space is classified here.
                            if (LastName(op) is PdfName xobjectName)
                            {
                                var xobject = Resolve<PdfStream>(doc,
                                    Resolve<PdfDictionary>(doc, resources?[PdfName.XObject])?[xobjectName]);
                                if (xobject != null && ResolveName(doc, xobject[PdfName.Subtype]) == "Image")
                                    ClassifyPainted(doc, xobject[PdfName.ColorSpace], colourSpaces, 0, found);
                            }
                            break;
                        case PdfOperators.BeginInlineImage:
                        {
                            // The parser hands back the image as a stream holding the keys as written.
                            var image = op.Operands.Length > 0 ? op.Operands[0] as PdfStream : null;
                            var inlineSpace = image?[PdfName.CS] ?? image?[PdfName.ColorSpace];
                            if (inlineSpace is PdfName inlineName)
                                inlineSpace = SelectedSpace(doc, inlineName, colourSpaces, inlineImage: true);
                            ClassifyPainted(doc, inlineSpace, colourSpaces, 0, found);
                            break;
                        }
                    }
                    return !found.All;
                });
        }

        /// <summary>
        /// The streams <paramref name="scan"/>'s page <b>draws</b>, and each one's operations in order:
        /// the page's content and its annotations' appearances, then what a drawn stream draws —
        /// <c>Do</c> a form, <c>scn</c>/<c>SCN</c> a tiling pattern, <c>Tf</c> a Type 3 font's glyph
        /// procedures, <c>gs</c> a soft mask's group — over the streams the scan already decoded, each
        /// once. ISO 19005-1 Corrigendum 2: a named resource the content does not reference is not
        /// used; only what is drawn is judged. The one walk of drawn content, for every reader of
        /// it (the device colours, the content stream rules).
        /// </summary>
        /// <param name="doc">The document, for resolving.</param>
        /// <param name="scan">The page's scan, whose decoded streams are walked.</param>
        /// <param name="enter">
        /// Called as a stream is entered, with whether it begins in the initial graphics state (the
        /// page's content and an appearance stream, §8.4.1, §12.5.5); false stops the walk.
        /// </param>
        /// <param name="visit">Called for each operation of the entered stream; false stops the walk.</param>
        private static void WalkDrawnOperations(PdfDocument doc, PdfXValidator.PageScan scan,
            Func<int, bool, bool> enter, Func<int, ContentStreamOperation, bool> visit)
        {
            int count = scan.ContentStreams.Count;
            var indexOf = new Dictionary<object, int>(PdfReferenceEquality.Instance);
            for (int i = 0; i < count; i++)
                if (scan.ContentStreamSources[i] != null)
                    indexOf[scan.ContentStreamSources[i]] = i;

            var drawn = new bool[count];
            var pending = new Stack<int>();
            void Draw(PdfObject source)
            {
                if (source != null && indexOf.TryGetValue(source, out int i) && !drawn[i])
                {
                    drawn[i] = true;
                    pending.Push(i);
                }
            }

            var initialState = new HashSet<int>();
            if (scan.PageContentIndex >= 0)
            {
                drawn[scan.PageContentIndex] = true;
                pending.Push(scan.PageContentIndex);
                initialState.Add(scan.PageContentIndex);
            }
            foreach (var ap in scan.AppearanceStreams)
            {
                Draw(ap);
                if (ap != null && indexOf.TryGetValue(ap, out int apIndex)) initialState.Add(apIndex);
            }

            while (pending.Count > 0)
            {
                int i = pending.Pop();
                if (!enter(i, initialState.Contains(i))) return;
                var resources = scan.ContentStreamResources[i];

                foreach (var op in ScanReuse.Parse(scan.ContentStreams[i]))
                {
                    if (!visit(i, op)) return;
                    switch (op.Operator)
                    {
                        case PdfOperators.SetColorFillN:
                        case PdfOperators.SetColorStrokeN:
                            if (LastName(op) is PdfName patternName
                                && Resolve<PdfDictionary>(doc, Resolve<PdfDictionary>(doc, resources?[PdfName.Pattern])?[patternName]) is PdfStream tiling)
                                Draw(tiling);
                            break;
                        case PdfOperators.InvokeXObject:
                            if (LastName(op) is PdfName xobjectName)
                            {
                                var xobject = Resolve<PdfStream>(doc,
                                    Resolve<PdfDictionary>(doc, resources?[PdfName.XObject])?[xobjectName]);
                                if (xobject != null && ResolveName(doc, xobject[PdfName.Subtype]) != "Image")
                                    Draw(xobject);
                            }
                            break;
                        case PdfOperators.SetFont:
                            if (op.Operands.Length > 0 && op.Operands[0] is PdfName fontName)
                            {
                                var font = Resolve<PdfDictionary>(doc,
                                    Resolve<PdfDictionary>(doc, resources?[PdfName.Font])?[fontName]);
                                var procs = ResolveName(doc, font?[PdfName.Subtype]) == "Type3"
                                    ? Resolve<PdfDictionary>(doc, font[PdfName.CharProcs])
                                    : null;
                                if (procs != null)
                                    foreach (var proc in procs) Draw(doc.ResolveObject(proc.Value) ?? proc.Value);
                            }
                            break;
                        case PdfOperators.SetExtGState:
                            if (LastName(op) is PdfName stateName)
                            {
                                var state = Resolve<PdfDictionary>(doc,
                                    Resolve<PdfDictionary>(doc, resources?[PdfName.ExtGState])?[stateName]);
                                var smask = Resolve<PdfDictionary>(doc, state?[PdfName.SMask]);
                                var group = smask?[PdfName.G];
                                if (group != null) Draw(doc.ResolveObject(group) ?? group);
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Whether a stream that began in the initial graphics state paints in a colour space nothing
        /// has set — DeviceGray, the initial fill and stroke space — with a path or with text. Only
        /// <c>cs</c>, <c>CS</c> and the device-colour operators change a space; <c>sc</c>/<c>scn</c>
        /// set a colour in the current one; <c>q</c>/<c>Q</c> save and restore; text paints by its
        /// render mode (<see cref="TextPaintTracker"/>).
        /// </summary>
        private sealed class InitialColourState
        {
            private bool _fillSet, _strokeSet;
            private readonly Stack<(bool Fill, bool Stroke)> _saved = new Stack<(bool, bool)>();
            private readonly TextPaintTracker _text = new TextPaintTracker();

            internal bool PaintsInInitialGray(ContentStreamOperation op)
            {
                bool fill = false, stroke = false;
                switch (op.Operator)
                {
                    case PdfOperators.Save:
                        _saved.Push((_fillSet, _strokeSet));
                        break;
                    case PdfOperators.Restore:
                        if (_saved.Count > 0) (_fillSet, _strokeSet) = _saved.Pop();
                        break;
                    case PdfOperators.SetColorSpaceFill:
                    case PdfOperators.SetGrayFill:
                    case PdfOperators.SetRgbFill:
                    case PdfOperators.SetCmykFill:
                        _fillSet = true;
                        break;
                    case PdfOperators.SetColorSpaceStroke:
                    case PdfOperators.SetGrayStroke:
                    case PdfOperators.SetRgbStroke:
                    case PdfOperators.SetCmykStroke:
                        _strokeSet = true;
                        break;
                    case PdfOperators.Fill:
                    case PdfOperators.FillObsolete:
                    case PdfOperators.FillEvenOdd:
                        fill = true;
                        break;
                    case PdfOperators.Stroke:
                    case PdfOperators.CloseStroke:
                        stroke = true;
                        break;
                    case PdfOperators.FillStroke:
                    case PdfOperators.FillStrokeEvenOdd:
                    case PdfOperators.CloseFillStroke:
                    case PdfOperators.CloseFillStrokeEvenOdd:
                        fill = stroke = true;
                        break;
                }
                if (_text.Observe(op.Operator, op.Operands))   // modes 3 and 7 are neither fill nor stroke below
                {
                    var mode = _text.RenderMode;
                    fill |= mode == TextRenderMode.Fill || mode == TextRenderMode.FillThenStroke
                            || mode == TextRenderMode.FillAndClip || mode == TextRenderMode.FillStrokeAndClip;
                    stroke |= mode == TextRenderMode.Stroke || mode == TextRenderMode.FillThenStroke
                              || mode == TextRenderMode.StrokeAndClip || mode == TextRenderMode.FillStrokeAndClip;
                }
                return (fill && !_fillSet) || (stroke && !_strokeSet);
            }
        }

        private static PdfName LastName(ContentStreamOperation op)
            => op.Operands.Length > 0 ? op.Operands[op.Operands.Length - 1] as PdfName : null;

        /// <summary>
        /// The colour space a name selects: a device name is itself, and so, for an inline image, is its
        /// abbreviation (§8.9.7 reserves those for inline images alone); any other name — a
        /// <c>cs</c> operand of <c>/G</c> names a resource — is looked up in the current <c>/ColorSpace</c>.
        /// </summary>
        private static PdfObject SelectedSpace(PdfDocument doc, PdfName name, PdfDictionary colourSpaces, bool inlineImage)
        {
            string expanded = inlineImage ? PdfColorSpaceNames.Expand(name.Value) : name.Value;
            if (expanded == PdfColorSpaceNames.DeviceRGB || expanded == PdfColorSpaceNames.DeviceCMYK
                || expanded == PdfColorSpaceNames.DeviceGray || expanded == PdfColorSpaceNames.Pattern)
                return name;
            return colourSpaces?[name];
        }

        private static void ClassifyShading(PdfDocument doc, PdfObject shading, PdfDictionary colourSpaces,
            DeviceFamilies found)
        {
            var dict = Resolve<PdfDictionary>(doc, shading);
            if (dict != null) ClassifyPainted(doc, dict[PdfName.ColorSpace], colourSpaces, 0, found);
        }

        /// <summary>
        /// Marks the device family <paramref name="space"/> paints in after §8.6.5.6's remapping
        /// through <paramref name="colourSpaces"/> — descending a Pattern space's underlying space, an
        /// Indexed space's base and a Separation or DeviceN alternate, each remapped the same way.
        /// <paramref name="colourSpaces"/> is the current resource dictionary's <c>/ColorSpace</c>, or
        /// <see langword="null"/> once a default has been applied (a default is not remapped again).
        /// </summary>
        private static void ClassifyPainted(PdfDocument doc, PdfObject space, PdfDictionary colourSpaces,
            int depth, DeviceFamilies found)
        {
            if (space == null || depth > PdfLimits.MaxResourceDepth) return;

            var resolved = doc.ResolveObject(space) ?? space;
            string family = resolved is PdfName n
                ? n.Value
                : resolved is PdfArray arr && arr.Count > 0 ? ResolveName(doc, arr[0]) : null;
            if (family == null) return;

            string device = PdfColorSpaceNames.Expand(family);
            PdfName defaultKey = device == PdfColorSpaceNames.DeviceRGB ? PdfName.DefaultRGB
                : device == PdfColorSpaceNames.DeviceCMYK ? PdfName.DefaultCMYK
                : device == PdfColorSpaceNames.DeviceGray ? PdfName.DefaultGray
                : null;
            if (defaultKey != null)
            {
                var substitute = colourSpaces?[defaultKey];
                if (substitute != null)
                {
                    ClassifyPainted(doc, substitute, null, depth + 1, found);
                    return;
                }
                if (device == PdfColorSpaceNames.DeviceRGB) found.Rgb = true;
                else if (device == PdfColorSpaceNames.DeviceCMYK) found.Cmyk = true;
                else found.Gray = true;
                return;
            }

            if (!(resolved is PdfArray a)) return;
            switch (device)
            {
                case PdfColorSpaceNames.Indexed:
                case PdfColorSpaceNames.Pattern:
                    if (a.Count > 1) ClassifyPainted(doc, a[1], colourSpaces, depth + 1, found);
                    break;
                case PdfColorSpaceNames.Separation:
                case PdfColorSpaceNames.DeviceN:
                    if (a.Count > 2) ClassifyPainted(doc, a[2], colourSpaces, depth + 1, found);
                    break;
            }
        }

        private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
            => obj == null ? null : (doc.ResolveObject(obj) as T ?? obj as T);

        private static string ResolveName(PdfDocument doc, PdfObject obj)
            => obj == null ? null : ((doc.ResolveObject(obj) ?? obj) as PdfName)?.Value;
    }
}
