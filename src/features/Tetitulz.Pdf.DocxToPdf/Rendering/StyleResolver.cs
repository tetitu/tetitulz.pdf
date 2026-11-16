// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal sealed class StyleResolver
    {
        private readonly WordprocessingDocument _wordDoc;
        private readonly Dictionary<string, Style> _stylesById;
        private readonly string _defaultParaStyleId;
        private RunPropertiesBaseStyle _docDefaultRunProps;
        private ParagraphPropertiesBaseStyle _docDefaultParaProps;

        internal StyleResolver(WordprocessingDocument wordDoc)
        {
            _wordDoc = wordDoc;
            _stylesById = new Dictionary<string, Style>();
            LoadStyles();
            _defaultParaStyleId = FindDefaultParagraphStyleId();
        }

        private void LoadStyles()
        {
            var stylesPart = _wordDoc.MainDocumentPart?.StyleDefinitionsPart;
            if (stylesPart?.Styles == null) return;

            var docDefaults = stylesPart.Styles.DocDefaults;
            if (docDefaults != null)
            {
                _docDefaultRunProps = docDefaults.RunPropertiesDefault?.RunPropertiesBaseStyle;
                _docDefaultParaProps = docDefaults.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle;
            }

            foreach (var style in stylesPart.Styles.Elements<Style>())
            {
                if (style.StyleId?.Value != null)
                    _stylesById[style.StyleId.Value] = style;
            }
        }

        private string FindDefaultParagraphStyleId()
        {
            // The style flagged w:default="1" of type paragraph is the implicit
            // base for paragraphs that carry no explicit pStyle.
            foreach (var style in _stylesById.Values)
            {
                if (style.Type?.Value == StyleValues.Paragraph
                    && style.Default?.Value == true)
                    return style.StyleId?.Value;
            }
            return null;
        }

        // ---- Invariant numeric parsing (OOXML attribute values are invariant) ----
        private static bool TryInt(string s, out double value)
            => double.TryParse(s, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                               CultureInfo.InvariantCulture, out value);

        // ------------------------------------------------------------------
        //  Style chains (walk w:basedOn with a cycle guard)
        // ------------------------------------------------------------------

        // Ordered most-specific → least-specific list of styles that contribute
        // run properties to a run: the run's own character style chain first, then
        // the paragraph style chain.
        private IEnumerable<Style> RunStyleChain(Run run, Paragraph paragraph)
        {
            var seen = new HashSet<string>();
            foreach (var s in StyleChain(run?.RunProperties?.RunStyle?.Val?.Value, seen))
                yield return s;
            foreach (var s in StyleChain(ParaStyleId(paragraph), seen))
                yield return s;
        }

        private IEnumerable<Style> ParagraphStyleChain(Paragraph paragraph)
            => StyleChain(ParaStyleId(paragraph), new HashSet<string>());

        /// <remarks>
        /// <b>Read once per paragraph.</b> The SDK finds <c>ParagraphProperties</c> by scanning the paragraph's
        /// children, and a paragraph without a <c>w:pPr</c> is scanned to its end — once for every run's font,
        /// size and colour would make a paragraph quadratic in its runs: one of 8000 runs would take the better part
        /// of a minute on either path. A paragraph's properties do not change while it is converted.
        /// </remarks>
        private string ParaStyleId(Paragraph paragraph)
        {
            if (paragraph == null) return _defaultParaStyleId;
            if (!ReferenceEquals(paragraph, _styledParagraph))
            {
                _styledParagraph = paragraph;
                _styledParagraphStyleId = PropertiesOf(paragraph)?.ParagraphStyleId?.Val?.Value ?? _defaultParaStyleId;
            }
            return _styledParagraphStyleId;
        }

        /// <summary>
        /// A paragraph's <c>w:pPr</c>, read once while it is the one being converted. Each flush of its
        /// runs asks several times, a page break or a drawing is a flush, and a scan of a paragraph of
        /// many such runs would be quadratic in them.
        /// </summary>
        private ParagraphProperties PropertiesOf(Paragraph paragraph)
        {
            if (paragraph == null) return null;
            if (!ReferenceEquals(paragraph, _propertiesOwner))
            {
                _propertiesOwner = paragraph;
                ParagraphPropertyReads++;
                _ownersProperties = paragraph.ParagraphProperties;
            }
            return _ownersProperties;
        }

        private Paragraph _propertiesOwner;
        private ParagraphProperties _ownersProperties;

        /// <summary>
        /// A table's <c>w:tblPr</c>, read once a table. Asked of every cell, and the scan for one a table
        /// does not have runs through all its rows: a few kilobytes of deflated rows would be quadratic.
        /// By table rather than the last one asked of: a cell holding a table alternates the two.
        /// </summary>
        internal TableProperties TablePropertiesOf(Table table)
        {
            if (table == null) return null;
            if (!_tableProperties.TryGetValue(table, out TableProperties properties))
            {
                TablePropertyReads++;
                properties = table.GetFirstChild<TableProperties>();
                _tableProperties[table] = properties;
            }
            return properties;
        }

        private readonly Dictionary<Table, TableProperties> _tableProperties = new Dictionary<Table, TableProperties>();

        /// <summary>How many tables have had their properties looked for: the evidence that a cell does not look again.</summary>
        internal int TablePropertyReads { get; private set; }

        private Paragraph _styledParagraph;
        private string _styledParagraphStyleId;

        /// <summary>How many times a paragraph's properties were looked up for its style — the per-paragraph read's evidence.</summary>
        internal long ParagraphPropertyReads { get; private set; }

        /// <summary>
        /// The most styles one <c>w:basedOn</c> chain is followed through. Every run's font, size and colour walk
        /// their style's chain until one says, so the chain's length multiplies the run count, and the file
        /// chooses both: a chain of 20 000 styles under a paragraph of 2000 runs would take over three
        /// minutes. Word's own styles chain a handful deep; a template's rarely a dozen.
        /// </summary>
        internal const int MaxStyleChain = 64;

        private bool _chainCutReported;

        /// <summary>How many styles the chains walked have yielded — the chain bound's evidence.</summary>
        internal long StyleChainSteps { get; private set; }

        private IEnumerable<Style> StyleChain(string styleId, HashSet<string> seen)
        {
            string id = styleId;
            int steps = 0;
            while (id != null && seen.Add(id) && _stylesById.TryGetValue(id, out var style))
            {
                if (++steps > MaxStyleChain)
                {
                    if (!_chainCutReported)
                    {
                        _chainCutReported = true;
                        DocxDiagnostics.Report("StyleResolver.StyleChain",
                            $"a w:basedOn chain is longer than {MaxStyleChain} styles; the styles past that are not consulted. "
                            + "Reported once per document.");
                    }
                    yield break;
                }
                StyleChainSteps++;
                yield return style;
                id = style.BasedOn?.Val?.Value;
            }
        }

        // ------------------------------------------------------------------
        //  Run-level properties
        // ------------------------------------------------------------------

        internal string ResolveFontName(Run run, Paragraph paragraph)
        {
            var runFonts = run?.RunProperties?.RunFonts;
            if (runFonts?.Ascii?.Value != null) return runFonts.Ascii.Value;

            foreach (var style in RunStyleChain(run, paragraph))
            {
                var sf = style.StyleRunProperties?.RunFonts;
                if (sf?.Ascii?.Value != null) return sf.Ascii.Value;
            }

            if (_docDefaultRunProps?.GetFirstChild<RunFonts>()?.Ascii?.Value is string d)
                return d;
            return StandardFonts.Helvetica;
        }

        internal double ResolveFontSizeInPoints(Run run, Paragraph paragraph, double defaultSize)
        {
            // half-points → points
            var fs = run?.RunProperties?.FontSize?.Val?.Value;
            if (fs != null && TryInt(fs, out double hp)) return PdfUnits.HalfPointsToPoints(hp);

            foreach (var style in RunStyleChain(run, paragraph))
            {
                var s = style.StyleRunProperties?.FontSize?.Val?.Value;
                if (s != null && TryInt(s, out double shp)) return PdfUnits.HalfPointsToPoints(shp);
            }

            var def = _docDefaultRunProps?.GetFirstChild<FontSize>()?.Val?.Value;
            if (def != null && TryInt(def, out double dhp)) return PdfUnits.HalfPointsToPoints(dhp);

            return defaultSize;
        }

        internal bool ResolveIsBold(Run run, Paragraph paragraph)
            => ResolveBoldNullable(run, paragraph) ?? false;

        // As ResolveIsBold, but returns null when neither the run, its style chain, nor the
        // document default specifies bold — so a caller can supply a lower-precedence default
        // (e.g. a table style's conditional header formatting) only when nothing else applies.
        internal bool? ResolveBoldNullable(Run run, Paragraph paragraph) => ResolveToggle<Bold>(run, paragraph);

        internal bool ResolveIsItalic(Run run, Paragraph paragraph) => ResolveToggle<Italic>(run, paragraph) ?? false;

        /// <summary><c>w:caps</c>: the run is drawn in capitals.</summary>
        internal bool ResolveCaps(Run run, Paragraph paragraph) => ResolveToggle<Caps>(run, paragraph) ?? false;

        /// <summary><c>w:smallCaps</c>: the run's lowercase letters are drawn as smaller capitals.</summary>
        internal bool ResolveSmallCaps(Run run, Paragraph paragraph) => ResolveToggle<SmallCaps>(run, paragraph) ?? false;

        /// <summary><c>w:vanish</c>: hidden text, which Word leaves out of print and PDF.</summary>
        internal bool ResolveVanish(Run run, Paragraph paragraph) => ResolveToggle<Vanish>(run, paragraph) ?? false;

        /// <summary>
        /// A toggle property (ECMA-376 Part 1 §17.7.3: <c>w:b</c>, <c>w:i</c>, <c>w:strike</c>,
        /// <c>w:caps</c>, <c>w:smallCaps</c>, <c>w:vanish</c>): the run's own value when it states
        /// one; else the exclusive or of the levels of the style hierarchy that state one — the
        /// character style chain's nearest value, the paragraph style chain's, the document
        /// defaults' — so an italic character style inside an italic paragraph style is upright, as
        /// Word draws it; null when no level states it.
        /// </summary>
        private bool? ResolveToggle<T>(Run run, Paragraph paragraph) where T : OnOffType
        {
            bool? direct = ToggleOf<T>(run?.RunProperties);
            if (direct != null) return direct;

            bool? character = null, paragraphStyle = null;
            foreach (var style in StyleChain(run?.RunProperties?.RunStyle?.Val?.Value, new HashSet<string>()))
                if ((character = ToggleOf<T>(style.StyleRunProperties)) != null) break;
            foreach (var style in ParagraphStyleChain(paragraph))
                if ((paragraphStyle = ToggleOf<T>(style.StyleRunProperties)) != null) break;
            bool? defaults = ToggleOf<T>(_docDefaultRunProps);

            if (character == null && paragraphStyle == null && defaults == null) return null;
            return (character ?? false) ^ (paragraphStyle ?? false) ^ (defaults ?? false);
        }

        private static bool? ToggleOf<T>(OpenXmlElement holder) where T : OnOffType
            => holder?.GetFirstChild<T>() is T toggle ? toggle.Val == null || toggle.Val.Value : (bool?)null;

        /// <summary>
        /// The numbering a paragraph takes: its own <c>w:numPr</c>, else the first of its style chain
        /// stating one (§17.3.1.19: a paragraph style may number its paragraphs); null for none, and
        /// for a <c>w:numId</c> of 0, which cancels an inherited numbering.
        /// </summary>
        internal NumberingProperties ResolveNumbering(Paragraph paragraph)
        {
            var own = PropertiesOf(paragraph)?.NumberingProperties;
            if (own != null) return own.NumberingId?.Val?.Value == 0 ? null : own;
            foreach (var style in ParagraphStyleChain(paragraph))
            {
                var inherited = style.StyleParagraphProperties?.NumberingProperties;
                if (inherited != null) return inherited.NumberingId?.Val?.Value == 0 ? null : inherited;
            }
            return null;
        }

        internal string ResolveColor(Run run, Paragraph paragraph)
        {
            // The most specific tier that defines a colour wins. An explicit
            // "auto" means automatic (black) and overrides an inherited colour.
            var col = run?.RunProperties?.Color;
            string fromCol = ColorHex(col);
            if (fromCol != null) return fromCol == "auto" ? null : fromCol;

            foreach (var style in RunStyleChain(run, paragraph))
            {
                string sc = ColorHex(style.StyleRunProperties?.Color);
                if (sc != null) return sc == "auto" ? null : sc;
            }

            string dc = ColorHex(_docDefaultRunProps?.GetFirstChild<Color>());
            if (dc != null) return dc == "auto" ? null : dc;
            return null; // default black
        }

        // An explicit RRGGBB value, else a resolved theme colour (w:themeColor).
        private string ColorHex(Color c)
        {
            if (c == null) return null;
            var v = c.Val?.Value;
            if (v != null && v != "auto") return v;
            if (c.ThemeColor?.Value != null)
            {
                var hex = ThemeColorHex(c.ThemeColor.Value);
                if (hex != null) return hex;
            }
            return v; // may be "auto" or null
        }

        private Dictionary<string, string> _themeColors;
        private string ThemeColorHex(ThemeColorValues v)
        {
            _themeColors ??= LoadThemeColors();
            string key =
                v == ThemeColorValues.Accent1 ? "accent1" : v == ThemeColorValues.Accent2 ? "accent2" :
                v == ThemeColorValues.Accent3 ? "accent3" : v == ThemeColorValues.Accent4 ? "accent4" :
                v == ThemeColorValues.Accent5 ? "accent5" : v == ThemeColorValues.Accent6 ? "accent6" :
                v == ThemeColorValues.Hyperlink ? "hlink" : v == ThemeColorValues.FollowedHyperlink ? "folHlink" :
                v == ThemeColorValues.Text1 || v == ThemeColorValues.Dark1 ? "dk1" :
                v == ThemeColorValues.Text2 || v == ThemeColorValues.Dark2 ? "dk2" :
                v == ThemeColorValues.Background1 || v == ThemeColorValues.Light1 ? "lt1" :
                v == ThemeColorValues.Background2 || v == ThemeColorValues.Light2 ? "lt2" : null;
            return key != null && _themeColors.TryGetValue(key, out var hex) ? hex : null;
        }

        /// <summary>
        /// The document's theme element, or <c>null</c>. Exposed so the DrawingML colour resolver
        /// can reach <c>a:fmtScheme</c> for the <c>fillRef</c>/<c>lnRef</c> style indirection —
        /// navigated by local name there, like the rest of the DrawingML reading.
        /// </summary>
        internal DocumentFormat.OpenXml.Drawing.Theme Theme
            => _wordDoc.MainDocumentPart?.ThemePart?.Theme;

        /// <summary>
        /// A theme colour by its <b>DrawingML</b> token, over the same cache the WordprocessingML
        /// path uses. Returns <c>null</c> when the document has no theme part.
        ///
        /// <para>
        /// <b>The DrawingML token set is not the WordprocessingML one</b>, and the difference is a
        /// trap: <c>bg1</c> maps to <c>lt1</c>, not to <c>dk1</c>. Resolving an
        /// <c>a:schemeClr val="bg1"</c> through <see cref="ThemeColorHex"/>'s enum would give the
        /// wrong colour, so the two mappings are kept separate on purpose.
        /// </para>
        /// </summary>
        internal string ThemeHexByDrawingMlToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            _themeColors ??= LoadThemeColors();

            string key = token switch
            {
                "tx1" => "dk1",
                "bg1" => "lt1",
                "tx2" => "dk2",
                "bg2" => "lt2",
                _ => token,
            };
            return _themeColors.TryGetValue(key, out var hex) ? hex : null;
        }

        private Dictionary<string, string> LoadThemeColors()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var scheme = _wordDoc.MainDocumentPart?.ThemePart?.Theme?.ThemeElements?.ColorScheme;
            if (scheme == null) return map;
            void Add(string key, DocumentFormat.OpenXml.Drawing.Color2Type c)
            {
                if (c == null) return;
                var hex = c.RgbColorModelHex?.Val?.Value ?? c.SystemColor?.LastColor?.Value;
                if (hex != null) map[key] = hex;
            }
            Add("dk1", scheme.Dark1Color); Add("lt1", scheme.Light1Color);
            Add("dk2", scheme.Dark2Color); Add("lt2", scheme.Light2Color);
            Add("accent1", scheme.Accent1Color); Add("accent2", scheme.Accent2Color);
            Add("accent3", scheme.Accent3Color); Add("accent4", scheme.Accent4Color);
            Add("accent5", scheme.Accent5Color); Add("accent6", scheme.Accent6Color);
            Add("hlink", scheme.Hyperlink); Add("folHlink", scheme.FollowedHyperlinkColor);
            return map;
        }

        // The complex-script font (w:cs) used for Hebrew/Arabic runs, falling back
        // to the regular (ascii) font when none is declared.
        internal string ResolveComplexScriptFontName(Run run, Paragraph paragraph)
        {
            var cs = run?.RunProperties?.RunFonts?.ComplexScript?.Value;
            if (cs != null) return cs;
            foreach (var style in RunStyleChain(run, paragraph))
            {
                var sc = style.StyleRunProperties?.RunFonts?.ComplexScript?.Value;
                if (sc != null) return sc;
            }
            return ResolveFontName(run, paragraph);
        }

        // w:rFonts @eastAsia — the font family for CJK text.
        internal string ResolveEastAsiaFontName(Run run, Paragraph paragraph)
        {
            var ea = run?.RunProperties?.RunFonts?.EastAsia?.Value;
            if (ea != null) return ea;
            foreach (var style in RunStyleChain(run, paragraph))
            {
                var se = style.StyleRunProperties?.RunFonts?.EastAsia?.Value;
                if (se != null) return se;
            }
            return null;
        }

        // w:u — underline (any value except "none").
        internal bool ResolveUnderline(Run run, Paragraph paragraph)
        {
            var u = run?.RunProperties?.Underline;
            if (u?.Val != null) return u.Val.Value != UnderlineValues.None;
            foreach (var style in RunStyleChain(run, paragraph))
            {
                var su = style.StyleRunProperties?.Underline;
                if (su?.Val != null) return su.Val.Value != UnderlineValues.None;
            }
            return false;
        }

        // w:strike — strikethrough.
        internal bool ResolveStrike(Run run, Paragraph paragraph) => ResolveToggle<Strike>(run, paragraph) ?? false;

        // ------------------------------------------------------------------
        //  Run properties that resolve through the style chain
        //
        //  These five took the `paragraph` argument and never used it: they read only the run's
        //  own w:rPr, so a character style — the textbook way to mark a footnote reference
        //  (<w:style><w:rPr><w:vertAlign w:val="superscript"/>) — or a w:docDefaults setting was
        //  silently ignored while every neighbouring resolver in this file walked all three tiers.
        //  Both output paths (RunRenderer and DocxToBuilderMapper) consume them, so both were wrong.
        // ------------------------------------------------------------------

        // w:highlight — text highlight; returns an RRGGBB hex colour or null.
        internal string ResolveHighlight(Run run, Paragraph paragraph)
        {
            var h = run?.RunProperties?.Highlight?.Val;
            if (h != null) return h.Value == HighlightColorValues.None ? null : HighlightHex(h.Value);

            foreach (var style in RunStyleChain(run, paragraph))
            {
                var sh = style.StyleRunProperties?.GetFirstChild<Highlight>()?.Val;
                if (sh != null) return sh.Value == HighlightColorValues.None ? null : HighlightHex(sh.Value);
            }

            var dh = _docDefaultRunProps?.GetFirstChild<Highlight>()?.Val;
            if (dh != null) return dh.Value == HighlightColorValues.None ? null : HighlightHex(dh.Value);
            return null;
        }

        // w:vertAlign — 1 = superscript, -1 = subscript, 0 = baseline.
        internal int ResolveVertAlign(Run run, Paragraph paragraph)
        {
            int? own = VertAlignOf(run?.RunProperties?.VerticalTextAlignment?.Val);
            if (own.HasValue) return own.Value;

            foreach (var style in RunStyleChain(run, paragraph))
            {
                int? sv = VertAlignOf(style.StyleRunProperties?.VerticalTextAlignment?.Val);
                if (sv.HasValue) return sv.Value;
            }

            return VertAlignOf(_docDefaultRunProps?.GetFirstChild<VerticalTextAlignment>()?.Val) ?? 0;
        }

        private static int? VertAlignOf(EnumValue<VerticalPositionValues> v)
        {
            if (v == null) return null;
            if (v.Value == VerticalPositionValues.Superscript) return 1;
            if (v.Value == VerticalPositionValues.Subscript) return -1;
            return 0;   // an explicit "baseline" is a decision, and outranks the tier below it
        }

        // w:position — raised/lowered baseline in points (value is half-points, signed).
        internal double ResolveTextPosition(Run run, Paragraph paragraph)
        {
            var p = run?.RunProperties?.Position?.Val;
            if (p != null && TryInt(p, out double hp)) return PdfUnits.HalfPointsToPoints(hp);

            foreach (var style in RunStyleChain(run, paragraph))
            {
                var sp = style.StyleRunProperties?.Position?.Val;
                if (sp != null && TryInt(sp, out double shp)) return PdfUnits.HalfPointsToPoints(shp);
            }

            var dp = _docDefaultRunProps?.GetFirstChild<Position>()?.Val;
            if (dp != null && TryInt(dp, out double dhp)) return PdfUnits.HalfPointsToPoints(dhp);
            return 0;
        }

        // w:spacing (run) — inter-character spacing in points (value is twips, signed).
        internal double ResolveCharacterSpacing(Run run, Paragraph paragraph)
        {
            var s = run?.RunProperties?.Spacing?.Val;
            if (s != null && TryInt(s, out double tw)) return PdfUnits.TwipsToPoints(tw);

            foreach (var style in RunStyleChain(run, paragraph))
            {
                var ss = style.StyleRunProperties?.Spacing?.Val;
                if (ss != null && TryInt(ss, out double stw)) return PdfUnits.TwipsToPoints(stw);
            }

            var ds = _docDefaultRunProps?.GetFirstChild<Spacing>()?.Val;
            if (ds != null && TryInt(ds, out double dtw)) return PdfUnits.TwipsToPoints(dtw);
            return 0;
        }

        // w:kern — kern the run's font when its size is at least this many half-points; 0 or absent
        // is off. The run, then its style chain, then the document defaults, as for w:spacing.
        internal bool ResolveKerning(Run run, Paragraph paragraph, double fontSizePt)
        {
            uint? threshold = run?.RunProperties?.Kern?.Val?.Value;
            if (threshold == null)
            {
                foreach (var style in RunStyleChain(run, paragraph))
                {
                    threshold = style.StyleRunProperties?.Kern?.Val?.Value;
                    if (threshold != null) break;
                }
            }
            threshold ??= _docDefaultRunProps?.GetFirstChild<Kern>()?.Val?.Value;
            return threshold is uint t && t > 0 && fontSizePt * 2 >= t;
        }

        // w14:cntxtAlts — Word 2010's "use contextual alternates": the font's calt feature. Off unless
        // set; the run, then its style chain, then the document defaults, as for w:kern. An element
        // with no w14:val is on; "false", "off" and "0" are off.
        internal bool ResolveContextualAlternates(Run run, Paragraph paragraph)
        {
            var set = ContextualAlternativesIn(run?.RunProperties);
            if (set == null)
            {
                foreach (var style in RunStyleChain(run, paragraph))
                {
                    set = ContextualAlternativesIn(style.StyleRunProperties);
                    if (set != null) break;
                }
            }
            set ??= ContextualAlternativesIn(_docDefaultRunProps);
            return OoxmlOnOff.ElementIsOn(set);
        }

        // w14:ligatures — Word 2010's "ligatures" setting: the font's liga feature where its w14:val is
        // anything but none (standard, contextual, historical, discretional and their combinations
        // all include the standard ones in Word's own grouping; this reads the standard set for any
        // of them). Off unless set, as Word leaves it; the run, then its style chain, then the
        // document defaults, as for w14:cntxtAlts.
        internal bool ResolveLigatures(Run run, Paragraph paragraph)
            => LigaturesValue(run, paragraph) is string val && val != "none";

        /// <summary>
        /// Whether a run's <c>w14:ligatures</c> names the discretional set (<c>discretional</c>,
        /// <c>standardDiscretional</c>, …, <c>all</c>) — the font's <c>dlig</c>.
        /// </summary>
        internal bool ResolveDiscretionaryLigatures(Run run, Paragraph paragraph)
            => LigaturesValue(run, paragraph) is string val
               && (val == "all" || val.IndexOf("iscretional", StringComparison.Ordinal) >= 0);

        /// <summary>
        /// Whether a run's <c>w14:ligatures</c> names the historical set (<c>historical</c>,
        /// <c>standardHistorical</c>, …, <c>all</c>) — the font's <c>hlig</c>.
        /// </summary>
        internal bool ResolveHistoricalLigatures(Run run, Paragraph paragraph)
            => LigaturesValue(run, paragraph) is string val
               && (val == "all" || val.IndexOf("istorical", StringComparison.Ordinal) >= 0);

        /// <summary>The <c>w14:ligatures</c> value in force: the run's, then its style chain's, then the document defaults'; null when none says.</summary>
        private string LigaturesValue(Run run, Paragraph paragraph)
        {
            var set = LigaturesIn(run?.RunProperties);
            if (set == null)
            {
                foreach (var style in RunStyleChain(run, paragraph))
                {
                    set = LigaturesIn(style.StyleRunProperties);
                    if (set != null) break;
                }
            }
            set ??= LigaturesIn(_docDefaultRunProps);
            return OoxmlAttribute.Value(set, "val");
        }

        private static DocumentFormat.OpenXml.OpenXmlElement LigaturesIn(DocumentFormat.OpenXml.OpenXmlElement properties)
            => properties?.ChildElements.FirstOrDefault(e => e.LocalName == "ligatures" && e.NamespaceUri == Word2010Namespace);

        private const string Word2010Namespace = "http://schemas.microsoft.com/office/word/2010/wordml";

        // By name, not type: the SDK types w14:cntxtAlts in a run's properties but reads it as an
        // unknown element in a style's and the document defaults'.
        private static DocumentFormat.OpenXml.OpenXmlElement ContextualAlternativesIn(DocumentFormat.OpenXml.OpenXmlElement properties)
            => properties?.ChildElements.FirstOrDefault(e => e.LocalName == "cntxtAlts" && e.NamespaceUri == Word2010Namespace);

        // w:w — horizontal text scale percentage (100 = normal).
        internal int ResolveTextScale(Run run, Paragraph paragraph)
        {
            var w = run?.RunProperties?.CharacterScale?.Val;
            if (w != null) return (int)w.Value;

            foreach (var style in RunStyleChain(run, paragraph))
            {
                var sw = style.StyleRunProperties?.CharacterScale?.Val;
                if (sw != null) return (int)sw.Value;
            }

            var dw = _docDefaultRunProps?.GetFirstChild<CharacterScale>()?.Val;
            return dw != null ? (int)dw.Value : 100;
        }

        private static string HighlightHex(HighlightColorValues v)
        {
            if (v == HighlightColorValues.Yellow) return "FFFF00";
            if (v == HighlightColorValues.Green) return "00FF00";
            if (v == HighlightColorValues.Cyan) return "00FFFF";
            if (v == HighlightColorValues.Magenta) return "FF00FF";
            if (v == HighlightColorValues.Blue) return "0000FF";
            if (v == HighlightColorValues.Red) return "FF0000";
            if (v == HighlightColorValues.DarkBlue) return "000080";
            if (v == HighlightColorValues.DarkCyan) return "008080";
            if (v == HighlightColorValues.DarkGreen) return "008000";
            if (v == HighlightColorValues.DarkMagenta) return "800080";
            if (v == HighlightColorValues.DarkRed) return "800000";
            if (v == HighlightColorValues.DarkYellow) return "808000";
            if (v == HighlightColorValues.DarkGray) return "808080";
            if (v == HighlightColorValues.LightGray) return "C0C0C0";
            if (v == HighlightColorValues.Black) return "000000";
            return "FFFF00"; // default highlight = yellow
        }

        // With w:lineRule="auto" the w:line value is expressed in 240ths of a single-spaced
        // line, so 240 = single, 360 = 1.5×, 480 = double.
        private const double AutoLineSpacingUnitsPerLine = 240.0;

        // w:spacing w:line / w:lineRule — explicit line height in points, or 0 if none.
        internal double ResolveLineHeight(Paragraph paragraph, double fontSize)
        {
            // OOXML resolves each ATTRIBUTE independently, not the w:spacing element as a unit.
            // `direct ?? FirstParaSpacing(...)` selected a direct w:spacing that carried only
            // w:before/w:after — an extremely common pairing with a style that sets w:line — and
            // then returned 0 because that element states no w:line, so the style's double spacing
            // was discarded and the paragraph rendered single-spaced among its double-spaced
            // siblings. Take the direct element only when it actually carries w:line;
            // FirstParaSpacing already applies the same test to each tier of the style chain.
            var spacing = LineSpacingOf(paragraph);
            if (spacing?.Line?.Value == null) return 0;
            if (!double.TryParse(spacing.Line.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double line))
                return 0;

            var rule = spacing.LineRule?.Value;
            if (rule == LineSpacingRuleValues.Exact || rule == LineSpacingRuleValues.AtLeast)
            {
                double pts = PdfUnits.TwipsToPoints(line); // twips → points
                return rule == LineSpacingRuleValues.AtLeast
                    ? Math.Max(pts, fontSize * DocxLayoutMetrics.SingleLineFactor) : pts;
            }
            // Auto (default): line is in 240ths of a line (multiple of single spacing).
            return fontSize * DocxLayoutMetrics.SingleLineFactor * (line / AutoLineSpacingUnitsPerLine);
        }

        /// <summary>
        /// The <c>w:spacing</c> whose <c>w:line</c> a paragraph's lines take — the first level that states one: its own,
        /// its style chain's, the document defaults' (<see cref="ParagraphLevels{T}"/>, as the other paragraph properties
        /// are resolved) — remembered for the last paragraph asked, as <see cref="ParaStyleId"/> is: the line height is
        /// asked once a line, and with no <c>w:pPr</c> the SDK looks for one through every child, so a paragraph of n
        /// runs cost lines × n unremembered (32 000 runs, 10 s).
        /// </summary>
        private SpacingBetweenLines LineSpacingOf(Paragraph paragraph)
        {
            if (_spacingAsked && ReferenceEquals(paragraph, _spacingParagraph)) return _spacing;
            _spacing = null;
            foreach (var spacing in ParagraphLevels(paragraph, p => p?.SpacingBetweenLines,
                         s => s.StyleParagraphProperties?.SpacingBetweenLines, d => d.GetFirstChild<SpacingBetweenLines>()))
            {
                if (spacing.Line?.Value == null) continue;
                _spacing = spacing;
                break;
            }
            _spacingParagraph = paragraph;
            _spacingAsked = true;
            return _spacing;
        }

        private Paragraph _spacingParagraph;
        private SpacingBetweenLines _spacing;
        private bool _spacingAsked;

        // w:bidi on a paragraph — RTL base direction (right alignment, RTL flow).
        internal bool ResolveParagraphRtl(Paragraph paragraph)
        {
            var bidi = PropertiesOf(paragraph)?.BiDi;
            if (bidi != null) return bidi.Val == null || bidi.Val.Value;
            foreach (var style in ParagraphStyleChain(paragraph))
            {
                var b = style.StyleParagraphProperties?.GetFirstChild<BiDi>();
                if (b != null) return b.Val == null || b.Val.Value;
            }
            return false;
        }

        // ------------------------------------------------------------------
        //  Paragraph-level properties
        // ------------------------------------------------------------------

        /// <summary>
        /// The edge a paragraph's lines are drawn against: <see cref="ResolveAlignment"/>, with a
        /// right-to-left (<c>w:bidi</c>) paragraph's <c>left</c> — the value an absent <c>w:jc</c> also
        /// resolves to — taken as its start edge, the right. Both paths ask it: the Builder mapper
        /// passed <c>left</c> through, so every Hebrew paragraph on that path hugged the left margin.
        /// </summary>
        internal JustificationValues ResolveDisplayAlignment(Paragraph paragraph)
        {
            var alignment = ResolveAlignment(paragraph);
            return alignment == JustificationValues.Left && ResolveParagraphRtl(paragraph)
                ? JustificationValues.Right
                : alignment;
        }

        internal JustificationValues ResolveAlignment(Paragraph paragraph)
        {
            var j = PropertiesOf(paragraph)?.Justification?.Val?.Value;
            if (j != null) return j.Value;

            foreach (var style in ParagraphStyleChain(paragraph))
            {
                var sj = style.StyleParagraphProperties?.Justification?.Val?.Value;
                if (sj != null) return sj.Value;
            }

            var dj = _docDefaultParaProps?.GetFirstChild<Justification>()?.Val?.Value;
            if (dj != null) return dj.Value;
            return JustificationValues.Left;
        }

        /// <summary>
        /// A paragraph's space before and after, in points, each attribute resolved on its own.
        /// </summary>
        /// <remarks>
        /// Each is the first a level states — the paragraph's own, then its style chain, then the document
        /// defaults, attribute by attribute: taking the whole element from the first level that has one would
        /// let a direct w:spacing stating only w:before (as Word writes one) set the space after to zero rather
        /// than leaving the style's.
        /// </remarks>
        internal (double before, double after) ResolveParagraphSpacing(Paragraph paragraph)
        {
            double? before = null, after = null;
            foreach (var spacing in ParagraphLevels(paragraph, p => p?.SpacingBetweenLines,
                         s => s.StyleParagraphProperties?.SpacingBetweenLines, d => d.GetFirstChild<SpacingBetweenLines>()))
            {
                if (before == null && spacing.Before?.Value != null && TryInt(spacing.Before.Value, out double b))
                    before = PdfUnits.TwipsToPoints(b);
                if (after == null && spacing.After?.Value != null && TryInt(spacing.After.Value, out double a))
                    after = PdfUnits.TwipsToPoints(a);
                if (before != null && after != null) break;
            }
            return (before ?? 0, after ?? 0);
        }

        /// <summary>
        /// A paragraph's left, right and first-line indents, in points, each attribute resolved on its own as
        /// <see cref="ResolveParagraphSpacing"/> resolves its two. <c>w:firstLine</c> and <c>w:hanging</c> are
        /// one attribute: a level stating either replaces what a lower one said of both.
        /// </summary>
        internal (double left, double right, double firstLine) ResolveIndentation(Paragraph paragraph)
        {
            double? left = null, right = null, firstLine = null;
            foreach (var indent in ParagraphLevels(paragraph, p => p?.Indentation,
                         s => s.StyleParagraphProperties?.Indentation, d => d.GetFirstChild<Indentation>()))
            {
                if (left == null && indent.Left?.Value != null && TryInt(indent.Left.Value, out double l))
                    left = PdfUnits.TwipsToPoints(l);
                if (right == null && indent.Right?.Value != null && TryInt(indent.Right.Value, out double rr))
                    right = PdfUnits.TwipsToPoints(rr);
                // A hanging indent outdents the first line relative to Left; FirstLine indents it.
                if (firstLine == null)
                {
                    if (indent.FirstLine?.Value != null && TryInt(indent.FirstLine.Value, out double fl))
                        firstLine = PdfUnits.TwipsToPoints(fl);
                    else if (indent.Hanging?.Value != null && TryInt(indent.Hanging.Value, out double hg))
                        firstLine = -PdfUnits.TwipsToPoints(hg);
                }
                if (left != null && right != null && firstLine != null) break;
            }
            return (left ?? 0, right ?? 0, firstLine ?? 0);
        }

        /// <summary>
        /// One paragraph property's element at each level that has it, most specific first: the paragraph's own,
        /// each style of its chain, the document defaults.
        /// </summary>
        private IEnumerable<T> ParagraphLevels<T>(Paragraph paragraph, Func<ParagraphProperties, T> direct,
            Func<Style, T> styled, Func<OpenXmlElement, T> defaulted) where T : class
        {
            var own = direct(PropertiesOf(paragraph));
            if (own != null) yield return own;
            foreach (var style in ParagraphStyleChain(paragraph))
            {
                var s = styled(style);
                if (s != null) yield return s;
            }
            if (_docDefaultParaProps != null)
            {
                var d = defaulted(_docDefaultParaProps);
                if (d != null) yield return d;
            }
        }

        // ------------------------------------------------------------------
        //  Font mapping (DOCX family name → PDF standard-14)
        // ------------------------------------------------------------------

        internal string MapToPdfFontName(string docxFontName, bool bold, bool italic)
        {
            var lower = (docxFontName ?? "").ToLowerInvariant();

            if ((lower.Contains("times") || lower.Contains("serif")) && !lower.Contains("sans"))
            {
                if (bold && italic) return StandardFonts.TimesBoldItalic;
                if (bold) return StandardFonts.TimesBold;
                if (italic) return StandardFonts.TimesItalic;
                return StandardFonts.TimesRoman;
            }

            if (lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consolas"))
            {
                if (bold && italic) return StandardFonts.CourierBoldOblique;
                if (bold) return StandardFonts.CourierBold;
                if (italic) return StandardFonts.CourierOblique;
                return StandardFonts.Courier;
            }

            // Default to Helvetica family (Arial, Calibri, sans-serif, etc.)
            if (bold && italic) return StandardFonts.HelveticaBoldOblique;
            if (bold) return StandardFonts.HelveticaBold;
            if (italic) return StandardFonts.HelveticaOblique;
            return StandardFonts.Helvetica;
        }

        // ------------------------------------------------------------------
        //  Table borders
        // ------------------------------------------------------------------

        // A resolved paragraph tab stop: position (points from text start), alignment
        // (0 left, 1 center, 2 right, 3 decimal), and leader char ('\0' = none).
        internal sealed class TabInfo { public double PosPt; public int Align; public char Leader; }

        /// <summary>
        /// The resolved stops of a paragraph with no tabs of its own, by its style id — what its style chain
        /// gives, shared and read only, so a paragraph of a style does not collect the chain's stops again.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<string, (System.Collections.Generic.List<TabInfo>, double)> _styleTabs =
            new System.Collections.Generic.Dictionary<string, (System.Collections.Generic.List<TabInfo>, double)>(StringComparer.Ordinal);

        /// <remarks>
        /// The list returned is read only: a paragraph with no tabs of its own shares its style's
        /// (<see cref="_styleTabs"/>).
        /// </remarks>
        internal (System.Collections.Generic.List<TabInfo> stops, double defaultPt) ResolveTabs(Paragraph paragraph)
        {
            string styleKey = null;
            if (PropertiesOf(paragraph)?.Tabs == null)
            {
                styleKey = PropertiesOf(paragraph)?.ParagraphStyleId?.Val?.Value ?? "";
                if (_styleTabs.TryGetValue(styleKey, out var known)) return known;
            }
            var resolved = ResolveTabsUncached(paragraph);
            if (styleKey != null) _styleTabs[styleKey] = resolved;
            return resolved;
        }

        private (System.Collections.Generic.List<TabInfo> stops, double defaultPt) ResolveTabsUncached(Paragraph paragraph)
        {
            var list = new System.Collections.Generic.List<TabInfo>();

            // w:val="clear" REMOVES the inherited stop at that position (ECMA-376 §17.3.1.37); it
            // is not merely a stop to skip. Skipping it would leave the style's stop in the list, and
            // being the lower one it would be the first ResolveTabStop finds — so a paragraph that
            // clears the style's 0.5" stop and asks for a right stop at 6.25" would still stop at
            // half an inch. Cleared positions are collected from the direct tabs before anything
            // is gathered, because they have to be able to veto a tier that is read later.
            //
            // Positions are compared in twips, the unit the file states them in: two stops within the
            // hundredth of a point the comparison allowed are the same whole number of twips (a twip is
            // a twentieth of a point), so a set answers what a scan of every earlier stop would — which
            // would be quadratic in a style's stops.
            var cleared = new System.Collections.Generic.HashSet<int>();
            foreach (var t in PropertiesOf(paragraph)?.Tabs?.Elements<TabStop>()
                              ?? System.Linq.Enumerable.Empty<TabStop>())
                if (t.Val?.Value == TabStopValues.Clear && t.Position?.Value != null)
                    cleared.Add(t.Position.Value);

            // Positions already taken by a more specific tier. The direct tabs are collected first,
            // so keeping the first occurrence is keeping the most specific one — and de-duplicating
            // here rather than after the sort matters, because List.Sort is not stable.
            var taken = new System.Collections.Generic.HashSet<int>();

            void Collect(Tabs tabs)
            {
                if (tabs == null) return;
                foreach (var t in tabs.Elements<TabStop>())
                {
                    var val = t.Val?.Value;
                    if (val == TabStopValues.Clear || val == TabStopValues.Bar) continue;
                    if (t.Position?.Value == null) continue;
                    int twips = t.Position.Value;
                    if (cleared.Contains(twips) || !taken.Add(twips)) continue;
                    double posPt = PdfUnits.TwipsToPoints(twips);
                    int align = val == TabStopValues.Right ? 2 : val == TabStopValues.Center ? 1
                              : val == TabStopValues.Decimal ? 3 : 0;
                    char leader = '\0';
                    var lc = t.Leader?.Value;
                    if (lc == TabStopLeaderCharValues.Dot) leader = '.';
                    else if (lc == TabStopLeaderCharValues.Hyphen) leader = '-';
                    else if (lc == TabStopLeaderCharValues.Underscore) leader = '_';
                    else if (lc == TabStopLeaderCharValues.MiddleDot) leader = '·';
                    list.Add(new TabInfo { PosPt = posPt, Align = align, Leader = leader });
                }
            }
            Collect(PropertiesOf(paragraph)?.Tabs);
            foreach (var style in ParagraphStyleChain(paragraph))
                Collect(style.StyleParagraphProperties?.Tabs);
            list.Sort((a, b) => a.PosPt.CompareTo(b.PosPt));

            double def = DefaultTabIntervalPt;
            var ds = _wordDoc.MainDocumentPart?.DocumentSettingsPart?.Settings?.GetFirstChild<DefaultTabStop>()?.Val?.Value;
            if (ds != null && ds.Value > 0) def = PdfUnits.TwipsToPoints(ds.Value);
            return (list, def);
        }

        /// <summary>Word's default tab interval when w:defaultTabStop is absent — half an inch.</summary>
        internal static readonly double DefaultTabIntervalPt = PdfUnits.InchesToPoints(0.5);

        internal enum BorderLineStyle { Solid, Dashed, Dotted, Double }

        internal struct BorderSide
        {
            public bool Defined;   // a tblBorders/tcBorders entry exists for this side
            public bool Visible;   // entry is not "nil"/"none" and has a positive width
            public double WidthPts;
            public string HexColor; // null = black/auto
            public BorderLineStyle Style;
        }

        // The cell's background shading fill (w:shd @fill), or null when none/auto.
        internal string ResolveCellShading(TableCell cell)
        {
            var fill = cell?.TableCellProperties?.Shading?.Fill?.Value;
            return !string.IsNullOrEmpty(fill) && fill != "auto" ? fill : null;
        }

        // ------------------------------------------------------------------
        //  Table-style conditional shading (banding / header / total / corners)
        //  ISO/IEC 29500 §17.7.6 — a table style's <w:tblStylePr> entries shade
        //  rows / columns / corner cells; which ones apply to a given cell is
        //  gated by <w:tblLook> and the row/column band sizes. Direct cell
        //  <w:shd> (ResolveCellShading) still takes precedence over these.
        // ------------------------------------------------------------------

        private readonly struct TblLook
        {
            public TblLook(bool fr, bool lr, bool fc, bool lc, bool hb, bool vb)
            { FirstRow = fr; LastRow = lr; FirstCol = fc; LastCol = lc; HBand = hb; VBand = vb; }
            public readonly bool FirstRow, LastRow, FirstCol, LastCol, HBand, VBand;
        }

        /// <summary>Cell shading + run overrides a table style contributes to one cell via its
        /// conditional formatting. Any field may be unset (null shading / null Bold / null colour).</summary>
        internal struct TableStyleCellFormat
        {
            public string ShadingHex;  // background fill, null = none
            public bool? Bold;         // conditional bold, null = unspecified
            public string ColorHex;    // conditional text colour, null = unspecified
        }

        /// <summary>
        /// Formatting contributed by the table's style (its conditional <c>tblStylePr</c>
        /// entries) for the cell at (<paramref name="rowIdx"/>, <paramref name="colIdx"/>):
        /// cell shading plus the conditional run bold / text colour. Empty when the table
        /// has no style or the style formats nothing at this position.
        /// </summary>
        internal TableStyleCellFormat ResolveTableStyleFormat(Table table,
            int rowIdx, int rowCount, int colIdx, int span, int colCount)
        {
            var fmt = default(TableStyleCellFormat);
            var tblPr = TablePropertiesOf(table);
            string styleId = tblPr?.TableStyle?.Val?.Value;
            if (styleId == null) return fmt;

            var conditionals = ConditionalsFor(styleId);
            if (conditionals.ByType.Count == 0) return fmt;

            var look = ReadTblLook(tblPr);

            // Least-specific → most-specific: a later (more specific) override wins.
            foreach (var type in ApplicableConditionalTypes(
                         look, rowIdx, rowCount, colIdx, span, colCount, conditionals.RowBand, conditionals.ColBand))
            {
                if (!conditionals.ByType.TryGetValue(type, out var tsp)) continue;

                var fill = tsp.TableStyleConditionalFormattingTableCellProperties?.Shading?.Fill?.Value;
                if (!string.IsNullOrEmpty(fill) && fill != "auto") fmt.ShadingHex = fill;

                // The <w:rPr> inside <w:tblStylePr> — located by element name so we don't depend
                // on the SDK's exact CLR class for it; its children are read generically.
                var rprEl = tsp.ChildElements.FirstOrDefault(e => e.LocalName == "rPr");
                if (rprEl != null)
                {
                    var boldEl = rprEl.GetFirstChild<Bold>();
                    if (boldEl != null) fmt.Bold = boldEl.Val == null || boldEl.Val.Value;
                    var col = ColorHex(rprEl.GetFirstChild<Color>());
                    if (col != null && col != "auto") fmt.ColorHex = col;
                }
            }
            return fmt;
        }

        /// <summary>
        /// What a table style's chain says about conditional formatting, read once for the style:
        /// its band sizes, and for each conditional type the first <c>tblStylePr</c> of that type in
        /// the chain — the one a cell takes.
        /// </summary>
        private sealed class TableStyleConditionals
        {
            public int RowBand = 1, ColBand = 1;
            public readonly Dictionary<TableStyleOverrideValues, TableStyleProperties> ByType =
                new Dictionary<TableStyleOverrideValues, TableStyleProperties>();
        }

        private readonly Dictionary<string, TableStyleConditionals> _tableStyleConditionals =
            new Dictionary<string, TableStyleConditionals>(StringComparer.Ordinal);

        /// <summary>
        /// <c>tblStylePr</c> entries read while building <see cref="TableStyleConditionals"/> on this
        /// thread — the evidence each style's entries are read once, not once a cell.
        /// </summary>
        [ThreadStatic] internal static long ConditionalEntriesRead;

        /// <summary>
        /// The table style's conditionals, read once per style id and conversion.
        /// </summary>
        /// <remarks>
        /// Each cell rebuilt the style chain and, for each conditional type that applied to it,
        /// scanned every <c>tblStylePr</c> of every style in the chain — in both the shading and
        /// the border resolver. A style of 100 000 entries a cell never takes, under a 100 x 100
        /// table, would be some 4x10^9 visits.
        /// </remarks>
        private TableStyleConditionals ConditionalsFor(string styleId)
        {
            if (_tableStyleConditionals.TryGetValue(styleId, out var cached)) return cached;

            var chain = StyleChain(styleId, new HashSet<string>()).ToList();
            var result = new TableStyleConditionals
            {
                RowBand = Math.Max(1, ResolveBandSize(chain, horizontal: true)),
                ColBand = Math.Max(1, ResolveBandSize(chain, horizontal: false)),
            };
            foreach (var s in chain)
                foreach (var tsp in s.Elements<TableStyleProperties>())
                {
                    ConditionalEntriesRead++;
                    if (tsp.Type != null && !result.ByType.ContainsKey(tsp.Type.Value))
                        result.ByType[tsp.Type.Value] = tsp;
                }
            _tableStyleConditionals[styleId] = result;
            return result;
        }

        private static TblLook ReadTblLook(TableProperties tblPr)
        {
            var look = tblPr?.TableLook;
            // Word's de-facto default for a styled table with no explicit tblLook:
            // header row + first column + horizontal banding, no vertical banding.
            if (look == null) return new TblLook(true, false, true, false, true, false);

            bool? fr = look.FirstRow?.Value, lr = look.LastRow?.Value,
                  fc = look.FirstColumn?.Value, lc = look.LastColumn?.Value,
                  nh = look.NoHorizontalBand?.Value, nv = look.NoVerticalBand?.Value;

            // Legacy form: a single hex bitmask in @w:val (ECMA-376 §17.4.56).
            if (fr == null && lr == null && fc == null && lc == null && nh == null && nv == null
                && look.Val?.Value != null
                && int.TryParse(look.Val.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int bits))
            {
                return new TblLook(
                    (bits & 0x0020) != 0, (bits & 0x0040) != 0,
                    (bits & 0x0080) != 0, (bits & 0x0100) != 0,
                    (bits & 0x0200) == 0, (bits & 0x0400) == 0); // noHBand / noVBand are inverted
            }
            return new TblLook(fr ?? false, lr ?? false, fc ?? false, lc ?? false, !(nh ?? false), !(nv ?? false));
        }

        private static int ResolveBandSize(List<Style> chain, bool horizontal)
        {
            foreach (var s in chain)
            {
                var tp = s.StyleTableProperties;
                if (tp == null) continue;
                int? v = horizontal
                    ? tp.GetFirstChild<TableStyleRowBandSize>()?.Val?.Value
                    : tp.GetFirstChild<TableStyleColumnBandSize>()?.Val?.Value;
                if (v.HasValue && v.Value > 0) return v.Value;
            }
            return 1;
        }

        private static IEnumerable<TableStyleOverrideValues> ApplicableConditionalTypes(
            TblLook look, int rowIdx, int rowCount, int colIdx, int span, int colCount,
            int rowBand, int colBand)
        {
            var list = new List<TableStyleOverrideValues> { TableStyleOverrideValues.WholeTable };

            bool isFirstRow = look.FirstRow && rowIdx == 0;
            bool isLastRow  = look.LastRow  && rowIdx == rowCount - 1;
            bool isFirstCol = look.FirstCol && colIdx == 0;
            bool isLastCol  = look.LastCol  && (colIdx + span - 1) == colCount - 1;

            // Column bands skip the (separately formatted) first/last columns.
            if (look.VBand && !isFirstCol && !isLastCol)
            {
                int eff = colIdx - (look.FirstCol ? 1 : 0);
                if (eff >= 0)
                    list.Add((eff / colBand) % 2 == 1 ? TableStyleOverrideValues.Band2Vertical
                                                      : TableStyleOverrideValues.Band1Vertical);
            }
            // Row bands skip the (separately formatted) header/total rows.
            if (look.HBand && !isFirstRow && !isLastRow)
            {
                int eff = rowIdx - (look.FirstRow ? 1 : 0);
                if (eff >= 0)
                    list.Add((eff / rowBand) % 2 == 1 ? TableStyleOverrideValues.Band2Horizontal
                                                      : TableStyleOverrideValues.Band1Horizontal);
            }
            if (isFirstCol) list.Add(TableStyleOverrideValues.FirstColumn);
            if (isLastCol)  list.Add(TableStyleOverrideValues.LastColumn);
            if (isFirstRow) list.Add(TableStyleOverrideValues.FirstRow);
            if (isLastRow)  list.Add(TableStyleOverrideValues.LastRow);
            if (isFirstRow && isFirstCol) list.Add(TableStyleOverrideValues.NorthWestCell);
            if (isFirstRow && isLastCol)  list.Add(TableStyleOverrideValues.NorthEastCell);
            if (isLastRow  && isFirstCol) list.Add(TableStyleOverrideValues.SouthWestCell);
            if (isLastRow  && isLastCol)  list.Add(TableStyleOverrideValues.SouthEastCell);
            return list;
        }

        internal struct CellBorders
        {
            public BorderSide Top, Bottom, Left, Right;
            public bool AnyDefined;
        }

        // Resolve the four borders for a cell at (rowIdx, colIdx) spanning `span`
        // columns in a rowCount×colCount table. Each edge selects its source by
        // position — the table's OUTER border on the table perimeter, the INSIDE
        // border between cells — with precedence cell tcBorders → table tblBorders
        // → table-style tblBorders.
        internal CellBorders ResolveCellBorders(TableCell cell, Table table,
            int rowIdx, int rowCount, int colIdx, int span, int colCount)
        {
            var tc = cell?.TableCellProperties?.TableCellBorders;
            var tb = TablePropertiesOf(table)?.TableBorders;

            TableBorders styleTb = null;
            var tblStyleId = TablePropertiesOf(table)?.TableStyle?.Val?.Value;
            foreach (var style in StyleChain(tblStyleId, new HashSet<string>()))
            {
                if (style.StyleTableProperties?.TableBorders != null)
                {
                    styleTb = style.StyleTableProperties.TableBorders;
                    break;
                }
            }

            // Table-style conditional borders (header/total row, banded rows/cols, corners):
            // per-cell tcBorders and the region's tblBorders from the applicable tblStylePr.
            var (cTop, cBot, cLeft, cRight, condTbl) =
                ResolveConditionalBorders(table, rowIdx, rowCount, colIdx, span, colCount);

            // Per edge, in precedence order: direct cell tcBorders → direct table tblBorders →
            // conditional-style cell tcBorders → conditional-style table tblBorders → base
            // table-style tblBorders. (Direct formatting always beats style formatting.)
            BorderType Pick(BorderType tcEdge, BorderType tbOuter, BorderType tbInside,
                            BorderType condTcEdge, BorderType condOuter, BorderType condInside,
                            BorderType styleOuter, BorderType styleInside, bool perimeter)
            {
                if (tcEdge != null) return tcEdge;
                var tbEdge = perimeter ? tbOuter : tbInside;
                if (tbEdge != null) return tbEdge;
                if (condTcEdge != null) return condTcEdge;
                var condEdge = perimeter ? condOuter : condInside;
                if (condEdge != null) return condEdge;
                return perimeter ? styleOuter : styleInside;
            }

            bool topPerim = rowIdx == 0;
            bool botPerim = rowIdx == rowCount - 1;
            bool leftPerim = colIdx == 0;
            bool rightPerim = colIdx + span - 1 >= colCount - 1;

            var top = Pick(tc?.TopBorder, tb?.TopBorder, tb?.InsideHorizontalBorder,
                           cTop, condTbl?.TopBorder, condTbl?.InsideHorizontalBorder,
                           styleTb?.TopBorder, styleTb?.InsideHorizontalBorder, topPerim);
            var bottom = Pick(tc?.BottomBorder, tb?.BottomBorder, tb?.InsideHorizontalBorder,
                              cBot, condTbl?.BottomBorder, condTbl?.InsideHorizontalBorder,
                              styleTb?.BottomBorder, styleTb?.InsideHorizontalBorder, botPerim);
            var left = Pick(tc?.LeftBorder, tb?.LeftBorder, tb?.InsideVerticalBorder,
                            cLeft, condTbl?.LeftBorder, condTbl?.InsideVerticalBorder,
                            styleTb?.LeftBorder, styleTb?.InsideVerticalBorder, leftPerim);
            var right = Pick(tc?.RightBorder, tb?.RightBorder, tb?.InsideVerticalBorder,
                             cRight, condTbl?.RightBorder, condTbl?.InsideVerticalBorder,
                             styleTb?.RightBorder, styleTb?.InsideVerticalBorder, rightPerim);

            var result = new CellBorders();
            if (top != null) { result.Top = ToSide(top); result.AnyDefined = true; }
            if (bottom != null) { result.Bottom = ToSide(bottom); result.AnyDefined = true; }
            if (left != null) { result.Left = ToSide(left); result.AnyDefined = true; }
            if (right != null) { result.Right = ToSide(right); result.AnyDefined = true; }
            return result;
        }

        // Merges the table style's conditional (tblStylePr) borders that apply to a cell:
        // its per-cell tcBorders edges and the conditional region's tblBorders. Later (more
        // specific) conditional types override earlier ones.
        private (BorderType tcTop, BorderType tcBot, BorderType tcLeft, BorderType tcRight,
                 TableBorders tbl)
            ResolveConditionalBorders(Table table, int rowIdx, int rowCount, int colIdx, int span, int colCount)
        {
            var tblPr = TablePropertiesOf(table);
            string styleId = tblPr?.TableStyle?.Val?.Value;
            if (styleId == null) return (null, null, null, null, null);

            var conditionals = ConditionalsFor(styleId);
            if (conditionals.ByType.Count == 0) return (null, null, null, null, null);

            var look = ReadTblLook(tblPr);

            BorderType tcTop = null, tcBot = null, tcLeft = null, tcRight = null;
            TableBorders tbl = null;
            foreach (var type in ApplicableConditionalTypes(
                         look, rowIdx, rowCount, colIdx, span, colCount, conditionals.RowBand, conditionals.ColBand))
            {
                if (!conditionals.ByType.TryGetValue(type, out var tsp)) continue;

                var tcb = tsp.TableStyleConditionalFormattingTableCellProperties?.TableCellBorders;
                if (tcb != null)
                {
                    if (tcb.TopBorder != null) tcTop = tcb.TopBorder;
                    if (tcb.BottomBorder != null) tcBot = tcb.BottomBorder;
                    if (tcb.LeftBorder != null) tcLeft = tcb.LeftBorder;
                    if (tcb.RightBorder != null) tcRight = tcb.RightBorder;
                }

                var ctbl = tsp.TableStyleConditionalFormattingTableProperties?.TableBorders;
                if (ctbl != null) tbl = ctbl;
            }
            return (tcTop, tcBot, tcLeft, tcRight, tbl);
        }

        private static BorderSide ToSide(BorderType bt)
        {
            var side = new BorderSide { Defined = true };
            var val = bt.Val?.Value;
            bool isNone = val == BorderValues.Nil || val == BorderValues.None;
            // w:sz is in eighths of a point.
            double widthPts = DocxLayoutMetrics.DefaultBorderWidthPt;
            if (bt.Size?.Value != null) widthPts = bt.Size.Value / PdfUnits.BorderEighthsPerPoint;
            side.WidthPts = widthPts;
            side.Visible = !isNone && widthPts > 0;
            var color = bt.Color?.Value;
            side.HexColor = (color != null && color != "auto") ? color : null;
            side.Style = ClassifyBorder(val);
            return side;
        }

        private static BorderLineStyle ClassifyBorder(BorderValues? v)
        {
            if (v == null) return BorderLineStyle.Solid;
            var x = v.Value;
            if (x == BorderValues.Dotted) return BorderLineStyle.Dotted;
            if (x == BorderValues.Dashed || x == BorderValues.DashSmallGap
                || x == BorderValues.DotDash || x == BorderValues.DotDotDash)
                return BorderLineStyle.Dashed;
            if (x == BorderValues.Double || x == BorderValues.Triple
                || x == BorderValues.ThinThickSmallGap || x == BorderValues.ThickThinSmallGap)
                return BorderLineStyle.Double;
            return BorderLineStyle.Solid;
        }

        // ------------------------------------------------------------------
        //  Width measurement (real standard-14 per-glyph advance widths)
        // ------------------------------------------------------------------

        internal double MeasureTextWidth(string text, string pdfFontName, double fontSize)
            => StandardFontMetrics.MeasureText(text, pdfFontName, fontSize);
    }
}
