// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Builds a PDF content stream and its associated resource objects
    /// (Type1 standard fonts and image XObjects).
    ///
    /// Usage:
    ///   var builder = new PdfContentStreamBuilder();
    ///   string f1 = builder.AddStandardFont("Helvetica");
    ///   builder.BeginText();
    ///   builder.SetFont(f1, 12);
    ///   builder.SetTextPosition(72, 720);
    ///   builder.ShowText("Hello, world!");
    ///   builder.EndText();
    ///   byte[] stream = builder.Build(compress: true);
    /// </summary>
    public sealed class PdfContentStreamBuilder
    {
        // Maps resource name (e.g. "F1") -> base font name (e.g. "Helvetica")
        private readonly Dictionary<string, string> _standardFonts =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Resource names (e.g. "EF1") of embedded composite fonts whose font objects
        // are written once at the document level; this content references them.
        private readonly HashSet<string> _externalFonts = new HashSet<string>(StringComparer.Ordinal);

        // Image XObjects
        private readonly List<ImageEntry> _images = new List<ImageEntry>();

        // ExtGState entries (name -> dictionary)
        private readonly Dictionary<string, PdfDictionary> _extGStates =
            new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);

        // Optional-content groups referenced by a /OC BDC (resource name -> the OCG's reference).
        //
        // A REFERENCE, not a dictionary, and that is the whole reason this category exists.
        // §14.6.2 lets a BDC property list be written inline only when it holds no indirect
        // reference, which is how BeginMarkedContent and BeginArtifact avoid needing resources at
        // all. An optional-content group is an indirect object by definition (§8.11.2) — several
        // pages and the catalog's /OCProperties all name the same one — so it cannot be inlined and
        // has to be reached through the page's /Properties.
        private readonly Dictionary<string, PdfReference> _optionalContent =
            new Dictionary<string, PdfReference>(StringComparer.Ordinal);

        private readonly StringBuilder _ops = new StringBuilder();

        private int _fontCounter;
        private int _imageCounter;
        // -1, not 0: the first optional-content name has always been OC0, and MintName advances
        // the counter before it names.
        private int _optionalContentCounter = -1;
        private int _gsCounter;

        // "fillAlpha|strokeAlpha" → the resource name already issued for that pair.
        private readonly Dictionary<string, string> _gsByAlpha =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Shading dictionaries (name -> dictionary), and the dedupe index over their definitions.
        private readonly Dictionary<string, PdfDictionary> _shadings =
            new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);

        private readonly Dictionary<string, string> _shadingByKey =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private int _shadingCounter;

        // Transparency-group form XObjects: another builder's content, drawn as one object (name,
        // the content, its bounding box in this content's space).
        private readonly List<FormEntry> _forms = new List<FormEntry>();
        private int _formCounter;

        // Resource names this content must not mint, because the page it is appended to already
        // uses them. See ReserveResourceNames.
        private readonly HashSet<string> _reservedNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Resource names this content must not mint, because the page it will be appended to
        /// already uses them.
        /// </summary>
        /// <param name="names">Names already in use, in any resource category.</param>
        /// <remarks>
        /// <para><b>Why this exists.</b> The builder names what it registers <c>F1</c>,
        /// <c>Im1</c>, <c>GS1</c>, …, which are the names ordinary PDFs use most. Appended to a
        /// page and merged into its resources, <c>/F1 Helvetica</c> replaced the page's own
        /// <c>/F1</c>. So a watermark re-rendered every line of the page's text in the watermark's
        /// font, and its <c>/GS1</c> put the page's own content at the watermark's opacity.</para>
        /// <para>Reserved across categories, not per category. PDF allows a <c>/Font F1</c> and an
        /// <c>/XObject F1</c> side by side, but skipping a name that is only taken in another
        /// category costs nothing, and it keeps the rule to one set.</para>
        /// </remarks>
        public void ReserveResourceNames(IEnumerable<string> names)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            foreach (var name in names)
                if (name != null) _reservedNames.Add(name);
        }

        /// <summary>
        /// <paramref name="prefix"/> followed by the next value of <paramref name="counter"/> that
        /// makes a name nobody reserved. The counter is advanced first, so an unreserved builder
        /// mints <c>F1</c>, <c>Im1</c>, … exactly as it always did.
        /// </summary>
        private string MintName(string prefix, ref int counter)
        {
            string name;
            do
            {
                counter++;
                name = prefix + counter.ToString(CultureInfo.InvariantCulture);
            }
            while (_reservedNames.Contains(name));
            return name;
        }

        // Clickable link rectangles (PDF user space) for the page that owns this content;
        // emitted as /Link annotations by PdfDocumentBuilder.
        private readonly List<(double x0, double y0, double x1, double y1, string uri)> _links =
            new List<(double, double, double, double, string)>();

        /// <summary>
        /// Registers a clickable hyperlink rectangle (PDF user space) with a URI action.
        /// Emitted as a /Link annotation when the document is built.
        /// </summary>
        public PdfContentStreamBuilder AddLink(double x0, double y0, double x1, double y1, string uri)
        {
            _links.Add((x0, y0, x1, y1, uri ?? ""));
            return this;
        }

        /// <summary>The link rectangles collected for the owning page.</summary>
        internal IReadOnlyList<(double x0, double y0, double x1, double y1, string uri)> Links => _links;

        /// <summary>
        /// Replaces every link rectangle registered since the <paramref name="fromLink"/>th URI link
        /// and the <paramref name="fromInternal"/>th internal one — both counts as
        /// <see cref="Links"/> and <see cref="InternalLinks"/> gave them before the links were added —
        /// with the axis-aligned bounds of its corners under <paramref name="matrix"/>
        /// (<see cref="TransformMatrix.BoundsOf"/>). An annotation's <c>/Rect</c> is in default user space
        /// (§12.5.2), which a content-stream <c>cm</c> does not reach, so a producer that transforms
        /// what it paints brings the clickable rectangles along through this, in the same default
        /// space the rectangles were registered in.
        /// </summary>
        internal void RemapLinkRectangles(int fromLink, int fromInternal, double[] matrix)
        {
            for (int i = Math.Max(0, fromLink); i < _links.Count; i++)
            {
                var (x0, y0, x1, y1, uri) = _links[i];
                var r = TransformMatrix.BoundsOf(matrix, x0, y0, x1, y1);
                _links[i] = (r.X0, r.Y0, r.X1, r.Y1, uri);
            }
            for (int i = Math.Max(0, fromInternal); i < _internalLinks.Count; i++)
            {
                var (x0, y0, x1, y1, dest) = _internalLinks[i];
                var r = TransformMatrix.BoundsOf(matrix, x0, y0, x1, y1);
                _internalLinks[i] = (r.X0, r.Y0, r.X1, r.Y1, dest);
            }
        }

        // Internal links (§12.3.2.2 GoTo) and the named destinations they aim at. Kept apart from
        // _links rather than folded into it with a flag: the two produce different /A dictionaries
        // and, more importantly, a destination NAME is not a URI — putting one in the uri slot is
        // how "#anchor" ends up in a /URI action and opens a browser.
        private readonly List<(double x0, double y0, double x1, double y1, string dest)> _internalLinks =
            new List<(double, double, double, double, string)>();

        private readonly List<(string name, double y)> _anchors = new List<(string, double)>();

        /// <summary>
        /// Registers a clickable rectangle (PDF user space) that jumps to the named destination
        /// <paramref name="destination"/>, wherever in the document that name is declared.
        /// </summary>
        /// <remarks>
        /// The name need not exist yet, or at all: resolution happens when the document is built.
        /// A link naming a destination nothing declared is dropped there rather than written as a
        /// dangling <c>/GoTo</c>, because a viewer's response to one is unspecified and in practice
        /// ranges from doing nothing to jumping to page 1.
        /// </remarks>
        public PdfContentStreamBuilder AddInternalLink(double x0, double y0, double x1, double y1,
                                                       string destination)
        {
            if (!string.IsNullOrEmpty(destination))
                _internalLinks.Add((x0, y0, x1, y1, destination));
            return this;
        }

        /// <summary>
        /// Declares a named destination on this page at <paramref name="y"/> (PDF user space).
        /// </summary>
        /// <remarks>
        /// Only the vertical position is taken. A destination is <c>/XYZ null y null</c> — left
        /// and zoom deliberately unset — so following the link scrolls the target to the top of
        /// the window without moving the reader horizontally or changing their magnification. A
        /// destination that resets zoom is a well-known annoyance, and the spec's own null is the
        /// way to say "leave it alone".
        /// </remarks>
        public PdfContentStreamBuilder AddAnchor(string name, double y)
        {
            if (!string.IsNullOrEmpty(name)) _anchors.Add((name, y));
            return this;
        }

        /// <summary>Internal link rectangles collected for the owning page.</summary>
        internal IReadOnlyList<(double x0, double y0, double x1, double y1, string dest)> InternalLinks
            => _internalLinks;

        /// <summary>Named destinations declared on the owning page.</summary>
        internal IReadOnlyList<(string name, double y)> Anchors => _anchors;

        /// <summary>
        /// Formats a number for PDF content streams: fixed-point notation (never scientific) with up
        /// to 6 decimal places, trailing zeros stripped, always the invariant culture.
        ///
        /// <para>Public because it is a <b>format rule, not a helper</b> — anything writing PDF
        /// operator text has to follow it exactly. Scientific notation is not valid in a content
        /// stream, and a decimal comma from a European culture makes the stream unparseable; both
        /// are silent corruption rather than an error. <c>Tetitulz.Pdf.Plugins.PostScript</c>'s
        /// emitter calls this rather than growing a second copy that could drift.</para>
        ///
        /// <para>It forwards to <see cref="PdfRealFormat"/>, which is where the rule lives, so the
        /// culture, the notation and the precision are right and no <c>NaN</c> reaches the operator
        /// text. A content
        /// stream is exactly where a non-finite value comes from: a layout that divides by zero, or
        /// a corrupt charstring's advance width.</para>
        /// </summary>
        public static string FormatNumber(double v) => PdfRealFormat.Format(v);

        private static string Fmt(double v) => FormatNumber(v);

        // ------------------------------------------------------------------
        // Resource registration
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers a PDF standard Type1 font (e.g. "Helvetica", "Times-Roman").
        /// Returns the resource name to pass to <see cref="SetFont"/>.
        /// Idempotent — the same base-font name always returns the same resource name.
        /// </summary>
        public string AddStandardFont(string baseFontName)
        {
            if (baseFontName == null) throw new ArgumentNullException(nameof(baseFontName));
            foreach (var kvp in _standardFonts)
                if (kvp.Value == baseFontName) return kvp.Key;
            string name = MintName("F", ref _fontCounter);
            _standardFonts[name] = baseFontName;
            return name;
        }

        /// <summary>
        /// Registers an image XObject. Returns the resource name to pass to
        /// <see cref="DrawImage"/>.
        /// </summary>
        /// <param name="data">Raw image bytes (JPEG, PNG/FlateDecode raw, etc.).</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="filter">
        /// PDF filter name ("DCTDecode", "FlateDecode"), or null for raw samples, which are
        /// Flate-compressed here and written as <c>/FlateDecode</c>.
        /// </param>
        /// <param name="colorSpace">Color space name (default "DeviceRGB").</param>
        /// <param name="bitsPerComponent">Bits per component (default 8).</param>
        public string AddImage(
            byte[] data,
            int width,
            int height,
            string filter = null,
            string colorSpace = PdfColorSpaceNames.DeviceRGB,
            int bitsPerComponent = 8)
            => AddImage(data, width, height, filter, colorSpace, bitsPerComponent, null, null);

        /// <summary>
        /// As the overload above, with an explicit <c>/Decode</c> array and/or a soft mask.
        ///
        /// <para><paramref name="decode"/> inverts or remaps the samples — a CMYK JPEG carrying an
        /// Adobe APP14 marker needs <c>[1 0 1 0 1 0 1 0]</c>, because Adobe writes those
        /// components inverted.</para>
        ///
        /// <para><paramref name="softMask"/> is real transparency: <b>raw, uncompressed</b> 8-bit
        /// greyscale alpha, one byte per pixel, the same size as this image. It is written as a
        /// separate XObject and referenced through <c>/SMask</c>; the compression is applied here,
        /// so a caller does not have to know which filter the writer uses — and so the
        /// fully-opaque test below inspects alpha VALUES rather than compressed bytes, which is
        /// what it must do to mean anything. It replaces compositing alpha over white, which is only correct when the
        /// background happens to be white — over a shaded paragraph or a coloured table cell the
        /// composited version shows a white halo around every transparent edge.</para>
        ///
        /// <para>A <b>fully opaque</b> mask is dropped rather than written: it would add a
        /// full-size greyscale image to the file and change nothing on the page. Same rule
        /// <c>Base.Operations.PdfImageReplacer</c> follows.</para>
        ///
        /// <para><b>The arrays are read when the page is built, not here</b>: raw samples and the
        /// mask are deflated as the image is written, so a document that registers the same arrays on several pages
        /// writes the image once. A caller must not change <paramref name="data"/> or <paramref name="softMask"/>
        /// after this call, before the document is built.</para>
        ///
        /// <para>An overload rather than optional parameters: this assembly ships as a NuGet
        /// package, and adding optional parameters changes the compiled signature, so a consumer
        /// built against the six-argument form would take a <see cref="MissingMethodException"/>.</para>
        /// </summary>
        public string AddImage(
            byte[] data,
            int width,
            int height,
            string filter,
            string colorSpace,
            int bitsPerComponent,
            double[] decode,
            byte[] softMask)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            RequireAuthoredSize(width, height);
            string name = MintName("Im", ref _imageCounter);

            // Raw samples are deflated by the writer, as the soft mask below always was. `filter: null`
            // meant UNCOMPRESSED, so every caller handing over decoded samples — a full-page scan at
            // three bytes a pixel — wrote them raw unless it remembered to compress first.
            // PdfImageToPdf remembered, twice, with a comment explaining why; PdfImageInserter did
            // not. Lossless, so no caller can tell but by the file's size. Deflated when the image is
            // written (BuildResources), not here: an image a document shares across pages
            // (ImageObjectShare) is then deflated once, and the caller's own array is the share's key.
            bool encode = filter == null;

            // An all-255 mask says "every pixel is opaque", which is what no mask at all says.
            // Tested on the RAW alpha: on compressed bytes this check is meaningless and silently
            // never fires.
            byte[] mask = softMask != null && !IsFullyOpaque(softMask) ? softMask : null;

            _images.Add(new ImageEntry
            {
                ResourceName = name,
                Data = data,
                EncodeData = encode,
                Width = width,
                Height = height,
                Filter = encode ? PdfName.FlateDecode.Value : filter,
                ColorSpace = colorSpace,
                BitsPerComponent = bitsPerComponent,
                Decode = decode,
                SoftMaskRaw = mask,
            });
            return name;
        }

        /// <summary>
        /// The one size rule for an authored image, asked by every image entry here: a side at most
        /// <see cref="PdfLimits.MaxImageDimension"/>, the area at most <see cref="PdfLimits.MaxImagePixelArea"/>,
        /// both sides positive. A producer that can skip the image and report asks
        /// <see cref="AuthoredImageSize.Refusal"/> first; one that does not gets the exception here,
        /// rather than writing a document hostile to whatever opens it next.
        /// </summary>
        private static void RequireAuthoredSize(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "An image's width must be positive.");
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "An image's height must be positive.");
            string refusal = AuthoredImageSize.Refusal(width, height);
            if (refusal != null)
                throw new ArgumentOutOfRangeException(
                    AuthoredImageSize.RefusedParameter(width, height, nameof(width), nameof(height)), refusal);
        }

        /// <summary>
        /// Registers a JPEG 2000 image XObject, embedded as it is under <c>/JPXDecode</c>:
        /// <paramref name="colorSpace"/> null leaves <c>/ColorSpace</c> out so the file's own colour
        /// space applies, <paramref name="bitsPerComponent"/> 0 leaves <c>/BitsPerComponent</c> out,
        /// and <paramref name="smaskInData"/> (0, 1 or 2) writes <c>/SMaskInData</c> so a viewer uses
        /// the file's straight or premultiplied opacity channel, or leaves it out for 0 (ISO 32000-2
        /// §7.4.9, Table 87). <c>Core.Imaging.Jpx.JpxImageDictionary</c> says which of these a file
        /// wants. Returns the resource name to pass to <see cref="DrawImage"/>.
        /// </summary>
        internal string AddJpxImage(byte[] data, int width, int height, string colorSpace, int bitsPerComponent, int smaskInData)
        {
            if (smaskInData < 0 || smaskInData > 2) throw new ArgumentOutOfRangeException(nameof(smaskInData), smaskInData, "/SMaskInData is 0, 1 or 2 (ISO 32000-2 Table 89)");
            if (data == null) throw new ArgumentNullException(nameof(data));
            RequireAuthoredSize(width, height);
            string name = MintName("Im", ref _imageCounter);
            _images.Add(new ImageEntry
            {
                ResourceName = name,
                Data = data,
                Width = width,
                Height = height,
                Filter = PdfName.JPXDecode.Value,
                ColorSpace = colorSpace,
                BitsPerComponent = bitsPerComponent,
                SMaskInData = smaskInData,
            });
            return name;
        }

        /// <summary>
        /// Registers an ExtGState resource with a fill opacity (ca) and/or stroke opacity (CA).
        /// Returns the resource name to pass to <see cref="SetGraphicsState"/>.
        /// </summary>
        public string AddExtGState(float? fillAlpha = null, float? strokeAlpha = null)
        {
            // Deduplicated on the pair. A generated document commonly asks for the same opacity
            // many times — every node of a translucent SmartArt diagram, every cell of a shaded
            // table — and each unique name costs an indirect object in the output. Callers that
            // ask twice for the same state get the same resource, which changes nothing for them.
            string key = (fillAlpha?.ToString("R", CultureInfo.InvariantCulture) ?? "-")
                       + "|" + (strokeAlpha?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
            if (_gsByAlpha.TryGetValue(key, out string existing)) return existing;

            string name = MintName("GS", ref _gsCounter);
            var gsDict = new PdfDictionary();
            gsDict.Set(PdfName.Type, PdfName.ExtGState);
            if (fillAlpha.HasValue)
                gsDict.Set(PdfName.ca, new PdfNumber(fillAlpha.Value));
            if (strokeAlpha.HasValue)
                gsDict.Set(PdfName.CA, new PdfNumber(strokeAlpha.Value));
            _extGStates[name] = gsDict;
            _gsByAlpha[key] = name;
            return name;
        }

        /// <summary>
        /// A colour stop of a gradient: a position in 0..1 and an RGB colour, components 0..1.
        /// </summary>
        public readonly struct ShadingStop
        {
            public ShadingStop(double offset, double r, double g, double b)
            {
                Offset = offset; R = r; G = g; B = b;
            }

            public double Offset { get; }
            public double R { get; }
            public double G { get; }
            public double B { get; }
        }

        /// <summary>
        /// Registers an axial (linear) gradient running from <c>(x0,y0)</c> to <c>(x1,y1)</c> in the
        /// current user space, and returns the resource name to pass to
        /// <see cref="ShadingFill(string)"/>.
        /// </summary>
        /// <param name="stops">
        /// Colour stops. They are sorted and clamped, and the ends are extended to 0 and 1 when the
        /// outermost stops fall short — which is what makes a gradient whose first stop is at, say,
        /// 20% render as a flat run of that colour up to the 20% mark rather than an interpolation
        /// from nowhere.
        /// </param>
        /// <param name="x0">Axis start x, in current user space.</param>
        /// <param name="y0">Axis start y, in current user space.</param>
        /// <param name="x1">Axis end x, in current user space.</param>
        /// <param name="y1">Axis end y, in current user space.</param>
        /// <param name="extendStart">
        /// Extends the first stop's colour beyond the axis start (§8.7.4.5.3 <c>/Extend</c>).
        /// </param>
        /// <param name="extendEnd">Extends the last stop's colour beyond the axis end.</param>
        public string AddAxialShading(double x0, double y0, double x1, double y1,
            IReadOnlyList<ShadingStop> stops, bool extendStart = true, bool extendEnd = true)
        {
            var normalised = NormaliseStops(stops);
            if (normalised == null) return null;

            string key = string.Format(CultureInfo.InvariantCulture, "A|{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                Fmt(x0), Fmt(y0), Fmt(x1), Fmt(y1), extendStart, extendEnd, StopsKey(normalised));
            if (_shadingByKey.TryGetValue(key, out string existing)) return existing;

            var dict = new PdfDictionary();
            dict.Set(PdfName.ShadingType, new PdfNumber(2));
            dict.Set(PdfName.ColorSpace, new PdfName(PdfColorSpaceNames.DeviceRGB));
            dict.Set(PdfName.Coords, Numbers(x0, y0, x1, y1));
            dict.Set(PdfName.Function, BuildStopFunction(normalised));
            dict.Set(PdfName.Extend, new PdfArray(new PdfObject[] { extendStart ? PdfBoolean.True : PdfBoolean.False, extendEnd ? PdfBoolean.True : PdfBoolean.False }));

            return Register(dict, key);
        }

        /// <summary>
        /// Registers a radial gradient: a point at <c>(cx,cy)</c> expanding to a circle of radius
        /// <paramref name="radius"/> about the same centre.
        /// </summary>
        public string AddRadialShading(double cx, double cy, double radius,
            IReadOnlyList<ShadingStop> stops, bool extendStart = true, bool extendEnd = true)
        {
            var normalised = NormaliseStops(stops);
            if (normalised == null || radius <= 0) return null;

            string key = string.Format(CultureInfo.InvariantCulture, "R|{0}|{1}|{2}|{3}|{4}|{5}",
                Fmt(cx), Fmt(cy), Fmt(radius), extendStart, extendEnd, StopsKey(normalised));
            if (_shadingByKey.TryGetValue(key, out string existing)) return existing;

            var dict = new PdfDictionary();
            dict.Set(PdfName.ShadingType, new PdfNumber(3));
            dict.Set(PdfName.ColorSpace, new PdfName(PdfColorSpaceNames.DeviceRGB));
            dict.Set(PdfName.Coords, Numbers(cx, cy, 0, cx, cy, radius));
            dict.Set(PdfName.Function, BuildStopFunction(normalised));
            dict.Set(PdfName.Extend, new PdfArray(new PdfObject[] { extendStart ? PdfBoolean.True : PdfBoolean.False, extendEnd ? PdfBoolean.True : PdfBoolean.False }));

            return Register(dict, key);
        }

        /// <summary>
        /// Paints the named shading over the current clip (<c>sh</c>). Clip to a path first —
        /// <c>sh</c> fills everything the clip allows.
        /// </summary>
        public PdfContentStreamBuilder ShadingFill(string shadingResourceName)
        {
            _ops.AppendFormat("/{0} " + PdfOperators.ShadingFill + "\n", shadingResourceName);
            return this;
        }

        private string Register(PdfDictionary dict, string key)
        {
            string name = MintName("Sh", ref _shadingCounter);
            _shadings[name] = dict;
            _shadingByKey[key] = name;
            return name;
        }

        /// <summary>
        /// Sorts stops, clamps their offsets into 0..1, drops duplicates, and pads the ends so the
        /// list always spans the whole domain. Returns null when there is nothing to draw.
        /// </summary>
        [ThreadStatic] private static string _lastShadingTruncation;

        /// <summary>
        /// Why the most recent shading built <b>on this thread</b> carried fewer stops than the
        /// caller supplied — <c>null</c> when none was dropped, and <c>null</c> on a thread that has
        /// built no shading.
        ///
        /// <para>Cleared at the start of every shading, on every path, so reading it straight after
        /// one describes that shading rather than an earlier one.</para>
        /// </summary>
        public static string LastShadingTruncation => _lastShadingTruncation;

        private static List<ShadingStop> NormaliseStops(IReadOnlyList<ShadingStop> stops)
        {
            _lastShadingTruncation = null;
            if (stops == null || stops.Count == 0) return null;

            var list = stops
                .Select(s => new ShadingStop(s.Offset < 0 ? 0 : s.Offset > 1 ? 1 : s.Offset, s.R, s.G, s.B))
                .OrderBy(s => s.Offset)
                .ToList();

            // Bounded HERE, in the primitive, not only in a caller. Each interval becomes its own
            // Type 2 sub-function inside a Type 3 stitching function, so the written dictionary
            // grows with the stop count without limit. DocxToPdf's GradientFill applies the same
            // cap before calling in, but this method is reachable from public API — a consumer
            // building a document from untrusted input (an SVG with thousands of gradient stops,
            // say) reaches it directly, and PdfLimits.MaxShadingStops already documents this
            // behaviour as if the primitive enforced it.
            if (list.Count > PdfLimits.MaxShadingStops)
            {
                _lastShadingTruncation =
                    $"{list.Count} shading stops supplied; only the first " +
                    $"{PdfLimits.MaxShadingStops} were written.";
                list = list.GetRange(0, PdfLimits.MaxShadingStops);
            }

            if (list[0].Offset > 0)
                list.Insert(0, new ShadingStop(0, list[0].R, list[0].G, list[0].B));
            var last = list[list.Count - 1];
            if (last.Offset < 1)
                list.Add(new ShadingStop(1, last.R, last.G, last.B));

            return list;
        }

        /// <summary>
        /// Builds the shading's colour function: a single Type 2 exponential for two stops, or a
        /// Type 3 stitching function over one Type 2 per interval for more. Both are written as
        /// direct dictionaries inside the shading, which the spec allows and which keeps a gradient
        /// to one indirect object.
        /// </summary>
        private static PdfObject BuildStopFunction(List<ShadingStop> stops)
        {
            var domain = Numbers(0, 1);

            if (stops.Count == 1)
                return Exponential(stops[0], stops[0], domain);
            if (stops.Count == 2)
                return Exponential(stops[0], stops[1], domain);

            var functions = new PdfArray();
            var bounds = new PdfArray();
            var encode = new PdfArray();
            for (int i = 0; i < stops.Count - 1; i++)
            {
                functions.Add(Exponential(stops[i], stops[i + 1], domain));
                if (i > 0) bounds.Add(new PdfNumber(stops[i].Offset));
                encode.Add(new PdfNumber(0));
                encode.Add(new PdfNumber(1));
            }

            var stitch = new PdfDictionary();
            stitch.Set(PdfName.FunctionType, new PdfNumber(3));
            stitch.Set(PdfName.Domain, domain);
            stitch.Set(PdfName.Function, functions);
            stitch.Set(PdfName.Bounds, bounds);
            stitch.Set(PdfName.Encode, encode);
            return stitch;
        }

        private static PdfDictionary Exponential(ShadingStop from, ShadingStop to, PdfArray domain)
        {
            var fn = new PdfDictionary();
            fn.Set(PdfName.FunctionType, new PdfNumber(2));
            fn.Set(PdfName.Domain, domain);
            fn.Set(PdfName.C0, Numbers(from.R, from.G, from.B));
            fn.Set(PdfName.C1, Numbers(to.R, to.G, to.B));
            fn.Set(PdfName.N, new PdfNumber(1));
            return fn;
        }

        private static PdfArray Numbers(params double[] values) => PdfArray.OfNumbers(values);

        private static string StopsKey(List<ShadingStop> stops)
            => string.Join(";", stops.Select(s => string.Format(CultureInfo.InvariantCulture,
                "{0:F4}:{1:F4},{2:F4},{3:F4}", s.Offset, s.R, s.G, s.B)));

        // ------------------------------------------------------------------
        // Graphics state operators
        // ------------------------------------------------------------------

        /// <summary>Saves the current graphics state (q).</summary>
        public PdfContentStreamBuilder SaveState()
        {
            _ops.Append(PdfOperators.Save + "\n");
            return this;
        }

        /// <summary>Restores the graphics state (Q).</summary>
        public PdfContentStreamBuilder RestoreState()
        {
            _ops.Append(PdfOperators.Restore + "\n");
            return this;
        }

        /// <summary>Concatenates a transformation matrix (cm).</summary>
        public PdfContentStreamBuilder Transform(double a, double b, double c, double d, double e, double f)
        {
            _ops.Append($"{Fmt(a)} {Fmt(b)} {Fmt(c)} {Fmt(d)} {Fmt(e)} {Fmt(f)} {PdfOperators.ConcatMatrix}\n");
            return this;
        }

        /// <summary>Sets the named ExtGState resource (gs).</summary>
        public PdfContentStreamBuilder SetGraphicsState(string gsResourceName)
        {
            _ops.AppendFormat("/{0} " + PdfOperators.SetExtGState + "\n", gsResourceName);
            return this;
        }

        /// <summary>Sets non-stroking (fill) RGB color (rg). Values 0–1.</summary>
        public PdfContentStreamBuilder SetFillColorRgb(float r, float g, float b)
        {
            _ops.Append($"{Fmt(r)} {Fmt(g)} {Fmt(b)} {PdfOperators.SetRgbFill}\n");
            return this;
        }

        /// <summary>Sets stroking RGB color (RG). Values 0–1.</summary>
        public PdfContentStreamBuilder SetStrokeColorRgb(float r, float g, float b)
        {
            _ops.Append($"{Fmt(r)} {Fmt(g)} {Fmt(b)} {PdfOperators.SetRgbStroke}\n");
            return this;
        }

        /// <summary>Sets non-stroking gray color (g). Value 0–1.</summary>
        public PdfContentStreamBuilder SetFillColorGray(float gray)
        {
            _ops.Append($"{Fmt(gray)} {PdfOperators.SetGrayFill}\n");
            return this;
        }

        /// <summary>Sets stroking gray color (G). Value 0–1.</summary>
        public PdfContentStreamBuilder SetStrokeColorGray(float gray)
        {
            _ops.Append($"{Fmt(gray)} {PdfOperators.SetGrayStroke}\n");
            return this;
        }

        /// <summary>Sets non-stroking CMYK color (k). Values 0–1.</summary>
        public PdfContentStreamBuilder SetFillColorCmyk(float c, float m, float y, float k)
        {
            _ops.Append($"{Fmt(c)} {Fmt(m)} {Fmt(y)} {Fmt(k)} {PdfOperators.SetCmykFill}\n");
            return this;
        }

        /// <summary>Sets stroking CMYK color (K). Values 0–1.</summary>
        public PdfContentStreamBuilder SetStrokeColorCmyk(float c, float m, float y, float k)
        {
            _ops.Append($"{Fmt(c)} {Fmt(m)} {Fmt(y)} {Fmt(k)} {PdfOperators.SetCmykStroke}\n");
            return this;
        }

        /// <summary>Sets line width (w).</summary>
        public PdfContentStreamBuilder SetLineWidth(float width)
        {
            _ops.Append($"{Fmt(width)} {PdfOperators.SetLineWidth}\n");
            return this;
        }

        // ------------------------------------------------------------------
        // Path construction and painting
        // ------------------------------------------------------------------

        /// <summary>Begins a new subpath at (x, y) (m).</summary>
        public PdfContentStreamBuilder MoveTo(double x, double y)
        {
            _ops.Append($"{Fmt(x)} {Fmt(y)} {PdfOperators.MoveTo}\n");
            return this;
        }

        /// <summary>Appends a line to (x, y) (l).</summary>
        public PdfContentStreamBuilder LineTo(double x, double y)
        {
            _ops.Append($"{Fmt(x)} {Fmt(y)} {PdfOperators.LineTo}\n");
            return this;
        }

        /// <summary>Appends a cubic Bézier curve (c).</summary>
        public PdfContentStreamBuilder CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            _ops.Append($"{Fmt(x1)} {Fmt(y1)} {Fmt(x2)} {Fmt(y2)} {Fmt(x3)} {Fmt(y3)} {PdfOperators.CurveTo}\n");
            return this;
        }

        /// <summary>Appends a rectangle to the path (re).</summary>
        public PdfContentStreamBuilder Rectangle(double x, double y, double width, double height)
        {
            _ops.Append($"{Fmt(x)} {Fmt(y)} {Fmt(width)} {Fmt(height)} {PdfOperators.Rectangle}\n");
            return this;
        }

        /// <summary>Closes the current subpath (h).</summary>
        public PdfContentStreamBuilder ClosePath()
        {
            _ops.Append(PdfOperators.ClosePath + "\n");
            return this;
        }

        /// <summary>Sets the clipping path using the nonzero winding rule (W n).</summary>
        public PdfContentStreamBuilder ClipNonzero()
        {
            _ops.Append(PdfOperators.Clip + " " + PdfOperators.EndPath + "\n");
            return this;
        }

        /// <summary>Sets the clipping path using the even-odd rule (W* n).</summary>
        public PdfContentStreamBuilder ClipEvenOdd()
        {
            _ops.Append(PdfOperators.ClipEvenOdd + " " + PdfOperators.EndPath + "\n");
            return this;
        }

        /// <summary>Fills the current path (f).</summary>
        public PdfContentStreamBuilder Fill()
        {
            _ops.Append(PdfOperators.Fill + "\n");
            return this;
        }

        /// <summary>Strokes the current path (S).</summary>
        public PdfContentStreamBuilder Stroke()
        {
            _ops.Append(PdfOperators.Stroke + "\n");
            return this;
        }

        /// <summary>Fills and strokes the current path (B).</summary>
        public PdfContentStreamBuilder FillAndStroke()
        {
            _ops.Append(PdfOperators.FillStroke + "\n");
            return this;
        }

        /// <summary>Closes and strokes the current path (s).</summary>
        public PdfContentStreamBuilder CloseAndStroke()
        {
            _ops.Append(PdfOperators.CloseStroke + "\n");
            return this;
        }

        // ------------------------------------------------------------------
        // Marked content (tagged PDF)
        // ------------------------------------------------------------------

        private int _nextMcid;
        private int _markedContentDepth;

        /// <summary>MCIDs handed out on this page — the length its <c>/ParentTree</c> array needs.</summary>
        public int MarkedContentCount => _nextMcid;

        /// <summary>
        /// Opens a marked-content sequence belonging to <paramref name="element"/>, allocating an
        /// MCID and recording it on that element in one step.
        ///
        /// <para>Taking the element rather than a role string is what makes "allocate an id" and
        /// "remember where it went" indivisible. Handing an <c>int</c> back for the caller to
        /// register is the desync waiting to happen: a missed registration produces a marked
        /// sequence that no structure element claims, which is exactly the violation tagged PDF
        /// exists to avoid.</para>
        ///
        /// <para>The property list is written inline. §14.6.2 permits that when it holds no
        /// indirect reference, so no <c>/Properties</c> resource entry is needed.</para>
        /// </summary>
        public int BeginMarkedContent(PdfStructureElement element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            int mcid = _nextMcid++;
            _markedContentDepth++;
            _ops.Append('/').Append(EscapeName(element.Role))
                .Append(" <</MCID ").Append(mcid.ToString(CultureInfo.InvariantCulture)).Append(">> BDC\n");
            element.AddContent(this, mcid);
            return mcid;
        }

        /// <summary>
        /// Opens an artifact sequence — content that is page furniture (a running head, a rule,
        /// a shading) rather than document content, so a reader skips it.
        ///
        /// <para>No MCID: an artifact belongs to no structure element by definition. Leaving such
        /// ink <i>unmarked</i> inside a tagged page is itself a conformance violation, which is
        /// why decoration has to be marked as something rather than left alone.</para>
        /// </summary>
        public PdfContentStreamBuilder BeginArtifact(string artifactType, string subtype = null)
        {
            _markedContentDepth++;
            _ops.Append("/Artifact <</Type /").Append(EscapeName(artifactType ?? PdfArtifactTypes.Layout));
            if (!string.IsNullOrEmpty(subtype)) _ops.Append(" /Subtype /").Append(EscapeName(subtype));
            _ops.Append(">> BDC\n");
            return this;
        }

        /// <summary>
        /// Opens an optional-content sequence: everything until the matching
        /// <see cref="EndMarkedContent"/> is shown only when <paramref name="optionalContentGroup"/>
        /// is visible.
        /// </summary>
        /// <remarks>
        /// <para><b>The property list is a NAME here, where the other two emitters write a
        /// dictionary inline.</b> §14.6.2 permits the inline form only for a property list holding
        /// no indirect reference, and an optional-content group is an indirect object by definition
        /// (§8.11.2): the catalog's <c>/OCProperties /OCGs</c> and every page that uses it name the
        /// same object. So this registers a page <c>/Properties</c> entry and writes
        /// <c>/OC /OC0 BDC</c> against it.</para>
        ///
        /// <para><b>The group is the caller's object.</b> This takes a reference rather than
        /// creating one, because an OCG is document-level — it belongs to the catalog's
        /// <c>/OCProperties</c>, which a page-content builder has no business writing.
        /// <c>Base.Operations.Structure.PdfOcgManager.CreateLayer</c> is what makes one.</para>
        ///
        /// <para><b>The same group used twice gets one resource entry.</b> Names are allocated per
        /// group, not per call, so wrapping three separate runs in one layer produces one
        /// <c>/Properties</c> entry and three <c>BDC</c>s naming it.</para>
        ///
        /// <para>Closed by <see cref="EndMarkedContent"/>, like the other two: <c>EMC</c> does not
        /// distinguish what it closes, so one counter serves all three.</para>
        /// </remarks>
        /// <param name="optionalContentGroup">
        /// Reference to the OCG (or an optional-content membership dictionary) that governs the
        /// sequence.
        /// </param>
        /// <returns>This builder, for chaining.</returns>
        public PdfContentStreamBuilder BeginOptionalContent(PdfReference optionalContentGroup)
        {
            if (optionalContentGroup == null) throw new ArgumentNullException(nameof(optionalContentGroup));

            // PdfReference.Equals compares ReferenceObjectNumber/ReferenceGenerationNumber -- the
            // object POINTED AT. Not ObjectNumber/GenerationNumber, which PdfObject defines as the
            // number of the object this one is stored in and which is 0 on a reference built by a
            // caller: comparing those made every group equal to every other and collapsed every
            // layer on the page into one /Properties entry.
            string name = null;
            foreach (KeyValuePair<string, PdfReference> entry in _optionalContent)
            {
                if (!entry.Value.Equals(optionalContentGroup)) continue;
                name = entry.Key;
                break;
            }

            if (name == null)
            {
                name = MintName("OC", ref _optionalContentCounter);
                _optionalContent[name] = optionalContentGroup;
            }

            _markedContentDepth++;
            _ops.Append("/OC /").Append(EscapeName(name)).Append(" BDC\n");
            return this;
        }

        /// <summary>Closes the innermost marked-content sequence (EMC).</summary>
        public PdfContentStreamBuilder EndMarkedContent()
        {
            if (_markedContentDepth <= 0)
                throw new InvalidOperationException(
                    "EndMarkedContent without a matching BeginMarkedContent/BeginArtifact.");
            _markedContentDepth--;
            _ops.Append("EMC\n");
            return this;
        }

        /// <summary>True when every opened marked-content sequence has been closed.</summary>
        internal bool MarkedContentBalanced => _markedContentDepth == 0;

        // A structure role or artifact type reaching the content stream as a PDF name. These come
        // from PdfStructureTypes, but a caller can pass its own, and an unescaped delimiter would
        // corrupt the stream.
        private static string EscapeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "P";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c > 0x20 && c < 0x7F && c != '/' && c != '(' && c != ')' && c != '<' && c != '>'
                    && c != '[' && c != ']' && c != '{' && c != '}' && c != '%' && c != '#')
                    sb.Append(c);
                else sb.Append('#').Append(((int)c & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "P" : sb.ToString();
        }

        // Text operators
        // ------------------------------------------------------------------

        /// <summary>Begins a text object (BT).</summary>
        public PdfContentStreamBuilder BeginText()
        {
            _ops.Append(PdfOperators.BeginText + "\n");
            return this;
        }

        /// <summary>Ends a text object (ET).</summary>
        public PdfContentStreamBuilder EndText()
        {
            _ops.Append(PdfOperators.EndText + "\n");
            return this;
        }

        /// <summary>Sets font and size (Tf).</summary>
        public PdfContentStreamBuilder SetFont(string fontResourceName, float size)
        {
            _ops.Append($"/{fontResourceName} {Fmt(size)} {PdfOperators.SetFont}\n");
            return this;
        }

        /// <summary>Moves the text position (Td).</summary>
        public PdfContentStreamBuilder SetTextPosition(double x, double y)
        {
            _ops.Append($"{Fmt(x)} {Fmt(y)} {PdfOperators.TextMove}\n");
            return this;
        }

        /// <summary>Sets the text matrix directly (Tm).</summary>
        public PdfContentStreamBuilder SetTextMatrix(double a, double b, double c, double d, double x, double y)
        {
            _ops.Append($"{Fmt(a)} {Fmt(b)} {Fmt(c)} {Fmt(d)} {Fmt(x)} {Fmt(y)} {PdfOperators.SetTextMatrix}\n");
            return this;
        }

        /// <summary>
        /// Sets the text leading (TL) — the baseline-to-baseline distance <see cref="NextLine"/>
        /// then applies. This emits TL only; call <see cref="NextLine"/> to move.
        /// </summary>
        public PdfContentStreamBuilder SetLeading(float leading)
        {
            _ops.Append($"{Fmt(leading)} {PdfOperators.SetLeading}\n");
            return this;
        }

        /// <summary>Moves to the start of the next line (T*).</summary>
        public PdfContentStreamBuilder NextLine()
        {
            _ops.Append(PdfOperators.NextLine + "\n");
            return this;
        }

        /// <summary>Sets character spacing (Tc).</summary>
        public PdfContentStreamBuilder SetCharSpacing(float spacing)
        {
            _ops.Append($"{Fmt(spacing)} {PdfOperators.SetCharSpacing}\n");
            return this;
        }

        /// <summary>Sets word spacing (Tw).</summary>
        public PdfContentStreamBuilder SetWordSpacing(float spacing)
        {
            _ops.Append($"{Fmt(spacing)} {PdfOperators.SetWordSpacing}\n");
            return this;
        }

        /// <summary>
        /// Sets the text rendering mode (Tr, §9.3.6): <see cref="TextRenderMode.Invisible"/> shows
        /// text that takes its room and paints nothing. Text state, so it persists to the next
        /// <c>Tr</c>, across <c>ET</c> included.
        /// </summary>
        public PdfContentStreamBuilder SetTextRenderMode(TextRenderMode mode)
        {
            _ops.Append($"{((int)mode).ToString(CultureInfo.InvariantCulture)} {PdfOperators.SetTextRenderMode}\n");
            return this;
        }

        /// <summary>
        /// Shows a text string encoded in WinAnsi (Tj).
        /// Characters outside WinAnsi (code points > 255) are replaced with '?'.
        /// </summary>
        public PdfContentStreamBuilder ShowText(string text)
        {
            AppendWinAnsiLiteral(text, 0, text.Length);
            _ops.Append(" " + PdfOperators.ShowText + "\n");
            return this;
        }

        /// <summary>
        /// Shows a WinAnsi string as <see cref="ShowText(string)"/> does, with an adjustment after each
        /// glyph (<c>TJ</c>, §9.4.3): <paramref name="adjustments"/>[i] is the space added after glyph
        /// i, in thousandths of an em — an AFM <c>KPX</c> value, negative bringing the next glyph closer
        /// — so it is written negated. One per gap; a 0 writes nothing. <c>Tj</c> when none applies.
        /// </summary>
        internal PdfContentStreamBuilder ShowText(string text, double[] adjustments)
        {
            if (adjustments == null || text.Length < 2) return ShowText(text);
            _ops.Append('[');
            int start = 0;
            for (int i = 0; i + 1 < text.Length && i < adjustments.Length; i++)
            {
                if (adjustments[i] == 0) continue;
                AppendWinAnsiLiteral(text, start, i + 1);
                _ops.Append(' ').Append(Fmt(-adjustments[i])).Append(' ');
                start = i + 1;
            }
            AppendWinAnsiLiteral(text, start, text.Length);
            _ops.Append("] " + PdfOperators.ShowTextArray + "\n");
            return this;
        }

        // text[start, end) as a WinAnsi literal string: a character above U+00FF as '?'.
        private void AppendWinAnsiLiteral(string text, int start, int end)
        {
            _ops.Append("(");
            for (int i = start; i < end; i++)
            {
                char c = text[i];
                byte b = c <= 0xFF ? (byte)c : (byte)'?';
                switch (b)
                {
                    case (byte)'(':  _ops.Append("\\("); break;
                    case (byte)')':  _ops.Append("\\)"); break;
                    case (byte)'\\': _ops.Append("\\\\"); break;
                    // Escape end-of-line and other control bytes: an unescaped CR
                    // inside a literal string is normalised to LF by the reader
                    // (ISO 32000 §7.3.4.2), silently altering the rendered text.
                    case (byte)'\r': _ops.Append("\\r"); break;
                    case (byte)'\n': _ops.Append("\\n"); break;
                    case (byte)'\t': _ops.Append("\\t"); break;
                    case (byte)'\b': _ops.Append("\\b"); break;
                    case (byte)'\f': _ops.Append("\\f"); break;
                    default: _ops.Append((char)b); break;
                }
            }
            _ops.Append(")");
        }

        /// <summary>
        /// Shows a run of glyph ids for a composite (Type0 / Identity-H) font registered
        /// via <see cref="UseEmbeddedFont"/>: emits <c>&lt;GID GID …&gt; Tj</c>, where each
        /// 2-byte code IS the glyph id. The caller is responsible for selecting the embedded
        /// font with <see cref="SetFont"/> first and for any bidi reordering / shaping.
        /// </summary>
        public PdfContentStreamBuilder ShowGlyphs(ushort[] glyphIds)
        {
            if (glyphIds == null || glyphIds.Length == 0) return this;
            _ops.Append('<');
            foreach (ushort g in glyphIds) _ops.Append(g.ToString("X4", CultureInfo.InvariantCulture));
            _ops.Append("> " + PdfOperators.ShowText + "\n");
            return this;
        }

        /// <summary>
        /// Shows glyph ids as <see cref="ShowGlyphs(ushort[])"/> does, each drawn where
        /// <paramref name="placements"/> puts it — a composed run's mark attachment
        /// (<see cref="Text.GlyphRunShowText"/>); <paramref name="fontSize"/> is the <c>Tf</c> size
        /// and <paramref name="rise"/> the text rise in force, which a placed mark's is relative to;
        /// <paramref name="charSpacing"/> is the <c>Tc</c> in force, which a mark does not take.
        /// </summary>
        public PdfContentStreamBuilder ShowGlyphs(ushort[] glyphIds, IReadOnlyList<Text.GlyphPlacement> placements,
                                                  double fontSize, double rise = 0, double charSpacing = 0)
        {
            if (glyphIds == null || glyphIds.Length == 0) return this;
            _ops.Append(Text.GlyphRunShowText.Operators(glyphIds, placements, fontSize, rise, null, charSpacing)).Append('\n');
            return this;
        }

        // ------------------------------------------------------------------
        // Transparency groups
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers <paramref name="content"/> — another builder, drawn into with the same calls
        /// as a page — as a form XObject with an isolated transparency group (ISO 32000-1 §11.6.6,
        /// <c>/Group &lt;&lt; /S /Transparency /I true &gt;&gt;</c>), so that
        /// <see cref="DrawForm"/> paints everything in it <b>once</b>, at the graphics state in force:
        /// a constant alpha set before the <c>Do</c> fades the group as a whole, where the same
        /// alpha on each object inside would let the lower ones show through the upper — CSS
        /// <c>opacity</c> against PDF's <c>/ca</c>. Returns the resource name to pass to
        /// <see cref="DrawForm"/>.
        /// </summary>
        /// <param name="content">
        /// The group's content, in this content's coordinate space (the form's matrix is the
        /// identity); its fonts, images, states and shadings become the form's own resources when
        /// the document is built, and its links and anchors are hoisted onto this content, since
        /// annotations belong to the page.
        /// </param>
        /// <param name="x0">The bounding box's left, in this content's space.</param>
        /// <param name="y0">Its bottom.</param>
        /// <param name="x1">Its right.</param>
        /// <param name="y1">Its top.</param>
        /// <remarks>
        /// <para><b>Marked content inside the group is refused.</b> An MCID inside a form belongs to
        /// the form's own <c>/StructParents</c> entry, which this builder does not write; the caller
        /// marks the <c>Do</c> instead — §14.7.4.4: a form XObject invoked inside a marked-content
        /// sequence is part of that sequence — which is what the Builder's exporter does.</para>
        /// </remarks>
        public string AddTransparencyGroup(PdfContentStreamBuilder content, double x0, double y0, double x1, double y1)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (ReferenceEquals(content, this)) throw new ArgumentException("A content stream cannot be its own transparency group.", nameof(content));
            if (content.MarkedContentCount > 0)
                throw new InvalidOperationException("Marked content inside a transparency group is not supported: mark the group's Do instead (ISO 32000-1 §14.7.4.4).");

            string name = MintName("Fm", ref _formCounter);
            _forms.Add(new FormEntry { Name = name, Content = content, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
            // Annotations belong to the page: what the group's content linked or anchored is the
            // page's to emit, in the same coordinates, since the form's matrix is the identity.
            _links.AddRange(content._links);
            _internalLinks.AddRange(content._internalLinks);
            _anchors.AddRange(content._anchors);
            return name;
        }

        /// <summary>Paints a form XObject registered by <see cref="AddTransparencyGroup"/> (<c>Do</c>).</summary>
        public PdfContentStreamBuilder DrawForm(string formResourceName)
        {
            if (string.IsNullOrEmpty(formResourceName)) throw new ArgumentNullException(nameof(formResourceName));
            _ops.Append('/').Append(EscapeName(formResourceName)).Append(' ').Append(PdfOperators.InvokeXObject).Append('\n');
            return this;
        }

        // ------------------------------------------------------------------
        // XObject (image) placement
        // ------------------------------------------------------------------

        /// <summary>
        /// Places an image XObject at (x, y) with the given display dimensions.
        /// Wraps the Do operator in a save/restore and cm matrix.
        /// </summary>
        public PdfContentStreamBuilder DrawImage(
            string imageResourceName,
            double x, double y,
            double displayWidth, double displayHeight)
        {
            SaveState();
            Transform(displayWidth, 0, 0, displayHeight, x, y);
            _ops.AppendFormat("/{0} " + PdfOperators.InvokeXObject + "\n", imageResourceName);
            RestoreState();
            return this;
        }

        // ------------------------------------------------------------------
        // Low-level escape hatch
        // ------------------------------------------------------------------

        /// <summary>Appends raw PDF operator text verbatim.</summary>
        public PdfContentStreamBuilder AppendRaw(string operatorText)
        {
            _ops.Append(operatorText);
            return this;
        }

        /// <summary>
        /// The length of the content written so far: a position <see cref="MoveTailTo"/> can later
        /// put content at.
        /// </summary>
        public int ContentLength => _ops.Length;

        /// <summary>
        /// Moves everything written since <paramref name="from"/> to <paramref name="position"/>,
        /// ahead of what was written between them — so content whose size is known only later is
        /// still painted first, behind what follows it.
        /// </summary>
        /// <remarks>
        /// A box's background is the case: its height is known once its contents are laid out, and it
        /// must be painted beneath them. Both positions come from <see cref="ContentLength"/>, and
        /// <paramref name="position"/> must not be later than <paramref name="from"/>. A position
        /// taken before an earlier move to a point ahead of it is stale; moving to the latest
        /// position first keeps every earlier one valid.
        /// </remarks>
        public void MoveTailTo(int from, int position)
        {
            if (from < 0 || from > _ops.Length) throw new ArgumentOutOfRangeException(nameof(from));
            if (position < 0 || position > from) throw new ArgumentOutOfRangeException(nameof(position));
            if (position == from || from == _ops.Length) return;

            string tail = _ops.ToString(from, _ops.Length - from);
            _ops.Length = from;
            _ops.Insert(position, tail);
        }

        /// <summary>
        /// Records that this content references an embedded composite font under the
        /// given resource name (e.g. "EF1"). The font's objects are written once at
        /// the document level; <see cref="BuildObjects(ref int, bool, IReadOnlyDictionary{string, int})"/>
        /// adds the reference to this content's /Font resource dictionary.
        /// </summary>
        public void UseEmbeddedFont(string fontResourceName)
        {
            if (!string.IsNullOrEmpty(fontResourceName))
                _externalFonts.Add(fontResourceName);
        }

        /// <summary>
        /// Replaces every occurrence of a placeholder token in the accumulated content
        /// (used for deferred field substitution such as NUMPAGES, which is only known
        /// once all pages exist).
        /// </summary>
        public void ReplaceInContent(string from, string to) => _ops.Replace(from, to);

        // ------------------------------------------------------------------
        // Build
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the content stream bytes, optionally FlateDecode-compressed.
        /// </summary>
        public byte[] Build(bool compress = false)
        {
            byte[] raw = Text.Latin1Text.Encode(_ops.ToString());
            if (!compress) return raw;
            return FlateEncodeFilter.Encode(raw);
        }

        /// <summary>
        /// Builds the complete set of indirect objects needed by this content stream:
        /// font dictionaries, image XObject streams, ExtGState dictionaries, a resources
        /// dictionary, and the content stream itself.
        /// </summary>
        /// <param name="nextObjectNumber">
        /// Starting object number; incremented for each object allocated.
        /// </param>
        /// <param name="compress">Whether to FlateDecode-compress the content stream.</param>
        /// <returns>
        /// A tuple of:
        ///   - <c>contentRef</c>: reference to the content stream object.
        ///   - <c>resources</c>: the inline resources dictionary (embed directly in the page).
        ///   - <c>sideObjects</c>: indirect objects that must be written to the document.
        /// </returns>
        public (PdfReference contentRef, PdfDictionary resources, List<(int objNum, PdfObject obj)> sideObjects)
            BuildObjects(ref int nextObjectNumber, bool compress = false)
            => BuildObjects(ref nextObjectNumber, compress, null);

        /// <summary>
        /// As <see cref="BuildObjects(ref int, bool)"/>, but resolves references to
        /// document-level embedded composite fonts via <paramref name="embeddedFontObjNums"/>
        /// (resource name → object number), so multiple pages can share one font program.
        /// </summary>
        public (PdfReference contentRef, PdfDictionary resources, List<(int objNum, PdfObject obj)> sideObjects)
            BuildObjects(ref int nextObjectNumber, bool compress,
                         IReadOnlyDictionary<string, int> embeddedFontObjNums)
            => BuildObjects(ref nextObjectNumber, compress, embeddedFontObjNums, null);

        /// <summary>
        /// As <see cref="BuildObjects(ref int, bool, IReadOnlyDictionary{string, int})"/>, drawing an image an earlier
        /// page of the same document wrote through that object (<paramref name="share"/>, one per document build) instead
        /// of writing it again; null writes every image, as the public overloads do.
        /// </summary>
        internal (PdfReference contentRef, PdfDictionary resources, List<(int objNum, PdfObject obj)> sideObjects)
            BuildObjects(ref int nextObjectNumber, bool compress,
                         IReadOnlyDictionary<string, int> embeddedFontObjNums, ImageObjectShare share)
        {
            var side = new List<(int, PdfObject)>();
            PdfDictionary resources = BuildResources(ref nextObjectNumber, compress, embeddedFontObjNums, side, share);

            // --- Content stream ---
            int contentNum = nextObjectNumber++;
            byte[] contentBytes = Build(compress);
            var contentStream = new PdfStream();
            contentStream.RawData = contentBytes;
            contentStream.Set(PdfName.Length, new PdfNumber(contentBytes.Length));
            if (compress)
                contentStream.Set(PdfName.Filter, PdfName.FlateDecode);
            contentStream.ObjectNumber = contentNum;
            contentStream.GenerationNumber = 0;
            side.Add((contentNum, contentStream));

            return (new PdfReference(contentNum, 0), resources, side);
        }

        /// <summary>
        /// The resources this content needs — font dictionaries, image XObjects, ExtGStates,
        /// shadings, transparency-group forms and optional-content properties — as indirect objects
        /// appended to <paramref name="side"/>, and the inline resources dictionary naming them. One
        /// method for a page's resources and a form's: a group's content builds its own resources
        /// here, recursively, so a form nested in a form is written as a form is.
        /// </summary>
        private PdfDictionary BuildResources(ref int nextObjectNumber, bool compress,
            IReadOnlyDictionary<string, int> embeddedFontObjNums, List<(int, PdfObject)> side, ImageObjectShare share)
        {
            // --- Font objects ---
            var fontResDict = new PdfDictionary();
            foreach (var kvp in _standardFonts)
            {
                int n = nextObjectNumber++;
                var fd = new PdfDictionary();
                fd.Set(PdfName.Type, PdfName.Font);
                fd.Set(PdfName.Subtype, PdfName.Type1);
                fd.Set(PdfName.BaseFont, new PdfName(kvp.Value));
                fd.Set(PdfName.Encoding, PdfName.WinAnsiEncoding);
                fd.ObjectNumber = n;
                fd.GenerationNumber = 0;
                side.Add((n, fd));
                fontResDict.Set(new PdfName(kvp.Key), new PdfReference(n, 0));
            }

            // --- Embedded composite fonts (document-level objects, referenced here) ---
            foreach (var resName in _externalFonts)
            {
                if (embeddedFontObjNums != null && embeddedFontObjNums.TryGetValue(resName, out int objNum))
                    fontResDict.Set(new PdfName(resName), new PdfReference(objNum, 0));
            }

            // --- Image XObject streams ---
            var xobjResDict = new PdfDictionary();
            foreach (var img in _images)
            {
                ImageObjectShare.Key key = img.ShareKey();
                if (share != null && share.TryGet(key, out int written))
                {
                    xobjResDict.Set(new PdfName(img.ResourceName), new PdfReference(written, 0));
                    continue;
                }
                byte[] data = img.EncodeData ? FlateEncodeFilter.Encode(img.Data) : img.Data;
                byte[] maskData = img.SoftMaskRaw != null ? FlateEncodeFilter.Encode(img.SoftMaskRaw) : null;

                int n = nextObjectNumber++;
                var imgStream = new PdfStream();
                imgStream.Set(PdfName.Type, PdfName.XObject);
                imgStream.Set(PdfName.Subtype, PdfName.Image);
                imgStream.Set(PdfName.Width, new PdfNumber(img.Width));
                imgStream.Set(PdfName.Height, new PdfNumber(img.Height));
                // A JPEG 2000 image may leave both out (ISO 32000-2 §7.4.9: the codestream's
                // depth applies; without /ColorSpace the file's own colour space does) and may
                // ask for its opacity channel with /SMaskInData.
                if (img.BitsPerComponent > 0)
                    imgStream.Set(PdfName.BitsPerComponent, new PdfNumber(img.BitsPerComponent));
                if (img.ColorSpace != null)
                    imgStream.Set(PdfName.ColorSpace, new PdfName(img.ColorSpace));
                if (img.Filter != null)
                    imgStream.Set(PdfName.Filter, new PdfName(img.Filter));
                if (img.SMaskInData > 0)
                    imgStream.Set(PdfName.SMaskInData, new PdfNumber(img.SMaskInData));
                if (img.Decode != null && img.Decode.Length > 0)
                {
                    var dec = new PdfArray();
                    foreach (double d in img.Decode) dec.Add(new PdfNumber(d));
                    imgStream.Set(PdfName.Decode, dec);
                }

                // The soft mask is its own image XObject — a DeviceGray one the same size as the
                // colour image — referenced from it. Its object number has to be allocated before
                // the /SMask entry is written, which is why it is emitted first.
                if (maskData != null)
                {
                    int maskNum = nextObjectNumber++;
                    var mask = new PdfStream();
                    mask.Set(PdfName.Type, PdfName.XObject);
                    mask.Set(PdfName.Subtype, PdfName.Image);
                    mask.Set(PdfName.Width, new PdfNumber(img.Width));
                    mask.Set(PdfName.Height, new PdfNumber(img.Height));
                    mask.Set(PdfName.BitsPerComponent, new PdfNumber(8));
                    mask.Set(PdfName.ColorSpace, new PdfName(PdfColorSpaceNames.DeviceGray));
                    mask.Set(PdfName.Filter, PdfName.FlateDecode);
                    mask.RawData = maskData;
                    mask.Set(PdfName.Length, new PdfNumber(maskData.Length));
                    mask.ObjectNumber = maskNum;
                    mask.GenerationNumber = 0;
                    side.Add((maskNum, mask));
                    imgStream.Set(PdfName.SMask, new PdfReference(maskNum, 0));
                }

                imgStream.RawData = data;
                imgStream.Set(PdfName.Length, new PdfNumber(data.Length));
                imgStream.ObjectNumber = n;
                imgStream.GenerationNumber = 0;
                side.Add((n, imgStream));
                xobjResDict.Set(new PdfName(img.ResourceName), new PdfReference(n, 0));
                share?.Add(key, n);
            }

            // --- ExtGState objects ---
            var gsResDict = new PdfDictionary();
            foreach (var kvp in _extGStates)
            {
                int n = nextObjectNumber++;
                kvp.Value.ObjectNumber = n;
                kvp.Value.GenerationNumber = 0;
                side.Add((n, kvp.Value));
                gsResDict.Set(new PdfName(kvp.Key), new PdfReference(n, 0));
            }

            // --- Shading objects ---
            var shadingResDict = new PdfDictionary();
            foreach (var kvp in _shadings)
            {
                int n = nextObjectNumber++;
                kvp.Value.ObjectNumber = n;
                kvp.Value.GenerationNumber = 0;
                side.Add((n, kvp.Value));
                shadingResDict.Set(new PdfName(kvp.Key), new PdfReference(n, 0));
            }

            // --- Transparency-group form XObjects (beside the images, under /XObject) ---
            foreach (FormEntry form in _forms)
            {
                PdfDictionary formResources = form.Content.BuildResources(ref nextObjectNumber, compress, embeddedFontObjNums, side, share);
                int n = nextObjectNumber++;
                var stream = new PdfStream();
                stream.Set(PdfName.Type, PdfName.XObject);
                stream.Set(PdfName.Subtype, PdfName.Form);
                var bbox = new PdfArray();
                bbox.Add(new PdfNumber(form.X0)); bbox.Add(new PdfNumber(form.Y0));
                bbox.Add(new PdfNumber(form.X1)); bbox.Add(new PdfNumber(form.Y1));
                stream.Set(PdfName.BBox, bbox);
                var group = new PdfDictionary();
                group.Set(PdfName.S, PdfName.Transparency);
                group.Set(PdfName.I, PdfBoolean.True);   // isolated: composited against a transparent backdrop, as CSS composites the element
                stream.Set(PdfName.Group, group);
                stream.Set(PdfName.Resources, formResources);
                byte[] bytes = form.Content.Build(compress);
                stream.RawData = bytes;
                stream.Set(PdfName.Length, new PdfNumber(bytes.Length));
                if (compress) stream.Set(PdfName.Filter, PdfName.FlateDecode);
                stream.ObjectNumber = n;
                stream.GenerationNumber = 0;
                side.Add((n, stream));
                xobjResDict.Set(new PdfName(form.Name), new PdfReference(n, 0));
            }

            // --- Resources dictionary ---
            var resources = new PdfDictionary();
            if (_standardFonts.Count > 0 || _externalFonts.Count > 0)
                resources.Set(PdfName.Font, fontResDict);
            if (_images.Count > 0 || _forms.Count > 0)
                resources.Set(PdfName.XObject, xobjResDict);
            if (_extGStates.Count > 0)
                resources.Set(PdfName.ExtGState, gsResDict);
            if (_shadings.Count > 0)
                resources.Set(PdfName.Shading, shadingResDict);

            // §14.6.2: a BDC naming its property list needs that name in /Properties. Only /OC
            // sequences use it — the other two emitters inline their dictionaries — so the category
            // is absent from a page that has none, which is what keeps an ordinary page's resources
            // unchanged.
            if (_optionalContent.Count > 0)
            {
                var propsResDict = new PdfDictionary();
                foreach (KeyValuePair<string, PdfReference> entry in _optionalContent)
                    propsResDict.Set(new PdfName(entry.Key), entry.Value);
                resources.Set(PdfName.Properties, propsResDict);
            }

            return resources;
        }

        /// <summary>A transparency-group form registered by <see cref="AddTransparencyGroup"/>.</summary>
        private sealed class FormEntry
        {
            public string Name;
            public PdfContentStreamBuilder Content;
            public double X0, Y0, X1, Y1;
        }

        // ------------------------------------------------------------------
        // Private types
        // ------------------------------------------------------------------

        private sealed class ImageEntry
        {
            public string ResourceName;
            public byte[] Data;
            public int Width;
            public int Height;
            public string Filter;
            public string ColorSpace;
            public int BitsPerComponent;
            /// <summary>/SMaskInData for a JPEG 2000 image (0 = absent).</summary>
            public int SMaskInData;

            /// <summary>Explicit <c>/Decode</c> array, or null for the colour space's default.</summary>
            public double[] Decode;

            /// <summary>Whether <see cref="Data"/> is raw samples the writer deflates (the caller passed no filter).</summary>
            public bool EncodeData;

            /// <summary>Raw 8-bit greyscale alpha, the caller's own array, deflated when written; null when fully opaque.</summary>
            public byte[] SoftMaskRaw;

            /// <summary>What makes this registration the same image as another (<see cref="ImageObjectShare"/>).</summary>
            public ImageObjectShare.Key ShareKey()
                => new ImageObjectShare.Key(Data, SoftMaskRaw, EncodeData, Width, Height, Filter, ColorSpace,
                    BitsPerComponent, SMaskInData, Decode);
        }

        private static bool IsFullyOpaque(byte[] mask)
        {
            foreach (byte b in mask) if (b != 255) return false;
            return true;
        }
    }
}

