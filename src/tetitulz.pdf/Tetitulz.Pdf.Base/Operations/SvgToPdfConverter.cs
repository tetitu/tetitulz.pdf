// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>Options controlling SVG-to-PDF conversion.</summary>
    public sealed class SvgConversionOptions
    {
        /// <summary>Page width in points (defaults to <see cref="PdfPageSizes.A4Width"/>).</summary>
        public float PageWidth { get; set; } = PdfPageSizes.A4Width;

        /// <summary>Page height in points (defaults to <see cref="PdfPageSizes.A4Height"/>).</summary>
        public float PageHeight { get; set; } = PdfPageSizes.A4Height;

        /// <summary>Flip Y axis: SVG is top-down, PDF is bottom-up.</summary>
        public bool FlipY { get; set; } = true;

        /// <summary>
        /// The caller's source of font programs (TrueType or OpenType bytes) for text WinAnsi cannot
        /// carry — Hebrew, Greek, Cyrillic, CJK — called with the run's first <c>font-family</c>, or
        /// the standard family it falls to; <c>null</c> for none. A font that covers the run is
        /// embedded as a subset and the run shaped through it; one that does not is passed over, to
        /// the host's fonts when <see cref="AllowSystemFontSubstitution"/> allows. Without either,
        /// such text is set as <c>?</c> and reported, as it always was.
        /// </summary>
        public Func<string, byte[]> FontProvider { get; set; }

        /// <summary>
        /// Permit embedding a font installed on this machine for text <see cref="FontProvider"/> does
        /// not cover. <b>Defaults to false, and that is a licensing decision</b>: embedding redistributes
        /// the font, and this library ships and licenses none. The same name and default as
        /// <c>FormFillOptions.AllowSystemFontSubstitution</c> and <c>FontResolutionRequest</c>'s.
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// The reader's languages, as BCP 47 tags, for <c>systemLanguage</c> (SVG 2 §5.8.5): an element
        /// listing none of them is not drawn, and a <c>switch</c> draws its first child that lists one.
        /// <c>null</c> (the default) is the host's UI culture and its parents (<c>en-US</c>, then
        /// <c>en</c>), as a browser offers its own; an empty list matches no language.
        /// </summary>
        public IList<string> Languages { get; set; }
    }

    /// <summary>
    /// Pure-managed SVG-to-PDF converter using only System.Xml.
    /// Supports a subset of SVG 1.1: rect (rounded too), circle, ellipse, line, polyline, polygon,
    /// path (every command), text (sized and placed through the same mapping), and g; every transform
    /// function, on a shape or a group; fill and stroke colour, stroke width, cap, join, miter limit
    /// and dashes, and fill-rule, inherited and read from <c>style</c>. An element it does not draw
    /// and a property it does not apply are reported on <see cref="LastError"/>; the text properties
    /// it does not read are not. GAPS.md §5d lists what remains.
    /// </summary>
    public static class SvgToPdfConverter
    {
        // ── Diagnostics ───────────────────────────────────────────────────────

        [ThreadStatic] private static List<string> _issues;

        /// <summary>
        /// What the last conversion on <b>this thread</b> could not carry through faithfully, or
        /// <c>null</c> when it carried everything. Reset at the start of every
        /// <see cref="Convert(string, SvgConversionOptions)"/> call, on every path.
        ///
        /// <para><b>Why this exists.</b> Conversion never throws for content it cannot express —
        /// it drops or approximates and returns a perfectly valid PDF — so "the call succeeded"
        /// and "the graphic survived" are two different questions, and this answers the second. Text
        /// outside the font's encoding is the sharpest case: unreported, it would reach the file as
        /// <c>?</c>, which at the viewer is indistinguishable from a font
        /// substitution problem.</para>
        ///
        /// <para>One call can hit several distinct issues, so they are joined rather than
        /// overwritten (a <c>Last*</c> slot that keeps only the newest would hide the rest).</para>
        /// </summary>
        public static string LastError =>
            _issues == null || _issues.Count == 0 ? null : string.Join("; ", _issues);

        private static void Report(string issue)
        {
            (_issues ??= new List<string>()).Add(issue);
        }

        /// <summary><see cref="Report"/>, unless the same issue is already recorded.</summary>
        private static void ReportOnce(string issue)
        {
            if (_issues == null || !_issues.Contains(issue)) Report(issue);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Converts an SVG stream to a PDF byte array.</summary>
        public static byte[] Convert(Stream svgStream, SvgConversionOptions options = null)
        {
            if (svgStream == null) throw new ArgumentNullException(nameof(svgStream));
            using var reader = new StreamReader(svgStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            return Convert(reader.ReadToEnd(), options);
        }

        /// <summary>Converts an SVG XML string to a PDF byte array.</summary>
        public static byte[] Convert(string svgXml, SvgConversionOptions options = null)
        {
            if (svgXml == null) throw new ArgumentNullException(nameof(svgXml));
            options ??= new SvgConversionOptions();

            // Reset at the START of the operation, on every path — a slot a caller reads after a
            // call must never be able to hand back the previous document's answer.
            _issues = null;

            // Parse via a hardened reader: prohibit DTDs (blocks inline-entity /
            // "billion laughs" expansion and DTD-driven XXE) and disable external
            // resolution. SVG input is untrusted.
            // PreserveWhitespace: the space between two tspans is a text node of spaces alone, and
            // the default dropped it, so "<tspan>Hello</tspan> <tspan>world</tspan>" read "Helloworld".
            // The tree walk passes over every node that is not an element.
            var doc = ParseSvg(svgXml);

            var root = doc.DocumentElement;
            float svgW = options.PageWidth;
            float svgH = options.PageHeight;
            float scaleX = 1f, scaleY = 1f, offsetX = 0f, offsetY = 0f;

            // Parse width/height/viewBox from <svg>. A percentage is NOT a length here: it resolves
            // against a containing block this converter does not have, so `width="100%"` must read
            // as "unspecified" rather than as the number 100 — which is what stripping the '%'
            // produced, and which then reported a 100-unit viewport for a full-width graphic.
            float parsedW = TryParseLengthF(root, "width", 0f);
            float parsedH = TryParseLengthF(root, "height", 0f);
            if (parsedW > 0) svgW = parsedW;
            if (parsedH > 0) svgH = parsedH;

            bool hasViewBox = false;
            float viewBoxW = 0, viewBoxH = 0;
            string vb = root?.GetAttribute("viewBox");
            if (!string.IsNullOrWhiteSpace(vb))
            {
                // Four numbers and nothing else, through the one number reader; anything else is
                // ignored, as SVG ignores an invalid viewBox.
                var vbParts = ParseFloatList(vb, out bool vbComplete);
                float vbX = 0, vbY = 0, vbW = 0, vbH = 0;
                if (vbComplete && vbParts.Count == 4)
                {
                    vbX = vbParts[0]; vbY = vbParts[1]; vbW = vbParts[2]; vbH = vbParts[3];
                }
                if (vbW > 0 && vbH > 0)
                {
                    scaleX = options.PageWidth / vbW;
                    scaleY = options.PageHeight / vbH;
                    offsetX = -vbX * scaleX;
                    offsetY = -vbY * scaleY;
                    hasViewBox = true;
                    (viewBoxW, viewBoxH) = (vbW, vbH);
                }
            }

            // Only a viewBox maps the SVG's coordinate system onto the page. Without one the SVG
            // user unit IS a PDF point and the graphic is drawn at its own size in the bottom-left
            // corner — which is right for a page sized to the graphic (what SvgRasterizer does) and
            // wrong-looking for anything else, so say so instead of leaving the caller to notice
            // that a 2000-unit drawing ran off an A4 page.
            if (!hasViewBox && (svgW > options.PageWidth + 0.5f || svgH > options.PageHeight + 0.5f))
                Report(string.Format(CultureInfo.InvariantCulture,
                    "the SVG declares a {0}x{1} viewport and has no viewBox, so it is drawn 1:1 " +
                    "and overflows the {2}x{3} page",
                    svgW, svgH, options.PageWidth, options.PageHeight));

            var ctx = new ConvertContext
            {
                Options = options,
                ViewportWidth = hasViewBox ? viewBoxW : svgW,
                ViewportHeight = hasViewBox ? viewBoxH : svgH,
                ScaleX = scaleX,
                ScaleY = scaleY,
                OffsetX = offsetX,
                OffsetY = offsetY,
                ElementsById = IndexIds(root),
            };
            ctx.LinkTargets = LinkTargetIds(root);

            var sb = new StringBuilder();
            sb.AppendLine("q"); // save graphics state
            if (root != null)
                // Transform.Identity, NOT `new Transform()`: the struct's default value has
                // Sx = Sy = 0, and every shape multiplies its geometry by those. The root call
                // used the default, so `pw = w * ctx.ScaleX * xform.Sx` was always 0 — the
                // converter emitted correct colours and correct operators around geometry that had
                // collapsed to a single point. Sixteen tests covered this and all of them asserted
                // only that no exception was thrown.
                ProcessNode(root, sb, ctx, Transform.Identity, depth: 0);
            sb.AppendLine("Q"); // restore graphics state

            return BuildPdf(sb.ToString(), MediaBox(options), ctx);
        }

        /// <summary>Every standard font's <c>/Encoding</c>: the content stream's text is WinAnsi (<see cref="ToWinAnsi"/>).</summary>
        private const string FontEncoding = "/WinAnsiEncoding";

        /// <summary>
        /// A font embedded for text WinAnsi cannot carry: a Type 0 font under Identity-H, whose
        /// content shows glyph ids, written with <see cref="Core.Writing.Type0FontBuilder"/> from the
        /// glyphs the runs set in it drew.
        /// </summary>
        private sealed class EmbeddedFont
        {
            /// <summary>The family, weight and style it was resolved for.</summary>
            public string Key;
            /// <summary>Its resource name, <c>F</c> and a number after the standard faces'.</summary>
            public string Name;
            public Core.Fonts.TrueTypeFontProgram Program;
            /// <summary>Each glyph drawn and the code point it stands for, for <c>/W</c> and <c>/ToUnicode</c>.</summary>
            public readonly Dictionary<ushort, int> GidToUnicode = new Dictionary<ushort, int>();
            /// <summary>A ligature glyph's typed letters, for <c>/ToUnicode</c> (<see cref="Core.Text.GlyphMapping.Text"/>).</summary>
            public readonly Dictionary<ushort, string> GidToText = new Dictionary<ushort, string>();
        }

        // ── SVG processing ────────────────────────────────────────────────────

        private sealed class ConvertContext
        {
            public SvgConversionOptions Options;

            private IReadOnlyList<string> _languages;

            /// <summary>The reader's languages for <c>systemLanguage</c> (<see cref="ReaderLanguages"/>), read once.</summary>
            public IReadOnlyList<string> Languages => _languages ??= ReaderLanguages(Options ?? new SvgConversionOptions());
            /// <summary>
            /// The nearest viewport's size, which a percentage length is a share of (§7.10): its
            /// viewBox's where it has one, else its own width and height. The root's to begin with; a
            /// nested <c>svg</c>, a used <c>symbol</c> and a marker set theirs for their content
            /// (<see cref="InViewport"/>), not the root's declared size throughout.
            /// </summary>
            public float ViewportWidth, ViewportHeight;
            public float ScaleX, ScaleY, OffsetX, OffsetY;

            /// <summary>
            /// The document's elements by <c>id</c>, the first of each, indexed before anything is
            /// drawn — so a copy a <c>&lt;use&gt;</c> makes, which keeps its id, is never found.
            /// </summary>
            public Dictionary<string, XmlElement> ElementsById;

            /// <summary><c>&lt;use&gt;</c> references expanded so far, against <see cref="PdfLimits.MaxSvgUseExpansions"/>.</summary>
            public int UseExpansions;

            /// <summary>Elements those references copied, all together, against <see cref="PdfLimits.MaxSvgUseExpandedElements"/>.</summary>
            public long UseExpandedElements;

            /// <summary>
            /// The element being drawn for its filter (<see cref="Filtered"/>): its own <c>opacity</c>
            /// applies to the filter's result, after it, not to what it draws (<see cref="OwnOpacity"/>).
            /// </summary>
            public XmlNode Filtered;

            /// <summary>How many elements this conversion has filtered, against <see cref="PdfLimits.MaxSvgFilteredElements"/>.</summary>
            public int FilteredCount;

            /// <summary>
            /// True while a clipPath's shapes are written: each writes its path and no colour, state or
            /// painting operator, and no clip-path of its own is applied.
            /// </summary>
            public bool ClipMode;

            /// <summary>
            /// True while a clipPath's shapes are drawn as a luminosity mask (<see cref="ClipMaskState"/>):
            /// each fills white under its own <c>clip-rule</c>, with no stroke, opacity, marker, filter or
            /// link, and its own <c>clip-path</c> applied. A clipPath one path under one rule cannot say
            /// — text in it, a child with a clip of its own, children whose rules differ — is drawn so.
            /// </summary>
            public bool ClipFill;

            /// <summary>Inside a clipPath's content, either way: nothing but geometry counts.</summary>
            public bool InClipPath => ClipMode || ClipFill;

            /// <summary>True while a marker's content is drawn: a marker inside it is not, so markers cannot recurse.</summary>
            public bool InMarker;

            /// <summary>
            /// The objects after the page skeleton's four — link annotations, image XObjects and their
            /// soft masks — numbered in this order from <see cref="SinglePageDocumentWriter.FirstExtraObject"/>.
            /// </summary>
            public readonly List<SinglePageDocumentWriter.ExtraObject> Objects = new List<SinglePageDocumentWriter.ExtraObject>();

            /// <summary>The page's <c>/XObject</c> entries: each image drawn as an object, and each transparency group's form.</summary>
            public readonly StringBuilder XObjectEntries = new StringBuilder();
            public int XObjectCount;

            /// <summary>Lists object <paramref name="number"/> in the page's <c>/XObject</c> as <paramref name="name"/>.</summary>
            public void ListXObject(string name, int number)
                => XObjectEntries.Append(XObjectEntries.Length == 0 ? "" : " ").Append('/').Append(name).Append(' ').Append(number).Append(" 0 R");

            /// <summary>Transparency groups drawn so far, for their <c>/Fm</c> names.</summary>
            public int FormCount;

            /// <summary>
            /// The number of the resources object every transparency group's form names, or 0 before
            /// the first: reserved then, and written when the drawing is done and the resources known.
            /// </summary>
            public int ResourcesObject;

            /// <summary>The shared resources object's number, reserved on first use.</summary>
            public int SharedResources()
            {
                if (ResourcesObject == 0)
                    ResourcesObject = AddObject(SinglePageDocumentWriter.ExtraObject.Plain("<< >>"));
                return ResourcesObject;
            }

            /// <summary>The page's <c>/Font</c> entries after <c>/F1</c>, one per face other than Helvetica.</summary>
            public readonly StringBuilder FontEntries = new StringBuilder();
            private readonly Dictionary<string, string> _fontNames = new Dictionary<string, string>(StringComparer.Ordinal);
            /// <summary>Font resources named so far after <c>/F1</c>, standard faces and embedded fonts alike.</summary>
            private int _fontCount;

            /// <summary>
            /// The resource <paramref name="face"/> is set in: Helvetica is the skeleton's <c>/F1</c>, so a
            /// drawing in it writes what it did; each other face is one entry, named on its first use.
            /// </summary>
            public string FontResource(string face)
            {
                if (face == StandardFonts.Helvetica) return "F1";
                if (!_fontNames.TryGetValue(face, out string name))
                {
                    name = NextFontName();
                    _fontNames.Add(face, name);
                    FontEntries.Append(FontEntries.Length == 0 ? "" : " ").Append('/').Append(name)
                        .Append(" << /Type /Font /Subtype /Type1 /BaseFont /").Append(face).Append(" /Encoding /WinAnsiEncoding >>");
                }
                return name;
            }

            private string NextFontName() => "F" + (++_fontCount + 1).ToString(CultureInfo.InvariantCulture);

            /// <summary>
            /// The fonts embedded for text WinAnsi cannot carry (<see cref="EmbeddedFontFor"/>), in the
            /// order named; their objects are written after the drawing, when the glyphs each draws are known.
            /// </summary>
            public readonly List<EmbeddedFont> EmbeddedFonts = new List<EmbeddedFont>();
            /// <summary>Family, weight and style keys no font could be resolved for: asked once, reported once.</summary>
            private readonly HashSet<string> _unresolvedFontKeys = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Resolutions made, against <see cref="PdfLimits.MaxSvgFontResolutions"/>.</summary>
            public int FontResolutions;

            /// <summary>
            /// An embedded font for <paramref name="family"/> in the weight and style given that covers
            /// <paramref name="required"/>: one already embedded when it covers them, else one resolved
            /// through the options' provider and, when <paramref name="host"/> allows, the host's fonts;
            /// <c>null</c>, reported, when none is found, when the key has failed before, or past the
            /// resolution ceiling. A key is the family, weight, style and whether the host was searched:
            /// a family the provider declined for Latin text is still asked of the host for Hebrew.
            /// </summary>
            public EmbeddedFont EmbeddedFontFor(string family, bool bold, bool italic, IReadOnlyCollection<int> required, bool host, out string failure)
            {
                failure = null;
                string key = family + "|" + (bold ? "b" : "") + (italic ? "i" : "") + (host ? "|h" : "");
                foreach (EmbeddedFont font in EmbeddedFonts)
                    if (font.Key == key && required.All(font.Program.Covers)) return font;
                if (_unresolvedFontKeys.Contains(key)) return null;
                if (FontResolutions >= PdfLimits.MaxSvgFontResolutions)
                {
                    ReportOnce($"past {PdfLimits.MaxSvgFontResolutions} font resolutions no further font is embedded; the rest of the text was set in the standard faces, outside WinAnsi as '?'");
                    return null;
                }
                FontResolutions++;
                var resolution = Core.Fonts.SystemFontResolver.Resolve(
                    new Core.Fonts.FontResolutionRequest
                    {
                        FamilyHint = family,
                        Bold = bold,
                        Italic = italic,
                        RequiredCodePoints = required,
                        AllowSystemFontSubstitution = host,
                    },
                    Options.FontProvider);
                if (!resolution.Success)
                {
                    _unresolvedFontKeys.Add(key);
                    failure = resolution.FailureReason;
                    return null;
                }
                var embedded = new EmbeddedFont { Key = key, Name = NextFontName(), Program = resolution.Program };
                EmbeddedFonts.Add(embedded);
                return embedded;
            }

            /// <summary>Adds an object and returns the number it will be written as.</summary>
            public int AddObject(SinglePageDocumentWriter.ExtraObject o)
            {
                Objects.Add(o);
                return SinglePageDocumentWriter.FirstExtraObject + Objects.Count - 1;
            }

            /// <summary>
            /// True while an anchor's content is drawn: every point mapped to the page widens
            /// (<see cref="MinX"/>, <see cref="MinY"/>)–(<see cref="MaxX"/>, <see cref="MaxY"/>), the link's rectangle.
            /// </summary>
            public bool Tracking;
            public bool HasBounds;
            public float MinX, MinY, MaxX, MaxY;

            public void Track(float x, float y)
            {
                if (!HasBounds) { MinX = MaxX = x; MinY = MaxY = y; HasBounds = true; return; }
                if (x < MinX) MinX = x;
                if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y;
                if (y > MaxY) MaxY = y;
            }

            /// <summary>The ids the drawing's <c>&lt;a href="#id"&gt;</c> links point at, indexed before anything is drawn.</summary>
            public HashSet<string> LinkTargets = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Each target's bounds on the page, in page space, once it has been drawn (<see cref="ProcessNode"/>).</summary>
            public readonly Dictionary<string, (float MinX, float MinY, float MaxX, float MaxY)> TargetBounds
                = new Dictionary<string, (float, float, float, float)>(StringComparer.Ordinal);
            /// <summary>Targets whose bounds are being captured, so the capture's own re-entry does not capture again.</summary>
            public readonly HashSet<string> CapturingTargets = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>
            /// Links inside the drawing, each holding an object of its own until the drawing is done and
            /// its target's bounds are known: the object's index, the link's rectangle and its target.
            /// </summary>
            public readonly List<(int Index, string Rect, string Id, string Href)> PendingGoTos = new List<(int, string, string, string)>();

            /// <summary>The page's <c>/ExtGState</c> entries, one per distinct pair of alphas.</summary>
            public readonly StringBuilder StateEntries = new StringBuilder();
            private readonly Dictionary<(float, float), string> _states = new Dictionary<(float, float), string>();

            /// <summary>The name of the graphics state with these fill and stroke alphas, added on first use.</summary>
            public string StateFor(float fill, float stroke)
            {
                if (_states.TryGetValue((fill, stroke), out string name)) return name;
                name = "GS" + _states.Count.ToString(CultureInfo.InvariantCulture);
                _states.Add((fill, stroke), name);
                if (StateEntries.Length > 0) StateEntries.Append(' ');
                StateEntries.Append('/').Append(name).Append(" << /ca ").Append(F(fill)).Append(" /CA ").Append(F(stroke)).Append(" >>");
                return name;
            }

            /// <summary>Soft masks set so far, for their <c>/SM</c> names.</summary>
            private int _masks;

            /// <summary>
            /// A graphics state whose soft mask is the luminosity of form <paramref name="form"/>
            /// (ISO 32000-1 Table 144), one per masked element.
            /// </summary>
            public string MaskStateFor(int form)
            {
                string name = ReserveMaskState();
                WriteMaskState(name, form);
                return name;
            }

            /// <summary>A soft-mask state's name, for a state written later by <see cref="WriteMaskState"/>.</summary>
            public string ReserveMaskState() => "SM" + _masks++.ToString(CultureInfo.InvariantCulture);

            /// <summary>The soft-mask state <paramref name="name"/>: the luminosity of form <paramref name="form"/>.</summary>
            public void WriteMaskState(string name, int form)
            {
                if (StateEntries.Length > 0) StateEntries.Append(' ');
                StateEntries.Append('/').Append(name).Append(" << /SMask << /Type /Mask /S /Luminosity /G ").Append(form).Append(" 0 R >> >>");
            }

            /// <summary>True while a mask's content is drawn, so a mask in it is not applied and cannot recurse.</summary>
            public bool InMask;

            /// <summary>
            /// The containers whose box is being measured, innermost last: each with the inverse of its
            /// user space and its union so far, and whether it was opened inside a mask's or a marker's
            /// content — whose shapes are not its geometry.
            /// </summary>
            public readonly List<(Transform Inverse, PaintTarget Union, bool InMask, bool InMarker)> Collectors =
                new List<(Transform, PaintTarget, bool, bool)>();

            /// <summary>
            /// Adds a shape's, a text's or an image's box, drawn in <paramref name="local"/>, to every
            /// container being measured (§7.11: a container's box is the union of its children's, each
            /// through its transform), its four corners taken into each container's space.
            /// </summary>
            public void Collect(PaintTarget box, Transform local)
            {
                if (!box.HasBounds) return;
                foreach (var (inverse, union, inMask, inMarker) in Collectors)
                {
                    if (inMask != InMask || inMarker != InMarker) continue;
                    var into = Transform.Multiply(inverse, local);
                    foreach (var (x, y) in new[] { (box.MinX, box.MinY), (box.MaxX, box.MinY), (box.MaxX, box.MaxY), (box.MinX, box.MaxY) })
                    {
                        var p = into.Apply(x, y);
                        union.Track(p.X, p.Y);
                    }
                }
            }

            /// <summary>The page's <c>/Pattern</c> entries, one per gradient or pattern paint.</summary>
            public readonly StringBuilder PatternEntries = new StringBuilder();
            private int _patterns;

            /// <summary>The <c>pattern</c> elements whose content is being drawn, so one that paints with itself is drawn once.</summary>
            public readonly HashSet<XmlElement> PatternsBeingDrawn = new HashSet<XmlElement>();

            /// <summary>The <c>mask</c> elements whose content is being drawn, so a mask masked with itself, directly or down a chain, is applied once.</summary>
            public readonly HashSet<XmlElement> MasksBeingDrawn = new HashSet<XmlElement>();

            /// <summary>The <c>marker</c> elements whose content is being drawn, so a marker marking itself, directly or down a chain, is drawn once.</summary>
            public readonly HashSet<XmlElement> MarkersBeingDrawn = new HashSet<XmlElement>();

            /// <summary>
            /// How many markers' contents are being drawn, one inside another. A marker's content is drawn
            /// at a fixed tree depth, so the tree-depth guard does not see a chain of distinct markers;
            /// this counter, against <see cref="PdfLimits.MaxTreeDepth"/>, bounds it.
            /// </summary>
            public int MarkerDepth;

            /// <summary>
            /// The shape being drawn, while it is: its user-space bounds, which <see cref="MapXY"/>
            /// widens, and the gradient paints it has asked for, which are made when it is done and
            /// its bounds known. <c>null</c> for anything that is not a shape, such as text.
            /// </summary>
            public PaintTarget Paint;

            /// <summary>
            /// A pattern name for <paramref name="gradient"/>, made into a pattern when the shape is done,
            /// with <paramref name="mask"/>, the soft-mask state of its stops' opacities, or <c>null</c>.
            /// </summary>
            public string ReservePattern(XmlElement gradient, string mask)
            {
                string name = NewPatternName();
                Paint.Pending.Add((name, gradient, mask));
                return name;
            }

            public string NewPatternName() => "P" + _patterns++.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>A shape's user-space bounds and its pending gradient paints; see <see cref="ConvertContext.Paint"/>.</summary>
        private sealed class PaintTarget
        {
            public bool HasBounds;
            public float MinX, MinY, MaxX, MaxY;
            /// <summary>A box measured before drawing — a text's — which the points drawn do not widen.</summary>
            public bool Fixed;
            public readonly List<(string Name, XmlElement Gradient, string Mask)> Pending = new List<(string, XmlElement, string)>();

            /// <summary>
            /// The soft-mask states the fill's and the stroke's gradients paint through, or <c>null</c>;
            /// and where the shape's path begins in the content, so it can be painted once per mask.
            /// </summary>
            public string FillMask, StrokeMask;
            public int PathStart;

            public void Track(float x, float y)
            {
                if (Fixed) return;
                if (!HasBounds) { MinX = MaxX = x; MinY = MaxY = y; HasBounds = true; return; }
                if (x < MinX) MinX = x;
                if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y;
                if (y > MaxY) MaxY = y;
            }
        }

        /// <summary>
        /// An SVG 2×3 affine matrix <c>[a b c d e f]</c>: <c>x' = a·x + c·y + e</c>,
        /// <c>y' = b·x + d·y + f</c> — the SVG shape of <see cref="Rendering.PdfMatrix"/>, whose
        /// arithmetic every operation here goes through (the converter sits in the renderers' ring at
        /// their precision, so it has no arithmetic of its own).
        ///
        /// <para><b>Why a matrix and not four fields.</b> The predecessor kept
        /// <c>Tx, Ty, Sx, Sy</c> and composed them as <c>tx += parent.Tx; sx *= parent.Sx</c>,
        /// which is not what nesting means: an outer <c>scale(s)</c> around an inner
        /// <c>translate(b)</c> must place a point at <c>s·(b + x)</c>, and the four-field form
        /// produced <c>s·x + b</c>. Every nested group mixing a scale with a translate landed in
        /// the wrong place, and the error compounded with depth.</para>
        /// </summary>
        private readonly struct Transform
        {
            public readonly float A, B, C, D, E, F;

            public Transform(float a, float b, float c, float d, float e, float f)
            { A = a; B = b; C = c; D = d; E = e; F = f; }

            private Transform(float[] m) : this(m[0], m[1], m[2], m[3], m[4], m[5]) { }

            private float[] ToArray() => new[] { A, B, C, D, E, F };

            /// <summary>
            /// The neutral transform. <b>Always start from this, never from <c>new Transform()</c></b>
            /// — the struct's default has <c>A = D = 0</c>, which silently collapses every shape
            /// that is scaled through it to a point.
            /// </summary>
            public static Transform Identity => new Transform(1, 0, 0, 1, 0, 0);

            /// <summary>The point (<paramref name="x"/>, <paramref name="y"/>) mapped.</summary>
            public (float X, float Y) Apply(float x, float y)
            {
                var (px, py) = Rendering.PdfMatrix.Transform(ToArray(), x, y);
                return (px, py);
            }

            /// <summary>
            /// The inverse, when there is one: a transform that collapses the plane, or whose
            /// determinant is not finite, has none. Any other determinant inverts, however small —
            /// a <c>viewBox</c> may scale a large coordinate system far down.
            /// </summary>
            public bool TryInvert(out Transform inverse)
            {
                float det = A * D - B * C;
                float[] m = float.IsNaN(det) || float.IsInfinity(det) ? null : Rendering.PdfMatrix.Invert(ToArray(), 0f);
                inverse = m == null ? Identity : new Transform(m);
                return m != null;
            }

            /// <summary>
            /// <paramref name="parent"/> × <paramref name="child"/> — the child's own transform
            /// applied first, then the parent's, which is what SVG inheritance and a left-to-right
            /// <c>transform</c> list both mean: <see cref="Rendering.PdfMatrix.Compose"/> of the
            /// child then the parent.
            /// </summary>
            public static Transform Multiply(Transform parent, Transform child)
                => new Transform(Rendering.PdfMatrix.Compose(child.ToArray(), parent.ToArray()));

            public static Transform Translate(float tx, float ty) => new Transform(1, 0, 0, 1, tx, ty);
            public static Transform Scale(float sx, float sy) => new Transform(sx, 0, 0, sy, 0, 0);
        }

        /// <summary>
        /// Walks one SVG element, capped at <see cref="PdfLimits.MaxTreeDepth"/> levels of nesting.
        /// </summary>
        /// <remarks>
        /// <b>The cap is what stands between untrusted SVG and a torn-down process.</b>
        /// <c>XmlDocument.Load</c> builds the DOM iteratively and this reader is already hardened
        /// against DTD attacks, so the converter's own <c>ProcessNode</c> → <c>EmitGroup</c> →
        /// <c>ProcessNode</c> recursion was the only unbounded thing left: roughly 200 KB of
        /// <c>&lt;g&gt;</c> open tags overflowed the stack, and a .NET
        /// <c>StackOverflowException</c> cannot be caught — the process dies, taking every other
        /// request in a server host with it. Past the cap the subtree is dropped and
        /// <see cref="LastError"/> says so; the enclosing q/Q pairs are still balanced because the
        /// refusal happens on entry, before either is written.
        /// </remarks>
        private static void ProcessNode(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform, int depth)
        {
            if (node == null) return;
            if (depth > PdfLimits.MaxTreeDepth)
            {
                Report($"SVG element nesting exceeded {PdfLimits.MaxTreeDepth} levels; " +
                       "the deeper content was not converted.");
                return;
            }

            // Conditional processing (§5.8.3) applies to every element, a switch's children and any other:
            // one whose conditions are false is not rendered, nor anything it holds.
            if (node is XmlElement conditional && !ConditionsHold(conditional, ctx)) return;

            // The target of a link inside the drawing: the bounds of everything it maps to
            // the page are captured as it is drawn — the first time, so a copy a <use> makes later does
            // not move the destination — the way an anchor's own rectangle is, and folded outward into
            // an anchor's tracking round it. The link's annotation is written once the drawing is done.
            if (ctx.LinkTargets.Count > 0 && node is XmlElement target && target.GetAttribute("id") is { Length: > 0 } targetId
                && ctx.LinkTargets.Contains(targetId) && !ctx.TargetBounds.ContainsKey(targetId) && ctx.CapturingTargets.Add(targetId))
            {
                var scope = BoundsScope.Begin(ctx);
                ProcessNode(node, sb, ctx, xform, depth);
                if (scope.End(ctx, out float tMinX, out float tMinY, out float tMaxX, out float tMaxY))
                    ctx.TargetBounds[targetId] = (tMinX, tMinY, tMaxX, tMaxY);
                ctx.CapturingTargets.Remove(targetId);
                return;
            }

            // The root svg's viewBox is the page mapping (ctx). A nested svg is a viewport of its own
            // (§7.7): it drew its content with neither its position nor its viewBox.
            if (node.LocalName == "svg")
            {
                bool isRoot = node == node.OwnerDocument?.DocumentElement;
                // A filter on an svg (Filter Effects 1 §7) filters what it draws, as a container's
                // does: its box is its children's in the space it is placed in (§7.11). It was
                // reported and the svg drawn without it.
                XmlElement svgFilter = ctx.InClipPath ? null : FilterOf((XmlElement)node, ctx);
                float vx = TryParseAttrF(node, "x", 0f, ctx), vy = TryParseAttrF(node, "y", 0f, ctx);
                string vw = GetAttr(node, "width"), vh = GetAttr(node, "height");
                var inner = isRoot ? xform : Transform.Multiply(xform, ViewportTransform((XmlElement)node, vx, vy, vw, vh, ctx));
                void DrawViewport(StringBuilder content)
                {
                    bool clipped = !isRoot && OpenViewportClip((XmlElement)node, vx, vy, vw, vh, content, ctx, xform);
                    void DrawChildren()
                    {
                        foreach (XmlNode child in node.ChildNodes)
                            ProcessNode(child, content, ctx, inner, depth + 1);
                    }
                    // The root's viewport is the context's from the start.
                    if (isRoot) DrawChildren();
                    else InViewport((XmlElement)node, vw, vh, ctx, DrawChildren);
                    if (clipped) content.AppendLine("Q");
                }
                if (svgFilter == null)
                {
                    AsGroup(node, sb, ctx, DrawViewport);
                    return;
                }
                XmlNode outerFilteredSvg = ctx.Filtered;
                ctx.Filtered = node;
                var svgBox = new PaintTarget();
                bool measured = xform.TryInvert(out Transform intoSvg);
                if (measured) ctx.Collectors.Add((intoSvg, svgBox, ctx.InMask, ctx.InMarker));
                var drawn = new StringBuilder();
                AsGroup(node, drawn, ctx, DrawViewport);
                if (measured) ctx.Collectors.RemoveAt(ctx.Collectors.Count - 1);
                ctx.Filtered = outerFilteredSvg;
                sb.Append(Filtered(svgFilter, drawn, xform, measured ? svgBox : null, OwnOpacity(node, ctx), ctx, depth));
                return;
            }

            // A shape or a text collects its box as it is drawn — for a gradient in objectBoundingBox
            // units, whose patterns are made once it is done (FinishPatterns), and for a clip or a mask
            // measured in the box. Every node sets its own, so nothing before it leaks in; markers,
            // drawn inside a shape, clear it (DrawMarkers).
            PaintTarget paintTarget = !ctx.ClipMode && node.LocalName is "rect" or "circle" or "ellipse" or "line"
                or "polyline" or "polygon" or "path" or "text" ? new PaintTarget() : null;
            // An image's box is its place, known before it is drawn.
            PaintTarget imageBox = !ctx.ClipMode && node.LocalName == "image" ? ImageBox((XmlElement)node, ctx) : null;
            // A container's box is the union of what it draws (§7.11), each child's box taken into its
            // space as it finishes (ConvertContext.Collect).
            bool container = !ctx.ClipMode && node.LocalName is "g" or "a" or "switch" or "use";
            // filter (Filter Effects 1): what the element draws, filtered as pixels (Filtered), before
            // its clip, its mask and its opacity.
            XmlElement filter = (paintTarget != null || container || imageBox != null || node.LocalName == "image")
                && !ctx.InClipPath ? FilterOf((XmlElement)node, ctx) : null;
            // Such an element — or one whose clip or mask is measured in its box — is drawn first, into
            // a buffer, and the filter, the clip and the mask are set around it once the box is known.
            bool boxed = filter != null || (paintTarget != null || container) && NeedsBox((XmlElement)node, ctx);
            XmlNode outerFiltered = ctx.Filtered;
            if (filter != null) ctx.Filtered = node;
            PaintTarget union = null;
            if (boxed && container && ApplyTransformAttr(node, xform).TryInvert(out Transform intoGroup))
            {
                union = new PaintTarget();
                ctx.Collectors.Add((intoGroup, union, ctx.InMask, ctx.InMarker));
            }
            // A clip-path on a shape inside a clipPath clips that shape's part of the region (CSS
            // Masking §6.1), which one path under one rule cannot say. It was left out without a word.
            if (ctx.ClipMode && node is XmlElement inClip && OwnPresentation(inClip, "clip-path") is string own && !AppliesNothing(own))
                ReportOnce("a 'clip-path' on an element inside a clipPath is not applied");
            StringBuilder outer = sb;
            bool clippedByPath = false, masked = false;
            if (boxed) sb = new StringBuilder();
            else
            {
                // clip-path (§14.3.5) in the element's user space — its transform included — around
                // everything it draws.
                clippedByPath = !ctx.ClipMode && node is XmlElement clipTarget
                    && OpenClipPath(clipTarget, sb, ctx, ApplyTransformAttr(node, xform), depth, box: imageBox);
                // mask (§14.4), likewise in the element's user space, inside its clip.
                masked = !ctx.InClipPath && node is XmlElement maskTarget
                    && OpenMask(maskTarget, sb, ctx, ApplyTransformAttr(node, xform), depth, box: imageBox);
            }

            // Opacity (§14.5) through an /ExtGState, set inside a q/Q pair so that it ends with the
            // shape — every opacity and every colour alpha.
            bool shape = node.LocalName is "rect" or "circle" or "ellipse" or "line" or "polyline"
                or "polygon" or "path" or "text";
            // A shape that both fills and strokes under an opacity is a transparency group (§14.5), as
            // a container is: inside the group the stroke's inner half covers the fill, and the group
            // is painted once at the opacity. As a constant alpha on each paint the fill showed
            // through that half. One paint alone is exact as a constant alpha, and stays one.
            float groupOpacity = shape && node.LocalName != "text" && !ctx.ClipMode && !ctx.InClipPath && FillsAndStrokes(node, ctx)
                ? OwnOpacity(node, ctx) : 1f;
            var (fillAlpha, strokeAlpha) = shape && !ctx.ClipMode ? Alphas(node, ctx, withoutOwnOpacity: groupOpacity < 1) : (1f, 1f);
            // An image is painted as a whole: its opacity is its own alone, since fill-opacity
            // belongs to a paint and an image has none.
            if (node.LocalName == "image" && !ctx.InClipPath) fillAlpha = OwnOpacity(node, ctx);
            bool translucent = fillAlpha < 1 || strokeAlpha < 1;
            StringBuilder outsideGroup = null;
            if (groupOpacity < 1) { outsideGroup = sb; sb = new StringBuilder(); }
            if (translucent)
            {
                sb.AppendLine("q");
                sb.AppendLine("/" + ctx.StateFor(fillAlpha, strokeAlpha) + " gs");
            }

            // A shape's own transform composes onto its parent's (§7.5), as a group's does in
            // EmitGroup. Only groups applied one, so <path transform="…"> — how an editor exports
            // nearly every shape — was drawn untransformed.
            var local = node.LocalName == "g" ? xform : ApplyTransformAttr(node, xform);
            ctx.Paint = paintTarget;
            switch (node.LocalName)
            {
                case "g":       AsGroup(node, sb, ctx, content => EmitGroup(node, content, ctx, xform, depth)); break;
                case "rect":    EmitRect(node, sb, ctx, local);  break;
                case "circle":  EmitCircle(node, sb, ctx, local); break;
                case "ellipse": EmitEllipse(node, sb, ctx, local); break;
                case "line":    EmitLine(node, sb, ctx, local);  break;
                case "polyline": EmitPolyline(node, sb, ctx, local, close: false); break;
                case "polygon": EmitPolyline(node, sb, ctx, local, close: true);  break;
                case "path":    EmitPath(node, sb, ctx, local);  break;
                case "text":    EmitText(node, sb, ctx, local);  break;
                case "use":     AsGroup(node, sb, ctx, content => EmitUse(node, content, ctx, local, depth)); break;
                case "image":   EmitImage(node, sb, ctx, local, depth); break;
                // <a> (§17.1) is a group whose link this converter does not keep; <switch> (§5.8)
                // draws its first child it can render.
                case "a":       AsGroup(node, sb, ctx, content => EmitAnchor(node, content, ctx, xform, depth)); break;
                case "switch":  AsGroup(node, sb, ctx, content => EmitSwitch(node, content, ctx, local, depth)); break;
                // A symbol is drawn only through a use (§5.5), in the use's viewport; on its own it
                // draws nothing.
                case "symbol":
                    if (node.ParentNode is XmlElement user && user.LocalName == "use")
                    {
                        string uw = GetAttr(user, "width"), uh = GetAttr(user, "height");
                        var inner = Transform.Multiply(local, ViewportTransform((XmlElement)node, 0f, 0f, uw, uh, ctx));
                        AsGroup(node, sb, ctx, content =>
                        {
                            bool clipped = OpenViewportClip((XmlElement)node, 0f, 0f, uw, uh, content, ctx, local);
                            InViewport((XmlElement)node, uw, uh, ctx, () =>
                            {
                                foreach (XmlNode child in node.ChildNodes)
                                    ProcessNode(child, content, ctx, inner, depth + 1);
                            });
                            if (clipped) content.AppendLine("Q");
                        });
                    }
                    break;
                // Draw nothing by definition: definitions are drawn only where referenced, and the
                // others are descriptive.
                case "defs": case "title": case "desc": case "metadata": case "style": case "clipPath": case "marker": case "mask":
                case "linearGradient": case "radialGradient": case "pattern": break;
                default:
                    // An element the converter does not draw was dropped without a word — a <use>,
                    // an <image>, a gradient's shape. Once per name, however many there are.
                    if (node is XmlElement)
                        ReportOnce($"<{node.LocalName}> elements are not drawn by this converter");
                    break;
            }
            if (paintTarget != null)
            {
                FinishPatterns(paintTarget, ctx, local, depth);
                ctx.Collect(paintTarget, local);
            }
            if (imageBox != null) ctx.Collect(imageBox, local);
            if (union != null) ctx.Collectors.RemoveAt(ctx.Collectors.Count - 1);
            if (translucent) sb.AppendLine("Q");
            if (outsideGroup != null)
            {
                StringBuilder grouped = sb;
                sb = outsideGroup;
                EmitTransparencyGroup(sb, ctx, groupOpacity, content => content.Append(grouped));
            }
            ctx.Filtered = outerFiltered;
            if (boxed)
            {
                // The box is known: the filter, then the clip and the mask, go round what was drawn.
                StringBuilder body = sb;
                sb = outer;
                var space = ApplyTransformAttr(node, xform);
                PaintTarget box = paintTarget ?? union ?? imageBox;
                if (filter != null) body = Filtered(filter, body, space, box, OwnOpacity(node, ctx), ctx, depth);
                clippedByPath = OpenClipPath((XmlElement)node, sb, ctx, space, depth, box);
                masked = !ctx.InClipPath && OpenMask((XmlElement)node, sb, ctx, space, depth, box);
                sb.Append(body);
            }
            if (masked) sb.AppendLine("Q");
            if (clippedByPath) sb.AppendLine("Q");
        }

        /// <summary>
        /// Whether the element's clip or mask is measured in its bounding box: a clipPath in
        /// <c>objectBoundingBox</c> units anywhere in its chain, or any mask — whose region is in the
        /// box unless it says otherwise, and whose content may be.
        /// </summary>
        private static bool NeedsBox(XmlElement element, ConvertContext ctx)
        {
            string clipValue = OwnPresentation(element, "clip-path");
            XmlElement clip = Referenced(clipValue, ctx, "clipPath");
            if (clip != null && ClipChain(clip, ctx).Any(InBox)) return true;
            // A CSS basic shape (CSS Masking 1 §5.1) is measured in the element's box unless it names the view-box.
            if (IsBasicShapeClip(clipValue)) return true;
            return Referenced(OwnPresentation(element, "mask"), ctx, "mask") != null;
        }

        /// <summary>A <c>clip-path</c> value that is not none, inherit or a <c>url(</c> reference: a basic shape, or a value reported as neither.</summary>
        private static bool IsBasicShapeClip(string value)
            => value != null && !AppliesNothing(value) && !value.TrimStart().StartsWith("url(", StringComparison.Ordinal);

        /// <summary>The <paramref name="localName"/> element a <c>url(#id)</c> value names, or <c>null</c>.</summary>
        private static XmlElement Referenced(string value, ConvertContext ctx, string localName)
        {
            if (value == null) return null;
            string v = value.Trim();
            if (!v.StartsWith("url(#", StringComparison.Ordinal) || !v.EndsWith(")", StringComparison.Ordinal)) return null;
            return ctx.ElementsById.TryGetValue(v.Substring(5, v.Length - 6).Trim().Trim('\'', '"'), out XmlElement e)
                && e.LocalName == localName ? e : null;
        }

        /// <summary>
        /// An image's box: (x, y, width, height), or <c>null</c> without a positive width and height —
        /// an image that takes its own size is not measured here.
        /// </summary>
        private static PaintTarget ImageBox(XmlElement image, ConvertContext ctx)
        {
            float x = TryParseAttrF(image, "x", 0f, ctx), y = TryParseAttrF(image, "y", 0f, ctx);
            float w = TryParseAttrF(image, "width", 0f, ctx), h = TryParseAttrF(image, "height", 0f, ctx);
            if (!(w > 0) || !(h > 0)) return null;
            var box = new PaintTarget();
            box.Track(x, y);
            box.Track(x + w, y + h);
            return box;
        }

        /// <summary>The unit square onto a box, for what is measured in <c>objectBoundingBox</c> units.</summary>
        private static Transform BoxTransform(PaintTarget box)
            => new Transform(box.MaxX - box.MinX, 0, 0, box.MaxY - box.MinY, box.MinX, box.MinY);

        // ── Image ─────────────────────────────────────────────────────────────

        /// <summary>
        /// An image XObject with its alpha as a soft mask, both deflated, the mask in grey; returns
        /// its resource name. <paramref name="deflatedRgb"/> is already deflated, <paramref name="alpha"/>
        /// not: a PNG's and a filter's result alike.
        /// </summary>
        private static string ImageWithAlpha(int width, int height, byte[] deflatedRgb, byte[] alpha, ConvertContext ctx)
        {
            string size = $"/Width {width} /Height {height} /BitsPerComponent 8 /Filter /FlateDecode";
            int mask = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.StreamObject(
                "/Type /XObject /Subtype /Image /ColorSpace /DeviceGray " + size,
                Core.IO.Filters.FlateEncodeFilter.Encode(alpha)));
            int number = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.StreamObject(
                $"/Type /XObject /Subtype /Image /ColorSpace /DeviceRGB {size} /SMask {mask} 0 R", deflatedRgb));
            string name = "Im" + ctx.XObjectCount++.ToString(CultureInfo.InvariantCulture);
            ctx.ListXObject(name, number);
            return name;
        }

        /// <summary>An image ready to go into the content stream inline: its size, its dictionary, its bytes.</summary>
        private sealed class InlineImage
        {
            public int Width, Height;
            public string Dictionary;
            public byte[] Data;
            /// <summary>A PNG's alpha plane when any pixel is not opaque, or <c>null</c>: the image then goes out as an XObject with a soft mask.</summary>
            public byte[] Alpha;
        }

        /// <summary>
        /// <c>&lt;image&gt;</c> (§5.7): a <c>data:</c> URI — a PNG, a JPEG, or a GIF, WebP or other
        /// raster SkiaSharp decodes — drawn as an inline image (<c>BI … ID … EI</c>), which needs no
        /// resource, so the one-page skeleton carries it, or as an XObject with a soft mask where it
        /// has transparency; an SVG document (<c>image/svg+xml</c>) as a nested viewport
        /// (<see cref="EmitSvgImage"/>). A raster's pixel box is mapped onto (x, y, width, height) by
        /// <c>preserveAspectRatio</c>, as a viewport is, and clipped to it; a missing width or height
        /// is the image's own, one user unit a pixel.
        /// </summary>
        /// <remarks>
        /// An image is drawn, GIF, WebP and SVG included, so a logo or a photo in an exported drawing
        /// is never a hole. A reference outside the document is not fetched, and what cannot be drawn is reported.
        /// </remarks>
        private static void EmitImage(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform, int depth)
        {
            if (ctx.InClipPath)
            {
                ReportOnce("an <image> in a clipPath is not applied; the clip is its other shapes");
                return;
            }
            var element = (XmlElement)node;
            string href = element.GetAttribute("href");
            if (href.Length == 0) href = element.GetAttribute("href", XlinkNamespace);
            href = href.Trim();
            if (TryParseDataUri(href, out string type, out bool base64, out string payload) && type == "image/svg+xml")
            {
                EmitSvgImage(element, base64, payload, sb, ctx, xform, depth);
                return;
            }
            if (!TryInlineImage(href, out InlineImage image)) return;

            string width = GetAttr(node, "width") ?? image.Width.ToString(CultureInfo.InvariantCulture);
            string height = GetAttr(node, "height") ?? image.Height.ToString(CultureInfo.InvariantCulture);
            float x = TryParseAttrF(node, "x", 0f, ctx), y = TryParseAttrF(node, "y", 0f, ctx);
            var viewport = MapViewBox(new float[] { 0, 0, image.Width, image.Height },
                element.GetAttribute("preserveAspectRatio"), x, y, width, height, ctx);

            // The page mapping (MapXY's), the element's transform, the viewport, and the unit square
            // of image space onto the pixel box, whose first row is at the top.
            var m = Transform.Multiply(Transform.Multiply(Transform.Multiply(PageMatrix(ctx), xform), viewport),
                new Transform(image.Width, 0, 0, -image.Height, 0, image.Height));

            bool clipped = OpenViewportClip(element, x, y, width, height, sb, ctx, xform);
            if (ctx.Tracking)
                foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
                    ctx.Track(m.A * u + m.C * v + m.E, m.B * u + m.D * v + m.F);
            sb.AppendLine("q");
            sb.AppendLine(F($"{m.A} {m.B} {m.C} {m.D} {m.E} {m.F} cm"));
            if (image.Alpha != null)
            {
                sb.AppendLine("/" + ImageWithAlpha(image.Width, image.Height, image.Data, image.Alpha, ctx) + " Do");
                sb.AppendLine("Q");
                if (clipped) sb.AppendLine("Q");
                return;
            }
            sb.Append("BI ").Append(image.Dictionary).Append(" ID ");
            foreach (byte b in image.Data) sb.Append((char)b);   // the content stream is Latin-1: one char, one byte
            // One white-space byte before EI, not AppendLine's \r\n: a reader takes the data to end
            // just before a single one, so a second would be read as the image's last byte.
            sb.Append('\n');
            sb.AppendLine("EI");
            sb.AppendLine("Q");
            if (clipped) sb.AppendLine("Q");
        }

        /// <summary>
        /// A <c>data:</c> URI (RFC 2397) taken apart: its media type, lower-cased, whether a
        /// <c>;base64</c> parameter is among its parameters, and the text after the comma. False for
        /// anything that is not one.
        /// </summary>
        private static bool TryParseDataUri(string href, out string type, out bool base64, out string payload)
        {
            type = null; base64 = false; payload = null;
            if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
            int comma = href.IndexOf(',');
            if (comma < 0) return false;
            string[] meta = href.Substring(5, comma - 5).Split(';');
            type = meta[0].Trim().ToLowerInvariant();
            base64 = meta.Skip(1).Any(m => m.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase));
            payload = href.Substring(comma + 1);
            return true;
        }

        /// <summary>
        /// A <c>data:</c> URI's raster as an inline image — a PNG, a JPEG, or what SkiaSharp decodes
        /// (<see cref="TryInlineEncoded"/>) — or <c>false</c> with the reason reported. The size is
        /// checked by <see cref="Core.Writing.AuthoredImageSize"/> before anything is decoded.
        /// </summary>
        private static bool TryInlineImage(string href, out InlineImage image)
        {
            image = null;
            if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                ReportOnce("an <image> was not drawn: only a data: URI is read; a reference outside the document is not fetched");
                return false;
            }
            if (!TryParseDataUri(href, out string type, out bool base64, out string payload) || !base64)
            {
                ReportOnce("an <image> was not drawn: its data: URI is not base64");
                return false;
            }
            byte[] bytes;
            try { bytes = System.Convert.FromBase64String(payload); }
            catch (FormatException)
            {
                ReportOnce("an <image> was not drawn: its data is not valid base64");
                return false;
            }
            if (type == "image/png") return TryInlinePng(bytes, out image);
            if (type == "image/jpeg" || type == "image/jpg") return TryInlineJpeg(bytes, out image);
            return TryInlineEncoded(bytes, type, out image);
        }

        /// <summary>
        /// A raster of any other type SkiaSharp's codec reads — GIF (its first frame), WebP, BMP, ICO
        /// — decoded to straight RGBA and carried as the PNG path carries its pixels: RGB samples,
        /// deflated, with an alpha plane where any pixel is not opaque. The codec's dimensions are
        /// checked by <see cref="Core.Writing.AuthoredImageSize"/> before a pixel is decoded.
        /// </summary>
        private static bool TryInlineEncoded(byte[] bytes, string type, out InlineImage image)
        {
            image = null;
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null)
            {
                ReportOnce($"an <image> of type {type} was not drawn: its data is not an image this decodes");
                return false;
            }
            string refusal = Core.Writing.AuthoredImageSize.Refusal(codec.Info.Width, codec.Info.Height);
            if (refusal != null)
            {
                ReportOnce("an <image> was not drawn: " + refusal);
                return false;
            }
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                ReportOnce($"an <image> of type {type} was not drawn: it did not decode ({result})");
                return false;
            }
            int width = info.Width, height = info.Height, i = 0;
            var rgb = new byte[width * height * 3];
            var alpha = new byte[width * height];
            bool translucent = false;
            // Read through the one straight-pixel route, in the order it walks: rows from the top.
            Tetitulz.Pdf.Base.Images.BitmapPixels.Map(bitmap, straight =>
            {
                rgb[i * 3] = straight.Red; rgb[i * 3 + 1] = straight.Green; rgb[i * 3 + 2] = straight.Blue;
                alpha[i] = straight.Alpha;
                if (straight.Alpha < 255) translucent = true;
                i++;
                return straight;
            });
            image = new InlineImage
            {
                Width = width,
                Height = height,
                Dictionary = $"/W {width} /H {height} /CS /RGB /BPC 8 /F /Fl",
                Data = Core.IO.Filters.FlateEncodeFilter.Encode(rgb),
                Alpha = translucent ? alpha : null,
            };
            return true;
        }

        /// <summary>
        /// An <c>&lt;image&gt;</c> whose <c>data:</c> URI is an SVG document (<c>image/svg+xml</c>,
        /// base64 or percent-encoded text): parsed as the drawing was (<see cref="ParseSvg"/>), its
        /// root imported under the image and drawn as a nested <c>svg</c> viewport (§5.7) at the
        /// image's x, y, width and height — the root's own width and height where the image gives
        /// none — fitted by the image's <c>preserveAspectRatio</c> where it has one, else the root's;
        /// a root with no <c>viewBox</c> takes its own width and height for one. Its ids join the
        /// drawing's, the drawing's own first. One that does not decode or parse, or whose root is
        /// not an <c>svg</c>, is reported and not drawn.
        /// </summary>
        private static void EmitSvgImage(XmlElement image, bool base64, string payload, StringBuilder sb, ConvertContext ctx, Transform xform, int depth)
        {
            string xml;
            try { xml = base64 ? Encoding.UTF8.GetString(System.Convert.FromBase64String(payload)) : Uri.UnescapeDataString(payload); }
            catch (FormatException)
            {
                ReportOnce("an <image> was not drawn: its SVG data: URI is not valid base64");
                return;
            }
            XmlDocument inner;
            try { inner = ParseSvg(xml); }
            catch (XmlException ex)
            {
                ReportOnce("an <image> was not drawn: its SVG does not parse: " + ex.Message);
                return;
            }
            XmlElement source = inner.DocumentElement;
            if (source == null || source.LocalName != "svg")
            {
                ReportOnce("an <image> was not drawn: its SVG document's root is not an svg element");
                return;
            }
            var root = (XmlElement)image.OwnerDocument.ImportNode(source, deep: true);
            string ownWidth = GetAttr(root, "width"), ownHeight = GetAttr(root, "height");
            if (!root.HasAttribute("viewBox") && ownWidth != null && ownHeight != null)
                root.SetAttribute("viewBox", "0 0 " + F(TryParseAttrF(root, "width", 0f, ctx)) + " " + F(TryParseAttrF(root, "height", 0f, ctx)));
            root.SetAttribute("x", GetAttr(image, "x") ?? "0");
            root.SetAttribute("y", GetAttr(image, "y") ?? "0");
            string width = GetAttr(image, "width") ?? ownWidth, height = GetAttr(image, "height") ?? ownHeight;
            if (width != null) root.SetAttribute("width", width); else root.RemoveAttribute("width");
            if (height != null) root.SetAttribute("height", height); else root.RemoveAttribute("height");
            if (image.HasAttribute("preserveAspectRatio")) root.SetAttribute("preserveAspectRatio", image.GetAttribute("preserveAspectRatio"));
            foreach (var pair in IndexIds(root))
                if (!ctx.ElementsById.ContainsKey(pair.Key)) ctx.ElementsById.Add(pair.Key, pair.Value);
            image.AppendChild(root);
            try { ProcessNode(root, sb, ctx, xform, depth + 1); }
            finally { image.RemoveChild(root); }
        }

        /// <summary>
        /// An SVG document parsed with no DTD and no resolver, its white space kept — the space
        /// between two tspans is a text node of spaces alone, and the default dropped it. The
        /// drawing and an SVG <c>&lt;image&gt;</c> (<see cref="EmitSvgImage"/>) parse alike.
        /// </summary>
        private static XmlDocument ParseSvg(string svgXml)
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var sr = new StringReader(svgXml))
            using (var xr = XmlReader.Create(sr, readerSettings))
                doc.Load(xr);
            return doc;
        }

        private static bool TryInlinePng(byte[] bytes, out InlineImage image)
        {
            image = null;
            // IHDR's width and height, big-endian at 16 and 20, refused before a pixel is decoded.
            if (bytes.Length >= 24)
            {
                int SideAt(int at) => (int)Math.Min(int.MaxValue,
                    ((long)bytes[at] << 24) | ((long)bytes[at + 1] << 16) | ((long)bytes[at + 2] << 8) | bytes[at + 3]);
                string refusal = Core.Writing.AuthoredImageSize.Refusal(SideAt(16), SideAt(20));
                if (refusal != null)
                {
                    ReportOnce("an <image> was not drawn: " + refusal);
                    return false;
                }
            }
            if (!Core.Imaging.PngImageDecoder.TryDecode(bytes, out var png))
            {
                ReportOnce("an <image> was not drawn: " + Core.Imaging.PngImageDecoder.LastError);
                return false;
            }
            image = new InlineImage
            {
                Width = png.Width,
                Height = png.Height,
                Dictionary = $"/W {png.Width} /H {png.Height} /CS /RGB /BPC 8 /F /Fl",
                Data = Core.IO.Filters.FlateEncodeFilter.Encode(png.Rgb),
                // An inline image carries no soft mask, so transparency makes it an XObject (EmitImage).
                // It was composited over white, which over a coloured background showed white.
                Alpha = png.Alpha != null && png.Alpha.Any(a => a < 255) ? png.Alpha : null,
            };
            return true;
        }

        private static bool TryInlineJpeg(byte[] bytes, out InlineImage image)
        {
            image = null;
            if (!Core.Imaging.RasterImageInspector.TryInspectJpeg(bytes, out var frame))
            {
                ReportOnce("an <image> was not drawn: its JPEG has no frame header this reads");
                return false;
            }
            string refusal = Core.Writing.AuthoredImageSize.Refusal(frame.Width, frame.Height);
            if (refusal != null)
            {
                ReportOnce("an <image> was not drawn: " + refusal);
                return false;
            }
            // The image writers' rule (Core.Imaging.JpegColorSpace): a four-component frame without
            // Adobe's marker, or any other count but 1 and 3, is refused rather than guessed at.
            string colorSpace = Core.Imaging.JpegColorSpace.Of(frame, out string why);
            if (colorSpace == null)
            {
                ReportOnce("an <image> was not drawn: " + why);
                return false;
            }
            string space = colorSpace == PdfColorSpaceNames.DeviceGray ? "/G"
                : colorSpace == PdfColorSpaceNames.DeviceCMYK ? "/CMYK" : "/RGB";
            double[] decodeValues = Core.Imaging.JpegColorSpace.DecodeFor(frame);
            string decode = decodeValues == null ? ""
                : " /D [" + string.Join(" ", decodeValues.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
            image = new InlineImage
            {
                Width = frame.Width,
                Height = frame.Height,
                Dictionary = $"/W {frame.Width} /H {frame.Height} /CS {space} /BPC 8 /F /DCT{decode}",
                Data = bytes,
            };
            return true;
        }

        // ── Markers ───────────────────────────────────────────────────────────

        /// <summary>A path vertex and the directions into and out of it, in user space.</summary>
        private sealed class Vertex
        {
            public Vertex(float x, float y) { X = x; Y = y; }
            public float X, Y, InX, InY, OutX, OutY;
            public bool HasIn, HasOut;
        }

        private static readonly string[] MarkerProperties = { "marker-start", "marker-mid", "marker-end" };

        /// <summary>Whether any of the element's marker properties names a marker; none inside a clip.</summary>
        private static bool HasMarkers(XmlNode node, ConvertContext ctx)
        {
            if (ctx.InClipPath) return false;
            foreach (string name in MarkerProperties)
                if (MarkerValue(node, name, out _) != null) return true;
            return false;
        }

        /// <summary>A marker property's value — the longhand, else the <c>marker</c> shorthand — or <c>null</c> for none.</summary>
        private static string MarkerValue(XmlNode node, string name, out string property)
        {
            property = name;
            string value = GetPresentation(node, name);
            if (value == null) { value = GetPresentation(node, "marker"); property = "marker"; }
            return value == null || AppliesNothing(value) ? null : value.Trim();
        }

        /// <summary>
        /// Draws <paramref name="node"/>'s markers (§11.6) at <paramref name="vertices"/>:
        /// <c>marker-start</c> at the first, <c>marker-end</c> at the last, <c>marker-mid</c> at
        /// every other.
        /// </summary>
        /// <remarks>
        /// Markers are drawn — every arrowhead of a diagram or a plotted line. A marker's content may have markers of its own; one that would draw
        /// itself, directly or down a chain, is skipped there and reported, so they cannot recurse; a
        /// reference that names no marker is reported.
        /// </remarks>
        private static void DrawMarkers(XmlNode node, List<Vertex> vertices, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            if (vertices.Count == 0) return;
            string widthValue = GetPresentation(node, "stroke-width");
            float strokeWidth = widthValue == null ? 1f : ParseLength("stroke-width", widthValue, 1f, ctx, node);
            XmlElement start = ResolveMarker(node, "marker-start", ctx), mid = ResolveMarker(node, "marker-mid", ctx),
                end = ResolveMarker(node, "marker-end", ctx);

            // InMarker stays set through a nested marker's content: nothing drawn there is a container's geometry.
            bool outerInMarker = ctx.InMarker;
            ctx.InMarker = true;
            // A marker is not the shape's geometry: its viewport clip, mapped here before any node of its
            // content sets a target of its own, must not widen the shape's box.
            PaintTarget shape = ctx.Paint;
            ctx.Paint = null;
            try
            {
                for (int i = 0; i < vertices.Count; i++)
                {
                    XmlElement marker = i == 0 ? start : i == vertices.Count - 1 ? end : mid;
                    if (marker != null) DrawMarker(marker, vertices[i], isStart: i == 0, strokeWidth, sb, ctx, xform);
                }
            }
            finally { ctx.InMarker = outerInMarker; ctx.Paint = shape; }
        }

        /// <summary>The marker element a marker property names, or <c>null</c>; an unresolvable reference is reported.</summary>
        private static XmlElement ResolveMarker(XmlNode node, string name, ConvertContext ctx)
        {
            string value = MarkerValue(node, name, out string property);
            if (value == null) return null;
            if (value.StartsWith("url(#", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal)
                && ctx.ElementsById.TryGetValue(value.Substring(5, value.Length - 6).Trim().Trim('\'', '"'), out XmlElement marker)
                && marker.LocalName == "marker")
                return marker;
            ReportOnce($"'{property}' was not applied: it names no marker in this document");
            return null;
        }

        /// <summary>
        /// One marker at <paramref name="vertex"/>: its content's viewBox mapped onto its
        /// <c>markerWidth</c> × <c>markerHeight</c> viewport (3 × 3 by default), the reference point
        /// (<c>refX</c>, <c>refY</c>) put on the vertex, turned by <c>orient</c> — an angle, or
        /// <c>auto</c>: the path's direction there, the bisector at a middle vertex; or SVG 2's
        /// <c>auto-start-reverse</c>: auto, but pointing back at the start vertex — and scaled by
        /// the stroke width unless <c>markerUnits</c> is <c>userSpaceOnUse</c>. The viewport clips
        /// unless <c>overflow</c> is <c>visible</c>. The content inherits from the marker's own
        /// ancestors, not from the path.
        /// </summary>
        private static void DrawMarker(XmlElement marker, Vertex vertex, bool isStart, float strokeWidth, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            // A marker's content may mark its own shapes; the marker itself, directly or down a chain, is drawn once.
            if (!ctx.MarkersBeingDrawn.Add(marker))
            {
                ReportOnce("a marker's content has a marker that is the marker itself; there it is not drawn");
                return;
            }
            // A chain of distinct markers, each marking with the next, is bounded as the element tree is.
            if (ctx.MarkerDepth >= PdfLimits.MaxTreeDepth)
            {
                ctx.MarkersBeingDrawn.Remove(marker);
                ReportOnce($"markers nested more than {PdfLimits.MaxTreeDepth} deep are not drawn");
                return;
            }
            ctx.MarkerDepth++;
            try
            {
                string width = GetAttr(marker, "markerWidth") ?? "3", height = GetAttr(marker, "markerHeight") ?? "3";
                var content = ViewportTransform(marker, 0f, 0f, width, height, ctx);
                // refX and refY are in the marker's content coordinates (§11.6.2): a percentage is of
                // its viewBox's side, or of its size without one.
                float[] markerBox = ViewBox(marker);
                float refX = ReferenceCoordinate(marker, "refX", markerBox?[2] ?? ViewportSide("width", width, ctx), ctx);
                float refY = ReferenceCoordinate(marker, "refY", markerBox?[3] ?? ViewportSide("height", height, ctx), ctx);
                float refPx = content.A * refX + content.C * refY + content.E;
                float refPy = content.B * refX + content.D * refY + content.F;

                string orient = marker.GetAttribute("orient").Trim();
                // auto-start-reverse was read as an angle of 0, so a double-headed arrow's start head
                // pointed right whatever the path did.
                bool reverse = orient == "auto-start-reverse";
                double angle = orient == "auto" || reverse ? VertexAngle(vertex) + (reverse && isStart ? Math.PI : 0)
                    : ParseF(orient.EndsWith("deg", StringComparison.Ordinal) ? orient.Substring(0, orient.Length - 3) : orient) * Math.PI / 180;
                float scale = marker.GetAttribute("markerUnits").Trim() == "userSpaceOnUse" ? 1f : strokeWidth;
                float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);

                var placed = Transform.Multiply(xform, new Transform(1, 0, 0, 1, vertex.X, vertex.Y));
                placed = Transform.Multiply(placed, new Transform(cos, sin, -sin, cos, 0, 0));
                placed = Transform.Multiply(placed, new Transform(scale, 0, 0, scale, 0, 0));
                placed = Transform.Multiply(placed, new Transform(1, 0, 0, 1, -refPx, -refPy));

                bool clipped = OpenViewportClip(marker, 0f, 0f, width, height, sb, ctx, placed);
                var inner = Transform.Multiply(placed, content);
                InViewport(marker, width, height, ctx, () =>
                {
                    foreach (XmlNode child in marker.ChildNodes)
                        ProcessNode(child, sb, ctx, inner, depth: 1);
                });
                if (clipped) sb.AppendLine("Q");
            }
            finally { ctx.MarkerDepth--; ctx.MarkersBeingDrawn.Remove(marker); }
        }

        /// <summary>The path's direction at a vertex, in radians: the bisector of in and out, or whichever there is.</summary>
        private static double VertexAngle(Vertex v)
        {
            double aIn = Math.Atan2(v.InY, v.InX), aOut = Math.Atan2(v.OutY, v.OutX);
            if (!v.HasIn) return v.HasOut ? aOut : 0;
            if (!v.HasOut) return aIn;
            return Math.Atan2(Math.Sin(aIn) + Math.Sin(aOut), Math.Cos(aIn) + Math.Cos(aOut));
        }

        // ── Clip path ─────────────────────────────────────────────────────────

        /// <summary>
        /// Sets <paramref name="element"/>'s <c>clip-path</c> (§14.3.5): the referenced clipPath's
        /// shapes, in the element's user space and the clipPath's own transform — as paths only
        /// (<see cref="ConvertContext.ClipMode"/>) then <c>W n</c>, <c>W* n</c> for a <c>clip-rule</c>
        /// of <c>evenodd</c>, where one path under one rule says it; else as a luminosity soft mask of
        /// the shapes filled white (<see cref="NeedsMask"/>). Returns whether a clip was opened; the
        /// caller closes it with <c>Q</c>.
        /// </summary>
        /// <remarks>
        /// A <c>url(#id)</c> to a clipPath, or a CSS basic shape (<see cref="OpenBasicShapeClip"/>);
        /// anything else is reported and the element drawn unclipped. An empty clipPath clips
        /// everything away, so a zero-size path stands in for its shapes. A clipPath's own
        /// <c>clip-path</c> intersects its region with the next one's (<see cref="ClipChain"/>), each
        /// set after the last in the same user space and box.
        /// </remarks>
        private static bool OpenClipPath(XmlElement element, StringBuilder sb, ConvertContext ctx, Transform xform, int depth, PaintTarget box)
        {
            string value = OwnPresentation(element, "clip-path");
            if (value == null || AppliesNothing(value)) return false;
            if (IsBasicShapeClip(value)) return OpenBasicShapeClip(value.Trim(), element, sb, ctx, xform, box);
            XmlElement clip = ClipNamed(value, ctx);
            if (clip == null) return false;
            List<XmlElement> chain = ClipChain(clip, ctx);
            // In objectBoundingBox units a clip's content is in the element's box (a shape's, a text's,
            // an image's or a container's union); an element with none has it reported.
            if (chain.Any(InBox) && (box == null || !box.HasBounds))
            {
                ReportOnce("'clip-path' with clipPathUnits=\"objectBoundingBox\" was not applied: the element's box is not known");
                return false;
            }

            sb.AppendLine("q");
            // A clipPath one path under one rule can say is that path, W n; one it cannot is a
            // luminosity mask (NeedsMask). Every link intersects: each path with the clip so far, and
            // the masks with each other through one state, since a second gs would replace the first.
            var masks = new List<XmlElement>();
            foreach (XmlElement link in chain)
            {
                if (NeedsMask(link, ctx)) masks.Add(link);
                else WriteClip(link, sb, ctx, xform, depth, box);
            }
            if (masks.Count > 0) sb.AppendLine("/" + ClipMaskState(masks, ctx, xform, depth, box) + " gs");
            return true;
        }

        /// <summary>
        /// A marker's <c>refX</c> or <c>refY</c> (§11.6.2): a percentage of <paramref name="side"/>, the
        /// marker's own viewport's; any other length as <see cref="ParseLength"/> reads it; 0 when absent.
        /// </summary>
        private static float ReferenceCoordinate(XmlElement marker, string name, float side, ConvertContext ctx)
        {
            string value = GetAttr(marker, name)?.Trim();
            if (string.IsNullOrEmpty(value)) return 0f;
            if (value.EndsWith("%", StringComparison.Ordinal)
                && float.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
                return side * percent / 100;
            return ParseLength(name, value, 0f, ctx, marker);
        }

        /// <summary>
        /// Sets a <c>clip-path</c> that is a CSS basic shape (CSS Masking 1 §5.1, CSS Shapes 1 §5.1) —
        /// <c>inset()</c>, <c>circle()</c>, <c>ellipse()</c>, <c>polygon()</c>, <c>path()</c> — with an
        /// optional geometry box before or after it. The shape is a path in the element's user space,
        /// measured in its reference box: the element's box (fill-box, and the default), that box grown
        /// by half the stroke width on every side when the element strokes (stroke-box —
        /// the stroke's bounding box less a join's spike), or the nearest viewport (view-box); then <c>W n</c>, or
        /// <c>W* n</c> for a polygon's or path's <c>evenodd</c>. Returns whether a clip was opened; the
        /// caller closes it with <c>Q</c>. A shape this does not read, a geometry box it does not know,
        /// and a box that is not known are reported and the element drawn unclipped; a shape with no
        /// area clips everything away.
        /// </summary>
        private static bool OpenBasicShapeClip(string value, XmlElement element, StringBuilder sb, ConvertContext ctx, Transform xform, PaintTarget box)
        {
            int open = value.IndexOf('('), close = value.LastIndexOf(')');
            if (open <= 0 || close < open)
            {
                ReportOnce($"'clip-path' was not applied: '{value}' is neither a url(#id) nor a basic shape");
                return false;
            }
            int nameStart = open;
            while (nameStart > 0 && (char.IsLetter(value[nameStart - 1]) || value[nameStart - 1] == '-')) nameStart--;
            string name = value.Substring(nameStart, open - nameStart).ToLowerInvariant();
            string args = value.Substring(open + 1, close - open - 1).Trim();
            string keyword = (value.Substring(0, nameStart) + " " + value.Substring(close + 1)).Trim().ToLowerInvariant();
            bool viewBox = keyword == "view-box";
            if (keyword.Length > 0 && !viewBox && keyword is not ("fill-box" or "stroke-box" or "content-box" or "padding-box" or "border-box" or "margin-box"))
            {
                ReportOnce($"'clip-path' was not applied: '{keyword}' is not a geometry box");
                return false;
            }
            float bx, by, bw, bh;
            if (viewBox) { bx = 0; by = 0; bw = ctx.ViewportWidth; bh = ctx.ViewportHeight; }
            else if (box != null && box.HasBounds) { bx = box.MinX; by = box.MinY; bw = box.MaxX - box.MinX; bh = box.MaxY - box.MinY; }
            else
            {
                ReportOnce("'clip-path' with a basic shape was not applied: the element's box is not known");
                return false;
            }
            // stroke-box (CSS Masking 1 §5.1) is the stroke's bounding box: the fill box grown by half
            // the stroke width on every side — a miter's spike not counted — when the element strokes.
            if (keyword == "stroke-box" && !PaintsNothing(GetPresentation(element, "stroke") ?? "none"))
            {
                string widthValue = GetPresentation(element, "stroke-width");
                float half = (widthValue == null ? 1f : ParseLength("stroke-width", widthValue, 1f, ctx, element)) / 2;
                bx -= half; by -= half; bw += 2 * half; bh += 2 * half;
            }

            // The path is written apart, then set as the clip: in the element's space, a path()'s
            // from the box's origin. Its points reach the shape's paint target too, harmlessly — the
            // gradients and the containers' unions are finished before a clip is set.
            var content = new StringBuilder();
            bool evenOdd = false;
            var path = new PathState(content, ctx, name == "path" ? Transform.Multiply(xform, Transform.Translate(bx, by)) : xform);
            string problem = name switch
            {
                "inset" => InsetShape(args, path, bx, by, bw, bh),
                "circle" => EllipseShape(args, path, bx, by, bw, bh, circle: true),
                "ellipse" => EllipseShape(args, path, bx, by, bw, bh, circle: false),
                "polygon" => PolygonShape(args, path, bx, by, bw, bh, out evenOdd),
                "path" => PathShape(args, path, out evenOdd),
                _ => $"{name}() is not a basic shape this converter reads",
            };
            if (problem != null)
            {
                ReportOnce("'clip-path' was not applied: " + problem);
                return false;
            }
            sb.AppendLine("q");
            if (content.Length == 0) sb.AppendLine("0 0 0 0 re");
            else sb.Append(content);
            sb.AppendLine(evenOdd ? "W*" : "W");
            sb.AppendLine("n");
            return true;
        }

        /// <summary>A shape's <c>&lt;length-percentage&gt;</c> in user units: an absolute CSS length, a bare number, or a percentage of <paramref name="extent"/>.</summary>
        private static bool TryShapeLength(string token, float extent, out float length)
        {
            length = 0;
            if (!TryNumberAndUnit(token, out float number, out string unit)) return false;
            if (unit == "%") { length = number / 100 * extent; return true; }
            if (unit.Length == 0) { length = number; return true; }
            if (!Core.Text.CssLength.TryToPixels(number, unit, out double pixels)) return false;
            length = (float)pixels;
            return true;
        }

        /// <summary>
        /// <c>inset(&lt;length-percentage&gt;{1,4} [round &lt;border-radius&gt;]?)</c>: the box shrunk by
        /// top, right, bottom and left (the shorthand's order and omissions), its corners rounded by
        /// a border-radius — one to four horizontal radii, a slash and one to four vertical ones —
        /// scaled down together where two would overlap (CSS Backgrounds 3 §5.5). No area: no path,
        /// an empty clip. <c>null</c>, or the problem.
        /// </summary>
        private static string InsetShape(string args, PathState path, float bx, float by, float bw, float bh)
        {
            var tokens = SplitAtDepthZero(args.Replace("/", " / "));
            int round = tokens.FindIndex(t => t.Equals("round", StringComparison.OrdinalIgnoreCase));
            var insets = (round < 0 ? tokens : tokens.Take(round)).ToList();
            if (insets.Count is 0 or > 4) return $"inset({args}) needs one to four insets";
            var v = new float[4];   // top, right, bottom, left
            for (int i = 0; i < 4; i++)
            {
                // The shorthand: a missing right takes the top's, a missing bottom the top's, a missing left the right's.
                int source = i < insets.Count ? i : i == 3 && insets.Count > 1 ? 1 : 0;
                if (!TryShapeLength(insets[source], i % 2 == 0 ? bh : bw, out v[i])) return $"inset({args}) has an inset this converter cannot read";
            }
            float x0 = bx + v[3], y0 = by + v[0], w = bw - v[1] - v[3], h = bh - v[0] - v[2];
            if (!(w > 0) || !(h > 0)) return null;
            var rx = new float[4]; var ry = new float[4];   // top-left, top-right, bottom-right, bottom-left
            if (round >= 0)
            {
                var radii = tokens.Skip(round + 1).ToList();
                int slash = radii.IndexOf("/");
                var horizontal = slash < 0 ? radii : radii.Take(slash).ToList();
                var vertical = slash < 0 ? radii : radii.Skip(slash + 1).ToList();
                if (horizontal.Count is 0 or > 4 || vertical.Count is 0 or > 4) return $"inset({args}) has a corner radius this converter cannot read";
                for (int i = 0; i < 4; i++)
                {
                    // border-radius's shorthand: top-left, top-right, bottom-right, bottom-left; a missing bottom-left takes the top-right's.
                    int hs = i < horizontal.Count ? i : i == 3 && horizontal.Count > 1 ? 1 : 0, vs = i < vertical.Count ? i : i == 3 && vertical.Count > 1 ? 1 : 0;
                    if (!TryShapeLength(horizontal[hs], w, out rx[i]) || !TryShapeLength(vertical[vs], h, out ry[i]) || rx[i] < 0 || ry[i] < 0)
                        return $"inset({args}) has a corner radius this converter cannot read";
                }
                float f = 1;
                void Fit(float side, float a, float b) { if (a + b > side) f = Math.Min(f, side / (a + b)); }
                Fit(w, rx[0], rx[1]); Fit(w, rx[3], rx[2]); Fit(h, ry[0], ry[3]); Fit(h, ry[1], ry[2]);
                if (f < 1) for (int i = 0; i < 4; i++) { rx[i] *= f; ry[i] *= f; }
            }
            path.MoveTo(x0 + rx[0], y0);
            path.LineTo(x0 + w - rx[1], y0);
            if (rx[1] > 0 && ry[1] > 0) path.ArcTo(rx[1], ry[1], 0, false, true, x0 + w, y0 + ry[1]);
            path.LineTo(x0 + w, y0 + h - ry[2]);
            if (rx[2] > 0 && ry[2] > 0) path.ArcTo(rx[2], ry[2], 0, false, true, x0 + w - rx[2], y0 + h);
            path.LineTo(x0 + rx[3], y0 + h);
            if (rx[3] > 0 && ry[3] > 0) path.ArcTo(rx[3], ry[3], 0, false, true, x0, y0 + h - ry[3]);
            path.LineTo(x0, y0 + ry[0]);
            if (rx[0] > 0 && ry[0] > 0) path.ArcTo(rx[0], ry[0], 0, false, true, x0 + rx[0], y0);
            path.Close();
            return null;
        }

        /// <summary>
        /// <c>circle(&lt;shape-radius&gt;? [at &lt;position&gt;]?)</c> and <c>ellipse(&lt;shape-radius&gt;{2}? [at &lt;position&gt;]?)</c>:
        /// centred in the box unless placed; a radius a length, a percentage — of the box's diagonal
        /// over √2 for a circle, of the width or the height for an ellipse's — or closest-side (the
        /// default) or farthest-side, the distance from the centre to that side. No area: an empty clip.
        /// </summary>
        private static string EllipseShape(string args, PathState path, float bx, float by, float bw, float bh, bool circle)
        {
            string fn = circle ? "circle" : "ellipse";
            var tokens = SplitAtDepthZero(args);
            int at = tokens.FindIndex(t => t.Equals("at", StringComparison.OrdinalIgnoreCase));
            var radii = (at < 0 ? tokens : tokens.Take(at)).ToList();
            var position = at < 0 ? new List<string>() : tokens.Skip(at + 1).ToList();
            if (!TryPosition(position, bx, by, bw, bh, out float cx, out float cy)) return $"{fn}({args}) has a position this converter cannot read";
            if (circle ? radii.Count > 1 : radii.Count is not (0 or 2)) return $"{fn}({args}) needs {(circle ? "one radius or none" : "two radii or none")}";
            float nearX = Math.Min(Math.Abs(cx - bx), Math.Abs(bx + bw - cx)), farX = Math.Max(Math.Abs(cx - bx), Math.Abs(bx + bw - cx));
            float nearY = Math.Min(Math.Abs(cy - by), Math.Abs(by + bh - cy)), farY = Math.Max(Math.Abs(cy - by), Math.Abs(by + bh - cy));
            float rx, ry;
            if (circle)
            {
                float extent = (float)(Math.Sqrt(bw * bw + bh * bh) / Math.Sqrt(2));
                if (!TryShapeRadius(radii.Count > 0 ? radii[0] : "closest-side", extent, Math.Min(nearX, nearY), Math.Max(farX, farY), out rx))
                    return $"{fn}({args}) has a radius this converter cannot read";
                ry = rx;
            }
            else if (!TryShapeRadius(radii.Count > 0 ? radii[0] : "closest-side", bw, nearX, farX, out rx)
                || !TryShapeRadius(radii.Count > 0 ? radii[1] : "closest-side", bh, nearY, farY, out ry))
                return $"{fn}({args}) has a radius this converter cannot read";
            if (!(rx > 0) || !(ry > 0)) return null;
            path.MoveTo(cx + rx, cy);
            path.ArcTo(rx, ry, 0, false, true, cx, cy + ry);
            path.ArcTo(rx, ry, 0, false, true, cx - rx, cy);
            path.ArcTo(rx, ry, 0, false, true, cx, cy - ry);
            path.ArcTo(rx, ry, 0, false, true, cx + rx, cy);
            path.Close();
            return null;
        }

        /// <summary>A shape radius: <c>closest-side</c>, <c>farthest-side</c>, or a non-negative length-percentage of <paramref name="extent"/>.</summary>
        private static bool TryShapeRadius(string token, float extent, float closest, float farthest, out float radius)
        {
            switch (token.ToLowerInvariant())
            {
                case "closest-side": radius = closest; return true;
                case "farthest-side": radius = farthest; return true;
            }
            return TryShapeLength(token, extent, out radius) && radius >= 0;
        }

        /// <summary>
        /// A CSS <c>&lt;position&gt;</c> in the box: none is the centre; one value places x (or y for
        /// <c>top</c>/<c>bottom</c>); two place x then y, in either order when a keyword says which;
        /// four are two edge keywords each with an offset inward from that edge.
        /// </summary>
        private static bool TryPosition(List<string> tokens, float bx, float by, float bw, float bh, out float cx, out float cy)
        {
            cx = bx + bw / 2; cy = by + bh / 2;
            bool Horizontal(string t, out float x)
            {
                switch (t.ToLowerInvariant())
                {
                    case "left": x = bx; return true;
                    case "center": x = bx + bw / 2; return true;
                    case "right": x = bx + bw; return true;
                }
                bool ok = TryShapeLength(t, bw, out float l);
                x = bx + l;
                return ok;
            }
            bool Vertical(string t, out float y)
            {
                switch (t.ToLowerInvariant())
                {
                    case "top": y = by; return true;
                    case "center": y = by + bh / 2; return true;
                    case "bottom": y = by + bh; return true;
                }
                bool ok = TryShapeLength(t, bh, out float l);
                y = by + l;
                return ok;
            }
            switch (tokens.Count)
            {
                case 0: return true;
                case 1:
                    return tokens[0].ToLowerInvariant() is "top" or "bottom" ? Vertical(tokens[0], out cy) : Horizontal(tokens[0], out cx);
                case 2:
                {
                    string a = tokens[0], b = tokens[1];
                    if (a.ToLowerInvariant() is "top" or "bottom" || b.ToLowerInvariant() is "left" or "right") (a, b) = (b, a);
                    return Horizontal(a, out cx) && Vertical(b, out cy);
                }
                case 4:
                    for (int i = 0; i < 4; i += 2)
                    {
                        bool horizontal = tokens[i].ToLowerInvariant() is "left" or "right";
                        if (!TryShapeLength(tokens[i + 1], horizontal ? bw : bh, out float offset)) return false;
                        switch (tokens[i].ToLowerInvariant())
                        {
                            case "left": cx = bx + offset; break;
                            case "right": cx = bx + bw - offset; break;
                            case "top": cy = by + offset; break;
                            case "bottom": cy = by + bh - offset; break;
                            default: return false;
                        }
                    }
                    return true;
                default: return false;
            }
        }

        /// <summary><c>polygon([&lt;fill-rule&gt;,]? [&lt;length-percentage&gt; &lt;length-percentage&gt;]#)</c>: three or more vertices in the box, under nonzero or evenodd.</summary>
        private static string PolygonShape(string args, PathState path, float bx, float by, float bw, float bh, out bool evenOdd)
        {
            evenOdd = false;
            var groups = args.Split(',').Select(g => g.Trim()).Where(g => g.Length > 0).ToList();
            if (groups.Count > 0 && groups[0].ToLowerInvariant() is "nonzero" or "evenodd")
            {
                evenOdd = groups[0].ToLowerInvariant() == "evenodd";
                groups.RemoveAt(0);
            }
            if (groups.Count < 3) return $"polygon({args}) needs three or more vertices";
            bool first = true;
            foreach (string g in groups)
            {
                var pair = SplitAtDepthZero(g);
                if (pair.Count != 2 || !TryShapeLength(pair[0], bw, out float x) || !TryShapeLength(pair[1], bh, out float y))
                    return $"polygon({args}) has a vertex this converter cannot read";
                if (first) path.MoveTo(bx + x, by + y); else path.LineTo(bx + x, by + y);
                first = false;
            }
            path.Close();
            return null;
        }

        /// <summary><c>path([&lt;fill-rule&gt;,]? &lt;string&gt;)</c>: SVG path data, from the box's origin (the path's transform is translated there), under nonzero or evenodd.</summary>
        private static string PathShape(string args, PathState path, out bool evenOdd)
        {
            evenOdd = false;
            string rest = args.Trim();
            if (rest.StartsWith("evenodd", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("nonzero", StringComparison.OrdinalIgnoreCase))
            {
                evenOdd = rest.StartsWith("evenodd", StringComparison.OrdinalIgnoreCase);
                int comma = rest.IndexOf(',');
                if (comma < 0) return $"path({args}) needs a quoted path after its fill rule";
                rest = rest.Substring(comma + 1).Trim();
            }
            if (rest.Length < 2 || rest[0] != rest[rest.Length - 1] || rest[0] is not ('"' or '\'')) return $"path({args}) needs a quoted path";
            string d = rest.Substring(1, rest.Length - 2);
            if (string.IsNullOrWhiteSpace(d)) return null;
            TracePath(d, path);
            return null;
        }

        /// <summary>
        /// Whether a clipPath is not one path under one rule (§14.3.5): text in it, a child with a
        /// <c>clip-path</c> of its own, or children whose <c>clip-rule</c>s differ. A <c>use</c> child
        /// is judged by what it references. Such a clipPath is a mask (<see cref="ClipMaskState"/>);
        /// the others are a path (<see cref="WriteClip"/>), which keeps the clip vector.
        /// </summary>
        private static bool NeedsMask(XmlElement clip, ConvertContext ctx)
        {
            bool? evenOdd = null;
            foreach (XmlElement child in clip.ChildNodes.OfType<XmlElement>())
            {
                XmlElement shape = child;
                if (child.LocalName == "use" && UseTarget(child, ctx) is XmlElement target) shape = target;
                if (shape.LocalName is "desc" or "title" or "metadata") continue;
                if (shape.LocalName == "text") return true;
                if (OwnPresentation(child, "clip-path") is string own && !AppliesNothing(own)) return true;
                if (shape != child && OwnPresentation(shape, "clip-path") is string targetOwn && !AppliesNothing(targetOwn)) return true;
                bool rule = GetPresentation(child, "clip-rule") == "evenodd";
                if (evenOdd == null) evenOdd = rule;
                else if (evenOdd != rule) return true;
            }
            return false;
        }

        /// <summary>A <c>use</c>'s reference, trimmed: its <c>href</c>, else its <c>xlink:href</c>; empty for neither.</summary>
        private static string UseHref(XmlElement use)
        {
            string href = use.GetAttribute("href");
            if (href.Length == 0) href = use.GetAttribute("href", XlinkNamespace);
            return href.Trim();
        }

        /// <summary>The element a <c>use</c>'s <c>href</c> names, or <c>null</c>; <see cref="EmitUse"/> reports what this passes over.</summary>
        private static XmlElement UseTarget(XmlElement use, ConvertContext ctx)
        {
            string href = UseHref(use);
            return href.StartsWith("#", StringComparison.Ordinal) && ctx.ElementsById.TryGetValue(href.Substring(1), out XmlElement target) ? target : null;
        }

        /// <summary>
        /// A soft-mask state for the first of <paramref name="masks"/> and those after it: each
        /// clipPath's children filled white (<see cref="ConvertContext.ClipFill"/>) in the element's
        /// user space — or its box — and the clipPath's own transform, over the black backdrop, so the
        /// element paints through where they are. The next mask, where there is one, masks this one's
        /// content, which intersects them.
        /// </summary>
        /// <remarks>
        /// Built from the last mask back, each one's content masked by the state after it: the order
        /// the recursion this replaces drew them in, one call a link. <see cref="ClipChain"/> stops a
        /// cycle, not a long chain, so a hundred thousand clipPaths each naming the next took the
        /// recursion past the stack, which nothing catches.
        /// </remarks>
        private static string ClipMaskState(List<XmlElement> masks, ConvertContext ctx, Transform xform, int depth, PaintTarget box)
        {
            string after = null;
            for (int i = masks.Count - 1; i >= 0; i--)
            {
                XmlElement clip = masks[i];
                var content = new StringBuilder();
                if (after != null)
                {
                    content.AppendLine("q");
                    content.AppendLine("/" + after + " gs");
                }
                var clipSpace = ApplyTransformAttr(clip, InBox(clip) ? Transform.Multiply(xform, BoxTransform(box)) : xform);
                bool outer = ctx.ClipFill;
                ctx.ClipFill = true;
                try
                {
                    foreach (XmlNode child in clip.ChildNodes)
                        ProcessNode(child, content, ctx, clipSpace, depth + 1);
                }
                finally { ctx.ClipFill = outer; }
                if (after != null) content.AppendLine("Q");
                after = LuminosityMaskState(content, ctx);
            }
            return after;
        }

        /// <summary>
        /// A luminosity soft-mask state (ISO 32000-1 §11.6.5.2) over <paramref name="content"/>, drawn
        /// in page space into a Form XObject with a DeviceRGB transparency group and the page's
        /// resources: its luminance over the black backdrop is the alpha an element paints through.
        /// </summary>
        private static string LuminosityMaskState(StringBuilder content, ConvertContext ctx)
        {
            int form = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.StreamObject(
                $"/Type /XObject /Subtype /Form /BBox {MediaBox(ctx.Options)} /Group << /S /Transparency /CS /DeviceRGB >> /Resources {ctx.SharedResources()} 0 R",
                Core.Text.Latin1Text.Encode(content.ToString())));
            return ctx.MaskStateFor(form);
        }

        /// <summary>The clipPath a <c>clip-path</c> value names, or <c>null</c>, reported, for any other value.</summary>
        private static XmlElement ClipNamed(string value, ConvertContext ctx)
        {
            string v = value.Trim();
            if (!v.StartsWith("url(#", StringComparison.Ordinal) || !v.EndsWith(")", StringComparison.Ordinal))
            {
                ReportOnce("'clip-path' was not applied: only a url(#id) reference to a clipPath is read");
                return null;
            }
            string id = v.Substring(5, v.Length - 6).Trim().Trim('\'', '"');
            if (!ctx.ElementsById.TryGetValue(id, out XmlElement clip) || clip.LocalName != "clipPath")
            {
                ReportOnce("'clip-path' was not applied: a reference names no clipPath in this document");
                return null;
            }
            return clip;
        }

        /// <summary>
        /// <paramref name="clip"/>, then the clipPath its own <c>clip-path</c> names, and so on
        /// (§14.3.5): the region is every one's intersected. The chain ends at a clipPath naming none,
        /// at a value naming no clipPath (reported by <see cref="ClipNamed"/>), and at one already in
        /// it, reported, so a cycle is set once each.
        /// </summary>
        /// <remarks>A clipPath's own <c>clip-path</c> was left out without a word.</remarks>
        private static List<XmlElement> ClipChain(XmlElement clip, ConvertContext ctx)
        {
            var chain = new List<XmlElement>();
            var seen = new HashSet<XmlElement>();
            while (clip != null)
            {
                if (!seen.Add(clip))
                {
                    ReportOnce("a clipPath's 'clip-path' that leads back to a clipPath already in its chain is not followed again");
                    break;
                }
                chain.Add(clip);
                string own = OwnPresentation(clip, "clip-path");
                clip = own == null || AppliesNothing(own) ? null : ClipNamed(own, ctx);
            }
            return chain;
        }

        /// <summary>Whether a clipPath's content is in the element's box (<c>clipPathUnits="objectBoundingBox"</c>).</summary>
        private static bool InBox(XmlElement clip) => clip.GetAttribute("clipPathUnits").Trim() == "objectBoundingBox";

        /// <summary>
        /// One clipPath of a chain: its shapes as paths, in the element's user space — or its box — and
        /// the clipPath's own transform, then <c>W n</c>, which intersects it with the clip already set.
        /// </summary>
        private static void WriteClip(XmlElement clip, StringBuilder sb, ConvertContext ctx, Transform xform, int depth, PaintTarget box)
        {
            int start = sb.Length;
            var clipSpace = ApplyTransformAttr(clip, InBox(clip) ? Transform.Multiply(xform, BoxTransform(box)) : xform);
            ctx.ClipMode = true;
            try
            {
                foreach (XmlNode child in clip.ChildNodes)
                    ProcessNode(child, sb, ctx, clipSpace, depth + 1);
            }
            finally { ctx.ClipMode = false; }
            if (sb.Length == start) sb.AppendLine("0 0 0 0 re");

            XmlElement first = clip.ChildNodes.OfType<XmlElement>().FirstOrDefault() ?? clip;
            sb.AppendLine(GetPresentation(first, "clip-rule") == "evenodd" ? "W*" : "W");
            sb.AppendLine("n");
        }

        // ── Mask ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets <paramref name="element"/>'s <c>mask</c> (§14.4) as a luminosity soft mask (ISO 32000-1
        /// §11.6.5.2): the referenced mask's content drawn into a Form XObject with a DeviceRGB
        /// transparency group, in the element's user space, whose luminance over the black backdrop is
        /// the alpha the element paints through — nothing where the mask draws nothing. Returns whether
        /// a mask was set; the caller closes it with <c>Q</c>.
        /// </summary>
        /// <remarks>
        /// Only a <c>url(#id)</c> to a mask whose content is in <c>userSpaceOnUse</c> units is applied;
        /// anything else is reported and the element drawn unmasked. The mask's region (<c>x</c>,
        /// <c>y</c>, <c>width</c>, <c>height</c>) is not applied and is reported when set, so its
        /// content is not clipped to it. A mask's content may be masked in turn; a mask
        /// that would draw itself, directly or down a chain, is not applied there, so masks cannot
        /// recurse.
        /// </remarks>
        private static bool OpenMask(XmlElement element, StringBuilder sb, ConvertContext ctx, Transform xform, int depth, PaintTarget box)
        {
            string value = OwnPresentation(element, "mask");
            if (value == null || AppliesNothing(value)) return false;
            string v = value.Trim();
            if (!v.StartsWith("url(#", StringComparison.Ordinal) || !v.EndsWith(")", StringComparison.Ordinal))
            {
                ReportOnce("'mask' was not applied: only a url(#id) reference to a mask is read");
                return false;
            }
            if (!ctx.ElementsById.TryGetValue(v.Substring(5, v.Length - 6).Trim().Trim('\'', '"'), out XmlElement mask)
                || mask.LocalName != "mask")
            {
                ReportOnce("'mask' was not applied: a reference names no mask in this document");
                return false;
            }
            // In objectBoundingBox units the content is in the element's box (a shape's or a text's,
            // collected as it was drawn); a group's is not collected, so it is reported.
            bool known = box != null && box.HasBounds;
            bool contentInBox = mask.GetAttribute("maskContentUnits").Trim() == "objectBoundingBox";
            if (contentInBox && !known)
            {
                ReportOnce("'mask' with maskContentUnits=\"objectBoundingBox\" was not applied: the element's box is not known");
                return false;
            }
            // A mask's content may be masked in turn — an export's masked group inside a mask; only
            // the mask itself, directly or down a chain, is refused, or masks would recurse.
            if (ctx.MasksBeingDrawn.Contains(mask))
            {
                ReportOnce("a mask's content is masked with the mask itself; there it is not applied");
                return false;
            }

            // The region (x, y, width, height) clips the content: in the box unless maskUnits is
            // userSpaceOnUse, and −10%, −10%, 120%, 120% when not set.
            bool region = true;
            float rx, ry, rw, rh;
            string X(string name, string fallback) => mask.HasAttribute(name) ? mask.GetAttribute(name) : fallback;
            if (mask.GetAttribute("maskUnits").Trim() == "userSpaceOnUse")
            {
                rx = UnitsLength("x", X("x", null), false, ctx.ViewportWidth, "-10%");
                ry = UnitsLength("y", X("y", null), false, ctx.ViewportHeight, "-10%");
                rw = UnitsLength("width", X("width", null), false, ctx.ViewportWidth, "120%");
                rh = UnitsLength("height", X("height", null), false, ctx.ViewportHeight, "120%");
            }
            else if (known)
            {
                float w = box.MaxX - box.MinX, h = box.MaxY - box.MinY;
                rx = box.MinX + w * UnitsLength("x", X("x", null), true, 1, "-10%");
                ry = box.MinY + h * UnitsLength("y", X("y", null), true, 1, "-10%");
                rw = w * UnitsLength("width", X("width", null), true, 1, "120%");
                rh = h * UnitsLength("height", X("height", null), true, 1, "120%");
            }
            else
            {
                region = false;
                rx = ry = rw = rh = 0;
                if (new[] { "x", "y", "width", "height" }.Any(a => mask.HasAttribute(a)))
                    ReportOnce("a mask's region (x, y, width, height) is not applied: the element's box is not known");
            }

            var content = new StringBuilder();
            if (region)
            {
                content.AppendLine("q");
                RectPath(rx, ry, rw, rh, content, ctx, xform);
                content.AppendLine("W");
                content.AppendLine("n");
            }
            var contentSpace = contentInBox ? Transform.Multiply(xform, BoxTransform(box)) : xform;
            // InMask stays set through a nested mask's content: what is drawn there is no container's geometry either.
            bool outerInMask = ctx.InMask;
            ctx.InMask = true;
            ctx.MasksBeingDrawn.Add(mask);
            try
            {
                foreach (XmlNode child in mask.ChildNodes)
                    ProcessNode(child, content, ctx, contentSpace, depth + 1);
            }
            finally { ctx.InMask = outerInMask; ctx.MasksBeingDrawn.Remove(mask); }
            if (region) content.AppendLine("Q");
            sb.AppendLine("q");
            sb.AppendLine("/" + LuminosityMaskState(content, ctx) + " gs");
            return true;
        }

        // ── Anchor ────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>&lt;a&gt;</c> (§17.1): its content drawn as a group, and its link kept as a link
        /// annotation over the bounds of what it drew, in page space, with a URI action. It was
        /// drawn with its link reported as not kept.
        /// </summary>
        /// <remarks>
        /// The bounds are every point the content maps to the page — a shape's control points, a
        /// rect's corners, a text run's box, an image's corners — so the rectangle can exceed the
        /// ink but never miss it. A nested anchor's drawing counts for the outer one too.
        /// </remarks>
        private static void EmitAnchor(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform, int depth)
        {
            var element = (XmlElement)node;
            string href = element.GetAttribute("href");
            if (href.Length == 0) href = element.GetAttribute("href", XlinkNamespace);
            href = href.Trim();
            // A link inside the drawing goes to its target's place on the page — when the
            // target is an element, and once it has been drawn (BuildPdf). It had no target in the PDF.
            string targetId = null, uri = null;
            if (href.StartsWith("#", StringComparison.Ordinal))
            {
                targetId = href.Substring(1);
                if (!ctx.ElementsById.ContainsKey(targetId))
                {
                    ReportOnce($"a link to '{href}' inside the drawing is not kept: no element has that id");
                    targetId = null;
                }
            }
            else uri = LinkTarget(href);

            var scope = BoundsScope.Begin(ctx);
            EmitGroup(node, sb, ctx, xform, depth);
            bool drew = scope.End(ctx, out float minX, out float minY, out float maxX, out float maxY);

            if (!drew || ctx.InClipPath) return;
            if (uri != null)
                ctx.AddObject(SinglePageDocumentWriter.ExtraObject.Annotation(
                    F($"<< /Type /Annot /Subtype /Link /Rect [{minX} {minY} {maxX} {maxY}] /Border [0 0 0] /A << /S /URI /URI ({EscapePdfString(uri)}) >> >>")));
            else if (targetId != null)
            {
                // A plain object holds the place: it becomes the annotation when the target's bounds
                // are known, and stays an unreferenced empty dictionary when they never are.
                int index = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.Plain("<< >>")) - SinglePageDocumentWriter.FirstExtraObject;
                ctx.PendingGoTos.Add((index, F($"[{minX} {minY} {maxX} {maxY}]"), targetId, href));
            }
        }

        /// <summary>
        /// The bounds of what is drawn between <see cref="Begin"/> and <see cref="End"/> — every point
        /// mapped to the page (<see cref="ConvertContext.Track"/>) — with the tracking round it saved
        /// and put back, and the bounds folded into it: an anchor's rectangle, a link target's place.
        /// </summary>
        private readonly struct BoundsScope
        {
            private readonly bool _tracking, _hasBounds;
            private readonly float _minX, _minY, _maxX, _maxY;

            private BoundsScope(ConvertContext ctx)
            {
                _tracking = ctx.Tracking; _hasBounds = ctx.HasBounds;
                _minX = ctx.MinX; _minY = ctx.MinY; _maxX = ctx.MaxX; _maxY = ctx.MaxY;
            }

            public static BoundsScope Begin(ConvertContext ctx)
            {
                var scope = new BoundsScope(ctx);
                ctx.Tracking = true;
                ctx.HasBounds = false;
                return scope;
            }

            /// <summary>Restores the outer tracking and returns whether anything was drawn, with its bounds.</summary>
            public bool End(ConvertContext ctx, out float minX, out float minY, out float maxX, out float maxY)
            {
                bool drew = ctx.HasBounds;
                minX = ctx.MinX; minY = ctx.MinY; maxX = ctx.MaxX; maxY = ctx.MaxY;
                ctx.Tracking = _tracking;
                ctx.HasBounds = _hasBounds;
                ctx.MinX = _minX; ctx.MinY = _minY; ctx.MaxX = _maxX; ctx.MaxY = _maxY;
                if (drew && _tracking) { ctx.Track(minX, minY); ctx.Track(maxX, maxY); }
                return drew;
            }
        }

        /// <summary>The ids the drawing's <c>&lt;a&gt;</c> elements link to with a <c>#id</c> href, or none.</summary>
        private static HashSet<string> LinkTargetIds(XmlElement root)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (root == null) return ids;
            foreach (XmlNode n in root.GetElementsByTagName("*"))
            {
                if (!(n is XmlElement a) || a.LocalName != "a") continue;
                string href = a.GetAttribute("href");
                if (href.Length == 0) href = a.GetAttribute("href", XlinkNamespace);
                href = href.Trim();
                if (href.Length > 1 && href[0] == '#') ids.Add(href.Substring(1));
            }
            return ids;
        }

        /// <summary>
        /// The URI a link keeps, or <c>null</c>: only http, https and mailto links are kept, since a
        /// <c>javascript:</c> or <c>file:</c> one is not a link to follow; each refusal is reported. A
        /// link inside the drawing is <see cref="EmitAnchor"/>'s. The kept URI is ASCII, anything
        /// else percent-encoded as UTF-8.
        /// </summary>
        private static string LinkTarget(string href)
        {
            if (href.Length == 0) return null;   // an anchor with no link is a group
            int colon = href.IndexOf(':');
            string scheme = colon > 0 ? href.Substring(0, colon).ToLowerInvariant() : "";
            if (scheme != "http" && scheme != "https" && scheme != "mailto")
            {
                ReportOnce($"a link to '{href}' is not kept: only http, https and mailto links are");
                return null;
            }
            var ascii = new StringBuilder(href.Length);
            foreach (byte b in Encoding.UTF8.GetBytes(href))
                if (b > 0x20 && b < 0x7F) ascii.Append((char)b);
                else ascii.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return ascii.ToString();
        }

        // ── Switch ────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>&lt;switch&gt;</c> (§5.8): the first child element whose conditions hold
        /// (<see cref="ConditionsHold"/>), and no other. A <c>foreignObject</c> fails too, since its
        /// non-SVG content cannot be drawn here — the very child a switch exists to fall back from.
        /// </summary>
        private static void EmitSwitch(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform, int depth)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                if (!(child is XmlElement element)) continue;
                if (element.LocalName == "foreignObject" || !ConditionsHold(element, ctx)) continue;
                ProcessNode(element, sb, ctx, xform, depth + 1);
                return;
            }
        }

        /// <summary>
        /// SVG 2 §5.8's conditional processing attributes: <c>requiredExtensions</c> holds for no
        /// value, since this converter implements no extension (an empty one is false by the
        /// specification); <c>systemLanguage</c> holds when one of the reader's languages
        /// (<see cref="SvgConversionOptions.Languages"/>) matches one of its comma-separated tags, case
        /// folded — the tag itself, or a tag it is the prefix of up to a <c>-</c> — and an empty one is
        /// false. <c>requiredFeatures</c>, which SVG 2 dropped, holds.
        /// </summary>
        private static bool ConditionsHold(XmlElement element, ConvertContext ctx)
        {
            if (element.HasAttribute("requiredExtensions")) return false;
            if (!element.HasAttribute("systemLanguage")) return true;
            string value = element.GetAttribute("systemLanguage");
            if (value.Length > MaxSystemLanguageLength) return false;
            foreach (string listed in value.Split(','))
            {
                string tag = listed.Trim();
                if (tag.Length == 0) continue;
                foreach (string reader in ctx.Languages)
                    if (string.Equals(tag, reader, StringComparison.OrdinalIgnoreCase)
                        || (tag.Length > reader.Length && tag[reader.Length] == '-'
                            && tag.StartsWith(reader, StringComparison.OrdinalIgnoreCase)))
                        return true;
            }
            return false;
        }

        /// <summary>A <c>systemLanguage</c> longer than this is not read (and does not hold).</summary>
        private const int MaxSystemLanguageLength = 1024;

        /// <summary>
        /// The reader's languages for <c>systemLanguage</c>: the caller's, or the host's UI culture and
        /// each of its parents — an <c>en-US</c> reader matches <c>en</c> — as a browser offers its own.
        /// </summary>
        private static IReadOnlyList<string> ReaderLanguages(SvgConversionOptions options)
        {
            var languages = new List<string>();
            if (options.Languages != null)
            {
                foreach (string language in options.Languages)
                    if (!string.IsNullOrWhiteSpace(language)) languages.Add(language.Trim());
                return languages;
            }
            for (CultureInfo culture = CultureInfo.CurrentUICulture;
                 culture != null && culture.Name.Length > 0 && languages.Count < 8; culture = culture.Parent)
                languages.Add(culture.Name);
            return languages;
        }

        // ── Filter ────────────────────────────────────────────────────────────

        /// <summary>
        /// How finely a filter region is rendered: two pixels a point, 144 dpi, fewer where
        /// <see cref="PdfLimits.MaxSvgFilterPixels"/> asks.
        /// </summary>
        private const double FilterPixelsPerPoint = 2;

        /// <summary>
        /// The filter <paramref name="element"/>'s <c>filter</c> names (Filter Effects 1 §7) — a
        /// <c>url(#id)</c> to a filter element, or a list of CSS filter functions made into one
        /// (<see cref="FunctionFilter"/>) — or <c>null</c>: for none, and — reported, the element
        /// drawn without it — for a reference naming nothing, a function list this does not read, a
        /// filter with a primitive this converter does not apply (<see cref="UnappliedPrimitive"/>),
        /// and past <see cref="PdfLimits.MaxSvgFilteredElements"/> filtered elements.
        /// </summary>
        private static XmlElement FilterOf(XmlElement element, ConvertContext ctx)
        {
            string value = OwnPresentation(element, "filter");
            if (value == null || AppliesNothing(value)) return null;
            XmlElement filter = Referenced(value, ctx, "filter");
            if (filter == null)
            {
                if (value.Trim().StartsWith("url(", StringComparison.Ordinal))
                {
                    ReportOnce("'filter' was not applied: a reference names no filter in this document");
                    return null;
                }
                filter = FunctionFilter(value, element, out string problem);
                if (filter == null)
                {
                    ReportOnce($"'filter' was not applied: {problem}");
                    return null;
                }
            }
            foreach (XmlElement primitive in filter.ChildNodes.OfType<XmlElement>())
            {
                string unapplied = UnappliedPrimitive(primitive);
                if (unapplied == null) continue;
                ReportOnce($"'filter' was not applied: {unapplied} is not applied by this converter; the element was drawn without it");
                return null;
            }
            if (ctx.FilteredCount >= PdfLimits.MaxSvgFilteredElements)
            {
                ReportOnce($"past {PdfLimits.MaxSvgFilteredElements} filtered elements a 'filter' is not applied; the rest were drawn without it");
                return null;
            }
            ctx.FilteredCount++;
            return filter;
        }

        /// <summary>
        /// The attribute a filter made from CSS functions carries: how far, in user units, its blurs
        /// and shadows reach past the element, by which <see cref="Filtered"/> grows the default region.
        /// </summary>
        private const string FunctionMarginAttribute = "data-tetitulz-filter-margin";

        /// <summary>
        /// A <c>filter</c> value that is a list of CSS filter functions (Filter Effects 1 §10.1) as
        /// the filter element it is equivalent to — each function its primitive, in order, in sRGB —
        /// or <c>null</c> with <paramref name="problem"/> saying why: a function this does not read, a
        /// negative amount, a length or angle it cannot read, a <c>url()</c> among functions.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item><c>blur(l)</c>: <c>feGaussianBlur</c> at l, 0 when absent.</item>
        /// <item><c>brightness(a)</c>, <c>contrast(a)</c>: a linear transfer of R, G and B — slope a; contrast's
        /// intercept −a/2 + ½.</item>
        /// <item><c>drop-shadow(color? dx dy blur?)</c>: <c>feDropShadow</c>; the colour <c>currentcolor</c>
        /// — the element's <c>color</c>, black — when absent.</item>
        /// <item><c>grayscale(a)</c>, <c>sepia(a)</c>: the specification's matrices, a in [0, 1];
        /// <c>saturate(a)</c> and <c>hue-rotate(θ)</c> the <c>feColorMatrix</c> types of those names.</item>
        /// <item><c>invert(a)</c>: a table transfer (a, 1 − a) of R, G and B; <c>opacity(a)</c> one of (0, a) of A.</item>
        /// </list>
        /// An amount is a number or a percentage, 1 when absent; a length is a CSS absolute length or
        /// a bare number of user units; an angle is deg, grad, rad or turn, a bare number degrees.
        /// The region is the default one grown by <see cref="FunctionMarginAttribute"/>: three
        /// deviations of a blur, and a shadow's offset with three of its blur.
        /// </remarks>
        private static XmlElement FunctionFilter(string value, XmlElement element, out string problem)
        {
            problem = null;
            var functions = new List<(string Name, string Args)>();
            string v = value.Trim();
            int i = 0;
            while (i < v.Length)
            {
                if (char.IsWhiteSpace(v[i])) { i++; continue; }
                int start = i;
                while (i < v.Length && (char.IsLetter(v[i]) || v[i] == '-')) i++;
                string name = v.Substring(start, i - start);
                if (name.Length == 0 || i >= v.Length || v[i] != '(') { problem = $"'{v}' is not a list of filter functions"; return null; }
                int open = ++i, depth = 1;
                while (i < v.Length && depth > 0) { if (v[i] == '(') depth++; else if (v[i] == ')') depth--; i++; }
                if (depth != 0) { problem = $"'{v}' is not a list of filter functions"; return null; }
                functions.Add((name.ToLowerInvariant(), v.Substring(open, i - 1 - open).Trim()));
            }
            if (functions.Count == 0) { problem = $"'{v}' is not a list of filter functions"; return null; }

            XmlDocument doc = element.OwnerDocument;
            XmlElement filter = doc.CreateElement("filter", element.NamespaceURI);
            // §10.1: the functions operate in sRGB.
            filter.SetAttribute("color-interpolation-filters", "sRGB");
            float margin = 0;
            XmlElement Primitive(string localName)
            {
                XmlElement primitive = doc.CreateElement(localName, element.NamespaceURI);
                filter.AppendChild(primitive);
                return primitive;
            }
            void Transfer(string type, Func<string, XmlElement, XmlElement> configure, params string[] channels)
            {
                XmlElement transfer = Primitive("feComponentTransfer");
                foreach (string channel in channels)
                {
                    XmlElement fn = doc.CreateElement("feFunc" + channel, element.NamespaceURI);
                    fn.SetAttribute("type", type);
                    configure(channel, fn);
                    transfer.AppendChild(fn);
                }
            }
            foreach (var (name, args) in functions)
            {
                switch (name)
                {
                    case "url":
                        problem = "a url() among filter functions is not applied by this converter";
                        return null;
                    case "blur":
                    {
                        if (!TryFunctionLength(args, 0, out float std)) { problem = $"blur({args}) has a length this converter cannot read"; return null; }
                        if (std < 0) { problem = $"blur({args}) is negative"; return null; }
                        Primitive("feGaussianBlur").SetAttribute("stdDeviation", F(std));
                        margin = Math.Max(margin, 3 * std);
                        break;
                    }
                    case "drop-shadow":
                    {
                        var lengths = new List<float>();
                        var colour = new List<string>();
                        foreach (string token in SplitAtDepthZero(args))
                        {
                            if (TryFunctionLength(token, 0, out float length) && (lengths.Count > 0 || char.IsDigit(token[0]) || token[0] is '-' or '+' or '.')) lengths.Add(length);
                            else colour.Add(token);
                        }
                        if (lengths.Count is < 2 or > 3 || lengths.Count == 3 && lengths[2] < 0) { problem = $"drop-shadow({args}) is not a colour with two or three lengths"; return null; }
                        XmlElement shadow = Primitive("feDropShadow");
                        shadow.SetAttribute("dx", F(lengths[0]));
                        shadow.SetAttribute("dy", F(lengths[1]));
                        shadow.SetAttribute("stdDeviation", F(lengths.Count == 3 ? lengths[2] : 0));
                        string paint = colour.Count > 0 ? string.Join(" ", colour) : GetPresentation(element, "color") ?? "black";
                        if (paint.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) paint = GetPresentation(element, "color") ?? "black";
                        shadow.SetAttribute("flood-color", paint);
                        margin = Math.Max(margin, Math.Max(Math.Abs(lengths[0]), Math.Abs(lengths[1])) + 3 * (lengths.Count == 3 ? lengths[2] : 0));
                        break;
                    }
                    case "hue-rotate":
                    {
                        if (!TryFunctionAngle(args, out float degrees)) { problem = $"hue-rotate({args}) has an angle this converter cannot read"; return null; }
                        XmlElement rotate = Primitive("feColorMatrix");
                        rotate.SetAttribute("type", "hueRotate");
                        rotate.SetAttribute("values", F(degrees));
                        break;
                    }
                    case "brightness": case "contrast": case "grayscale": case "invert": case "opacity": case "saturate": case "sepia":
                    {
                        if (!TryFunctionAmount(args, out float amount)) { problem = $"{name}({args}) has an amount this converter cannot read"; return null; }
                        if (amount < 0) { problem = $"{name}({args}) is negative"; return null; }
                        switch (name)
                        {
                            case "brightness":
                                Transfer("linear", (_, fn) => { fn.SetAttribute("slope", F(amount)); return fn; }, "R", "G", "B");
                                break;
                            case "contrast":
                                Transfer("linear", (_, fn) => { fn.SetAttribute("slope", F(amount)); fn.SetAttribute("intercept", F(-amount / 2 + 0.5f)); return fn; }, "R", "G", "B");
                                break;
                            case "invert":
                                Transfer("table", (_, fn) => { fn.SetAttribute("tableValues", F(Math.Min(1, amount)) + " " + F(1 - Math.Min(1, amount))); return fn; }, "R", "G", "B");
                                break;
                            case "opacity":
                                Transfer("table", (_, fn) => { fn.SetAttribute("tableValues", "0 " + F(Math.Min(1, amount))); return fn; }, "A");
                                break;
                            case "saturate":
                            {
                                XmlElement saturate = Primitive("feColorMatrix");
                                saturate.SetAttribute("type", "saturate");
                                saturate.SetAttribute("values", F(amount));
                                break;
                            }
                            default:
                            {
                                // grayscale and sepia (§10.1): the matrix at amount a is between the identity (a 0) and the full effect (a 1).
                                float k = 1 - Math.Min(1, amount);
                                float[] m = name == "grayscale"
                                    ? new[]
                                    {
                                        0.2126f + 0.7874f * k, 0.7152f - 0.7152f * k, 0.0722f - 0.0722f * k, 0, 0,
                                        0.2126f - 0.2126f * k, 0.7152f + 0.2848f * k, 0.0722f - 0.0722f * k, 0, 0,
                                        0.2126f - 0.2126f * k, 0.7152f - 0.7152f * k, 0.0722f + 0.9278f * k, 0, 0,
                                        0, 0, 0, 1, 0,
                                    }
                                    : new[]
                                    {
                                        0.393f + 0.607f * k, 0.769f - 0.769f * k, 0.189f - 0.189f * k, 0, 0,
                                        0.349f - 0.349f * k, 0.686f + 0.314f * k, 0.168f - 0.168f * k, 0, 0,
                                        0.272f - 0.272f * k, 0.534f - 0.534f * k, 0.131f + 0.869f * k, 0, 0,
                                        0, 0, 0, 1, 0,
                                    };
                                XmlElement matrix = Primitive("feColorMatrix");
                                matrix.SetAttribute("type", "matrix");
                                matrix.SetAttribute("values", string.Join(" ", m.Select(F)));
                                break;
                            }
                        }
                        break;
                    }
                    default:
                        problem = $"{name}() is not a filter function this converter applies";
                        return null;
                }
            }
            if (margin > 0) filter.SetAttribute(FunctionMarginAttribute, F(margin));
            return filter;
        }

        /// <summary><paramref name="args"/> split at the spaces and commas outside parentheses — a colour function stays whole.</summary>
        private static List<string> SplitAtDepthZero(string args)
        {
            var tokens = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i <= args.Length; i++)
            {
                bool end = i == args.Length;
                if (!end && args[i] == '(') depth++;
                else if (!end && args[i] == ')') depth--;
                if (end || depth == 0 && (char.IsWhiteSpace(args[i]) || args[i] == ','))
                {
                    if (i > start) tokens.Add(args.Substring(start, i - start));
                    start = i + 1;
                }
            }
            return tokens;
        }

        /// <summary>A CSS number with an optional unit, split: <c>false</c> for anything else.</summary>
        private static bool TryNumberAndUnit(string token, out float number, out string unit)
        {
            number = 0; unit = null;
            string t = token.Trim();
            int i = 0;
            while (i < t.Length && (char.IsDigit(t[i]) || t[i] is '.' or '-' or '+' || (t[i] is 'e' or 'E') && i > 0 && i + 1 < t.Length && (char.IsDigit(t[i + 1]) || t[i + 1] is '-' or '+'))) i++;
            if (i == 0 || !float.TryParse(t.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || float.IsNaN(number) || float.IsInfinity(number)) return false;
            unit = t.Substring(i).Trim();
            return true;
        }

        /// <summary>A filter function's length in user units (CSS pixels): an absolute CSS length, a bare number, or <paramref name="fallback"/> for an empty argument.</summary>
        private static bool TryFunctionLength(string token, float fallback, out float length)
        {
            length = fallback;
            if (string.IsNullOrWhiteSpace(token)) return true;
            if (!TryNumberAndUnit(token, out float number, out string unit)) return false;
            if (unit.Length == 0) { length = number; return true; }
            if (!Core.Text.CssLength.TryToPixels(number, unit, out double pixels)) return false;
            length = (float)pixels;
            return true;
        }

        /// <summary>A filter function's amount: a number or a percentage, 1 for an empty argument.</summary>
        private static bool TryFunctionAmount(string token, out float amount)
        {
            amount = 1;
            if (string.IsNullOrWhiteSpace(token)) return true;
            if (!TryNumberAndUnit(token, out float number, out string unit)) return false;
            if (unit == "%") { amount = number / 100; return true; }
            if (unit.Length > 0) return false;
            amount = number;
            return true;
        }

        /// <summary>A filter function's angle in degrees: deg, grad, rad or turn, a bare number degrees, 0 for an empty argument.</summary>
        private static bool TryFunctionAngle(string token, out float degrees)
        {
            degrees = 0;
            if (string.IsNullOrWhiteSpace(token)) return true;
            if (!TryNumberAndUnit(token, out float number, out string unit)) return false;
            switch (unit.ToLowerInvariant())
            {
                case "": case "deg": degrees = number; return true;
                case "grad": degrees = number * 0.9f; return true;
                case "rad": degrees = (float)(number * 180 / Math.PI); return true;
                case "turn": degrees = number * 360; return true;
                default: return false;
            }
        }

        /// <summary>
        /// A filter's child this converter does not apply, named for the report, or <c>null</c>: the
        /// primitives are <see cref="SvgFilterEffects"/>'s, and a description is no primitive at all.
        /// </summary>
        private static string UnappliedPrimitive(XmlElement primitive)
        {
            string Value(string name, string initial) => primitive.HasAttribute(name) ? primitive.GetAttribute(name).Trim() : initial;
            switch (primitive.LocalName)
            {
                case "feGaussianBlur": case "feOffset": case "feFlood": case "feMerge":
                case "feDropShadow": case "feComponentTransfer": case "feTile":
                case "desc": case "title": case "metadata":
                    return null;
                case "feImage":
                    return UseHref(primitive).Length == 0 ? "<feImage> with no href" : null;
                case "feMorphology":
                    string morphOp = Value("operator", "erode");
                    return morphOp is "erode" or "dilate" ? null : $"<feMorphology operator=\"{morphOp}\">";
                case "feConvolveMatrix":
                    return ConvolveMatrixProblem(primitive);
                case "feComposite":
                    string op = Value("operator", "over");
                    return op is "over" or "in" or "out" or "atop" or "xor" or "lighter" or "arithmetic" ? null : $"<feComposite operator=\"{op}\">";
                case "feColorMatrix":
                    string type = Value("type", "matrix");
                    return type is "matrix" or "saturate" or "hueRotate" or "luminanceToAlpha" ? null : $"<feColorMatrix type=\"{type}\">";
                case "feBlend":
                    string mode = Value("mode", "normal");
                    return SvgFilterEffects.Blend.IsKnown(mode) ? null : $"<feBlend mode=\"{mode}\">";
                case "feTurbulence":
                    string noise = Value("type", "turbulence");
                    if (noise is not ("turbulence" or "fractalNoise")) return $"<feTurbulence type=\"{noise}\">";
                    string stitch = Value("stitchTiles", "noStitch");
                    return stitch is "stitch" or "noStitch" ? null : $"<feTurbulence stitchTiles=\"{stitch}\">";
                case "feDisplacementMap":
                    string xc = Value("xChannelSelector", "A"), yc = Value("yChannelSelector", "A");
                    return xc is "R" or "G" or "B" or "A" && yc is "R" or "G" or "B" or "A"
                        ? null : $"<feDisplacementMap> with a channel selector that is not R, G, B or A";
                case "feDiffuseLighting": case "feSpecularLighting":
                    return LightSource(primitive) != null ? null : $"<{primitive.LocalName}> with no light source";
                default:
                    return $"<{primitive.LocalName}>";
            }
        }

        /// <summary>
        /// What keeps a <c>feConvolveMatrix</c> from being applied (§9.11), for the report, or
        /// <c>null</c>: an <c>order</c> that is not one or two whole numbers from 1 to 25, a
        /// <c>kernelMatrix</c> of another count than the order's, a target off the kernel, or an
        /// <c>edgeMode</c> not named. Past 25 the work a pixel takes is the kernel's 625 and more.
        /// </summary>
        private static string ConvolveMatrixProblem(XmlElement e)
        {
            var order = ParseFloatList(e.HasAttribute("order") ? e.GetAttribute("order") : "3");
            if (order.Count is 0 or > 2 || order.Any(o => o < 1 || o > 25 || o != Math.Floor(o)))
                return "<feConvolveMatrix> with an order that is not 1 to 25";
            int ox = SaturatingCast.ToInt(order[0]), oy = order.Count > 1 ? SaturatingCast.ToInt(order[1]) : ox;
            int count = ParseFloatList(e.GetAttribute("kernelMatrix")).Count;
            if (count != ox * oy) return $"<feConvolveMatrix> with {count} kernel values for an order of {ox} × {oy}";
            float tx = e.HasAttribute("targetX") ? ParseF(e.GetAttribute("targetX")) : ox / 2;
            float ty = e.HasAttribute("targetY") ? ParseF(e.GetAttribute("targetY")) : oy / 2;
            if (tx < 0 || tx >= ox || ty < 0 || ty >= oy) return "<feConvolveMatrix> with a target off its kernel";
            string edge = e.HasAttribute("edgeMode") ? e.GetAttribute("edgeMode").Trim() : "duplicate";
            return edge is "duplicate" or "wrap" or "none" ? null : $"<feConvolveMatrix edgeMode=\"{edge}\">";
        }

        /// <summary>A lighting primitive's light source (§9.10): its first <c>feDistantLight</c>, <c>fePointLight</c> or <c>feSpotLight</c> child, or <c>null</c>.</summary>
        private static XmlElement LightSource(XmlElement lighting)
            => lighting.ChildNodes.OfType<XmlElement>().FirstOrDefault(n => n.LocalName is "feDistantLight" or "fePointLight" or "feSpotLight");

        /// <summary>A <c>feTurbulence</c>'s <c>numOctaves</c> (§9.21), 1 when absent, a whole number from 0 up — the passes it makes over the pixels.</summary>
        private static int TurbulenceOctaves(XmlElement e)
        {
            float octaves = e.HasAttribute("numOctaves") ? ParseF(e.GetAttribute("numOctaves")) : 1;
            return SaturatingCast.ToInt(Math.Floor(Math.Max(0, Math.Min(int.MaxValue, octaves))));
        }

        /// <summary>A <c>feConvolveMatrix</c>'s kernel size, the samples it reads per pixel; its order was checked by <see cref="ConvolveMatrixProblem"/>.</summary>
        private static int ConvolveKernelSize(XmlElement e)
        {
            var order = ParseFloatList(e.HasAttribute("order") ? e.GetAttribute("order") : "3");
            int ox = SaturatingCast.ToInt(order[0]), oy = order.Count > 1 ? SaturatingCast.ToInt(order[1]) : ox;
            return ox * oy;
        }

        /// <summary>
        /// <paramref name="body"/>, what a filtered element drew, through <paramref name="filter"/>
        /// (Filter Effects 1): rendered over the filter region, the primitives run on its pixels
        /// (<see cref="SvgFilterEffects"/>), and the result drawn back as an image with a soft mask,
        /// clipped to the region, at the element's <paramref name="opacity"/>. A PDF has no filter of
        /// its own.
        /// </summary>
        /// <remarks>
        /// <para>The region is <c>x</c>, <c>y</c>, <c>width</c> and <c>height</c> in <c>filterUnits</c>
        /// — the element's box by default, −10%, −10%, 120%, 120% — and a primitive's lengths and
        /// subregion are in <c>primitiveUnits</c>, user space by default. The region is rendered at
        /// <see cref="FilterPixelsPerPoint"/>, fewer where <see cref="PdfLimits.MaxSvgFilterPixels"/>
        /// asks, and no further from the page than a page's width and height.</para>
        /// <para>An element with no box has no region in box units, and draws nothing, as one whose
        /// region is empty does, and one whose filter has no primitive.</para>
        /// </remarks>
        private static StringBuilder Filtered(XmlElement filter, StringBuilder body, Transform space, PaintTarget box, float opacity, ConvertContext ctx, int depth)
        {
            var nothing = new StringBuilder();
            var elements = filter.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName.StartsWith("fe", StringComparison.Ordinal)).ToList();
            if (elements.Count == 0) return nothing;

            string A(XmlElement e, string name) => e.HasAttribute(name) ? e.GetAttribute(name) : null;
            bool known = box != null && box.HasBounds;
            float bx = known ? box.MinX : 0, by = known ? box.MinY : 0;
            float bw = known ? box.MaxX - box.MinX : 0, bh = known ? box.MaxY - box.MinY : 0;
            float rx, ry, rw, rh;
            if (filter.GetAttribute("filterUnits").Trim() == "userSpaceOnUse")
            {
                rx = UnitsLength("x", A(filter, "x"), false, ctx.ViewportWidth, "-10%");
                ry = UnitsLength("y", A(filter, "y"), false, ctx.ViewportHeight, "-10%");
                rw = UnitsLength("width", A(filter, "width"), false, ctx.ViewportWidth, "120%");
                rh = UnitsLength("height", A(filter, "height"), false, ctx.ViewportHeight, "120%");
            }
            else
            {
                if (!known) return nothing;
                rx = bx + bw * UnitsLength("x", A(filter, "x"), true, 1, "-10%");
                ry = by + bh * UnitsLength("y", A(filter, "y"), true, 1, "-10%");
                rw = bw * UnitsLength("width", A(filter, "width"), true, 1, "120%");
                rh = bh * UnitsLength("height", A(filter, "height"), true, 1, "120%");
            }
            // A filter made from CSS functions (FunctionFilter) reaches past the default region by
            // its blurs and shadows; the region grows by that much on every side.
            float margin = filter.HasAttribute(FunctionMarginAttribute) ? ParseF(filter.GetAttribute(FunctionMarginAttribute)) : 0;
            rx -= margin; ry -= margin; rw += 2 * margin; rh += 2 * margin;
            if (!(rw > 0) || !(rh > 0)) return nothing;

            // The region's corners on the page, and its bounds there, kept within a page's size of it.
            Transform toPage = Transform.Multiply(PageMatrix(ctx), space);
            var corners = new[] { toPage.Apply(rx, ry), toPage.Apply(rx + rw, ry), toPage.Apply(rx + rw, ry + rh), toPage.Apply(rx, ry + rh) };
            float pw = ctx.Options.PageWidth, ph = ctx.Options.PageHeight;
            float x0 = Math.Max(corners.Min(c => c.X), -pw), x1 = Math.Min(corners.Max(c => c.X), 2 * pw);
            float y0 = Math.Max(corners.Min(c => c.Y), -ph), y1 = Math.Min(corners.Max(c => c.Y), 2 * ph);
            if (!(x1 > x0) || !(y1 > y0)) return nothing;

            // Every image a primitive reads is the region's size: the pixels are bounded by the reads.
            // A merge reads each node; a convolution reads its kernel's pixels for each of its own; a
            // turbulence makes a pass over the pixels for each octave, which counts the same.
            long reads = elements.Sum(e => e.LocalName == "feConvolveMatrix" ? ConvolveKernelSize(e)
                : e.LocalName == "feTurbulence" ? (long)Math.Max(1, TurbulenceOctaves(e))
                : Math.Max(1, e.ChildNodes.OfType<XmlElement>().Count(n => n.LocalName == "feMergeNode")));
            double pixelsPerPoint = Math.Min(FilterPixelsPerPoint,
                Math.Sqrt(PdfLimits.MaxSvgFilterPixels / ((double)(x1 - x0) * (y1 - y0) * reads)));
            int dpi = SaturatingCast.ToInt(Math.Floor(72 * pixelsPerPoint));
            if (dpi < 1)
            {
                ReportOnce($"a filter region past {PdfLimits.MaxSvgFilterPixels} pixels at one dot an inch was not filtered; the element was drawn without it");
                return WithOpacity(body, opacity, ctx);
            }

            SvgFilterEffects.Image source = Rasterize(body, x0, y0, x1, y1, dpi, ctx, out string failure);
            if (source == null)
            {
                ReportOnce("a filtered element could not be rendered for its filter (" + failure + "); it was drawn without it");
                return WithOpacity(body, opacity, ctx);
            }

            // User space to pixels: a vector through the page mapping, the rows running down; a
            // spread — a blur's deviation along user x and y — as the axis-aligned bounds it maps to.
            float kx = source.Width / (x1 - x0), ky = source.Height / (y1 - y0);
            bool boxPrimitives = known && filter.GetAttribute("primitiveUnits").Trim() == "objectBoundingBox";
            float Ux(float v) => boxPrimitives ? v * bw : v;
            float Uy(float v) => boxPrimitives ? v * bh : v;
            (float X, float Y) Vector(float ux, float uy) => ((toPage.A * ux + toPage.C * uy) * kx, -(toPage.B * ux + toPage.D * uy) * ky);
            float Hypot(float a, float b) => (float)Math.Sqrt(a * a + b * b);
            int[] PixelRect(float ux, float uy, float uw, float uh)
            {
                var pts = new[] { toPage.Apply(ux, uy), toPage.Apply(ux + uw, uy), toPage.Apply(ux + uw, uy + uh), toPage.Apply(ux, uy + uh) };
                return new[]
                {
                    SaturatingCast.ToInt(Math.Floor((pts.Min(p => p.X) - x0) * kx)), SaturatingCast.ToInt(Math.Floor((y1 - pts.Max(p => p.Y)) * ky)),
                    SaturatingCast.ToInt(Math.Ceiling((pts.Max(p => p.X) - x0) * kx)), SaturatingCast.ToInt(Math.Ceiling((y1 - pts.Min(p => p.Y)) * ky)),
                };
            }
            float SubregionLength(string name, string value, bool horizontal, float fallback)
            {
                if (value == null) return fallback;
                if (!boxPrimitives) return UnitsLength(name, value, false, horizontal ? ctx.ViewportWidth : ctx.ViewportHeight, "0");
                float fraction = UnitsLength(name, value, true, 1, "0");
                return name is "x" ? bx + fraction * bw : name is "y" ? by + fraction * bh : fraction * (horizontal ? bw : bh);
            }
            // A deviation — one number, or x and y — in pixels along the page's axes. Negative on
            // either axis disables the blur (§9.13), both axes: the input passes on.
            (float X, float Y) Deviation(string value)
            {
                var std = ParseFloatList(value);
                float sx = std.Count > 0 ? Ux(std[0]) : 0, sy = std.Count > 1 ? Uy(std[1]) : Uy(std.Count > 0 ? std[0] : 0);
                if (sx < 0 || sy < 0) sx = sy = 0;
                return (Hypot(toPage.A * sx, toPage.C * sy) * kx, Hypot(toPage.B * sx, toPage.D * sy) * ky);
            }
            // A flood's paint, feFlood's and feDropShadow's: flood-color, black by default, straight
            // sRGB; its alpha the colour's times flood-opacity.
            (float R, float G, float B, float A) FloodPaint(XmlElement e)
            {
                string color = OwnPresentation(e, "flood-color") ?? "black";
                var (r, g, b) = ParseColor(color);
                return (r, g, b, PaintAlpha(color) * ReadOpacity(OwnPresentation(e, "flood-opacity")));
            }

            var primitives = new List<SvgFilterEffects.Primitive>();
            foreach (XmlElement e in elements)
            {
                SvgFilterEffects.Primitive primitive;
                switch (e.LocalName)
                {
                    case "feGaussianBlur":
                    {
                        var (sx, sy) = Deviation(A(e, "stdDeviation") ?? "0");
                        primitive = new SvgFilterEffects.Blur { StdX = sx, StdY = sy };
                        break;
                    }
                    case "feOffset":
                    {
                        var (dx, dy) = Vector(Ux(ParseF(A(e, "dx") ?? "0")), Uy(ParseF(A(e, "dy") ?? "0")));
                        primitive = new SvgFilterEffects.Offset { Dx = SaturatingCast.ToInt(Math.Round(dx)), Dy = SaturatingCast.ToInt(Math.Round(dy)) };
                        break;
                    }
                    case "feFlood":
                    {
                        var (r, g, b, a) = FloodPaint(e);
                        primitive = new SvgFilterEffects.Flood { R = r, G = g, B = b, A = a };
                        break;
                    }
                    case "feMerge":
                    {
                        var merge = new SvgFilterEffects.Merge();
                        foreach (XmlElement n in e.ChildNodes.OfType<XmlElement>().Where(n => n.LocalName == "feMergeNode"))
                            merge.Nodes.Add(A(n, "in")?.Trim());
                        primitive = merge;
                        break;
                    }
                    case "feComposite":
                        primitive = new SvgFilterEffects.Composite
                        {
                            Operator = A(e, "operator")?.Trim() ?? "over",
                            K1 = ParseF(A(e, "k1") ?? "0"), K2 = ParseF(A(e, "k2") ?? "0"),
                            K3 = ParseF(A(e, "k3") ?? "0"), K4 = ParseF(A(e, "k4") ?? "0"),
                        };
                        break;
                    case "feColorMatrix":
                        primitive = new SvgFilterEffects.ColorMatrix { Matrix = ColorMatrixOf(e) };
                        break;
                    case "feBlend":
                        primitive = new SvgFilterEffects.Blend { Mode = A(e, "mode")?.Trim() ?? "normal" };
                        break;
                    case "feDropShadow":
                    {
                        // dx, dy and stdDeviation are 2 when absent (§9.12), unlike feOffset's and feGaussianBlur's 0.
                        var (sx, sy) = Deviation(A(e, "stdDeviation") ?? "2");
                        var (dx, dy) = Vector(Ux(ParseF(A(e, "dx") ?? "2")), Uy(ParseF(A(e, "dy") ?? "2")));
                        var (r, g, b, a) = FloodPaint(e);
                        primitive = new SvgFilterEffects.DropShadow
                        {
                            StdX = sx, StdY = sy,
                            Dx = SaturatingCast.ToInt(Math.Round(dx)), Dy = SaturatingCast.ToInt(Math.Round(dy)),
                            R = r, G = g, B = b, A = a,
                        };
                        break;
                    }
                    case "feMorphology":
                    {
                        // The radius is a spread like a blur's deviation: one number, or x and y, to
                        // pixels along the page's axes; negative disables (§9.16), as Deviation does.
                        var (mx, my) = Deviation(A(e, "radius") ?? "0");
                        primitive = new SvgFilterEffects.Morphology
                        {
                            Dilate = A(e, "operator")?.Trim() == "dilate",
                            RadiusX = SaturatingCast.ToInt(Math.Round(mx)),
                            RadiusY = SaturatingCast.ToInt(Math.Round(my)),
                        };
                        break;
                    }
                    case "feConvolveMatrix":
                    {
                        var order = ParseFloatList(A(e, "order") ?? "3");
                        int ox = SaturatingCast.ToInt(order[0]), oy = order.Count > 1 ? SaturatingCast.ToInt(order[1]) : ox;
                        var kernel = ParseFloatList(A(e, "kernelMatrix") ?? "");
                        // The divisor is the kernel's sum when absent, 1 when that is zero (§9.11); a
                        // divisor of zero is an error and is read as absent.
                        float sum = kernel.Sum();
                        float divisor = e.HasAttribute("divisor") ? ParseF(A(e, "divisor")) : 0;
                        if (divisor == 0) divisor = sum != 0 ? sum : 1;
                        primitive = new SvgFilterEffects.ConvolveMatrix
                        {
                            OrderX = ox, OrderY = oy, Kernel = kernel.ToArray(),
                            TargetX = e.HasAttribute("targetX") ? SaturatingCast.ToInt(ParseF(A(e, "targetX"))) : ox / 2,
                            TargetY = e.HasAttribute("targetY") ? SaturatingCast.ToInt(ParseF(A(e, "targetY"))) : oy / 2,
                            Divisor = divisor, Bias = ParseF(A(e, "bias") ?? "0"),
                            EdgeMode = A(e, "edgeMode")?.Trim() ?? "duplicate",
                            PreserveAlpha = A(e, "preserveAlpha")?.Trim() == "true",
                        };
                        break;
                    }
                    case "feTile":
                        primitive = new SvgFilterEffects.Tile();
                        break;
                    case "feImage":
                    {
                        // The picture is fitted into the primitive subregion, the filter region by default.
                        float ix = SubregionLength("x", A(e, "x"), true, rx), iy = SubregionLength("y", A(e, "y"), false, ry);
                        float iw = SubregionLength("width", A(e, "width"), true, rw), ih = SubregionLength("height", A(e, "height"), false, rh);
                        primitive = new SvgFilterEffects.Picture
                        {
                            Rendered = FilterImage(e, space, ix, iy, iw, ih, x0, y0, x1, y1, dpi, ctx, depth),
                        };
                        break;
                    }
                    case "feTurbulence":
                    {
                        var freq = ParseFloatList(A(e, "baseFrequency") ?? "0");
                        float fx = freq.Count > 0 ? freq[0] : 0, fy = freq.Count > 1 ? freq[1] : fx;
                        // A negative frequency is an error (§9.21); it is read as 0, as a browser reads it.
                        // The seed is truncated toward zero, its integer kept whole for the generator.
                        double seed = Math.Truncate(ParseF(A(e, "seed") ?? "0"));
                        // The noise is sampled at each pixel's centre in user space: the region's pixel
                        // grid back through the page mapping. A mapping with no inverse collapsed the
                        // element; the noise is then laid on the pixels themselves.
                        double[] toUser = { 1, 0, 0, 1, 0, 0 };
                        if (toPage.TryInvert(out Transform inv))
                            toUser = new double[]
                            {
                                inv.A / kx, inv.B / kx, -inv.C / ky, -inv.D / ky,
                                inv.A * x0 + inv.C * y1 + inv.E, inv.B * x0 + inv.D * y1 + inv.F,
                            };
                        primitive = new SvgFilterEffects.Turbulence
                        {
                            BaseFreqX = Math.Max(0, fx), BaseFreqY = Math.Max(0, fy),
                            Octaves = TurbulenceOctaves(e),
                            Seed = SaturatingCast.ToLong(Math.Max(long.MinValue, Math.Min(long.MaxValue, seed))),
                            FractalNoise = A(e, "type")?.Trim() == "fractalNoise",
                            Stitch = A(e, "stitchTiles")?.Trim() == "stitch",
                            TileX = SubregionLength("x", A(e, "x"), true, rx), TileY = SubregionLength("y", A(e, "y"), false, ry),
                            TileWidth = SubregionLength("width", A(e, "width"), true, rw), TileHeight = SubregionLength("height", A(e, "height"), false, rh),
                            ToUser = toUser,
                        };
                        break;
                    }
                    case "feDisplacementMap":
                    {
                        // The scale is a user-space length along each axis; the map's unit displaces by
                        // the pixel vector it maps to, so a transformed element is displaced along its own axes.
                        float scale = ParseF(A(e, "scale") ?? "0");
                        var (xdx, xdy) = Vector(Ux(scale), 0);
                        var (ydx, ydy) = Vector(0, Uy(scale));
                        int Channel(string name) => A(e, name)?.Trim() switch { "R" => 0, "G" => 1, "B" => 2, _ => 3 };
                        primitive = new SvgFilterEffects.DisplacementMap
                        {
                            XDx = xdx, XDy = xdy, YDx = ydx, YDy = ydy,
                            XChannel = Channel("xChannelSelector"), YChannel = Channel("yChannelSelector"),
                        };
                        break;
                    }
                    case "feDiffuseLighting": case "feSpecularLighting":
                    {
                        XmlElement light = LightSource(e);
                        // A light's place is in primitiveUnits, mapped to the region's pixels; its z
                        // at the mapping's mean scale, one unit of z being one of x and y (§9.10).
                        (double X, double Y) PixelOf(float ux, float uy)
                        {
                            var p = toPage.Apply(ux, uy);
                            return ((p.X - x0) * kx, (y1 - p.Y) * ky);
                        }
                        double kz = Math.Sqrt(Math.Abs(toPage.A * toPage.D - toPage.B * toPage.C)) * Math.Sqrt(kx * ky);
                        float LightLength(string name, bool horizontal) => SubregionLength(name is "x" or "pointsAtX" ? "x" : name is "y" or "pointsAtY" ? "y" : "width",
                            A(light, name), horizontal, 0);
                        var (r, g, b) = ParseColor(OwnPresentation(e, "lighting-color") ?? "white");
                        var (lx, ly) = PixelOf(LightLength("x", true), LightLength("y", false));
                        var (px, py) = PixelOf(LightLength("pointsAtX", true), LightLength("pointsAtY", false));
                        bool specular = e.LocalName == "feSpecularLighting";
                        primitive = new SvgFilterEffects.Lighting
                        {
                            Specular = specular,
                            SurfaceScale = ParseF(A(e, "surfaceScale") ?? "1"),
                            Constant = ParseF(A(e, specular ? "specularConstant" : "diffuseConstant") ?? "1"),
                            // 1 to 128 (§9.11); a value outside is an error, and is read as the nearer end.
                            Exponent = Math.Max(1, Math.Min(128, ParseF(A(e, "specularExponent") ?? "1"))),
                            R = r, G = g, B = b,
                            Kind = light.LocalName switch { "fePointLight" => SvgFilterEffects.Lighting.LightKind.Point, "feSpotLight" => SvgFilterEffects.Lighting.LightKind.Spot, _ => SvgFilterEffects.Lighting.LightKind.Distant },
                            Azimuth = ParseF(A(light, "azimuth") ?? "0"), Elevation = ParseF(A(light, "elevation") ?? "0"),
                            X = lx, Y = ly, Z = Ux(ParseF(A(light, "z") ?? "0")) * kz,
                            PointsAtX = px, PointsAtY = py, PointsAtZ = Ux(ParseF(A(light, "pointsAtZ") ?? "0")) * kz,
                            Focus = ParseF(A(light, "specularExponent") ?? "1"),
                            ConeAngle = light.HasAttribute("limitingConeAngle") ? Math.Abs(ParseF(A(light, "limitingConeAngle"))) : double.NaN,
                        };
                        break;
                    }
                    case "feComponentTransfer":
                    {
                        var transfer = new SvgFilterEffects.ComponentTransfer();
                        // The last function for a channel is the one applied.
                        foreach (XmlElement fn in e.ChildNodes.OfType<XmlElement>())
                        {
                            int channel = fn.LocalName switch { "feFuncR" => 0, "feFuncG" => 1, "feFuncB" => 2, "feFuncA" => 3, _ => -1 };
                            if (channel >= 0) transfer.Functions[channel] = TransferFunctionOf(fn);
                        }
                        primitive = transfer;
                        break;
                    }
                    default:
                        continue;
                }
                primitive.In = A(e, "in")?.Trim();
                primitive.In2 = A(e, "in2")?.Trim();
                primitive.Result = A(e, "result")?.Trim();
                // §9.5: linearRGB unless the primitive — or what it inherits from — says sRGB.
                primitive.Linear = GetPresentation(e, "color-interpolation-filters")?.Trim() != "sRGB";
                if (e.HasAttribute("x") || e.HasAttribute("y") || e.HasAttribute("width") || e.HasAttribute("height"))
                    primitive.Subregion = PixelRect(
                        SubregionLength("x", A(e, "x"), true, rx), SubregionLength("y", A(e, "y"), false, ry),
                        SubregionLength("width", A(e, "width"), true, rw), SubregionLength("height", A(e, "height"), false, rh));
                primitives.Add(primitive);
            }

            SvgFilterEffects.Image result = SvgFilterEffects.Run(primitives, source,
                input => ReportOnce($"a filter's '{input}' input is not modelled; it was read as transparent black"));

            int count = result.Width * result.Height;
            var rgb = new byte[count * 3];
            var alpha = new byte[count];
            for (int i = 0; i < count; i++)
            {
                float a = result.Px[i * 4 + 3];
                alpha[i] = Rendering.PdfColor.Channel(a);
                if (a > 0)
                    for (int c = 0; c < 3; c++)
                        rgb[i * 3 + c] = Rendering.PdfColor.Channel(Math.Min(1f, result.Px[i * 4 + c] / a));
            }
            string imageName = ImageWithAlpha(result.Width, result.Height, Core.IO.Filters.FlateEncodeFilter.Encode(rgb), alpha, ctx);

            var drawn = new StringBuilder();
            drawn.AppendLine("q");
            if (opacity < 1) drawn.AppendLine("/" + ctx.StateFor(opacity, opacity) + " gs");
            // The region clips the result: under a rotation its bounds are larger than it.
            drawn.AppendLine(F($"{corners[0].X} {corners[0].Y} m"));
            for (int i = 1; i < 4; i++) drawn.AppendLine(F($"{corners[i].X} {corners[i].Y} l"));
            drawn.AppendLine("h");
            drawn.AppendLine("W");
            drawn.AppendLine("n");
            drawn.AppendLine(F($"{x1 - x0} 0 0 {y1 - y0} {x0} {y0} cm"));
            drawn.AppendLine("/" + imageName + " Do");
            drawn.AppendLine("Q");
            return drawn;
        }

        /// <summary>
        /// <paramref name="body"/>, content in page space, rendered over the page-space bounds
        /// [<paramref name="x0"/>, <paramref name="x1"/>] × [<paramref name="y0"/>, <paramref name="y1"/>]
        /// at <paramref name="dpi"/> on a transparent background — a one-page PDF with every resource
        /// made so far, through <see cref="Rendering.PdfPageRenderer"/> — as a filter image in sRGB;
        /// <c>null</c>, with <paramref name="failure"/> saying why, where it could not be rendered.
        /// A filtered element's own drawing and an <c>feImage</c>'s picture are rasterised alike.
        /// </summary>
        private static SvgFilterEffects.Image Rasterize(StringBuilder body, float x0, float y0, float x1, float y1, int dpi, ConvertContext ctx, out string failure)
        {
            Rendering.RenderedPage page;
            try
            {
                byte[] pdf = BuildPdf(body.ToString(), "[" + F(x0) + " " + F(y0) + " " + F(x1) + " " + F(y1) + "]", ctx);
                page = Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, dpi,
                    new Rendering.PdfRenderOptions { BackgroundArgb = 0 });
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return null;
            }
            failure = null;
            var image = new SvgFilterEffects.Image(page.Width, page.Height, linear: false);
            for (int y = 0; y < page.Height; y++)
                for (int x = 0; x < page.Width; x++)
                    for (int c = 0; c < 4; c++)
                        image.Px[(y * page.Width + x) * 4 + c] = page.Pixels[y * page.Stride + x * 4 + c] / 255f;
            return image;
        }

        /// <summary>
        /// An <c>feImage</c>'s picture (§9.14): the element its <c>href</c> names, drawn where it is
        /// in the filtered element's user space, or the image its <c>href</c> is a URI to, fitted
        /// into the primitive subregion — the filter region by default — by its
        /// <c>preserveAspectRatio</c>; each through the converter's own drawing (an <c>image</c>
        /// element made for the purpose, or a copy of the named element, hung under the
        /// <c>feImage</c> while it draws, as a <c>use</c>'s copy is), rasterised as the source is.
        /// <c>null</c>, reported, where the href names nothing or the picture could not be rendered.
        /// </summary>
        private static SvgFilterEffects.Image FilterImage(XmlElement e, Transform space, float sx, float sy, float sw, float sh,
            float x0, float y0, float x1, float y1, int dpi, ConvertContext ctx, int depth)
        {
            string href = UseHref(e);
            XmlNode drawn;
            bool namesElement = href.StartsWith("#", StringComparison.Ordinal);
            if (namesElement)
            {
                if (!ctx.ElementsById.TryGetValue(href.Substring(1), out XmlElement target))
                {
                    ReportOnce("an feImage names no element in this document; it is transparent");
                    return null;
                }
                drawn = target.CloneNode(deep: true);
            }
            else
            {
                XmlElement image = e.OwnerDocument.CreateElement("image", e.NamespaceURI);
                image.SetAttribute("href", href);
                image.SetAttribute("x", F(sx));
                image.SetAttribute("y", F(sy));
                image.SetAttribute("width", F(sw));
                image.SetAttribute("height", F(sh));
                if (e.HasAttribute("preserveAspectRatio")) image.SetAttribute("preserveAspectRatio", e.GetAttribute("preserveAspectRatio"));
                drawn = image;
            }
            var content = new StringBuilder();
            e.AppendChild(drawn);
            try { ProcessNode(drawn, content, ctx, space, depth + 1); }
            finally { e.RemoveChild(drawn); }
            SvgFilterEffects.Image picture = Rasterize(content, x0, y0, x1, y1, dpi, ctx, out string failure);
            if (picture == null) ReportOnce("an feImage could not be rendered (" + failure + "); it is transparent");
            return picture;
        }

        /// <summary>
        /// A <c>feColorMatrix</c>'s matrix (§9.10): <c>values</c> as twenty numbers for <c>matrix</c>,
        /// one for <c>saturate</c> and <c>hueRotate</c>, none for <c>luminanceToAlpha</c>. A missing
        /// value is the identity's; a matrix of any other count is in error, and passes its input on.
        /// </summary>
        private static float[] ColorMatrixOf(XmlElement e)
        {
            var values = e.HasAttribute("values") ? ParseFloatList(e.GetAttribute("values")) : null;
            switch (e.HasAttribute("type") ? e.GetAttribute("type").Trim() : "matrix")
            {
                case "saturate": return SvgFilterEffects.ColorMatrix.Saturate(values != null && values.Count > 0 ? values[0] : 1);
                case "hueRotate": return SvgFilterEffects.ColorMatrix.HueRotate(values != null && values.Count > 0 ? values[0] : 0);
                case "luminanceToAlpha": return SvgFilterEffects.ColorMatrix.LuminanceToAlpha();
            }
            if (values != null && values.Count == 20) return values.ToArray();
            return new float[] { 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0 };
        }

        /// <summary>
        /// A <c>feFuncR</c>, <c>feFuncG</c>, <c>feFuncB</c> or <c>feFuncA</c> (§9.8): its <c>type</c>
        /// and the attributes that type reads, each at its initial value when absent. A type it does not
        /// name is the identity.
        /// </summary>
        private static SvgFilterEffects.TransferFunction TransferFunctionOf(XmlElement fn)
        {
            float Number(string name, float initial) => fn.HasAttribute(name) ? ParseF(fn.GetAttribute(name)) : initial;
            string type = fn.HasAttribute("type") ? fn.GetAttribute("type").Trim() : "identity";
            return new SvgFilterEffects.TransferFunction
            {
                Type = type is "table" or "discrete" or "linear" or "gamma" ? type : "identity",
                Table = fn.HasAttribute("tableValues") ? ParseFloatList(fn.GetAttribute("tableValues")).ToArray() : Array.Empty<float>(),
                Slope = Number("slope", 1), Intercept = Number("intercept", 0),
                Amplitude = Number("amplitude", 1), Exponent = Number("exponent", 1), Offset = Number("offset", 0),
            };
        }

        /// <summary><paramref name="body"/> at <paramref name="opacity"/>: a filtered element drawn without its filter, its opacity still its own.</summary>
        private static StringBuilder WithOpacity(StringBuilder body, float opacity, ConvertContext ctx)
        {
            if (opacity >= 1) return body;
            var drawn = new StringBuilder();
            drawn.AppendLine("q");
            drawn.AppendLine("/" + ctx.StateFor(opacity, opacity) + " gs");
            drawn.Append(body);
            drawn.AppendLine("Q");
            return drawn;
        }

        // ── Viewport ──────────────────────────────────────────────────────────

        /// <summary>
        /// §7.8: the transform mapping <paramref name="element"/>'s <c>viewBox</c> onto the viewport
        /// (<paramref name="x"/>, <paramref name="y"/>, width, height), by its
        /// <c>preserveAspectRatio</c>. <c>none</c> stretches; otherwise one scale, the smaller
        /// (<c>meet</c>, the default) or the larger (<c>slice</c>), aligned by
        /// <c>xMin</c>/<c>xMid</c>/<c>xMax</c> and <c>yMin</c>/<c>yMid</c>/<c>yMax</c> (<c>xMidYMid</c>
        /// by default). Without a viewBox it is a translation to (x, y).
        /// </summary>
        /// <remarks>
        /// A width or height is a length or a percentage of the enclosing viewport, and a missing one
        /// is 100% of it (<see cref="ViewportSide"/>). Both took the viewBox's size, which draws at
        /// scale 1, and a percentage was reported.
        /// </remarks>
        private static Transform ViewportTransform(XmlElement element, float x, float y, string width, string height, ConvertContext ctx)
            => MapViewBox(ViewBox(element), element.GetAttribute("preserveAspectRatio"), x, y, width, height, ctx);

        /// <summary>
        /// <see cref="ViewportTransform"/> over a viewBox the caller supplies — an image's own pixel
        /// box — with <paramref name="preserveAspectRatio"/>'s value.
        /// </summary>
        private static Transform MapViewBox(float[] box, string preserveAspectRatio, float x, float y, string width, string height, ConvertContext ctx)
        {
            if (box == null)
                return new Transform(1, 0, 0, 1, x, y);
            float vbX = box[0], vbY = box[1], vbW = box[2], vbH = box[3];

            float w = ViewportSide("width", width, ctx), h = ViewportSide("height", height, ctx);
            float sx = w / vbW, sy = h / vbH;

            string[] par = (preserveAspectRatio ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int at = par.Length > 0 && par[0] == "defer" ? 1 : 0;
            string align = par.Length > at ? par[at] : "xMidYMid";
            if (align == "none")
                return new Transform(sx, 0, 0, sy, x - vbX * sx, y - vbY * sy);

            bool slice = par.Length > at + 1 && par[at + 1] == "slice";
            float s = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            float ax = align.StartsWith("xMin", StringComparison.Ordinal) ? 0f : align.StartsWith("xMax", StringComparison.Ordinal) ? 1f : 0.5f;
            float ay = align.EndsWith("YMin", StringComparison.Ordinal) ? 0f : align.EndsWith("YMax", StringComparison.Ordinal) ? 1f : 0.5f;
            return new Transform(s, 0, 0, s,
                x - vbX * s + ax * (w - vbW * s),
                y - vbY * s + ay * (h - vbH * s));
        }

        /// <summary>An element's <c>viewBox</c> as four numbers with a positive size, or <c>null</c>.</summary>
        private static float[] ViewBox(XmlElement element)
        {
            var box = ParseFloatList(element.GetAttribute("viewBox"), out bool complete);
            return complete && box.Count == 4 && box[2] > 0 && box[3] > 0 ? box.ToArray() : null;
        }

        /// <summary>
        /// A viewport's side (§7.9): its length, or a percentage of the enclosing viewport's; missing
        /// or not positive, 100% of it — SVG's initial value for a nested <c>svg</c>'s size, and a
        /// used symbol's.
        /// </summary>
        private static float ViewportSide(string name, string value, ConvertContext ctx)
        {
            float full = ViewportPercent(name, 100, ctx);
            if (value == null) return full;
            float side = ParseLength(name, value, full, ctx);
            return side > 0 ? side : full;
        }

        /// <summary>
        /// Runs <paramref name="draw"/> with <paramref name="element"/> as the nearest viewport
        /// (§7.10), what its content's percentages are of: its viewBox's size, else its own width and
        /// height (<see cref="ViewportSide"/>), in the enclosing viewport. The enclosing one is back
        /// when it returns.
        /// </summary>
        private static void InViewport(XmlElement element, string width, string height, ConvertContext ctx, Action draw)
        {
            float[] box = ViewBox(element);
            float w = box?[2] ?? ViewportSide("width", width, ctx), h = box?[3] ?? ViewportSide("height", height, ctx);
            var (outerW, outerH) = (ctx.ViewportWidth, ctx.ViewportHeight);
            (ctx.ViewportWidth, ctx.ViewportHeight) = (w, h);
            try { draw(); }
            finally { (ctx.ViewportWidth, ctx.ViewportHeight) = (outerW, outerH); }
        }

        /// <summary>
        /// Clips to the viewport (x, y, width, height) in <paramref name="xform"/>'s space, as SVG's
        /// initial <c>overflow: hidden</c> asks (§14.3.3), unless the element's <c>overflow</c> is
        /// <c>visible</c> or <c>auto</c>. A side is <see cref="ViewportSide"/>'s. Returns whether a
        /// clip was opened; the caller closes it with <c>Q</c>.
        /// </summary>
        /// <remarks>
        /// Without it the viewport's content spilled over the drawing around it — all of a
        /// <c>slice</c>, which fills the viewport by overflowing it, and anything past the viewBox. A
        /// viewport with a percentage size, or none, and no viewBox was not clipped, its size unknown.
        /// </remarks>
        private static bool OpenViewportClip(XmlElement element, float x, float y, string width, string height,
            StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            string overflow = OwnPresentation(element, "overflow");
            if (overflow == "visible" || overflow == "auto") return false;
            float w = ViewportSide("width", width, ctx), h = ViewportSide("height", height, ctx);
            sb.AppendLine("q");
            RectPath(x, y, w, h, sb, ctx, xform);
            sb.AppendLine("W");
            sb.AppendLine("n");
            return true;
        }

        // ── Use ───────────────────────────────────────────────────────────────

        private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

        /// <summary>Every element with an <c>id</c>, the first of each, under <paramref name="root"/>.</summary>
        private static Dictionary<string, XmlElement> IndexIds(XmlElement root)
        {
            var byId = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            if (root == null) return byId;
            foreach (XmlNode n in root.GetElementsByTagName("*"))
                if (n is XmlElement el && el.GetAttribute("id") is string id && id.Length > 0 && !byId.ContainsKey(id))
                    byId.Add(id, el);
            if (root.GetAttribute("id") is string rootId && rootId.Length > 0 && !byId.ContainsKey(rootId))
                byId.Add(rootId, root);
            return byId;
        }

        /// <summary>
        /// <c>&lt;use&gt;</c> (§5.6): the referenced element drawn again after the use's transform
        /// and a translation by its <c>x</c> and <c>y</c>. The copy is made a child of the use for
        /// the draw, so it inherits from the use and not from where it is defined, as SVG says.
        /// </summary>
        /// <remarks>
        /// A reference can reach an element that contains it, which
        /// would draw forever, and references double at every level of a chain, so a cycle is
        /// refused and the expansions are counted against <see cref="PdfLimits.MaxSvgUseExpansions"/>;
        /// each is reported. Only a reference into the same document is drawn.
        /// </remarks>
        private static void EmitUse(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform, int depth)
        {
            var use = (XmlElement)node;
            string href = UseHref(use);
            if (href.Length == 0)
            {
                ReportOnce("a <use> with no href was skipped");
                return;
            }
            if (!href.StartsWith("#", StringComparison.Ordinal))
            {
                ReportOnce($"<use> of '{href}' was skipped: only a reference into the same document is drawn");
                return;
            }
            string id = href.Substring(1);
            if (!ctx.ElementsById.TryGetValue(id, out XmlElement target))
            {
                ReportOnce($"<use> of '{href}' was skipped: no element has that id");
                return;
            }
            // A copy keeps its id, so the chain of ids above this use is what finds a cycle: the
            // original target need not be an ancestor when the use sits inside an earlier copy.
            for (XmlNode n = use; n is XmlElement el; n = n.ParentNode)
                if (el.GetAttribute("id") == id)
                {
                    ReportOnce($"<use> of '{href}' was skipped: it refers to an element that contains it");
                    return;
                }
            if (++ctx.UseExpansions > PdfLimits.MaxSvgUseExpansions)
            {
                ReportOnce($"<use> references past {PdfLimits.MaxSvgUseExpansions} were skipped: each draws " +
                           "its target again, and a chain of them multiplies");
                return;
            }
            // Charged the elements it copies, before the copy: the count above bounds the references
            // and not their size, and a group of 2 000 rectangles referenced 2 000 times was 8 GB.
            long room = PdfLimits.MaxSvgUseExpandedElements - ctx.UseExpandedElements;
            long size = ElementCount(target, room);
            if (size > room)
            {
                // Spent: a later reference to the same target would count it again, up to the room.
                ctx.UseExpandedElements = PdfLimits.MaxSvgUseExpandedElements;
                ReportOnce($"<use> references past {PdfLimits.MaxSvgUseExpandedElements} copied elements were skipped: " +
                           "each copies its target whole");
                return;
            }
            ctx.UseExpandedElements += size;

            float x = TryParseAttrF(node, "x", 0f, ctx), y = TryParseAttrF(node, "y", 0f, ctx);
            var placed = Transform.Multiply(xform, new Transform(1, 0, 0, 1, x, y));
            XmlNode copy = target.CloneNode(deep: true);
            use.AppendChild(copy);
            try { ProcessNode(copy, sb, ctx, placed, depth + 1); }
            finally { use.RemoveChild(copy); }
        }

        /// <summary>The elements of <paramref name="root"/>'s subtree, itself included, counted no further than one past <paramref name="limit"/>.</summary>
        private static long ElementCount(XmlNode root, long limit)
        {
            long count = 0;
            var pending = new Stack<XmlNode>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                XmlNode n = pending.Pop();
                if (n is not XmlElement) continue;
                if (++count > limit) return count;
                for (XmlNode c = n.FirstChild; c != null; c = c.NextSibling) pending.Push(c);
            }
            return count;
        }

        // ── Group ─────────────────────────────────────────────────────────────

        private static void EmitGroup(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform parent, int depth)
        {
            sb.AppendLine("q");
            var xform = ApplyTransformAttr(node, parent);
            foreach (XmlNode child in node.ChildNodes)
                ProcessNode(child, sb, ctx, xform, depth + 1);
            sb.AppendLine("Q");
        }

        /// <summary>
        /// A container — a <c>g</c>, <c>a</c>, <c>switch</c>, <c>use</c>, nested <c>svg</c> or a
        /// <c>use</c>d <c>symbol</c> — drawn by <paramref name="draw"/>: as it is, or — when its <c>opacity</c> is below
        /// 1, outside a clipPath — as a transparency group (<see cref="EmitTransparencyGroup"/>).
        /// </summary>
        private static void AsGroup(XmlNode node, StringBuilder sb, ConvertContext ctx, Action<StringBuilder> draw)
        {
            float opacity = ctx.InClipPath ? 1f : OwnOpacity(node, ctx);
            if (opacity < 1) EmitTransparencyGroup(sb, ctx, opacity, draw);
            else draw(sb);
        }

        /// <summary>
        /// A container with <c>opacity</c> below 1, as a transparency group (§14.5; ISO 32000-1
        /// §11.6.6): what <paramref name="draw"/> writes is a Form XObject with an isolated
        /// <c>/Group</c>, painted once at the container's opacity, fill and stroke alike. The content is
        /// in page space, so the form's box is the page's, and it draws with the page's resources
        /// (<see cref="ConvertContext.SharedResources"/>).
        /// </summary>
        /// <remarks>
        /// Multiplied into each element the container draws, the opacity would let the lower of two
        /// overlapping elements show through the upper — for a <c>g</c>, an <c>a</c>, a <c>switch</c>,
        /// a <c>use</c> and an <c>svg</c> alike. <see cref="OwnOpacity"/> leaves a container's opacity
        /// out of its elements', since it is applied here, once.
        /// </remarks>
        private static void EmitTransparencyGroup(StringBuilder sb, ConvertContext ctx, float opacity, Action<StringBuilder> draw)
        {
            var content = new StringBuilder();
            draw(content);
            int resources = ctx.SharedResources();
            int number = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.StreamObject(
                $"/Type /XObject /Subtype /Form /BBox {MediaBox(ctx.Options)} /Group << /S /Transparency /I true >> /Resources {resources} 0 R",
                Core.Text.Latin1Text.Encode(content.ToString())));
            string name = "Fm" + ctx.FormCount++.ToString(CultureInfo.InvariantCulture);
            ctx.ListXObject(name, number);
            sb.AppendLine("q");
            sb.AppendLine("/" + ctx.StateFor(opacity, opacity) + " gs");
            sb.AppendLine("/" + name + " Do");
            sb.AppendLine("Q");
        }

        /// <summary>
        /// Composes the element's own <c>transform</c> list onto <paramref name="parent"/>.
        /// The list is applied <b>left to right</b> (SVG 1.1 §7.4), each entry multiplied onto the
        /// accumulated matrix — the predecessor searched for the first <c>translate(</c> and the
        /// first <c>scale(</c> anywhere in the string and merged them component-wise, so both the
        /// order within one attribute and the nesting between two were ignored.
        /// </summary>
        /// <remarks>
        /// All six functions of §7.6: <c>matrix</c>, <c>translate</c>, <c>scale</c>, <c>rotate</c>
        /// (about the origin or a given centre), <c>skewX</c> and <c>skewY</c>. Only the second and
        /// third were read, and a rotation — the commonest transform after a translation — was
        /// reported and dropped.
        /// </remarks>
        private static Transform ApplyTransformAttr(XmlNode node, Transform parent)
            => ApplyTransformList((node as XmlElement)?.GetAttribute("transform"), parent);

        /// <summary>
        /// A transform list — a <c>transform</c> or a <c>gradientTransform</c> — composed onto
        /// <paramref name="parent"/>, as <see cref="ApplyTransformAttr"/> describes.
        /// </summary>
        private static Transform ApplyTransformList(string t, Transform parent)
        {
            if (string.IsNullOrWhiteSpace(t)) return parent;

            Transform result = parent;
            int i = 0;
            while (i < t.Length)
            {
                // name
                while (i < t.Length && !char.IsLetter(t[i])) i++;
                int nameStart = i;
                while (i < t.Length && (char.IsLetter(t[i]) || t[i] == '-')) i++;
                if (i >= t.Length || nameStart == i) break;

                string name = t.Substring(nameStart, i - nameStart);
                int open = t.IndexOf('(', i);
                if (open < 0) break;
                int close = t.IndexOf(')', open + 1);
                if (close < 0) break;

                var args = ParseFloatList(t.Substring(open + 1, close - open - 1));
                i = close + 1;

                if (name.Equals("translate", StringComparison.OrdinalIgnoreCase) && args.Count >= 1)
                    result = Transform.Multiply(result,
                        Transform.Translate(args[0], args.Count >= 2 ? args[1] : 0f));
                else if (name.Equals("scale", StringComparison.OrdinalIgnoreCase) && args.Count >= 1)
                    result = Transform.Multiply(result,
                        Transform.Scale(args[0], args.Count >= 2 ? args[1] : args[0]));
                else if (name.Equals("matrix", StringComparison.OrdinalIgnoreCase) && args.Count >= 6)
                    result = Transform.Multiply(result,
                        new Transform(args[0], args[1], args[2], args[3], args[4], args[5]));
                else if (name.Equals("rotate", StringComparison.OrdinalIgnoreCase) && args.Count >= 1)
                {
                    // rotate(a cx cy) is translate(cx cy) rotate(a) translate(−cx −cy) (§7.6).
                    double a = args[0] * Math.PI / 180;
                    var turn = new Transform((float)Math.Cos(a), (float)Math.Sin(a), (float)-Math.Sin(a), (float)Math.Cos(a), 0, 0);
                    if (args.Count >= 3)
                        turn = Transform.Multiply(Transform.Multiply(Transform.Translate(args[1], args[2]), turn),
                                                  Transform.Translate(-args[1], -args[2]));
                    result = Transform.Multiply(result, turn);
                }
                else if (name.Equals("skewX", StringComparison.OrdinalIgnoreCase) && args.Count >= 1)
                    result = Transform.Multiply(result, new Transform(1, 0, (float)Math.Tan(args[0] * Math.PI / 180), 1, 0, 0));
                else if (name.Equals("skewY", StringComparison.OrdinalIgnoreCase) && args.Count >= 1)
                    result = Transform.Multiply(result, new Transform(1, (float)Math.Tan(args[0] * Math.PI / 180), 0, 1, 0, 0));
                else
                    Report($"transform function '{name}' is not supported and was ignored");
            }

            return result;
        }

        // ── Rect ──────────────────────────────────────────────────────────────

        private static void EmitRect(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            float x  = TryParseAttrF(node, "x", 0f, ctx);
            float y  = TryParseAttrF(node, "y", 0f, ctx);
            float w  = TryParseAttrF(node, "width", 0f, ctx);
            float h  = TryParseAttrF(node, "height", 0f, ctx);

            SetColors(node, sb, ctx, xform);

            // §9.2: a missing radius takes the other's, and each is clamped to half its side.
            float rx = TryParseAttrF(node, "rx", -1f, ctx), ry = TryParseAttrF(node, "ry", -1f, ctx);
            if (rx < 0) rx = ry;
            if (ry < 0) ry = rx;
            if (rx > 0 && ry > 0)
            {
                // Four quarter-ellipse corners, clockwise from the top edge. The radii were
                // ignored, so a rounded button drew square.
                rx = Math.Min(rx, w / 2);
                ry = Math.Min(ry, h / 2);
                var path = new PathState(sb, ctx, xform);
                path.MoveTo(x + rx, y);
                path.LineTo(x + w - rx, y);
                path.ArcTo(rx, ry, 0, false, true, x + w, y + ry);
                path.LineTo(x + w, y + h - ry);
                path.ArcTo(rx, ry, 0, false, true, x + w - rx, y + h);
                path.LineTo(x + rx, y + h);
                path.ArcTo(rx, ry, 0, false, true, x, y + h - ry);
                path.LineTo(x, y + ry);
                path.ArcTo(rx, ry, 0, false, true, x + rx, y);
                path.Close();
            }
            else
                RectPath(x, y, w, h, sb, ctx, xform);
            EmitPaintOperator(node, sb, ctx, fillApplies: true);
        }

        /// <summary>
        /// The rectangle (x, y, w, h) as a path: an <c>re</c> when the mapping keeps it
        /// axis-aligned, else its four corners mapped. A rect's shape and a viewport's clip both.
        /// </summary>
        private static void RectPath(float x, float y, float w, float h, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            if (xform.B != 0 || xform.C != 0)
            {
                // `re` is axis-aligned: a rotated or skewed rect is its four corners.
                var (ax, ay) = MapXY(x, y, ctx, xform);
                var (bx, by) = MapXY(x + w, y, ctx, xform);
                var (cx, cy) = MapXY(x + w, y + h, ctx, xform);
                var (dx, dy) = MapXY(x, y + h, ctx, xform);
                sb.AppendLine(F($"{ax} {ay} m"));
                sb.AppendLine(F($"{bx} {by} l"));
                sb.AppendLine(F($"{cx} {cy} l"));
                sb.AppendLine(F($"{dx} {dy} l"));
                sb.AppendLine("h");
            }
            else
            {
                (float px, float py) = MapXY(x, y, ctx, xform);
                float pw = w * ctx.ScaleX * xform.A;
                float ph = h * ctx.ScaleY * xform.D;
                if (ctx.Options.FlipY) py -= ph; // rect origin is bottom-left in PDF
                if (ctx.Tracking) { ctx.Track(px, py); ctx.Track(px + pw, py + ph); }
                ctx.Paint?.Track(x + w, y + h);   // the far corner, which MapXY did not see
                sb.AppendLine(F($"{px} {py} {pw} {ph} re"));
            }
        }

        // ── Circle ────────────────────────────────────────────────────────────

        private static void EmitCircle(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            float cx = TryParseAttrF(node, "cx", 0f, ctx);
            float cy = TryParseAttrF(node, "cy", 0f, ctx);
            float r  = TryParseAttrF(node, "r",  0f, ctx);
            EmitEllipseCore(node, sb, ctx, xform, cx, cy, r, r);
        }

        private static void EmitEllipse(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            float cx = TryParseAttrF(node, "cx", 0f, ctx);
            float cy = TryParseAttrF(node, "cy", 0f, ctx);
            float rx = TryParseAttrF(node, "rx", 0f, ctx);
            float ry = TryParseAttrF(node, "ry", 0f, ctx);
            EmitEllipseCore(node, sb, ctx, xform, cx, cy, rx, ry);
        }

        private static void EmitEllipseCore(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform,
            float cx, float cy, float rx, float ry)
        {
            // Approximate ellipse with 4 cubic Bézier curves.
            // k ≈ 0.5523 is the magic constant.
            const float k = 0.5523f;
            SetColors(node, sb, ctx, xform);
            if (xform.B != 0 || xform.C != 0)
            {
                // Rotated or skewed: the four quarters are built in user space and every point
                // mapped, since the scale-only arithmetic below cannot turn.
                (float, float) P(float ux, float uy) => MapXY(ux, uy, ctx, xform);
                var s0 = P(cx + rx, cy);
                sb.AppendLine(F($"{s0.Item1} {s0.Item2} m"));
                void Quarter((float, float) c1, (float, float) c2, (float, float) e)
                    => sb.AppendLine(F($"{c1.Item1} {c1.Item2} {c2.Item1} {c2.Item2} {e.Item1} {e.Item2} c"));
                Quarter(P(cx + rx, cy + k * ry), P(cx + k * rx, cy + ry), P(cx, cy + ry));
                Quarter(P(cx - k * rx, cy + ry), P(cx - rx, cy + k * ry), P(cx - rx, cy));
                Quarter(P(cx - rx, cy - k * ry), P(cx - k * rx, cy - ry), P(cx, cy - ry));
                Quarter(P(cx + k * rx, cy - ry), P(cx + rx, cy - k * ry), s0);
                EmitPaintOperator(node, sb, ctx, fillApplies: true);
                return;
            }

            (float pcx, float pcy) = MapXY(cx, cy, ctx, xform);
            float prx = rx * ctx.ScaleX * xform.A;
            float pry = ry * ctx.ScaleY * xform.D;
            // MapXY saw the centre alone: the box's corners, for a link's rectangle and a gradient's box.
            // Only the centre reached a link, so a linked circle's link was a point.
            if (ctx.Tracking) { ctx.Track(pcx - prx, pcy - pry); ctx.Track(pcx + prx, pcy + pry); }
            ctx.Paint?.Track(cx - rx, cy - ry);
            ctx.Paint?.Track(cx + rx, cy + ry);
            sb.AppendLine(F($"{pcx} {pcy + pry} m"));
            sb.AppendLine(F($"{pcx + k * prx} {pcy + pry} {pcx + prx} {pcy + k * pry} {pcx + prx} {pcy} c"));
            sb.AppendLine(F($"{pcx + prx} {pcy - k * pry} {pcx + k * prx} {pcy - pry} {pcx} {pcy - pry} c"));
            sb.AppendLine(F($"{pcx - k * prx} {pcy - pry} {pcx - prx} {pcy - k * pry} {pcx - prx} {pcy} c"));
            sb.AppendLine(F($"{pcx - prx} {pcy + k * pry} {pcx - k * prx} {pcy + pry} {pcx} {pcy + pry} c"));
            EmitPaintOperator(node, sb, ctx, fillApplies: true);
        }

        // ── Line ──────────────────────────────────────────────────────────────

        private static void EmitLine(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            float x1 = TryParseAttrF(node, "x1", 0f, ctx);
            float y1 = TryParseAttrF(node, "y1", 0f, ctx);
            float x2 = TryParseAttrF(node, "x2", 0f, ctx);
            float y2 = TryParseAttrF(node, "y2", 0f, ctx);

            (float px1, float py1) = MapXY(x1, y1, ctx, xform);
            (float px2, float py2) = MapXY(x2, y2, ctx, xform);

            SetStrokeColor(node, sb, ctx, xform);
            if (ctx.Paint != null) ctx.Paint.PathStart = sb.Length;   // the path follows
            sb.AppendLine(F($"{px1} {py1} m"));
            sb.AppendLine(F($"{px2} {py2} l"));
            // `fill` has no effect on <line> (SVG 1.1 §11.3), so the only question is whether to
            // stroke: appending `S` unconditionally would draw stroke="none" lines in whatever RG
            // colour the previous element happened to leave in the graphics state.
            EmitPaintOperator(node, sb, ctx, fillApplies: false);
            if (HasMarkers(node, ctx))
                DrawMarkers(node, new List<Vertex>
                {
                    new Vertex(x1, y1) { OutX = x2 - x1, OutY = y2 - y1, HasOut = true },
                    new Vertex(x2, y2) { InX = x2 - x1, InY = y2 - y1, HasIn = true },
                }, sb, ctx, xform);
        }

        // ── Polyline / Polygon ────────────────────────────────────────────────

        private static void EmitPolyline(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform, bool close)
        {
            var el = node as XmlElement;
            string pts = el?.GetAttribute("points") ?? string.Empty;
            var coords = ParseFloatList(pts);
            if (coords.Count < 2) return;

            SetColors(node, sb, ctx, xform);
            for (int i = 0; i < coords.Count - 1; i += 2)
            {
                (float px, float py) = MapXY(coords[i], coords[i + 1], ctx, xform);
                sb.AppendLine(i == 0 ? F($"{px} {py} m") : F($"{px} {py} l"));
            }
            // A <polygon> closes its subpath, so a stroked one draws the closing edge too.
            if (close) sb.AppendLine("h");
            List<Vertex> vertices = null;
            if (HasMarkers(node, ctx))
            {
                // The same path through PathState, into a scratch buffer, for its vertices alone.
                var trace = new PathState(new StringBuilder(), ctx, xform) { Vertices = new List<Vertex>() };
                for (int i = 0; i < coords.Count - 1; i += 2)
                    if (i == 0) trace.MoveTo(coords[i], coords[i + 1]); else trace.LineTo(coords[i], coords[i + 1]);
                if (close) trace.Close();
                vertices = trace.Vertices;
            }
            // Open or closed, the paint operator is decided the same way: an open <polyline> is
            // still filled (implicitly closed, §11.3) when it has a fill, and must NOT be stroked
            // when it has none — the `else sb.AppendLine("S")` this replaces stroked every one.
            EmitPaintOperator(node, sb, ctx, fillApplies: true);
            if (vertices != null) DrawMarkers(node, vertices, sb, ctx, xform);
        }

        // ── Path ──────────────────────────────────────────────────────────────

        /// <summary>
        /// A <c>&lt;path&gt;</c>: every command of SVG 1.1 §8.3's grammar, absolute and relative.
        /// </summary>
        /// <remarks>
        /// <para><b>It drew five of them.</b> <c>M</c>, <c>L</c>, <c>H</c>, <c>V</c> and <c>Z</c> were
        /// handled, and every curve — cubic <c>C</c>/<c>S</c>, quadratic <c>Q</c>/<c>T</c>, elliptical
        /// arc <c>A</c> — became a PDF comment reported nowhere, so an icon or a logo came out as the
        /// straight segments between its curves. A quadratic is drawn as its exact cubic; an arc as
        /// cubics of at most a quarter turn each, from the endpoint parameterisation of Appendix
        /// F.6.</para>
        /// <para><b>Two grammar rules were wrong too.</b> Pairs after a moveto are implicit linetos
        /// (§8.3.2) and were drawn as further movetos, so <c>M 0 0 10 10</c> drew nothing; and
        /// closepath returns the current point to the subpath's start (§8.3.3), which a relative
        /// command after it counts from.</para>
        /// </remarks>
        private static void EmitPath(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            var el = node as XmlElement;
            string d = el?.GetAttribute("d") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(d)) return;

            SetColors(node, sb, ctx, xform);

            var path = new PathState(sb, ctx, xform) { Vertices = HasMarkers(node, ctx) ? new List<Vertex>() : null };
            TracePath(d, path);
            EmitPaintOperator(node, sb, ctx, fillApplies: true);
            if (path.Vertices != null) DrawMarkers(node, path.Vertices, sb, ctx, xform);
        }

        /// <summary>
        /// Path data (§8.3) through <paramref name="path"/>, command by command: drawn, or flattened
        /// when the state is capturing (<see cref="PathState.Flat"/>).
        /// </summary>
        private static void TracePath(string d, PathState path)
        {
            var tokens = TokenisePath(d);
            int t = 0;
            while (t < tokens.Count)
            {
                string cmd = tokens[t++];
                if (!IsAsciiLetter(cmd[0]))
                {
                    Report($"path data has a number ('{cmd}') where a command belongs; it was skipped");
                    continue;
                }

                char op = char.ToUpperInvariant(cmd[0]);
                bool rel = cmd[0] != op;
                switch (op)
                {
                    case 'M':
                    {
                        bool first = true;
                        while (HasNumbers(tokens, t, 2))
                        {
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x, out float y);
                            if (first) path.MoveTo(x, y); else path.LineTo(x, y);   // §8.3.2: the rest are linetos
                            first = false;
                        }
                        t = SkipDanglingCoordinate(tokens, t, cmd);
                        break;
                    }
                    case 'L':
                        while (HasNumbers(tokens, t, 2))
                        {
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x, out float y);
                            path.LineTo(x, y);
                        }
                        t = SkipDanglingCoordinate(tokens, t, cmd);
                        break;
                    case 'H':
                        while (HasNumbers(tokens, t, 1))
                        {
                            float v = ParseF(tokens[t++]);
                            path.LineTo(rel ? path.X + v : v, path.Y);
                        }
                        break;
                    case 'V':
                        while (HasNumbers(tokens, t, 1))
                        {
                            float v = ParseF(tokens[t++]);
                            path.LineTo(path.X, rel ? path.Y + v : v);
                        }
                        break;
                    case 'C':
                        while (HasNumbers(tokens, t, 6))
                        {
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x1, out float y1);
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x2, out float y2);
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x, out float y);
                            path.CurveTo(x1, y1, x2, y2, x, y);
                        }
                        t = SkipIncomplete(tokens, t, cmd);
                        break;
                    case 'S':
                        while (HasNumbers(tokens, t, 4))
                        {
                            // §8.3.6: the first control point reflects the previous cubic's second
                            // about the current point, or is the current point after anything else.
                            float x1 = path.LastWasCubic ? 2 * path.X - path.ControlX : path.X;
                            float y1 = path.LastWasCubic ? 2 * path.Y - path.ControlY : path.Y;
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x2, out float y2);
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x, out float y);
                            path.CurveTo(x1, y1, x2, y2, x, y);
                        }
                        t = SkipIncomplete(tokens, t, cmd);
                        break;
                    case 'Q':
                        while (HasNumbers(tokens, t, 4))
                        {
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float qx, out float qy);
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x, out float y);
                            path.QuadTo(qx, qy, x, y);
                        }
                        t = SkipIncomplete(tokens, t, cmd);
                        break;
                    case 'T':
                        while (HasNumbers(tokens, t, 2))
                        {
                            // §8.3.7: the control point reflects the previous quadratic's.
                            float qx = path.LastWasQuad ? 2 * path.X - path.ControlX : path.X;
                            float qy = path.LastWasQuad ? 2 * path.Y - path.ControlY : path.Y;
                            path.Point(ParseF(tokens[t++]), ParseF(tokens[t++]), rel, out float x, out float y);
                            path.QuadTo(qx, qy, x, y);
                        }
                        t = SkipDanglingCoordinate(tokens, t, cmd);
                        break;
                    case 'A':
                        while (TryReadArc(tokens, ref t, out float rx, out float ry, out float rotation,
                                          out bool largeArc, out bool sweep, out float ax, out float ay))
                        {
                            path.Point(ax, ay, rel, out float x, out float y);
                            path.ArcTo(rx, ry, rotation, largeArc, sweep, x, y);
                        }
                        t = SkipIncomplete(tokens, t, cmd);
                        break;
                    case 'Z':
                        path.Close();
                        break;
                    default:
                        Report($"path command '{cmd}' is not in SVG's path grammar; it and its numbers were skipped");
                        while (t < tokens.Count && IsNum(tokens[t])) t++;
                        break;
                }
            }
        }

        /// <summary>
        /// The current point, the subpath's start and the last control point, and the operators that
        /// move them — each point mapped through <see cref="MapXY"/> as it is written.
        /// </summary>
        private sealed class PathState
        {
            private readonly StringBuilder _sb;
            private readonly ConvertContext _ctx;
            private readonly Transform _xform;
            private float _startX, _startY;
            private char _last;   // 'C' after a cubic, 'Q' after a quadratic, else ' '
            private int _compound;                    // > 0 while a quadratic or an arc writes its cubics
            private float _firstX1, _firstY1, _lastX2, _lastY2;
            private bool _haveFirst;

            internal PathState(StringBuilder sb, ConvertContext ctx, Transform xform)
            { _sb = sb; _ctx = ctx; _xform = xform; }

            /// <summary>
            /// The path's vertices with their directions, for markers; <c>null</c> (the default)
            /// records nothing. A command is one segment, so a quadratic or an arc — written as
            /// cubics — adds one vertex, not one per cubic.
            /// </summary>
            internal List<Vertex> Vertices;

            /// <summary>
            /// When set, the path is not written: it is flattened into these polylines, one per subpath,
            /// in user space — a textPath's path (§10.13), measured along rather than drawn. Nothing is
            /// mapped, so no link's or gradient's box sees it.
            /// </summary>
            internal List<List<(float X, float Y)>> Flat;

            /// <summary>Straight pieces a flattened cubic is cut into.</summary>
            private const int FlatSteps = 16;

            internal float X { get; private set; }
            internal float Y { get; private set; }
            internal float ControlX { get; private set; }
            internal float ControlY { get; private set; }
            internal bool LastWasCubic => _last == 'C';
            internal bool LastWasQuad => _last == 'Q';

            /// <summary>A command's coordinate pair in user space: relative ones count from the current point.</summary>
            internal void Point(float x, float y, bool relative, out float ux, out float uy)
            {
                ux = relative ? X + x : x;
                uy = relative ? Y + y : y;
            }

            internal void MoveTo(float x, float y)
            {
                Write("m", x, y);
                X = _startX = x; Y = _startY = y; _last = ' ';
                Vertices?.Add(new Vertex(x, y));
            }

            internal void LineTo(float x, float y)
            {
                float x0 = X, y0 = Y;
                Write("l", x, y);
                X = x; Y = y; _last = ' ';
                Segment(x0, y0, x - x0, y - y0, x, y, x - x0, y - y0);
            }

            internal void CurveTo(float x1, float y1, float x2, float y2, float x, float y)
            {
                float x0 = X, y0 = Y;
                if (_compound > 0)
                {
                    if (!_haveFirst) { _firstX1 = x1; _firstY1 = y1; _haveFirst = true; }
                    _lastX2 = x2; _lastY2 = y2;
                }
                else
                    Segment(x0, y0, x1 - x0, y1 - y0, x, y, x - x2, y - y2);
                if (Flat != null)
                {
                    for (int i = 1; i <= FlatSteps; i++)
                    {
                        float t = (float)i / FlatSteps, u = 1 - t;
                        float b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
                        Flatten(b0 * x0 + b1 * x1 + b2 * x2 + b3 * x, b0 * y0 + b1 * y1 + b2 * y2 + b3 * y);
                    }
                }
                else
                {
                    // A curve's box is its ends and its turning points, not its control points' hull,
                    // which can reach well past it (SVG 2 §8.10's bounding box is the geometry's) — so
                    // the control points are mapped untracked and the extrema tracked in each space.
                    var (p0x, p0y) = MapXY(x0, y0, _ctx, _xform, track: false);
                    var (p1x, p1y) = MapXY(x1, y1, _ctx, _xform, track: false);
                    var (p2x, p2y) = MapXY(x2, y2, _ctx, _xform, track: false);
                    var (px, py) = MapXY(x, y, _ctx, _xform);
                    _sb.AppendLine(F($"{p1x} {p1y} {p2x} {p2y} {px} {py} c"));
                    if (_ctx.Paint != null)
                        TrackCubicExtrema(x0, y0, x1, y1, x2, y2, x, y, _ctx.Paint.Track);
                    if (_ctx.Tracking)
                        TrackCubicExtrema(p0x, p0y, p1x, p1y, p2x, p2y, px, py, _ctx.Track);
                }
                ControlX = x2; ControlY = y2;
                X = x; Y = y; _last = 'C';
            }

            /// <summary>A quadratic as its exact cubic: each control point two thirds of the way from an end to the quadratic's.</summary>
            internal void QuadTo(float qx, float qy, float x, float y)
            {
                float x0 = X, y0 = Y;
                _compound++;
                CurveTo(x0 + 2f / 3 * (qx - x0), y0 + 2f / 3 * (qy - y0),
                        x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y), x, y);
                _compound--;
                ControlX = qx; ControlY = qy; _last = 'Q';
                Segment(x0, y0, qx - x0, qy - y0, x, y, x - qx, y - qy);
            }

            /// <summary>
            /// An elliptical arc as cubics of at most a quarter turn, from the endpoint
            /// parameterisation (SVG 1.1 Appendix F.6.5), with out-of-range radii corrected (F.6.6).
            /// </summary>
            internal void ArcTo(float rxIn, float ryIn, float rotationDeg, bool largeArc, bool sweep, float x2, float y2)
            {
                double x1 = X, y1 = Y;
                if (x1 == x2 && y1 == y2) { _last = ' '; return; }        // F.6.2: omitted
                double rx = Math.Abs(rxIn), ry = Math.Abs(ryIn);
                if (rx == 0 || ry == 0) { LineTo(x2, y2); return; }      // F.6.2: a straight line

                double phi = rotationDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
                double dx2 = (x1 - x2) / 2, dy2 = (y1 - y2) / 2;
                double x1p = cos * dx2 + sin * dy2, y1p = -sin * dx2 + cos * dy2;

                double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
                if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; }

                double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
                double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
                double coef = Math.Sqrt(Math.Max(0, num / den)) * (largeArc == sweep ? -1 : 1);
                double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
                double cx = cos * cxp - sin * cyp + (x1 + x2) / 2;
                double cy = sin * cxp + cos * cyp + (y1 + y2) / 2;

                double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
                double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;
                double theta1 = Math.Atan2(uy, ux);
                double delta = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
                if (!sweep && delta > 0) delta -= 2 * Math.PI;
                else if (sweep && delta < 0) delta += 2 * Math.PI;

                int segments = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9)));
                double step = delta / segments, k = 4.0 / 3 * Math.Tan(step / 4);
                _compound++; _haveFirst = false;
                for (int i = 0; i < segments; i++)
                {
                    double a1 = theta1 + i * step, a2 = a1 + step;
                    double c1 = Math.Cos(a1), s1 = Math.Sin(a1), c2 = Math.Cos(a2), s2 = Math.Sin(a2);
                    bool last = i == segments - 1;
                    (float, float) OnEllipse(double px, double py) =>
                        ((float)(cx + rx * cos * px - ry * sin * py), (float)(cy + rx * sin * px + ry * cos * py));
                    var (p1x, p1y) = OnEllipse(c1 - k * s1, s1 + k * c1);
                    var (p2x, p2y) = OnEllipse(c2 + k * s2, s2 - k * c2);
                    var (ex, ey) = last ? (x2, y2) : OnEllipse(c2, s2);   // the last lands exactly on the end
                    CurveTo(p1x, p1y, p2x, p2y, ex, ey);
                }
                _compound--;
                _last = ' ';
                Segment((float)x1, (float)y1, _firstX1 - (float)x1, _firstY1 - (float)y1, x2, y2, x2 - _lastX2, y2 - _lastY2);
            }

            /// <summary>§8.3.3: <c>h</c>, and the current point returns to the subpath's start.</summary>
            internal void Close()
            {
                float x0 = X, y0 = Y;
                if (Flat != null) Flatten(_startX, _startY);
                else _sb.AppendLine("h");
                if (x0 != _startX || y0 != _startY)
                    Segment(x0, y0, _startX - x0, _startY - y0, _startX, _startY, _startX - x0, _startY - y0);
                X = _startX; Y = _startY; _last = ' ';
            }

            /// <summary>A segment from (x0, y0) to (x, y): the start's outgoing direction and a new vertex with its incoming one.</summary>
            private void Segment(float x0, float y0, float outX, float outY, float x, float y, float inX, float inY)
            {
                if (Vertices == null || _compound > 0) return;
                if (Vertices.Count == 0) Vertices.Add(new Vertex(x0, y0));
                Vertex from = Vertices[Vertices.Count - 1];
                if (!from.HasOut) { from.OutX = outX; from.OutY = outY; from.HasOut = true; }
                Vertices.Add(new Vertex(x, y) { InX = inX, InY = inY, HasIn = true });
            }

            private void Write(string op, float x, float y)
            {
                if (Flat != null)
                {
                    if (op == "m") Flat.Add(new List<(float, float)> { (x, y) });
                    else Flatten(x, y);
                    return;
                }
                var (px, py) = MapXY(x, y, _ctx, _xform);
                _sb.AppendLine(F($"{px} {py} {op}"));
            }

            /// <summary>A point of the flattened subpath; a path that draws before its first move starts at the origin.</summary>
            private void Flatten(float x, float y)
            {
                if (Flat.Count == 0) Flat.Add(new List<(float, float)> { (X, Y) });
                Flat[Flat.Count - 1].Add((x, y));
            }
        }

        /// <summary>Whether <paramref name="count"/> more numeric tokens are available from <paramref name="t"/>.</summary>
        /// <remarks>
        /// <b>Why every command asks this before it reads.</b> The <c>M</c>/<c>L</c> handlers once
        /// checked for <i>one</i> token and then consumed <i>two</i>, so <c>d="M 10"</c> — or any
        /// segment the input truncates — indexed one past the end and threw
        /// <see cref="ArgumentOutOfRangeException"/> out of the public <c>Convert</c> entry points.
        /// </remarks>
        private static bool HasNumbers(List<string> tokens, int t, int count)
        {
            if (t + count > tokens.Count) return false;
            for (int i = 0; i < count; i++) if (!IsNum(tokens[t + i])) return false;
            return true;
        }

        /// <summary>Consumes the numbers of a segment the data cut short, reporting it rather than throwing.</summary>
        private static int SkipIncomplete(List<string> tokens, int t, string cmd)
        {
            if (t >= tokens.Count || !IsNum(tokens[t])) return t;
            Report($"path command '{cmd}' ended with an incomplete segment; it was dropped");
            while (t < tokens.Count && IsNum(tokens[t])) t++;
            return t;
        }

        /// <summary>
        /// One arc segment's seven arguments. A flag is one character (§8.3.9's grammar), so a
        /// compact <c>01</c> is the two flags, and the rest of a token after a flag is the next token.
        /// </summary>
        private static bool TryReadArc(List<string> tokens, ref int t, out float rx, out float ry,
            out float rotation, out bool largeArc, out bool sweep, out float x, out float y)
        {
            rx = ry = rotation = x = y = 0; largeArc = sweep = false;
            int at = t;
            if (!HasNumbers(tokens, at, 3)) return false;
            float r1 = ParseF(tokens[at]), r2 = ParseF(tokens[at + 1]), rot = ParseF(tokens[at + 2]);
            at += 3;

            // The flags and the end point read from a copy, so a split flag token is not committed
            // until the whole segment has been read.
            int copied = Math.Min(tokens.Count - at, 4);
            var scratch = tokens.GetRange(at, copied);
            int si = 0;
            if (!TakeFlag(scratch, ref si, out bool f1) || !TakeFlag(scratch, ref si, out bool f2)) return false;
            if (!HasNumbers(scratch, si, 2)) return false;
            float ex = ParseF(scratch[si]), ey = ParseF(scratch[si + 1]);

            // Commit: what the flags left unread replaces what was copied, one token per number.
            tokens.RemoveRange(at, copied);
            tokens.InsertRange(at, scratch.GetRange(si, scratch.Count - si));
            t = at + 2;       // past the end point's two numbers
            rx = r1; ry = r2; rotation = rot; largeArc = f1; sweep = f2; x = ex; y = ey;
            return true;
        }

        /// <summary>Takes one flag character from <paramref name="scratch"/>, splitting a longer token.</summary>
        private static bool TakeFlag(List<string> scratch, ref int si, out bool flag)
        {
            flag = false;
            if (si >= scratch.Count || !IsNum(scratch[si])) return false;
            string tok = scratch[si];
            if (tok[0] != '0' && tok[0] != '1') return false;
            flag = tok[0] == '1';
            if (tok.Length == 1) si++;
            else scratch[si] = tok.Substring(1);
            return true;
        }

        /// <summary>Consumes a half-written coordinate pair, reporting it rather than throwing.</summary>
        private static int SkipDanglingCoordinate(List<string> tokens, int t, string cmd)
        {
            if (t >= tokens.Count || !IsNum(tokens[t])) return t;
            Report($"path command '{cmd}' ended with a half-written coordinate pair ('{tokens[t]}'); " +
                   "it was dropped");
            return t + 1;
        }

        // ── Text ──────────────────────────────────────────────────────────────

        /// <summary>One run of a text element: the characters one element styles, and where they start.</summary>
        private sealed class TextRun
        {
            public XmlElement Owner;
            public string Text;
            public float? AbsoluteX, AbsoluteY;
            public float Dx, Dy;
            public string Drawn;
            public float Size, X, Y, Width;
            public bool StartsChunk;
            /// <summary>The standard face the run is set in (<see cref="FaceOf"/>), when <see cref="Embedded"/> is null.</summary>
            public string Face;
            /// <summary>
            /// The embedded font the run is set in and its glyphs, shaped, in visual order — for text
            /// WinAnsi cannot carry (<see cref="ShapeRun"/>); <c>null</c> for a run in a standard face.
            /// </summary>
            public EmbeddedFont Embedded;
            public Core.Text.ShapedGlyphRun Shaped;
            /// <summary><c>letter-spacing</c> (§10.11), in user units: added after each character.</summary>
            public float Spacing;
            /// <summary><c>word-spacing</c> (§10.11), in user units: added at each space.</summary>
            public float WordSpacing;
            /// <summary>The run's <c>rotate</c> (§10.5), in degrees: a turned run is one character, turned about its origin.</summary>
            public float Rotate;
            /// <summary>The textPath the run is set along (§10.13), or <c>null</c>: such a run is one character.</summary>
            public XmlElement OnPath;
            /// <summary>A character off its path's ends, or on a path that names none: laid out, not drawn.</summary>
            public bool Hidden;
        }

        /// <summary>
        /// The standard face a run is set in (§10.10): the first family in <c>font-family</c> that one of
        /// the twelve non-symbolic standard faces stands for — <c>serif</c> is Times, <c>monospace</c>
        /// Courier, <c>sans-serif</c> Helvetica, and a named family as <see cref="Core.Fonts.Standard14Names.Canonical"/>
        /// reads it — in its weight (bold from 600, or <c>bold</c>/<c>bolder</c>) and style
        /// (<c>italic</c> or <c>oblique</c>). None, and it is Helvetica — <paramref name="unmatched"/>
        /// is the list then, for the caller to report unless an embedded font took the run.
        /// <paramref name="first"/> is the first family named, or <c>null</c>; <paramref name="preferred"/>
        /// is that family when it is a named one no standard face stands for — Roboto, David — which
        /// the options' provider may supply for text a standard face could carry (<see cref="ShapeRun"/>).
        /// </summary>
        /// <remarks>
        /// Every run was Helvetica. Symbol and ZapfDingbats are passed over: their codes are not
        /// WinAnsi's, so the run's bytes would draw other glyphs.
        /// </remarks>
        private static string FaceOf(XmlElement owner, out string first, out string preferred, out string unmatched)
        {
            WeightAndStyle(owner, out bool bold, out bool italic);
            string styled = "," + (bold ? "Bold" : "") + (italic ? "Italic" : "");
            string families = GetPresentation(owner, "font-family");
            first = preferred = unmatched = null;
            string chosen = null;
            if (families != null)
            {
                foreach (string entry in families.Split(','))
                {
                    string family = entry.Trim().Trim('\'', '"').Trim();
                    if (family.Length == 0) continue;
                    string named = IsGenericFamily(family) ? StandardFamilyOf(family) : family;
                    string face = Core.Fonts.Standard14Names.Canonical(named + styled);
                    bool standard = face != null && face != StandardFonts.Symbol && face != StandardFonts.ZapfDingbats;
                    if (first == null)
                    {
                        first = family;
                        // A generic family always has a standard face, so it is never preferred.
                        if (!standard) preferred = family;
                    }
                    if (standard && chosen == null) chosen = face;
                }
                if (chosen == null) unmatched = families.Trim();
            }
            return chosen ?? Core.Fonts.Standard14Names.Canonical("Helvetica" + styled);
        }

        private static bool IsGenericFamily(string family) => family == "serif" || family == "sans-serif" || family == "monospace";

        /// <summary>Whether <paramref name="owner"/>'s text runs right to left: <c>direction="rtl"</c> (§10.7.4, inherited).</summary>
        private static bool IsRightToLeft(XmlNode owner) => GetPresentation(owner, "direction") == "rtl";

        /// <summary>
        /// Whether <paramref name="owner"/>'s text is drawn under a directional override
        /// (<c>unicode-bidi: bidi-override</c> or <c>isolate-override</c>, §10.7.4): set on it or on
        /// the element whose content it is part of, since an override covers that content.
        /// </summary>
        private static bool IsDirectionallyOverridden(XmlNode owner)
            => GetPresentation(owner, "unicode-bidi") is "bidi-override" or "isolate-override";

        /// <summary>The standard family a generic one stands for; any other family is itself.</summary>
        private static string StandardFamilyOf(string family)
            => family == "serif" ? "Times" : family == "monospace" ? "Courier" : family == "sans-serif" ? "Helvetica" : family;

        /// <summary>
        /// Whether <paramref name="owner"/>'s text is bold (<c>font-weight</c> from 600, or <c>bold</c>/<c>bolder</c>)
        /// and italic (<c>italic</c> or <c>oblique</c>) — the standard face and the embedded font read it alike.
        /// </summary>
        private static void WeightAndStyle(XmlElement owner, out bool bold, out bool italic)
        {
            string weight = GetPresentation(owner, "font-weight");
            bold = weight == "bold" || weight == "bolder"
                || (int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w >= 600);
            string style = GetPresentation(owner, "font-style");
            italic = style == "italic" || style == "oblique";
        }

        private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

        /// <summary>
        /// A text element as runs (§10.4, §10.5): each tspan's characters with its own position
        /// and style, each run after the one before it, and <c>text-anchor</c> applied per chunk —
        /// the runs from one absolute position to the next.
        /// </summary>
        /// <remarks>
        /// The element's whole <c>InnerText</c> was one run at its own position: a tspan's
        /// <c>x</c>, <c>y</c>, <c>dx</c>, <c>dy</c>, fill and size were ignored, so a two-line label
        /// printed as one line in one colour, and the source's newlines and indentation reached the
        /// PDF string.
        /// </remarks>
        private static void EmitText(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            if (ctx.ClipMode)
            {
                // Text as a clip would need text render mode 7 inside the clip's BT/ET.
                ReportOnce("text in a clipPath is not applied; the clip is its other shapes");
                return;
            }
            var element = (XmlElement)node;
            var runs = new List<TextRun>();
            bool preserve = element.GetAttribute("space", XmlNamespace) == "preserve";
            bool lastWasSpace = true;   // so a leading space is stripped
            var spans = new List<TextSpan>();
            int count = 0;
            CollectRuns(element, runs, preserve, ref lastWasSpace, spans, ref count, depth: 0);
            if (!preserve)
                for (int i = runs.Count - 1; i >= 0; i--)
                {
                    if (runs[i].Text.Length == 0) continue;
                    if (runs[i].Text[runs[i].Text.Length - 1] == ' ') runs[i].Text = runs[i].Text.Substring(0, runs[i].Text.Length - 1);
                    break;
                }
            runs = PlaceCharacters(runs, spans, ctx);

            float cx = 0, cy = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                TextRun run = runs[i];
                // A textPath's characters are a chunk of their own, anchored on the path (PlaceOnPaths).
                run.StartsChunk = i == 0 || run.AbsoluteX.HasValue || run.AbsoluteY.HasValue || run.OnPath != runs[i - 1].OnPath;
                if (run.AbsoluteX.HasValue) cx = run.AbsoluteX.Value;
                if (run.AbsoluteY.HasValue) cy = run.AbsoluteY.Value;
                cx += run.Dx; cy += run.Dy;
                // font-size is inherited and may be set in style (§10.10, §6.4): every ancestor's is
                // applied in turn, not the element's own attribute alone.
                run.Size = FontSizeOf(run.Owner);
                run.X = cx; run.Y = cy;
                run.Face = FaceOf(run.Owner, out string first, out string preferred, out string unmatched);
                // Text WinAnsi cannot carry — Hebrew, Greek, Cyrillic, CJK — is set in an embedded font
                // when the options offer one, never written as '?'. The
                // family asked for is the first named, or the standard family the run falls to. Text a
                // standard face could carry, in a named family no standard face stands for, is set in
                // that family when the provider supplies it — the provider alone: a standard
                // face suffices, so the host's fonts are not searched for it.
                if (NeedsEmbeddedFont(run.Text))
                    ShapeRun(run, ctx, first == null || IsGenericFamily(first) ? run.Face.Split('-')[0] : first, ctx.Options.AllowSystemFontSubstitution);
                else if (preferred != null)   // never an empty run: PlaceCharacters places none
                    ShapeRun(run, ctx, preferred, host: false);
                if (unmatched != null && run.Embedded == null)
                    ReportOnce($"font-family '{unmatched}' names none of the standard faces this converter sets (the twelve non-symbolic ones); the text was set in Helvetica");
                // unicode-bidi: bidi-override (or isolate-override) draws the run's characters in the
                // element's direction whatever their class — a right-to-left run reversed. An embedded
                // run is composed under the override (ShapeRun); a standard face's text, never
                // reordered otherwise, is put in visual order here.
                run.Drawn = run.Text.Length == 0 ? "" : run.Embedded != null ? run.Text
                    : ToWinAnsi(IsDirectionallyOverridden(run.Owner)
                        ? Core.Text.UnicodeBidi.ReorderOverriddenWithMap(run.Text, IsRightToLeft(run.Owner), out _)
                        : run.Text);
                // letter-spacing (§10.11, inherited) is added after each character, as Tc is. It was
                // not read, so spaced lettering came out tight and was placed by its unspaced width.
                string spacing = GetPresentation(run.Owner, "letter-spacing");
                run.Spacing = spacing == null || spacing == "normal" ? 0f : ParseSpacing("letter-spacing", spacing, run.Owner);
                // word-spacing likewise, at each space: Tw, which a simple font adds at byte 32 alone.
                string wordSpacing = GetPresentation(run.Owner, "word-spacing");
                run.WordSpacing = wordSpacing == null || wordSpacing == "normal" ? 0f : ParseSpacing("word-spacing", wordSpacing, run.Owner);
                if (run.Embedded != null)
                {
                    // Tc is added after each glyph's two-byte code. Tw applies to the single byte 32
                    // (§9.3.3), which a composite font's codes never are, so the word spacing is a TJ
                    // adjustment after each space glyph instead (EmitRun), and is measured here as the
                    // simple-font path measures its Tw.
                    run.Width = (float)run.Shaped.WidthAt(run.Size) + run.Spacing * (run.Shaped.GlyphIds.Length - run.Shaped.MarkCount)
                        + run.WordSpacing * SpaceGlyphs(run.Shaped);
                }
                else
                    run.Width = RunWidth(run.Drawn, run.Size, run.Face) + run.Spacing * run.Drawn.Length
                        + run.WordSpacing * run.Drawn.Count(ch => ch == ' ');
                cx += run.Width;
            }

            // text-anchor (§10.9.1, inherited): a chunk's x is its start, middle or end. It was not
            // read, so a centred label began at its centre. The runs are measured as drawn, and the
            // shift is along the baseline in user space, before the mapping, so it follows any transform.
            for (int start = 0; start < runs.Count;)
            {
                int end = start + 1;
                while (end < runs.Count && !runs[end].StartsChunk) end++;
                float chunk = 0;
                for (int i = start; i < end; i++) chunk += runs[i].Width;
                // direction (§10.7.4, inherited): in a right-to-left chunk the runs advance leftwards
                // from the chunk's position — the first run's end is at it — and text-anchor's start
                // is that end, so start and end swap with the direction (§10.9.1's example). It was
                // not read, so a right-to-left label ran rightwards from its position and
                // its start anchor was its left end.
                bool rtl = IsRightToLeft(runs[start].Owner);
                if (rtl)
                {
                    float right = runs[start].X;
                    for (int i = start; i < end; i++)
                    {
                        // dx is along the x axis, whatever the direction; the first run's is in its X.
                        runs[i].X = right - runs[i].Width + (i == start ? 0 : runs[i].Dx);
                        right = runs[i].X;
                    }
                }
                string anchor = GetPresentation(runs[start].Owner, "text-anchor");
                float shift = anchor == "middle" ? chunk / 2 : anchor == "end" ? chunk : 0;
                if (rtl) shift = -shift;
                for (int i = start; i < end; i++) runs[i].X -= shift;
                start = end;
            }
            PlaceOnPaths(runs, ctx);

            // A run's fill alpha is its own fill's and fill-opacity's, times the element's opacity
            // (§14.5). ProcessNode set the element's; a run that differs sets its own, and /ca is
            // absolute, so it replaces the element's inside the run's q/Q, so a tspan's own fill-opacity
            // applies.
            var (elementFill, elementStroke) = Alphas(element, ctx);
            float elementOpacity = OwnOpacity(element, ctx);
            // A gradient on the text or a tspan is laid over the text element's box: the runs' advance
            // across, and each face's ascent and descent about its baseline. It was not applied, and
            // the text was painted black. The box is ProcessNode's target, which makes the patterns
            // and sets a clip or a mask measured in it.
            PaintTarget box = ctx.Paint;
            foreach (TextRun run in runs)
            {
                if (run.Hidden) continue;
                double ascender, descender;
                if (run.Embedded != null)
                {
                    var program = run.Embedded.Program;
                    ascender = program.Ascent * 1000.0 / program.UnitsPerEm;
                    descender = program.Descent * 1000.0 / program.UnitsPerEm;
                }
                else
                    Core.Fonts.Standard14Metrics.TryGetVerticalMetrics(run.Face, out ascender, out descender, out _, out _);
                box.Track(run.X, run.Y - (float)(ascender / 1000 * run.Size));
                box.Track(run.X + run.Width, run.Y - (float)(descender / 1000 * run.Size));
            }
            box.Fixed = true;
            foreach (TextRun run in runs)
            {
                // A fill that paints nothing draws no text, though the run still takes its width. It
                // was drawn anyway, in whatever colour the graphics state held.
                // In a clipPath's mask the text is geometry: its fill, none included, does not count.
                if (run.Hidden || run.Drawn.Length == 0 || !ctx.ClipFill && PaintsNothing(GetPresentation(run.Owner, "fill"))) continue;
                float fill = ctx.ClipFill ? 1f : PaintAlpha(GetPresentation(run.Owner, "fill")) * ReadOpacity(GetPresentation(run.Owner, "fill-opacity")) * elementOpacity;
                bool own = fill != elementFill;
                if (own)
                {
                    sb.AppendLine("q");
                    sb.AppendLine("/" + ctx.StateFor(fill, elementStroke) + " gs");
                }
                EmitRun(run, sb, ctx, xform);
                if (own) sb.AppendLine("Q");
            }
        }

        /// <summary>
        /// The runs of <paramref name="owner"/> in document order, white space handled as §10.15
        /// says: without <c>xml:space="preserve"</c>, newlines go, tabs become spaces, and runs of
        /// spaces collapse to one across run boundaries (leading and trailing are stripped by the
        /// caller); with it, a newline or tab is a space and nothing collapses.
        /// </summary>
        private static void CollectRuns(XmlElement owner, List<TextRun> runs, bool preserve, ref bool lastWasSpace,
            List<TextSpan> spans, ref int count, int depth)
        {
            if (depth > PdfLimits.MaxTreeDepth)
            {
                ReportOnce($"text nesting exceeded {PdfLimits.MaxTreeDepth} levels; the deeper text was not drawn");
                return;
            }
            // The element's characters, counted after white space is handled, for its position lists.
            var span = new TextSpan { Element = owner, Start = count };
            spans.Add(span);
            foreach (XmlNode child in owner.ChildNodes)
            {
                if (child is XmlElement nested)
                {
                    CollectRuns(nested, runs, preserve, ref lastWasSpace, spans, ref count, depth + 1);
                    continue;
                }
                if (!(child is XmlCharacterData chars) || child is XmlComment) continue;
                string text = WhiteSpace(chars.Value ?? "", preserve, ref lastWasSpace);
                runs.Add(new TextRun { Owner = owner, Text = text });
                count += text.Length;
            }
            span.End = count;
        }

        /// <summary>An element of a text's subtree and its characters, [<see cref="Start"/>, <see cref="End"/>) of the text's.</summary>
        private sealed class TextSpan
        {
            public XmlElement Element;
            public int Start, End;
        }

        /// <summary>
        /// The runs split where a character is placed (§10.4, §10.5): a character that a position list
        /// — <c>x</c>, <c>y</c>, <c>dx</c>, <c>dy</c> — gives a value starts a run of its own, with
        /// those values, and the characters after it follow on. <see cref="PositionValues"/> says which.
        /// </summary>
        /// <remarks>
        /// A list was read as its first value and the rest reported, so text laid out character by
        /// character was set as one run from its first position.
        /// </remarks>
        private static List<TextRun> PlaceCharacters(List<TextRun> runs, List<TextSpan> spans, ConvertContext ctx)
        {
            int total = 0;
            foreach (TextRun run in runs) total += run.Text.Length;
            float?[] x = PositionValues(spans, "x", total, ctx), y = PositionValues(spans, "y", total, ctx);
            float?[] dx = PositionValues(spans, "dx", total, ctx), dy = PositionValues(spans, "dy", total, ctx);
            float[] angles = Rotations(spans, runs, total);

            var placed = new List<TextRun>();
            int index = 0;
            foreach (TextRun run in runs)
            {
                // A textPath's characters are each a run, each placed and turned on the path.
                XmlElement onPath = null;
                for (XmlNode n = run.Owner; n is XmlElement e && e != spans[0].Element; n = n.ParentNode)
                    if (e.LocalName == "textPath") { onPath = e; break; }
                int from = 0;
                for (int i = 1; i <= run.Text.Length; i++)
                {
                    int next = index + i;
                    // A turned character is a run of its own, since it turns about its own origin.
                    if (i < run.Text.Length && !x[next].HasValue && !y[next].HasValue && !dx[next].HasValue && !dy[next].HasValue
                        && angles[next] == 0 && angles[next - 1] == 0 && onPath == null)
                        continue;
                    int at = index + from;
                    placed.Add(new TextRun
                    {
                        Owner = run.Owner,
                        Text = run.Text.Substring(from, i - from),
                        AbsoluteX = x[at],
                        AbsoluteY = y[at],
                        Dx = dx[at] ?? 0,
                        Dy = dy[at] ?? 0,
                        Rotate = angles[at],
                        OnPath = onPath,
                    });
                    from = i;
                }
                index += run.Text.Length;
            }
            return placed;
        }

        /// <summary>
        /// Sets each textPath's characters along its path (§10.13): a character's midpoint on the path at
        /// <c>startOffset</c> (a length, or a percentage of the path's) plus the advance before it, less
        /// the whole text's advance, or half, for a <c>text-anchor</c> of <c>end</c> or <c>middle</c>;
        /// its origin half its width back along the path's direction there, and turned to it, on top of
        /// any <c>rotate</c>. A character whose midpoint is off the path's ends is not drawn.
        /// </summary>
        /// <remarks>
        /// Not drawn flat, as if the textPath were a tspan. Text after a textPath in the same
        /// element continues from where the path's text ended (§10.13.2): its chunk, laid out flat,
        /// is moved so that it starts at the last drawn character's end — on the path, where none was
        /// drawn — unless its own <c>x</c> or <c>y</c> places it.
        /// </remarks>
        private static void PlaceOnPaths(List<TextRun> runs, ConvertContext ctx)
        {
            for (int i = 0; i < runs.Count;)
            {
                XmlElement textPath = runs[i].OnPath;
                if (textPath == null)
                {
                    i++;
                    continue;
                }
                int end = i;
                while (end < runs.Count && runs[end].OnPath == textPath) end++;

                var segments = TextPathSegments(textPath, ctx);
                float length = 0;
                if (segments != null) foreach (var s in segments) length += s.Length;
                string offset = GetAttr(textPath, "startOffset")?.Trim();
                float along = string.IsNullOrEmpty(offset) ? 0
                    : offset.EndsWith("%", StringComparison.Ordinal) ? ParseF(offset.Substring(0, offset.Length - 1)) / 100 * length
                    : ParseLength("startOffset", offset, 0f, owner: textPath);
                string anchor = GetPresentation(textPath, "text-anchor");
                float perpendicular = 0;
                for (int k = i; k < end; k++)
                {
                    TextRun run = runs[k];
                    // A character's own position (§10.13.2): x is a new offset along the path, dx a
                    // shift along it, dy one across it, to the glyph's down; y is not used. Each x
                    // starts a chunk, anchored on its own offset over its characters and their shifts.
                    // The lists were laid out flat and then overridden by the path, so none applied.
                    if (run.AbsoluteX.HasValue) along = run.AbsoluteX.Value;
                    if (k == i || run.AbsoluteX.HasValue)
                    {
                        float chunk = 0;
                        for (int c = k; c < end && (c == k || !runs[c].AbsoluteX.HasValue); c++) chunk += runs[c].Width + (c == k ? 0 : runs[c].Dx);
                        along -= anchor == "middle" ? chunk / 2 : anchor == "end" ? chunk : 0;
                    }
                    along += run.Dx;
                    perpendicular += run.Dy;
                    float mid = along + run.Width / 2;
                    along += run.Width;
                    if (segments == null || !PointAlong(segments, mid, out float px, out float py, out double angle))
                    {
                        run.Hidden = true;
                        continue;
                    }
                    float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
                    run.X = px - run.Width / 2 * cos - perpendicular * sin;
                    run.Y = py - run.Width / 2 * sin + perpendicular * cos;
                    run.Rotate += (float)(angle * 180 / Math.PI);
                }
                if (segments != null) ContinueAfterPath(runs, i, end, segments, along);
                i = end;
            }
        }

        /// <summary>
        /// Moves the flat text after a textPath — its runs from <paramref name="end"/> to the next
        /// chunk — so that it starts where the path's text ended: the last drawn character's end, or
        /// the point <paramref name="along"/> the path where none was drawn, the path's own end past
        /// it and its start before it. A run's own <c>x</c> or <c>y</c> keeps that coordinate.
        /// </summary>
        private static void ContinueAfterPath(List<TextRun> runs, int first, int end, List<(float X0, float Y0, float X1, float Y1, float Length)> segments, float along)
        {
            if (end >= runs.Count || runs[end].OnPath != null) return;
            float endX, endY;
            TextRun last = null;
            for (int k = end - 1; k >= first; k--)
                if (!runs[k].Hidden) { last = runs[k]; break; }
            if (last != null)
            {
                double angle = last.Rotate * Math.PI / 180;
                endX = last.X + last.Width * (float)Math.Cos(angle);
                endY = last.Y + last.Width * (float)Math.Sin(angle);
            }
            else if (!PointAlong(segments, along, out endX, out endY, out _))
            {
                var edge = along < 0 ? segments[0] : segments[segments.Count - 1];
                (endX, endY) = along < 0 ? (edge.X0, edge.Y0) : (edge.X1, edge.Y1);
            }
            float dx = runs[end].AbsoluteX.HasValue ? 0 : endX - runs[end].X;
            float dy = runs[end].AbsoluteY.HasValue ? 0 : endY - runs[end].Y;
            for (int k = end; k < runs.Count && (k == end || !runs[k].StartsChunk); k++)
            {
                runs[k].X += dx;
                runs[k].Y += dy;
            }
        }

        /// <summary>
        /// A textPath's path as straight segments in the text's user space, its own <c>transform</c>
        /// applied — flattened, never drawn (<see cref="PathState.Flat"/>) — or <c>null</c>, reported,
        /// when it names no path.
        /// </summary>
        private static List<(float X0, float Y0, float X1, float Y1, float Length)> TextPathSegments(XmlElement textPath, ConvertContext ctx)
        {
            string href = textPath.GetAttribute("href");
            if (href.Length == 0) href = textPath.GetAttribute("href", XlinkNamespace);
            href = href.Trim();
            if (!href.StartsWith("#", StringComparison.Ordinal)
                || !ctx.ElementsById.TryGetValue(href.Substring(1), out XmlElement path) || path.LocalName != "path")
            {
                ReportOnce("a textPath names no path in this document; its text was not drawn");
                return null;
            }
            var state = new PathState(new StringBuilder(), ctx, Transform.Identity) { Flat = new List<List<(float X, float Y)>>() };
            TracePath(path.GetAttribute("d"), state);
            Transform place = ApplyTransformAttr(path, Transform.Identity);
            var segments = new List<(float, float, float, float, float)>();
            foreach (var line in state.Flat)
                for (int j = 1; j < line.Count; j++)
                {
                    var (x0, y0) = place.Apply(line[j - 1].X, line[j - 1].Y);
                    var (x1, y1) = place.Apply(line[j].X, line[j].Y);
                    float length = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                    if (length > 0) segments.Add((x0, y0, x1, y1, length));
                }
            return segments;
        }

        /// <summary>The point <paramref name="distance"/> along the segments, and their direction there in radians; false off either end.</summary>
        private static bool PointAlong(List<(float X0, float Y0, float X1, float Y1, float Length)> segments, float distance,
            out float x, out float y, out double angle)
        {
            x = y = 0; angle = 0;
            if (distance < 0) return false;
            foreach (var s in segments)
            {
                if (distance <= s.Length)
                {
                    float t = distance / s.Length;
                    x = s.X0 + t * (s.X1 - s.X0);
                    y = s.Y0 + t * (s.Y1 - s.Y0);
                    angle = Math.Atan2(s.Y1 - s.Y0, s.X1 - s.X0);
                    return true;
                }
                distance -= s.Length;
            }
            return false;
        }

        /// <summary>
        /// Each character's <c>rotate</c> (§10.5), in degrees: the i-th value of the nearest list,
        /// its own element's or an ancestor's, counted from that element's first character, and past
        /// its end the list's last value. A list with its last value reaches every character of its
        /// element, so the nearest one decides: each element's is found once, in document order, and
        /// each character read once — the work is the characters', not characters times depth.
        /// </summary>
        private static float[] Rotations(List<TextSpan> spans, List<TextRun> runs, int total)
        {
            var nearest = new Dictionary<XmlElement, (TextSpan Span, float[] List)>();
            foreach (TextSpan span in spans)
            {
                string value = GetAttr(span.Element, "rotate");
                float[] list = value?.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(ParseF).ToArray();
                if (list != null && list.Length > 0) nearest[span.Element] = (span, list);
                else if (span.Element.ParentNode is XmlElement parent && nearest.TryGetValue(parent, out var inherited))
                    nearest[span.Element] = inherited;
            }
            var angles = new float[total];
            int index = 0;
            foreach (TextRun run in runs)
            {
                if (nearest.TryGetValue(run.Owner, out var r))
                    for (int i = 0; i < run.Text.Length && index + i < total; i++)
                        angles[index + i] = r.List[Math.Min(index + i - r.Span.Start, r.List.Length - 1)];
                index += run.Text.Length;
            }
            return angles;
        }

        /// <summary>
        /// Each character's value of the position list <paramref name="name"/>, or <c>null</c>: an
        /// element's i-th value is its i-th character's, its descendants' characters counted, and a
        /// descendant's list replaces its ancestors' for the characters it covers. A character past
        /// every list that reaches it has none. The spans are in document order, ancestors first, so
        /// each list is written once, over its own characters: the work is the lists' length.
        /// </summary>
        private static float?[] PositionValues(List<TextSpan> spans, string name, int total, ConvertContext ctx)
        {
            var values = new float?[total];
            foreach (TextSpan span in spans)
            {
                string value = GetAttr(span.Element, name);
                if (value == null) continue;
                string[] parts = value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int end = Math.Min(span.End, total);
                for (int i = 0; i < parts.Length && span.Start + i < end; i++)
                    values[span.Start + i] = ParseLength(name, parts[i], 0f, ctx, span.Element);
            }
            return values;
        }

        /// <summary>§10.15's white space handling of one run; see <see cref="CollectRuns"/>.</summary>
        private static string WhiteSpace(string s, bool preserve, ref bool lastWasSpace)
        {
            var b = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (preserve) { b.Append(ch == '\n' || ch == '\r' || ch == '\t' ? ' ' : ch); continue; }
                if (ch == '\n' || ch == '\r') continue;
                char c = ch == '\t' ? ' ' : ch;
                if (c == ' ')
                {
                    if (lastWasSpace) continue;
                    lastWasSpace = true;
                }
                else lastWasSpace = false;
                b.Append(c);
            }
            return b.ToString();
        }

        /// <summary>One run, set in its owner's fill and size at its place.</summary>
        private static void EmitRun(TextRun run, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            XmlNode node = run.Owner;
            float fs = run.Size, x = run.X, y = run.Y;
            string drawn = run.Drawn;
            if (run.Rotate != 0)
            {
                // rotate (§10.5) turns the glyph about its own origin, (x, y), which stays put.
                double angle = run.Rotate * Math.PI / 180;
                float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
                xform = Transform.Multiply(xform, new Transform(1, 0, 0, 1, x, y));
                xform = Transform.Multiply(xform, new Transform(cos, sin, -sin, cos, 0, 0));
                xform = Transform.Multiply(xform, new Transform(1, 0, 0, 1, -x, -y));
            }
            if (ctx.Tracking) MapXY(x + run.Width, y - fs, ctx, xform);   // the run's far corner, a size above its baseline

            (float px, float py) = MapXY(x, y, ctx, xform);

            SetFillColor(node, sb, ctx);
            // A gradient whose stops fade: the run is painted through its soft mask.
            string mask = ctx.Paint?.FillMask;
            if (ctx.Paint != null) ctx.Paint.FillMask = null;
            if (mask != null) { sb.AppendLine("q"); sb.AppendLine("/" + mask + " gs"); }
            sb.AppendLine("BT");
            sb.AppendLine("/" + (run.Embedded?.Name ?? ctx.FontResource(run.Face)) + " " + F(fs) + " Tf");
            // The glyphs go through MapXY's mapping — the transform, the viewBox scale, the flip —
            // with text space's y as user space's −y, since SVG's glyphs stand up against its y-down
            // axis. Only the anchor was mapped, so under a viewBox or a transform the shapes scaled
            // and turned and the text kept its size and stayed upright. The identity keeps Td.
            float flip = ctx.Options.FlipY ? -1f : 1f;
            float a = ctx.ScaleX * xform.A, b = flip * ctx.ScaleY * xform.B;
            float c = -ctx.ScaleX * xform.C, d = -flip * ctx.ScaleY * xform.D;
            if (a == 1 && b == 0 && c == 0 && d == 1)
                sb.AppendLine(F($"{px} {py} Td"));
            else
                sb.AppendLine(F($"{a} {b} {c} {d} {px} {py} Tm"));
            // Tc and Tw are in text space, which the mapping above takes to the page as it takes user
            // space. They are graphics state, which ET does not reset, so the run sets them back.
            if (run.Spacing != 0) sb.AppendLine(F($"{run.Spacing} Tc"));
            // Tw is a simple font's: it applies to byte 32 alone (§9.3.3), never to a two-byte code.
            bool tw = run.WordSpacing != 0 && run.Embedded == null;
            if (tw) sb.AppendLine(F($"{run.WordSpacing} Tw"));
            if (run.Embedded != null)
            {
                // Glyph ids under Identity-H, in the visual order the composer put them in; each one
                // drawn joins the font's /W and /ToUnicode. The word spacing is a TJ adjustment after
                // each space glyph (§9.4.3: thousandths of text space, subtracted, so negative moves
                // the next glyph on), which is what Tw does at a simple font's byte 32; the run was
                // written unspaced.
                // The font's mark attachment rides along (GlyphRunShowText).
                run.Shaped.RecordUsedGlyphs(run.Embedded.GidToUnicode, run.Embedded.GidToText);
                double[] after = null;
                if (run.WordSpacing != 0 && SpaceGlyphs(run.Shaped) > 0)
                {
                    double adjustment = -run.WordSpacing / fs * PdfUnits.GlyphSpaceUnitsPerEm;
                    after = new double[run.Shaped.GlyphIds.Length];
                    for (int g = 0; g < after.Length; g++)
                        if (run.Shaped.Glyphs[g].CodePoint == ' ') after[g] = adjustment;
                }
                sb.AppendLine(Core.Text.GlyphRunShowText.Operators(run.Shaped.GlyphIds, run.Shaped.Placements, fs, 0, after, run.Spacing));
            }
            else
                sb.AppendLine("(" + EscapePdfString(drawn) + ") Tj");
            if (run.Spacing != 0) sb.AppendLine("0 Tc");
            if (tw) sb.AppendLine("0 Tw");
            sb.AppendLine("ET");
            if (mask != null) sb.AppendLine("Q");
        }

        // WinAnsiEncoding differs from Latin-1 only over 0x80–0x9F, which is where the punctuation
        // a real document uses actually lives — curly quotes, the en/em dash, the euro. The
        // content stream is written as iso-8859-1 (a byte-for-byte pass-through of chars 0–255),
        // so mapping to the WinAnsi CODE here and declaring /WinAnsiEncoding on the font is what
        // gets those bytes to the right glyphs.
        private static readonly Dictionary<char, char> WinAnsiHighRange = new Dictionary<char, char>
        {
            ['€'] = (char)0x80, ['‚'] = (char)0x82, ['ƒ'] = (char)0x83,
            ['„'] = (char)0x84, ['…'] = (char)0x85, ['†'] = (char)0x86,
            ['‡'] = (char)0x87, ['ˆ'] = (char)0x88, ['‰'] = (char)0x89,
            ['Š'] = (char)0x8A, ['‹'] = (char)0x8B, ['Œ'] = (char)0x8C,
            ['Ž'] = (char)0x8E, ['‘'] = (char)0x91, ['’'] = (char)0x92,
            ['“'] = (char)0x93, ['”'] = (char)0x94, ['•'] = (char)0x95,
            ['–'] = (char)0x96, ['—'] = (char)0x97, ['˜'] = (char)0x98,
            ['™'] = (char)0x99, ['š'] = (char)0x9A, ['›'] = (char)0x9B,
            ['œ'] = (char)0x9C, ['ž'] = (char)0x9E, ['Ÿ'] = (char)0x9F,
        };

        /// <summary><see cref="WinAnsiHighRange"/> backwards: the character each of those codes draws.</summary>
        private static readonly Dictionary<char, char> WinAnsiHighRangeCharacter =
            WinAnsiHighRange.ToDictionary(pair => pair.Value, pair => pair.Key);

        /// <summary>
        /// The advance of <paramref name="drawn"/>, text as <see cref="ToWinAnsi"/> wrote it, at
        /// <paramref name="size"/>: the AFM widths of <paramref name="face"/>, the face it is set in.
        /// A code from 0x80 to 0x9F is measured as the mark it draws, not as Latin-1.
        /// </summary>
        private static float RunWidth(string drawn, float size, string face)
        {
            if (!Core.Fonts.Standard14Metrics.TryGetWidthTable(face, out var widths, out double missing))
                return 0;
            double total = 0;
            foreach (char code in drawn)
            {
                char ch = WinAnsiHighRangeCharacter.TryGetValue(code, out char mark) ? mark : code;
                total += widths.TryGetValue(ch, out double width) ? width : missing;
            }
            return (float)(total / 1000 * size);
        }

        /// <summary>
        /// Maps text onto the single-byte codes of <c>/WinAnsiEncoding</c>, <b>reporting</b> every
        /// character neither that nor Latin-1 can carry.
        ///
        /// <para><b>Why the report is the important half.</b> The content stream was encoded with
        /// <c>iso-8859-1</c>, whose encoder fallback substitutes <c>?</c> for every code point above
        /// U+00FF — so Hebrew, Greek, Cyrillic and CJK reached the file as question marks and the
        /// conversion reported success. At the viewer that is indistinguishable from a font
        /// substitution problem, which is the wrong thing for the caller to go and investigate.
        /// The only fonts offered are standard faces in WinAnsi, so a genuine fix is out of scope here;
        /// saying so is not.</para>
        /// </summary>
        private static string ToWinAnsi(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            var sb = new StringBuilder(s.Length);
            string unsupported = null;
            foreach (char c in s)
            {
                if (c <= 0xFF) { sb.Append(c); continue; }
                if (WinAnsiHighRange.TryGetValue(c, out char mapped)) { sb.Append(mapped); continue; }
                sb.Append('?');
                if (unsupported == null || unsupported.IndexOf(c) < 0) unsupported += c;
            }

            if (unsupported != null)
                Report($"text contains {unsupported.Length} character(s) WinAnsiEncoding cannot " +
                       $"carry ('{unsupported}'); each was written as '?' — a font that covers them is embedded " +
                       "when SvgConversionOptions.FontProvider supplies one or AllowSystemFontSubstitution is set");

            return sb.ToString();
        }

        /// <summary>
        /// How many of a shaped run's glyphs are the space, U+0020 — the character <c>word-spacing</c>
        /// widens (§10.11), and the one a simple font's <c>Tw</c> widens as byte 32, so the two paths
        /// space the same characters.
        /// </summary>
        private static int SpaceGlyphs(Core.Text.ShapedGlyphRun shaped)
        {
            int spaces = 0;
            foreach (var glyph in shaped.Glyphs) if (glyph.CodePoint == ' ') spaces++;
            return spaces;
        }

        /// <summary>
        /// True when <see cref="ToWinAnsi"/> would write a <c>?</c> for some character of <paramref name="s"/>:
        /// one above U+00FF that is not one of the marks WinAnsi has at 0x80–0x9F.
        /// </summary>
        private static bool NeedsEmbeddedFont(string s)
        {
            foreach (char c in s)
                if (c > 0xFF && !WinAnsiHighRange.ContainsKey(c)) return true;
            return false;
        }

        /// <summary>
        /// Sets <paramref name="run"/> in an embedded font for <paramref name="family"/> when one covering
        /// its text can be had (<see cref="ConvertContext.EmbeddedFontFor"/>) — from the options' provider,
        /// and from the host's fonts when <paramref name="host"/> allows — shaped through
        /// <see cref="Core.Text.TextRunComposer"/>: presentation forms and bidi order chosen here, as a
        /// producer does, in visual order. A run no font covers keeps its standard face, and
        /// <see cref="ToWinAnsi"/> writes and reports it.
        /// </summary>
        private static void ShapeRun(TextRun run, ConvertContext ctx, string family, bool host)
        {
            if (ctx.Options.FontProvider == null && !host) return;
            WeightAndStyle(run.Owner, out bool bold, out bool italic);
            var required = Core.Text.TextRunComposer.CodePointsToCover(run.Text);
            EmbeddedFont font = ctx.EmbeddedFontFor(family, bold, italic, required, host, out string failure);
            if (font == null)
            {
                // For text a standard face carries the face is the answer, and the caller reports the
                // family; for text outside WinAnsi the '?' needs the resolver's reason.
                if (failure != null && NeedsEmbeddedFont(run.Text))
                    ReportOnce($"no font covers the text in font-family '{family}' ({failure}); it was written as '?'");
                return;
            }
            // The base direction is the element's direction (§10.7.4), ltr unless set: a run beginning
            // with a Hebrew word in a left-to-right text keeps the word at its left, as a browser does.
            var shaped = Core.Text.TextRunComposer.Compose(run.Text, font.Program, new Core.Text.TextRunOptions
            {
                BaseDirection = IsRightToLeft(run.Owner) ? Core.Text.BaseDirection.RightToLeft : Core.Text.BaseDirection.LeftToRight,
                DirectionalOverride = IsDirectionallyOverridden(run.Owner),
            });
            if (!shaped.IsComplete)
            {
                // The font was chosen for the shaped code points, so this is a glyph the cmap maps to 0.
                ReportOnce($"the font embedded for font-family '{family}' has no glyph for U+{shaped.MissingCodePoint.Value:X4}; the text was written as '?'");
                return;
            }
            run.Embedded = font;
            run.Shaped = shaped;
        }

        // ── Coordinate mapping ────────────────────────────────────────────────

        private static (float px, float py) MapXY(float svgX, float svgY, ConvertContext ctx, Transform xform, bool track = true)
        {
            float x = (xform.A * svgX + xform.C * svgY + xform.E) * ctx.ScaleX + ctx.OffsetX;
            float y = (xform.B * svgX + xform.D * svgY + xform.F) * ctx.ScaleY + ctx.OffsetY;
            if (ctx.Options.FlipY)
                y = ctx.Options.PageHeight - y;
            if (!track) return (x, y);
            if (ctx.Tracking) ctx.Track(x, y);
            ctx.Paint?.Track(svgX, svgY);   // the shape's own points, in its user space
            return (x, y);
        }

        /// <summary>
        /// Hands <paramref name="track"/> the points of a cubic Bézier where either coordinate turns —
        /// the roots in (0, 1) of its derivative, at most two per axis. With the end points, which the
        /// caller tracks, these are the curve's exact bounding box.
        /// </summary>
        internal static void TrackCubicExtrema(float x0, float y0, float x1, float y1, float x2, float y2,
                                               float x3, float y3, Action<float, float> track)
        {
            void Axis(double p0, double p1, double p2, double p3)
            {
                // B'(t)/3 = a·t² + b·t + c.
                double a = -p0 + 3 * p1 - 3 * p2 + p3, b = 2 * (p0 - 2 * p1 + p2), c = p1 - p0;
                if (Math.Abs(a) < 1e-12)
                {
                    if (Math.Abs(b) > 1e-12) At(-c / b);
                    return;
                }
                double disc = b * b - 4 * a * c;
                if (disc < 0) return;
                double root = Math.Sqrt(disc);
                At((-b + root) / (2 * a));
                At((-b - root) / (2 * a));
            }

            void At(double t)
            {
                if (!(t > 0 && t < 1)) return;
                double u = 1 - t, b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
                track((float)(b0 * x0 + b1 * x1 + b2 * x2 + b3 * x3), (float)(b0 * y0 + b1 * y1 + b2 * y2 + b3 * y3));
            }

            Axis(x0, x1, x2, x3);
            Axis(y0, y1, y2, y3);
        }

        /// <summary>
        /// The drawing's mapping from the root's user space to the page, as a matrix: the scale, the
        /// offset and the flip <see cref="MapXY"/> applies after an element's transform.
        /// </summary>
        private static Transform PageMatrix(ConvertContext ctx)
            => ctx.Options.FlipY
                ? new Transform(ctx.ScaleX, 0, 0, -ctx.ScaleY, ctx.OffsetX, ctx.Options.PageHeight - ctx.OffsetY)
                : new Transform(ctx.ScaleX, 0, 0, ctx.ScaleY, ctx.OffsetX, ctx.OffsetY);

        // ── Color helpers ─────────────────────────────────────────────────────

        private static void SetColors(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            if (ctx.ClipMode) return;   // a clipPath's shapes are paths, not paint
            SetFillColor(node, sb, ctx);
            SetStrokeColor(node, sb, ctx, xform);
            if (ctx.Paint != null) ctx.Paint.PathStart = sb.Length;   // the path follows
        }

        /// <summary>
        /// A paint that paints nothing: <c>none</c>, or a colour with an alpha of 0 — the
        /// <c>transparent</c> keyword, <c>rgba(…, 0)</c> — so <c>fill="transparent"</c> never fills black.
        /// <c>null</c>, an unset
        /// paint, is not.
        /// </summary>
        private static bool PaintsNothing(string paint)
            => paint != null && (paint == "none" || Core.Text.CssColor.IsFullyTransparent(paint));

        private static void SetFillColor(XmlNode node, StringBuilder sb, ConvertContext ctx)
        {
            // A clipPath's mask is luminance: its shapes are white, whatever their fill.
            if (ctx.ClipFill) { sb.AppendLine("1 1 1 rg"); return; }
            string fill = GetPresentation(node, "fill") ?? "black";
            if (PaintsNothing(fill)) return;
            if (IsPaintServer(fill) && SetPaintServer(fill, sb, ctx, stroking: false, out fill)) return;
            var (r, g, b) = ParseColor(fill);
            sb.AppendLine(F($"{r} {g} {b} rg"));
        }

        /// <summary>
        /// The stroke colour and width. The width is in user units (§11.4, initial value 1) and the
        /// content is written in page units, so it is scaled by the square root of the transform's
        /// area scale — exact under a uniform scale or a rotation, the nearest single width under a
        /// non-uniform one — not PDF's default width of 1.
        /// </summary>
        private static void SetStrokeColor(XmlNode node, StringBuilder sb, ConvertContext ctx, Transform xform)
        {
            if (ctx.InClipPath) return;   // a clipPath's shapes are geometry: a line sets its stroke here, not in SetColors
            string stroke = GetPresentation(node, "stroke");
            if (stroke == null || PaintsNothing(stroke)) return;
            if (!IsPaintServer(stroke) || !SetPaintServer(stroke, sb, ctx, stroking: true, out stroke))
            {
                var (r, g, b) = ParseColor(stroke);
                sb.AppendLine(F($"{r} {g} {b} RG"));
            }

            string widthValue = GetPresentation(node, "stroke-width");
            float width = widthValue == null ? 1f : ParseLength("stroke-width", widthValue, 1f, ctx, node);
            double areaScale = Math.Abs((xform.A * xform.D - xform.B * xform.C) * ctx.ScaleX * ctx.ScaleY);
            float scale = (float)Math.Sqrt(areaScale);
            sb.AppendLine(F($"{width * scale} w"));
            SetStrokeStyle(node, sb, scale, ctx);
        }

        /// <summary>
        /// A stroke's cap, join, miter limit and dash pattern (§11.4), written only when set: the
        /// initial values are PDF's defaults too. None of them was read, so every stroke had butt
        /// caps, miter joins and no dashes. Dash lengths scale to page units as the width does.
        /// </summary>
        private static void SetStrokeStyle(XmlNode node, StringBuilder sb, float scale, ConvertContext ctx)
        {
            switch (GetPresentation(node, "stroke-linecap"))
            {
                case "butt":   sb.AppendLine("0 J"); break;
                case "round":  sb.AppendLine("1 J"); break;
                case "square": sb.AppendLine("2 J"); break;
            }
            switch (GetPresentation(node, "stroke-linejoin"))
            {
                case "miter": sb.AppendLine("0 j"); break;
                case "round": sb.AppendLine("1 j"); break;
                case "bevel": sb.AppendLine("2 j"); break;
            }

            string miter = GetPresentation(node, "stroke-miterlimit");
            if (miter != null)
            {
                float limit = ParseF(miter.Trim());
                if (limit >= 1) sb.AppendLine(F($"{limit} M"));   // below 1 is an error (§11.4): ignored
            }

            string dashes = GetPresentation(node, "stroke-dasharray");
            if (dashes == null) return;
            var lengths = DashLengths(dashes, ctx, node);
            // `none` and a list with a non-length in it are not a list; a negative length is an
            // error, and a list of zeros is a solid line (§11.4). None of them dashes.
            if (lengths == null || lengths.Count == 0 || lengths.Any(v => v < 0) || lengths.All(v => v == 0)) return;
            if (lengths.Count % 2 == 1) lengths.AddRange(lengths.ToArray());   // an odd list repeats

            string offsetValue = GetPresentation(node, "stroke-dashoffset");
            float offset = offsetValue == null ? 0f : ParseLength("stroke-dashoffset", offsetValue, 0f, ctx, node);
            sb.AppendLine("[" + string.Join(" ", lengths.Select(v => F(v * scale))) + "] " + F(offset * scale) + " d");
        }

        /// <summary>
        /// A dash array's lengths (§11.4), separated by white space or commas: each a length in any
        /// CSS unit, or a percentage of the viewport's normalised diagonal. <c>null</c> for
        /// <c>none</c>, and for a list with anything not a number in it, which is not a list —
        /// reported.
        /// </summary>
        /// <remarks>
        /// The list was read as bare numbers, so a unit or a percentage ended it and the stroke drew
        /// solid without a word.
        /// </remarks>
        private static List<float> DashLengths(string value, ConvertContext ctx, XmlNode owner = null)
        {
            if (value.Trim() == "none") return null;
            var lengths = new List<float>();
            foreach (string part in value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = 0;
                if (!TryScanNumber(part, ref i, out _))
                {
                    ReportOnce($"'stroke-dasharray=\"{value}\"' is not a list of lengths; the stroke is solid");
                    return null;
                }
                lengths.Add(ParseLength("stroke-dasharray", part, 0f, ctx, owner));
            }
            return lengths;
        }

        /// <summary>
        /// A presentation property as it applies to <paramref name="node"/>: the element's own
        /// <c>style</c> declaration, then its attribute, then its parent's, up the tree (§6.1, §6.4).
        /// <c>inherit</c> defers to the parent. <c>null</c> when nothing sets it.
        /// </summary>
        /// <remarks>
        /// For the inherited properties this converter paints with — <c>fill</c>, <c>stroke</c>,
        /// <c>stroke-width</c>, <c>fill-rule</c>. They were read from the element's own attribute
        /// only, so <c>&lt;g fill="red"&gt;</c> drew its children black and a <c>style</c>
        /// attribute, which editors write for every shape, was not read at all.
        /// </remarks>
        private static string GetPresentation(XmlNode node, string name)
        {
            for (XmlNode n = node; n is XmlElement el; n = n.ParentNode)
            {
                string value = OwnPresentation(el, name);
                if (value != null && !value.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return value;
            }
            return null;
        }

        /// <summary>
        /// <paramref name="name"/> as <paramref name="element"/> itself sets it — its <c>style</c>
        /// declaration, else its attribute — or <c>null</c>. No inheritance.
        /// </summary>
        private static string OwnPresentation(XmlElement element, string name)
        {
            string value = StyleDeclaration(element.GetAttribute("style"), name);
            if (value != null) return value;
            string attr = element.GetAttribute(name);
            return attr.Length > 0 ? attr.Trim() : null;
        }

        /// <summary>
        /// <c>none</c>, the value that draws as the property's absence. <c>inherit</c> too — the
        /// ancestor that sets the value is reported where it sets it.
        /// </summary>
        private static bool AppliesNothing(string value)
            => value.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// An opacity in [0, 1]: a number or a percentage (CSS Color 4); 1 when absent or not a
        /// number, which is its initial value.
        /// </summary>
        private static float ReadOpacity(string value)
        {
            if (value == null) return 1f;
            string v = value.Trim();
            bool percent = v.EndsWith("%", StringComparison.Ordinal);
            if (percent) v = v.Substring(0, v.Length - 1);
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float opacity) || float.IsNaN(opacity))
                return 1f;
            if (percent) opacity /= 100;
            return opacity < 0 ? 0 : opacity > 1 ? 1 : opacity;
        }

        /// <summary>
        /// An element's own <c>opacity</c>: not inherited, and every container's (<see cref="AsGroup"/>)
        /// is its transparency group's, applied once around its content (<see cref="EmitTransparencyGroup"/>),
        /// not in each element.
        /// </summary>
        /// <remarks>
        /// Not multiplied with any ancestor's: every container's applies once, as its group. A
        /// filtered element's is 1 while
        /// it draws: its opacity applies to the filter's result (<see cref="Filtered"/>).
        /// </remarks>
        private static float OwnOpacity(XmlNode node, ConvertContext ctx) => node == ctx.Filtered ? 1f : ReadOpacity(OwnPresentation((XmlElement)node, "opacity"));

        /// <summary>A paint's alpha channel; 1 for an unset paint or one that is not a colour.</summary>
        private static float PaintAlpha(string paint)
            => paint != null && Core.Text.CssColor.TryParse(paint, out _, out _, out _, out double alpha)
                ? (float)Core.Text.CssColor.Clamp01(alpha) : 1f;

        /// <summary>
        /// The fill and stroke alphas an element paints with (§14.5): each paint's colour alpha
        /// times its <c>fill-opacity</c> or <c>stroke-opacity</c> (inherited), times the
        /// element's own <c>opacity</c> (<see cref="OwnOpacity"/>).
        /// <c>opacity</c> is not inherited; a group's is a transparency group of its own.
        /// </summary>
        /// <param name="node">The element whose paints are read.</param>
        /// <param name="ctx">The conversion; inside a clipPath there is no opacity.</param>
        /// <param name="withoutOwnOpacity">
        /// True when the element's own <c>opacity</c> is its transparency group's (a shape that fills
        /// and strokes, in <see cref="ProcessNode"/>), applied once around it and so left out here.
        /// </param>
        private static (float Fill, float Stroke) Alphas(XmlNode node, ConvertContext ctx, bool withoutOwnOpacity = false)
        {
            if (ctx.InClipPath) return (1f, 1f);   // a clip has no opacity
            float opacity = withoutOwnOpacity ? 1f : OwnOpacity(node, ctx);
            float fill = PaintAlpha(GetPresentation(node, "fill")) * ReadOpacity(GetPresentation(node, "fill-opacity"));
            float stroke = PaintAlpha(GetPresentation(node, "stroke")) * ReadOpacity(GetPresentation(node, "stroke-opacity"));
            return (fill * opacity, stroke * opacity);
        }

        /// <summary>
        /// Whether the element paints both a fill and a stroke, as <see cref="EmitPaintOperator"/> judges
        /// them — the fill black unless set, the stroke none unless set, a paint server that paints
        /// nothing counted as none, and a <c>line</c> never filled — which is when its <c>opacity</c>
        /// is a transparency group's rather than a constant alpha's.
        /// </summary>
        private static bool FillsAndStrokes(XmlNode node, ConvertContext ctx)
        {
            if (node.LocalName == "line") return false;
            string fill = GetPresentation(node, "fill"), stroke = GetPresentation(node, "stroke");
            return !PaintsNothing(fill) && !ServerPaintsNothing(fill, ctx)
                && stroke != null && !PaintsNothing(stroke) && !ServerPaintsNothing(stroke, ctx);
        }

        /// <summary>The value of <paramref name="name"/> in a <c>style</c> attribute's declarations, or <c>null</c>.</summary>
        private static string StyleDeclaration(string style, string name)
        {
            if (string.IsNullOrEmpty(style)) return null;
            string found = null;
            foreach (string declaration in style.Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                if (declaration.Substring(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    found = declaration.Substring(colon + 1).Trim();   // the last declaration wins
            }
            return string.IsNullOrEmpty(found) ? null : found;
        }

        /// <summary>
        /// Appends the one path-painting operator the element's <c>fill</c>/<c>stroke</c> ask for.
        ///
        /// <para><b>Every path in this converter goes through here</b>, which is the point:
        /// an element that SVG renders as nothing is not stroked in whatever <c>RG</c> colour the
        /// previous element left behind. <c>n</c> — the no-op painter,
        /// §8.5.3.1 — is what "paint neither" means; the predecessor's <c>else</c> branch reached
        /// <c>f</c> and filled in black instead.</para>
        /// </summary>
        /// <param name="fillApplies">
        /// False for shapes SVG never fills (<c>&lt;line&gt;</c>), so a stray <c>fill</c> attribute
        /// cannot turn one into a filled path.
        /// </param>
        /// <param name="node">The SVG element whose <c>fill</c> and <c>stroke</c> are read.</param>
        /// <param name="sb">Receives the emitted PDF painting operator.</param>
        /// <param name="ctx">The conversion; inside a clipPath nothing is painted, only the path kept.</param>
        private static void EmitPaintOperator(XmlNode node, StringBuilder sb, ConvertContext ctx, bool fillApplies)
        {
            if (ctx.ClipMode) return;
            if (ctx.ClipFill)
            {
                // Geometry alone (§14.3.5): filled white under the shape's clip-rule, its fill and
                // stroke — none included — not read; a line has no area and adds nothing.
                sb.AppendLine(!fillApplies ? "n" : GetPresentation(node, "clip-rule") == "evenodd" ? "f*" : "f");
                return;
            }
            string fill   = GetPresentation(node, "fill");
            string stroke = GetPresentation(node, "stroke");
            // SVG initial values: fill is black (so an absent `fill` still fills), stroke is none.
            bool hasFill   = fillApplies && !PaintsNothing(fill) && !ServerPaintsNothing(fill, ctx);
            bool hasStroke = stroke != null && !PaintsNothing(stroke) && !ServerPaintsNothing(stroke, ctx);
            // §11.3: fill-rule evenodd is PDF's even-odd painters (§8.5.3.3).
            bool evenOdd   = string.Equals(GetPresentation(node, "fill-rule"), "evenodd", StringComparison.Ordinal);

            // A gradient whose stops fade paints through a soft mask that must reach its own paint
            // alone: the path is taken back and painted once per paint, the masked one inside q/Q.
            string fillMask = hasFill ? ctx.Paint?.FillMask : null, strokeMask = hasStroke ? ctx.Paint?.StrokeMask : null;
            if (fillMask != null || strokeMask != null)
            {
                string path = sb.ToString(ctx.Paint.PathStart, sb.Length - ctx.Paint.PathStart);
                sb.Length = ctx.Paint.PathStart;
                void Paint(string mask, string op)
                {
                    if (mask != null) { sb.AppendLine("q"); sb.AppendLine("/" + mask + " gs"); }
                    sb.Append(path);
                    sb.AppendLine(op);
                    if (mask != null) sb.AppendLine("Q");
                }
                if (hasFill) Paint(fillMask, evenOdd ? "f*" : "f");
                if (hasStroke) Paint(strokeMask, "S");
                return;
            }

            if (hasFill && hasStroke) sb.AppendLine(evenOdd ? "B*" : "B");
            else if (hasStroke)       sb.AppendLine("S");
            else if (hasFill)         sb.AppendLine(evenOdd ? "f*" : "f");
            else                      sb.AppendLine("n");
        }

        /// <summary>
        /// A paint colour as RGB in [0, 1], through <see cref="Core.Text.CssColor"/>: hex in its
        /// four lengths, <c>rgb()</c>/<c>rgba()</c> with numbers or percentages,
        /// <c>hsl()</c>/<c>hsla()</c>, and the CSS named colours, which SVG 1.1 §4.4 takes from CSS.
        /// </summary>
        /// <remarks>
        /// <para><b>This knew sixteen names</b>, no percentages and no <c>hsl()</c>, and drew anything
        /// else black. For a name it did not know it said nothing, so <c>fill="steelblue"</c> came
        /// out black with a clean <see cref="LastError"/>. HtmlToPdf's parser had all of them, and
        /// is now the one both read.</para>
        /// <para>The alpha is not this method's: <see cref="Alphas"/> reads it into the shape's
        /// <c>/ExtGState</c>. A value that is not a colour is reported and drawn black. It is never
        /// an exception: <c>fill='#gggggg'</c> once escaped one from <c>System.Convert.ToByte</c>.</para>
        /// </remarks>
        private static (float r, float g, float b) ParseColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color) || color == "none")
                return (0, 0, 0);

            if (Core.Text.CssColor.TryParse(color, out double r, out double g, out double b, out _))
                return ((float)Core.Text.CssColor.Clamp01(r),
                        (float)Core.Text.CssColor.Clamp01(g),
                        (float)Core.Text.CssColor.Clamp01(b));

            Report($"colour '{color.Trim()}' is not a colour this converter reads; black was used");
            return (0, 0, 0);
        }

        // ── Paint servers ─────────────────────────────────────────────────────

        /// <summary>A paint that names a paint server: <c>url(#id)</c>, with or without a fallback colour after it.</summary>
        private static bool IsPaintServer(string paint) => paint != null && paint.TrimStart().StartsWith("url(", StringComparison.Ordinal);

        /// <summary>
        /// The <c>linearGradient</c>, <c>radialGradient</c> or <c>pattern</c> a paint names, or
        /// <c>null</c>; and the fallback colour written after the reference (§11.2:
        /// <c>url(#g) green</c>), or <c>null</c>.
        /// </summary>
        private static XmlElement ResolvePaintServer(string paint, ConvertContext ctx, out string fallback)
        {
            fallback = null;
            string v = paint.Trim();
            int close = v.IndexOf(')');
            if (close < 0) return null;
            string rest = v.Substring(close + 1).Trim();
            if (rest.Length > 0) fallback = rest;
            string reference = v.Substring(4, close - 4).Trim().Trim('\'', '"');
            return reference.StartsWith("#", StringComparison.Ordinal)
                && ctx.ElementsById.TryGetValue(reference.Substring(1), out XmlElement e)
                && e.LocalName is "linearGradient" or "radialGradient" or "pattern" ? e : null;
        }

        /// <summary>
        /// Sets a paint that names a gradient (§13.2) or a pattern (§13.3): its PDF pattern, for a
        /// shape, through the pattern colour space; for a gradient of one stop, nothing, and
        /// <paramref name="colour"/> is the stop's colour. Returns whether the paint was set — or needs
        /// none, for a gradient with no stops or a pattern with no tile or content, which paint nothing
        /// (<see cref="ServerPaintsNothing"/>). Otherwise <paramref name="colour"/> is the colour to paint:
        /// the fallback after an unresolved reference, or black, reported.
        /// </summary>
        private static bool SetPaintServer(string paint, StringBuilder sb, ConvertContext ctx, bool stroking, out string colour)
        {
            XmlElement server = ResolvePaintServer(paint, ctx, out string fallback);
            if (server == null)
            {
                colour = fallback ?? "black";
                if (fallback == null) ReportOnce($"'{paint.Trim()}' names no gradient or pattern in this document; black was used");
                return false;
            }
            if (server.LocalName == "pattern")
            {
                colour = "black";
                if (PatternPaintsNothing(server, ctx)) return true;
                string tiling = ctx.ReservePattern(server, null);
                sb.AppendLine(stroking ? "/Pattern CS" : "/Pattern cs");
                sb.AppendLine("/" + tiling + (stroking ? " SCN" : " scn"));
                return true;
            }
            XmlElement gradient = server;
            var stops = Stops(GradientChain(gradient, ctx));
            bool opaque = stops.All(s => s.Opacity >= 1);
            colour = stops.Count == 1 ? stops[0].Colour : "black";
            if (stops.Count == 0) return true;
            if (stops.Count == 1 && opaque) return false;
            // Stops that are not all opaque paint through a soft mask of their opacities (§13.2.4):
            // the fill's or the stroke's alone (EmitPaintOperator, EmitRun). They were drawn opaque.
            string mask = opaque ? null : ctx.ReserveMaskState();
            if (stroking) ctx.Paint.StrokeMask = mask; else ctx.Paint.FillMask = mask;
            string name = ctx.ReservePattern(gradient, mask);
            sb.AppendLine(stroking ? "/Pattern CS" : "/Pattern cs");
            sb.AppendLine("/" + name + (stroking ? " SCN" : " scn"));
            return true;
        }

        /// <summary>A paint naming a gradient with no stops (§13.2.4), or a pattern with no tile or no content (§13.3), which paints nothing.</summary>
        private static bool ServerPaintsNothing(string paint, ConvertContext ctx)
        {
            if (!IsPaintServer(paint)) return false;
            XmlElement server = ResolvePaintServer(paint, ctx, out _);
            if (server == null) return false;
            return server.LocalName == "pattern" ? PatternPaintsNothing(server, ctx) : Stops(GradientChain(server, ctx)).Count == 0;
        }

        /// <summary>
        /// A pattern whose tile has no width or height, or whose chain has no content, paints nothing
        /// (§13.3.2: a zero width or height disables rendering).
        /// </summary>
        private static bool PatternPaintsNothing(XmlElement pattern, ConvertContext ctx)
        {
            var chain = PatternChain(pattern, ctx);
            bool box = ChainAttr(chain, "patternUnits") != "userSpaceOnUse";
            return !(UnitsLength("width", ChainAttr(chain, "width"), box, ctx.ViewportWidth, "0") > 0)
                || !(UnitsLength("height", ChainAttr(chain, "height"), box, ctx.ViewportHeight, "0") > 0)
                || PatternContent(chain) == null;
        }

        /// <summary>The first pattern in the chain with element children: whose children are the content (§13.3.1), or <c>null</c>.</summary>
        private static XmlElement PatternContent(List<XmlElement> chain)
            => chain.FirstOrDefault(p => p.ChildNodes.OfType<XmlElement>().Any());

        /// <summary>
        /// A paint server and the templates it names by <c>href</c>, nearest first, each once: a cycle
        /// ends the chain, and so does <see cref="PdfLimits.MaxTreeDepth"/> links, and so does a
        /// reference to an element that is not one of <paramref name="localNames"/>.
        /// </summary>
        private static List<XmlElement> ServerChain(XmlElement server, ConvertContext ctx, params string[] localNames)
        {
            var chain = new List<XmlElement>();
            var seen = new HashSet<XmlElement>();
            for (XmlElement g = server; g != null && seen.Add(g) && chain.Count <= PdfLimits.MaxTreeDepth;)
            {
                chain.Add(g);
                string href = g.GetAttribute("href");
                if (href.Length == 0) href = g.GetAttribute("href", XlinkNamespace);
                href = href.Trim();
                g = href.StartsWith("#", StringComparison.Ordinal)
                    && ctx.ElementsById.TryGetValue(href.Substring(1), out XmlElement next)
                    && Array.IndexOf(localNames, next.LocalName) >= 0 ? next : null;
            }
            return chain;
        }

        /// <summary>A gradient's chain: templates of either gradient kind (§13.2.2).</summary>
        private static List<XmlElement> GradientChain(XmlElement gradient, ConvertContext ctx)
            => ServerChain(gradient, ctx, "linearGradient", "radialGradient");

        /// <summary>A pattern's chain: pattern templates (§13.3.1).</summary>
        private static List<XmlElement> PatternChain(XmlElement pattern, ConvertContext ctx)
            => ServerChain(pattern, ctx, "pattern");

        /// <summary>An attribute of the first gradient in the chain that sets it (§13.2.2: a template's, if the gradient's own is absent).</summary>
        private static string ChainAttr(List<XmlElement> chain, string name)
        {
            foreach (XmlElement g in chain)
                if (g.HasAttribute(name)) return g.GetAttribute(name).Trim();
            return null;
        }

        /// <summary>
        /// The stops of the first gradient in the chain that has any (§13.2.4): each offset a number or a
        /// percentage, clamped to [0, 1] and to no less than the one before; each opacity its
        /// <c>stop-opacity</c> times its colour's alpha.
        /// </summary>
        private static List<(float Offset, string Colour, float Opacity)> Stops(List<XmlElement> chain)
        {
            var stops = new List<(float, string, float)>();
            foreach (XmlElement g in chain)
            {
                float last = 0;
                foreach (XmlElement stop in g.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "stop"))
                {
                    string value = stop.GetAttribute("offset").Trim();
                    float offset = value.EndsWith("%", StringComparison.Ordinal) ? ParseF(value.Substring(0, value.Length - 1)) / 100 : ParseF(value);
                    offset = Math.Max(last, Math.Min(1f, Math.Max(0f, offset)));
                    last = offset;
                    string colour = OwnPresentation(stop, "stop-color") ?? "black";
                    // A stop's opacity is its stop-opacity times its colour's own alpha.
                    stops.Add((offset, colour, ReadOpacity(OwnPresentation(stop, "stop-opacity")) * PaintAlpha(colour)));
                }
                if (stops.Count > 0) break;
            }
            if (stops.Count == 0) ReportOnce("a gradient with no stops paints nothing");
            return stops;
        }

        /// <summary>
        /// The stops as a PDF function of t in [0, 1] to DeviceRGB (ISO 32000-1 §7.10): one exponential
        /// (type 2) piece between each two stops, stitched (type 3) when there are more; before the
        /// first stop its colour holds, after the last the last's, and two stops at one offset are a
        /// hard edge.
        /// </summary>
        private static string StopFunction(List<(float Offset, float[] Values)> stops)
        {
            var points = new List<(float T, float[] C)>();
            if (stops[0].Offset > 0) points.Add((0f, stops[0].Values));
            points.AddRange(stops);
            if (stops[stops.Count - 1].Offset < 1) points.Add((1f, stops[stops.Count - 1].Values));

            var pieces = new List<string>();
            var bounds = new List<string>();
            string Components(float[] c) => string.Join(" ", c.Select(v => F($"{v}")));
            for (int i = 0; i + 1 < points.Count; i++)
            {
                if (points[i + 1].T <= points[i].T) continue;   // no width: the edge between two colours
                if (pieces.Count > 0) bounds.Add(F($"{points[i].T}"));
                pieces.Add($"<< /FunctionType 2 /Domain [0 1] /C0 [{Components(points[i].C)}] /C1 [{Components(points[i + 1].C)}] /N 1 >>");
            }
            if (pieces.Count == 1) return pieces[0];
            string encode = string.Join(" ", Enumerable.Repeat("0 1", pieces.Count));
            return $"<< /FunctionType 3 /Domain [0 1] /Functions [{string.Join(" ", pieces)}] /Bounds [{string.Join(" ", bounds)}] /Encode [{encode}] >>";
        }

        /// <summary>
        /// A coordinate in a gradient's or a mask region's units: a number, or a percentage — of 1 in
        /// bounding-box units, of <paramref name="extent"/> (the viewport's) in user space.
        /// </summary>
        private static float UnitsLength(string name, string value, bool boxUnits, float extent, string fallback)
        {
            string v = (value ?? fallback).Trim();
            if (v.EndsWith("%", StringComparison.Ordinal))
                return ParseF(v.Substring(0, v.Length - 1)) / 100 * (boxUnits ? 1 : extent);
            return ParseLength(name, v, 0f);
        }

        /// <summary>
        /// Makes the patterns a shape asked for, now its bounds are known: each an axial or radial
        /// shading of its gradient's stops (ISO 32000-1 §8.7.3, §8.7.4.5.3–4), extended at both ends,
        /// whose matrix takes the gradient's space — through <c>gradientTransform</c>, the bounding box
        /// in <c>objectBoundingBox</c> units, and the shape's user space — to the page.
        /// </summary>
        /// <remarks>
        /// <c>spreadMethod</c> <c>reflect</c> and <c>repeat</c> are drawn as <c>pad</c>, reported. A
        /// shape with no width or height has no box to map a bounding-box gradient onto, which is reported.
        /// </remarks>
        private static void FinishPatterns(PaintTarget target, ConvertContext ctx, Transform local, int depth)
        {
            foreach (var (name, gradient, mask) in target.Pending)
            {
                if (gradient.LocalName == "pattern")
                {
                    FinishTilingPattern(name, gradient, target, ctx, local, depth);
                    continue;
                }
                var chain = GradientChain(gradient, ctx);
                bool box = ChainAttr(chain, "gradientUnits") != "userSpaceOnUse";
                // spreadMethod (§13.2.2); any other value is pad, the initial one. Both were drawn as pad.
                string spread = ChainAttr(chain, "spreadMethod");
                bool repeat = spread == "repeat", reflect = spread == "reflect";

                var units = Transform.Identity;
                if (box)
                {
                    float w = target.MaxX - target.MinX, h = target.MaxY - target.MinY;
                    if (!target.HasBounds || w <= 0 || h <= 0)
                        ReportOnce("a gradient in objectBoundingBox units on a shape with no width or height is drawn over a unit box");
                    else
                        units = new Transform(w, 0, 0, h, target.MinX, target.MinY);
                }
                var gradientToUser = Transform.Multiply(units, ApplyTransformList(ChainAttr(chain, "gradientTransform"), Transform.Identity));
                var matrix = Transform.Multiply(PageMatrix(ctx), Transform.Multiply(local, gradientToUser));

                // The shape's corners in the gradient's space, for repeat and reflect to cover it.
                (float X, float Y)[] corners = null;
                if ((repeat || reflect) && target.HasBounds && gradientToUser.TryInvert(out Transform userToGradient))
                    corners = new[]
                    {
                        userToGradient.Apply(target.MinX, target.MinY), userToGradient.Apply(target.MaxX, target.MinY),
                        userToGradient.Apply(target.MaxX, target.MaxY), userToGradient.Apply(target.MinX, target.MaxY),
                    };
                int first = 0, cycles = 1;
                void Cover(double low, double high)
                {
                    first = SaturatingCast.ToInt(Math.Max(int.MinValue / 2, Math.Floor(low)));
                    double span = Math.Ceiling(high) - first;
                    cycles = span < 1 ? 1 : span > PdfLimits.MaxSvgGradientCycles ? PdfLimits.MaxSvgGradientCycles : SaturatingCast.ToInt(span);
                    if (span > PdfLimits.MaxSvgGradientCycles)
                        ReportOnce($"a gradient's spreadMethod=\"{spread}\" asks for more than {PdfLimits.MaxSvgGradientCycles} cycles over its shape; past them it is padded");
                }

                float vw = ctx.ViewportWidth, vh = ctx.ViewportHeight;
                string shading;
                if (gradient.LocalName == "linearGradient")
                {
                    float x1 = UnitsLength("x1", ChainAttr(chain, "x1"), box, vw, "0%"), y1 = UnitsLength("y1", ChainAttr(chain, "y1"), box, vh, "0%");
                    float x2 = UnitsLength("x2", ChainAttr(chain, "x2"), box, vw, "100%"), y2 = UnitsLength("y2", ChainAttr(chain, "y2"), box, vh, "0%");
                    double dx = x2 - x1, dy = y2 - y1, length2 = dx * dx + dy * dy;
                    if (corners != null && length2 > 0)
                    {
                        // t, the fraction along the gradient's vector, at each corner.
                        double[] t = corners.Select(p => ((p.X - x1) * dx + (p.Y - y1) * dy) / length2).ToArray();
                        Cover(t.Min(), t.Max());
                        (x1, y1, x2, y2) = ((float)(x1 + first * dx), (float)(y1 + first * dy),
                            (float)(x1 + (first + cycles) * dx), (float)(y1 + (first + cycles) * dy));
                    }
                    shading = F($"/ShadingType 2 /Coords [{x1} {y1} {x2} {y2}]");
                }
                else
                {
                    float diagonal = NormalisedDiagonal(vw, vh);
                    float cx = UnitsLength("cx", ChainAttr(chain, "cx"), box, vw, "50%"), cy = UnitsLength("cy", ChainAttr(chain, "cy"), box, vh, "50%");
                    float r = UnitsLength("r", ChainAttr(chain, "r"), box, diagonal, "50%");
                    float fx = ChainAttr(chain, "fx") == null ? cx : UnitsLength("fx", ChainAttr(chain, "fx"), box, vw, "50%");
                    float fy = ChainAttr(chain, "fy") == null ? cy : UnitsLength("fy", ChainAttr(chain, "fy"), box, vh, "50%");
                    // §13.2.3: a focal point outside the circle is moved onto it.
                    double d = Math.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    if (d > r && d > 0) { fx = (float)(cx + (fx - cx) * r / d); fy = (float)(cy + (fy - cy) * r / d); }
                    if (corners != null && r > 0)
                    {
                        // s, the circle a corner lies on: centre f + s·(c − f), radius s·r, so
                        // |q − s·e|² = s²r² for q = p − f, e = c − f. With f inside, a = e·e − r² < 0.
                        double ex = cx - fx, ey = cy - fy, a = ex * ex + ey * ey - (double)r * r;
                        double[] s = corners.Select(p =>
                        {
                            double qx = p.X - fx, qy = p.Y - fy, qe = qx * ex + qy * ey, qq = qx * qx + qy * qy;
                            if (Math.Abs(a) < 1e-9 * r * r) return qe > 0 ? qq / (2 * qe) : 0;   // f on the circle
                            return a < 0 ? (qe - Math.Sqrt(qe * qe - a * qq)) / a : 0;
                        }).ToArray();
                        Cover(0, s.Max());
                        (cx, cy, r) = ((float)(fx + cycles * ex), (float)(fy + cycles * ey), r * cycles);
                    }
                    shading = F($"/ShadingType 3 /Coords [{fx} {fy} 0 {cx} {cy} {r}]");
                }

                var stops = Stops(chain);
                string placed = F($" /Matrix [{matrix.A} {matrix.B} {matrix.C} {matrix.D} {matrix.E} {matrix.F}]");
                // One copy of the stops per cycle, run backwards on odd cycles for reflect; the copy is
                // written once and named by each.
                string Spread(string single)
                {
                    if (cycles == 1 && !(reflect && (first & 1) != 0)) return single;
                    int shared = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.Plain(single));
                    var functions = string.Join(" ", Enumerable.Repeat(shared + " 0 R", cycles));
                    var bounds = string.Join(" ", Enumerable.Range(1, cycles - 1).Select(k => F($"{(float)k / cycles}")));
                    var encode = string.Join(" ", Enumerable.Range(0, cycles).Select(k => reflect && ((first + k) & 1) != 0 ? "1 0" : "0 1"));
                    return $"<< /FunctionType 3 /Domain [0 1] /Functions [{functions}] /Bounds [{bounds}] /Encode [{encode}] >>";
                }
                void AddPattern(string pattern, string space, List<(float, float[])> values)
                {
                    int number = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.Plain(
                        $"<< /Type /Pattern /PatternType 2 /Shading << {shading} /ColorSpace /{space} /Function {Spread(StopFunction(values))} /Extend [true true] >>{placed} >>"));
                    ctx.PatternEntries.Append(ctx.PatternEntries.Length == 0 ? "" : " ").Append('/').Append(pattern).Append(' ').Append(number).Append(" 0 R");
                }
                AddPattern(name, "DeviceRGB", stops.Select(s =>
                {
                    var (r, g, b) = ParseColor(s.Colour);
                    return (s.Offset, new[] { r, g, b });
                }).ToList());

                if (mask == null) continue;
                // The stops' opacities: the same shading in grey, filling the page inside a luminosity
                // group, whose luminance over black is the alpha the fill or stroke is painted through.
                string grey = ctx.NewPatternName();
                AddPattern(grey, "DeviceGray", stops.Select(s => (s.Offset, new[] { s.Opacity })).ToList());
                int form = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.StreamObject(
                    $"/Type /XObject /Subtype /Form /BBox {MediaBox(ctx.Options)} /Group << /S /Transparency /CS /DeviceGray >> /Resources {ctx.SharedResources()} 0 R",
                    Core.Text.Latin1Text.Encode("/Pattern cs\n/" + grey + " scn\n" + F($"0 0 {ctx.Options.PageWidth} {ctx.Options.PageHeight} re") + "\nf\n")));
                ctx.WriteMaskState(mask, form);
            }
        }

        /// <summary>
        /// A <c>pattern</c> paint (§13.3) as a tiling pattern (ISO 32000-1 §8.7.3): the tile — x, y,
        /// width and height in <c>patternUnits</c>, the shape's box by default — is the cell; its
        /// content is the pattern's children (a template's, by <c>href</c>, when it has none), drawn
        /// in <c>patternContentUnits</c>, user space by default, or fitted into the tile by the
        /// pattern's <c>viewBox</c>; the pattern matrix takes the tile through <c>patternTransform</c>
        /// and the shape's user space to the page. The cell's content is written in page units, as
        /// every content here is, so the matrix takes the page mapping off again first.
        /// </summary>
        /// <remarks>
        /// A pattern is painted as its tile, not reported as naming no gradient. A tile with
        /// no width or height, and a pattern with no content, paint nothing before this is reached
        /// (<see cref="ServerPaintsNothing"/>); a pattern in box units on a shape with no box, and one
        /// whose content paints with the pattern itself, are an empty cell, reported. The pattern's
        /// children are a drawing of their own: not part of the shape's box, a container's union or a
        /// link's rectangle. A pattern's <c>viewBox</c> does not make a viewport for percentages in
        /// its content, which are of the enclosing one.
        /// </remarks>
        private static void FinishTilingPattern(string name, XmlElement pattern, PaintTarget target, ConvertContext ctx, Transform local, int depth)
        {
            var chain = PatternChain(pattern, ctx);
            bool box = ChainAttr(chain, "patternUnits") != "userSpaceOnUse";
            bool contentBox = ChainAttr(chain, "patternContentUnits") == "objectBoundingBox";
            float vw = ctx.ViewportWidth, vh = ctx.ViewportHeight;
            float bw = target.MaxX - target.MinX, bh = target.MaxY - target.MinY;
            bool hasBox = target.HasBounds && bw > 0 && bh > 0;
            float tx = UnitsLength("x", ChainAttr(chain, "x"), box, vw, "0"), ty = UnitsLength("y", ChainAttr(chain, "y"), box, vh, "0");
            float tw = UnitsLength("width", ChainAttr(chain, "width"), box, vw, "0"), th = UnitsLength("height", ChainAttr(chain, "height"), box, vh, "0");
            if (box)
            {
                if (hasBox) { tx = target.MinX + tx * bw; ty = target.MinY + ty * bh; tw *= bw; th *= bh; }
                else { ReportOnce("a pattern in objectBoundingBox units on a shape with no width or height paints nothing"); tw = th = 0; }
            }
            if (contentBox && !hasBox)
            {
                ReportOnce("a pattern's content in objectBoundingBox units on a shape with no width or height paints nothing");
                tw = th = 0;
            }

            var content = new StringBuilder();
            if (tw > 0 && th > 0 && PatternContent(chain) is XmlElement source)
            {
                if (!ctx.PatternsBeingDrawn.Add(pattern))
                    ReportOnce("a pattern's content paints with the pattern itself; there it paints nothing");
                else
                {
                    // The content's space to the tile's: the viewBox fitted into the tile, or the box's scale.
                    XmlElement viewBoxHolder = chain.FirstOrDefault(p => p.HasAttribute("viewBox"));
                    Transform contentToTile = viewBoxHolder != null ? ViewportTransform(viewBoxHolder, 0, 0, F(tw), F(th), ctx)
                        : contentBox ? new Transform(bw, 0, 0, bh, 0, 0) : Transform.Identity;
                    PaintTarget outerPaint = ctx.Paint;
                    var outerCollectors = new List<(Transform, PaintTarget, bool, bool)>(ctx.Collectors);
                    bool outerTracking = ctx.Tracking;
                    ctx.Collectors.Clear();
                    ctx.Tracking = false;
                    try
                    {
                        foreach (XmlNode child in source.ChildNodes)
                            ProcessNode(child, content, ctx, contentToTile, depth + 1);
                    }
                    finally
                    {
                        ctx.Paint = outerPaint;
                        ctx.Collectors.Clear();
                        ctx.Collectors.AddRange(outerCollectors);
                        ctx.Tracking = outerTracking;
                        ctx.PatternsBeingDrawn.Remove(pattern);
                    }
                }
            }
            if (!(tw > 0) || !(th > 0)) { tw = th = 1; content.Clear(); }

            // Pattern space is the cell's content in page units: the matrix takes the page mapping
            // off, then the tile through patternTransform and the shape's user space onto the page.
            Transform page = PageMatrix(ctx);
            Transform tileToUser = Transform.Multiply(ApplyTransformList(ChainAttr(chain, "patternTransform"), Transform.Identity), Transform.Translate(tx, ty));
            page.TryInvert(out Transform unpage);   // a scale, an offset and a flip: always invertible
            Transform matrix = Transform.Multiply(page, Transform.Multiply(local, Transform.Multiply(tileToUser, unpage)));
            var corners = new[] { page.Apply(0, 0), page.Apply(tw, 0), page.Apply(tw, th), page.Apply(0, th) };
            float x0 = corners.Min(c => c.X), x1 = corners.Max(c => c.X), y0 = corners.Min(c => c.Y), y1 = corners.Max(c => c.Y);
            string dictionary = "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1"
                + " /BBox [" + F(x0) + " " + F(y0) + " " + F(x1) + " " + F(y1) + "]"
                + " /XStep " + F(x1 - x0) + " /YStep " + F(y1 - y0)
                + " /Resources " + ctx.SharedResources().ToString(CultureInfo.InvariantCulture) + " 0 R"
                + " /Matrix [" + F(matrix.A) + " " + F(matrix.B) + " " + F(matrix.C) + " " + F(matrix.D) + " " + F(matrix.E) + " " + F(matrix.F) + "]";
            int number = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.StreamObject(dictionary, Core.Text.Latin1Text.Encode(content.ToString())));
            ctx.PatternEntries.Append(ctx.PatternEntries.Length == 0 ? "" : " ").Append('/').Append(name).Append(' ').Append(number).Append(" 0 R");
        }

        // ── Path tokeniser ────────────────────────────────────────────────────

        /// <summary>
        /// Path data as tokens: each command letter alone, each number whole.
        /// </summary>
        private static List<string> TokenisePath(string d)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
                if (TryScanNumber(d, ref i, out string number)) { tokens.Add(number); continue; }
                if (IsAsciiLetter(c)) { tokens.Add(c.ToString()); i++; continue; }
                i++; // not part of the path grammar
            }
            return tokens;
        }

        /// <summary>
        /// Reads one SVG number at <paramref name="i"/> and moves past it; <c>false</c>, with
        /// <paramref name="i"/> unmoved, when no number starts there.
        /// </summary>
        /// <remarks>
        /// <para>SVG 1.1 §8.3.9's grammar, which point lists and transform arguments share: an
        /// optional sign, digits with at most one decimal point and at least one digit, and an
        /// optional exponent whose <c>e</c> is followed by digits. So <c>1.5.5</c> is two numbers,
        /// <c>1.5</c> and <c>.5</c>; <c>10-5</c> is <c>10</c> and <c>-5</c>; and <c>1e-3</c> is one
        /// number.</para>
        /// <para><b>The tokenizer took every digit and point it met</b>, so <c>1.5.5</c>, which
        /// illustration tools write, was one token that did not parse and read as 0. An exponent
        /// split at its <c>e</c>, which then went through as a path command. Point lists and
        /// transform arguments split on separators only, so <c>10-5</c> was one unparseable part and
        /// was dropped. Digits are ASCII: <c>char.IsDigit</c> took any Unicode decimal digit into a
        /// token the number parse then rejected.</para>
        /// </remarks>
        private static bool TryScanNumber(string s, ref int i, out string token)
        {
            int p = i;
            if (p < s.Length && (s[p] == '+' || s[p] == '-')) p++;

            int intStart = p;
            while (p < s.Length && IsAsciiDigit(s[p])) p++;
            bool digits = p > intStart;

            if (p < s.Length && s[p] == '.')
            {
                int q = p + 1;
                while (q < s.Length && IsAsciiDigit(s[q])) q++;
                if (q > p + 1 || digits) { p = q; digits = true; }   // "1.5", ".5" and "1."
            }

            if (!digits) { token = null; return false; }

            if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
            {
                int q = p + 1;
                if (q < s.Length && (s[q] == '+' || s[q] == '-')) q++;
                int expStart = q;
                while (q < s.Length && IsAsciiDigit(s[q])) q++;
                if (q > expStart) p = q;   // an 'e' with no digits after it is not an exponent
            }

            token = s.Substring(i, p - i);
            i = p;
            return true;
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        /// <summary>Whether a path token is a number rather than a command letter.</summary>
        private static bool IsNum(string s)
            => !string.IsNullOrEmpty(s) && !IsAsciiLetter(s[0]);

        private static float ParseF(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        // ── Attribute helpers ─────────────────────────────────────────────────

        private static string GetAttr(XmlNode node, string name) =>
            (node as XmlElement)?.GetAttribute(name) is string v && v.Length > 0 ? v : null;

        /// <summary>
        /// A length attribute in user units, or <paramref name="def"/> when it is absent or
        /// unreadable.
        /// </summary>
        /// <remarks>
        /// <para>A user unit is a CSS pixel (SVG 1.1 §7.10), so an absolute unit converts through
        /// <see cref="Core.Text.CssLength.TryToPixels"/>, the unit table HtmlToPdf reads too:
        /// <c>10mm</c> is about 37.8, never 10 by trimming the unit's letters, and an unknown unit is
        /// reported.</para>
        /// <para>A percentage is of the nearest viewport (<see cref="ViewportPercent"/>) when
        /// <paramref name="ctx"/> is given: a geometry length. An <c>em</c> is the element's font size
        /// (<see cref="FontSizeOf"/>) and an <c>ex</c> half of it; a percentage without a viewport — a
        /// spacing, a marker's reference point — is read as its number and reported. A unit that is not a CSS unit, or a value that is not
        /// a number, takes the default and is reported.</para>
        /// </remarks>
        private static float TryParseAttrF(XmlNode node, string name, float def, ConvertContext ctx = null)
        {
            string v = GetAttr(node, name);
            return v == null ? def : ParseLength(name, v, def, ctx, node);
        }

        /// <summary>
        /// <see cref="TryParseAttrF"/>'s reading of one value, for a caller that found it elsewhere;
        /// <paramref name="owner"/> is the element whose font size an <c>em</c> or <c>ex</c> is of.
        /// </summary>
        private static float ParseLength(string name, string v, float def, ConvertContext ctx = null, XmlNode owner = null)
        {
            string text = v.Trim();
            int i = 0;
            if (!TryScanNumber(text, ref i, out string number))
            {
                Report($"'{name}=\"{v}\"' is not a length; the default was used");
                return def;
            }

            float n = ParseF(number);
            string unit = text.Substring(i).Trim();
            if (unit.Length == 0) return n;
            if (Core.Text.CssLength.TryToPixels(n, unit, out double pixels)) return (float)pixels;
            if (unit == "%" && ctx != null) return ViewportPercent(name, n, ctx);

            bool em = unit.Equals("em", StringComparison.OrdinalIgnoreCase), ex = unit.Equals("ex", StringComparison.OrdinalIgnoreCase);
            if ((em || ex) && owner != null)
            {
                // §10.10 / CSS Values 4 §6.1.1: an em is the element's font size; an ex, with no
                // x-height to read from a face this converter does not open, half an em.
                float size = FontSizeOf(owner);
                return em ? n * size : n * size / 2;
            }
            if (unit == "%" || em || ex)
            {
                Report($"'{name}=\"{v}\"' is relative to a font size or a viewport this converter " +
                       "does not model here; it was read as user units");
                return n;
            }

            Report($"'{name}=\"{v}\"' has a unit this converter does not know; the default was used");
            return def;
        }

        /// <summary>
        /// A <c>letter-spacing</c> or <c>word-spacing</c> value: a length as <see cref="ParseLength"/> reads
        /// one, or a percentage of the element's font size (<see cref="FontSizeOf"/>), as Chromium (Edge's
        /// print) renders both: at a 20px font, 10% spaces as 2px, 50% as 10px, a word-spacing of 100% as
        /// 20px. CSS Text 3 and SVG 1.1 admit no percentage here, so it is read
        /// as the browser reads it rather than as that many user units.
        /// </summary>
        private static float ParseSpacing(string name, string value, XmlNode owner)
        {
            string text = value.Trim();
            if (text.EndsWith("%", StringComparison.Ordinal))
            {
                int i = 0;
                string body = text.Substring(0, text.Length - 1).Trim();
                if (TryScanNumber(body, ref i, out string number) && i == body.Length)
                    return ParseF(number) / 100f * FontSizeOf(owner);
            }
            return ParseLength(name, value, 0f, owner: owner);
        }

        /// <summary>The font size where none is set: SVG's <c>medium</c>, read as 12 user units, as this converter always has.</summary>
        private const float DefaultFontSize = 12f;

        /// <summary>
        /// An element's font size in user units (§10.10): every <c>font-size</c> from the root down to
        /// it applied in turn — a length; a percentage, <c>em</c> or <c>ex</c> of the size so far, the
        /// parent's; <c>larger</c> and <c>smaller</c> a step of 1.2 from it; an absolute-size keyword
        /// from <c>medium</c> (<see cref="DefaultFontSize"/>) by steps of 1.2 — and 12 where none is set.
        /// </summary>
        /// <remarks>
        /// The tree is walked up once and folded from the root, not recursed, so a deep tree costs its
        /// depth in a list. Every ancestor's value is applied, a percentage or an em in it relative
        /// to the inherited size.
        /// </remarks>
        private static float FontSizeOf(XmlNode node)
        {
            var chain = new List<XmlElement>();
            for (XmlNode n = node; n is XmlElement e; n = n.ParentNode) chain.Add(e);
            float size = DefaultFontSize;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                string own = OwnPresentation(chain[i], "font-size");
                if (own != null) size = ResolveFontSize(own, size);
            }
            return size;
        }

        /// <summary>One <c>font-size</c> value against <paramref name="parent"/>, the size inherited so far.</summary>
        private static float ResolveFontSize(string value, float parent)
        {
            string v = value.Trim();
            switch (v.ToLowerInvariant())
            {
                case "xx-small": return DefaultFontSize / 1.728f;
                case "x-small": return DefaultFontSize / 1.44f;
                case "small": return DefaultFontSize / 1.2f;
                case "medium": return DefaultFontSize;
                case "large": return DefaultFontSize * 1.2f;
                case "x-large": return DefaultFontSize * 1.44f;
                case "xx-large": return DefaultFontSize * 1.728f;
                case "larger": return parent * 1.2f;
                case "smaller": return parent / 1.2f;
                case "inherit": return parent;
            }
            int i = 0;
            if (!TryScanNumber(v, ref i, out string number))
            {
                Report($"'font-size=\"{value}\"' is not a length; the inherited size was used");
                return parent;
            }
            float n = ParseF(number);
            string unit = v.Substring(i).Trim();
            if (unit.Length == 0) return n;
            if (Core.Text.CssLength.TryToPixels(n, unit, out double pixels)) return (float)pixels;
            switch (unit.ToLowerInvariant())
            {
                case "%": return parent * n / 100;
                case "em": return parent * n;
                case "ex": return parent * n / 2;
            }
            Report($"'font-size=\"{value}\"' has a unit this converter does not know; the inherited size was used");
            return parent;
        }

        /// <summary>
        /// <paramref name="percent"/> of the nearest viewport (§7.10): of its width for a horizontal
        /// length, its height for a vertical one, and its normalised diagonal for any other (a radius,
        /// a stroke width).
        /// </summary>
        private static float ViewportPercent(string name, float percent, ConvertContext ctx)
        {
            float w = ctx.ViewportWidth, h = ctx.ViewportHeight;
            float extent = name is "x" or "cx" or "x1" or "x2" or "dx" or "width" or "rx" ? w
                : name is "y" or "cy" or "y1" or "y2" or "dy" or "height" or "ry" ? h
                : NormalisedDiagonal(w, h);
            return percent / 100 * extent;
        }

        /// <summary>√((w² + h²) / 2): what a percentage that is neither horizontal nor vertical is of (§7.10).</summary>
        private static float NormalisedDiagonal(float w, float h) => (float)Math.Sqrt((w * w + h * h) / 2);

        /// <summary>
        /// The size of a page sized to the graphic: the root's <c>width</c>/<c>height</c> (a
        /// percentage is not a length, <see cref="TryParseLengthF"/>), else its <c>viewBox</c>'s, else
        /// the 300×150 default of a replaced element with no size (CSS 2.1 §10.4) — in SVG user units,
        /// which are PDF points here. <see cref="Images.SvgRasterizer"/>'s page; read by the same rules
        /// <see cref="Convert(string, SvgConversionOptions)"/> reads the root with, so the two cannot
        /// disagree about it.
        /// </summary>
        internal static (float Width, float Height) IntrinsicSize(string svgXml)
        {
            XmlElement root = ParseSvg(svgXml).DocumentElement;
            float w = TryParseLengthF(root, "width", 0f);
            float h = TryParseLengthF(root, "height", 0f);
            float vbW = 0, vbH = 0;
            string vb = root?.GetAttribute("viewBox");
            if (!string.IsNullOrWhiteSpace(vb))
            {
                var parts = ParseFloatList(vb, out bool complete);
                if (complete && parts.Count == 4) { vbW = parts[2]; vbH = parts[3]; }
            }
            return (w > 0 ? w : vbW > 0 ? vbW : DefaultIntrinsicWidth,
                    h > 0 ? h : vbH > 0 ? vbH : DefaultIntrinsicHeight);
        }

        /// <summary>CSS 2.1 §10.4 / SVG 1.1 §7.2: a replaced element with no usable size is 300×150.</summary>
        private const float DefaultIntrinsicWidth = 300, DefaultIntrinsicHeight = 150;

        /// <summary>
        /// <see cref="TryParseAttrF"/> for the root viewport, with percentages excluded.
        ///
        /// <para>A percentage resolves against a containing block this converter has no concept of,
        /// so it is <b>not</b> a length: <c>width="100%"</c> has to read as "unspecified".
        /// <see cref="TryParseAttrF"/> trims the <c>%</c> off and yields the number 100, which is a
        /// confidently wrong viewport rather than an absent one.</para>
        /// </summary>
        private static float TryParseLengthF(XmlNode node, string name, float def)
        {
            string v = GetAttr(node, name);
            if (v == null) return def;
            if (v.TrimEnd().EndsWith("%", StringComparison.Ordinal))
            {
                Report($"'{name}=\"{v}\"' is a percentage of a containing block this converter does " +
                       "not model; the attribute was ignored");
                return def;
            }
            return TryParseAttrF(node, name, def);
        }

        /// <summary>
        /// A list of numbers (point list, transform arguments, <c>viewBox</c>) read with
        /// <see cref="TryScanNumber"/>, up to the first character that is neither a separator nor
        /// the start of a number.
        /// </summary>
        /// <remarks>
        /// Stopping there follows SVG's error handling for a point list, which draws up to the last
        /// valid point. <paramref name="complete"/> says whether the whole string was read, for a
        /// <c>viewBox</c>, which is either four numbers or ignored.
        /// </remarks>
        private static List<float> ParseFloatList(string s, out bool complete)
        {
            var result = new List<float>();
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i]) || s[i] == ',') { i++; continue; }
                if (!TryScanNumber(s, ref i, out string token)) break;
                result.Add(ParseF(token));
            }
            complete = i >= s.Length;
            return result;
        }

        private static List<float> ParseFloatList(string s) => ParseFloatList(s, out _);

        // ── String helpers ────────────────────────────────────────────────────

        // One implementation, in Core.Objects.PdfStringEscape (ISO 32000-1 7.3.4.2).
        // This copy escaped only ( ) and backslash, so a carriage return reached the file raw
        // and came back as a line feed, and a NUL truncated some viewers' parsers.
        private static string EscapePdfString(string s) => PdfStringEscape.EscapeLiteral(s);

        private static string F(float v) => PdfRealFormat.Format(v, "F4");
        /// <summary>
        /// Formats an interpolated operator line, giving every numeric argument the same treatment
        /// <see cref="F(float)"/> gives one.
        /// </summary>
        /// <remarks>
        /// <b>This is the overload most operator lines actually go through</b>, and fixing only the
        /// single-value <c>F</c> left it emitting <c>NaN NaN NaN Infinity re</c> for
        /// <c>&lt;g transform="scale(1e40)"&gt;&lt;rect width="0" …</c> — a caller-controlled input.
        /// <c>double</c> is handled as well as <c>float</c>: an argument that arrives as a
        /// <c>double</c> would otherwise fall into the <c>else</c> and be formatted by
        /// <see cref="string.Format(IFormatProvider, string, object[])"/>, which writes
        /// <c>NaN</c> just as readily.
        /// </remarks>
        private static string F(FormattableString fs)
        {
            var args = new object[fs.ArgumentCount];
            for (int i = 0; i < fs.ArgumentCount; i++)
            {
                object arg = fs.GetArgument(i);
                args[i] = arg switch
                {
                    float f  => PdfRealFormat.Format(f, "F4"),
                    double d => PdfRealFormat.Format(d, "F4"),
                    _        => arg,
                };
            }
            return string.Format(CultureInfo.InvariantCulture, fs.Format, args);
        }

        // ── PDF builder ───────────────────────────────────────────────────────

        /// <summary>
        /// A one-page PDF of <paramref name="contentStream"/> in <paramref name="mediaBox"/>, with
        /// every resource the conversion has made so far, through the one implementation in
        /// <see cref="SinglePageDocumentWriter"/>: the page at the end, and a filtered
        /// element's region on the way (<see cref="Filtered"/>).
        /// </summary>
        /// <remarks>
        /// /WinAnsiEncoding, not the font's built-in StandardEncoding: the text bytes are mapped
        /// to WinAnsi codes in <c>ToWinAnsi</c>, and the two disagree over 0x80-0x9F, most of
        /// 0xA0-0xFF, and — easy to miss — 0x27 and 0x60, where StandardEncoding has quoteright
        /// and quoteleft rather than the straight quotesingle and grave.
        /// </remarks>
        private static byte[] BuildPdf(string contentStream, string mediaBox, ConvertContext ctx)
        {
            // Each link inside the drawing, now that its target has been drawn — or has not: a
            // definition, a target inside a clip — and so has no place on the page to go to.
            foreach (var (index, rect, id, href) in ctx.PendingGoTos)
            {
                if (ctx.TargetBounds.TryGetValue(id, out var b))
                    ctx.Objects[index] = SinglePageDocumentWriter.ExtraObject.Annotation(
                        F($"<< /Type /Annot /Subtype /Link /Rect {rect} /Border [0 0 0] /A << /S /GoTo /D [{SinglePageDocumentWriter.PageObject} 0 R /XYZ {b.MinX} {b.MaxY} null] >> >>"));
                else
                    ReportOnce($"a link to '{href}' inside the drawing is not kept: its target was not drawn");
            }
            // Each embedded font's objects, built now that every glyph drawn in it is known, numbered
            // on from the drawing's objects: the builder numbers them consecutively from the number
            // given, which is the next object's, so each lands at its own index.
            foreach (EmbeddedFont font in ctx.EmbeddedFonts)
            {
                int next = SinglePageDocumentWriter.FirstExtraObject + ctx.Objects.Count;
                var (type0, objects) = Core.Writing.Type0FontBuilder.BuildObjects(ref next, font.Program, font.GidToUnicode, font.GidToText);
                foreach (var (number, obj) in objects)
                {
                    int assigned = ctx.AddObject(SinglePageDocumentWriter.ExtraObject.Built(obj));
                    if (assigned != number)
                        throw new Core.PdfException($"embedded font object {number} would be written as {assigned}");
                }
                ctx.FontEntries.Append(ctx.FontEntries.Length == 0 ? "" : " ").Append('/').Append(font.Name)
                    .Append(' ').Append(type0.ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
            }
            string states = ctx.StateEntries.Length == 0 ? null : ctx.StateEntries.ToString();
            string xObjects = ctx.XObjectEntries.Length == 0 ? null : ctx.XObjectEntries.ToString();
            string fonts = ctx.FontEntries.Length == 0 ? null : ctx.FontEntries.ToString();
            string patterns = ctx.PatternEntries.Length == 0 ? null : ctx.PatternEntries.ToString();
            // A transparency group draws with the page's resources. The object its forms name is
            // written from the text the page's own are written from — again at the end, once all
            // are known.
            if (ctx.ResourcesObject != 0)
                ctx.Objects[ctx.ResourcesObject - SinglePageDocumentWriter.FirstExtraObject] =
                    SinglePageDocumentWriter.ExtraObject.Plain(
                        SinglePageDocumentWriter.Resources(FontEncoding, states, xObjects, fonts, patterns));
            return SinglePageDocumentWriter.Build(
                contentStream, mediaBox, fontEncoding: FontEncoding, states, xObjects, ctx.Objects, fonts, patterns);
        }

        /// <summary>
        /// The page's box, <c>[0 0 width height]</c>: the <c>/MediaBox</c>, and the <c>/BBox</c> of a
        /// transparency group's form, whose content is in page space. A /MediaBox is the one place a
        /// NaN is fatal rather than merely wrong: a page whose box will not parse has no geometry at all.
        /// </summary>
        private static string MediaBox(SvgConversionOptions opt)
            => $"[0 0 {PdfRealFormat.Format(opt.PageWidth, "F2")} {PdfRealFormat.Format(opt.PageHeight, "F2")}]";
    }
}

