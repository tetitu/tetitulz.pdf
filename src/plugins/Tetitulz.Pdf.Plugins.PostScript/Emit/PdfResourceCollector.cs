// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// Builds the <c>/Resources</c> dictionary the emitted content stream names.
    ///
    /// <para><b>Every entry is a direct object.</b> The plugin does not own the host document's
    /// object-number space, so fabricating an indirect reference into it would alias whatever
    /// object happened to have that number — the contract's "object identity" rule. The host's
    /// renderer resolves a direct dictionary or stream in place, and the document-import path
    /// promotes them to indirect objects when it serialises.</para>
    /// </summary>
    internal sealed class PdfResourceCollector
    {
        private readonly Dictionary<string, PdfDictionary> _fonts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfStream> _xobjects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _fontResourceByBaseFont = new(StringComparer.Ordinal);
        private int _nextFontId = 1;
        private int _nextXObjectId = 1;

        // Each captured embedded program's number in this collector, the key its resource is cached under.
        private readonly Dictionary<PsEmbeddedFont, int> _embeddedIds = new();

        private string EmbeddedId(PsEmbeddedFont font)
        {
            if (!_embeddedIds.TryGetValue(font, out int id))
            {
                id = _embeddedIds.Count;
                _embeddedIds[font] = id;
            }
            return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Each Type 3 font's glyph count and glyph resources when this collector last built it.
        private readonly Dictionary<PsType3Font, (int Glyphs, PdfDictionary Resources)> _type3Built = new();

        /// <summary>Type 3 font dictionaries built on the calling thread: the evidence that a font is rebuilt only when it grows.</summary>
        [ThreadStatic] internal static long Type3FontBuilds;
        private readonly Dictionary<string, PdfObject> _shadings = new(StringComparer.Ordinal);

        private readonly Dictionary<PdfObject, string> _patternNames = new(PdfReferenceEquality.Instance);
        private readonly Dictionary<string, PdfObject> _patterns = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PdfObject> _colorSpaces = new(StringComparer.Ordinal);

        /// <summary>True when nothing has been registered, so <c>/Resources</c> can be omitted.</summary>
        public bool IsEmpty => _fonts.Count == 0 && _xobjects.Count == 0 && _shadings.Count == 0
                               && _patterns.Count == 0 && _colorSpaces.Count == 0 && _extGStates.Count == 0;

        /// <summary>
        /// Registers a pattern — a tiling pattern's stream, or a shading pattern's dictionary — and
        /// returns its resource name; the same object set again keeps its name.
        /// </summary>
        public string AddPattern(PdfObject pattern)
        {
            if (_patternNames.TryGetValue(pattern, out string known)) return known;
            string resourceName = "P" + (_patterns.Count + 1);
            _patterns[resourceName] = pattern;
            _patternNames[pattern] = resourceName;
            return resourceName;
        }

        private readonly Dictionary<PdfObject, string> _formNames = new(PdfReferenceEquality.Instance);

        /// <summary>A form XObject (<c>execform</c>), registered once however often it is drawn; its resource name.</summary>
        public string AddForm(PdfStream form)
        {
            if (_formNames.TryGetValue(form, out string known)) return known;
            string resourceName = "Fm" + (_formNames.Count + 1);
            _xobjects[resourceName] = form;
            _formNames[form] = resourceName;
            return resourceName;
        }

        /// <summary>
        /// The <c>/ColorSpace</c> resource naming <c>[/Pattern base]</c> for an uncoloured pattern
        /// painted in the device space <paramref name="baseSpace"/>, registered once per space.
        /// </summary>
        public string AddPatternSpace(PdfName baseSpace)
        {
            foreach (var kv in _colorSpaces)
                if (kv.Value is PdfArray a && a.Count == 2 && a[1] is PdfName n && n.Value == baseSpace.Value)
                    return kv.Key;
            string resourceName = "CsP" + (_colorSpaces.Count + 1);
            var space = new PdfArray();
            space.Add(PdfName.Pattern);
            space.Add(baseSpace);
            _colorSpaces[resourceName] = space;
            return resourceName;
        }

        private readonly Dictionary<PdfObject, string> _transferNames = new(PdfReferenceEquality.Instance);
        private readonly Dictionary<string, PdfObject> _extGStates = new(StringComparer.Ordinal);
        private string _identityTransfer;

        /// <summary>
        /// The <c>/ExtGState</c> setting <paramref name="transfer"/> as <c>/TR</c> — <c>/Identity</c> for
        /// null — registered once a function; its resource name. Bounded by
        /// <see cref="PsLimits.MaxTransferFunctions"/>, which caps the functions sampled.
        /// </summary>
        public string AddTransferState(PdfObject transfer)
        {
            if (transfer == null && _identityTransfer != null) return _identityTransfer;
            if (transfer != null && _transferNames.TryGetValue(transfer, out string known)) return known;
            var state = new PdfDictionary();
            state[PdfName.Type] = PdfName.ExtGState;
            state[PdfName.TR] = transfer ?? PdfName.Identity;
            string resourceName = "GS" + (_extGStates.Count + 1);
            _extGStates[resourceName] = state;
            if (transfer == null) _identityTransfer = resourceName; else _transferNames[transfer] = resourceName;
            return resourceName;
        }

        private readonly Dictionary<string, string> _deviceStateNames = new(StringComparer.Ordinal);
        private readonly Dictionary<PdfObject, int> _deviceFunctionIds = new(PdfReferenceEquality.Instance);
        private bool _deviceStateCapReported;

        /// <summary>
        /// The <c>/ExtGState</c> taking a stream from <paramref name="from"/> to <paramref name="to"/>:
        /// <c>/OP</c> and <c>/op</c> for overprint, <c>/BG</c> and <c>/UCR</c> for a sampled function
        /// (<c>/BG2</c> and <c>/UCR2</c> <c>/Default</c> back to the device's own), <c>/FL</c>,
        /// <c>/SM</c> and <c>/SA</c> — only the entries that differ — registered once a change; its
        /// resource name, or null past <see cref="PsLimits.MaxDeviceParameterStates"/>, where the
        /// change is not written, said once.
        /// </summary>
        public string AddDeviceState(Graphics.PsDeviceParameters from, Graphics.PsDeviceParameters to)
        {
            var state = new PdfDictionary();
            var key = new System.Text.StringBuilder();
            if (from.Overprint != to.Overprint)
            {
                state[PdfName.OP] = to.Overprint ? PdfBoolean.True : PdfBoolean.False;
                state[PdfName.op] = to.Overprint ? PdfBoolean.True : PdfBoolean.False;
                key.Append("OP=").Append(to.Overprint).Append(';');
            }
            if (!ReferenceEquals(from.BlackGeneration, to.BlackGeneration))
            {
                if (to.BlackGeneration != null) state[PdfName.BG] = to.BlackGeneration;
                else state[PdfName.BG2] = PdfName.Default;
                key.Append("BG=").Append(FunctionId(to.BlackGeneration)).Append(';');
            }
            if (!ReferenceEquals(from.UnderColorRemoval, to.UnderColorRemoval))
            {
                if (to.UnderColorRemoval != null) state[PdfName.UCR] = to.UnderColorRemoval;
                else state[PdfName.UCR2] = PdfName.Default;
                key.Append("UCR=").Append(FunctionId(to.UnderColorRemoval)).Append(';');
            }
            if (from.Flatness != to.Flatness)
            {
                state[PdfName.FL] = new PdfNumber(to.Flatness);
                key.Append("FL=").Append(to.Flatness.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            if (from.Smoothness != to.Smoothness)
            {
                state[PdfName.SM] = new PdfNumber(to.Smoothness);
                key.Append("SM=").Append(to.Smoothness.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            if (from.StrokeAdjust != to.StrokeAdjust)
            {
                state[PdfName.SA] = to.StrokeAdjust ? PdfBoolean.True : PdfBoolean.False;
                key.Append("SA=").Append(to.StrokeAdjust).Append(';');
            }

            if (_deviceStateNames.TryGetValue(key.ToString(), out string known)) return known;
            if (_deviceStateNames.Count >= PsLimits.MaxDeviceParameterStates)
            {
                if (!_deviceStateCapReported)
                {
                    _deviceStateCapReported = true;
                    PostScriptDiagnostics.Report("PdfResourceCollector",
                        $"the {PsLimits.MaxDeviceParameterStates}-graphics-state cap for overprint, black generation, " +
                        "flatness and the other device parameters was reached; later changes were not written. Reported once.");
                }
                return null;
            }

            state[PdfName.Type] = PdfName.ExtGState;
            string resourceName = "GS" + (_extGStates.Count + 1);
            _extGStates[resourceName] = state;
            _deviceStateNames[key.ToString()] = resourceName;
            return resourceName;
        }

        /// <summary>A sampled function's number in this collector, 0 for none: two keys naming one function are one state.</summary>
        private int FunctionId(PdfObject function)
        {
            if (function == null) return 0;
            if (!_deviceFunctionIds.TryGetValue(function, out int id))
            {
                id = _deviceFunctionIds.Count + 1;
                _deviceFunctionIds[function] = id;
            }
            return id;
        }

        private readonly Dictionary<PdfObject, string> _colorSpaceNames = new(PdfReferenceEquality.Instance);
        private bool _colorSpaceCapReported;

        /// <summary>
        /// A colour space a colour is painted in (<c>cs</c>) — an ICC, Cal or Lab space — registered
        /// once however often it is set; its resource name, or null past
        /// <see cref="PsLimits.MaxColorSpaceResources"/>, where the colour is painted in its device
        /// approximation, said once.
        /// </summary>
        public string AddColorSpace(PdfObject space)
        {
            if (_colorSpaceNames.TryGetValue(space, out string known)) return known;
            if (_colorSpaceNames.Count >= PsLimits.MaxColorSpaceResources)
            {
                if (!_colorSpaceCapReported)
                {
                    _colorSpaceCapReported = true;
                    PostScriptDiagnostics.Report("PdfResourceCollector",
                        $"the {PsLimits.MaxColorSpaceResources}-colour-space cap was reached; later colours in " +
                        "spaces not yet named are painted in their device approximation. Reported once.");
                }
                return null;
            }
            string resourceName = "Cs" + (_colorSpaces.Count + 1);
            _colorSpaces[resourceName] = space;
            _colorSpaceNames[space] = resourceName;
            return resourceName;
        }

        /// <summary>Registers a shading — a dictionary, or a stream for a mesh — and returns its resource name.</summary>
        public string AddShading(PdfObject shading)
        {
            string resourceName = "Sh" + (_shadings.Count + 1);
            _shadings[resourceName] = shading;
            return resourceName;
        }

        /// <summary>
        /// The name to reuse once <see cref="PsLimits.MaxFontResources"/> is reached, or null while
        /// there is still room.
        ///
        /// <para>De-duplication by base font plus encoding bounds the <i>repeats</i>, not the
        /// distinct keys — and a program can generate unlimited distinct encodings, each retaining a
        /// font dictionary, an encoding dictionary, a differences array and their names. Measured
        /// without this cap: 172 bytes of PostScript retain ~226 MB, 3.5x the whole
        /// <c>MaxCompositeAllocationBytes</c> ceiling, none of it charged to that budget.</para>
        ///
        /// <para>Past the cap the most recent resource is <b>reused</b> rather than refused, so the
        /// text still draws — in the wrong encoding — instead of vanishing. Reported once.</para>
        /// </summary>
        private string CappedResourceName()
        {
            if (_fonts.Count < PsLimits.MaxFontResources) return null;

            if (!_fontCapReported)
            {
                _fontCapReported = true;
                PostScriptDiagnostics.Report("PdfResourceCollector",
                    $"the {PsLimits.MaxFontResources}-font cap was reached; later fonts reuse the " +
                    "last resource, so their text draws in the wrong encoding rather than not at " +
                    "all. Reported once per conversion.");
            }

            return _lastFontResourceName ?? "F1";
        }

        private bool _fontCapReported;
        private string _lastFontResourceName;

        /// <summary>Mints the next font resource name, or null when the cap refuses one.</summary>
        private string MintFontName()
        {
            string capped = CappedResourceName();
            if (capped != null) return null;

            _lastFontResourceName = "F" + _nextFontId++;
            return _lastFontResourceName;
        }

        /// <summary>How many image XObjects have been registered, for the per-conversion cap.</summary>
        public int ImageCount => _xobjects.Count;

        /// <summary>
        /// The resource name for a standard-14 base font, registering it on first use.
        ///
        /// <para>De-duplicated by base font: a document that sets Helvetica two hundred times gets
        /// one <c>/F1</c>, not two hundred entries.</para>
        /// </summary>
        public string GetOrAddFont(string baseFont) => GetOrAddFont(baseFont, null);

        /// <summary>
        /// Registers a Type 3 font built from the program's own glyph procedures (Table 112).
        ///
        /// <para><b>Its <c>/Encoding</c> carries <c>/Differences</c> and no <c>/BaseEncoding</c></b>,
        /// unlike every other font here. Both renderers fall back to the base encoding for a code
        /// the Differences omits, so declaring WinAnsi would make such a code resolve to a WinAnsi
        /// glyph name and draw a notdef box instead of nothing.</para>
        ///
        /// <para><b><c>/Widths</c> are in glyph space</b> — the raw <c>wx</c> each procedure
        /// declared, not thousandths. Both renderers multiply by <c>/FontMatrix</c>[0] themselves.</para>
        /// </summary>
        /// <param name="font">The recognised Type 3 font.</param>
        /// <param name="glyphs">Built glyphs by character code.</param>
        /// <param name="glyphResources">
        /// Resources the glyph procedures named, or null. It must be the glyphs' OWN dictionary:
        /// this collector's own <see cref="Build"/> output would be a cycle.
        /// </param>
        public string AddType3Font(PsType3Font font, IReadOnlyDictionary<int, PsBuiltGlyph> glyphs,
                                   PdfDictionary glyphResources)
        {
            // ONE resource per font, rebuilt in place as more glyphs are built — not one per
            // `show`. Minting each time was measurably wrong: three `show`s in one font produced
            // /F1 /F2 /F3, each carrying only the glyphs of its own string, and 256 of them
            // exhausted MaxFontResources so every later font reused the capped name. The three
            // sibling registrars here each memoise; this one was written without it.
            string resourceName = font.ResourceName;
            if (resourceName == null)
            {
                resourceName = MintFontName();
                if (resourceName == null) return CappedResourceName();
                font.ResourceName = resourceName;
            }

            // Rebuilt only when a glyph was added or the glyphs' resources changed since this
            // collector last built it. Every show, glyphshow and charpath in the font called this,
            // and each rebuilt the whole font — every CharProc stream, the differences and their
            // sort, the widths — so a page of N shows over G glyphs was N × G.
            if (_type3Built.TryGetValue(font, out var last) && last.Glyphs == glyphs.Count
                && ReferenceEquals(last.Resources, glyphResources) && _fonts.ContainsKey(resourceName))
                return resourceName;
            _type3Built[font] = (glyphs.Count, glyphResources);
            Type3FontBuilds++;

            int firstChar = int.MaxValue, lastChar = int.MinValue;
            foreach (int code in glyphs.Keys)
            {
                if (code < firstChar) firstChar = code;
                if (code > lastChar)  lastChar  = code;
            }
            if (firstChar > lastChar) { firstChar = 0; lastChar = 0; }

            var charProcs = new PdfDictionary();
            var differences = new Dictionary<int, string>();

            foreach (var pair in glyphs)
            {
                string glyphName = font.GlyphNameFor(pair.Key);
                differences[pair.Key] = glyphName;

                var stream = new PdfStream();
                stream.RawData = pair.Value.Content;
                charProcs[new PdfName(glyphName)] = stream;
            }

            var widths = new PdfArray();
            for (int code = firstChar; code <= lastChar; code++)
                widths.Add(new PdfNumber(glyphs.TryGetValue(code, out var g) ? g.Wx : 0));

            var pdfFont = new PdfDictionary();
            pdfFont[PdfName.Type]       = PdfName.Font;
            pdfFont[PdfName.Subtype]    = PdfName.Type3;
            pdfFont[PdfName.FontMatrix] = font.BuildPdfFontMatrix();
            pdfFont[PdfName.FontBBox]   = font.BuildPdfFontBBox();
            pdfFont[PdfName.CharProcs]  = charProcs;
            pdfFont[PdfName.Encoding]   = BuildDifferencesOnlyEncoding(differences);
            pdfFont[PdfName.FirstChar]  = new PdfNumber(firstChar);
            pdfFont[PdfName.LastChar]   = new PdfNumber(lastChar);
            pdfFont[PdfName.Widths]     = widths;

            // §9.6.5 makes /Resources optional, and null is the right value when the glyphs named
            // nothing — an empty dictionary would be noise in every emitted font.
            if (glyphResources != null) pdfFont[PdfName.Resources] = glyphResources;

            // Deliberately NO /BaseFont: a Type 3 font has no base face, and writing one is what
            // made the substitution silent for as long as it was.
            _fonts[resourceName] = pdfFont;
            return resourceName;
        }

        /// <summary>
        /// A <c>/Differences</c> encoding with no <c>/BaseEncoding</c> — see
        /// <see cref="AddType3Font"/> for why the difference matters.
        /// </summary>
        private static PdfDictionary BuildDifferencesOnlyEncoding(
            IReadOnlyDictionary<int, string> differences)
        {
            var encoding = new PdfDictionary();
            encoding[PdfName.Type] = PdfName.Encoding;
            encoding[PdfName.Differences] = BuildDifferencesArray(differences);
            return encoding;
        }

        /// <summary>
        /// The resource name for a standard-14 base font under an optional custom encoding.
        ///
        /// <para><paramref name="differences"/> maps character code to Adobe glyph name, as a
        /// PostScript <c>/Encoding</c> array does. When present it is emitted as a PDF
        /// <c>/Differences</c> encoding, which is what makes a re-encoded font — the way every
        /// legacy Greek, Hebrew, Cyrillic or Central-European document selects its glyphs — draw the
        /// characters the program asked for instead of the WinAnsi ones at those code points.</para>
        ///
        /// <para>The cache key includes the encoding: one base font under two encodings is two PDF
        /// fonts, and collapsing them would silently give the second one the first one's glyphs.</para>
        ///
        /// <para><b>The key is rebuilt on every call, cache hits included, and that is left alone
        /// deliberately.</b> <c>EncodingKey</c> sorts the differences dictionary's keys and
        /// concatenates every <c>code=glyph;</c> pair, so a page issuing a few thousand
        /// <c>show</c> calls through a 256-entry re-encoded font rebuilds a ~3 KB string per call
        /// and throws it away after the lookup hits. A reference-identity memo would make the
        /// repeat cost zero — <c>PsTextOps.EncodingDifferences</c> stores one instance on
        /// <c>i.CurrentFontEncoding</c> per <c>setfont</c> — but this repo's rule is measure before
        /// you rank, and the cost may well be noise beside the interpreter's own per-operator work.
        /// It is recorded here rather than guessed at; a benchmark comes first.</para>
        /// </summary>
        public string GetOrAddFont(string baseFont, IReadOnlyDictionary<int, string> differences)
        {
            string key = differences == null || differences.Count == 0
                ? baseFont
                : baseFont + "\0" + EncodingKey(differences);

            if (_fontResourceByBaseFont.TryGetValue(key, out string existing)) return existing;

            string resourceName = MintFontName();
            if (resourceName == null) return CappedResourceName();

            var font = new PdfDictionary();
            font[PdfName.Type]     = PdfName.Font;
            font[PdfName.Subtype]  = PdfName.Type1;
            font[PdfName.BaseFont] = new PdfName(baseFont);

            // Symbol and ZapfDingbats draw through their built-in encoding whatever /Encoding says
            // (§9.6.6.4), and PsFontMapper measures them through it, so the dictionary declares no
            // encoding for them — a WinAnsi would misdescribe the font to a strict reader — and a /Differences for them sits on that built-in base, not on WinAnsi.
            bool builtIn = Standard14Metrics.HasBuiltInEncoding(baseFont);

            if (differences == null || differences.Count == 0)
            {
                // WinAnsiEncoding, and the shown bytes are passed through UNCHANGED — there is no
                // re-encoding step anywhere in this plugin, and a comment here once claimed there
                // was. PostScript's own default is StandardEncoding, which disagrees with WinAnsi
                // above 0x7F and at 0x27 / 0x60, so this is a deliberate normalisation rather than a
                // faithful mapping: PsFontMapper.MeasureEmWidth measures in the same WinAnsi so the
                // metrics and the declaration always agree, and PsFontMapper.CountUnrepresentable
                // reports any byte the result cannot draw. A document that cares re-encodes with
                // definefont, which takes the /Differences branch below instead.
                if (!builtIn) font[PdfName.Encoding] = PdfName.WinAnsiEncoding;
            }
            else
            {
                font[PdfName.Encoding] = builtIn
                    ? BuildDifferencesOnlyEncoding(differences)
                    : BuildDifferencesEncoding(differences);
            }

            _fonts[resourceName] = font;
            _fontResourceByBaseFont[key] = resourceName;
            return resourceName;
        }

        /// <summary>
        /// A <c>/Differences</c> encoding dictionary.
        ///
        /// <para>The array is run-length encoded the way the format expects — a starting code
        /// followed by consecutive glyph names — because a reader is entitled to a compact array and
        /// emitting one code per name would triple its size for a full 256-entry re-encoding, which
        /// is exactly the common case.</para>
        /// </summary>
        private static PdfDictionary BuildDifferencesEncoding(IReadOnlyDictionary<int, string> differences)
        {
            var encoding = new PdfDictionary();
            encoding[PdfName.Type]         = PdfName.Encoding;
            encoding[PdfName.BaseEncoding] = PdfName.WinAnsiEncoding;
            encoding[PdfName.Differences]  = BuildDifferencesArray(differences);
            return encoding;
        }

        /// <summary>
        /// The run-length-encoded <c>/Differences</c> array: one starting number per run of
        /// consecutive codes, then the names. Shared by the WinAnsi-based encoding above and the
        /// Differences-only one a Type 3 font needs, so the two cannot drift on the encoding rule.
        /// </summary>
        private static PdfArray BuildDifferencesArray(IReadOnlyDictionary<int, string> differences)
        {
            var codes = new List<int>(differences.Keys);
            codes.Sort();

            var array = new PdfArray();
            int previous = int.MinValue;
            foreach (int code in codes)
            {
                if (code != previous + 1) array.Add(new PdfNumber(code));
                array.Add(new PdfName(differences[code]));
                previous = code;
            }
            return array;
        }

        private static string EncodingKey(IReadOnlyDictionary<int, string> differences)
        {
            var codes = new List<int>(differences.Keys);
            codes.Sort();

            var sb = new System.Text.StringBuilder();
            foreach (int code in codes) sb.Append(code).Append('=').Append(differences[code]).Append(';');
            return sb.ToString();
        }

        /// <summary>
        /// The resource name for a CJK composite font: a PDF <b>Type0</b> font naming the same
        /// Adobe predefined CMap the PostScript used — or embedding the CMap the program defined,
        /// <paramref name="defined"/>, whose collection the descendant then declares.
        ///
        /// <para>Naming the CMap rather than embedding a decoding table is what makes this work at
        /// all — the reader applies the identical CMap the PostScript did, so the shown bytes need
        /// no translation on the way through, and no CJK font data has to be carried.</para>
        ///
        /// <para><b>The descendant declares <c>/DW 1000</c> and no <c>/W</c> array.</b> Per-CID
        /// widths would have to come from the font program, which is out of scope; 1000 is the
        /// PDF default and the correct em width for the full-width glyphs that dominate CJK text.
        /// Proportional Latin runs inside a CJK font will be spaced as full-width — reported by the
        /// caller, not hidden.</para>
        /// </summary>
        public string GetOrAddCidFont(string face, string cmapName, PsCMap defined = null)
        {
            string key = "\u0001cid\u0001" + face + "\u0001" + cmapName + CMapIdentity(defined);
            if (_fontResourceByBaseFont.TryGetValue(key, out string existing)) return existing;

            string resourceName = MintFontName();
            if (resourceName == null) return CappedResourceName();
            var (registry, ordering, supplement) = defined != null
                ? (defined.Registry, defined.Ordering, defined.Supplement)
                : PsCidFonts.CollectionFor(cmapName);

            var descendant = CompositeFontDictionaries.DescendantCidFont(PdfName.CIDFontType0, face,
                CompositeFontDictionaries.CidSystemInfo(registry, ordering, supplement), CidFontDescriptor(face));
            descendant[PdfName.DW] = new PdfNumber(1000);

            var font = CompositeFontDictionaries.Type0(face + "-" + cmapName, CMapEncoding(cmapName, defined), descendant);

            _fonts[resourceName] = font;
            _fontResourceByBaseFont[key] = resourceName;
            return resourceName;
        }

        /// <summary>
        /// A Type0 font's <c>/Encoding</c>: the predefined CMap's name, or the CMap the program
        /// defined as an embedded CMap stream (ISO 32000-2 §9.7.5.3, Table 118) — its program, name,
        /// character collection and writing mode, and the CMap it uses as <c>/UseCMap</c>: a
        /// program-defined one its own stream, a predefined one its name. One stream a CMap.
        /// </summary>
        private PdfObject CMapEncoding(string cmapName, PsCMap defined)
        {
            if (defined == null) return new PdfName(cmapName);
            if (_cmapStreams.TryGetValue(defined, out PdfStream known)) return known;

            var stream = new PdfStream();
            stream[PdfName.Type]          = PdfName.CMap;
            stream[PdfName.CMapName]      = new PdfName(defined.Name);
            stream[PdfName.CIDSystemInfo] = CompositeFontDictionaries.CidSystemInfo(defined.Registry, defined.Ordering, defined.Supplement);
            stream[PdfName.WMode]         = new PdfNumber(defined.WMode);
            if (defined.Parent != null) stream[PdfName.UseCMap] = CMapEncoding(defined.Parent.Name, defined.Parent);
            else if (defined.UseCMap != null) stream[PdfName.UseCMap] = new PdfName(defined.UseCMap);
            stream.RawData = defined.Program;

            _cmapStreams[defined] = stream;
            return stream;
        }

        private readonly Dictionary<PsCMap, PdfStream> _cmapStreams = new(PdfReferenceEquality.Instance);

        /// <summary>What sets a program-defined CMap's fonts apart in a resource key: one CMap redefined under its name is another CMap.</summary>
        private string CMapIdentity(PsCMap defined)
        {
            if (defined == null) return "";
            if (!_cmapIds.TryGetValue(defined, out int id)) _cmapIds[defined] = id = _cmapIds.Count + 1;
            return "\u0005" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private readonly Dictionary<PsCMap, int> _cmapIds = new(PdfReferenceEquality.Instance);

        /// <summary>
        /// The descendant CIDFont's <c>/FontDescriptor</c> — <b>required</b>, embedded or not.
        ///
        /// <para>Table 117 lists it as Required for every CIDFont and requires an indirect reference.
        /// This is precisely the case where it matters most: no CJK program is embedded here, so the
        /// reader <i>must</i> substitute a face — and <c>/Flags</c>, <c>/FontBBox</c>,
        /// <c>/ItalicAngle</c> and <c>/StemV</c> are the only things it has to choose and metric that
        /// substitute from. Without them a strict reader or preflight rejects the font outright and
        /// the whole CJK run draws as nothing, while the conversion reports success.</para>
        ///
        /// <para>The values are <b>declared conservatively, not measured</b>, the same trade
        /// <see cref="GetOrAddEmbeddedFont"/> makes: measuring needs the font program, which is the
        /// one thing this path deliberately never has. The box comfortably contains a full-width em
        /// plus overhang, and <c>/MissingWidth</c> agrees with the descendant's <c>/DW 1000</c> so a
        /// reader cannot get two different answers for the same unlisted CID.</para>
        ///
        /// <para>Left <b>direct</b> here. <c>ResourcePromoter</c> promotes any value under the
        /// <c>/FontDescriptor</c> key to an indirect reference wherever it appears, including inside
        /// <c>/DescendantFonts</c>, so the document path satisfies Table 117 for free; the fragment
        /// path hands its resources to a host that is writing them into a page's own resource
        /// dictionary and does its own promotion.</para>
        /// </summary>
        private static PdfDictionary CidFontDescriptor(string face)
        {
            var descriptor = new PdfDictionary();
            descriptor[PdfName.Type]         = PdfName.FontDescriptor;
            descriptor[PdfName.FontName]     = new PdfName(face);
            descriptor[PdfName.Flags]        = new PdfNumber(4);   // symbolic: the CMap chooses glyphs
            descriptor[PdfName.FontBBox]     = Rect(-200, -250, 1200, 1000);
            descriptor[PdfName.ItalicAngle]  = new PdfNumber(0);
            descriptor[PdfName.Ascent]       = new PdfNumber(880);
            descriptor[PdfName.Descent]      = new PdfNumber(-120);
            descriptor[PdfName.CapHeight]    = new PdfNumber(700);
            descriptor[PdfName.StemV]        = new PdfNumber(80);
            descriptor[PdfName.MissingWidth] = new PdfNumber(1000);
            return descriptor;
        }

        /// <summary>
        /// The resource name for a font whose program the document supplied, embedded verbatim as a
        /// <c>/FontFile</c>.
        ///
        /// <para><b>This is what lifts the last script limit.</b> Hebrew and Cyrillic have no glyphs
        /// in any standard-14 face, so a <c>/Differences</c> encoding alone leaves the reader
        /// nothing to draw. With the document's own Type 1 program embedded, the glyphs it shipped
        /// are the glyphs the reader uses — and the program is passed through byte for byte, so
        /// nothing here has to decrypt a charstring or understand an outline.</para>
        ///
        /// <para>The descriptor's metrics are declared conservatively rather than measured: a
        /// <c>/FontDescriptor</c> is mandatory for an embedded font and a reader uses it for
        /// fallback layout, but the real metrics live in the program the reader is about to
        /// parse.</para>
        ///
        /// <para><b>No <c>/Widths</c>, <c>/FirstChar</c> or <c>/LastChar</c>, and that is a known
        /// deviation rather than an oversight.</b> Table 111 requires all three for any Type 1 font
        /// that is not one of the standard 14. Deriving them needs the per-glyph advances, which
        /// live in the charstrings — and not decrypting a charstring is the whole basis of this
        /// path. The two ways to satisfy the letter of the rule both make the output worse:
        /// measuring against whichever standard-14 face the name happens to resemble writes widths
        /// for the wrong glyphs, and §9.6.2.1 requires an embedded program's widths to <i>agree</i>
        /// with the array, so a fabricated one is a second violation that also visibly wrecks the
        /// spacing; filling with zeros overprints every glyph on the last. Omitting leaves a reader
        /// to use the program's own metrics, which are correct. So the entry is skipped and
        /// <b>reported</b>, per this plugin's rule that what it cannot do is named rather than
        /// hidden.</para>
        /// </summary>
        public string GetOrAddEmbeddedFont(PsEmbeddedFont font,
                                           IReadOnlyDictionary<int, string> differences)
        {
            if (font.GlyphIndexed != null) return GetOrAddGlyphIndexedFont(font);

            // Keyed by the captured program, not its /FontName: two different programs a document
            // defines under one name were one resource, and every string set in the second drew
            // with the first's glyphs.
            string name = font.FontName ?? "EmbeddedFont";
            string key = "\u0002emb\u0002" + EmbeddedId(font) +
                         (differences == null || differences.Count == 0
                              ? string.Empty
                              : "\u0002" + EncodingKey(differences));

            if (_fontResourceByBaseFont.TryGetValue(key, out string existing)) return existing;

            string resourceName = MintFontName();
            if (resourceName == null) return CappedResourceName();

            var program = GetOrAddFontProgram(font);

            var descriptor = new PdfDictionary();
            descriptor[PdfName.Type]        = PdfName.FontDescriptor;
            descriptor[PdfName.FontName]    = new PdfName(name);
            descriptor[PdfName.Flags]       = new PdfNumber(4);   // symbolic: the font brings its own encoding
            descriptor[PdfName.FontBBox]    = Rect(-200, -250, 1200, 1000);
            descriptor[PdfName.ItalicAngle] = new PdfNumber(0);
            descriptor[PdfName.Ascent]      = new PdfNumber(750);
            descriptor[PdfName.Descent]     = new PdfNumber(-250);
            descriptor[PdfName.CapHeight]   = new PdfNumber(700);
            descriptor[PdfName.StemV]       = new PdfNumber(80);
            descriptor[font.Cff != null ? PdfName.FontFile3 : PdfName.FontFile] = program;

            var pdfFont = new PdfDictionary();
            pdfFont[PdfName.Type]           = PdfName.Font;
            pdfFont[PdfName.Subtype]        = PdfName.Type1;
            pdfFont[PdfName.BaseFont]       = new PdfName(name);
            pdfFont[PdfName.FontDescriptor] = descriptor;

            // A symbolic font uses its built-in encoding unless overridden; a /Differences the
            // program itself supplied would be redundant, one the document supplied is not.
            if (differences != null && differences.Count > 0)
                pdfFont[PdfName.Encoding] = BuildDifferencesEncoding(differences);

            _fonts[resourceName] = pdfFont;
            _fontResourceByBaseFont[key] = resourceName;

            // The /Widths Table 111 requires, read from the program itself — each code's glyph under
            // the encoding the font dictionary declares, its hsbw advance in glyph units, which a
            // Type 1 font's 1/1000 glyph space reads as thousandths of text space (§9.2.4) as the
            // emitted Tm does (§9.6.2.1: they agree with the program).
            if (font.CanMeasure)
            {
                var widths = new PdfArray();
                IReadOnlyDictionary<int, string> encoding = differences != null && differences.Count > 0 ? differences : null;
                for (int code = 0; code < 256; code++)
                    widths.Add(new PdfNumber(Math.Round(font.GlyphAdvance(font.GlyphNameFor(code, encoding)), 3)));
                pdfFont[PdfName.FirstChar] = new PdfNumber(0);
                pdfFont[PdfName.LastChar] = new PdfNumber(255);
                pdfFont[PdfName.Widths] = widths;
                return resourceName;
            }

            if (!_widthsOmissionReported)
            {
                _widthsOmissionReported = true;
                PostScriptDiagnostics.Report("PdfResourceCollector",
                    $"embedded font '{name}' is emitted without the /Widths, /FirstChar and " +
                    "/LastChar that Table 111 requires for a non-standard-14 Type 1 font: the " +
                    "per-glyph advances are in the charstrings, which this path deliberately never " +
                    "decrypts. Readers use the font program's own metrics, so text renders " +
                    "correctly, but the font dictionary will not pass a strict validator; and the " +
                    "interpreter's own pen advance after each show is measured against the " +
                    "standard face the name maps to, so a position taken from currentpoint after " +
                    "embedded text is approximate. Reported once per conversion.");
            }

            return resourceName;
        }

        // Per collector, and a collector is per conversion — so this needs no thread-static slot
        // and no reset hook, unlike the operator-level one-shots.
        private bool _widthsOmissionReported;

        /// <summary>
        /// The resource name for a Type 42 font: a <b>Type0</b> font under <c>/Identity-H</c> over a
        /// <c>CIDFontType2</c> whose CIDs are glyph indices (<c>/CIDToGIDMap /Identity</c>), its program
        /// a <c>/FontFile2</c> — one resource a font, whatever encoding its text is shown through.
        ///
        /// <para><b>Why not a simple TrueType font.</b> A simple font reaches a glyph through the
        /// program's <c>cmap</c> by code, so each encoding a document shows the font through — and
        /// every <c>glyphshow</c> of a name outside it — would need a program of its own with its own
        /// <c>cmap</c>. Showing two-byte glyph indices names the glyph directly; the text operators
        /// write them (<c>PsTextOps</c>), splitting a run after each space where word spacing applies,
        /// since <c>Tw</c> spaces a one-byte code 32 alone (ISO 32000-1 §9.3.3).</para>
        ///
        /// <para>The program, the <c>/W</c> widths and the <c>/ToUnicode</c> are written when the
        /// resources are built (<see cref="Build"/>): the glyphs a driver downloads arrive as the
        /// document runs, and each is taken when it is first used (<see cref="PsType42Font"/>).</para>
        /// </summary>
        public string GetOrAddGlyphIndexedFont(PsEmbeddedFont font)
        {
            string key = "\u0003t42\u0003" + EmbeddedId(font);
            if (_fontResourceByBaseFont.TryGetValue(key, out string existing)) return existing;

            string resourceName = MintFontName();
            if (resourceName == null) return CappedResourceName();

            IPsGlyphIndexedProgram program = font.GlyphIndexed;
            string name = font.FontName ?? "Type42Font";
            var (xMin, yMin, xMax, yMax, ascent, descent) = program.DescriptorMetrics();

            var descriptor = new PdfDictionary();
            descriptor[PdfName.Type]        = PdfName.FontDescriptor;
            descriptor[PdfName.FontName]    = new PdfName(name);
            descriptor[PdfName.Flags]       = new PdfNumber(4);   // symbolic: glyphs by index, no standard character set
            descriptor[PdfName.FontBBox]    = Rect(Math.Round(xMin), Math.Round(yMin), Math.Round(xMax), Math.Round(yMax));
            descriptor[PdfName.ItalicAngle] = new PdfNumber(0);
            descriptor[PdfName.Ascent]      = new PdfNumber(Math.Round(ascent));
            descriptor[PdfName.Descent]     = new PdfNumber(Math.Round(descent));
            descriptor[PdfName.CapHeight]   = new PdfNumber(Math.Round(ascent));
            descriptor[PdfName.StemV]       = new PdfNumber(80);

            // A TrueType program maps CIDs to its glyphs through /CIDToGIDMap; a CFF whose Top DICT
            // uses no CIDFont operators takes its CIDs as glyph indices (ISO 32000-2 §9.7.4.2).
            var descendant = CompositeFontDictionaries.DescendantCidFont(
                program.IsCff ? PdfName.CIDFontType0 : PdfName.CIDFontType2, name,
                CompositeFontDictionaries.IdentityCidSystemInfo(), descriptor);
            if (!program.IsCff) descendant[PdfName.CIDToGIDMap] = PdfName.Identity;

            var type0 = CompositeFontDictionaries.Type0(name, PdfName.IdentityH, descendant);

            _fonts[resourceName] = type0;
            _fontResourceByBaseFont[key] = resourceName;
            _type42.Add((program, type0, descendant, descriptor));
            return resourceName;
        }

        private readonly List<(IPsGlyphIndexedProgram Font, PdfDictionary Type0, PdfDictionary Descendant, PdfDictionary Descriptor)> _type42 = new();

        /// <summary>
        /// The resource name for a CID-keyed CFF font under <paramref name="cmapName"/>: a Type0 font
        /// naming the CMap, or embedding the one the program defined (<paramref name="defined"/>) — the shown bytes pass through, the reader decoding them as the PostScript
        /// did — over a <c>CIDFontType0</c> whose program is the font set itself, a <c>/FontFile3</c> of
        /// <c>/CIDFontType0C</c>, its <c>/CIDSystemInfo</c> the font's own <c>ROS</c>, and a <c>/W</c>
        /// written when the resources are built from the CIDs the text used.
        /// </summary>
        public string GetOrAddCidCffFont(PsEmbeddedFont font, string cmapName, PsCMap defined = null)
        {
            string key = "\u0004cff\u0004" + EmbeddedId(font) + "\u0004" + cmapName + CMapIdentity(defined);
            if (_fontResourceByBaseFont.TryGetValue(key, out string existing)) return existing;

            string resourceName = MintFontName();
            if (resourceName == null) return CappedResourceName();

            string name = font.FontName ?? "CIDFont";
            var descriptor = CidFontDescriptor(name);
            descriptor[PdfName.FontFile3] = GetOrAddFontProgram(font);

            var descendant = CompositeFontDictionaries.DescendantCidFont(PdfName.CIDFontType0, name,
                CompositeFontDictionaries.CidSystemInfo(font.Cff.Registry, font.Cff.Ordering, font.Cff.Supplement), descriptor);

            var type0 = CompositeFontDictionaries.Type0(name + "-" + cmapName, CMapEncoding(cmapName, defined), descendant);

            _fonts[resourceName] = type0;
            _fontResourceByBaseFont[key] = resourceName;
            _cidCff.Add((font, descendant));
            return resourceName;
        }

        private readonly List<(PsEmbeddedFont Font, PdfDictionary Descendant)> _cidCff = new();

        /// <summary>Each CID-keyed CFF font's <c>/W</c>: a width for every CID its text used, the advance the pen moved by.</summary>
        private void FinishCidCffFonts()
        {
            foreach (var (font, descendant) in _cidCff)
            {
                var widths = new PdfArray();
                var cids = new List<int>(font.CidAdvances.Keys);
                cids.Sort();
                foreach (int cid in cids)
                {
                    var one = new PdfArray();
                    one.Add(new PdfNumber(Math.Round(font.CidAdvances[cid], 3)));
                    widths.Add(new PdfNumber(cid));
                    widths.Add(one);
                }
                descendant[PdfName.W] = widths;
            }
        }

        /// <summary>
        /// Completes each Type 42 font from what its text used: the program, a <c>/W</c> entry a glyph
        /// shown (the advance the interpreter moved the pen by, so the reader's agrees), and a
        /// <c>/ToUnicode</c> from each glyph's name (Core's one writer of the map).
        /// </summary>
        private void FinishType42Fonts()
        {
            foreach (var (font, type0, descendant, descriptor) in _type42)
            {
                if (font.ProgramStream() is { } program)
                    descriptor[font.IsCff ? PdfName.FontFile3 : PdfName.FontFile2] = program;

                var widths = new PdfArray();
                var glyphs = new List<int>(font.Advances.Keys);
                glyphs.Sort();
                foreach (int glyph in glyphs)
                {
                    var one = new PdfArray();
                    one.Add(new PdfNumber(Math.Round(font.Advances[glyph], 3)));
                    widths.Add(new PdfNumber(glyph));
                    widths.Add(one);
                }
                descendant[PdfName.W] = widths;

                // One code point stands as it is; longer text (a CID mapping to several characters)
                // is the writer's text override, keyed by its first code point.
                var text = new List<KeyValuePair<ushort, int>>();
                var longer = new Dictionary<ushort, string>();
                foreach (var kv in font.Texts)
                {
                    if (string.IsNullOrEmpty(kv.Value)) continue;
                    bool pair = char.IsSurrogatePair(kv.Value, 0);
                    int first = pair ? char.ConvertToUtf32(kv.Value, 0) : kv.Value[0];
                    text.Add(new KeyValuePair<ushort, int>((ushort)kv.Key, first));
                    if (kv.Value.Length > (pair ? 2 : 1)) longer[(ushort)kv.Key] = kv.Value;
                }
                if (text.Count > 0)
                    type0[PdfName.ToUnicode] = new PdfStream { RawData = Core.Writing.ToUnicodeCMapWriter.Build(text, longer) };
            }
        }

        /// <summary>
        /// The one <c>/FontFile</c> stream for a captured program, shared by every encoding of it.
        ///
        /// <para><b>The font dictionaries legitimately differ per encoding; the program does not.</b>
        /// <see cref="GetOrAddEmbeddedFont"/> keys its resource cache on (name, encoding), and
        /// <c>ResourcePromoter</c> memoises by <b>reference identity</b> of the stream, so a fresh
        /// <c>PdfStream</c> over the same <c>byte[]</c> per miss would make N encodings of one
        /// program N promoted objects and <c>PdfWriter</c> write the bytes N times — bounded only
        /// by <see cref="PsLimits.MaxFontResources"/> (256) times the captured program size, since
        /// <c>MaxEmittedDocumentBytes</c> bounds content streams and never sees resources. A 16 MiB
        /// <c>eexec</c> section shown under 256 distinct encodings would write ~4 GiB from a ~16 MiB
        /// input, with no cap and no diagnostic.</para>
        ///
        /// <para>Reference identity is the right key: <c>PsInterpreter.LastEmbeddedFont</c> and
        /// <c>EmbeddedFonts</c> both hand out the same instance for the same captured program, and
        /// two genuinely separate captures must stay two streams. With one stream instance,
        /// <c>ResourcePromoter</c>'s own memo then collapses them to one indirect object for free.</para>
        /// </summary>
        private PdfStream GetOrAddFontProgram(PsEmbeddedFont font)
        {
            if (_fontPrograms.TryGetValue(font, out var existing)) return existing;

            var program = new PdfStream();
            if (font.Cff != null)
            {
                // A CFF font as it is: ISO 32000-1 Table 126's /FontFile3 of /Type1C, or of
                // /CIDFontType0C for a CID-keyed one.
                program[PdfName.Subtype] = font.Cff.IsCidKeyed ? PdfName.CIDFontType0C : PdfName.Type1C;
                program.RawData = font.Cff.Data;
                _fontPrograms[font] = program;
                return program;
            }
            program[PdfName.Length1] = new PdfNumber(font.Length1);
            program[PdfName.Length2] = new PdfNumber(font.Length2);
            program[PdfName.Length3] = new PdfNumber(font.Length3);
            program.RawData = font.Program;

            _fontPrograms[font] = program;
            return program;
        }

        // Reference identity, not value equality: PsEmbeddedFont has no Equals override, so the
        // default reference comparer is what this wants — but it is stated rather than inherited,
        // because adding a record-style Equals to that type later would silently merge two
        // different captures that happened to agree byte for byte.
        private readonly Dictionary<PsEmbeddedFont, PdfStream> _fontPrograms =
            new(PdfReferenceEquality.Instance);

        private static PdfArray Rect(double x0, double y0, double x1, double y1)
        {
            var a = new PdfArray();
            a.Add(new PdfNumber(x0)); a.Add(new PdfNumber(y0));
            a.Add(new PdfNumber(x1)); a.Add(new PdfNumber(y1));
            return a;
        }

        /// <summary>Registers an image XObject and returns its resource name.</summary>
        public string AddImage(PdfStream image)
        {
            string resourceName = "Im" + _nextXObjectId++;
            _xobjects[resourceName] = image;
            return resourceName;
        }

        /// <summary>
        /// The finished <c>/Resources</c> dictionary, or null when nothing was registered — the
        /// contract reads null as "inherit", which is what an empty dictionary would wrongly
        /// prevent.
        /// </summary>
        public PdfDictionary Build()
        {
            if (IsEmpty) return null;
            FinishType42Fonts();
            FinishCidCffFonts();

            var resources = new PdfDictionary();

            if (_fonts.Count > 0)
            {
                var fonts = new PdfDictionary();
                foreach (var kv in _fonts) fonts[new PdfName(kv.Key)] = kv.Value;
                resources[PdfName.Font] = fonts;
            }

            if (_xobjects.Count > 0)
            {
                var xobjects = new PdfDictionary();
                foreach (var kv in _xobjects) xobjects[new PdfName(kv.Key)] = kv.Value;
                resources[PdfName.XObject] = xobjects;
            }

            if (_shadings.Count > 0)
            {
                var shadings = new PdfDictionary();
                foreach (var kv in _shadings) shadings[new PdfName(kv.Key)] = kv.Value;
                resources[PdfName.Shading] = shadings;
            }

            if (_patterns.Count > 0)
            {
                var patterns = new PdfDictionary();
                foreach (var kv in _patterns) patterns[new PdfName(kv.Key)] = kv.Value;
                resources[PdfName.Pattern] = patterns;
            }

            if (_colorSpaces.Count > 0)
            {
                var spaces = new PdfDictionary();
                foreach (var kv in _colorSpaces) spaces[new PdfName(kv.Key)] = kv.Value;
                resources[PdfName.ColorSpace] = spaces;
            }

            if (_extGStates.Count > 0)
            {
                var states = new PdfDictionary();
                foreach (var kv in _extGStates) states[new PdfName(kv.Key)] = kv.Value;
                resources[PdfName.ExtGState] = states;
            }

            return resources;
        }
    }
}
