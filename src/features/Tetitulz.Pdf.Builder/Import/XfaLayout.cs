// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>How <see cref="XfaLayout.Read"/> materialises the form.</summary>
    public sealed class XfaLayoutOptions
    {
        /// <summary>
        /// Whether a field the data left empty is still drawn as its box and caption (the default:
        /// a form is its boxes, filled or not). Off, an empty field is left out.
        /// </summary>
        public bool DrawEmptyFields { get; set; } = true;
    }

    /// <summary>What <see cref="XfaLayout.Read"/> produced.</summary>
    public sealed class XfaLayoutResult
    {
        internal XfaLayoutResult(Document document, IReadOnlyList<string> approximations, int pageAreasUsed, bool hasData)
        {
            Document = document; Approximations = approximations; PageAreasUsed = pageAreasUsed; HasData = hasData;
        }

        /// <summary>The form's pages as a document, which every Builder exporter writes.</summary>
        public Document Document { get; }

        /// <summary>
        /// Every way the layout departs from what an XFA processor would show, each once with a
        /// count — a script not run, a left-to-right flow laid out top to bottom, a prototype not
        /// resolved. Empty when the form used nothing the mapping does not carry.
        /// </summary>
        public IReadOnlyList<string> Approximations { get; }

        /// <summary>How many page areas the layout started a section for (page breaks included).</summary>
        public int PageAreasUsed { get; }

        /// <summary>Whether a <c>datasets</c> packet was found and bound; without one the template's own values show.</summary>
        public bool HasData { get; }

        /// <summary>Data elements whose names the layout read to bind by name — each element's once: the evidence the index is live.</summary>
        internal long DataChildrenExamined { get; set; }
    }

    /// <summary>
    /// Materialises a <b>dynamic</b> XFA form's designed layout — pages, boxes, captions, values —
    /// as a Builder <see cref="Document"/>: the template packet laid out and the datasets packet
    /// bound into it, which every exporter then writes as PDF, DOCX, HTML or ODT.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is.</b> The second half of dynamic XFA. A
    /// dynamic form has no pages until a processor lays its <c>template</c> packet out around its
    /// <c>datasets</c>; this is that processor's layout, flat: ISO 32000-2 Annex K says whether such
    /// pages "are materialized as PDF pages is implementation dependent", and this materialises
    /// them as content — boxes with their captions and values — not as interactive widgets, which
    /// Builder's model has no element for. <see cref="XfaContent"/> remains the grammar-free half:
    /// the data as a readable table.</para>
    /// <para><b>The mapping.</b> A <c>pageArea</c> is a section with the medium's page size and the
    /// first <c>contentArea</c> as its margins; the page area's own draws and fields repeat on every
    /// page it produces. A subform with <c>layout="position"</c> (the default) is a box
    /// (<see cref="BoxStart"/>) of its width and height, its children placed inside it by their
    /// <c>x</c>/<c>y</c> (<see cref="AbsolutePlacement.ContainingBox"/>); one with <c>tb</c> flows
    /// its children, and Builder paginates the flow; <c>table</c> and <c>row</c> become a
    /// <see cref="Table"/>. A field is its caption (at the <c>placement</c> the template says,
    /// within the <c>reserve</c>) beside its value in a bordered box when its interface draws one;
    /// a draw is its text, image, rectangle or line. An <c>occur</c> repeats a subform once per
    /// bound data instance; a <c>breakBefore</c>/<c>breakAfter</c> to a page area starts a section.
    /// Measurements and colours go through <see cref="XfaMeasure"/>; fonts map the Designer
    /// defaults (Myriad Pro, Arial → Helvetica; Times New Roman → Times; Courier New → Courier) and
    /// pass any other family through to Builder's resolver.</para>
    /// <para><b>Data binding</b> follows XFA's normal binding: a named subform binds to the data
    /// elements of its name under the parent's data node, one instance each; an unnamed subform or
    /// an <c>area</c> is transparent; a field takes the text of the element of its name;
    /// <c>bind match="none"</c> takes the template's own value; a <c>bind ref</c> is honoured for
    /// a dotted path (with <c>[n]</c> indexes, <c>$</c>, <c>$record</c> and <c>$data</c> prefixes),
    /// and anything richer is reported.</para>
    /// <para><b>Laid out as chapters 7, 8, 17 and 27 have it</b>: <c>lr-tb</c>/<c>rl-tb</c> flows in
    /// lines; <c>para vAlign</c> in every box that has a height; lines, arcs (a partial one as its own
    /// path) and rectangles with their own edge and fill; <c>anchorType</c> and quarter-turn
    /// <c>rotate</c>, in a positioned container and in a flow; XHTML rich text as styled
    /// paragraphs.</para>
    /// <para><b>What is reported, not done.</b> Scripts (<c>calculate</c>, <c>validate</c>,
    /// <c>event</c>) do not run — a form whose layout depends on them shows its initial state; a
    /// left-to-right flow or a turned container the line or turn cannot be built for (a child of
    /// unknown width, a flowing child turned, a page-area break) lays out top to bottom or unturned; a
    /// barcode is drawn as its value and a signature field as an empty box; rich text's properties
    /// the reader does not apply (tab stops, kerning) are named; and a <c>use</c>/<c>usehref</c>
    /// prototype that cannot be resolved (another document, a SOM expression naming several nodes or
    /// another model, a loop) — each counted in <see cref="XfaLayoutResult.Approximations"/>.
    /// Nothing is silent.</para>
    /// <para><b>Hostile input.</b> Template depth is bounded (<see cref="XfaTemplate.MaxDepth"/>),
    /// occurrences are bounded by <see cref="MaxInstancesPerSubform"/> and the whole layout by
    /// <see cref="MaxBlocks"/>, so a template that repeats itself unboundedly cannot run away.</para>
    /// </remarks>
    public static class XfaLayout
    {
        /// <summary>Instances one repeating subform may produce, whatever the data says.</summary>
        internal const int MaxInstancesPerSubform = 10_000;

        /// <summary>Blocks one layout may emit, and repeating-subform instances it may enter, before it stops and says so.</summary>
        internal const int DefaultMaxBlocks = 200_000;

        /// <summary>
        /// The block ceiling — <see cref="DefaultMaxBlocks"/>, settable so a test reaches it with a
        /// template of ordinary size; the mutation that removes the check is then one a test sees.
        /// </summary>
        internal static int MaxBlocks { get; set; } = DefaultMaxBlocks;

        /// <summary>Why the last call produced nothing, or <c>null</c> after one that did. <c>[ThreadStatic]</c>; cleared on every call.</summary>
        public static string? LastError { get => _lastError; private set => _lastError = value; }

        [ThreadStatic] private static string? _lastError;

        /// <summary>
        /// Lays the form out, or returns <c>null</c> — with <see cref="LastError"/> saying why — when
        /// the PDF carries no readable XFA template.
        /// </summary>
        /// <param name="pdf">The PDF. Not disposed.</param>
        /// <param name="options">Presentation policy; null uses the defaults.</param>
        public static XfaLayoutResult? Read(Stream pdf, XfaLayoutOptions? options = null)
        {
            if (pdf == null) throw new ArgumentNullException(nameof(pdf));
            LastError = null;
            options ??= new XfaLayoutOptions();

            byte[]? templateBytes, datasetsBytes;
            try
            {
                using var doc = PdfDocument.Open(pdf);
                PdfDictionary? acroForm = doc.AcroForm?.Dictionary;
                if (!XfaPacket.IsPresent(acroForm)) { LastError = "the document carries no /AcroForm /XFA entry"; return null; }
                XfaPacket? packet = XfaPacket.Read(acroForm, doc);
                if (packet == null) { LastError = "the /XFA entry did not resolve to any packet stream"; return null; }
                if (!packet.IsPackaged)
                {
                    LastError = "the /XFA entry is a single XDP stream rather than named packets, so its template cannot be addressed without splitting the XDP";
                    return null;
                }
                PdfStream? templateStream = packet.GetPacketStream(XfaPacket.TemplatePacket);
                PdfStream? datasetsStream = packet.GetPacketStream(XfaPacket.DatasetsPacket);
                string refused = "";
                templateBytes = templateStream == null ? null : XfaPacket.BoundedBytes(templateStream, out refused);
                if (templateStream != null && templateBytes == null) { LastError = "the template packet could not be read: " + refused; return null; }
                datasetsBytes = datasetsStream == null ? null : XfaPacket.BoundedBytes(datasetsStream, out refused);
                if (datasetsStream != null && datasetsBytes == null) { LastError = "the datasets packet could not be read: " + refused; return null; }
            }
            catch (PdfException ex) { LastError = ex.Message; return null; }

            if (templateBytes == null) { LastError = "the /XFA array carries no 'template' packet"; return null; }
            XfaTemplate? template = XfaTemplate.Parse(templateBytes);
            if (template == null) { LastError = "the template packet is not well-formed XML, holds no template, or is past what a form holds"; return null; }

            XElement? data = datasetsBytes != null ? XfaDatasets.ReadDataRoot(datasetsBytes) : null;

            var mapper = new Mapper(template, data, options);
            Document document = mapper.Run();
            return new XfaLayoutResult(document, mapper.Approximations(), mapper.PageAreasUsed, data != null)
                { DataChildrenExamined = mapper.DataChildrenExamined };
        }

        /// <summary>Lays out a template already in hand — the test seam, and the packet route a host that split an XDP itself can take.</summary>
        internal static XfaLayoutResult Layout(XfaTemplate template, XElement? data, XfaLayoutOptions? options = null)
        {
            var mapper = new Mapper(template, data, options ?? new XfaLayoutOptions());
            return new XfaLayoutResult(mapper.Run(), mapper.Approximations(), mapper.PageAreasUsed, data != null)
                { DataChildrenExamined = mapper.DataChildrenExamined };
        }

        /// <summary>
        /// An arc as a path in its <paramref name="w"/> × <paramref name="h"/> box: of the ellipse the
        /// box holds, from <paramref name="start"/> by <paramref name="sweep"/> degrees — XFA 3.3
        /// chapter 17's arc, its angles from the positive x axis and counter-clockwise as the page is
        /// seen (clockwise for a negative sweep) — in cubic Béziers of at most a quarter turn each
        /// (k = 4/3 · tan(θ/4) of each radius along the tangent). Left open, so its fill closes it with
        /// its chord — the specification's "red-filled semicircle" for a 180° sweep —
        /// and its stroke draws the curve alone; a hidden edge strokes nothing.
        /// </summary>
        internal static VectorDrawing ArcDrawing(double w, double h, double start, double sweep, XfaBorder stroke)
        {
            var drawing = new VectorDrawing
            {
                StrokeColor = stroke.Visible ? stroke.Color : null,
                StrokeWidthPt = Math.Max(0, stroke.Thickness),
                FillColor = stroke.Fill,
            };
            double rx = w / 2, ry = h / 2;
            sweep = Math.Max(-360, Math.Min(360, sweep));
            int segments = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(Math.Abs(sweep) / 90 - 1e-9)));
            double step = sweep / segments * Math.PI / 180, a = start * Math.PI / 180;
            double k = 4.0 / 3 * Math.Tan(step / 4);
            // y runs down in the box, so counter-clockwise on the page is -sin.
            double px = rx + rx * Math.Cos(a), py = ry - ry * Math.Sin(a);
            drawing.Path.Add(PathCommand.MoveTo(px, py));
            for (int i = 0; i < segments; i++)
            {
                double b = a + step;
                double qx = rx + rx * Math.Cos(b), qy = ry - ry * Math.Sin(b);
                // Along the tangent (-rx sin t, -ry cos t): forward from the start, back from the end.
                drawing.Path.Add(PathCommand.CurveTo(px - k * rx * Math.Sin(a), py - k * ry * Math.Cos(a),
                                                     qx + k * rx * Math.Sin(b), qy + k * ry * Math.Cos(b), qx, qy));
                a = b; px = qx; py = qy;
            }
            return drawing;
        }

        // ================================================================== the mapper

        private sealed class Mapper
        {
            private readonly XfaTemplate _template;
            private readonly XElement? _data;
            private readonly XfaLayoutOptions _options;
            private readonly Document _doc = new Document();
            private readonly Dictionary<string, int> _notes = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<XfaPageArea> _pageAreas;
            private Section? _section;
            private XfaPageArea? _page;
            private int _blocks;
            private readonly Dictionary<XElement, Dictionary<string, List<XElement>>> _childrenByName =
                new Dictionary<XElement, Dictionary<string, List<XElement>>>();

            public int PageAreasUsed { get; private set; }

            public long DataChildrenExamined { get; private set; }

            public Mapper(XfaTemplate template, XElement? data, XfaLayoutOptions options)
            {
                _template = template; _data = data; _options = options;
                _pageAreas = FindPageAreas(template.Root);
                if (_pageAreas.Count == 0)
                {
                    Note("the template declares no page area; a Letter page with no margins is assumed");
                    _pageAreas.Add(new XfaPageArea { ContentAreas = { new XfaContentArea { X = 0, Y = 0, W = 612, H = 792 } } });
                }
            }

            public Document Run()
            {
                if (_template.UnresolvedPrototypes > 0)
                    Note(_template.PrototypeBudgetExhausted
                        ? "a use/usehref prototype reference is not resolved (another document, an unsupported SOM expression, a missing or mistyped target, a loop, or the expansion ceiling); the element shows what it declares itself"
                        : "a use/usehref prototype reference is not resolved (another document, an unsupported SOM expression, a missing or mistyped target, or a loop); the element shows what it declares itself",
                        _template.UnresolvedPrototypes);
                if (_template.DepthRefusals > 0)
                    Note("an element nested deeper than the template reader follows was left out", _template.DepthRefusals);
                if (_template.Scripts > 0)
                    Note("a script (event, calculate or validate) is not run; the form shows its initial state", _template.Scripts);
                foreach (string unapplied in _template.FormattingUnapplied.OrderBy(s => s, StringComparer.Ordinal))
                    Note("text formatting: " + unapplied + " is not applied");

                StartSection(_pageAreas[0]);
                XfaSubform root = _template.Root;

                // The root subform binds like any other: a named root (Designer's `form1`) to the
                // data element of its name under <xfa:data>, an unnamed one to the data root itself.
                XElement? rootData = _data;
                if (_data != null && root.Name.Length > 0 && root.BindMatch != "none")
                {
                    var named = ChildrenNamed(_data, root.Name);
                    rootData = named.Count > 0 ? named[0] : _data;
                }
                if (root.BindMatch == "none") rootData = null;

                if (root.Layout == "position")
                {
                    // Everything is placed by its x/y against the first content area.
                    XfaContentArea area = _page!.ContentAreas[0];
                    EmitPositioned(root, rootData, null, area.X + root.X, area.Y + root.Y);
                }
                else
                {
                    EmitFlow(root, rootData, 0);
                }
                return _doc;
            }

            public IReadOnlyList<string> Approximations()
                => _notes.OrderBy(n => n.Key, StringComparer.Ordinal)
                         .Select(n => n.Value > 1 ? n.Key + " (×" + n.Value.ToString(CultureInfo.InvariantCulture) + ")" : n.Key)
                         .ToList();

            private void Note(string what, int count = 1)
            {
                _notes.TryGetValue(what, out int n);
                _notes[what] = n + count;
            }

            // ---------------------------------------------------------------- pages

            private static List<XfaPageArea> FindPageAreas(XfaSubform root)
            {
                if (root.PageSet != null) return new List<XfaPageArea>(root.PageSet.PageAreas);
                foreach (XfaBoxNode c in root.Children)
                    if (c is XfaSubform s && s.PageSet != null) return new List<XfaPageArea>(s.PageSet.PageAreas);
                return new List<XfaPageArea>();
            }

            private void StartSection(XfaPageArea page)
            {
                // Charged and tested like every block: a repeating subform whose child breaks to a page
                // area starts a section an instance, each a copy of the area's content areas and
                // decorations, and nothing tested the ceiling between them — ten thousand instances of a
                // page area of ten thousand content areas were 10⁸ objects before any export.
                if (_section != null && _blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return; }
                _page = page;
                XfaContentArea area = page.ContentAreas[0];
                var section = new Section
                {
                    PageWidthPt = page.PageWidth,
                    PageHeightPt = page.PageHeight,
                    MarginLeft = Clamp(area.X, 0, page.PageWidth),
                    MarginTop = Clamp(area.Y, 0, page.PageHeight),
                    MarginRight = Clamp(page.PageWidth - area.X - area.W, 0, page.PageWidth),
                    MarginBottom = Clamp(page.PageHeight - area.Y - area.H, 0, page.PageHeight),
                };

                // XFA 3.3 chapter 8, "Flowing Between ContentArea Objects": a full content area
                // passes the flow to its next sibling, and the last to the next page — the section's
                // regions, in document order. One area is the margins alone, as before.
                if (page.ContentAreas.Count > 1)
                {
                    _blocks += page.ContentAreas.Count;
                    foreach (XfaContentArea a in page.ContentAreas)
                        section.ContentAreas.Add(new ContentArea
                        {
                            LeftPt = Clamp(a.X, 0, page.PageWidth),
                            TopPt = Clamp(a.Y, 0, page.PageHeight),
                            WidthPt = Clamp(a.W, 0, page.PageWidth),
                            HeightPt = Clamp(a.H, 0, page.PageHeight),
                        });
                }
                _doc.Sections.Add(section);
                _section = section;
                _blocks++;
                PageAreasUsed++;

                // The page's own marks repeat on every page the area produces (EveryPage), in page
                // coordinates — a page area's x/y are measured from the page's corner.
                foreach (XfaBoxNode deco in page.Decorations)
                {
                    if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return; }
                    EmitPositionedNode(deco, null, null, 0, 0, everyPage: true);
                }
            }

            private XfaPageArea PageAreaFor(XfaBreak brk)
            {
                if (brk.Target.Length > 0)
                {
                    // By name, the first of a name winning, as the scan of every page area at every
                    // break did — page areas × breaks.
                    if (_pageAreasByName == null)
                    {
                        _pageAreasByName = new Dictionary<string, XfaPageArea>(StringComparer.Ordinal);
                        foreach (XfaPageArea p in _pageAreas)
                            if (!_pageAreasByName.ContainsKey(p.Name)) _pageAreasByName.Add(p.Name, p);
                    }
                    if (_pageAreasByName.TryGetValue(brk.Target, out XfaPageArea? named)) return named;
                }
                return _page ?? _pageAreas[0];
            }

            private Dictionary<string, XfaPageArea>? _pageAreasByName;

            private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

            /// <summary>
            /// Whether a left-to-right flow would put two children on one line — the case laying it out
            /// top to bottom changes. A flow whose every child is wider than what its line has left
            /// (Designer's full-width sections) is one child per line either way; a child or a container
            /// whose width cannot be known cannot be judged, and counts as sharing; a flow of no given width
            /// is unbounded, so any two children share. A right-to-left flow's child narrower than its line
            /// is set at the line's right, which top to bottom is not, so it counts too.
            /// </summary>
            private static bool SharesALine(XfaSubform flow)
            {
                double width = flow.W ?? double.PositiveInfinity;
                double inner = width - (flow.Margin?.Left ?? 0) - (flow.Margin?.Right ?? 0);
                double line = 0;
                foreach (XfaBoxNode child in flow.Children)
                {
                    if (!child.IsShown) continue;
                    if (!(ExtentWidth(child, 0) is double w)) return true;
                    if (flow.Layout == "rl-tb" && w < inner - 0.01) return true;
                    if (line > 0 && line + w <= width + 0.01) return true;
                    // A subform that may repeat shares a line with its own next instance.
                    if (child is XfaSubform { OccurMax: not 1 } && w + w <= width + 0.01) return true;
                    line = w;
                }
                return false;
            }

            /// <summary>
            /// A left-to-right flow laid out as XFA's chapter 8 has it ("Flowing Layout for Containers"):
            /// its children left to right — right to left for <c>rl-tb</c> — wrapping to a new line where
            /// the next does not fit the flow's width, each line as tall as its tallest child. A line is a
            /// kept-together box of that height, and each child is placed in it at its running offset, as
            /// a positioned subform's children are — so a line of Designer's positioned subforms keeps
            /// their own layouts. A child whose height is not known before it is laid out — a flowing
            /// subform, whose height is its own layout's, or a field of no given height — alone on a line is
            /// laid out there as the flow lays out any child, since a line of one is that child; beside others,
            /// or narrower than a right-to-left line, its line is floated boxes. A turned child of
            /// known extent stands in its line at its turned extent, and a child's anchor point is ignored, as a
            /// flowed child's is. False, having emitted nothing, where a line cannot be built: the
            /// flow's width or a child's width unknown, a turn not a multiple of 90 degrees, a turned child of
            /// unknown extent or a turned flowing subform, or a child of unknown height that breaks to a page area
            /// (which a line does not carry).
            /// </summary>
            private bool TryEmitLines(XfaSubform flow, XElement? data, int depth)
            {
                if (!(flow.W is double declared)) return false;
                double width = declared - (flow.Margin?.Left ?? 0) - (flow.Margin?.Right ?? 0);
                if (width <= 0) return false;
                var items = new List<(XfaBoxNode Node, XElement? Data, double W, double? H)>();
                // A turned child's turn, by node: placed in its line at its turned extent.
                var turned = new Dictionary<XfaBoxNode, FlowedTurn>();
                foreach (XfaBoxNode child in flow.Children)
                {
                    if (!child.IsShown) continue;
                    // A flowed child's anchor point is ignored (chapter 8); a turned one needs its extent known,
                    // and a flowing subform turned needs a box of its own (TryEmitTurnedInFlow), which a line is not.
                    if (child.Rotate % 360 != 0)
                    {
                        if (!(FlowTurn(child) is FlowedTurn t) || child is XfaSubform { Layout: not "position" }) return false;
                        turned[child] = t;
                        IEnumerable<XElement?> copies = child is XfaSubform rs ? Instances(rs, data) : new[] { data };
                        foreach (XElement? instance in copies) items.Add((child, instance, t.TurnedW, t.TurnedH));
                        continue;
                    }
                    if (!(ExtentWidth(child, 0) is double w)) return false;
                    double? h = ExtentHeight(child, 0);
                    if (child is XfaSubform s)
                    {
                        if (s.Layout != "position") h = null;
                        foreach (XElement? instance in Instances(s, data)) items.Add((s, instance, w, h));
                    }
                    else items.Add((child, data, w, h));
                }

                var lines = new List<List<(XfaBoxNode Node, XElement? Data, double W, double? H)>>();
                double used = 0;
                foreach (var item in items)
                {
                    if (lines.Count == 0 || (used > 0 && used + item.W > width + 0.01))
                    {
                        lines.Add(new List<(XfaBoxNode, XElement?, double, double?)>());
                        used = 0;
                    }
                    lines[lines.Count - 1].Add(item);
                    used += item.W;
                }

                bool rightToLeft = flow.Layout == "rl-tb";
                foreach (var line in lines)
                    foreach (var item in line)
                        if (item.H == null && (item.Node is XfaSubform { BreakBefore.TargetType: "pageArea" }
                                               || item.Node is XfaSubform { BreakAfter.TargetType: "pageArea" }))
                            return false;

                // A line of floats is open, which the next line, or what follows the flow, starts below.
                bool floats = false;
                foreach (var line in lines)
                {
                    // A line holding a child whose height its own layout decides, beside others or at the right of
                    // a right-to-left line: each child a box of its
                    // width floated to the line's start side, its content laid out in it as the flow lays it out —
                    // the line as tall as its tallest child, which nothing need measure, the next line below it.
                    if (line.Exists(i => i.H == null) && (line.Count > 1 || (rightToLeft && line[0].W < width - 0.01)))
                    {
                        for (int i = 0; i < line.Count; i++)
                        {
                            Add(new BoxStart { Float = rightToLeft ? FloatSide.Right : FloatSide.Left, FloatWidth = line[i].W, ClearFloats = i == 0 });
                            EmitFlowed(line[i].Node, line[i].Data, depth);
                            Add(new BoxEnd());
                        }
                        floats = true;
                        if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return true; }
                        continue;
                    }
                    if (line[0].H == null)
                    {
                        // Alone on its line: laid out in the flow, as top to bottom.
                        if (floats) { Add(new BoxStart { ClearFloats = true }); Add(new BoxEnd()); floats = false; }
                        EmitFlowed(line[0].Node, line[0].Data, depth);
                        if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return true; }
                        continue;
                    }
                    double height = line.Max(i => i.H!.Value);
                    var box = new BoxStart
                    {
                        WidthPt = width,
                        MinHeightPt = Math.Max(0, Math.Min(height, TallestAreaHeight() - 0.5)),
                        KeepTogether = true,
                        ClearFloats = floats,
                    };
                    floats = false;
                    // A line is kept together and no taller than an area, so its minimum height holds wherever
                    // it lands — no height spacer, which a box that may break needs.
                    Add(box);
                    double x = 0;
                    foreach (var item in line)
                    {
                        double left = rightToLeft ? width - x - item.W : x;
                        // The node's own x and y are a flowed child's nothing; its place is the line's — a turned
                        // one's top-left corner where its turn carries it in its turned extent.
                        double ax = 0, ay = 0;
                        if (turned.TryGetValue(item.Node, out FlowedTurn t)) (ax, ay) = (t.AnchorX, t.AnchorY);
                        EmitPositionedNode(item.Node, item.Data, box, left + ax - item.Node.X, ay - item.Node.Y, everyPage: false, flowed: true);
                        x += item.W;
                    }
                    Add(new BoxEnd());
                    if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return true; }
                }
                // What follows the flow starts below its last line of floats: an empty box that clears them.
                if (floats) { Add(new BoxStart { ClearFloats = true }); Add(new BoxEnd()); }
                return true;
            }

            /// <summary>A flowed child laid out as a top-to-bottom flow lays it out: a subform in its own layout, a field or a draw as a block.</summary>
            private void EmitFlowed(XfaBoxNode node, XElement? data, int depth)
            {
                if (node is XfaSubform s) EmitSubformInFlow(s, data, depth + 1);
                else if (node is XfaField f) EmitFieldBlock(f, data);
                else if (node is XfaDraw d) EmitDrawBlock(d);
            }

            /// <summary>
            /// A node's height: its own, or — a positioned subform of no given height growing to its
            /// content — the furthest down a child reaches; <c>null</c> when that is not known either.
            /// </summary>
            private static double? ExtentHeight(XfaBoxNode node, int depth)
            {
                if (node.H is double h) return h;
                if (!(node is XfaSubform s) || s.Layout != "position" || depth > XfaTemplate.MaxDepth) return null;
                double bottom = 0;
                foreach (XfaBoxNode child in s.Children)
                {
                    if (!child.IsShown) continue;
                    if (!(ExtentHeight(child, depth + 1) is double ch)) return null;
                    bottom = Math.Max(bottom, child.Y + ch);
                }
                return bottom;
            }

            /// <summary>
            /// A node's width: its own, or — a subform of no given width growing to its content, its
            /// dimensions "determined from the inside-out" (XFA 3.3 chapter 8, "Growable Containers") —
            /// for a positioned one the furthest right a child reaches, for a top-to-bottom flow its widest
            /// child and its margins (each child set at the flow's start, the flow's box padded by them);
            /// <c>null</c> when that is not known either.
            /// </summary>
            private static double? ExtentWidth(XfaBoxNode node, int depth)
            {
                if (node.W is double w) return w;
                if (node is XfaSubform { Layout: "tb" } flow && depth <= XfaTemplate.MaxDepth)
                {
                    double widest = 0;
                    foreach (XfaBoxNode child in flow.Children)
                    {
                        if (!child.IsShown) continue;
                        if (!(ExtentWidth(child, depth + 1) is double cw)) return null;
                        widest = Math.Max(widest, cw);
                    }
                    return widest + Math.Max(0, flow.Margin?.Left ?? 0) + Math.Max(0, flow.Margin?.Right ?? 0);
                }
                if (!(node is XfaSubform s) || s.Layout != "position" || depth > XfaTemplate.MaxDepth) return null;
                double right = 0;
                foreach (XfaBoxNode child in s.Children)
                {
                    if (!child.IsShown) continue;
                    if (!(ExtentWidth(child, depth + 1) is double cw)) return null;
                    right = Math.Max(right, child.X + cw);
                }
                return right;
            }

            // ---------------------------------------------------------------- the flow

            private void EmitFlow(XfaSubform subform, XElement? data, int depth)
            {
                if (depth > XfaTemplate.MaxDepth) return;
                if (subform.Layout is "lr-tb" or "rl-tb" && SharesALine(subform))
                {
                    if (TryEmitLines(subform, data, depth)) return;
                    Note("a left-to-right flow (layout lr-tb/rl-tb) is laid out top to bottom");
                }

                foreach (XfaBoxNode child in subform.Children)
                {
                    if (!child.IsShown) continue;
                    if (child.Rotate % 360 != 0)
                    {
                        if (TryEmitTurnedInFlow(child, data, depth)) { if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return; } continue; }
                        Note("a rotated container in a flow is laid out unrotated");
                    }
                    if (child is XfaSubform s)
                    {
                        if (s.BreakBefore?.TargetType == "pageArea") StartSection(PageAreaFor(s.BreakBefore));
                        foreach (XElement? instance in Instances(s, data))
                            EmitSubformInFlow(s, instance, depth + 1);
                        if (s.BreakAfter?.TargetType == "pageArea") StartSection(PageAreaFor(s.BreakAfter));
                    }
                    else if (child is XfaField f) EmitFieldBlock(f, data);
                    else if (child is XfaDraw d) EmitDrawBlock(d);
                    if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return; }
                }
            }

            /// <summary>
            /// A container turned by a quarter or half turn in a flow, laid out along its turned axes (XFA 3.3
            /// chapter 8, "Rotating Containers"): the flow gives it a kept-together box of its turned extent —
            /// its width and height swapped by a quarter turn — and it is placed in that box as a positioned
            /// node is, at the anchor its turn carries its extent from onto the box: (0, w) a quarter turn
            /// counter-clockwise, (w, h) a half, (h, 0) three quarters. A flowing subform's content is laid
            /// out in a box of its own width and height, turned about its top-left corner
            /// (<see cref="BoxStart.Transform"/>), placed at that anchor (<see cref="AbsolutePlacement.FollowingBlocks"/>).
            /// The anchor point, as a flowed child's, is ignored. False, having emitted nothing, for a turn that is not a multiple of 90 degrees, an
            /// extent not known, or a subform that breaks to a page area.
            /// </summary>
            private bool TryEmitTurnedInFlow(XfaBoxNode child, XElement? data, int depth)
            {
                if (!(FlowTurn(child) is FlowedTurn t)) return false;
                if (child is XfaSubform { BreakBefore.TargetType: "pageArea" } || child is XfaSubform { BreakAfter.TargetType: "pageArea" })
                    return false;
                var (w, h, quarter, sideways, anchorX, anchorY) = (t.W, t.H, t.Quarter, t.Sideways, t.AnchorX, t.AnchorY);
                IEnumerable<XElement?> instances = child is XfaSubform s ? Instances(s, data) : new[] { data };
                foreach (XElement? instance in instances)
                {
                    var box = new BoxStart
                    {
                        WidthPt = sideways ? h : w,
                        MinHeightPt = Math.Max(0, Math.Min(sideways ? w : h, TallestAreaHeight() - 0.5)),
                        KeepTogether = true,
                    };
                    Add(box);
                    if (child is XfaSubform { Layout: not "position" } flowing)
                        EmitTurnedFlow(flowing, instance, box, depth, w, h, quarter, anchorX, anchorY);
                    // The node's own x and y are a flowed child's nothing: its anchor is the box's corner.
                    else EmitPositionedNode(child, instance, box, anchorX - child.X, anchorY - child.Y, everyPage: false, flowed: true);
                    Add(new BoxEnd());
                }
                return true;
            }

            /// <summary>
            /// A flowed child turned by quarter turns: its own extent (<see cref="W"/> by <see cref="H"/>), the turns
            /// (0 to 3), whether a quarter turn swaps its sides, and the point its top-left corner — the point it is
            /// turned about — goes to in its turned extent's box: (0, w) a quarter turn counter-clockwise, (w, h) a
            /// half, (h, 0) three quarters.
            /// </summary>
            private readonly struct FlowedTurn
            {
                public FlowedTurn(double w, double h, int quarter)
                {
                    W = w; H = h; Quarter = quarter; Sideways = quarter % 2 == 1;
                    AnchorX = quarter switch { 1 => 0, 2 => w, 3 => h, _ => 0 };
                    AnchorY = quarter switch { 1 => w, 2 => h, _ => 0 };
                }
                public double W { get; }
                public double H { get; }
                public int Quarter { get; }
                public bool Sideways { get; }
                public double AnchorX { get; }
                public double AnchorY { get; }
                /// <summary>The turned extent's width.</summary>
                public double TurnedW => Sideways ? H : W;
                /// <summary>The turned extent's height.</summary>
                public double TurnedH => Sideways ? W : H;
            }

            /// <summary>A flowed child's turn, its extent known; <c>null</c> for a turn not a multiple of 90 degrees, or an extent not known.</summary>
            private static FlowedTurn? FlowTurn(XfaBoxNode child)
            {
                double turns = child.Rotate / 90;
                if (turns != Math.Floor(turns)) return null;
                if (!(ExtentWidth(child, 0) is double w) || !(ExtentHeight(child, 0) is double h)) return null;
                return new FlowedTurn(w, h, SaturatingCast.ToInt(((turns % 4) + 4) % 4));
            }

            /// <summary>
            /// A flowing subform turned by <paramref name="quarter"/> quarter turns counter-clockwise, in
            /// <paramref name="box"/> (its turned extent): its content laid out in the flow of a box
            /// <paramref name="w"/> by <paramref name="h"/>, the box turned about its top-left corner and
            /// placed at (<paramref name="ax"/>, <paramref name="ay"/>) in <paramref name="box"/>, where the
            /// turn carries the content onto it — a positioned container's draws and a line of an <c>lr-tb</c>
            /// flow, each placed in its own box, with the rest.
            /// </summary>
            private void EmitTurnedFlow(XfaSubform s, XElement? data, BoxStart box, int depth, double w, double h, int quarter, double ax, double ay)
            {
                List<IDocElement> children = _section!.Children;
                var anchor = new Paragraph { Placement = Place(box, ax, ay, w, everyPage: false), SpacingAfter = 0, SpacingBefore = 0 };
                Add(anchor);
                int first = children.Count;
                Add(new BoxStart
                {
                    WidthPt = w,
                    MinHeightPt = h,
                    Transform = QuarterTurn(quarter),
                    TransformOrigin = new[] { 0.0, 0, 0, 0 },
                });
                EmitSubformInFlow(s, data, depth + 1);
                Add(new BoxEnd());

                // The content rides with the placement, turned. Placed content among it — a positioned container's
                // draws, a line of an lr-tb flow — is placed against a box that opens among it, the container's own
                // or the line's, which the placement draws it with (AbsolutePlacement.CanFollow). Nothing in a flow is placed against the page.
                var content = children.GetRange(first, children.Count - first);
                children.RemoveRange(first, children.Count - first);
                anchor.Placement.FollowingBlocks.AddRange(content);
            }

            private void EmitSubformInFlow(XfaSubform s, XElement? data, int depth)
            {
                switch (s.Layout)
                {
                    case "table":
                    case "row":
                    case "rl-row":
                        EmitTable(s, data);
                        break;
                    case "tb":
                    case "lr-tb":
                    case "rl-tb":
                    {
                        bool framed = s.Border?.Visible == true || s.Border?.Fill != null || s.Margin != null || s.W.HasValue;
                        BoxStart? box = framed ? OpenBox(s) : null;
                        EmitFlow(s, data, depth);
                        if (box != null) Add(new BoxEnd());
                        break;
                    }
                    default:   // position, area, exclGroup
                    {
                        // Its placed content cannot split (XFA 3.3 chapter 8, "Content Splitting":
                        // draws and the subforms of a positioned container are kept intact), so the
                        // box goes whole to the next content area rather than leave its draws behind.
                        BoxStart box = OpenBox(s, keepTogether: true);
                        AddHeightSpacer(s, box);
                        EmitPositioned(s, data, box, 0, 0);
                        Add(new BoxEnd());
                        break;
                    }
                }
            }

            /// <summary>Opens a subform's box: its width and height, border, fill and insets.</summary>
            /// <remarks>
            /// Designer writes a page-filling subform exactly as tall as the content area; a box of
            /// that height plus a border's hairline would not fit the page it was drawn for, so the
            /// minimum height is held half a point under the content area's.
            /// </remarks>
            private BoxStart OpenBox(XfaBoxNode node, bool keepTogether = false)
            {
                double areaHeight = TallestAreaHeight();
                var box = new BoxStart
                {
                    WidthPt = Math.Max(0, node.W ?? 0),
                    MinHeightPt = Math.Max(0, Math.Min(node.H ?? 0, areaHeight - 0.5)),
                    KeepTogether = keepTogether,
                };
                ApplyBox(box, node);
                Add(box);
                return box;
            }

            private static void ApplyBox(BoxStart box, XfaBoxNode node)
            {
                if (node.Margin is XfaMargin m)
                {
                    box.PaddingTop = Math.Max(0, m.Top); box.PaddingBottom = Math.Max(0, m.Bottom);
                    box.PaddingLeft = Math.Max(0, m.Left); box.PaddingRight = Math.Max(0, m.Right);
                }
                if (node.Border is XfaBorder b)
                {
                    if (b.Visible)
                    {
                        var border = new Border(b.Thickness, b.Color);
                        box.BorderTop = border; box.BorderBottom = border; box.BorderLeft = border; box.BorderRight = border;
                    }
                    if (b.Fill != null) box.Shading = b.Fill;
                }
            }

            private void Add(IDocElement element)
            {
                _blocks++;
                _section!.Children.Add(element);
            }

            /// <summary>
            /// A rich text's first paragraph's <c>page-break-before: avoid</c> (chapter 27), before the text is added:
            /// no break between it and the block before it in the flow, which lies outside the text — a table subform
            /// kept with its next, or the box the block before closes (every flowed draw's, field's and subform's)
            /// kept whole with it. A text first in the flow, or first in a
            /// box, has no block before it there.
            /// </summary>
            private void KeepPreviousWith(List<XfaRichParagraph>? rich)
            {
                if (rich?.Find(r => r.Runs.Count > 0) is not { KeepWithPrevious: true }) return;
                var children = _section!.Children;
                if (children.Count == 0) return;
                switch (children[children.Count - 1])
                {
                    case Table t: t.KeepWithNext = true; break;
                    case BoxEnd when BoxEnd.IndexOfStart(children, children.Count - 1) is int start && start >= 0:
                        ((BoxStart)children[start]).KeepWithNext = true;
                        break;
                }
            }

            // ---------------------------------------------------------------- positioned content

            /// <summary>
            /// Places a positioned subform's children: inside <paramref name="box"/> when one is
            /// open, else on the page, at (<paramref name="ox"/> + x, <paramref name="oy"/> + y).
            /// </summary>
            private void EmitPositioned(XfaSubform s, XElement? data, BoxStart? box, double ox, double oy)
            {
                foreach (XfaBoxNode child in s.Children)
                {
                    if (!child.IsShown) continue;
                    if (child is XfaSubform inner)
                    {
                        // Each instance of a repeating subform is placed by the one offset vector the
                        // template gives it, so the instances share a place: positioned layout does
                        // not move them apart ("it is not the layout processor's job to second-guess
                        // the offset vectors", XFA 3.3 chapter 7, "Positioned Layout", p. 268).
                        foreach (XElement? instance in Instances(inner, data))
                        {
                            double ax = ox + inner.X, ay = oy + inner.Y;
                            var (x, y) = ExtentOrigin(inner, ax, ay);
                            int first = _section!.Children.Count;
                            if (!inner.IsArea && (inner.Border?.Visible == true || inner.Border?.Fill != null) && inner.W.HasValue && inner.H.HasValue)
                                Add(Frame(x, y, inner.W.Value, inner.H.Value, inner.Border!, box, everyPage: false));
                            if (inner.Layout is "table" or "row" or "rl-row")
                            {
                                // The table at its offset vector, its rows laid out inside it: a
                                // placed anchor of no height, the table after it.
                                if (BuildTable(inner, instance) is Table t)
                                {
                                    var anchor = new Paragraph { Placement = Place(box, x, y, inner.W ?? 0, everyPage: false), SpacingAfter = 0, SpacingBefore = 0 };
                                    anchor.Placement.FollowingBlocks.Add(t);
                                    Add(anchor);
                                }
                            }
                            else EmitPositioned(inner, instance, box, x, y);
                            RotateEmitted(inner, first, ax, ay);
                        }
                    }
                    else EmitPositionedNode(child, data, box, ox, oy, everyPage: false);
                    if (_blocks > MaxBlocks) { Note("the layout stopped at its block ceiling"); return; }
                }
            }

            /// <summary>
            /// A node placed at (<paramref name="ox"/>, <paramref name="oy"/>) plus its own x and y, by its anchor point
            /// — or, <paramref name="flowed"/>, a flowed child placed by its flow (a line's, a turned container's box),
            /// its top-left corner its place and its anchor point ignored, as XFA 3.3 chapter 8 has it for flowing
            /// layout: "the contained object's x and y properties, as well as its anchor point, are ignored"
            /// — then turned by its <c>rotate</c> about that point.
            /// </summary>
            private void EmitPositionedNode(XfaBoxNode node, XElement? data, BoxStart? box, double ox, double oy, bool everyPage, bool flowed = false)
            {
                if (!node.IsShown) return;
                double ax = ox + node.X, ay = oy + node.Y;
                var (x, y) = flowed ? (ax, ay) : ExtentOrigin(node, ax, ay);
                int first = _section!.Children.Count;
                switch (node)
                {
                    case XfaSubform s:
                        if ((s.Border?.Visible == true || s.Border?.Fill != null) && s.W.HasValue && s.H.HasValue)
                            Add(Frame(x, y, s.W.Value, s.H.Value, s.Border!, box, everyPage));
                        EmitPositioned(s, data, box, x, y);
                        break;
                    case XfaField f:
                        EmitPositionedField(f, data, box, x, y, everyPage);
                        break;
                    case XfaDraw d:
                        EmitPositionedDraw(d, box, x, y, everyPage);
                        break;
                }
                RotateEmitted(node, first, ax, ay);
            }

            /// <summary>
            /// Where a positioned node's nominal extent starts: its x and y place its anchor point
            /// (chapter 17's <c>anchorType</c>), so the extent's top-left is the anchor less the anchor's
            /// offset into the extent.
            /// </summary>
            private (double Left, double Top) ExtentOrigin(XfaBoxNode node, double ax, double ay)
            {
                string anchor = node.AnchorType;
                double fx = anchor.EndsWith("Center", StringComparison.Ordinal) ? 0.5 : anchor.EndsWith("Right", StringComparison.Ordinal) ? 1 : 0;
                double fy = anchor.StartsWith("middle", StringComparison.Ordinal) ? 0.5 : anchor.StartsWith("bottom", StringComparison.Ordinal) ? 1 : 0;
                if ((fx != 0 && !node.W.HasValue) || (fy != 0 && !node.H.HasValue))
                    Note("an anchor point other than the top-left corner, on a container whose size the template does not give, is taken as its top-left");
                return (ax - fx * (node.W ?? 0), ay - fy * (node.H ?? 0));
            }

            /// <summary>
            /// <paramref name="quarter"/> quarter turns (1 to 3) counter-clockwise, as XFA's <c>rotate</c>
            /// turns, as a matrix in PDF space (y up) — a positioned node's and a turned flow's alike.
            /// </summary>
            private static double[] QuarterTurn(int quarter)
            {
                double cos = quarter == 2 ? -1 : 0, sin = quarter == 1 ? 1 : quarter == 3 ? -1 : 0;
                return new[] { cos, sin, -sin, cos, 0, 0 };
            }

            /// <summary>
            /// Turns what a positioned node drew — the elements from <paramref name="first"/> on — by its
            /// <c>rotate</c>: counter-clockwise, in multiples of 90 degrees, about its anchor point
            /// (<paramref name="ax"/>, <paramref name="ay"/>), after any turn an element already has (a
            /// sloped line's, an inner container's).
            /// </summary>
            private void RotateEmitted(XfaBoxNode node, int first, double ax, double ay)
            {
                if (node.Rotate == 0) return;
                double turns = node.Rotate / 90;
                if (turns != Math.Floor(turns)) { Note("a rotation that is not a multiple of 90 degrees is not applied"); return; }
                int quarter = SaturatingCast.ToInt(((turns % 4) + 4) % 4);
                if (quarter == 0) return;
                double[] turn = QuarterTurn(quarter);
                List<IDocElement> children = _section!.Children;
                for (int i = first; i < children.Count; i++)
                {
                    if (children[i] is not Paragraph p || p.Placement == null) continue;
                    if (p.Placement.FollowingBlocks.Count > 0) Note("a table in a rotated container is drawn unrotated");
                    double[]? origin = p.TransformOrigin;
                    if (p.Transform != null && (origin == null || origin[0] != 0 || origin[2] != 0))
                    {
                        Note("an element turned about a point its own size places is drawn unrotated in a rotated container");
                        continue;
                    }
                    // In the paragraph's own frame: its top-left corner the origin, x right, y up.
                    double[] about = TransformMatrix.About(turn, ax - p.Placement.LeftPt, -(ay - p.Placement.TopPt));
                    p.Transform = p.Transform == null
                        ? about
                        : TransformMatrix.Multiply(TransformMatrix.About(p.Transform, origin![1], -origin[3]), about);
                    p.TransformOrigin = new[] { 0.0, 0, 0, 0 };
                }
            }

            private void EmitPositionedDraw(XfaDraw d, BoxStart? box, double x, double y, bool everyPage)
            {
                double w = d.W ?? 0, h = d.H ?? 0;
                XfaMargin inset = d.Margin ?? new XfaMargin();
                if (d.IsRectangle || d.IsArc || d.IsLine)
                {
                    // The draw's border frames its nominal extent; the shape is drawn in the content
                    // region with its own edge and fill (chapter 17's rectangle, line and arc).
                    if (d.Border?.Visible == true || d.Border?.Fill != null)
                        Add(Frame(x, y, w, h, d.Border!, box, everyPage));
                    XfaBorder stroke = d.Shape ?? new XfaBorder { Visible = true };
                    double sx = x + inset.Left, sy = y + inset.Top;
                    double sw = Math.Max(0, w - inset.Left - inset.Right), sh = Math.Max(0, h - inset.Top - inset.Bottom);
                    if (d.IsLine)
                    {
                        if (stroke.Visible) Add(LineShape(sx, sy, sw, sh, d.LineSlope, stroke, box, everyPage));
                        return;
                    }
                    Paragraph shape;
                    if (d.IsArc)
                    {
                        if (d.ArcCircular)
                        {
                            // "Adjusted to a circular path": a circle across the region's smaller side,
                            // centred in it (the text's "radius equal to the smaller dimension" would
                            // overflow the region it is placed in).
                            double side = Math.Min(sw, sh);
                            sx += (sw - side) / 2; sy += (sh - side) / 2; sw = sh = side;
                        }
                        if (IsPartialArc(d))
                        {
                            Add(ArcParagraph(d, stroke, sw, sh, Place(box, sx, sy, sw, everyPage)));
                            return;
                        }
                        shape = Frame(sx, sy, sw, sh, stroke, box, everyPage);
                        shape.CornerRadiiPercent = EllipseCorners();
                    }
                    else shape = Frame(sx, sy, sw, sh, stroke, box, everyPage);
                    Add(shape);
                    return;
                }
                if (d.Border?.Visible == true || d.Border?.Fill != null)
                    Add(Frame(x, y, w, h, d.Border!, box, everyPage));

                if (d.Image != null)
                {
                    var p = new Paragraph { Placement = Place(box, x + inset.Left, y + inset.Top, Math.Max(0, w - inset.Left - inset.Right), everyPage), SpacingAfter = 0 };
                    p.Runs.Add(new ImageRun { Data = d.Image.Data, WidthPt = Math.Max(1, w - inset.Left - inset.Right), HeightPt = Math.Max(1, h - inset.Top - inset.Bottom), IsDecorative = true });
                    Add(p);
                    return;
                }
                if (d.Text.Length > 0 || d.Caption?.Text.Length > 0)
                {
                    string text = d.Text.Length > 0 ? d.Text : d.Caption!.Text;
                    List<XfaRichParagraph>? rich = d.Text.Length > 0 ? d.Rich : d.Caption!.Rich;
                    Add(TextParagraph(text, d.Font, d.Para, Place(box, x + inset.Left, y + inset.Top, Math.Max(0, w - inset.Left - inset.Right), everyPage),
                                      d.H == null ? null : Math.Max(0, h - inset.Top - inset.Bottom), rich: rich));
                }
            }

            private void EmitPositionedField(XfaField f, XElement? data, BoxStart? box, double x, double y, bool everyPage)
            {
                double w = f.W ?? 0, h = f.H ?? 0;
                string value = ValueText(f, data);
                if (value.Length == 0 && !_options.DrawEmptyFields) return;
                XfaMargin inset = f.Margin ?? new XfaMargin();

                // The field's own border frames the whole field, caption included.
                if (f.Border?.Visible == true || f.Border?.Fill != null)
                    Add(Frame(x, y, w, h, f.Border!, box, everyPage));

                XfaCaption? cap = f.Caption != null && f.Caption.Visible && f.Caption.Text.Length > 0 ? f.Caption : null;
                double cx = x + inset.Left, cy = y + inset.Top;
                double cw = Math.Max(0, w - inset.Left - inset.Right), ch = Math.Max(0, h - inset.Top - inset.Bottom);

                // A field of no given height is as tall as its parts, which only their layout knows: a
                // placed table, the row model of exactly that. A caption beside the value is a row of two
                // cells, the caption's its reserve wide, each part aligned (para vAlign) in the row's
                // height — taken when either part aligns other than at the top, which is all it changes; a
                // caption above or below is a row of its own at least its reserve tall, so it follows the
                // value whatever the value's height (placed at a height of none, it overlapped it).
                bool sideAligned = cap?.Placement is "left" or "right"
                                   && (cap.Para?.VAlign is "middle" or "bottom" || f.Para?.VAlign is "middle" or "bottom");
                if (cap != null && f.H == null && value.Length > 0 && (sideAligned || cap.Placement is "top" or "bottom"))
                {
                    double reserve = Math.Min(CaptionReserve(cap, f, cw), cw);
                    TableCell Part(string text, XfaFont? font, XfaPara? para, double padding, List<XfaRichParagraph>? rich)
                    {
                        var cell = new TableCell { InnerPaddingPt = padding };
                        var more = new List<Paragraph>();
                        cell.Paragraphs.Add(TextParagraph(text, font, para, null, cell: cell, rich: rich, rest: more));
                        cell.Paragraphs.AddRange(more);
                        return cell;
                    }
                    TableCell captionCell = Part(cap.Text, cap.Font ?? f.Font, cap.Para, 0, cap.Rich), valueCell = Part(value, f.Font, f.Para, 1, null);
                    var t = new Table { SpacingAfter = 0, BorderWidth = 0, MinRowHeightPt = 0, WidthPt = cw };
                    if (cap.Placement is "top" or "bottom")
                    {
                        t.ColumnWidths.Add(cw);
                        var captionRow = new TableRow { HeightPt = reserve };
                        captionRow.Cells.Add(captionCell);
                        var valueRow = new TableRow();
                        valueRow.Cells.Add(valueCell);
                        t.Rows.Add(cap.Placement == "top" ? captionRow : valueRow);
                        t.Rows.Add(cap.Placement == "top" ? valueRow : captionRow);
                    }
                    else
                    {
                        bool right = cap.Placement == "right";
                        t.ColumnWidths.Add(right ? cw - reserve : reserve);
                        t.ColumnWidths.Add(right ? reserve : cw - reserve);
                        var row = new TableRow();
                        row.Cells.Add(right ? valueCell : captionCell);
                        row.Cells.Add(right ? captionCell : valueCell);
                        t.Rows.Add(row);
                    }
                    var anchor = new Paragraph { Placement = Place(box, cx, cy, cw, everyPage), SpacingAfter = 0, SpacingBefore = 0 };
                    anchor.Placement.FollowingBlocks.Add(t);
                    Add(anchor);
                    return;
                }

                // Where the caption sits and what the value is left.
                double vx = cx, vy = cy, vw = cw, vh = ch;
                // Each part is aligned (para vAlign) in the band it is set in. A field of no given height
                // reaches here with nothing to align — a side caption at the top, or no value — or with an
                // aligned caption and no value, whose band of 0 TextParagraph reports.
                if (cap != null)
                {
                    double reserve = CaptionReserve(cap, f, cw);
                    switch (cap.Placement)
                    {
                        case "right":
                            vw = Math.Max(0, cw - reserve);
                            Add(TextParagraph(cap.Text, cap.Font ?? f.Font, cap.Para, Place(box, cx + vw, cy, reserve, everyPage), ch, rich: cap.Rich));
                            break;
                        case "top":
                            vy = cy + reserve; vh = Math.Max(0, ch - reserve);
                            Add(TextParagraph(cap.Text, cap.Font ?? f.Font, cap.Para, Place(box, cx, cy, cw, everyPage), Math.Min(reserve, ch), rich: cap.Rich));
                            break;
                        case "bottom":
                            vh = Math.Max(0, ch - reserve);
                            Add(TextParagraph(cap.Text, cap.Font ?? f.Font, cap.Para, Place(box, cx, cy + vh, cw, everyPage), ch - vh, rich: cap.Rich));
                            break;
                        case "inline":
                            value = cap.Text + " " + value;
                            break;
                        default:   // left
                            vx = cx + reserve; vw = Math.Max(0, cw - reserve);
                            Add(TextParagraph(cap.Text, cap.Font ?? f.Font, cap.Para, Place(box, cx, cy, reserve, everyPage), ch, rich: cap.Rich));
                            break;
                    }
                }

                // The interface's box (a text edit's lowered edge, a button's raised one).
                if (f.UiHasBorder || f.UiKind == "button")
                    Add(Frame(vx, vy, vw, vh, new XfaBorder { Visible = true, Thickness = 0.5, Color = "#000000", Fill = f.UiKind == "button" ? (f.Border?.Fill ?? "#D4D0C8") : null }, box, everyPage));

                if (f.UiKind == "imageEdit" && f.DefaultImage != null && value.Length == 0)
                {
                    var p = new Paragraph { Placement = Place(box, vx, vy, vw, everyPage), SpacingAfter = 0 };
                    p.Runs.Add(new ImageRun { Data = f.DefaultImage.Data, WidthPt = Math.Max(1, vw), HeightPt = Math.Max(1, vh), IsDecorative = true });
                    Add(p);
                    return;
                }
                if (value.Length > 0)
                    Add(TextParagraph(value, f.Font, f.Para, Place(box, vx + 1, vy + 1, Math.Max(0, vw - 2), everyPage),
                                      f.H == null && cap == null ? null : Math.Max(0, vh - 2)));
            }

            /// <summary>
            /// A rectangle of a border and fill: an empty paragraph as tall as the box, placed, with
            /// exact zero insets so its border is drawn on the rectangle's own edges — the exporter
            /// otherwise widens a bordered paragraph by <see cref="Paragraph.DefaultBoxInsetPt"/> a side.
            /// </summary>
            private Paragraph Frame(double x, double y, double w, double h, XfaBorder border, BoxStart? box, bool everyPage)
            {
                var p = new Paragraph { Placement = Place(box, x, y, w, everyPage), SpacingAfter = 0, SpacingBefore = 0, BoxInsetIsExact = true };
                if (border.Visible)
                {
                    var edge = new Border(border.Thickness, border.Color);
                    p.BorderTop = edge; p.BorderBottom = edge; p.BorderLeft = edge; p.BorderRight = edge;
                }
                if (border.Fill != null) p.Shading = border.Fill;
                // The paragraph's one line is the box's height, so border and fill reach the bottom.
                p.LineSpacingPt = Math.Max(0.5, h);
                p.Runs.Add(new TextRun { Text = " ", FontSize = 1 });
                return p;
            }

            /// <summary>Every corner a half of the box's width and height: CSS's <c>border-radius: 50%</c>, the ellipse the box holds.</summary>
            private static double[] EllipseCorners() => new[] { 50.0, 50, 50, 50 };

            /// <summary>An arc of less than a full turn, which the box's ellipse cannot draw.</summary>
            private static bool IsPartialArc(XfaDraw d) => d.IsArc && Math.Abs(d.ArcSweep) < 360;

            /// <summary>
            /// A partial arc as a picture of paths (<see cref="ImageRun.Drawing"/>) in its
            /// <paramref name="w"/> × <paramref name="h"/> region: placed by <paramref name="place"/>,
            /// or in the flow when it is null; decoration, as every XFA shape is.
            /// </summary>
            private static Paragraph ArcParagraph(XfaDraw d, XfaBorder stroke, double w, double h, AbsolutePlacement? place)
            {
                var p = new Paragraph { Placement = place, SpacingAfter = 0, SpacingBefore = 0 };
                p.Runs.Add(new ImageRun { WidthPt = w, HeightPt = h, IsDecorative = true, Drawing = ArcDrawing(w, h, d.ArcStart, d.ArcSweep, stroke) });
                return p;
            }


            /// <summary>
            /// A <c>line</c> in its content region: a horizontal rule when the region is no taller than
            /// the stroke, a vertical one when it is no wider, otherwise the diagonal
            /// <see cref="SlopedEdge"/> draws.
            /// </summary>
            private Paragraph LineShape(double x, double y, double w, double h, string slope, XfaBorder stroke, BoxStart? box, bool everyPage)
            {
                // Exact zero insets: the rule runs from x to x + w, not the exporter's 2 pt wider.
                var line = new Paragraph { Placement = Place(box, x, y, w, everyPage), LineSpacingPt = Math.Max(0.5, h), SpacingAfter = 0, BoxInsetIsExact = true };
                var edge = new Border(stroke.Thickness, stroke.Color);
                if (h <= stroke.Thickness) line.BorderTop = edge;
                else if (w <= stroke.Thickness) line.BorderLeft = edge;
                else
                {
                    // A rule along the top of a band from the line's start — the top-left corner for \,
                    // the bottom-left for / — turned about its own top-left onto the diagonal, so a
                    // rotated container's turn composes with it (RotateEmitted).
                    if (slope == "/") { line.Placement = Place(box, x, y + h, w, everyPage); line.LineSpacingPt = 0.5; }
                    line.BorderTop = edge;
                    line.Transform = SlopedEdge(w, h, slope);
                    line.TransformOrigin = new[] { 0.0, 0, 0, 0 };
                }
                line.Runs.Add(new TextRun { Text = " " });
                return line;
            }

            /// <summary>
            /// A <paramref name="w"/>-wide edge from the start of a <paramref name="w"/> × <paramref name="h"/>
            /// box's diagonal — its top-left corner for <c>\</c>, its bottom-left for <c>/</c> — stretched to
            /// the diagonal's length and turned down (or up) onto it, about that start. The stroke's
            /// thickness is across the edge and is not stretched.
            /// </summary>
            internal static double[] SlopedEdge(double w, double h, string slope)
            {
                double angle = Math.Atan2(h, w) * (slope == "/" ? 1 : -1);   // PDF space: y up, anticlockwise
                var stretch = new[] { Math.Sqrt(w * w + h * h) / w, 0, 0, 1.0, 0, 0 };
                var turn = new[] { Math.Cos(angle), Math.Sin(angle), -Math.Sin(angle), Math.Cos(angle), 0, 0 };
                return TransformMatrix.Multiply(stretch, turn);
            }

            /// <summary>
            /// A positioned subform's height as flow content: one empty line as tall as the box's
            /// inside, so a box the page cannot hold moves to the next page instead of collapsing.
            /// </summary>
            /// <remarks>
            /// A positioned subform's children are placed, not flowed, so its box holds no block the
            /// exporter has to find room for; its minimum height alone is honoured only where it fits
            /// (a box that would break is as tall as its blocks). Designer's forty 20 pt rows would
            /// then all land on one page. Held half a point under the content area, like the box.
            /// </remarks>
            private void AddHeightSpacer(XfaSubform s, BoxStart box)
            {
                if (!(s.H > 0)) return;
                double areaHeight = TallestAreaHeight();
                double inside = Math.Min(s.H.Value, areaHeight - 0.5)
                                - box.PaddingTop - box.PaddingBottom
                                - (box.BorderTop?.Width ?? 0) - (box.BorderBottom?.Width ?? 0);
                if (inside <= 0.5) return;
                var spacer = new Paragraph { LineSpacingPt = inside, SpacingAfter = 0, SpacingBefore = 0 };
                spacer.Runs.Add(new TextRun { Text = " ", FontSize = 1 });
                Add(spacer);
            }

            /// <summary>The tallest content area of the page area in use: a box no taller fits one of its regions.</summary>
            private double TallestAreaHeight()
                => _page == null || _page.ContentAreas.Count == 0 ? double.MaxValue : _page.ContentAreas.Max(a => a.H);

            private static AbsolutePlacement Place(BoxStart? box, double x, double y, double w, bool everyPage)
                => new AbsolutePlacement { ContainingBox = box, LeftPt = x, TopPt = y, WidthPt = Math.Max(0, w), EveryPage = everyPage };

            // ---------------------------------------------------------------- flowed content

            private void EmitFieldBlock(XfaField f, XElement? data)
            {
                string value = ValueText(f, data);
                if (value.Length == 0 && !_options.DrawEmptyFields) return;
                var box = new BoxStart { WidthPt = Math.Max(0, f.W ?? 0), MinHeightPt = Math.Max(0, f.H ?? 0) };
                ApplyBox(box, f);
                if (box.BorderTop == null && f.UiHasBorder)
                {
                    var edge = new Border(0.5, "#000000");
                    box.BorderTop = edge; box.BorderBottom = edge; box.BorderLeft = edge; box.BorderRight = edge;
                }
                XfaCaption? cap = f.Caption != null && f.Caption.Visible && f.Caption.Text.Length > 0 ? f.Caption : null;
                // A caption the field opens with: its first paragraph's avoid is a break before the field.
                if (cap != null && cap.Placement is "left" or "top" or "inline") KeepPreviousWith(cap.Rich);
                Add(box);
                if (cap != null && cap.Placement is "left" or "right" or "inline")
                {
                    if (cap.Rich != null)
                    {
                        // A rich caption sharing the value's paragraph: its runs, in their own styles,
                        // beside the value's (its several paragraphs, if it has them, one line each).
                        Paragraph captionRuns = TextParagraph(cap.Text, cap.Font ?? f.Font, null, null, rich: cap.Rich);
                        var p = TextParagraph(cap.Placement == "right" ? value + " " : "", f.Font, f.Para, null, flowBox: box);
                        if (cap.Placement == "right") p.Runs.AddRange(captionRuns.Runs);
                        else { p.Runs.Clear(); p.Runs.AddRange(captionRuns.Runs); p.Runs.Add(Run(" " + value, f.Font)); }
                        Add(p);
                    }
                    else Add(TextParagraph(cap.Placement == "right" ? value + " " + cap.Text : cap.Text + " " + value, f.Font, f.Para, null, flowBox: box));
                }
                else if (cap == null) Add(TextParagraph(value, f.Font, f.Para, null, flowBox: box));
                else
                {
                    // A caption above or below is a part of its own, its reserve tall; the value has the
                    // rest of the field's content height. Each part is a band of that height its text
                    // aligns in (para vAlign), as the positioned field's are.
                    double inner = Math.Max(0, box.MinHeightPt - box.PaddingTop - box.PaddingBottom
                                                - (box.BorderTop?.Width ?? 0) - (box.BorderBottom?.Width ?? 0));
                    double reserve = CaptionReserve(cap, f, 0);
                    if (inner > 0) reserve = Math.Min(reserve, inner);
                    void Part(string text, XfaFont? font, XfaPara? para, double height, List<XfaRichParagraph>? rich)
                    {
                        var band = new BoxStart { MinHeightPt = height };
                        Add(band);
                        var more = new List<Paragraph>();
                        Add(TextParagraph(text, font, para, null, flowBox: band, rich: rich, rest: more));
                        foreach (Paragraph m in more) Add(m);
                        Add(new BoxEnd());
                    }
                    double valueHeight = inner > 0 ? Math.Max(0, inner - reserve) : 0;
                    if (cap.Placement == "top") Part(cap.Text, cap.Font ?? f.Font, cap.Para, reserve, cap.Rich);
                    if (value.Length > 0) Part(value, f.Font, f.Para, valueHeight, null);
                    if (cap.Placement == "bottom") Part(cap.Text, cap.Font ?? f.Font, cap.Para, reserve, cap.Rich);
                }
                Add(new BoxEnd());
            }

            /// <summary>
            /// The room a caption takes from its field: its <c>reserve</c>, or — none given — a line of its
            /// font above or below the value, and two fifths of the field's <paramref name="contentWidth"/>
            /// beside it. One rule for the positioned and the flowed field.
            /// </summary>
            private static double CaptionReserve(XfaCaption cap, XfaField f, double contentWidth)
                => cap.Reserve ?? (cap.Placement is "top" or "bottom" ? (cap.Font?.Size ?? f.Font?.Size ?? 10) * 1.3 : contentWidth * 0.4);

            private void EmitDrawBlock(XfaDraw d)
            {
                if (d.IsRectangle || d.IsLine || d.IsArc)
                {
                    // In a flow a shape is a box of its size, drawn with its own edge and fill; the
                    // draw's border, when it has one, frames a box round it.
                    double w = Math.Max(0, d.W ?? 0), h = Math.Max(0, d.H ?? 0);
                    BoxStart? frame = null;
                    if (d.Border?.Visible == true || d.Border?.Fill != null)
                    {
                        frame = new BoxStart { WidthPt = w, MinHeightPt = h, Shading = d.Border.Fill ?? "" };
                        if (d.Border.Visible)
                        {
                            var b = new Border(d.Border.Thickness, d.Border.Color);
                            frame.BorderTop = b; frame.BorderBottom = b; frame.BorderLeft = b; frame.BorderRight = b;
                        }
                        Add(frame);
                    }
                    XfaBorder stroke = d.Shape ?? new XfaBorder { Visible = true };
                    var shape = new BoxStart { WidthPt = w, MinHeightPt = h };
                    var edge = new Border(stroke.Thickness, stroke.Color);
                    if (stroke.Visible && d.IsLine)
                    {
                        if (h <= stroke.Thickness) shape.BorderTop = edge;
                        else if (w <= stroke.Thickness) shape.BorderLeft = edge;
                        else
                        {
                            // The box's top edge about its top-left for \, its bottom edge about its bottom-left for /.
                            bool up = d.LineSlope == "/";
                            if (up) shape.BorderBottom = edge; else shape.BorderTop = edge;
                            shape.Transform = SlopedEdge(w, h, d.LineSlope);
                            shape.TransformOrigin = up ? new[] { 0.0, 0, 1, 0 } : new[] { 0.0, 0, 0, 0 };
                        }
                    }
                    else if (stroke.Visible) { shape.BorderTop = edge; shape.BorderBottom = edge; shape.BorderLeft = edge; shape.BorderRight = edge; }
                    if (!d.IsLine && stroke.Fill != null) shape.Shading = stroke.Fill;
                    if (d.IsArc)
                    {
                        // A circular arc is as wide as the smaller side; in a flow it starts at the left.
                        if (d.ArcCircular) shape.WidthPt = shape.MinHeightPt = Math.Min(w, h);
                        if (IsPartialArc(d))
                        {
                            // Less than a full turn: the arc's own path in the box, which keeps the room.
                            Add(new BoxStart { WidthPt = shape.WidthPt, MinHeightPt = shape.MinHeightPt });
                            Add(ArcParagraph(d, stroke, shape.WidthPt, shape.MinHeightPt, null));
                            Add(new BoxEnd());
                            if (frame != null) Add(new BoxEnd());
                            return;
                        }
                        shape.CornerRadiiPercent = EllipseCorners();
                    }
                    Add(shape); Add(new BoxEnd());
                    if (frame != null) Add(new BoxEnd());
                    return;
                }
                var box = new BoxStart { WidthPt = Math.Max(0, d.W ?? 0), MinHeightPt = Math.Max(0, d.H ?? 0) };
                ApplyBox(box, d);
                if (d.Image == null && d.Text.Length > 0) KeepPreviousWith(d.Rich);
                Add(box);
                if (d.Image != null)
                {
                    var p = new Paragraph { SpacingAfter = 0 };
                    p.Runs.Add(new ImageRun { Data = d.Image.Data, WidthPt = Math.Max(1, d.W ?? 100), HeightPt = Math.Max(1, d.H ?? 100), IsDecorative = true });
                    Add(p);
                }
                else if (d.Text.Length > 0)
                {
                    var more = new List<Paragraph>();
                    Add(TextParagraph(d.Text, d.Font, d.Para, null, flowBox: box, rich: d.Rich, rest: more));
                    foreach (Paragraph m in more) Add(m);
                }
                Add(new BoxEnd());
            }

            private void EmitTable(XfaSubform table, XElement? data)
            {
                if (BuildTable(table, data) is Table t) Add(t);
            }

            /// <summary>A table subform's rows and cells as a table, or null when it has none.</summary>
            private Table? BuildTable(XfaSubform table, XElement? data)
            {
                var t = new Table { SpacingAfter = 0, BorderWidth = table.Border?.Visible == true ? table.Border.Thickness : 0 };
                if (table.W.HasValue) t.WidthPt = Math.Max(0, table.W.Value);
                foreach (double cw in table.ColumnWidths) if (cw > 0) t.ColumnWidths.Add(cw);

                IEnumerable<(XfaSubform Row, XElement? Data)> rows = table.Layout == "table"
                    ? table.Children.OfType<XfaSubform>().Where(r => r.IsShown)
                        .SelectMany(r => Instances(r, data).Select(inst => (r, inst)))
                    : new[] { (table, data) };

                foreach (var (row, rowData) in rows)
                {
                    var tr = new TableRow();
                    if (row.H.HasValue) tr.HeightPt = Math.Max(0, row.H.Value);
                    foreach (XfaBoxNode cellNode in row.Children)
                    {
                        if (!cellNode.IsShown) continue;
                        var cell = new TableCell();
                        if (cellNode.W.HasValue) cell.WidthPt = Math.Max(0, cellNode.W.Value);
                        if (cellNode.Border?.Fill != null) cell.BackgroundColor = cellNode.Border.Fill;
                        switch (cellNode)
                        {
                            case XfaDraw d:
                                var more = new List<Paragraph>();
                                cell.Paragraphs.Add(TextParagraph(d.Text, d.Font, d.Para, null, cell: cell, rich: d.Rich, rest: more));
                                cell.Paragraphs.AddRange(more);
                                break;
                            case XfaField f:
                            {
                                string value = ValueText(f, rowData);
                                string text = f.Caption != null && f.Caption.Visible && f.Caption.Text.Length > 0 ? f.Caption.Text + " " + value : value;
                                cell.Paragraphs.Add(TextParagraph(text, f.Font, f.Para, null, cell: cell));
                                break;
                            }
                            case XfaSubform s:
                            {
                                var text = new List<string>();
                                foreach (XfaBoxNode n in s.Children)
                                    if (n is XfaDraw dd && dd.Text.Length > 0) text.Add(dd.Text);
                                    else if (n is XfaField ff) text.Add(ValueText(ff, rowData));
                                cell.Paragraphs.Add(TextParagraph(string.Join(" ", text.Where(x => x.Length > 0)), s.Font, s.Para, null, cell: cell));
                                break;
                            }
                        }
                        tr.Cells.Add(cell);
                    }
                    if (tr.Cells.Count > 0) t.Rows.Add(tr);
                    _blocks++;
                }
                return t.Rows.Count > 0 ? t : null;
            }

            // ---------------------------------------------------------------- text

            // band: the height of the box's content region the text is set in, when the template gives
            // it — what para vAlign aligns in; null for a placed box of no given height, which its text
            // sizes, so nothing is left to align. cell: the table cell the paragraph goes in, which takes
            // the alignment itself; flowBox: the flowed box the paragraph is the whole of, which aligns
            // its blocks in its minimum height (BoxStart.ContentAlign). rich: the text's rich paragraphs
            // (XfaRichText), which replace text when they hold any — the first is the paragraph returned;
            // the others follow it, a placed one's in its Placement.Following (drawn under it and moved
            // with it in its band), any other's in rest, for the caller to add after it.
            private Paragraph TextParagraph(string text, XfaFont? font, XfaPara? para, AbsolutePlacement? place,
                                            double? band = 0, TableCell? cell = null, BoxStart? flowBox = null,
                                            List<XfaRichParagraph>? rich = null, List<Paragraph>? rest = null)
            {
                var p = ParaParagraph(para, font);
                p.Placement = place;
                if (para != null)
                {
                    if (para.VAlign != "top")
                    {
                        // In a placed paragraph the band its box gives it; in a table cell the cell's own
                        // alignment; in a flowed box the box's, in its height. A placed box its text sizes
                        // has no room to align in; one whose band is shared with another part (a caption
                        // of no given height) cannot say where its middle is.
                        var align = para.VAlign == "middle" ? CellVerticalAlign.Center : CellVerticalAlign.Bottom;
                        if (cell != null) cell.VAlign = align;
                        else if (flowBox != null) flowBox.ContentAlign = align;
                        else if (place != null && band is double h && h > 0) { place.BandHeightPt = h; place.BandAlign = align; }
                        else if (!(place != null && band == null)) Note("vertical alignment inside a box (para vAlign) is not applied");
                    }
                }
                if (rich != null && rich.Exists(r => r.Runs.Count > 0))
                {
                    bool first = true;
                    foreach (XfaRichParagraph r in rich)
                    {
                        if (r.Runs.Count == 0) continue;
                        Paragraph target = first ? p : ParaParagraph(para, font);
                        RichInto(target, r, font);
                        if (!first)
                        {
                            if (place != null) place.Following.Add(target);
                            else if (rest != null) rest.Add(target);
                            else { p.Runs.Add(new LineBreak()); p.Runs.AddRange(target.Runs); }   // no room for a paragraph of its own
                        }
                        first = false;
                    }
                    return p;
                }
                // Designer writes U+2029 (paragraph separator) and U+2028 for the line breaks a user typed.
                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2029', '\n').Replace('\u2028', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.Runs.Add(new LineBreak());
                    // A tab character advances to the next tab stop (chapter 2, "Tab Stops").
                    string[] cells = lines[i].Split('\t');
                    for (int c = 0; c < cells.Length; c++)
                    {
                        if (c > 0) p.Runs.Add(new TabRun());
                        if (cells[c].Length > 0 || cells.Length == 1) p.Runs.Add(Run(cells[c], font));
                    }
                }
                return p;
            }

            /// <summary>
            /// A paragraph set as the container's <c>para</c> says: alignment, spacing, indents and its line
            /// height — a <c>lineHeight</c> as stated, else the font's (XFA 3.3: "the font is to be used to
            /// determine the line height"), 1.2 times the size (the larger of 1.2 and the font's own) for a
            /// standard font — with no least height (the exporter's 14pt would make 10pt text, XFA's default, stand 17% taller);
            /// a stated one grows with the font's <c>fontVerticalScale</c>.
            /// </summary>
            private static Paragraph ParaParagraph(XfaPara? para, XfaFont? font)
            {
                var p = new Paragraph { SpacingAfter = 0, SpacingBefore = 0 };
                if (para?.LineHeight is double stated && stated > 0)
                    p.LineSpacingPt = stated * ((font?.VerticalScale ?? 100) / 100.0);
                else
                    p.LineHeightFactor = FontLineHeightFactor;
                if (para == null) return p;
                p.Alignment = para.HAlign switch
                {
                    "center" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    "justify" or "justifyAll" => TextAlignment.Justify,
                    _ => TextAlignment.Left,
                };
                p.SpacingBefore = Math.Max(0, para.SpaceAbove);
                p.SpacingAfter = Math.Max(0, para.SpaceBelow);
                p.IndentLeft = Math.Max(0, para.MarginLeft);
                p.IndentRight = Math.Max(0, para.MarginRight);
                p.FirstLineIndent = para.TextIndent;
                p.Tabs.AddRange(XfaTabStops.WithDefaults(para.TabStops, para.TabDefault));
                return p;
            }

            /// <summary>
            /// A rich paragraph's formatting over the container's (what its <c>p</c> states, nothing else) and
            /// its runs \u2014 each the container's font, overridden by the styles in force on it.
            /// </summary>
            private static void RichInto(Paragraph p, XfaRichParagraph r, XfaFont? font)
            {
                if (r.HAlign != null)
                    p.Alignment = r.HAlign switch
                    {
                        "center" => TextAlignment.Center,
                        "right" => TextAlignment.Right,
                        "justify" or "justify-all" => TextAlignment.Justify,
                        _ => TextAlignment.Left,
                    };
                if (r.MarginLeft is double ml) p.IndentLeft = Math.Max(0, ml);
                if (r.MarginRight is double mr) p.IndentRight = Math.Max(0, mr);
                if (r.SpaceAbove is double sa) p.SpacingBefore = Math.Max(0, sa);
                if (r.SpaceBelow is double sb) p.SpacingAfter = Math.Max(0, sb);
                if (r.TextIndent is double ti) p.FirstLineIndent = ti;
                if (r.LineHeight is double lh && lh > 0) p.LineSpacingPt = lh;
                if (r.PageBreakBefore) p.PageBreakBefore = true;
                if (r.KeepWithNext) p.KeepWithNext = true;
                if (r.KeepTogether) p.KeepTogether = true;
                if (r.Orphans is int orphans) p.Orphans = orphans;
                if (r.Widows is int widows) p.Widows = widows;
                if (r.TabStops != null || r.TabInterval != null)
                {
                    // The paragraph's own stops are in effect within it, in place of the container's.
                    p.Tabs.Clear();
                    p.Tabs.AddRange(XfaTabStops.WithDefaults(r.TabStops ?? new List<TabStop>(), r.TabInterval));
                }
                foreach (XfaRichRun rr in r.Runs)
                {
                    if (rr.LineBreak) { p.Runs.Add(new LineBreak()); continue; }
                    if (rr.Tab) { p.Runs.Add(new TabRun()); continue; }
                    TextRun run = Run(rr.Text, font, scaleVertically: false);
                    if (rr.Bold is bool bold) run.IsBold = bold;
                    if (rr.Italic is bool italic) run.IsItalic = italic;
                    if (rr.Underline is bool underline) run.IsUnderline = underline;
                    if (rr.Strike is bool strike) run.IsStrikethrough = strike;
                    if (rr.Size is double size) run.FontSize = size;
                    if (rr.Family != null) run.FontName = FontName(rr.Family);
                    if (rr.Color != null) run.Color = rr.Color;
                    if (rr.LetterSpacing is double spacing) run.CharSpacing = spacing;
                    if (rr.Shift != 0) run.VerticalAlign = rr.Shift > 0 ? VerticalAlign.Superscript : VerticalAlign.Subscript;
                    if (rr.Link != null) run.LinkUri = rr.Link;
                    if (rr.Kerning is bool kern) run.Kerning = kern;
                    if (rr.HorizontalScale is int scale) run.HorizontalScale = scale;
                    ScaleVertically(run, rr.VerticalScale ?? font?.VerticalScale);
                    p.Runs.Add(run);
                }
                // page-break-after: always — the paragraph ends its page.
                if (r.PageBreakAfter) p.Runs.Add(new PageBreak());
            }

            private static TextRun Run(string text, XfaFont? font, bool scaleVertically = true)
            {
                var run = new TextRun { Text = text };
                if (font != null)
                {
                    run.FontName = FontName(font.Typeface);
                    run.FontSize = font.Size;
                    run.IsBold = font.Bold;
                    run.IsItalic = font.Italic;
                    run.IsUnderline = font.Underline;
                    if (font.Color != null) run.Color = font.Color;
                    if (font.Kerning) run.Kerning = true;
                    run.IsStrikethrough = font.LineThrough;
                    if (font.LetterSpacing is double spacing) run.CharSpacing = spacing;
                    run.BaselineShift = font.BaselineShift;
                    if (font.HorizontalScale is int scale) run.HorizontalScale = scale;
                    if (scaleVertically) ScaleVertically(run, font.VerticalScale);
                }
                else
                {
                    // XFA's default font is Courier at 10 pt.
                    run.FontName = StandardFonts.Courier;
                    run.FontSize = 10;
                }
                return run;
            }

            /// <summary>The line height the font decides, as a multiple of its size, for a standard font.</summary>
            private const double FontLineHeightFactor = 1.2;

            /// <summary>
            /// A <c>fontVerticalScale</c> (or rich text's <c>xfa-font-vertical-scale</c>) applied to a run whose
            /// size and horizontal scale are set: its glyphs scaled by <paramref name="vertical"/> percent
            /// vertically and its horizontal scale horizontally — the glyph matrix's two
            /// factors — are the run's size times the vertical factor at the horizontal
            /// over the vertical, to the nearest percent the model keeps; and the line, which the size decides,
            /// grows by the vertical factor (its
            /// <c>fLineHeight *= fVerScale</c>). XFA 3.3 says "a vertical geometric scaling factor … applied to
            /// all glyphs".
            /// </summary>
            private static void ScaleVertically(TextRun run, int? vertical)
            {
                if (vertical is not int v || v == 100) return;
                run.FontSize *= v / 100.0;
                run.HorizontalScale = Math.Max(1, SaturatingCast.ToInt(Math.Round(run.HorizontalScale * 100.0 / v)));
            }

            /// <summary>Designer's defaults onto the standard 14; any other family through to Builder's resolver by name.</summary>
            internal static string FontName(string typeface)
            {
                switch (typeface.Trim().ToLowerInvariant())
                {
                    case "myriad pro":
                    case "arial":
                    case "helvetica":
                    case "verdana":
                    case "tahoma":
                    case "segoe ui":
                    case "calibri":
                        return StandardFonts.Helvetica;
                    case "times new roman":
                    case "times":
                    case "minion pro":
                    case "georgia":
                    case "cambria":
                        return StandardFonts.TimesRoman;
                    case "courier new":
                    case "courier":
                    case "consolas":
                        return StandardFonts.Courier;
                    default:
                        return typeface.Length == 0 ? StandardFonts.Courier : typeface;
                }
            }

            // ---------------------------------------------------------------- data binding

            /// <summary>
            /// The data instances a subform occurs for, each charged to the block ceiling: an instance
            /// is layout work whether or not it emits a block, and an unframed top-to-bottom subform emits
            /// none — three such nested, each occurring ten thousand times, were 10¹² instances and no
            /// block, from a few hundred bytes. Past the ceiling, none.
            /// </summary>
            private List<XElement?> Instances(XfaSubform s, XElement? data)
            {
                if (_blocks > MaxBlocks) return new List<XElement?>();
                List<XElement?> instances = InstancesOf(s, data);
                _blocks += instances.Count;
                return instances;
            }

            /// <summary>The data instances a subform occurs for: one per bound element, within <c>occur</c>.</summary>
            private List<XElement?> InstancesOf(XfaSubform s, XElement? data)
            {
                int max = s.OccurMax == -1 ? MaxInstancesPerSubform : Math.Min(s.OccurMax, MaxInstancesPerSubform);
                int min = Math.Max(0, Math.Min(s.OccurMin, max));

                if (s.BindMatch == "none" || data == null || s.IsArea || s.Name.Length == 0)
                {
                    // Transparent to the data: the parent's node carries on, as many times as the
                    // template's initial count (one, unless `occur` says more).
                    int count = Math.Max(1, Math.Min(Math.Max(min, s.OccurInitial), max));
                    var same = new List<XElement?>(count);
                    for (int i = 0; i < count; i++) same.Add(s.BindMatch == "none" ? null : data);
                    return same;
                }

                IReadOnlyList<XElement> matches = s.BindRef.Length > 0 ? Resolve(data, s.BindRef) : ChildrenNamed(data, s.Name);
                if (matches.Count == 0)
                {
                    int count = Math.Max(0, Math.Min(Math.Max(min, s.OccurInitial), max));
                    if (count == 0 && s.OccurMin == 0) return new List<XElement?>();
                    var none = new List<XElement?>(Math.Max(1, count));
                    for (int i = 0; i < Math.Max(1, count); i++) none.Add(null);
                    return none;
                }
                if (matches.Count > max) { Note("a repeating subform has more data instances than its occur max; the rest are not shown"); matches = matches.Take(max).ToList(); }
                var result = new List<XElement?>(matches);
                for (int i = result.Count; i < min; i++) result.Add(null);
                return result;
            }

            private string ValueText(XfaField f, XElement? data)
            {
                string? bound = null;
                if (f.BindMatch != "none" && data != null)
                {
                    IReadOnlyList<XElement> hits = f.BindRef.Length > 0 ? Resolve(data, f.BindRef) : ChildrenNamed(data, f.Name);
                    if (hits.Count > 0) bound = hits[0].HasElements ? XfaTemplate.HtmlText(hits[0]) : hits[0].Value;
                }
                string raw = (bound ?? f.DefaultValue).Trim();

                switch (f.UiKind)
                {
                    case "checkButton":
                    {
                        bool on = raw.Length > 0 && raw != "0" && !string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
                                  && (f.ItemSaves.Count == 0 || string.Equals(raw, f.OnValue, StringComparison.Ordinal) || raw == "1");
                        // A no-break space inside the brackets: the exporter breaks a line's words at
                        // U+0020, and a mark split in three is three draws no reader joins.
                        return on ? "[X]" : "[" + (char)0xA0 + "]";
                    }
                    case "choiceList":
                        for (int i = 0; i < f.ItemSaves.Count && i < f.ItemDisplays.Count; i++)
                            if (string.Equals(f.ItemSaves[i], raw, StringComparison.Ordinal)) return f.ItemDisplays[i];
                        return raw;
                    case "button":
                        return "";
                    case "barcode":
                        Note("a barcode field is drawn as its value");
                        return raw;
                    case "signature":
                        Note("a signature field is drawn as an empty box");
                        return "";
                    case "imageEdit":
                        return "";
                    default:
                        return raw;
                }
            }

            /// <summary>
            /// A data element's children of one name, in document order. Each element's children are
            /// indexed by name the first time any is asked for: every binding read them afresh, and a
            /// record of n children bound by m fields was m × n comparisons. The data does not change
            /// during the layout; callers copy what they keep.
            /// </summary>
            private IReadOnlyList<XElement> ChildrenNamed(XElement data, string name)
            {
                if (!_childrenByName.TryGetValue(data, out Dictionary<string, List<XElement>>? byName))
                {
                    byName = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
                    foreach (XElement c in data.Elements())
                    {
                        DataChildrenExamined++;
                        if (!byName.TryGetValue(c.Name.LocalName, out List<XElement>? list))
                            byName[c.Name.LocalName] = list = new List<XElement>();
                        list.Add(c);
                    }
                    _childrenByName[data] = byName;
                }
                return byName.TryGetValue(name, out List<XElement>? named) ? named : (IReadOnlyList<XElement>)Array.Empty<XElement>();
            }

            /// <summary>
            /// A <c>bind ref</c> as far as a dotted path with indexes goes: <c>$.a.b</c>, <c>a.b[1]</c>,
            /// <c>$record.a</c>, <c>$data.a</c>. Anything else is reported and binds nothing.
            /// </summary>
            private List<XElement> Resolve(XElement data, string somRef)
            {
                var empty = new List<XElement>();
                string s = somRef.Trim();
                if (s.Length == 0) return empty;
                if (s.IndexOfAny(new[] { '(', '"', '\'', ' ', '*' }) >= 0 || s.Contains(".."))
                {
                    Note("a bind ref beyond a dotted path is not resolved: " + s);
                    return empty;
                }
                string[] segments = s.Split('.');
                int start = 0;
                XElement current = data;
                for (; start < segments.Length; start++)
                {
                    string seg = segments[start];
                    if (seg == "$") continue;
                    if (seg is "$data" or "xfa" or "data") { current = RootOf(data); continue; }
                    if (seg == "$record") { current = RootOf(data).Elements().FirstOrDefault() ?? RootOf(data); continue; }
                    break;
                }
                var frontier = new List<XElement> { current };
                for (int i = start; i < segments.Length; i++)
                {
                    string seg = segments[i];
                    int index = -1;
                    int br = seg.IndexOf('[');
                    if (br >= 0)
                    {
                        string inside = seg.Substring(br + 1).TrimEnd(']');
                        if (inside == "*") index = -2;
                        else if (!int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) { Note("a bind ref beyond a dotted path is not resolved: " + s); return empty; }
                        seg = seg.Substring(0, br);
                    }
                    var next = new List<XElement>();
                    foreach (XElement node in frontier)
                    {
                        var named = ChildrenNamed(node, seg);
                        if (index == -2) next.AddRange(named);
                        else if (index >= 0) { if (index < named.Count) next.Add(named[index]); }
                        else if (named.Count > 0) next.Add(named[0]);
                    }
                    if (next.Count == 0 && i == start && !ReferenceEquals(current, RootOf(data)))
                    {
                        // A relative path that misses under the current node is tried from the root.
                        frontier = new List<XElement> { RootOf(data) };
                        i--; start = i + 1; current = RootOf(data);
                        continue;
                    }
                    frontier = next;
                    if (frontier.Count == 0) break;
                }
                return frontier;
            }

            private static XElement RootOf(XElement data)
            {
                XElement root = data;
                while (root.Parent != null && !string.Equals(root.Name.LocalName, "data", StringComparison.Ordinal)) root = root.Parent;
                return root;
            }
        }
    }
}
