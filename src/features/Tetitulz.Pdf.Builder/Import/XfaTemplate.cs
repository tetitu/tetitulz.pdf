// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>A node of the template that occupies a box: a subform, a field or a draw.</summary>
    internal abstract class XfaBoxNode
    {
        public string Name = "";
        /// <summary>Position inside the parent, in points (XFA's <c>x</c>/<c>y</c>; 0 when absent).</summary>
        public double X, Y;
        /// <summary>Size in points; <c>null</c> when the template leaves it to the content.</summary>
        public double? W, H;
        /// <summary><c>visible</c>, <c>hidden</c>, <c>invisible</c> or <c>inactive</c>.</summary>
        public string Presence = "visible";
        public XfaMargin? Margin;
        public XfaBorder? Border;
        public XfaFont? Font;
        public XfaPara? Para;
        /// <summary>
        /// The point <see cref="X"/>/<see cref="Y"/> place under positioned layout (<c>anchorType</c>):
        /// <c>topLeft</c> (the default), <c>topCenter</c>, … <c>bottomRight</c>.
        /// </summary>
        public string AnchorType = "topLeft";
        /// <summary><c>rotate</c>: degrees counter-clockwise about the anchor point; 0 when absent or not a finite number.</summary>
        public double Rotate;

        public bool IsShown => Presence == "visible";
    }

    internal sealed class XfaMargin
    {
        public double Top, Bottom, Left, Right;
    }

    internal sealed class XfaBorder
    {
        /// <summary>Whether any edge is drawn (the border's own presence, and at least one visible edge).</summary>
        public bool Visible;
        public double Thickness = 0.5;
        public string Color = "#000000";
        /// <summary>The <c>fill</c>'s colour, or <c>null</c> for none.</summary>
        public string? Fill;
    }

    internal sealed class XfaFont
    {
        public string Typeface = "Courier";
        public double Size = 10;
        public bool Bold, Italic, Underline;
        public string? Color;
        /// <summary><c>kerningMode="pair"</c>, <c>lineThrough</c>, <c>letterSpacing</c>, <c>baselineShift</c> (points, up), <c>fontHorizontalScale</c> and <c>fontVerticalScale</c> (percentages).</summary>
        public bool Kerning, LineThrough;
        public double? LetterSpacing;
        public double BaselineShift;
        public int? HorizontalScale, VerticalScale;
    }

    internal sealed class XfaPara
    {
        /// <summary><c>left</c>, <c>center</c>, <c>right</c>, <c>justify</c>, <c>justifyAll</c> or <c>radix</c>.</summary>
        public string HAlign = "left";
        public string VAlign = "top";
        public double SpaceAbove, SpaceBelow, MarginLeft, MarginRight, TextIndent;
        /// <summary>The <c>tabStops</c> (<see cref="XfaTabStops"/>) and the <c>tabDefault</c> interval; none when absent.</summary>
        public List<Model.TabStop> TabStops = new List<Model.TabStop>();
        public double? TabDefault;
        /// <summary><c>lineHeight</c>, in points; <c>null</c> (absent, or not above 0) for the font's, XFA 3.3's "the font is to be used to determine the line height".</summary>
        public double? LineHeight;
    }

    internal sealed class XfaCaption
    {
        /// <summary><c>left</c> (the default), <c>right</c>, <c>top</c>, <c>bottom</c> or <c>inline</c>.</summary>
        public string Placement = "left";
        /// <summary>The space set aside for the caption, in points; <c>null</c> when the template gives none.</summary>
        public double? Reserve;
        public string Text = "";
        /// <summary>The caption's rich text (an <c>exData</c> of <c>text/html</c>), its paragraphs and runs; <c>null</c> for plain text.</summary>
        public List<XfaRichParagraph>? Rich;
        public XfaFont? Font;
        public XfaPara? Para;
        public bool Visible = true;
    }

    internal sealed class XfaImageValue
    {
        public byte[] Data = Array.Empty<byte>();
        public string ContentType = "";
    }

    /// <summary>A <c>subform</c>, <c>exclGroup</c> or <c>area</c> — a container of boxes.</summary>
    internal sealed class XfaSubform : XfaBoxNode
    {
        /// <summary><c>position</c> (the default), <c>tb</c>, <c>lr-tb</c>, <c>rl-tb</c>, <c>row</c>, <c>rl-row</c> or <c>table</c>.</summary>
        public string Layout = "position";
        public readonly List<XfaBoxNode> Children = new List<XfaBoxNode>();
        public XfaPageSet? PageSet;
        public int OccurMin = 1;
        /// <summary>-1 is unbounded.</summary>
        public int OccurMax = 1;
        public int OccurInitial = 1;
        public string BindMatch = "once";
        public string BindRef = "";
        public readonly List<double> ColumnWidths = new List<double>();
        public XfaBreak? BreakBefore, BreakAfter;
        /// <summary>An <c>area</c> is transparent: no box of its own, children offset by its x/y.</summary>
        public bool IsArea;
        public bool IsExclGroup;
    }

    internal sealed class XfaBreak
    {
        /// <summary><c>pageArea</c>, <c>contentArea</c> or <c>auto</c>.</summary>
        public string TargetType = "auto";
        /// <summary>The page area's name, without the SOM prefix, or empty.</summary>
        public string Target = "";
    }

    internal sealed class XfaField : XfaBoxNode
    {
        /// <summary>The <c>ui</c> child's local name: <c>textEdit</c>, <c>checkButton</c>, <c>choiceList</c>, <c>button</c>, <c>imageEdit</c>, <c>signature</c>, <c>barcode</c>, …; empty for none.</summary>
        public string UiKind = "";
        public bool UiHasBorder;
        public bool MultiLine;
        public XfaCaption? Caption;
        /// <summary>The template's own value (<c>value/text</c>, <c>value/integer</c>, …), or empty.</summary>
        public string DefaultValue = "";
        public XfaImageValue? DefaultImage;
        /// <summary>Display texts (the <c>items</c> element without <c>save="1"</c>) and save values, by index.</summary>
        public readonly List<string> ItemDisplays = new List<string>(), ItemSaves = new List<string>();
        public string BindMatch = "once";
        public string BindRef = "";
        /// <summary>A checkButton's on-value (its first item), or "1".</summary>
        public string OnValue => ItemSaves.Count > 0 ? ItemSaves[0] : ItemDisplays.Count > 0 ? ItemDisplays[0] : "1";
    }

    internal sealed class XfaDraw : XfaBoxNode
    {
        public string Text = "";
        /// <summary>The value's rich text (an <c>exData</c> of <c>text/html</c>), its paragraphs and runs; <c>null</c> for plain text. <see cref="Text"/> is its text, flattened.</summary>
        public List<XfaRichParagraph>? Rich;
        public XfaImageValue? Image;
        public bool IsRectangle, IsLine, IsArc;
        /// <summary>A line's slope: <c>\</c> (the default, top-left to bottom-right) or <c>/</c>.</summary>
        public string LineSlope = "\\";
        /// <summary>
        /// The shape's own stroke and fill — a <c>rectangle</c>'s, <c>line</c>'s or <c>arc</c>'s <c>edge</c>
        /// and <c>fill</c>, read as a border's are (no <c>edge</c> is the default 0.5 pt black one) — or
        /// <c>null</c> when the value is not a shape. The draw's <c>border</c> is its frame, drawn apart.
        /// </summary>
        public XfaBorder? Shape;
        /// <summary>An arc's <c>startAngle</c> and <c>sweepAngle</c> in degrees (0 and 360 by default).</summary>
        public double ArcStart, ArcSweep = 360;
        /// <summary>An arc's <c>circular="1"</c>: a circle as wide as the content region's smaller side.</summary>
        public bool ArcCircular;
        public XfaCaption? Caption;
    }

    internal sealed class XfaContentArea
    {
        public double X, Y, W, H;
    }

    internal sealed class XfaPageArea
    {
        public string Name = "";
        public double PageWidth = 612, PageHeight = 792;
        public readonly List<XfaContentArea> ContentAreas = new List<XfaContentArea>();
        /// <summary>Draws, fields and subforms placed on the page itself — repeated on every page the area produces.</summary>
        public readonly List<XfaBoxNode> Decorations = new List<XfaBoxNode>();
        public int OccurMax = -1;
    }

    internal sealed class XfaPageSet
    {
        public readonly List<XfaPageArea> PageAreas = new List<XfaPageArea>();
    }

    /// <summary>
    /// The <c>template</c> packet of an XFA form, read into the boxes a layout needs: subforms with
    /// their layout and occurrence, fields with caption, value and interface, draws with text,
    /// image or shape, page areas with their content areas and medium — and nothing that runs.
    /// </summary>
    /// <remarks>
    /// <para><b>The grammar's source.</b> The XML Template Specification (XFA 3.3) is public at
    /// the PDF Association's normative-references mirror and is not in <c>docs/iso</c> (its host
    /// refuses scripted downloads). The element and attribute names, their allowed values and
    /// defaults here are taken from public statements of the same grammar: the templates
    /// the XFA form designer writes (public pixel-test fixtures, <c>tools/corpus-fetch</c>) and an
    /// open-source reader's element table, which declares every element's attributes
    /// with their option lists — <c>layout</c> is one of <c>position</c>, <c>lr-tb</c>, <c>rl-row</c>,
    /// <c>rl-tb</c>, <c>row</c>, <c>table</c>, <c>tb</c>; <c>presence</c> one of <c>visible</c>,
    /// <c>hidden</c>, <c>inactive</c>, <c>invisible</c>; a caption's <c>placement</c> one of
    /// <c>left</c>, <c>bottom</c>, <c>inline</c>, <c>right</c>, <c>top</c>; an <c>occur</c>'s
    /// <c>min</c> defaults to 1 and <c>max</c> to -1 (unbounded) under a page set; a font's
    /// <c>typeface</c> defaults to Courier and its <c>size</c> to 10pt. Where the two agree the
    /// value is taken as the grammar's; nothing here is a guess from recollection.</para>
    /// <para><b>Namespaces are matched by local name</b>, as <c>XfaDatasets</c> does: the template
    /// namespace carries its version (<c>…/xfa-template/2.8/</c>, <c>3.3</c>), and Designer writes
    /// processing instructions (<c>&lt;?templateDesigner …?&gt;</c>, <c>&lt;?renderCache …?&gt;</c>)
    /// everywhere, all of which are skipped.</para>
    /// <para><b>What is read past.</b> <c>event</c>, <c>script</c>, <c>calculate</c>, <c>validate</c>,
    /// <c>connect</c>, <c>assist</c>, <c>traversal</c>, <c>extras</c>, <c>desc</c>, <c>variables</c>:
    /// behaviour, not layout. <c>use</c>/<c>usehref</c> prototypes are resolved first, in the
    /// parsed copy (<see cref="XfaPrototypes"/>); one that cannot be is counted so the layout can
    /// report it.</para>
    /// <para><b>Hostile input.</b> The packet is parsed with the same XXE-hardened settings as the
    /// datasets packet, element depth is bounded, and every measurement goes through
    /// <see cref="XfaMeasure"/>.</para>
    /// </remarks>
    internal sealed class XfaTemplate
    {
        /// <summary>The template's root subform (the first <c>subform</c> under <c>template</c>).</summary>
        public XfaSubform Root = new XfaSubform { Layout = "tb" };

        /// <summary>How many <c>use</c>/<c>usehref</c> references were left unresolved (<see cref="XfaPrototypes"/>).</summary>
        public int UnresolvedPrototypes;

        /// <summary>Whether <see cref="XfaPrototypes.MaxAddedElements"/> or <see cref="XfaPrototypes.MaxSomExamined"/> stopped the expansion.</summary>
        public bool PrototypeBudgetExhausted;

        /// <summary>Elements the reader skipped because the tree went deeper than <see cref="MaxDepth"/>.</summary>
        public int DepthRefusals;

        /// <summary>Scripts met and not run: <c>event</c>, <c>calculate</c> and <c>validate</c> children of any box.</summary>
        public int Scripts;

        /// <summary>The text formatting the reader did not apply — rich text's styles and values, tab stops — by name, for the layout's report.</summary>
        public readonly HashSet<string> FormattingUnapplied = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Nesting beyond which a template's elements are not read — a form designer does not nest subforms a hundred deep; a hostile file might.</summary>
        internal const int MaxDepth = 64;

        /// <summary>Parses a <c>template</c> packet, or returns <c>null</c> when it is not well-formed XML or holds no template.</summary>
        public static XfaTemplate? Parse(byte[] packet)
        {
            XDocument? doc = Load(packet);
            if (doc?.Root == null) return null;

            XElement? templateEl = Local(doc.Root, "template") ? doc.Root : FindDescendant(doc.Root, "template", 4);
            if (templateEl == null) return null;

            var template = new XfaTemplate();
            var prototypes = XfaPrototypes.Expand(templateEl);
            template.UnresolvedPrototypes = prototypes.Unresolved;
            template.PrototypeBudgetExhausted = prototypes.BudgetExhausted;
            XElement? rootSubform = null;
            foreach (XElement e in templateEl.Elements())
                if (Local(e, "subform")) { rootSubform = e; break; }
            if (rootSubform == null) return null;

            template.Root = template.ReadSubform(rootSubform, 0);
            // The root flows top to bottom unless it says otherwise (Designer writes layout="tb").
            if (Attr(rootSubform, "layout") == null) template.Root.Layout = "tb";
            return template;
        }

        /// <summary>
        /// The deepest a template packet's XML may nest before it is refused. <c>XDocument.Load</c> builds
        /// the tree iteratively, but reading a value's text (<see cref="ReadValue"/>'s <c>AppendText</c>),
        /// cloning a prototype (<c>XfaPrototypes</c>'s <c>new XElement</c>, whose copy constructor recurses
        /// per level) and walking the subform tree all recurse once a level, so a packet nested thousands
        /// deep overflows the thread stack — uncatchably — well before <see cref="MaxDepth"/> or the
        /// element budget would stop the walk. A streaming pass at load refuses such a packet; 512 is far
        /// above any real template (a form nests a few dozen deep) and far below the stack's limit.
        /// </summary>
        internal const int MaxXmlDepth = Core.Forms.XfaDatasets.MaxXmlDepth;

        /// <summary>Column widths a subform's <c>columnWidths</c> is read for; a table has far fewer, and each is a measure parsed.</summary>
        internal const int MaxColumnWidths = 4096;

        /// <summary>Parses the packet with XXE prohibited and no resolver, as every XFA packet is read.</summary>
        private static XDocument? Load(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return null;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
            };
            try
            {
                // A streaming check before the tree is built, since XmlReader has no depth limit, the
                // per-level recursions below a deep tree overflow the stack uncatchably, and the tree is
                // what a small packet is costly as.
                if (!Core.Forms.XfaDatasets.IsWithinBounds(packet, settings)) return null;

                using var ms = new MemoryStream(packet, writable: false);
                using var reader = XmlReader.Create(ms, settings);
                return XDocument.Load(reader);
            }
            catch (XmlException) { return null; }
            catch (IOException) { return null; }
        }

        // ------------------------------------------------------------------ elements

        /// <summary>
        /// A subform set's members, as the set's parent's children: a set is a grouping of subforms
        /// with a relation, and its members lay out as if they were the parent's. A set inside a set
        /// (the grammar allows it) adds its own members the same way; it was
        /// read as a subform, positioned at its members' offsets in a box of no height, and bound to a
        /// data node by its name.
        /// </summary>
        private void AddSetMembers(XElement set, XfaSubform into, int depth)
        {
            if (depth >= MaxDepth) { DepthRefusals++; return; }
            foreach (XElement m in set.Elements())
            {
                if (Local(m, "subform")) into.Children.Add(ReadSubform(m, depth));
                else if (Local(m, "subformSet")) AddSetMembers(m, into, depth + 1);
            }
        }

        private XfaSubform ReadSubform(XElement e, int depth)
        {
            var s = new XfaSubform
            {
                IsArea = Local(e, "area"),
                IsExclGroup = Local(e, "exclGroup"),
            };
            ReadBox(e, s);
            s.Layout = Option(e, "layout", s.IsArea ? "position" : "position",
                "position", "lr-tb", "rl-row", "rl-tb", "row", "table", "tb");
            if (s.IsExclGroup && Attr(e, "layout") == null) s.Layout = "position";

            string? cols = Attr(e, "columnWidths");
            if (cols != null)
                foreach (string part in cols.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (s.ColumnWidths.Count >= MaxColumnWidths)
                    {
                        FormattingUnapplied.Add("columnWidths past " + MaxColumnWidths + " columns");
                        break;
                    }
                    s.ColumnWidths.Add(part == "-1" ? -1 : XfaMeasure.Parse(part, 0));
                }

            if (depth >= MaxDepth) { DepthRefusals++; return s; }

            foreach (XElement c in e.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "subform":
                    case "exclGroup":
                    case "area":
                        s.Children.Add(ReadSubform(c, depth + 1));
                        break;
                    case "subformSet":
                        AddSetMembers(c, s, depth + 1);
                        break;
                    case "field":
                        s.Children.Add(ReadField(c));
                        break;
                    case "draw":
                        s.Children.Add(ReadDraw(c));
                        break;
                    case "pageSet":
                        s.PageSet = ReadPageSet(c, depth + 1);
                        break;
                    case "occur":
                        s.OccurMin = Int(c, "min", 1);
                        s.OccurMax = Int(c, "max", 1);
                        if (s.OccurMax != -1 && s.OccurMax < s.OccurMin) s.OccurMax = s.OccurMin;
                        s.OccurInitial = Int(c, "initial", s.OccurMin);
                        break;
                    case "bind":
                        s.BindMatch = Option(c, "match", "once", "once", "dataRef", "global", "none");
                        s.BindRef = Attr(c, "ref") ?? "";
                        break;
                    case "breakBefore":
                        s.BreakBefore = ReadBreak(c, Attr(c, "targetType"), Attr(c, "target"));
                        break;
                    case "breakAfter":
                        s.BreakAfter = ReadBreak(c, Attr(c, "targetType"), Attr(c, "target"));
                        break;
                    case "break":
                        // The deprecated form: `before`/`after` name the target type directly.
                        if (Attr(c, "before") is string before && before != "auto")
                            s.BreakBefore = ReadBreak(c, before, Attr(c, "beforeTarget"));
                        if (Attr(c, "after") is string after && after != "auto")
                            s.BreakAfter = ReadBreak(c, after, Attr(c, "afterTarget"));
                        break;
                    case "proto":
                        break;
                }
            }
            return s;
        }

        private static XfaBreak ReadBreak(XElement c, string? targetType, string? target)
        {
            string type = targetType ?? "auto";
            if (type is "pageEven" or "pageOdd") type = "pageArea";
            return new XfaBreak { TargetType = type, Target = SomLeaf(target) };
        }

        private XfaField ReadField(XElement e)
        {
            var f = new XfaField();
            ReadBox(e, f);
            foreach (XElement c in e.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "ui":
                        foreach (XElement u in c.Elements())
                        {
                            if (Local(u, "extras") || Local(u, "picture")) continue;
                            f.UiKind = u.Name.LocalName;
                            f.UiHasBorder = Border(FindChild(u, "border"))?.Visible == true;
                            if (Local(u, "textEdit")) f.MultiLine = Attr(u, "multiLine") == "1";
                            break;
                        }
                        break;
                    case "caption":
                        f.Caption = ReadCaption(c);
                        break;
                    case "value":
                        ReadValue(c, out f.DefaultValue, out f.DefaultImage, out _, out _);
                        break;
                    case "items":
                        bool save = Attr(c, "save") == "1";
                        var list = save ? f.ItemSaves : f.ItemDisplays;
                        foreach (XElement item in c.Elements()) list.Add(item.Value.Trim());
                        break;
                    case "bind":
                        f.BindMatch = Option(c, "match", "once", "once", "dataRef", "global", "none");
                        f.BindRef = Attr(c, "ref") ?? "";
                        break;
                }
            }
            // A single items element without save="1" is both the display and the save list.
            if (f.ItemSaves.Count == 0 && f.ItemDisplays.Count > 0) f.ItemSaves.AddRange(f.ItemDisplays);
            return f;
        }

        private XfaDraw ReadDraw(XElement e)
        {
            var d = new XfaDraw();
            ReadBox(e, d);
            foreach (XElement c in e.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "value":
                        ReadValue(c, out d.Text, out d.Image, out XElement? shape, out d.Rich);
                        if (shape != null) ReadShape(shape, d);
                        break;
                    case "caption":
                        d.Caption = ReadCaption(c);
                        break;
                }
            }
            return d;
        }

        private XfaCaption ReadCaption(XElement e)
        {
            var cap = new XfaCaption
            {
                Placement = Option(e, "placement", "left", "left", "bottom", "inline", "right", "top"),
                Reserve = NonNegativeOrNull(XfaMeasure.ParseOrNull(Attr(e, "reserve"))),
                Visible = Option(e, "presence", "visible", "visible", "hidden", "inactive", "invisible") == "visible",
            };
            foreach (XElement c in e.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "value": ReadValue(c, out cap.Text, out _, out _, out cap.Rich); break;
                    case "font": cap.Font = ReadFont(c); break;
                    case "para": cap.Para = ReadPara(c); break;
                }
            }
            return cap;
        }

        private XfaPageSet ReadPageSet(XElement e, int depth)
        {
            var set = new XfaPageSet();
            if (depth >= MaxDepth) { DepthRefusals++; return set; }
            foreach (XElement c in e.Elements())
            {
                if (Local(c, "pageArea")) set.PageAreas.Add(ReadPageArea(c, depth + 1));
                else if (Local(c, "pageSet")) set.PageAreas.AddRange(ReadPageSet(c, depth + 1).PageAreas);
            }
            return set;
        }

        private XfaPageArea ReadPageArea(XElement e, int depth)
        {
            var page = new XfaPageArea { Name = Attr(e, "name") ?? "" };
            foreach (XElement c in e.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "contentArea":
                        page.ContentAreas.Add(new XfaContentArea
                        {
                            X = XfaMeasure.Parse(Attr(c, "x"), 0), Y = XfaMeasure.Parse(Attr(c, "y"), 0),
                            W = NonNegativeSize(XfaMeasure.Parse(Attr(c, "w"), 0)),
                            H = NonNegativeSize(XfaMeasure.Parse(Attr(c, "h"), 0)),
                        });
                        break;
                    case "medium":
                    {
                        // A non-positive or non-finite page side would reach PdfDocumentBuilder.AddPage
                        // (ArgumentOutOfRangeException "widthPt") or ModelMeasurements in every exporter;
                        // XfaMeasure admits a leading '-', so fall back to Letter rather than carry it through.
                        double shortSide = XfaMeasure.Parse(Attr(c, "short"), 612);
                        double longSide = XfaMeasure.Parse(Attr(c, "long"), 792);
                        if (!(shortSide > 0) || double.IsInfinity(shortSide)) shortSide = 612;
                        if (!(longSide > 0) || double.IsInfinity(longSide)) longSide = 792;
                        bool landscape = Attr(c, "orientation") == "landscape";
                        page.PageWidth = landscape ? Math.Max(shortSide, longSide) : Math.Min(shortSide, longSide);
                        page.PageHeight = landscape ? Math.Min(shortSide, longSide) : Math.Max(shortSide, longSide);
                        break;
                    }
                    case "occur":
                        page.OccurMax = Int(c, "max", -1);
                        break;
                    case "draw":
                        page.Decorations.Add(ReadDraw(c));
                        break;
                    case "field":
                        page.Decorations.Add(ReadField(c));
                        break;
                    case "subform":
                    case "area":
                    case "exclGroup":
                        if (depth < MaxDepth) page.Decorations.Add(ReadSubform(c, depth + 1)); else DepthRefusals++;
                        break;
                }
            }
            if (page.ContentAreas.Count == 0)
                page.ContentAreas.Add(new XfaContentArea { X = 0, Y = 0, W = page.PageWidth, H = page.PageHeight });
            return page;
        }

        // ------------------------------------------------------------------ shared pieces

        // A caller-built model refuses a negative or non-finite size at export with an exception naming a
        // parameter no caller passed; XfaMeasure admits a leading '-', so a size is clamped as it is read.
        private static double NonNegativeSize(double v) => (v > 0 && !double.IsInfinity(v)) ? v : 0;

        private static double? NonNegativeOrNull(double? v) =>
            v.HasValue && (v.Value < 0 || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) ? null : v;

        private void ReadBox(XElement e, XfaBoxNode node)
        {
            node.Name = Attr(e, "name") ?? "";
            node.X = XfaMeasure.Parse(Attr(e, "x"), 0);
            node.Y = XfaMeasure.Parse(Attr(e, "y"), 0);
            node.W = NonNegativeOrNull(XfaMeasure.ParseOrNull(Attr(e, "w")));
            node.H = NonNegativeOrNull(XfaMeasure.ParseOrNull(Attr(e, "h")));
            node.Presence = Option(e, "presence", "visible", "visible", "hidden", "inactive", "invisible");
            node.AnchorType = Option(e, "anchorType", "topLeft", "topLeft", "topCenter", "topRight", "middleLeft", "middleCenter",
                                     "middleRight", "bottomLeft", "bottomCenter", "bottomRight");
            node.Rotate = Angle(Attr(e, "rotate"), 0);

            foreach (XElement c in e.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "margin": node.Margin = ReadMargin(c); break;
                    case "border": node.Border = Border(c); break;
                    case "font": node.Font = ReadFont(c); break;
                    case "para": node.Para = ReadPara(c); break;
                    case "event":
                    case "calculate":
                    case "validate":
                        if (FindChild(c, "script") != null) Scripts++;
                        break;
                }
            }
        }

        private static XfaMargin ReadMargin(XElement c) => new XfaMargin
        {
            Top = XfaMeasure.Parse(Attr(c, "topInset"), 0),
            Bottom = XfaMeasure.Parse(Attr(c, "bottomInset"), 0),
            Left = XfaMeasure.Parse(Attr(c, "leftInset"), 0),
            Right = XfaMeasure.Parse(Attr(c, "rightInset"), 0),
        };

        /// <summary>A border: visible when its own presence is and at least one edge is drawn.</summary>
        private static XfaBorder? Border(XElement? c)
        {
            if (c == null) return null;
            var b = new XfaBorder();
            bool borderShown = Option(c, "presence", "visible", "visible", "hidden", "inactive", "invisible") == "visible";
            int edges = 0, visibleEdges = 0;
            foreach (XElement x in c.Elements())
            {
                switch (x.Name.LocalName)
                {
                    case "edge":
                        edges++;
                        bool shown = Option(x, "presence", "visible", "visible", "hidden", "inactive", "invisible") == "visible";
                        if (shown) visibleEdges++;
                        if (XfaMeasure.TryParse(Attr(x, "thickness"), out double t) && t > 0) b.Thickness = t;
                        if (XfaMeasure.Color(FindChild(x, "color") is XElement col ? Attr(col, "value") : null) is string ec) b.Color = ec;
                        break;
                    case "fill":
                        if (Option(x, "presence", "visible", "visible", "hidden", "inactive", "invisible") == "visible")
                            b.Fill = XfaMeasure.Color(FindChild(x, "color") is XElement fc ? Attr(fc, "value") : null) ?? "#FFFFFF";
                        break;
                }
            }
            // No edge element at all means the default edge: drawn.
            b.Visible = borderShown && (edges == 0 || visibleEdges > 0);
            return b;
        }

        private XfaFont ReadFont(XElement c)
        {
            var f = new XfaFont
            {
                Typeface = Attr(c, "typeface") ?? "Courier",
                Size = XfaMeasure.Parse(Attr(c, "size"), 10),
                Bold = Attr(c, "weight") == "bold",
                Italic = Attr(c, "posture") == "italic",
                Underline = Attr(c, "underline") is "1" or "2",
                // The properties chapter 27 maps to rich text's (kerning-mode, letter-spacing,
                // text-decoration, vertical-align, xfa-font-horizontal-scale).
                Kerning = Attr(c, "kerningMode") == "pair",
                LineThrough = Attr(c, "lineThrough") is "1" or "2",
                LetterSpacing = XfaMeasure.ParseOrNull(Attr(c, "letterSpacing")),
                BaselineShift = XfaMeasure.Parse(Attr(c, "baselineShift"), 0),
                HorizontalScale = XfaRichText.Percent(Attr(c, "fontHorizontalScale")),
                VerticalScale = XfaRichText.Percent(Attr(c, "fontVerticalScale")),
            };
            if (f.Size <= 0 || f.Size > 1000) f.Size = 10;
            if (FindChild(c, "fill") is XElement fill && FindChild(fill, "color") is XElement col)
                f.Color = XfaMeasure.Color(Attr(col, "value"));
            return f;
        }

        private XfaPara ReadPara(XElement c) => new XfaPara
        {
            HAlign = Option(c, "hAlign", "left", "left", "center", "justify", "justifyAll", "radix", "right"),
            VAlign = Option(c, "vAlign", "top", "top", "bottom", "middle"),
            SpaceAbove = XfaMeasure.Parse(Attr(c, "spaceAbove"), 0),
            SpaceBelow = XfaMeasure.Parse(Attr(c, "spaceBelow"), 0),
            MarginLeft = XfaMeasure.Parse(Attr(c, "marginLeft"), 0),
            MarginRight = XfaMeasure.Parse(Attr(c, "marginRight"), 0),
            TextIndent = XfaMeasure.Parse(Attr(c, "textIndent"), 0),
            LineHeight = XfaMeasure.ParseOrNull(Attr(c, "lineHeight")),
            TabStops = XfaTabStops.Parse(Attr(c, "tabStops"), FormattingUnapplied),
            TabDefault = XfaMeasure.ParseOrNull(Attr(c, "tabDefault")),
        };

        /// <summary>
        /// A <c>value</c>: its one-of child. Text-like children give text (an <c>exData</c> of
        /// <c>text/html</c> flattened to its text, paragraphs and breaks kept as line breaks); an
        /// <c>image</c> its decoded bytes; <c>rectangle</c>, <c>line</c> and <c>arc</c> their element.
        /// </summary>
        private void ReadValue(XElement value, out string text, out XfaImageValue? image, out XElement? shape, out List<XfaRichParagraph>? rich)
        {
            text = ""; image = null; shape = null; rich = null;
            foreach (XElement c in value.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "text":
                    case "integer":
                    case "decimal":
                    case "float":
                    case "boolean":
                    case "date":
                    case "time":
                    case "dateTime":
                        text = c.Value;
                        return;
                    case "exData":
                        if ((Attr(c, "contentType") ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                        {
                            text = HtmlText(c);
                            rich = XfaRichText.Read(c, FormattingUnapplied);
                        }
                        else text = c.Value;
                        return;
                    case "image":
                        image = ReadImage(c);
                        return;
                    case "rectangle":
                    case "line":
                    case "arc":
                        shape = c;
                        return;
                }
            }
        }

        /// <summary>
        /// A draw's shape: its kind, its own edge and fill, a line's slope and an arc's angles (chapter
        /// 17's <c>rectangle</c>, <c>line</c> and <c>arc</c>). An angle that is not a finite number is the
        /// default.
        /// </summary>
        private static void ReadShape(XElement shape, XfaDraw d)
        {
            switch (shape.Name.LocalName)
            {
                case "rectangle": d.IsRectangle = true; break;
                case "line": d.IsLine = true; d.LineSlope = Attr(shape, "slope") == "/" ? "/" : "\\"; break;
                case "arc":
                    d.IsArc = true;
                    d.ArcStart = Angle(Attr(shape, "startAngle"), 0);
                    d.ArcSweep = Angle(Attr(shape, "sweepAngle"), 360);
                    d.ArcCircular = Attr(shape, "circular") == "1";
                    break;
            }
            d.Shape = Border(shape);
        }

        private static double Angle(string? text, double fallback)
            => text != null && text.Length <= 32
               && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
               && !double.IsNaN(a) && !double.IsInfinity(a)
                ? a : fallback;

        /// <summary><paramref name="text"/> with every white-space character removed — a base64 body's line breaks and indentation. A loop, not a pattern: the body is the file's.</summary>
        internal static string WithoutWhiteSpace(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
                if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            return sb.ToString();
        }

        private static XfaImageValue? ReadImage(XElement c)
        {
            string encoding = Attr(c, "transferEncoding") ?? "base64";
            string body = c.Value.Trim();
            if (body.Length == 0 || encoding == "package") return null;
            byte[] data;
            if (encoding == "base64")
            {
                try { data = Convert.FromBase64String(WithoutWhiteSpace(body)); }
                catch (FormatException) { return null; }
            }
            else data = Encoding.UTF8.GetBytes(body);
            return new XfaImageValue { Data = data, ContentType = Attr(c, "contentType") ?? "" };
        }

        /// <summary>The text of an XHTML fragment: block elements and <c>br</c> become line breaks, everything else its text.</summary>
        internal static string HtmlText(XElement html)
        {
            var sb = new StringBuilder();
            Walk(html, sb, 0);
            return sb.ToString().Trim('\n', '\r', ' ');

            static void Walk(XElement e, StringBuilder sb, int depth)
            {
                if (depth > 64) return;
                foreach (XNode n in e.Nodes())
                {
                    if (n is XText t) { sb.Append(t.Value); continue; }
                    if (n is not XElement x) continue;
                    string name = x.Name.LocalName;
                    if (name == "br") { sb.Append('\n'); continue; }
                    bool block = name is "p" or "div" or "li" or "tr";
                    if (block && sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    Walk(x, sb, depth + 1);
                    if (block) sb.Append('\n');
                }
            }
        }

        // ------------------------------------------------------------------ attribute helpers

        private static bool Local(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal);

        private static string? Attr(XElement e, string name) => XfaXml.Attr(e, name);

        /// <summary>An enumerated attribute: its value when it is one of <paramref name="allowed"/>, else <paramref name="fallback"/>.</summary>
        private static string Option(XElement e, string name, string fallback, params string[] allowed)
        {
            string? v = Attr(e, name);
            if (v == null) return fallback;
            foreach (string a in allowed) if (string.Equals(a, v, StringComparison.Ordinal)) return v;
            return fallback;
        }

        private static int Int(XElement e, string name, int fallback)
            => int.TryParse(Attr(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static XElement? FindChild(XElement? e, string name)
        {
            if (e == null) return null;
            foreach (XElement c in e.Elements()) if (Local(c, name)) return c;
            return null;
        }

        private static XElement? FindDescendant(XElement e, string name, int maxDepth)
        {
            if (maxDepth < 0) return null;
            foreach (XElement c in e.Elements())
            {
                if (Local(c, name)) return c;
                XElement? found = FindDescendant(c, name, maxDepth - 1);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>The last name of a SOM expression (<c>xfa.template.form1.Page2</c> → <c>Page2</c>; <c>#pageSet.Page1</c> → <c>Page1</c>).</summary>
        internal static string SomLeaf(string? som)
        {
            if (string.IsNullOrWhiteSpace(som)) return "";
            string s = Core.Forms.FieldNames.Leaf(som!.Trim());
            int bracket = s.IndexOf('[');
            if (bracket >= 0) s = s.Substring(0, bracket);
            return s.TrimStart('#', '$');
        }
    }
}
