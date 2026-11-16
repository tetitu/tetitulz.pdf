// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder
{
    /// <summary>
    /// Fluent API for building documents — inspired by docx.js.
    /// Chain methods to construct a rich document, then export to PDF/DOCX/HTML/ODT.
    /// </summary>
    /// <remarks>
    /// <para><b>This chain is one of three producers of <see cref="Document"/>, not a layer the
    /// others pass through.</b> <c>HtmlToBuilderMapper</c> and <c>DocxToBuilderMapper</c> build the
    /// same model with object initializers and never touch these classes. So a method added here
    /// widens code-first authoring and changes nothing for the converters; a capability reaches
    /// them only through <see cref="Model"/> plus <see cref="Export.PdfExporter"/>.</para>
    ///
    /// <para><b>New members are overloads, never new optional parameters.</b> This assembly ships
    /// as a NuGet package, and adding an optional parameter changes the compiled signature, so a
    /// consumer built against the previous version takes a
    /// <see cref="MissingMethodException"/> — the same reasoning recorded on
    /// <see cref="Document.SaveAsPdf(System.IO.Stream, Export.PdfExportOptions)"/>.</para>
    /// </remarks>
    public sealed class DocumentBuilder
    {
        private readonly Document _doc = new();

        public DocumentBuilder WithTitle(string title) { _doc.Title = title; return this; }
        public DocumentBuilder WithAuthor(string author) { _doc.Author = author; return this; }
        public DocumentBuilder WithSubject(string subject) { _doc.Subject = subject; return this; }

        /// <summary>
        /// Emit tagged (accessible) PDF — <see cref="Document.Tagged"/>.
        /// </summary>
        /// <remarks>
        /// Without this the fluent path could not produce a tagged document at all, however many
        /// roles its paragraphs carried: the exporter keys the whole structure tree off this flag.
        /// Pair it with <see cref="ParagraphBuilder.Role"/> and the <c>alt</c> overload of
        /// <see cref="ParagraphBuilder.AddImage(byte[], double, double, string)"/> — a tree of
        /// undifferentiated <c>/P</c> elements passes structural checks while telling a screen
        /// reader nothing.
        /// </remarks>
        public DocumentBuilder Tagged() { _doc.Tagged = true; return this; }

        /// <summary>
        /// The document's natural language for the catalog <c>/Lang</c>, e.g. <c>"he-IL"</c> —
        /// <see cref="Document.Language"/>.
        /// </summary>
        public DocumentBuilder Language(string bcp47) { _doc.Language = bcp47; return this; }

        public DocumentBuilder AddSection(Action<SectionBuilder> configure)
        {
            var sb = new SectionBuilder();
            configure(sb);
            _doc.Sections.Add(sb.Build());
            return this;
        }

        public Document Build() => _doc;
    }

    public sealed class SectionBuilder
    {
        private readonly Section _section = new();

        public SectionBuilder PageSize(double widthPt, double heightPt) { _section.PageWidthPt = widthPt; _section.PageHeightPt = heightPt; return this; }
        public SectionBuilder Margins(double top, double right, double bottom, double left) { _section.MarginTop = top; _section.MarginRight = right; _section.MarginBottom = bottom; _section.MarginLeft = left; return this; }

        /// <summary>Sets the section's base writing direction (cascades to paragraphs
        /// that don't override it).</summary>
        public SectionBuilder Direction(TextDirection direction) { _section.Direction = direction; return this; }
        /// <summary>Shorthand for <c>Direction(TextDirection.RightToLeft)</c> (Hebrew/Arabic).</summary>
        public SectionBuilder Rtl() { _section.Direction = TextDirection.RightToLeft; return this; }

        public SectionBuilder Header(Action<HeaderFooterBuilder> configure)
        {
            var hb = new HeaderFooterBuilder();
            configure(hb);
            _section.Header = new Model.Header();
            _section.Header.Paragraphs.AddRange(hb.Paragraphs);
            return this;
        }

        public SectionBuilder Footer(Action<HeaderFooterBuilder> configure)
        {
            var fb = new HeaderFooterBuilder();
            configure(fb);
            _section.Footer = new Model.Footer();
            _section.Footer.Paragraphs.AddRange(fb.Paragraphs);
            return this;
        }

        /// <summary>
        /// First-page header variant. Only selected when <see cref="TitlePage"/> is also set —
        /// the exporter's header selection reads that flag, not the presence of the variant.
        /// </summary>
        public SectionBuilder FirstPageHeader(Action<HeaderFooterBuilder> configure)
        {
            _section.FirstPageHeader = BuildHeader(configure);
            return this;
        }

        /// <summary>First-page footer variant; see <see cref="FirstPageHeader"/>.</summary>
        public SectionBuilder FirstPageFooter(Action<HeaderFooterBuilder> configure)
        {
            _section.FirstPageFooter = BuildFooter(configure);
            return this;
        }

        /// <summary>
        /// Even-page header variant. Only selected when <see cref="EvenAndOddHeaders"/> is set.
        /// </summary>
        public SectionBuilder EvenPageHeader(Action<HeaderFooterBuilder> configure)
        {
            _section.EvenPageHeader = BuildHeader(configure);
            return this;
        }

        /// <summary>Even-page footer variant; see <see cref="EvenPageHeader"/>.</summary>
        public SectionBuilder EvenPageFooter(Action<HeaderFooterBuilder> configure)
        {
            _section.EvenPageFooter = BuildFooter(configure);
            return this;
        }

        /// <summary>Use the first-page header/footer variant on this section's first page.</summary>
        public SectionBuilder TitlePage() { _section.TitlePage = true; return this; }

        /// <summary>Use distinct even/odd header and footer variants.</summary>
        public SectionBuilder EvenAndOddHeaders() { _section.EvenAndOddHeaders = true; return this; }

        /// <summary>
        /// Page border drawn around every page of the section.
        /// </summary>
        /// <remarks>
        /// Takes the model type directly rather than nesting yet another builder:
        /// <see cref="Model.PageBorders"/> is four optional <see cref="Border"/>s plus four
        /// insets, all public and all independent, so a builder would add a class and express
        /// nothing an object initializer does not.
        /// </remarks>
        public SectionBuilder PageBorders(PageBorders borders) { _section.PageBorders = borders; return this; }

        /// <summary>Page-number format for PAGE fields: <c>decimal</c>, <c>upperRoman</c>,
        /// <c>lowerRoman</c>, <c>upperLetter</c>, <c>lowerLetter</c>.</summary>
        public SectionBuilder PageNumberFormat(string format) { _section.PageNumberFormat = format; return this; }

        /// <summary>Explicit starting page number for this section.</summary>
        public SectionBuilder PageNumberStart(int start) { _section.PageNumberStart = start; return this; }

        public SectionBuilder AddParagraph(Action<ParagraphBuilder> configure)
        {
            var pb = new ParagraphBuilder();
            configure(pb);
            _section.Children.Add(pb.Build());
            return this;
        }

        public SectionBuilder AddTable(Action<TableBuilder> configure)
        {
            var tb = new TableBuilder();
            configure(tb);
            _section.Children.Add(tb.Build());
            return this;
        }

        /// <summary>
        /// Adds a table of contents here, listing every heading in the document.
        /// </summary>
        /// <param name="title">The heading above the entries, or null for none.</param>
        /// <param name="maxLevel">The deepest heading level to list; 3 by default.</param>
        /// <remarks>
        /// <para><b>The entries are collected when the document is exported, not now.</b> A
        /// contents block added before the sections it lists still lists them — which is where
        /// one usually goes, and a snapshot taken at this point would be empty.</para>
        ///
        /// <para><b>A heading is a paragraph whose <c>Role</c> is <c>H1</c>–<c>H6</c>.</b> A
        /// document whose headings are only large and bold has no headings to list, and the block
        /// comes out empty rather than guessing from font sizes.</para>
        /// </remarks>
        public SectionBuilder AddTableOfContents(string? title = "Contents", int maxLevel = 3)
        {
            _section.Children.Add(new TocBlock { Title = title, MaxLevel = maxLevel });
            return this;
        }

        public SectionBuilder AddListItem(string text, bool numbered = false, int level = 0)
        {
            _section.Children.Add(new ListItem { Text = text, IsNumbered = numbered, Level = level });
            return this;
        }

        /// <summary>
        /// A list item with an explicit marker, rich runs, direction or run formatting — the
        /// <see cref="ListItem"/> properties the <c>(text, numbered, level)</c> overload cannot
        /// reach.
        /// </summary>
        public SectionBuilder AddListItem(Action<ListItemBuilder> configure)
        {
            var lb = new ListItemBuilder();
            configure(lb);
            _section.Children.Add(lb.Build());
            return this;
        }

        /// <summary>
        /// A list, which numbers its own items and can nest — unlike a run of loose
        /// <see cref="AddListItem(string, bool, int)"/> calls, which cannot.
        /// </summary>
        public SectionBuilder AddList(Action<ListBuilder> configure)
        {
            var lb = new ListBuilder();
            configure(lb);
            _section.Children.Add(lb.Build());
            return this;
        }

        private static Model.Header BuildHeader(Action<HeaderFooterBuilder> configure)
        {
            var hb = new HeaderFooterBuilder();
            configure(hb);
            var h = new Model.Header();
            h.Paragraphs.AddRange(hb.Paragraphs);
            return h;
        }

        private static Model.Footer BuildFooter(Action<HeaderFooterBuilder> configure)
        {
            var fb = new HeaderFooterBuilder();
            configure(fb);
            var f = new Model.Footer();
            f.Paragraphs.AddRange(fb.Paragraphs);
            return f;
        }

        internal Section Build() => _section;
    }

    public sealed class HeaderFooterBuilder
    {
        internal System.Collections.Generic.List<Paragraph> Paragraphs { get; } = new();

        public HeaderFooterBuilder AddParagraph(Action<ParagraphBuilder> configure)
        {
            var pb = new ParagraphBuilder();
            configure(pb);
            Paragraphs.Add(pb.Build());
            return this;
        }
    }

    public sealed class ParagraphBuilder
    {
        private readonly Paragraph _p = new();
        private TextRun? _lastRun;

        public ParagraphBuilder Alignment(TextAlignment align) { _p.Alignment = align; return this; }
        /// <summary>Sets the paragraph writing direction (overrides the section default).</summary>
        public ParagraphBuilder Direction(TextDirection direction) { _p.Direction = direction; return this; }
        /// <summary>Shorthand for <c>Direction(TextDirection.RightToLeft)</c> (Hebrew/Arabic).</summary>
        public ParagraphBuilder Rtl() { _p.Direction = TextDirection.RightToLeft; return this; }
        public ParagraphBuilder SpacingBefore(double pts) { _p.SpacingBefore = pts; return this; }
        public ParagraphBuilder SpacingAfter(double pts) { _p.SpacingAfter = pts; return this; }
        public ParagraphBuilder Indent(double left, double firstLine = 0) { _p.IndentLeft = left; _p.FirstLineIndent = firstLine; return this; }

        /// <summary>Right indent in points — the edge <see cref="Indent"/> does not reach.</summary>
        public ParagraphBuilder IndentRight(double pts) { _p.IndentRight = pts; return this; }

        /// <summary>
        /// Structure role for tagged output — <c>"H1"</c>…<c>"H6"</c>, <c>"Caption"</c>,
        /// <c>"BlockQuote"</c>, <c>"Code"</c>; the constants are in
        /// <c>Core.Constants.PdfStructureTypes</c>. Null (the default) means <c>/P</c>.
        /// Only observable when the document is <see cref="DocumentBuilder.Tagged"/>.
        /// </summary>
        public ParagraphBuilder Role(string structureType) { _p.Role = structureType; return this; }

        /// <summary>
        /// Mark this paragraph as page furniture rather than content, so tagged output emits an
        /// artifact instead of a structure element.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from <see cref="Role"/>: <c>/S /Artifact</c> is not a structure
        /// type, and it is exactly what a PDF/UA checker's UA13 exists to catch.
        /// </remarks>
        public ParagraphBuilder Artifact() { _p.IsArtifact = true; return this; }

        /// <summary>Background shading fill behind the whole paragraph, as a hex colour.</summary>
        public ParagraphBuilder Shading(string hex) { _p.Shading = hex; return this; }

        /// <summary>Background image painted behind the text, on top of <see cref="Shading"/>.</summary>
        public ParagraphBuilder BackgroundImage(BackgroundImage image) { _p.BackgroundImage = image; return this; }

        /// <summary>Exact line height in points; 0 (the default) derives it from the font size.</summary>
        public ParagraphBuilder LineSpacing(double pts) { _p.LineSpacingPt = pts; return this; }

        /// <summary>Start this paragraph on a new page.</summary>
        public ParagraphBuilder PageBreakBefore() { _p.PageBreakBefore = true; return this; }

        /// <summary>
        /// Constant alpha for everything this paragraph paints, including its runs.
        /// See <see cref="Paragraph.Opacity"/> — it is PDF <c>/ca</c>, not CSS group opacity.
        /// </summary>
        public ParagraphBuilder Opacity(double alpha) { _p.Opacity = alpha; return this; }

        /// <summary>The same border on all four sides.</summary>
        public ParagraphBuilder Borders(Border border)
        {
            _p.BorderTop = border; _p.BorderBottom = border;
            _p.BorderLeft = border; _p.BorderRight = border;
            return this;
        }

        public ParagraphBuilder BorderTop(Border border) { _p.BorderTop = border; return this; }
        public ParagraphBuilder BorderBottom(Border border) { _p.BorderBottom = border; return this; }
        public ParagraphBuilder BorderLeft(Border border) { _p.BorderLeft = border; return this; }
        public ParagraphBuilder BorderRight(Border border) { _p.BorderRight = border; return this; }

        /// <summary>A left-aligned tab stop with no leader.</summary>
        public ParagraphBuilder AddTabStop(double posPt)
        {
            _p.Tabs.Add(new TabStop { PosPt = posPt });
            return this;
        }

        /// <summary>
        /// A tab stop with an explicit alignment and leader.
        /// </summary>
        /// <remarks>
        /// <b>The leader is currently inert in every exporter.</b> The PDF exporter's tab
        /// resolution uses the position only, and the DOCX/HTML/ODT exporters ignore tab stops
        /// entirely — "real tab-leader dots" is an open item in GAPS.md §5. The parameter is here
        /// so the model value is reachable and round-trips, not because it draws.
        /// </remarks>
        public ParagraphBuilder AddTabStop(double posPt, TabAlignment align, TabLeader leader)
        {
            _p.Tabs.Add(new TabStop { PosPt = posPt, Align = align, Leader = leader });
            return this;
        }

        public ParagraphBuilder AddText(string text)
        {
            _lastRun = new TextRun { Text = text };
            _p.Runs.Add(_lastRun);
            return this;
        }

        /// <summary>
        /// Adds a footnote here: a reference mark at this point in the text, and the note itself
        /// wherever the format puts notes.
        /// </summary>
        /// <param name="text">The note's text.</param>
        /// <param name="mark">
        /// An explicit reference mark, or null to let the exporter number it. Set it for a symbol
        /// sequence; an explicit mark does not advance the automatic numbering.
        /// </param>
        /// <remarks>
        /// <para><b>It does not become the current run.</b> A footnote is not a text run, so
        /// <c>AddFootnote("...").Bold()</c> would have nothing sensible to bold — the chained
        /// formatting keeps applying to whatever <see cref="AddText"/> came last, which is the run
        /// the mark sits beside and the one a caller means.</para>
        ///
        /// <para><b>Where the note is drawn is the exporter's decision, not the model's.</b> PDF
        /// reserves room at the foot of the page the mark landed on; HTML has no page, so the notes
        /// go in a section at the end with the mark linking to them; ODT and DOCX each have a
        /// native note the application lays out itself.</para>
        /// </remarks>
        public ParagraphBuilder AddFootnote(string text, string? mark = null)
        {
            var note = new Footnote { Mark = mark };
            note.Runs.Add(new TextRun { Text = text ?? "" });
            _p.Runs.Add(note);
            return this;
        }

        /// <summary>
        /// Adds a footnote whose content is built by <paramref name="build"/>, for a note that
        /// needs more than one run.
        /// </summary>
        /// <remarks>
        /// The callback receives the note's own run list so a caller can add formatted runs, a
        /// link, or a line break — the same inline vocabulary a paragraph has, minus a nested
        /// footnote, which no exporter draws.
        /// </remarks>
        public ParagraphBuilder AddFootnote(Action<List<IInlineElement>> build, string? mark = null)
        {
            var note = new Footnote { Mark = mark };
            build?.Invoke(note.Runs);
            _p.Runs.Add(note);
            return this;
        }

        /// <summary>
        /// Adds an endnote: a reference mark beside the current run, and the note itself after the
        /// document's last content (<see cref="NotePlacement.DocumentEnd"/>).
        /// </summary>
        /// <param name="text">The note's text.</param>
        /// <param name="mark">
        /// An explicit reference mark, or null to let the exporter number it — in the endnotes' own
        /// sequence, apart from the footnotes'.
        /// </param>
        /// <remarks>
        /// As <see cref="AddFootnote(string, string?)"/>: it does not become the current run, and
        /// where the note is drawn is each exporter's decision — PDF flows the endnotes under a rule
        /// after the last section, DOCX and ODT write their native endnote, HTML a second section
        /// after the footnotes.
        /// </remarks>
        public ParagraphBuilder AddEndnote(string text, string? mark = null)
        {
            var note = new Footnote { Mark = mark, Placement = NotePlacement.DocumentEnd };
            note.Runs.Add(new TextRun { Text = text ?? "" });
            _p.Runs.Add(note);
            return this;
        }

        /// <summary>
        /// Adds an endnote whose content is built by <paramref name="build"/>, for a note that needs
        /// more than one run.
        /// </summary>
        public ParagraphBuilder AddEndnote(Action<List<IInlineElement>> build, string? mark = null)
        {
            var note = new Footnote { Mark = mark, Placement = NotePlacement.DocumentEnd };
            build?.Invoke(note.Runs);
            _p.Runs.Add(note);
            return this;
        }

        /// <summary>
        /// The run the chained formatting applies to, or a diagnostic if there is none.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No <see cref="AddText"/> run is current.
        /// </exception>
        /// <remarks>
        /// As <c>if (_lastRun != null) …</c>, <c>p.Bold().AddText("x")</c> — the natural reading
        /// order, and wrong — would compile, run, and produce unbolded text with no error. So would
        /// <c>AddText("a").AddImage(…).Bold()</c>, because
        /// <see cref="AddImage(byte[], double, double)"/> and <see cref="AddPageBreak"/> end the
        /// current run.
        /// A misused builder is a programming error, and the .NET convention for one is to throw:
        /// silently producing output that differs from what the call chain says is the failure
        /// mode this codebase spends its time hunting.
        /// </remarks>
        private TextRun CurrentRun(string method)
            => _lastRun ?? throw new InvalidOperationException(
                $"{method}() styles the preceding AddText(...) run, and none is current. " +
                "Call AddText(...) before it — note that AddImage(...) and AddPageBreak() end " +
                "the current run, so formatting cannot follow them either.");

        // Chained formatting — applies to the last AddText()
        public ParagraphBuilder Bold() { CurrentRun(nameof(Bold)).IsBold = true; return this; }
        public ParagraphBuilder Italic() { CurrentRun(nameof(Italic)).IsItalic = true; return this; }
        public ParagraphBuilder Underline() { CurrentRun(nameof(Underline)).IsUnderline = true; return this; }
        public ParagraphBuilder Strikethrough() { CurrentRun(nameof(Strikethrough)).IsStrikethrough = true; return this; }
        public ParagraphBuilder FontSize(double size) { CurrentRun(nameof(FontSize)).FontSize = size; return this; }
        public ParagraphBuilder FontName(string name) { CurrentRun(nameof(FontName)).FontName = name; return this; }
        public ParagraphBuilder Color(string hex) { CurrentRun(nameof(Color)).Color = hex; return this; }

        /// <summary>Struck with two rules rather than one (DOCX <c>w:dstrike</c>).</summary>
        public ParagraphBuilder DoubleStrike() { CurrentRun(nameof(DoubleStrike)).IsDoubleStrike = true; return this; }

        /// <summary>Highlight fill drawn behind this run's glyphs, as a hex colour.</summary>
        public ParagraphBuilder Highlight(string hex) { CurrentRun(nameof(Highlight)).Highlight = hex; return this; }

        /// <summary>Raise and shrink this run (superscript).</summary>
        public ParagraphBuilder Superscript() { CurrentRun(nameof(Superscript)).VerticalAlign = VerticalAlign.Superscript; return this; }

        /// <summary>Drop and shrink this run (subscript).</summary>
        public ParagraphBuilder Subscript() { CurrentRun(nameof(Subscript)).VerticalAlign = VerticalAlign.Subscript; return this; }

        /// <summary>Extra baseline shift in points, positive upward; adds to super/subscript.</summary>
        public ParagraphBuilder BaselineShift(double pts) { CurrentRun(nameof(BaselineShift)).BaselineShift = pts; return this; }

        /// <summary>Inter-character spacing in points (PDF <c>Tc</c>).</summary>
        public ParagraphBuilder CharSpacing(double pts) { CurrentRun(nameof(CharSpacing)).CharSpacing = pts; return this; }

        /// <summary>Horizontal glyph scale as a percentage (PDF <c>Tz</c>); 100 is normal.</summary>
        public ParagraphBuilder HorizontalScale(int percent) { CurrentRun(nameof(HorizontalScale)).HorizontalScale = percent; return this; }

        public ParagraphBuilder SmallCaps() { CurrentRun(nameof(SmallCaps)).IsSmallCaps = true; return this; }
        public ParagraphBuilder AllCaps() { CurrentRun(nameof(AllCaps)).IsAllCaps = true; return this; }

        /// <summary>Make this run a clickable external hyperlink.</summary>
        public ParagraphBuilder Link(string uri) { CurrentRun(nameof(Link)).LinkUri = uri; return this; }

        /// <summary>
        /// Constant alpha for this run alone, multiplied by the paragraph's
        /// <see cref="Opacity"/>. See <see cref="TextRun.Opacity"/>.
        /// </summary>
        public ParagraphBuilder TextOpacity(double alpha) { CurrentRun(nameof(TextOpacity)).Opacity = alpha; return this; }

        public ParagraphBuilder AddImage(byte[] data, double widthPt, double heightPt)
        {
            _p.Runs.Add(new ImageRun { Data = data, WidthPt = widthPt, HeightPt = heightPt });
            _lastRun = null;
            return this;
        }

        /// <summary>
        /// An image with alternative text — required for tagged output, where an undescribed
        /// figure is precisely what a PDF/UA checker reports.
        /// </summary>
        /// <remarks>
        /// An overload rather than an optional parameter on the method above, so a consumer
        /// compiled against the previous package keeps working. For a purely decorative image use
        /// <see cref="AddDecorativeImage"/>: empty alt text and <i>absent</i> alt text are
        /// different answers, and the model keeps them apart.
        /// </remarks>
        public ParagraphBuilder AddImage(byte[] data, double widthPt, double heightPt, string alt)
        {
            _p.Runs.Add(new ImageRun { Data = data, WidthPt = widthPt, HeightPt = heightPt, Alt = alt });
            _lastRun = null;
            return this;
        }

        /// <summary>
        /// An image that carries no information — HTML's <c>alt=""</c>. Tagged output marks it as
        /// decoration rather than as a figure with an empty description.
        /// </summary>
        public ParagraphBuilder AddDecorativeImage(byte[] data, double widthPt, double heightPt)
        {
            _p.Runs.Add(new ImageRun { Data = data, WidthPt = widthPt, HeightPt = heightPt, IsDecorative = true });
            _lastRun = null;
            return this;
        }

        /// <summary>A line break inside the paragraph (not a new paragraph).</summary>
        public ParagraphBuilder AddLineBreak()
        {
            _p.Runs.Add(new Model.LineBreak());
            _lastRun = null;
            return this;
        }

        /// <summary>Advance to the next tab stop, or the next default interval.</summary>
        public ParagraphBuilder AddTab()
        {
            _p.Runs.Add(new Model.TabRun());
            _lastRun = null;
            return this;
        }

        public ParagraphBuilder AddPageBreak()
        {
            _p.Runs.Add(new Model.PageBreak());
            _lastRun = null;
            return this;
        }

        internal Paragraph Build() => _p;
    }

    /// <summary>
    /// A list item with the properties the <c>AddListItem(text, numbered, level)</c> shorthand
    /// cannot reach: an explicit marker, rich runs, run formatting and direction.
    /// </summary>

    /// <summary>Builds a <see cref="ListBlock"/>: its items, its ordering and where it starts.</summary>
    /// <remarks>
    /// <b>The container is what makes an ordinal possible.</b> A loose <see cref="ListItem"/> has no
    /// siblings to count, so <c>AddListItem</c>'s number came from the item's own <i>level</i> and
    /// three siblings all rendered "1."; the alternative was for the caller to resolve every marker
    /// itself. Both spellings still work — a loose item is still rendered by every exporter — but a
    /// list built here numbers itself, nests, and comes out as <c>&lt;ol&gt;</c>,
    /// <c>&lt;text:list&gt;</c> and tagged <c>/L</c>/<c>/LI</c> rather than as paragraphs.
    /// </remarks>
    public sealed class ListBuilder
    {
        private readonly ListBlock _list = new();

        /// <summary>Numbers the items instead of bulleting them.</summary>
        public ListBuilder Ordered() { _list.IsOrdered = true; return this; }

        /// <summary>
        /// The ordinal of the first item — HTML's <c>&lt;ol start&gt;</c>, for a list continued
        /// after an interruption. Implies <see cref="Ordered"/>.
        /// </summary>
        public ListBuilder StartAt(int start)
        {
            _list.IsOrdered = true;
            _list.Start = start;
            return this;
        }

        public ListBuilder Direction(TextDirection direction) { _list.Direction = direction; return this; }
        public ListBuilder Rtl() { _list.Direction = TextDirection.RightToLeft; return this; }

        /// <summary>Adds an item carrying plain text.</summary>
        public ListBuilder AddItem(string text)
        {
            _list.Items.Add(new ListItem { Text = text, IsNumbered = _list.IsOrdered });
            return this;
        }

        /// <summary>
        /// Adds an item with runs, an explicit marker, a direction or a sublist — the
        /// <see cref="ListItem"/> properties the <c>(text)</c> overload cannot reach.
        /// </summary>
        public ListBuilder AddItem(Action<ListItemBuilder> configure)
        {
            var ib = new ListItemBuilder();
            configure(ib);

            ListItem item = ib.Build();
            item.IsNumbered = item.IsNumbered || _list.IsOrdered;
            _list.Items.Add(item);
            return this;
        }

        internal ListBlock Build() => _list;
    }

    public sealed class ListItemBuilder
    {
        private readonly ListItem _li = new();
        private TextRun? _lastRun;

        public ListItemBuilder Text(string text) { _li.Text = text; return this; }
        public ListItemBuilder Level(int level) { _li.Level = level; return this; }
        public ListItemBuilder Numbered() { _li.IsNumbered = true; return this; }

        /// <summary>
        /// A pre-resolved marker — <c>"1.2."</c>, <c>"•"</c>, <c>"א."</c> — overriding whatever
        /// would otherwise be derived, whether from <see cref="Numbered"/> on a loose item or from
        /// the item's position in a <see cref="ListBuilder"/>.
        ///
        /// <para>Still needed with a container, because a container numbers <c>1, 2, 3</c> and a
        /// producer may already have resolved something it cannot: a CSS <c>list-style-type</c>, a
        /// multi-level <c>"1.2."</c>, a DOCX sequence that restarts or skips.</para>
        /// </summary>
        public ListItemBuilder Marker(string marker) { _li.Marker = marker; return this; }

        public ListItemBuilder Direction(TextDirection direction) { _li.Direction = direction; return this; }
        public ListItemBuilder Rtl() { _li.Direction = TextDirection.RightToLeft; return this; }

        /// <summary>Formatting for the plain <see cref="Text"/> form (font, size, colour).</summary>
        public ListItemBuilder TextFormat(TextRun format) { _li.TextFormat = format; return this; }

        /// <summary>
        /// Adds a formatted run. Any run makes <see cref="ListItem.Runs"/> authoritative and
        /// <see cref="Text"/> unused, so do not mix the two on one item.
        /// </summary>
        public ListItemBuilder AddText(string text)
        {
            _lastRun = new TextRun { Text = text };
            _li.Runs.Add(_lastRun);
            return this;
        }

        /// <summary>See <see cref="ParagraphBuilder"/> for why this throws rather than doing nothing.</summary>
        /// <exception cref="InvalidOperationException">No <see cref="AddText"/> run is current.</exception>
        private TextRun CurrentRun(string method)
            => _lastRun ?? throw new InvalidOperationException(
                $"{method}() styles the preceding AddText(...) run, and none is current. " +
                "Call AddText(...) before it.");

        /// <summary>A list nested under this item.</summary>
        /// <remarks>
        /// The nesting lives here rather than in <see cref="Level"/> because a level is a number
        /// beside an item, not a relationship between items: it cannot say where a sublist starts
        /// or stops, so it cannot number one and cannot be turned into a nested
        /// <c>&lt;ul&gt;</c> or <c>/L</c>.
        /// </remarks>
        public ListItemBuilder AddList(Action<ListBuilder> configure)
        {
            var lb = new ListBuilder();
            configure(lb);
            _li.Sublist = lb.Build();
            return this;
        }

        public ListItemBuilder Bold() { CurrentRun(nameof(Bold)).IsBold = true; return this; }
        public ListItemBuilder Italic() { CurrentRun(nameof(Italic)).IsItalic = true; return this; }
        public ListItemBuilder FontSize(double size) { CurrentRun(nameof(FontSize)).FontSize = size; return this; }
        public ListItemBuilder Color(string hex) { CurrentRun(nameof(Color)).Color = hex; return this; }

        internal ListItem Build() => _li;
    }

    public sealed class TableBuilder
    {
        private readonly Table _table = new();

        public TableBuilder AddRow(Action<RowBuilder> configure)
        {
            var rb = new RowBuilder();
            configure(rb);
            _table.Rows.Add(rb.Build());
            return this;
        }

        public TableBuilder BorderWidth(double width) { _table.BorderWidth = width; return this; }
        public TableBuilder BorderColor(string hex) { _table.BorderColor = hex; return this; }

        /// <summary>Explicit total table width in points; unset fills the content width.</summary>
        public TableBuilder Width(double pts) { _table.WidthPt = pts; return this; }

        /// <summary>
        /// Explicit per-column widths in points, first column first. An entry &#8804;&#160;0 (or a
        /// missing trailing entry) means auto: that column shares the leftover width equally with
        /// the other auto columns. Passing none leaves every column equal, the default.
        /// </summary>
        public TableBuilder ColumnWidths(params double[] widthsPt)
        {
            _table.ColumnWidths.Clear();
            if (widthsPt != null) _table.ColumnWidths.AddRange(widthsPt);
            return this;
        }

        /// <summary>Mirror column order for a right-to-left table (DOCX <c>w:bidiVisual</c>).</summary>
        public TableBuilder RtlColumns() { _table.RtlColumns = true; return this; }

        /// <summary>Repeat the header rows at the top of each page the table spans.</summary>
        public TableBuilder RepeatHeaderRow() { _table.RepeatHeaderRow = true; return this; }

        /// <summary>
        /// Draw <see cref="BorderWidth"/> around the table's outer edge only, rather than as every
        /// cell's default border — CSS <c>table { border: … }</c> rather than the legacy
        /// <c>&lt;table border="1"&gt;</c>.
        /// </summary>
        public TableBuilder OuterBorderOnly() { _table.OuterBorderOnly = true; return this; }

        internal Table Build() => _table;
    }

    public sealed class RowBuilder
    {
        private readonly TableRow _row = new();
        private TextRun? _lastCellRun;

        public RowBuilder AddCell(string text)
        {
            var cell = new TableCell();
            var p = new Paragraph();
            _lastCellRun = new TextRun { Text = text };
            p.Runs.Add(_lastCellRun);
            cell.Paragraphs.Add(p);
            _row.Cells.Add(cell);
            return this;
        }

        /// <summary>
        /// A cell with arbitrary block content and its own formatting — multiple paragraphs, a
        /// nested table, an image, spans, merges, shading, per-side borders and padding.
        /// </summary>
        /// <remarks>
        /// <para>This overload is what makes <see cref="TableCell.Blocks"/> reachable. The
        /// <see cref="AddCell(string)"/> form writes a single paragraph into
        /// <see cref="TableCell.Paragraphs"/> and is unchanged, so existing documents export
        /// byte-identically — and the two must not be mixed on one cell, because
        /// <see cref="TableCell.Content"/> prefers <c>Blocks</c> whenever it is non-empty and
        /// would silently drop the paragraphs.</para>
        /// <para>Chained run formatting (<see cref="Bold"/> and friends) applies to
        /// <see cref="AddCell(string)"/> only; inside this overload the formatting belongs to the
        /// paragraph builder, which has the full run vocabulary.</para>
        /// </remarks>
        public RowBuilder AddCell(Action<CellBuilder> configure)
        {
            var cb = new CellBuilder();
            configure(cb);
            _row.Cells.Add(cb.Build());
            _lastCellRun = null;
            return this;
        }

        /// <summary>Explicit row height in points, treated as a minimum.</summary>
        public RowBuilder Height(double pts) { _row.HeightPt = pts; _row.ExactHeight = false; return this; }

        /// <summary>Explicit row height in points, exact (DOCX <c>hRule="exact"</c>).</summary>
        public RowBuilder ExactHeight(double pts) { _row.HeightPt = pts; _row.ExactHeight = true; return this; }

        /// <summary>
        /// Mark the row as a header row, which is what
        /// <see cref="TableBuilder.RepeatHeaderRow"/> repeats across page breaks. Distinct from
        /// <see cref="CellBuilder.Header"/>, which decides <c>/TH</c> versus <c>/TD</c> per cell.
        /// </summary>
        public RowBuilder Header() { _row.IsHeader = true; return this; }

        /// <summary>The cell run the chained formatting applies to. See
        /// <see cref="ParagraphBuilder"/> for why this throws rather than doing nothing.</summary>
        /// <exception cref="InvalidOperationException">No <see cref="AddCell(string)"/> run is current.</exception>
        private TextRun CurrentCellRun(string method)
            => _lastCellRun ?? throw new InvalidOperationException(
                $"{method}() styles the preceding AddCell(...), and none is current. " +
                "Call AddCell(...) before it.");

        public RowBuilder Bold() { CurrentCellRun(nameof(Bold)).IsBold = true; return this; }
        public RowBuilder Italic() { CurrentCellRun(nameof(Italic)).IsItalic = true; return this; }
        public RowBuilder FontSize(double size) { CurrentCellRun(nameof(FontSize)).FontSize = size; return this; }
        public RowBuilder Color(string hex) { CurrentCellRun(nameof(Color)).Color = hex; return this; }

        // Underline / Strikethrough / FontName complete the set: they style the same TextRun the
        // four above do, and their absence made cell text a strictly poorer citizen than paragraph
        // text for no reason anyone recorded.
        public RowBuilder Underline() { CurrentCellRun(nameof(Underline)).IsUnderline = true; return this; }
        public RowBuilder Strikethrough() { CurrentCellRun(nameof(Strikethrough)).IsStrikethrough = true; return this; }
        public RowBuilder FontName(string name) { CurrentCellRun(nameof(FontName)).FontName = name; return this; }

        internal TableRow Build() => _row;
    }

    /// <summary>
    /// A table cell with block content. Everything added here goes into
    /// <see cref="TableCell.Blocks"/>, which the renderer prefers over
    /// <see cref="TableCell.Paragraphs"/> — that is what allows nested tables and multi-paragraph
    /// cells, neither of which <see cref="RowBuilder.AddCell(string)"/> can express.
    /// </summary>
    public sealed class CellBuilder
    {
        private readonly TableCell _cell = new();

        public CellBuilder AddParagraph(Action<ParagraphBuilder> configure)
        {
            var pb = new ParagraphBuilder();
            configure(pb);
            _cell.Blocks.Add(pb.Build());
            return this;
        }

        /// <summary>A table nested inside this cell.</summary>
        public CellBuilder AddTable(Action<TableBuilder> configure)
        {
            var tb = new TableBuilder();
            configure(tb);
            _cell.Blocks.Add(tb.Build());
            return this;
        }

        public CellBuilder AddListItem(Action<ListItemBuilder> configure)
        {
            var lb = new ListItemBuilder();
            configure(lb);
            _cell.Blocks.Add(lb.Build());
            return this;
        }

        /// <summary>A list inside this cell.</summary>
        public CellBuilder AddList(Action<ListBuilder> configure)
        {
            var lb = new ListBuilder();
            configure(lb);
            _cell.Blocks.Add(lb.Build());
            return this;
        }

        /// <summary>Tag this cell as <c>/TH</c> rather than <c>/TD</c> in tagged output.</summary>
        public CellBuilder Header() { _cell.IsHeader = true; return this; }

        /// <summary>
        /// What a header cell labels, written as the <c>/Scope</c> of its <c>/TH</c>. A header
        /// cell with neither a scope nor an id/headers association is reported by PDF/UA checkers.
        /// </summary>
        public CellBuilder HeaderScope(TableHeaderScope scope) { _cell.HeaderScope = scope; return this; }

        public CellBuilder Width(double pts) { _cell.WidthPt = pts; return this; }
        public CellBuilder Background(string hex) { _cell.BackgroundColor = hex; return this; }

        /// <summary>Horizontal cells spanned (DOCX <c>w:gridSpan</c>); 1 is none.</summary>
        public CellBuilder GridSpan(int columns) { _cell.GridSpan = columns; return this; }

        /// <summary>Vertical merge state (DOCX <c>w:vMerge</c>).</summary>
        public CellBuilder VMerge(VerticalMerge merge) { _cell.VMerge = merge; return this; }

        public CellBuilder VAlign(CellVerticalAlign align) { _cell.VAlign = align; return this; }

        /// <summary>The same border on all four sides, overriding the table's default.</summary>
        public CellBuilder Borders(Border border)
        {
            _cell.BorderTop = border; _cell.BorderBottom = border;
            _cell.BorderLeft = border; _cell.BorderRight = border;
            return this;
        }

        public CellBuilder BorderTop(Border border) { _cell.BorderTop = border; return this; }
        public CellBuilder BorderBottom(Border border) { _cell.BorderBottom = border; return this; }
        public CellBuilder BorderLeft(Border border) { _cell.BorderLeft = border; return this; }
        public CellBuilder BorderRight(Border border) { _cell.BorderRight = border; return this; }

        /// <summary>Inner padding in points; 0 on a side keeps the renderer's default.</summary>
        public CellBuilder Padding(double left, double right, double top, double bottom)
        {
            _cell.MarginLeft = left; _cell.MarginRight = right;
            _cell.MarginTop = top; _cell.MarginBottom = bottom;
            return this;
        }

        internal TableCell Build() => _cell;
    }
}
