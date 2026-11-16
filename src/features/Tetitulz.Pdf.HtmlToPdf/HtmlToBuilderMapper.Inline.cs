// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= inline

        private sealed class Style
        {
            public bool Bold, Italic, Underline, Strike, Sup, Sub, Mono, Serif, AllCaps, SmallCaps, Pre;

            /// <summary>
            /// <c>white-space: pre-line</c> — keep newlines, but <b>collapse</b> runs of spaces.
            /// Only meaningful alongside <see cref="Pre"/>, which is what preserves the newlines;
            /// this is the half that says the spaces are still collapsed. Without it
            /// <c>pre-line</c> mapped onto full <c>pre</c> and kept every space CSS requires it to
            /// collapse.
            /// </summary>
            public bool PreCollapseSpaces;

            /// <summary>
            /// The writing direction in force, inherited from the nearest ancestor that declared
            /// one (via <c>dir</c> or CSS <c>direction</c>).
            ///
            /// <para>It lives on the inherited style precisely because <c>dir</c> inherits in
            /// HTML. Read off the paragraph element alone, the overwhelmingly common authoring
            /// pattern — <c>&lt;div dir="rtl"&gt;</c> wrapping the content — would render every
            /// paragraph inside it left-to-right.</para>
            /// </summary>
            public M.TextDirection? Direction;

            /// <summary>
            /// The first family the author named, verbatim — e.g. <c>"David"</c>. Kept alongside
            /// the sans/serif/mono bucket below, which is all the standard-14 fallback can use:
            /// without the real name a document asking for a specific Hebrew face could only ever
            /// get whichever installed font happened to cover Hebrew first.
            /// </summary>
            public string? FontFamily;
            public string? Color;
            /// <summary>
            /// <c>color</c> is fully transparent (<c>transparent</c>, <c>rgba(…, 0)</c>): the text is
            /// laid out and paints nothing (<see cref="M.TextRun.IsInvisible"/>). Inherited, as
            /// <c>color</c> is, until a descendant's own colour ends it. <see cref="Color"/> keeps
            /// the last visible colour, which a browser also does not show.
            /// </summary>
            public bool Invisible;
            public string? Highlight;

            /// <summary>
            /// The inline box the runs are drawn inside — shared by every run of the element that
            /// declared it, and by its inline descendants, which is how a box spans them all.
            /// </summary>
            public M.InlineBox? Box;
            public double Size = DefaultFontSizePt;

            /// <summary>
            /// CSS <c>line-height</c> in force, inherited from the nearest element that declared it:
            /// a unitless number as the <b>factor</b> (a descendant multiplies its own font size, as
            /// CSS inherits the number), a length or percentage as <b>points</b> computed where it was
            /// declared. Both 0 is <c>normal</c> — the exporter's own line height.
            /// </summary>
            /// <remarks>
            /// Inherited, not read off each paragraph's own element only: Bootstrap's one
            /// <c>body { line-height: 1.42857 }</c> reaches every line of the page and every table
            /// row, which would otherwise be a fifth shorter than a browser's.
            /// </remarks>
            public double LineHeightFactor, LineHeightPt;

            /// <summary>The line height, in points, a paragraph of this style's font size gets; 0 for <c>normal</c>.</summary>
            public double LineHeight => LineHeightFactor > 0 ? LineHeightFactor * Size : LineHeightPt;

            public double LetterSpacing;

            /// <summary>
            /// <c>font-kerning: none</c> (inherited). Kerning is on otherwise — <c>auto</c>, the initial
            /// value, is what browsers kern by — and reaches the page where the run's font is embedded.
            /// </summary>
            public bool NoKerning;

            /// <summary>
            /// <c>font-variant-ligatures</c> naming <c>none</c> or <c>no-contextual</c> (inherited).
            /// Contextual alternates are on otherwise — browsers draw them by default — and reach the
            /// page where the run's font is embedded.
            /// </summary>
            public bool NoContextual;

            /// <summary>
            /// <c>font-variant-ligatures</c> naming <c>none</c> or <c>no-common-ligatures</c> (inherited).
            /// The common ligatures (fi, fl) are formed otherwise, as browsers form them, where the run's
            /// font is embedded and has them.
            /// </summary>
            public bool NoLigatures;

            /// <summary><c>font-variant-ligatures</c> naming <c>discretionary-ligatures</c> (inherited): the font's <c>dlig</c>. Off by default, as in a browser.</summary>
            public bool DiscretionaryLigatures;

            /// <summary><c>font-variant-ligatures</c> naming <c>historical-ligatures</c> (inherited): the font's <c>hlig</c>. Off by default, as in a browser.</summary>
            public bool HistoricalLigatures;
            public string? Link;

            /// <summary>
            /// The <b>accumulated</b> CSS <c>opacity</c> in force — every ancestor's factor
            /// multiplied together, which is what CSS means: the property itself does not inherit,
            /// but its effect applies to the whole subtree and nesting compounds it.
            ///
            /// <para>It rides on the inherited style for exactly that reason: <see cref="Clone"/>
            /// carries the product down, and <c>ApplyInlineStyle</c> multiplies each element's own
            /// declaration in once. Assigning here instead would let a nested
            /// <c>opacity: .5</c> inside another one render at 0.5 rather than 0.25.</para>
            /// </summary>
            public double Opacity = 1.0;

            /// <summary>
            /// The value <see cref="Opacity"/> had at the nearest enclosing <b>block</b>, which is
            /// what <c>NewPara</c> hands to <c>Paragraph.Opacity</c>.
            ///
            /// <para>It exists because the exporter <i>multiplies</i> a paragraph's opacity by its
            /// runs': handing the accumulated value to both would square it, so an ordinary
            /// <c>&lt;p style="opacity:.5"&gt;</c> would draw its text at 0.25 while its shading
            /// drew at 0.5. A run therefore carries only what it added — see
            /// <c>RelativeOpacity</c>. Maintained by <c>DeriveStyle</c>, which is the block
            /// boundary: inline elements clone rather than derive.</para>
            /// </summary>
            public double BlockOpacity = 1.0;

            /// <summary>
            /// CSS <c>orphans</c> and <c>widows</c> in force — inherited, as CSS defines them, and 2
            /// until something says otherwise: the initial value, which a browser applies to every
            /// paragraph it breaks across a page.
            /// </summary>
            public int Orphans = 2, Widows = 2;

            /// <summary>CSS <c>hyphens: auto</c> in force — inherited, <c>manual</c> by default.</summary>
            public bool HyphensAuto;

            /// <summary>The <c>lang</c> in force — the nearest ancestor's attribute, else the document's.</summary>
            public string Language = "";
            public Style Clone() => (Style)MemberwiseClone();
        }

        // Computes the inline style for a block element's content: inherits the ancestor style,
        // applies an optional size / bold default, then the element's own declarations. Block
        // background, links and (text) highlight do NOT inherit, so they are cleared.
        private Style DeriveStyle(Style parent, IElement? el, double? sizeOverride, bool boldDefault)
        {
            var st = parent.Clone();
            // A tag's default size (a heading's, <pre>'s) only where the CSS sets none, so a
            // declared percentage or em is of the parent's size, as CSS computes it.
            if (sizeOverride.HasValue && (el == null || !DeclaresFontSize(el))) st.Size = sizeOverride.Value;
            if (boldDefault) st.Bold = true;
            st.Highlight = null;
            st.Link = null;
            // No `st.Box = null`: only an inline element's own style ever carries a box, and a
            // block is never derived from one — a mutation removing that line survived the suite.
            if (el != null) { ApplyDirection(el, st); ApplyInlineStyle(el.GetAttribute("style"), st); ApplyLineHeight(el, st); }
            if (el?.GetAttribute("lang") is string lang) st.Language = lang;
            st.Highlight = null; // a block's background-color is paragraph shading, not text highlight
            // This IS the block boundary — inline elements Clone() instead of deriving — so this is
            // the one place that can record what the enclosing Paragraph will carry.
            st.BlockOpacity = st.Opacity;
            return st;
        }

        /// <summary>Whether <paramref name="el"/>'s CSS sets its font size, by the longhand or the <c>font</c> shorthand.</summary>
        private static bool DeclaresFontSize(IElement el) =>
            StyleValue(el, "font-size") != null || StyleValue(el, "font") != null;

        private void CollectInline(INode node, M.Paragraph para, Style st, int depth)
        {
            if (TooDeep(depth)) return;

            // ::before and ::after wrap the element's own children, and they are added HERE rather
            // than at the six block-level callers for two reasons: one place cannot drift, and this
            // method is also how an inline element recurses, so a <span> with generated content
            // works by the same code that makes a <p> work.
            //
            // Both APPEND, and the call order does the rest — the before-run is generated before
            // the children are walked. Inserting at index 0 instead would put it in front of
            // whatever an earlier sibling had already contributed to this paragraph.
            var el = node as IElement;
            if (el != null) AddGeneratedContent(el, para, st, after: false);

            foreach (var child in node.ChildNodes) CollectInlineNode(child, para, st, depth);

            if (el != null) AddGeneratedContent(el, para, st, after: true);
        }

        private void CollectInlineNode(INode child, M.Paragraph para, Style st, int depth)
        {
            if (TooDeep(depth)) return;
            if (!TakeNode()) return;

            if (child.NodeType == NodeType.Text)
            {
                string raw = child.TextContent ?? "";
                if (st.Pre)
                {
                    // Preformatted: preserve spaces, turn newlines into line breaks, tabs → spaces.
                    // `pre-line` keeps the newlines but collapses the spaces within each line —
                    // the same collapse the non-preformatted path below applies, and the only
                    // thing separating the two white-space values.
                    var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) para.Runs.Add(new M.LineBreak());
                        string ln = st.PreCollapseSpaces
                            ? CollapseSpaces(lines[i])
                            : lines[i].Replace("\t", "    ");
                        if (ln.Length > 0) para.Runs.Add(MakeRun(ln, st));
                    }
                    return;
                }
                string text = CollapseSpaces(raw);
                // A collapsible space at the start of a line is removed (CSS Text 3 §4.1.2). This
                // dropped only a whitespace-only node opening the paragraph, so indented source —
                // `<td>\n  Label` — drew a space before its first word, and the newline after a
                // closing `<br>` became a space run that the layout had to put on a line of its
                // own: every `Label<br>` cell of a form was two lines tall.
                if (text.Length > 0 && text[0] == ' ' && AtLineStart(para)) text = text.Substring(1);
                if (text.Length == 0) return;
                para.Runs.Add(MakeRun(text, st));
                return;
            }
            if (child is not IElement el) return;
            if (IsHidden(el)) return; // display:none / hidden / visibility:hidden

            string tag = el.LocalName.ToLowerInvariant();
            if (tag == HtmlTags.Br) { para.Runs.Add(new M.LineBreak()); return; }
            if (tag == HtmlTags.Img)
            {
                var imgRun = MapImage(el);
                if (imgRun != null)
                {
                    // Padding, border and background round the picture — Bootstrap's .img-thumbnail.
                    imgRun.Box = ResolveInlineBox(el, st.Size, rounds: true);
                    para.Runs.Add(imgRun);
                }
                return;
            }
            if (tag == HtmlTags.Canvas) { para.Runs.Add(MapCanvas(el, st.Size)); return; }
            if (tag == HtmlTags.Svg)
            {
                // Block-context <svg> also reaches here (svg is not a BlockTag), so this single
                // hook covers both inline and block placement; the image is drawn as a block.
                var svgRun = MapSvg(el);
                if (svgRun != null) para.Runs.Add(svgRun);
                return;
            }
            if (tag == HtmlTags.Script || tag == HtmlTags.Style || tag == HtmlTags.Head) return;

            // Form controls are intercepted here, before the generic "recurse into unknown
            // elements" fallback below reaches them. Without this a <select> emits EVERY option
            // into the text flow, and a <textarea>'s default value leaks as ordinary prose —
            // both of which look like content the author never wrote.
            if (MapFormControl(el, tag, para, st)) return;
            // <noscript> renders only when scripting is off — the same rule a browser applies, so
            // the two engines agree, and its body text does not leak into every document.
            if (tag == HtmlTags.NoScript && _options.JavaScriptEnabled) return;
            if (BlockTags.Contains(tag))
            {
                // A block nested in inline flow: treat its text inline (best-effort).
                CollectInline(el, para, st, depth + 1);
                return;
            }

            var ns = st.Clone();

            // <bdo>, <bdi> and CSS unicode-bidi are expressed as the Unicode control characters
            // that mean them, which UnicodeBidi.AssignEmbeddingLevels already understands. Wrapping
            // the content is the whole implementation — the renderer strips the controls before
            // drawing, so they change the level resolution without ever appearing on the page or
            // in /ToUnicode.
            // `ns`, not `st`: DeriveStyle has already folded this element's own `dir`/`direction`
            // into it, so it carries the COMPUTED direction — which is what unicode-bidi's
            // embedding direction is defined against, and which inherits.
            var (bidiOpen, bidiClose) = ResolveBidiOverride(el, tag, ns);
            if (bidiOpen != '\0')
            {
                para.Runs.Add(MakeRun(bidiOpen.ToString(), ns));
                CollectInline(el, para, ns, depth + 1);
                para.Runs.Add(MakeRun(bidiClose.ToString(), ns));
                return;
            }

            switch (tag)
            {
                case HtmlTags.B: case HtmlTags.Strong: ns.Bold = true; break;
                case HtmlTags.I: case HtmlTags.Em: case HtmlTags.Cite: case HtmlTags.Dfn: case HtmlTags.Var: ns.Italic = true; break;
                case HtmlTags.U: case HtmlTags.Ins: ns.Underline = true; break;
                case HtmlTags.S: case HtmlTags.Strike: case HtmlTags.Del: ns.Strike = true; break;
                case HtmlTags.Sup: ns.Sup = true; break;
                case HtmlTags.Sub: ns.Sub = true; break;
                case HtmlTags.Mark: if (ns.Highlight == null) ns.Highlight = MarkHighlightColor; break;
                // The tag's own size only where the CSS sets none: a declared percentage is of the
                // parent's size, and applying both made Bootstrap's `h3 small { font-size: 65% }` 52%.
                case HtmlTags.Small: if (!DeclaresFontSize(el)) ns.Size *= 0.8; break;
                case HtmlTags.Big: if (!DeclaresFontSize(el)) ns.Size *= 1.2; break;
                // Monospace replaces an inherited family, a named one included (the element's own
                // CSS, applied below, can still name another).
                case HtmlTags.Code: case HtmlTags.Kbd: case HtmlTags.Samp: case HtmlTags.Tt: ns.Mono = true; ns.Serif = false; ns.FontFamily = null; break;
                case HtmlTags.A:
                    ns.Link = el.GetAttribute("href");
                    // Browser-default link appearance (overridable by the element's own CSS,
                    // applied below). Anchors without an href are left unstyled.
                    if (!string.IsNullOrEmpty(ns.Link)) { ns.Color = LinkColor; ns.Invisible = false; ns.Underline = true; }
                    break;
                case HtmlTags.Font: ApplyFontTag(el, ns); break;
            }
            ApplyInlineStyle(el.GetAttribute("style"), ns);
            M.InlineBox? inlineBox = ResolveInlineBox(el, ns.Size);
            // An inline-block's margin box is part of its line (InlineBox.OccupiesLine): a wrapped
            // row of tags is spaced by their margins, which an inline box's never are vertically.
            if (Eq(StyleValue(el, "display")?.Trim(), "inline-block"))
            {
                var (mt, mr, mb, ml) = ResolveSides(el, "margin", ns.Size);
                if (inlineBox != null || mt > 0 || mr > 0 || mb > 0 || ml > 0)
                {
                    inlineBox ??= new M.InlineBox();
                    inlineBox.OccupiesLine = true;
                    (inlineBox.MarginTop, inlineBox.MarginRight, inlineBox.MarginBottom, inlineBox.MarginLeft)
                        = (Math.Max(0, mt), Math.Max(0, mr), Math.Max(0, mb), Math.Max(0, ml));
                }
            }
            if (inlineBox is { } box)
            {
                ns.Box = box;
                // The box paints the element's background over its padding; a run highlight would
                // paint it again, tighter, over the text alone.
                if (box.Background.Length > 0) ns.Highlight = null;
            }

            int before = para.Runs.Count;
            CollectInline(el, para, ns, depth + 1);

            // A box with no content still draws: an icon painted as the background of an empty,
            // padded <i> — a checklist's ticks — is nothing but its box. An empty run carries it.
            if (ns.Box != null && para.Runs.Count == before)
                para.Runs.Add(MakeRun("", ns));

            // A floated element's runs stay in the line for now; lifted once the paragraph has a place.
            RecordInlineFloat(el, para, before);
        }

        /// <summary>
        /// The box an inline element draws around its content — its border, padding, background
        /// colour and image, and corner radius — or <c>null</c> when it declares none of a border,
        /// padding or a background image.
        /// </summary>
        /// <remarks>
        /// <para>A background colour alone stays a run highlight, as it always was: a box is created
        /// only for what a highlight cannot express. Bootstrap's <c>.label</c>, <c>.badge</c>,
        /// <c>.btn</c> and <c>code</c> all have padding, and in print its labels have a border; they
        /// do not print as plain text. A background image is one of those things.</para>
        /// <para><b>The border is drawn only when every edge is.</b> The model carries one border per
        /// box. Taking the first drawn edge for all four, a lone <c>border-bottom</c> — a rule under
        /// a caption — would draw a closed box around the text; an edge the model cannot draw
        /// on its own is left out instead, and its padding still takes room.</para>
        /// <para>An image (<paramref name="rounds"/>) takes a box for its corner radius alone:
        /// Bootstrap's <c>.img-circle</c> is nothing but <c>border-radius: 50%</c>, and the box is
        /// what clips the picture to it.</para>
        /// </remarks>
        private M.InlineBox? ResolveInlineBox(IElement el, double fontSize, bool rounds = false)
        {
            M.Border? border = null;
            foreach (BorderEdge edge in ResolveBorderEdges(el))
            {
                if (edge.ToBorder() is not { } drawn) { border = null; break; }
                border ??= drawn;
            }

            var (top, right, bottom, left) = ResolveSides(el, "padding", fontSize);
            M.BackgroundImage? image = ResolveBackgroundImage(el);
            var radius = ResolveBorderRadius(el, fontSize);
            bool rounded = rounds && radius != null;
            if (border == null && image == null && top + right + bottom + left <= 0 && !rounded) return null;

            var box = new M.InlineBox
            {
                Border = border,
                Background = ResolveBackgroundColor(el) ?? "",
                BackgroundImage = image,
                PaddingTop = top, PaddingRight = right, PaddingBottom = bottom, PaddingLeft = left,
            };

            // The box's corners as a paragraph's are read: one radius, four, their vertical
            // radii, one percentage or per-corner ones. It took the largest corner's radius for all
            // four before, so a tab rounded at the top only was a pill.
            if (radius is { } r)
            {
                box.CornerRadiusPercent = r.Percent;
                box.CornerRadiusPt = r.Uniform; box.CornerRadiiPt = r.Corners; box.CornerRadiiYPt = r.CornersY;
                box.CornerRadiiPercent = r.Percents; box.CornerRadiiYPercent = r.PercentsY;
            }
            return box;
        }

        // Maps an <img> to an inline image: a data: URI (base64) always; a local file only when a
        // base directory is in force (relative / rooted / file: URI that resolves inside it); and a
        // remote http(s) source only through HtmlToPdfOptions.RemoteResourceLoader.
        private M.ImageRun? MapImage(IElement img)
        {
            // srcset first: it names better candidates than src, which is only the fallback for a
            // browser that does not understand srcset. Print wants the highest-resolution one that
            // actually resolves.
            string? src = ResolveBestCandidate(img) ?? img.GetAttribute("src");
            if (string.IsNullOrEmpty(src)) return null;

            byte[]? data;
            bool isDataUri = src!.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            bool isRemote = !isDataUri && IsRemoteUrl(src);
            if (isDataUri) data = DecodeDataUri(src);
            else if (isRemote) data = LoadRemote(src, "image", BuilderDiagnosticKind.ImageDecodeFailed);
            else data = LoadLocalImage(src);
            if (data == null || data.Length == 0)
            {
                // Local and unreadable: outside BasePath, no BasePath set, or missing. Silence here
                // was the same defect as the remote case — the image simply vanished and the caller
                // could not tell a correctly-converted document from one that lost its branding.
                // A remote source has already said why, in LoadRemote.
                if (!isDataUri && !isRemote) ReportResourceDeclined("image", src);
                return null;
            }

            // An SVG data URI decodes perfectly and is then not a raster image at all; the image
            // decoder would refuse it and leave a sized blank hole. Route it to the same opt-in
            // rasterizer an inline <svg> uses. An SVG *file* — local or remote — is the same
            // picture by a third route, told by its bytes, not only by a data URI's media type.
            if ((isDataUri && DataUriMediaType(src) == "image/svg+xml") || LooksLikeSvg(data!))
            {
                if (_options.SvgRasterizer == null)
                {
                    ReportDiagnostic(BuilderDiagnosticKind.ImageDecodeFailed,
                        $"<img src=\"{Excerpt(src!)}\"> is an SVG image and HtmlToPdfOptions.SvgRasterizer is not set; " +
                        "it is absent from the output.");
                    return null;
                }
                return MapSvgMarkup(Encoding.UTF8.GetString(data!),
                    img.GetAttribute("width") ?? StyleValue(img, "width"),
                    img.GetAttribute("height") ?? StyleValue(img, "height"),
                    "<img src=\"" + Excerpt(src!) + "\">");
            }

            // Decodability is decided HERE, where the src is known, rather than in the exporter,
            // which sees only bytes. An undecodable image sized from its header and then silently
            // dropped at draw time would leave a correctly-sized blank hole — the most confusing
            // possible failure, because the gap looks deliberate.
            if (!BuilderImageDecoder.CanDecode(data!, out string? why))
            {
                ReportDiagnostic(BuilderDiagnosticKind.ImageDecodeFailed,
                    $"<img src=\"{Excerpt(src!)}\"> was not embedded: {why}.");
                return null;
            }

            string? wAttr = img.GetAttribute("width") ?? StyleValue(img, "width");
            string? hAttr = img.GetAttribute("height") ?? StyleValue(img, "height");
            int iw = 0, ih = 0;
            var orientation = M.ImageOrientation.Normal;
            if (RasterImageInspector.TryInspect(data!, out var info))
            {
                // CSS `image-orientation: from-image`, a browser's default: the EXIF orientation
                // turns the stored pixels, and a quarter turn exchanges the shown width and height.
                // A phone stores an upright photo sideways and says so here; ignoring it printed
                // the photo on its side. `image-orientation: none` asks for the pixels as stored.
                bool apply = !Eq(StyleValue(img, "image-orientation")?.Trim(), "none");
                bool swap = apply && info.ExifSwapsAxes;
                iw = swap ? info.Height : info.Width;
                ih = swap ? info.Width : info.Height;
                if (apply) orientation = (M.ImageOrientation)info.ExifOrientation;
            }
            var run = BuildSizedImageRun(data, wAttr, hAttr, iw, ih);
            if (run != null)
            {
                run.Orientation = orientation;
                // HTML defines alt="" as "this image is decorative". Absent and empty are
                // therefore different answers, and the model keeps them apart.
                string? alt = img.GetAttribute("alt");
                if (alt != null)
                {
                    if (alt.Length == 0) run.IsDecorative = true;
                    else run.Alt = alt;
                }
            }
            return run;
        }

        /// <summary>
        /// A <c>&lt;canvas&gt;</c> as the blank box it is before a script draws on it: its own size,
        /// its padding, border and background, and no picture (ReportCanvas says why).
        /// </summary>
        /// <remarks>
        /// <para><b>Its <c>width</c> and <c>height</c> attributes are its intrinsic size</b> — CSS
        /// pixels, 300 × 150 when absent (HTML §4.12.5) — and CSS <c>width</c>/<c>height</c> scale
        /// it as they scale an image. Not an <c>&lt;img&gt;</c>'s rule, where the attribute is a
        /// presentational hint: here it is the bitmap's size.</para>
        /// <para>It was an unknown inline element with no content: its border drew as a sliver and
        /// the box took no room, so everything after a signature pad moved up into its place.</para>
        /// </remarks>
        private M.ImageRun MapCanvas(IElement canvas, double fontSize)
        {
            int intrinsicW = CanvasDimension(canvas.GetAttribute("width"), DefaultCanvasWidthPx);
            int intrinsicH = CanvasDimension(canvas.GetAttribute("height"), DefaultCanvasHeightPx);
            // A zero-sized bitmap is an empty box, not one of the fallback size an unsized image gets.
            if (intrinsicW == 0 || intrinsicH == 0)
                return new M.ImageRun { Data = Array.Empty<byte>(), Box = new M.InlineBox() };
            var run = BuildSizedImageRun(Array.Empty<byte>(),
                StyleValue(canvas, "width"), StyleValue(canvas, "height"), intrinsicW, intrinsicH);
            run.Box = ResolveInlineBox(canvas, fontSize) ?? new M.InlineBox();
            return run;
        }

        /// <summary>A canvas's width when the attribute is absent (HTML §4.12.5).</summary>
        private const int DefaultCanvasWidthPx = 300;

        /// <summary>A canvas's height when the attribute is absent (HTML §4.12.5).</summary>
        private const int DefaultCanvasHeightPx = 150;

        /// <summary>
        /// A canvas <c>width</c>/<c>height</c> attribute: a valid non-negative integer, else the
        /// default — HTML's rule for these two, which is not a CSS length.
        /// </summary>
        private static int CanvasDimension(string? value, int fallback) =>
            int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : fallback;

        // Maps an inline <svg> to an embedded raster image via the opt-in
        // HtmlToPdfOptions.SvgRasterizer (null → skipped, no SkiaSharp dependency). Display size
        // comes from the SVG's own width/height (else its viewBox), NOT the rasterized pixels — so
        // it is independent of the rasterizer's resolution.
        private M.ImageRun? MapSvg(IElement svg)
        {
            if (_options.SvgRasterizer == null) return null; // SVG disabled (default)

            // AngleSharp's serialiser recurses, and a stack overflow ends the process: the subtree
            // is measured first, with an explicit stack. The mapper's walk never enters an <svg>, so
            // MaxDepth does not bound it, and the markup scan admits MaxMarkupNesting.
            int depth = SubtreeDepth(svg);
            if (depth > MaxSvgSerialisationDepth)
            {
                ReportDiagnostic(BuilderDiagnosticKind.ImageDecodeFailed,
                    "an inline <svg> nests " + depth + " elements deep, past the " + MaxSvgSerialisationDepth +
                    " its markup is serialised to; it is absent from the output.");
                return null;
            }
            string markup = SvgMarkup(svg);
            byte[]? data = Rasterize(markup, "an inline <svg>");
            if (data == null) return null;

            string? wAttr = svg.GetAttribute("width") ?? StyleValue(svg, "width");
            string? hAttr = svg.GetAttribute("height") ?? StyleValue(svg, "height");
            (int vbW, int vbH) = ParseViewBoxSize(svg.GetAttribute("viewBox"));
            return BuildSizedImageRun(data, wAttr, hAttr, vbW, vbH);
        }

        /// <summary>
        /// Runs the opt-in SVG rasterizer, reporting the SVG as undecodable when it throws or returns
        /// nothing — the silent <c>catch { data = null; }</c> dropped a malformed <c>&lt;svg&gt;</c> (an
        /// unclosed tag, a DOCTYPE the hardened reader refuses) from the output with nothing on OnDiagnostic.
        /// </summary>
        private byte[]? Rasterize(string markup, string what)
        {
            try
            {
                byte[]? data = _options.SvgRasterizer!(markup);
                if (data != null && data.Length > 0) return data;
                ReportDiagnostic(BuilderDiagnosticKind.ImageDecodeFailed,
                    what + " could not be rasterized: HtmlToPdfOptions.SvgRasterizer returned nothing. It is absent from the output.");
                return null;
            }
            catch (Exception ex)
            {
                ReportDiagnostic(BuilderDiagnosticKind.ImageDecodeFailed,
                    what + " could not be rasterized (" + ex.GetType().Name + ": " + ex.Message + "). It is absent from the output.");
                return null;
            }
        }

        /// <summary>
        /// Rasterizes SVG markup that did not come from an element in the document — an
        /// <c>&lt;img src="data:image/svg+xml,…"&gt;</c>. Shares the opt-in rasterizer and the
        /// intrinsic-size rules with <see cref="MapSvg"/> so the two placements cannot differ.
        /// </summary>
        private M.ImageRun? MapSvgMarkup(string markup, string? wAttr, string? hAttr, string what)
        {
            if (_options.SvgRasterizer == null) return null;   // SVG disabled (default)
            byte[]? data = Rasterize(markup, what);
            if (data == null) return null;

            // The intrinsic ratio comes from the SVG's own viewBox, read out of the markup rather
            // than from an element, so a data-URI SVG scales like an inline one.
            string? viewBox = ViewBoxAttribute(markup);
            (int vbW, int vbH) = viewBox != null ? ParseViewBoxSize(viewBox) : (0, 0);
            return BuildSizedImageRun(data, wAttr, hAttr, vbW, vbH);
        }

        /// <summary>
        /// The first <c>viewBox</c> attribute's value in SVG markup — the name in any ASCII case, white
        /// space around the <c>=</c>, a non-empty value between quotes — or <c>null</c>. Scanned by hand:
        /// the markup is the document's.
        /// </summary>
        internal static string? ViewBoxAttribute(string markup)
        {
            const string name = "viewBox";
            for (int at = markup.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = markup.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                int i = at + name.Length;
                while (i < markup.Length && char.IsWhiteSpace(markup[i])) i++;
                if (i >= markup.Length || markup[i] != '=') continue;
                i++;
                while (i < markup.Length && char.IsWhiteSpace(markup[i])) i++;
                if (i >= markup.Length || (markup[i] != '"' && markup[i] != '\'')) continue;
                int start = ++i;
                while (i < markup.Length && markup[i] != '"' && markup[i] != '\'') i++;
                if (i < markup.Length && i > start) return markup.Substring(start, i - start);
            }
            return null;
        }

        /// <summary>
        /// The deepest an inline <c>&lt;svg&gt;</c> may nest and still be serialised for the
        /// rasterizer — AngleSharp's serialiser recurses; HtmlScript's MaxDocumentDepth uses the same
        /// figure for the same serialiser.
        /// </summary>
        internal const int MaxSvgSerialisationDepth = 1_024;

        /// <summary>How deep elements nest under <paramref name="root"/>, measured with an explicit stack.</summary>
        private static int SubtreeDepth(IElement root)
        {
            int deepest = 0;
            var pending = new Stack<(IElement Element, int Depth)>();
            pending.Push((root, 1));
            while (pending.Count > 0)
            {
                var (element, depth) = pending.Pop();
                if (depth > deepest) deepest = depth;
                foreach (IElement child in element.Children) pending.Push((child, depth + 1));
            }
            return deepest;
        }

        // Serializes an element's outer markup for the rasterizer. Prefers AngleSharp's own
        // serializer (handles SVG-namespace foreign content); falls back to text content.
        private static string SvgMarkup(IElement el)
        {
            try { string s = el.OuterHtml; if (!string.IsNullOrEmpty(s)) return s; } catch { }
            return el.TextContent ?? "";
        }

        // Returns the (width, height) from an SVG viewBox ("minX minY w h"), or (0,0).
        private (int w, int h) ParseViewBoxSize(string? viewBox)
        {
            if (string.IsNullOrWhiteSpace(viewBox)) return (0, 0);
            var p = viewBox!.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 4
                && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
                && w > 0 && h > 0)
                return (SaturatingCast.ToInt(Math.Round(w)), SaturatingCast.ToInt(Math.Round(h)));
            return (0, 0);
        }

        // Builds an ImageRun, resolving display size from explicit width/height attributes, then
        // the supplied intrinsic dimensions for aspect ratio, then a FallbackImageSizePt square.
        private M.ImageRun BuildSizedImageRun(byte[] data, string? wAttr, string? hAttr, int intrinsicW, int intrinsicH)
        {
            bool got = intrinsicW > 0 && intrinsicH > 0;
            double w = wAttr != null ? ParseLength(wAttr, 0) : 0;
            double h = hAttr != null ? ParseLength(hAttr, 0) : 0;

            if (w <= 0 && h <= 0)
            {
                if (got) { w = PdfUnits.PixelsToPoints(intrinsicW); h = PdfUnits.PixelsToPoints(intrinsicH); }
                else { w = FallbackImageSizePt; h = FallbackImageSizePt; }
            }
            else if (w > 0 && h <= 0) h = got ? w * intrinsicH / intrinsicW : w;
            else if (h > 0 && w <= 0) w = got ? h * intrinsicW / intrinsicH : h;

            return new M.ImageRun { Data = data, WidthPt = w, HeightPt = h };
        }

        /// <summary>
        /// Decodes a <c>data:</c> URI's payload. Both encodings are handled: <c>;base64</c> and
        /// the default percent-encoded form, which is how an inline SVG is almost always written.
        /// </summary>
        private readonly Dictionary<string, byte[]?> _dataUriBytes = new(StringComparer.Ordinal);

        /// <summary>
        /// Decodes a <c>data:</c> URI's bytes, once per distinct URI in this conversion. A page with a
        /// large inline image repeated across many elements — an <c>&lt;img&gt;</c> or a
        /// <c>background-image</c> on N elements — otherwise decoded a fresh array each time, and the
        /// exporter (which dedups by array identity) then held and wrote N copies, to the point of an
        /// OutOfMemoryException. Caching by the URI hands every reference the same array.
        /// </summary>
        private byte[]? DecodeDataUri(string src)
        {
            if (_dataUriBytes.TryGetValue(src, out byte[]? known)) return known;
            byte[]? data = DecodeDataUriCore(src);
            _dataUriBytes[src] = data;
            return data;
        }

        private static byte[]? DecodeDataUriCore(string src)
        {
            int comma = src.IndexOf(',');
            if (comma < 0) return null;
            string meta = src.Substring(0, comma);
            string payload = src.Substring(comma + 1);
            try
            {
                byte[] data = meta.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0
                    ? System.Convert.FromBase64String(payload.Trim())
                    : PercentDecodeToBytes(payload);
                return data.Length > 0 ? data : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Percent-decodes a <c>data:</c> payload to <b>bytes</b>.
        ///
        /// <para>Deliberately not <see cref="Uri.UnescapeDataString(string)"/>: that decodes to a
        /// <i>string</i>, which round-trips a binary payload through UTF-8 and mangles every byte
        /// above 0x7F. It happens to work for an SVG (which is text) and silently corrupts a
        /// percent-encoded PNG.</para>
        /// </summary>
        private static byte[] PercentDecodeToBytes(string payload)
        {
            var bytes = new List<byte>(payload.Length);
            for (int i = 0; i < payload.Length; i++)
            {
                char c = payload[i];
                if (c == '%' && i + 2 < payload.Length
                    && Uri.IsHexDigit(payload[i + 1]) && Uri.IsHexDigit(payload[i + 2]))
                {
                    bytes.Add((byte)((Uri.FromHex(payload[i + 1]) << 4) | Uri.FromHex(payload[i + 2])));
                    i += 2;
                }
                else if (c == '+') bytes.Add((byte)' ');
                else if (c <= 0x7F) bytes.Add((byte)c);
                else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
            return bytes.ToArray();
        }

        /// <summary>The MIME type declared by a <c>data:</c> URI, lower-cased, or empty.</summary>
        private static string DataUriMediaType(string src)
        {
            int comma = src.IndexOf(',');
            if (comma < 0) return "";
            string meta = src.Substring(5, Math.Max(0, comma - 5));   // after "data:"
            int semi = meta.IndexOf(';');
            if (semi >= 0) meta = meta.Substring(0, semi);
            return meta.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Whether <paramref name="data"/> is an SVG document rather than a raster image: after an
        /// optional byte-order mark and whitespace it starts with <c>&lt;</c>, and an <c>&lt;svg</c>
        /// tag opens within its first kilobyte (an XML declaration, a doctype or a comment may come
        /// first). Decided from the bytes, not the file name — a producer's <c>logo.png</c> can be an
        /// SVG and its <c>icon.svg</c> a PNG.
        /// </summary>
        internal static bool LooksLikeSvg(byte[] data)
        {
            int i = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n')) i++;
            if (i >= data.Length || data[i] != '<') return false;
            int end = Math.Min(data.Length, i + 1024);
            string head = Encoding.ASCII.GetString(data, i, end - i);
            return head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// True for a scheme-relative or <c>http(s)</c> URL — fetched only through
        /// <see cref="HtmlToPdfOptions.RemoteResourceLoader"/>, by <see cref="LoadRemote"/>.
        /// </summary>
        private static bool IsRemoteUrl(string src)
            => src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("//", StringComparison.Ordinal);

        /// <summary>
        /// Records a subresource this engine declined to load, so a document that lost its assets is
        /// distinguishable from one that never had any.
        /// </summary>
        /// <remarks>
        /// <para>The managed engine's documented "no network I/O" limit was enforced by silently
        /// returning null at three sites — <c>&lt;img src&gt;</c>, <c>background-image: url()</c> and
        /// <c>&lt;link rel=stylesheet&gt;</c> — so a document depending on remote assets converted to
        /// a valid-looking PDF missing all of them with nothing on <c>OnDiagnostic</c>. The
        /// <c>@font-face</c> path in this same assembly already treated the identical condition as
        /// diagnostic-worthy, which is what makes the omission a defect rather than a policy.</para>
        /// <para>This is the <b>local</b> half. A remote URL reports through <see cref="LoadRemote"/>,
        /// which can say which of its several reasons applied.</para>
        /// </remarks>
        private void ReportResourceDeclined(string what, string url)
            => ReportDiagnostic(
                what == "stylesheet" || what == "script" ? BuilderDiagnosticKind.ContentApproximated
                                     : BuilderDiagnosticKind.ImageDecodeFailed,
                "The " + what + " '" + Excerpt(url) + "' was not loaded: this engine reads local " +
                "files only inside HtmlToPdfOptions.BasePath, and this one is outside it, missing, " +
                "or no base directory is set. It is absent from the output.");

        private readonly Dictionary<string, byte[]?> _localImageBytes = new(StringComparer.Ordinal);

        /// <summary>
        /// Reads a local image's bytes, once per resolved path in this conversion — N references to one
        /// file otherwise re-read and re-decoded it N times and the exporter wrote N copies. Caching by
        /// the full path hands every reference the same array, as <see cref="LoadRemote"/> does for a URL.
        /// </summary>
        private byte[]? LoadLocalImage(string src)
        {
            string? full = ResolveLocalPath(src);
            if (full == null) return null;
            if (_localImageBytes.TryGetValue(full, out byte[]? known)) return known;
            byte[]? data;
            try { data = File.ReadAllBytes(full); }
            catch { data = null; }
            _localImageBytes[full] = data;
            return data;
        }

        // Resolves a relative / rooted / file: src to an existing file path INSIDE BasePath, or
        // null (no BasePath, remote http(s), traversal outside base, or missing). Query/fragment
        // suffixes (style.css?v=2) are stripped. The single trust boundary for managed file I/O.
        private string? ResolveLocalPath(string src)
        {
            string? basePath = EffectiveBasePath;
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(src)) return null;
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("//", StringComparison.Ordinal)) return null; // remote → never fetched
            int q = src.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) src = src.Substring(0, q);
            if (src.Length == 0) return null;
            try
            {
                string path = src;
                if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(src, UriKind.Absolute, out var u) && u.IsFile) path = u.LocalPath;
                    else return null;
                }
                if (!Path.IsPathRooted(path)) path = Path.Combine(basePath!, path);
                string full = Path.GetFullPath(path);
                if (!IsInsideDirectory(basePath!, full)) return null; // outside base
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> lies under <paramref name="baseDirectory"/> — the
        /// <b>security boundary</b> that stops a <c>file:</c> or relative <c>src</c> reading outside
        /// the caller's base directory. The rule is Core's
        /// (<see cref="Tetitulz.Pdf.Core.IO.DirectoryContainment.IsInside"/>), the one the browser
        /// engine's request guard asks too, so the two engines admit the same files.
        /// </summary>
        private static bool IsInsideDirectory(string baseDirectory, string candidate)
            => Tetitulz.Pdf.Core.IO.DirectoryContainment.IsInside(baseDirectory, candidate);

        private static M.TextRun MakeRun(string text, Style st) => new M.TextRun
        {
            Text = text,
            FontName = st.FontFamily ?? (st.Mono ? StandardFonts.Courier
                : st.Serif ? StandardFonts.TimesRoman : StandardFonts.Helvetica),
            FontSize = st.Size,
            IsBold = st.Bold,
            IsItalic = st.Italic,
            IsUnderline = st.Underline,
            IsStrikethrough = st.Strike,
            Color = st.Color ?? BlackColor,
            IsInvisible = st.Invisible,
            Highlight = st.Highlight ?? "",
            VerticalAlign = st.Sup ? M.VerticalAlign.Superscript
                : st.Sub ? M.VerticalAlign.Subscript : M.VerticalAlign.Baseline,
            CharSpacing = st.LetterSpacing,
            Kerning = !st.NoKerning,
            ContextualAlternates = !st.NoContextual,
            Ligatures = !st.NoLigatures,
            DiscretionaryLigatures = st.DiscretionaryLigatures,
            HistoricalLigatures = st.HistoricalLigatures,
            IsAllCaps = st.AllCaps,
            IsSmallCaps = st.SmallCaps,
            LinkUri = st.Link,
            Opacity = RelativeOpacity(st),
            Box = st.Box,
        };

        /// <summary>
        /// The opacity a run must carry: what inline elements added <i>since</i> the enclosing
        /// paragraph, because the exporter multiplies the paragraph's value back in.
        /// </summary>
        /// <remarks>
        /// A fully transparent block makes the quotient meaningless (and 0/0 a <c>NaN</c>), so it
        /// returns 1 and lets the paragraph's own 0 do the hiding — the same answer, reached
        /// without dividing by zero.
        /// </remarks>
        private static double RelativeOpacity(Style st)
        {
            if (st.BlockOpacity <= 0) return 1.0;
            double rel = st.Opacity / st.BlockOpacity;
            return rel < 0 ? 0 : (rel > 1 ? 1 : rel);
        }

        // Legacy <font color|size|face> attributes.
        private static void ApplyFontTag(IElement el, Style st)
        {
            string? color = el.GetAttribute("color");
            if (color != null && TryNormalizeColor(color) is { } fontColor) st.Color = fontColor;
            string? face = el.GetAttribute("face");
            if (face != null)
            {
                if (IsMonoFamily(face)) { st.Mono = true; st.Serif = false; }
                else if (IsSerifFamily(face)) { st.Serif = true; st.Mono = false; }
            }
            string? size = el.GetAttribute("size");
            if (size != null)
            {
                // A leading '+' or '-' is RELATIVE to the default BASEFONT level 3 (HTML legacy
                // <font size>): "+1" is level 4, "-1" is level 2. Stripping the sign read both as the
                // absolute level 1, so every relative size came out inverted and tiny.
                string t = size.Trim();
                bool relative = t.Length > 0 && (t[0] == '+' || t[0] == '-');
                if (int.TryParse(relative ? t.Substring(1) : t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
                {
                    int level = relative ? 3 + (t[0] == '-' ? -s : s) : s;
                    if (relative) level = Math.Max(1, Math.Min(7, level));
                    st.Size = level switch { 1 => 8, 2 => 10, 3 => 12, 4 => 14, 5 => 18, 6 => 24, _ => 36 };
                }
            }
        }

        private void ApplyInlineStyle(string? style, Style st)
        {
            if (string.IsNullOrEmpty(style)) return;
            // Collected rather than applied in the loop, because opacity is the one property here
            // that COMPOUNDS with its ancestors while still being last-wins within an element.
            // Multiplying inside the switch would square a value that appears twice in one
            // declaration block — which the cascade flattener routinely produces, since it folds a
            // stylesheet rule and the element's own style= attribute into a single attribute.
            double? declaredOpacity = null;
            foreach (var decl in SplitDeclarations(style))
            {
                if (!TrySplitDeclaration(decl, out string prop, out string v, out _)) continue;
                if (v.Length == 0) continue;
                string k = DeclarationKey(prop);
                switch (k)
                {
                    // An unparseable colour drops the declaration (the inherited value stands)
                    // rather than falling back to black. A fully transparent one is a colour that
                    // paints nothing: the text is drawn invisibly, not in the
                    // inherited colour.
                    case "color":
                        if (Tetitulz.Pdf.Core.Text.CssColor.IsFullyTransparent(v)) st.Invisible = true;
                        else if (TryNormalizeColor(v) is { } fg) { st.Color = fg; st.Invisible = false; }
                        break;
                    case "background-color":
                        st.Highlight = IsTransparent(v) ? null : TryNormalizeColor(v);
                        break;
                    case "background":
                        st.Highlight = IsTransparent(v) ? null : TryExtractBackgroundColor(v);
                        break;
                    case "font-weight":
                        if (Eq(v, "bold") || Eq(v, "bolder") || (int.TryParse(v, out int fw) && fw >= 600)) st.Bold = true;
                        else if (Eq(v, "normal") || Eq(v, "lighter") || (int.TryParse(v, out int fw2) && fw2 < 600)) st.Bold = false;
                        break;
                    case "font-style":
                        if (Eq(v, "italic") || Eq(v, "oblique")) st.Italic = true;
                        else if (Eq(v, "normal")) st.Italic = false;
                        break;
                    case "text-decoration":
                    case "text-decoration-line":
                        // CSS keyword VALUES are ASCII case-insensitive, exactly like the property
                        // names DeclarationKey already normalises. An ordinal Contains matched
                        // neither `UNDERLINE` nor — worse — `text-decoration:NONE` inside a link,
                        // which then kept the UA underline against the author's instruction.
                        if (Has(v, "none")) { st.Underline = false; st.Strike = false; }
                        if (Has(v, "underline")) st.Underline = true;
                        if (Has(v, "line-through")) st.Strike = true;
                        break;
                    case "font-size": st.Size = ParseFontSize(v, st.Size); break;
                    case "font-family": ApplyFontFamily(v, st); break;
                    case "font": ApplyFontShorthand(v, st); break;
                    case "vertical-align":
                        if (Eq(v, "super")) { st.Sup = true; st.Sub = false; }
                        else if (Eq(v, "sub")) { st.Sub = true; st.Sup = false; }
                        else if (Eq(v, "baseline")) { st.Sup = false; st.Sub = false; }
                        break;
                    case "text-transform":
                        if (Eq(v, "uppercase")) st.AllCaps = true;
                        else if (Eq(v, "none")) st.AllCaps = false;
                        break;
                    case "font-variant":
                        if (Has(v, "small-caps")) st.SmallCaps = true;
                        else if (Eq(v, "normal")) st.SmallCaps = false;
                        break;
                    case "letter-spacing":
                        st.LetterSpacing = Eq(v, "normal") ? 0 : ParseLength(v, 0, st.Size);
                        break;
                    case "font-kerning":
                        if (Eq(v, "none")) st.NoKerning = true;
                        else if (Eq(v, "auto") || Eq(v, "normal")) st.NoKerning = false;
                        break;
                    case "font-variant-ligatures":
                        // A declaration sets every ligature kind at once: contextual alternates are
                        // off only where it says none or no-contextual, the common ligatures only
                        // where it says none or no-common-ligatures, on for anything else.
                        string[] kinds = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        st.NoContextual = kinds.Any(t => Eq(t, "none") || Eq(t, "no-contextual"));
                        st.NoLigatures = kinds.Any(t => Eq(t, "none") || Eq(t, "no-common-ligatures"));
                        // The optional kinds are off unless named (CSS Fonts 4 §6.4).
                        st.DiscretionaryLigatures = kinds.Any(t => Eq(t, "discretionary-ligatures"));
                        st.HistoricalLigatures = kinds.Any(t => Eq(t, "historical-ligatures"));
                        break;
                    case "white-space":
                        // Style.Pre inherits through DeriveStyle's MemberwiseClone and MapBlock
                        // forces it on for <pre>, so without this reset arm no descendant could
                        // ever turn preformatting back off — the asymmetry every neighbouring
                        // property here avoids by having one.
                        if (Eq(v, "normal") || Eq(v, "nowrap")) { st.Pre = false; st.PreCollapseSpaces = false; }
                        else if (v.StartsWith("pre", StringComparison.OrdinalIgnoreCase))
                        {
                            st.Pre = true;
                            // `pre-line` preserves newlines and COLLAPSES spaces; mapping it onto
                            // full `pre` kept every space CSS requires it to collapse.
                            st.PreCollapseSpaces = Eq(v, "pre-line");
                        }
                        break;
                    case "opacity":
                        if (TryParseOpacity(v, out double op)) declaredOpacity = op;
                        break;
                    case "orphans":
                        if (TryParseLineCount(v, out int orphans)) st.Orphans = orphans;
                        break;
                    case "widows":
                        if (TryParseLineCount(v, out int widows)) st.Widows = widows;
                        break;
                    case "hyphens":
                    case "-webkit-hyphens":
                        if (Eq(v, "auto")) st.HyphensAuto = true;
                        else if (Eq(v, "manual") || Eq(v, "none")) st.HyphensAuto = false;
                        break;
                }
            }

            if (declaredOpacity.HasValue) st.Opacity *= declaredOpacity.Value;
        }

        /// <summary>
        /// A CSS <c>orphans</c> / <c>widows</c> value: a positive integer. Anything else — zero, a
        /// negative, a fraction, a keyword — is invalid there and <b>declined</b>, so the inherited
        /// value stands.
        /// </summary>
        private static bool TryParseLineCount(string v, out int count) =>
            int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 1;

        /// <summary>
        /// A CSS <c>opacity</c> value: a number in 0–1, or a percentage. Out-of-range values are
        /// clamped, as CSS requires; anything unparseable is <b>declined</b> so the inherited value
        /// stands, matching what the colour properties above already do.
        /// </summary>
        private static bool TryParseOpacity(string v, out double opacity)
        {
            opacity = 1.0;
            string s = v.Trim();
            if (s.Length == 0) return false;

            bool pct = s.EndsWith("%", StringComparison.Ordinal);
            if (pct) s = s.Substring(0, s.Length - 1).Trim();

            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double n))
                return false;
            if (double.IsNaN(n) || double.IsInfinity(n)) return false;

            if (pct) n /= 100.0;
            opacity = n < 0 ? 0 : (n > 1 ? 1 : n);
            return true;
        }

        private static void ApplyFontFamily(string v, Style st)
        {
            // The bucket is computed from the WHOLE list, so `font-family: "Foo", monospace`
            // still falls back to Courier; the recorded name is the first entry, which is what
            // an @font-face rule or a FontProvider is keyed on.
            if (IsMonoFamily(v)) { st.Mono = true; st.Serif = false; }
            else if (IsSerifFamily(v)) { st.Serif = true; st.Mono = false; }
            else { st.Mono = false; st.Serif = false; }

            foreach (string name in v.Split(','))
            {
                string first = UnquoteFamily(name);
                if (first.Length == 0) continue;
                // A generic first entry names no font: an inherited name must not survive it, or
                // `font-family: monospace` under a Georgia body drew Georgia.
                st.FontFamily = IsGenericFamily(first) ? null : first;
                break;
            }
        }

        private static bool IsGenericFamily(string f) =>
            Eq(f, "serif") || Eq(f, "sans-serif") || Eq(f, "monospace")
            || Eq(f, "cursive") || Eq(f, "fantasy") || Eq(f, "system-ui");

        // Parses the `font` shorthand: [style] [variant] [weight] size[/line-height] family.
        // The first token containing a digit is the size; everything after it is the family.
        // Tokens before the size set italic / small-caps / bold. (line-height is a block
        // property and is ignored here; the shorthand does not reset unspecified sub-properties.)
        /// <summary>
        /// A CSS <c>font-size</c> value in points, resolved against <paramref name="parentSize"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Percentages and keywords are the whole reason this is not just
        /// <c>ParseLength</c>.</b> <c>ParseLength</c> maps <c>%</c> to its fallback, and the
        /// font-size call site passed the <i>current</i> size as that fallback — so every percentage
        /// font-size resolved to "no change", and the size keywords failed the numeric regex and
        /// took the same path. <c>font-size:200%</c> rendered at the parent's size, and the very
        /// common <c>html{font-size:62.5%}</c> idiom left every <c>rem</c>-derived size 1.6× wrong.
        /// The percentage is fully computable here, so this was a dropped feature rather than an
        /// unresolvable one.</para>
        /// <para><c>ParseLength</c>'s generic <c>%</c> → fallback is deliberately left alone: its
        /// other callers genuinely have no reference value.</para>
        /// </remarks>
        private double ParseFontSize(string v, double parentSize)
        {
            v = v.Trim();
            if (v.Length == 0) return parentSize;

            if (v.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out double pct))
                // "NaN%" parses to NaN under NumberStyles.Float, and a negative percentage gives a
                // negative size; both would reach the model and throw. Decline, keep the inherited size.
                return DeclinedSizeOr(pct / 100 * parentSize, parentSize);

            // CSS 2.1 §15.7 absolute keywords, on the 1.2 scale with `medium` = the initial size.
            if (Eq(v, "xx-small")) return DefaultFontSizePt / 1.2 / 1.2 / 1.2;
            if (Eq(v, "x-small"))  return DefaultFontSizePt / 1.2 / 1.2;
            if (Eq(v, "small"))    return DefaultFontSizePt / 1.2;
            if (Eq(v, "medium"))   return DefaultFontSizePt;
            if (Eq(v, "large"))    return DefaultFontSizePt * 1.2;
            if (Eq(v, "x-large"))  return DefaultFontSizePt * 1.2 * 1.2;
            if (Eq(v, "xx-large")) return DefaultFontSizePt * 1.2 * 1.2 * 1.2;

            // …and the two relative ones, which are a step on that scale from the PARENT.
            if (Eq(v, "smaller")) return parentSize / 1.2;
            if (Eq(v, "larger"))  return parentSize * 1.2;

            return DeclinedSizeOr(ParseLength(v, parentSize, parentSize), parentSize);
        }

        /// <summary>A font size the browser would drop — negative or non-finite (<c>font-size:-1px</c>,
        /// <c>font-size:NaN%</c>) — keeps the inherited size instead of reaching the model, which refuses it.</summary>
        private static double DeclinedSizeOr(double size, double inherited) =>
            size >= 0 && !double.IsNaN(size) && !double.IsInfinity(size) ? size : inherited;

        private void ApplyFontShorthand(string v, Style st)
        {
            var parts = v.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int sizeIdx = -1;
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i];
                if (t.Any(char.IsDigit)) { sizeIdx = i; break; } // size token (e.g. 14px, 1.2em, 12px/1.5)
                if (Eq(t, "italic") || Eq(t, "oblique")) st.Italic = true;
                else if (Eq(t, "bold") || Eq(t, "bolder") || (int.TryParse(t, out int w) && w >= 600)) st.Bold = true;
                else if (t.IndexOf("small-caps", StringComparison.OrdinalIgnoreCase) >= 0) st.SmallCaps = true;
            }
            if (sizeIdx < 0) return; // no size → not a valid font shorthand
            string sizeTok = parts[sizeIdx];
            int slash = sizeTok.IndexOf('/');
            st.Size = ParseFontSize(slash >= 0 ? sizeTok.Substring(0, slash) : sizeTok, st.Size);
            if (sizeIdx + 1 < parts.Length)
                ApplyFontFamily(string.Join(" ", parts, sizeIdx + 1, parts.Length - sizeIdx - 1), st);
        }

        private static bool IsMonoFamily(string family)
        {
            string f = family.ToLowerInvariant();
            return f.Contains("courier") || f.Contains("mono") || f.Contains("consol");
        }

        private static bool IsSerifFamily(string family)
        {
            string f = family.ToLowerInvariant();
            if (f.Contains("sans")) return false; // "sans-serif" / "sans serif" → not serif
            return f.Contains("serif") || f.Contains("times") || f.Contains("georgia")
                || f.Contains("garamond") || f.Contains("cambria") || f.Contains("palatino")
                || f.Contains("minion");
        }

        private static bool Eq(string? a, string b) => a != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Case-insensitive substring test for a CSS keyword inside a multi-keyword <b>value</b>
        /// (<c>text-decoration: underline line-through</c>).
        ///
        /// <para>The companion to <see cref="Eq"/>, and it exists for the same reason: CSS keyword
        /// values are ASCII case-insensitive, while <c>string.Contains(string)</c> is ordinal. The
        /// property <i>name</i> was already normalised by <see cref="DeclarationKey"/>, so
        /// <c>TEXT-DECORATION: UNDERLINE</c> reached the switch and then matched nothing — a
        /// half-normalised declaration is the shape that hides this.</para>
        /// </summary>
        private static bool Has(string value, string keyword) =>
            value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Collapses runs of whitespace to a single space, preserving U+00A0 (<c>&amp;nbsp;</c>),
        /// which must neither collapse nor become a word-break opportunity.
        ///
        /// <para>One implementation, shared by the ordinary text path and by
        /// <c>white-space: pre-line</c> — whose entire definition is "this collapse, but keep the
        /// newlines". A second copy is how the two would come to disagree about the NBSP
        /// exemption.</para>
        /// </summary>
        internal static string CollapseSpaces(string raw)
        {
            var sb = new System.Text.StringBuilder(raw.Length);
            bool inRun = false;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c) && c != '\u00A0')
                {
                    if (!inRun) sb.Append(' ');
                    inRun = true;
                }
                else
                {
                    sb.Append(c);
                    inRun = false;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// True when the next run would open a line: nothing is in the paragraph yet, or the last
        /// run is a forced break.
        /// </summary>
        private static bool AtLineStart(M.Paragraph para) =>
            para.Runs.Count == 0 || para.Runs[para.Runs.Count - 1] is M.LineBreak;
    }
}
