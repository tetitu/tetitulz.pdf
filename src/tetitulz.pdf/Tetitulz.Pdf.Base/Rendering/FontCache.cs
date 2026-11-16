// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Caches resolved <see cref="SKTypeface"/> instances and font decoders
    /// for the lifetime of a single page render.
    /// </summary>
    internal sealed class FontCache : IDisposable
    {
        /// <summary>
        /// Forces SkiaSharp's type initializers to run in an order that resolves.
        ///
        /// <para><b>Why this exists.</b> <c>SKObject</c>'s static constructor calls
        /// <c>SKTypeface.EnsureStaticInstanceAreInitialized()</c>, and <c>SKTypeface</c>'s own
        /// static constructor needs <c>SKObject</c> — a circular static dependency that the CLR
        /// resolves only if <c>SKTypeface</c> is touched <i>first</i>. Reach SkiaSharp through
        /// <c>SKFontStyle</c> instead (which <see cref="ResolveByName"/> does, via
        /// <c>SKFontStyle.Normal</c>) and the cycle is entered from the wrong side: the initializer
        /// throws a <c>NullReferenceException</c>, and because a failed type initializer is
        /// <b>permanent for the lifetime of the process</b>, every subsequent SkiaSharp call —
        /// every render, in every later operation — then fails with
        /// <c>TypeInitializationException</c>.</para>
        ///
        /// <para>It is order-dependent and therefore intermittent: whichever code path happens to
        /// touch SkiaSharp first decides whether the process works at all. In this repository it
        /// surfaced as whole-solution test runs that intermittently failed a large block of
        /// rendering tests (GAPS.md §8a) while the same tests passed in isolation. A consumer whose
        /// first SkiaSharp touch is a font lookup would hit exactly the same thing.</para>
        ///
        /// <para>Touching <c>SKTypeface.Default</c> once, before anything else here can reach
        /// <c>SKFontStyle</c>, enters the cycle from the side that resolves.</para>
        /// </summary>
        static FontCache()
        {
            try
            {
                _ = SKTypeface.Default;
            }
            catch (Exception ex)
            {
                // Never let this abort the type initializer — a host without native SkiaSharp
                // assets should fail at the actual render call with its own error, not here.
                RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                    "SkiaSharp could not be initialised; rendering will fail. " + NativeAssetHint(),
                    ex);
            }
        }

        /// <summary>
        /// Names the native package this host needs, so a missing asset reads as a fixable
        /// packaging problem rather than a bare <c>DllNotFoundException</c> from deep inside a
        /// render call. Diagnostic text only — nothing branches on it.
        /// </summary>
        private static string NativeAssetHint()
        {
            // RuntimeInformation.RuntimeIdentifier is net5.0+; this assembly also targets
            // netstandard2.0, so describe the host from members that exist on both.
            string rid;
            try
            {
                rid = System.Runtime.InteropServices.RuntimeInformation.OSDescription
                    + " / " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
            }
            catch (Exception) { rid = "unknown"; }

            string package = "SkiaSharp.NativeAssets.Win32";
            try
            {
                var os = System.Runtime.InteropServices.OSPlatform.Linux;
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(os))
                    // Names only what the library uses: naming a package it does not reference would
                    // send a host chasing the wrong missing file.
                    package = "SkiaSharp.NativeAssets.Linux";
                else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                             System.Runtime.InteropServices.OSPlatform.OSX))
                    package = "SkiaSharp.NativeAssets.macOS";
            }
            catch (Exception) { /* keep the Windows default */ }

            return $"No libSkiaSharp was loadable for runtime '{rid}'. "
                 + $"Ensure {package} is referenced and its runtimes/ folder reached the output. "
                 + "On Linux, SkiaSharp.NativeAssets.Linux also requires libfontconfig1 to be "
                 + "installed on the host.";
        }

        /// <summary>
        /// Everything this cache remembers about one font resource, in one record rather than
        /// parallel containers keyed identically.
        ///
        /// <para>The <c>*Cached</c> flags exist because "not cached yet" and "cached as
        /// null / 0 / false" are different states. A <c>null</c> typeface or <c>PdfFont</c>
        /// is a legitimate cached result that must NOT trigger a reload.</para>
        /// </summary>
        private sealed class FontEntry
        {
            public bool       TypefaceCached;
            public SKTypeface Typeface;

            public bool    PdfFontCached;
            public PdfFont PdfFont;


            /// <summary>The font's /Encoding CMap (composite fonts only; null for simple fonts).</summary>
            public bool                 EncodingCodecCached;
            public Core.Fonts.CMapCodec EncodingCodec;

            public bool WModeCached;
            public int  WMode;

            public bool IsType3Cached;
            public bool IsType3;

            /// <summary>Set when the embedded program is CFF that SkiaSharp could not load.</summary>
            public CffGlyphSource CffSource;

            /// <summary>Set when the font was parsed from a /FontFile Type 1 PostScript program.</summary>
            public Type1GlyphSource Type1Source;

            /// <summary>True when the typeface is an OTF-wrapped raw CFF (use the GID path in CSI).</summary>
            public bool IsOtfCff;

            /// <summary>
            /// A CFF program (bare, or OpenType's CFF table) SkiaSharp loaded itself, kept until the draw first
            /// asks for <see cref="HintingProbe"/>, which is parsed from it then.
            /// </summary>
            public byte[] HintingProbeProgram;

            /// <summary>Our evaluator over a program Skia draws itself (<see cref="GetHintingProbe"/>).</summary>
            public CffGlyphSource HintingProbe;

            /// <summary>
            /// True when the embedded font could not be decoded and a system-font substitute was
            /// used. For these, PDF /Widths are not applicable — the substitute's own metrics must
            /// be used.
            /// </summary>
            public bool IsSystemFallback;
        }

        // (resources-dict identity, resource-name) → everything cached for that font.
        // The resources dict identity is included so that a Form XObject whose /Resources
        // dict maps "/F1" to a different font than the page's /Resources does not get the
        // wrong cached typeface when both share the same FontCache instance.
        private readonly Dictionary<(int, int, string), FontEntry> _entries
            = new Dictionary<(int, int, string), FontEntry>();

        /// <summary>
        /// How many distinct fonts this cache is holding state for. Exists so the key's
        /// <b>liveness</b> can be asserted directly.
        /// </summary>
        /// <remarks>
        /// Asserting on the returned <c>SKTypeface</c> cannot do it: SkiaSharp hands back the same
        /// instance for identical program bytes, so two entries built from one embedded font are
        /// reference-equal and a test comparing them passes with the key change reverted. What
        /// duplicate entries actually cost is the duplicated managed work behind them — the font
        /// dictionary read, the program extracted and parsed, the widths built — and the entry
        /// count is what sees it. Same reason <c>CMapCodec.UsesCidRangeIndex</c> exists.
        /// </remarks>
        internal int EntryCount => _entries.Count;
        /// <summary>Returns the entry for <paramref name="key"/>, creating an empty one if needed.</summary>
        private FontEntry Entry((int, int, string) key)
        {
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new FontEntry();
                _entries[key] = e;
                EntriesCreated++;
            }
            return e;
        }

        /// <summary>
        /// Entries any cache has created on the calling thread: the evidence, across caches, that
        /// the writers drawing one page share one — <see cref="EntryCount"/> sees a single cache.
        /// </summary>
        [ThreadStatic] internal static long EntriesCreated;

        /// <summary>Returns the entry for <paramref name="key"/>, or <c>null</c> when nothing is cached.</summary>
        private FontEntry EntryOrNull((int, int, string) key)
            => _entries.TryGetValue(key, out var e) ? e : null;

        /// <summary>
        /// The cache key for the font <paramref name="name"/> names in <paramref name="resources"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Preferred form: the font's own object number</b> (kind 1). A page's
        /// <c>/Font</c> subdictionary normally names its fonts indirectly, and the object number
        /// identifies the font <i>uniquely</i> — so every page that draws it shares one entry
        /// however many distinct <c>/Resources</c> dictionaries are involved, and the font program
        /// is decoded once rather than once per page. The resource name is deliberately NOT part
        /// of this key: the object number already says which font it is, and two names for one
        /// font should share.</para>
        ///
        /// <para><b>Fallback: the resources dictionary's identity hash plus the name</b> (kind 0),
        /// for a direct font dictionary, or an indirect <c>/Font</c> this cannot see through
        /// without a document to resolve with. <c>RuntimeHelpers.GetHashCode</c> rather than the
        /// object's own <c>GetHashCode</c>, so structurally equal but distinct dictionaries still
        /// key apart. Either way a Form XObject whose <c>/Resources</c> maps the same name to a
        /// different font cannot pick up the page's cached typeface.</para>
        ///
        /// <para><b>Why the preferred form exists.</b> The fallback key alone would accept its
        /// collision risk "for the handful of resource dictionaries alive during a single render",
        /// and <b>that premise does not hold for the shape most producers emit</b>: a <c>/Resources</c> dictionary per page,
        /// batch-rendered through one <c>FontCache</c>, is one key per page — thousands on a large
        /// document, and nothing evicts them. The failure a collision produces is a page drawn
        /// with another page's font. Keying on the object number removes the collision for that
        /// case rather than re-costing it, and restores the "handful" the fallback assumed.
        /// <c>RenderBenchmarks.FiftyPages_PerPageResources_All</c> is the A/B against the
        /// shared-resources file.</para>
        /// </remarks>
        private static (int, int, string) MakeKey(PdfDictionary resources, string name)
        {
            // The indexer deliberately does NOT resolve: the reference itself is what identifies
            // the font, and resolving would need a document this method is not given.
            if (name != null && resources != null
                && resources[Core.Objects.PdfName.Font] is PdfDictionary fonts
                && fonts[name] is Core.Objects.PdfReference fontRef)
            {
                return (1, fontRef.ReferenceObjectNumber, null);
            }

            return (0,
                    resources is null ? 0
                        : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(resources),
                    name);
        }

        // ---- Standard 14 PDF font → closest system-available family ----
        // The map moved to Core.Fonts.Standard14Names.SystemFamily, which the Accessibility font
        // remediator also calls. Its copy's comment had asserted the two were "deliberately the
        // same" pairings so that an embedded substitute matches what this renderer draws — a
        // correctness requirement that nothing enforced until they shared one implementation.
        //
        // ZapfDingbats and its ZaDb alias are handled by the GID-probing branch above, which
        // returns before this lookup.

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Returns a <see cref="SKTypeface"/> for the given font resource name,
        /// loading it from the page resources (embedded data or system fallback).
        /// </summary>
        public SKTypeface GetTypeface(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return FallbackTypeface();
            var key = MakeKey(resources, resourceName);
            var entry = Entry(key);
            if (entry.TypefaceCached) return entry.Typeface;

            // LoadTypeface populates the same entry instance as a side effect (CFF source,
            // Type 1 source, OTF-CFF and system-fallback flags), which is why the entry is
            // created before the call rather than after it.
            var tf = LoadTypeface(resourceName, resources, doc);
            entry.Typeface = tf;
            entry.TypefaceCached = true;
            return tf;
        }

        /// <summary>
        /// Returns the PdfFont object for width lookups via GetWidth(charCode).
        /// Used by ContentStreamInterpreter for PDF-based glyph positioning (Hebrew fix).
        /// </summary>
        public PdfFont GetPdfFont(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return null;
            var entry = Entry(MakeKey(resources, resourceName));
            if (!entry.PdfFontCached)
            {
                entry.PdfFont = LoadPdfFont(resourceName, resources, doc);
                entry.PdfFontCached = true;
            }
            return entry.PdfFont;
        }

        /// <summary>
        /// Returns the CID-to-GID map for the font if it is a TrueType CIDFont with /CIDToGIDMap.
        /// Used by the renderer to map character codes directly to font glyph indices.
        /// </summary>
        public IReadOnlyDictionary<int, int> GetCidToGid(string resourceName, PdfDictionary resources, PdfDocument doc)
            => GetPdfFont(resourceName, resources, doc)?.GetCidToGid();

        public bool GetIsIdentityCidToGid(string resourceName, PdfDictionary resources, PdfDocument doc)
            => GetPdfFont(resourceName, resources, doc)?.IsIdentityCidToGid ?? false;

        /// <summary>
        /// Decodes a byte array to a Unicode string using the font's encoding / ToUnicode CMap.
        /// </summary>
        public string DecodeText(string resourceName, byte[] bytes, PdfDictionary resources, PdfDocument doc)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            if (resourceName == null) return FallbackDecode(bytes);

            // GetPdfFont performs the identical load-and-cache; it returns null only for a
            // null resource name (handled above) or a font that genuinely failed to load,
            // both of which fall through to FallbackDecode exactly as before.
            return GetPdfFont(resourceName, resources, doc)?.Decode(bytes) ?? FallbackDecode(bytes);
        }

        /// <summary>
        /// The text one code of <paramref name="resourceName"/> decodes to — <see cref="DecodeText"/>
        /// for the code's own bytes — answered from the font's per-code cache
        /// (<see cref="Core.Fonts.PdfFont.Decode(Core.Fonts.CMapCodec.Code)"/>), so a caller that has
        /// split a shown string into codes decodes each without copying its bytes out.
        /// </summary>
        internal string DecodeCode(string resourceName, Core.Fonts.CMapCodec.Code code,
            PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return FallbackDecode(Core.Fonts.PdfFont.CodeBytes(code));
            return GetPdfFont(resourceName, resources, doc)?.Decode(code)
                   ?? FallbackDecode(Core.Fonts.PdfFont.CodeBytes(code));
        }

        /// <summary>
        /// Returns the <b>encoding</b> CMap of a composite (Type 0) font, or <c>null</c> for a
        /// simple font. Used to split show-string bytes into character codes and map them to CIDs.
        /// </summary>
        /// <remarks>
        /// The source is the font's <c>/Encoding</c> entry (PDF §9.7.5) — a predefined CMap name
        /// or an embedded CMap stream — never <c>/ToUnicode</c>, which is an optional text-extraction
        /// aid: it may be absent,
        /// may declare a codespace that disagrees with the encoding's, and carries no code→CID
        /// mapping at all. A non-Identity predefined CMap (<c>/UniJIS-UCS2-H</c>, the RKSJ family)
        /// was therefore decoded as though code == CID, picking the wrong glyphs.
        /// </remarks>
        public Core.Fonts.CMapCodec GetEncodingCodec(
            string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return null;
            var entry = Entry(MakeKey(resources, resourceName));
            if (entry.EncodingCodecCached) return entry.EncodingCodec;

            entry.EncodingCodec = ComputeEncodingCodec(resourceName, resources, doc);
            entry.EncodingCodecCached = true;
            return entry.EncodingCodec;
        }

        /// <summary>
        /// The renderers' view of a composite font's <c>/Encoding</c> codec: Core's resolution,
        /// plus this assembly's diagnostic reporting, plus the <c>null</c> that means "not a
        /// composite font, so there is no codec to cache".
        /// </summary>
        /// <remarks>
        /// <para><b>The resolution itself is Core's</b> (<c>CMapCodec.FromFontEncoding</c>, the
        /// single answer for the splitting question). Two copies of "which codec does this font
        /// declare?" is the exact
        /// shape that produced the divergence the splitting consolidation closed; the second copy
        /// was simply never given a chance to drift.</para>
        ///
        /// <para><b>What stays here, and why.</b> Two things Core cannot own. The <c>null</c>
        /// return for a non-Type 0 font is a <i>cache</i> decision — <c>FromFontEncoding</c> never
        /// returns null, because for a descendant CIDFont handed in directly Identity is the right
        /// answer — and the <c>RenderDiagnostics</c> reporting, because Core has no render
        /// diagnostics sink and must not grow one.</para>
        ///
        /// <para>Core reports two cases rather than handling them silently — an <c>/Encoding</c> stream
        /// that decodes to nothing, and an <c>/Encoding</c> that is neither a name nor a stream.
        /// Becoming Identity with no word to the caller would be the "no /Encoding and an unhonourable /Encoding look identical"
        /// failure the diagnostic exists for.</para>
        /// </remarks>
        private Core.Fonts.CMapCodec ComputeEncodingCodec(
            string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null) return null;
            var resolved = doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict;
            if (PdfResolve.Name(doc, resolved[PdfName.Subtype])?.Value != PdfName.Type0.Value) return null;

            var codec = Core.Fonts.CMapCodec.FromFontEncoding(resolved, doc);

            string issue = Core.Fonts.CMapCodec.LastEncodingError;
            if (issue != null)
                RenderDiagnostics.Report(RenderDiagnosticKind.MissingPredefinedCMap, issue);

            return codec;
        }

        /// <summary>
        /// Returns the writing mode of the font: 0 = horizontal (default), 1 = vertical.
        /// </summary>
        public int GetWMode(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return 0;
            var entry = Entry(MakeKey(resources, resourceName));
            if (entry.WModeCached) return entry.WMode;

            entry.WMode = ComputeWMode(resourceName, resources, doc);
            entry.WModeCached = true;
            return entry.WMode;
        }

        private int ComputeWMode(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null) return 0;
            var resolved = doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict;

            // A Type0 font's /Encoding names its writing mode. A predefined name is Core's to read
            // (CMapCodec.FromFontEncoding: a -V name is vertical, registered or not), and so is an
            // embedded CMap's program (`/WMode 1 def`, or a `usecmap` of Identity-V). This method
            // kept its own copy of the name rule beside the codec's; the codec is the one reading.
            // An embedded CMap stream also carries /WMode in its stream dictionary (Table 120),
            // which the codec does not see. Neither embedded source was consulted, so text through
            // an embedded vertical CMap was laid out horizontally.
            if (doc.ResolveObject(resolved[PdfName.Encoding]) is PdfStream cmapStream
                && PdfResolve.Number(doc, cmapStream, PdfName.WMode)?.LongValue == 1)
                return 1;
            if (GetEncodingCodec(resourceName, resources, doc)?.WMode == 1)
                return 1;

            // WMode may also be declared explicitly in the descendant CIDFont dictionary.
            var descendant = GetDescendantFont(resolved, doc);
            var target = descendant ?? resolved;
            if (doc.ResolveObject(target[PdfName.WMode]) is PdfNumber wm)
                return wm.IntValue;
            // Check /Encoding on the descendant for vertical CMaps (e.g. /90ms-RKSJ-V).
            var encObj = doc.ResolveObject(target[PdfName.Encoding]) ?? target[PdfName.Encoding];
            if (encObj is PdfName encName)
            {
                string enc = encName.Value;
                if (enc.EndsWith("-V", System.StringComparison.OrdinalIgnoreCase) ||
                    enc.Contains("-V-", System.StringComparison.OrdinalIgnoreCase))
                    return 1;
            }
            return 0;
        }

        public bool IsType3Font(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null || resources == null) return false;
            var entry = Entry(MakeKey(resources, resourceName));
            if (entry.IsType3Cached) return entry.IsType3;

            var fontDict = LookupFontDict(resourceName, resources, doc);
            entry.IsType3 = fontDict != null &&
                (doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict)
                    is PdfDictionary fd3 && (doc.ResolveObject(fd3[PdfName.Subtype]) as PdfName)?.Value == PdfName.Type3.Value;
            entry.IsType3Cached = true;
            return entry.IsType3;
        }

        /// <summary>
        /// The resolved <c>/FontDescriptor</c> of a font resource, or null when it has none.
        /// </summary>
        /// <remarks>
        /// <b>For a Type0 font the descriptor is on the descendant CIDFont</b>, which is the one
        /// piece of this lookup a caller would get wrong on its own — and getting it wrong reads as
        /// "this font has no descriptor", which is indistinguishable from a standard-14. Extracted
        /// from <see cref="GetFontDescriptorMetrics"/> so the two callers cannot answer it
        /// differently.
        /// </remarks>
        public PdfDictionary GetFontDescriptor(
            string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null || doc == null) return null;

            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null) return null;

            var resolved = doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict;
            var fdObj = resolved[PdfName.FontDescriptor];

            if (fdObj == null)
            {
                var descendant = GetDescendantFont(resolved, doc);
                if (descendant != null) fdObj = descendant[PdfName.FontDescriptor];
            }

            return fdObj == null ? null : doc.ResolveObject(fdObj) as PdfDictionary;
        }

        /// <summary>
        /// Extracts Ascent, Descent, CapHeight and XHeight from the /FontDescriptor.
        /// All values are in PDF glyph units (1/1000 of the text space unit).
        /// Returns zeros when the font has no /FontDescriptor.
        /// </summary>
        public FontDescriptorMetrics GetFontDescriptorMetrics(
            string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            PdfDictionary fd = GetFontDescriptor(resourceName, resources, doc);
            if (fd == null) return default;

            float GetFdFloat(PdfName key)
                => PdfResolve.Number(doc, fd, key) is PdfNumber n ? (float)n.DoubleValue : 0f;

            return new FontDescriptorMetrics(
                ascent:    GetFdFloat(PdfName.Ascent),
                descent:   GetFdFloat(PdfName.Descent),
                capHeight: GetFdFloat(PdfName.CapHeight),
                xHeight:   GetFdFloat(PdfName.XHeight));  // no PdfName.XHeight yet
        }

        /// <summary>
        /// Returns a <see cref="Type1GlyphSource"/> for the given font resource when the
        /// embedded font program is Type 1 PostScript and SkiaSharp could not load it.
        /// Returns <c>null</c> when no Type 1 source is available.
        /// </summary>
        public Type1GlyphSource GetType1GlyphSource(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return null;
            GetTypeface(resourceName, resources, doc); // ensure loading has run
            return EntryOrNull(MakeKey(resources, resourceName))?.Type1Source;
        }

        /// <summary>
        /// Returns a <see cref="CffGlyphSource"/> for the given font resource when the
        /// embedded font program is CFF but SkiaSharp could not load it as a typeface.
        /// Returns <c>null</c> when no CFF fallback is available (TrueType or system font).
        /// </summary>
        public CffGlyphSource GetGlyphSource(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return null;
            // Ensure the typeface loading has run (it populates CffSource as a side effect).
            GetTypeface(resourceName, resources, doc);
            return EntryOrNull(MakeKey(resources, resourceName))?.CffSource;
        }

        /// <summary>
        /// Our charstring evaluator over an embedded CFF program — bare or OpenType's — that SkiaSharp
        /// loaded itself, so Skia draws it; <c>null</c> for any other font. The draw asks it whether
        /// the glyphs it is about to hand Skia are too heavy for it
        /// (<see cref="CffGlyphSource.IsTooHeavyForHinting(IReadOnlyList{ushort})"/>): a subroutine bomb
        /// through Skia cost about 15 ms a glyph this way. Parsed on the first question.
        /// </summary>
        public CffGlyphSource GetHintingProbe(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return null;
            GetTypeface(resourceName, resources, doc);
            var entry = EntryOrNull(MakeKey(resources, resourceName));
            if (entry == null) return null;
            if (entry.HintingProbe == null && entry.HintingProbeProgram != null)
            {
                var font = CffParser.Parse(entry.HintingProbeProgram);
                entry.HintingProbeProgram = null;
                if (font != null) entry.HintingProbe = new CffGlyphSource(font);
            }
            return entry.HintingProbe;
        }

        /// <summary>Whether an sfnt is CFF-flavoured OpenType (<c>'OTTO'</c>).</summary>
        private static bool IsOpenTypeCff(byte[] data) =>
            data.Length >= 4 && data[0] == 0x4F && data[1] == 0x54 && data[2] == 0x54 && data[3] == 0x4F;

        /// <summary>
        /// Returns <c>true</c> when the typeface for this font resource was produced by
        /// wrapping raw CFF bytes in an OTF envelope. In that case the renderer should
        /// use GID-based drawing (with Skia hinting) instead of path-based fallback.
        /// </summary>
        public bool IsOtfCff(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return false;
            GetTypeface(resourceName, resources, doc);
            return EntryOrNull(MakeKey(resources, resourceName))?.IsOtfCff ?? false;
        }

        /// <summary>
        /// Returns true when the embedded font program could not be decoded (e.g. unknown /Filter)
        /// and a system-font substitute was loaded instead.  In that case the PDF /Widths entries
        /// reflect the original font, not the substitute, so callers should use the substitute's
        /// own glyph metrics rather than the PDF widths.
        /// </summary>
        public bool IsSystemFallbackFont(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null) return false;
            GetTypeface(resourceName, resources, doc);
            return EntryOrNull(MakeKey(resources, resourceName))?.IsSystemFallback ?? false;
        }

        /// <summary>
        /// Returns true when the font resource carries an embedded font program
        /// (/FontFile, /FontFile2 or /FontFile3 in its /FontDescriptor — on the descendant
        /// CIDFont for Type0 fonts). The SVG renderer uses this to decide whether to
        /// vectorise glyph outlines (embedded fonts an SVG viewer would not have) or emit a
        /// plain, selectable &lt;text&gt; element (base-14 / non-embedded fonts the viewer
        /// already provides).
        /// </summary>
        public bool IsEmbedded(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null) return false;
            var resolved = doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict;

            var fdObj = resolved[PdfName.FontDescriptor];
            if (fdObj == null)
            {
                var descendant = GetDescendantFont(resolved, doc);
                if (descendant != null) fdObj = descendant[PdfName.FontDescriptor];
            }

            var fd = doc.ResolveObject(fdObj) as PdfDictionary;
            if (fd == null) return false;
            return fd[PdfName.FontFile]  != null
                || fd[PdfName.FontFile2] != null
                || fd[PdfName.FontFile3] != null;
        }

        private PdfDictionary GetDescendantFont(PdfDictionary fontDict, PdfDocument doc)
        {
            var da = fontDict[PdfName.DescendantFonts];
            if (da == null) return null;
            var arr = doc.ResolveObject(da) as PdfArray;
            if (arr == null || arr.Count == 0) return null;
            return doc.ResolveObject(arr[0]) as PdfDictionary;
        }

        // ================================================================
        //  Internal loading
        // ================================================================

        private SKTypeface LoadTypeface(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null)
            {
                // Font dict is unresolvable (e.g. stored in an ObjStm with an unknown /Filter).
                // Mark as system fallback so callers use SkiaSharp-measured glyph advances
                // rather than the PDF /Widths (which can't be read either).
                if (resourceName != null) Entry(MakeKey(resources, resourceName)).IsSystemFallback = true;
                return FallbackTypeface();
            }

            var resolved = doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict;

            // 1. Try to load from embedded font program.
            var embedded = TryLoadEmbeddedTypeface(resolved, doc, resourceName, resources);
            if (embedded != null) return embedded;

            // 2. Fall back to base font name → system font.
            // Record that this font key is using a substitute so callers can avoid applying
            // the PDF /Widths (which belong to the unloadable font, not the substitute).
            Entry(MakeKey(resources, resourceName)).IsSystemFallback = true;
            var baseFontName = (doc.ResolveObject(resolved[PdfName.BaseFont]) as PdfName)?.Value;

            // Carried out of the block below and into ResolveByName: /CIDSystemInfo /Ordering
            // names the script outright, which is a better substitution signal than guessing from
            // the base font name, so a non-embedded Adobe-Japan1 font with an opaque name does not
            // get a Western substitute and render as squares.
            string cidOrdering = null;

            // Warn when a CID font with a standard Adobe CID ordering has no embedded font
            // and no ToUnicode CMap.  Characters will render as boxes unless the caller has
            // registered the matching predefined CMap via CMapParser.RegisterPredefinedCMap.
            if ((doc.ResolveObject(resolved[PdfName.Subtype]) as PdfName)?.Value == PdfName.Type0.Value)
            {
                var descFont = GetDescendantFont(resolved, doc);
                if (descFont != null)
                {
                    var csInfo = descFont[PdfName.CIDSystemInfo] as PdfDictionary
                        ?? doc.ResolveObject(descFont[PdfName.CIDSystemInfo]) as PdfDictionary;
                    // Resolved: the ordering is what selects a CJK substitute, and read raw an
                    // `/Ordering 8 0 R` string left a Korean or Chinese font on a Western face.
                    string ordering = PdfResolve.Value(doc, csInfo?[PdfName.Ordering]) switch
                    {
                        Tetitulz.Pdf.Core.Objects.PdfString os => os.GetText(),
                        PdfName on => on.Value,
                        _ => null,
                    };

                    cidOrdering = ordering;

                    if (ordering != null)
                    {
                        // Check whether ANY of the accepted alias names for this ordering is registered.
                        string[] aliases = ordering switch
                        {
                            "Japan1"  => new[] { "Adobe-Japan1-UCS2", "UniJIS-UTF16-H",  "UniJIS-UCS2-H"  },
                            "GB1"     => new[] { "Adobe-GB1-UCS2",    "UniGB-UTF16-H",   "UniGB-UCS2-H"   },
                            "CNS1"    => new[] { "Adobe-CNS1-UCS2",   "UniCNS-UTF16-H",  "UniCNS-UCS2-H"  },
                            "Korea1"  => new[] { "Adobe-Korea1-UCS2", "UniKS-UTF16-H",   "UniKS-UCS2-H"   },
                            _         => System.Array.Empty<string>()
                        };
                        bool anyRegistered = false;
                        foreach (var a in aliases)
                            if (Tetitulz.Pdf.Core.Fonts.CMapParser.IsPredefinedCMapRegistered(a)) { anyRegistered = true; break; }

                        if (aliases.Length > 0 && !anyRegistered && resolved[PdfName.ToUnicode] == null)
                        {
                            // Build the download URL for the recommended CMap file
                            var (repoDir, fileName) = ordering switch
                            {
                                "Japan1"  => ("Adobe-Japan1-7/CMap", "UniJIS-UTF16-H"),
                                "GB1"     => ("Adobe-GB1-6/CMap",    "UniGB-UTF16-H"),
                                "CNS1"    => ("Adobe-CNS1-7/CMap",   "UniCNS-UTF16-H"),
                                "Korea1"  => ("Adobe-Korea1-2/CMap", "UniKS-UTF16-H"),
                                _         => ("", "")
                            };
                            Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.Report(
                                Tetitulz.Pdf.Base.Rendering.RenderDiagnosticKind.MissingPredefinedCMap,
                                $"Non-embedded CID font '{baseFontName}' (ordering={ordering}) has no /ToUnicode CMap. " +
                                $"CJK text will not render correctly. " +
                                $"Download '{fileName}' from https://github.com/adobe-type-tools/cmap-resources/tree/master/{repoDir} " +
                                $"and register at startup: CMapParser.RegisterPredefinedCMap(\"{fileName}\", bytes)");
                        }
                    }
                }
            }

            return ResolveByName(baseFontName, cidOrdering);
        }

        private SKTypeface TryLoadEmbeddedTypeface(PdfDictionary fontDict, PdfDocument doc, string resourceName, PdfDictionary resources)
        {
            // Try /FontDescriptor → /FontFile2 (TrueType) or /FontFile3 (CFF/OTF) or /FontFile (Type1)
            var fdObj = fontDict[PdfName.FontDescriptor];

            // For Type0 (composite) fonts, FontDescriptor is on the descendant CIDFont, not the Type0 dict.
            // This is critical for Hebrew/Arabic/CJK fonts that embed TrueType via CIDFontType2.
            if (fdObj == null)
            {
                var descendant = GetDescendantFont(fontDict, doc);
                if (descendant != null)
                    fdObj = descendant[PdfName.FontDescriptor];
            }

            if (fdObj == null) return null;

            var fd = doc.ResolveObject(fdObj) as PdfDictionary;
            if (fd == null) return null;

            foreach (var (nameKey, isCff) in new[] {
                (PdfName.FontFile2, false),
                (PdfName.FontFile3, true),
                (PdfName.FontFile,  false) }) // /FontFile = Type 1 PostScript, not CFF
            {
                var ffObj = fd[nameKey];
                if (ffObj == null) continue;
                if (doc.ResolveObject(ffObj) is not PdfStream ffStream) continue;
                try
                {
                    // Snapshot the unknown-filter name so we can detect if GetDecodedData
                    // encountered an unrecognised filter on this specific stream.
                    string filterNameBefore = Tetitulz.Pdf.Core.IO.Filters.FilterPipeline.LastUnknownFilterName;
                    byte[] data = Core.Fonts.FontStreamBytes.Program(ffStream);
                    string filterNameAfter  = Tetitulz.Pdf.Core.IO.Filters.FilterPipeline.LastUnknownFilterName;
                    bool hadUnknownFilter   = filterNameAfter != filterNameBefore && filterNameAfter != null;

                    if (data == null || data.Length < 4) continue;

                    // First try SkiaSharp — handles TrueType, OpenType, and most CFF.
                    // Require at least one glyph: Skia occasionally returns a non-null
                    // typeface for a truncated/corrupt program that has no usable glyphs,
                    // which would render as blanks; fall through to our own parser instead.
                    SKTypeface tf;
                    using (var skData = SKData.CreateCopy(data))
                        tf = SKTypeface.FromData(skData);
                    if (tf != null && tf.GlyphCount > 0)
                    {
                        // Skia will run this program's charstrings itself, under no budget of ours:
                        // a CFF one keeps its bytes so the draw can ask our evaluator first (F15).
                        if (resourceName != null && (isCff || IsOpenTypeCff(data)))
                            Entry(MakeKey(resources, resourceName)).HintingProbeProgram = data;
                        return tf;
                    }
                    tf?.Dispose();

                    // For /FontFile (Type 1 PostScript), SkiaSharp on Windows does
                    // not support Type 1 format.  Try two approaches:
                    //   (a) PFB wrapping: add sector headers so Skia can parse it.
                    //   (b) Our own Type 1 parser + path-based rendering fallback.
                    if (nameKey == PdfName.FontFile && !hadUnknownFilter)
                    {
                        // Resolved: /Length1..3 are written as indirect references by real
                        // producers just as /Length itself routinely is. Read raw they came back
                        // null -> 0, the `l1 > 0 && l2 > 0` guard below failed, and PFB wrapping
                        // was skipped entirely - so an embedded Type 1 font silently lost its
                        // best rendering path on exactly those files.
                        int l1 = PdfResolve.Int(doc, ffStream, PdfName.Length1, 0);
                        int l2 = PdfResolve.Int(doc, ffStream, PdfName.Length2, 0);
                        int l3 = PdfResolve.Int(doc, ffStream, PdfName.Length3, 0);
                        // PfbAssembler decides whether the lengths describe the data — null when not.
                        // This kept its own copy of that check, summed in int: /Length1 2147483647
                        // /Length2 1 overflowed past it, and the copy threw out of font loading.
                        byte[] pfb = WrapType1AsPfb(data, l1, l2, l3);
                        if (pfb != null)
                        {
                            // (a) PFB wrapping — works if Skia has the Type 1 driver
                            SKTypeface pfbTf;
                            using (var pfbData = SKData.CreateCopy(pfb))
                                pfbTf = SKTypeface.FromData(pfbData);
                            var t1font = Tetitulz.Pdf.Core.Fonts.Type1.Type1Parser.Parse(data, l1, l2);
                            if (pfbTf != null)
                            {
                                // Skia would run every charstring under no budget of ours: a program
                                // our evaluator finds heavy is drawn from its outlines instead (F15's rule).
                                if (t1font == null || !Type1GlyphSource.IsTooHeavyForHinting(t1font)) return pfbTf;
                                pfbTf.Dispose();
                                Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.Report(
                                    Tetitulz.Pdf.Base.Rendering.RenderDiagnosticKind.FontHintingSkipped,
                                    $"Embedded Type 1 font '{resourceName}' runs glyphs heavier than " +
                                    "PdfLimits.MaxCharstringStepsForHinting, or past PdfLimits.MaxCharstringStepsPerFont; " +
                                    "it is drawn from the library's own outlines, not through Skia.");
                            }

                            // (b) Our own parser's program, for path rendering
                            if (t1font != null && resourceName != null)
                            {
                                Entry(MakeKey(resources, resourceName)).Type1Source =
                                    new Type1GlyphSource(t1font);
                                // Return null SKTypeface so the renderer uses the Type1GlyphSource path.
                                // (The caller will fall through to the system-font name lookup which
                                //  is also fine — the Type 1 source takes precedence in DrawTextBytes.)
                            }
                            else if (t1font == null)
                            {
                                // Neither Skia (via PFB) nor our own Type 1 parser could read the
                                // embedded program; the renderer will substitute a system font, so glyph
                                // shapes/metrics may differ.  Surface it rather than failing silently.
                                Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.Report(
                                    Tetitulz.Pdf.Base.Rendering.RenderDiagnosticKind.FontLoadFailed,
                                    $"Embedded Type 1 font '{resourceName}' could not be parsed; using a system substitute.");
                            }
                        }
                    }

                    // SkiaSharp rejected the bytes. If an unknown filter was applied, the bytes
                    // are still encoded (passthrough).  Many real-world PDFs use a renamed
                    // filter (e.g. "XXXDecode") that is actually FlateDecode.  Try FlateDecode
                    // as a best-effort recovery before giving up.
                    if (hadUnknownFilter)
                    {
                        try
                        {
                            var flate = new Tetitulz.Pdf.Core.IO.Filters.FlateDecodeFilter();
                            byte[] inflated = flate.Decode(data, null);
                            if (inflated != null && inflated.Length >= 4)
                            {
                                SKTypeface tfInflated;
                                using (var skInflated = SKData.CreateCopy(inflated))
                                    tfInflated = SKTypeface.FromData(skInflated);
                                if (tfInflated != null) return tfInflated;
                            }
                        }
                        catch { }

                        Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.Report(
                            Tetitulz.Pdf.Base.Rendering.RenderDiagnosticKind.FontLoadFailed,
                            $"Font '{resourceName}' (/{nameKey.Value}) uses unknown filter '{filterNameAfter}'; " +
                            $"embedded data cannot be decoded.  Falling back to system font.");
                    }

                    // SkiaSharp failed.  For CFF/Type1, attempt our own CFF parser.
                    // Then try wrapping the raw CFF in an OTF envelope so Skia can load
                    // it with full Type 2 hinting.  If that also fails, store a CffGlyphSource
                    // for path-based fallback rendering.
                    if (isCff)
                    {
                        var cffFont = CffParser.Parse(data);
                        if (cffFont != null && resourceName != null)
                        {
                            // Apply PDF-level /Encoding override so that non-ASCII
                            // characters (e.g. WinAnsiEncoding chars 128-255) map to
                            // the correct GIDs rather than the CFF's internal encoding.
                            cffFont = ApplyPdfEncodingOverride(cffFont, fontDict, doc);
                            var entry = Entry(MakeKey(resources, resourceName));
                            entry.CffSource = new CffGlyphSource(cffFont);

                            // Try building an OTF wrapper so Skia's hinting engine
                            // is used instead of our path-only evaluator.
                            byte[] otfBytes = CffOtfWrapper.TryBuild(data, cffFont);
                            if (otfBytes != null)
                            {
                                SKTypeface otfTf;
                                using (var otfData = SKData.CreateCopy(otfBytes))
                                    otfTf = SKTypeface.FromData(otfData);
                                if (otfTf != null)
                                {
                                    entry.IsOtfCff = true;
                                    return otfTf;
                                }
                            }
                        }
                        else
                        {
                            // Report all CFF failures so callers can diagnose which fonts
                            // fell back to the system font and why.
                            string reason = CffParser.LastFailureReason == CffParser.CffFailure.UnsupportedFeature
                                ? "a CFF2 font program that could not be parsed (unsupported CFF2 sub-structure)"
                                : $"parse error ({CffParser.LastFailureReason})";
                            Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.Report(
                                Tetitulz.Pdf.Base.Rendering.RenderDiagnosticKind.FontLoadFailed,
                                $"CFF font '{resourceName}' failed to load: {reason}.  Falling back to system font.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Font loading failed; report it (per the project diagnostics contract)
                    // then continue so the renderer can fall back to the next font program
                    // key or the system-font fallback.
                    Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.Report(
                        Tetitulz.Pdf.Base.Rendering.RenderDiagnosticKind.FontLoadFailed,
                        $"Embedded font '{resourceName}' (/{nameKey.Value}) failed to load; " +
                        $"falling back to system font.", ex);
                }
            }
            return null;
        }

        /// <summary>
        /// Applies the PDF font dict's /Encoding to override the CFF's built-in char-code
        /// mapping.  This is necessary for fonts that use WinAnsiEncoding (or a custom
        /// /Differences encoding) whose non-ASCII chars differ from the CFF's internal
        /// Standard Encoding.  The CFF's glyph-name index must have been parsed for the
        /// override to take effect; if no useful mapping is found, the font is returned unchanged.
        ///
        /// Two strategies are tried and merged:
        ///  1. Glyph-name lookup: encoding table → glyph name → GlyphNameToGid.
        ///  2. ToUnicode bridge: /ToUnicode CMap gives charCode→Unicode; GlyphNameToGid
        ///     entries named "uniXXXX" / "uXXXXXX" are reverse-indexed to Unicode→GID.
        ///     Strategy 2 fills entries that strategy 1 missed (handles CFF subsets that
        ///     rename glyphs to "uni" + Unicode codepoint in hex, a common PDF generator
        ///     convention).
        /// </summary>
        internal static CffFont ApplyPdfEncodingOverride(CffFont font, PdfDictionary fontDict, PdfDocument doc)
        {
            if (font.GlyphNameToGid == null || font.GlyphNameToGid.Count == 0) return font;

            var encObj = fontDict[PdfName.Encoding];
            if (encObj == null) return font;

            // Named encoding: /WinAnsiEncoding, /MacRomanEncoding, etc.
            string encName = null;
            if (encObj is PdfName encPdfName)
                encName = encPdfName.Value;
            else if (doc.ResolveObject(encObj) is PdfName resolvedName)
                encName = resolvedName.Value;

            if (encName != null)
            {
                IReadOnlyList<string> nameTable = GetNamedEncodingTable(encName);
                if (nameTable == null) return font;

                var newMap = new Dictionary<int, int>();
                for (int code = 0; code < nameTable.Count; code++)
                {
                    string gname = nameTable[code];
                    if (gname == null || gname == ".notdef") continue;
                    if (font.GlyphNameToGid.TryGetValue(gname, out int gid))
                        newMap[code] = gid;
                }
                // Fill gaps via ToUnicode + "uniXXXX" reverse index.
                FillMissingViaToUnicode(newMap, font.GlyphNameToGid, fontDict, doc);
                if (newMap.Count > 0)
                    return font.WithCharCodeToGid(newMap);
                return font;
            }

            // Encoding dictionary with optional /Differences array.
            var encDict = doc.ResolveObject(encObj) as PdfDictionary;
            if (encDict == null) return font;

            // Start from the base encoding if specified.
            string baseEncName = (doc.ResolveObject(encDict[PdfName.BaseEncoding]) as PdfName)?.Value
                              ?? (encDict[PdfName.BaseEncoding] as PdfName)?.Value;
            // §9.6.6.1: with no /BaseEncoding, /Differences are differences from an implicit base,
            // and for an EMBEDDED program — which this always is — that base is the program's own
            // built-in encoding, not StandardEncoding. A subset CFF with a custom encoding drew
            // StandardEncoding's glyph for every code /Differences did not name.
            IReadOnlyList<string> baseTable = baseEncName != null ? GetNamedEncodingTable(baseEncName) : null;

            var map = new Dictionary<int, int>();
            if (baseEncName == null)
            {
                foreach (var kv in font.CharCodeToGid) map[kv.Key] = kv.Value;
            }
            else if (baseTable != null)
            {
                for (int code = 0; code < baseTable.Count; code++)
                {
                    string gname = baseTable[code];
                    if (gname == null || gname == ".notdef") continue;
                    if (font.GlyphNameToGid.TryGetValue(gname, out int gid))
                        map[code] = gid;
                }
            }

            // Apply /Differences: [startCode glyphName glyphName …  startCode …]
            var diffsObj = encDict[PdfName.Differences];
            if (diffsObj != null)
            {
                var diffs = doc.ResolveObject(diffsObj) as PdfArray;
                if (diffs != null)
                {
                    // Build uniXXXX reverse map once for Unicode-named glyph fallback.
                    var unicodeToGidFromNames = BuildUnicodeToGidFromNames(font.GlyphNameToGid);

                    int currentCode = 0;
                    foreach (var item in diffs)
                    {
                        if (item is PdfNumber num)
                        {
                            currentCode = num.IntValue;
                        }
                        else if (item is PdfName dname)
                        {
                            if (dname.Value != ".notdef")
                            {
                                if (font.GlyphNameToGid.TryGetValue(dname.Value, out int gid))
                                {
                                    map[currentCode] = gid;
                                }
                                else if (unicodeToGidFromNames.Count > 0 &&
                                         Tetitulz.Pdf.Core.Fonts.StandardEncodings.TryGetGlyphUnicode(
                                             dname.Value, out char uc) &&
                                         unicodeToGidFromNames.TryGetValue((int)uc, out int gid2))
                                {
                                    // Glyph name uses a non-uniXXXX convention (e.g. afii57664) but
                                    // the embedded CFF uses uniXXXX names — bridge via Unicode codepoint.
                                    map[currentCode] = gid2;
                                }
                            }
                            currentCode++;
                        }
                    }
                }
            }

            // Fill gaps via ToUnicode + "uniXXXX" reverse index.
            FillMissingViaToUnicode(map, font.GlyphNameToGid, fontDict, doc);

            return map.Count > 0 ? font.WithCharCodeToGid(map) : font;
        }

        /// <summary>
        /// Fills missing entries in <paramref name="map"/> by bridging the font's /ToUnicode
        /// CMap (charCode → Unicode) with a reverse index built from "uniXXXX"/"uXXXXXX"
        /// glyph names in <paramref name="glyphNameToGid"/> (Unicode codepoint → GID).
        /// Only char codes not already present in <paramref name="map"/> are added.
        /// </summary>
        private static void FillMissingViaToUnicode(
            Dictionary<int, int> map,
            IReadOnlyDictionary<string, int> glyphNameToGid,
            PdfDictionary fontDict,
            PdfDocument doc)
        {
            var unicodeToGid = BuildUnicodeToGidFromNames(glyphNameToGid);
            if (unicodeToGid.Count == 0) return;

            var tuObj = fontDict[PdfName.ToUnicode];
            if (tuObj == null) return;
            if (doc.ResolveObject(tuObj) is not PdfStream tuStream) return;
            byte[] cmapData = Core.Fonts.FontStreamBytes.CMap(tuStream);
            if (cmapData == null || cmapData.Length == 0) return;

            var charCodeToUnicode = Core.Fonts.CMapParser.Parse(cmapData);
            foreach (var kv in charCodeToUnicode)
            {
                int charCode = kv.Key;
                if (map.ContainsKey(charCode)) continue;
                string unicodeStr = kv.Value;
                if (unicodeStr == null || unicodeStr.Length == 0) continue;
                // A lone surrogate (a bfchar of <D800>) names no character. ConvertToUtf32 throws
                // on it, and the load's catch then discarded the whole embedded program for one
                // bad /ToUnicode entry.
                if (char.IsSurrogate(unicodeStr[0]) && !char.IsSurrogatePair(unicodeStr, 0)) continue;
                int cp = char.ConvertToUtf32(unicodeStr, 0);
                if (unicodeToGid.TryGetValue(cp, out int gid))
                    map[charCode] = gid;
            }
        }

        /// <summary>
        /// Builds a Unicode-codepoint → GID reverse index from glyph names that follow the
        /// "uniXXXX" (4 BMP hex digits) or "uXXXXXX" (5-6 supplementary hex digits) convention.
        /// Standard-name glyphs (e.g. "A", "eacute") are not included here — they are handled
        /// by the direct glyph-name table lookup in <see cref="ApplyPdfEncodingOverride"/>.
        /// Internal for unit testing.
        /// </summary>
        internal static Dictionary<int, int> BuildUnicodeToGidFromNames(
            IReadOnlyDictionary<string, int> glyphNameToGid)
        {
            var result = new Dictionary<int, int>();
            foreach (var kv in glyphNameToGid)
            {
                // The AGL numeric forms only — the standard glyph-name table is deliberately NOT
                // consulted here. A font naming a glyph "A" is describing its own inventory, not
                // declaring that GID as U+0041; that bridge is ApplyPdfEncodingOverride's job.
                //
                // StandardEncodings' classification, not a private copy, because the two meet inside
                // ApplyPdfEncodingOverride, which joins both answers on one int key. "uniXXXXYYYY"
                // is now refused rather than truncated to its first four digits: that form is a
                // code-point SEQUENCE, and mapping U+0041 to a glyph drawing "AB" made a page that
                // asked for "A" render "AB".
                if (StandardEncodings.TryGetAglCodePoint(kv.Key, out int cp) && !result.ContainsKey(cp))
                    result[cp] = kv.Value;
            }
            return result;
        }

        /// <summary>
        /// The §9.6.6 named simple-font encoding as a char code → glyph name table, or <c>null</c>
        /// for a name that is not one of the four.
        /// </summary>
        /// <remarks>
        /// <para>Returns <see cref="IReadOnlyList{T}"/> rather than <c>string[]</c> because the
        /// StandardEncoding table is Core's single shared instance: handing the array itself out
        /// would let a caller write through it and change what every other reader sees.</para>
        ///
        /// <para><c>internal</c> for the test that pins which table each name selects.
        /// StandardEncoding and WinAnsi differ at 0x27 and 0x60, and a mix-up there is silent —
        /// the wrong glyph draws, nothing fails.</para>
        /// </remarks>
        internal static IReadOnlyList<string> GetNamedEncodingTable(string name)
            => SimpleFontEncodingNames.Named(name);

        // The code → glyph-name tables are Core's (Core.Fonts.SimpleFontEncodingNames), where the
        // conformance checkers and the font remediator read them too; these names stay for the
        // renderer and Type3Encoding.
        internal static readonly string[] WinAnsiNames = SimpleFontEncodingNames.WinAnsi;
        internal static readonly string[] MacRomanNames = SimpleFontEncodingNames.MacRoman;

        internal static readonly string[] MacExpertNames = SimpleFontEncodingNames.MacExpert;

        // Candidate fonts with comprehensive dingbats / symbol coverage,
        // tried in order for ZapfDingbats/ZaDb when the named font isn't on the system.
        // We pick the first one whose GetGlyph(0x2713) returns a valid GID.
        private static readonly string[] DingbatsFallbacks =
        {
            StandardFonts.ZapfDingbats, // if user has it installed
            "Segoe UI Symbol", // Windows 7+
            "Apple Symbols",   // macOS
            "Symbola",         // Linux (if installed)
            "Noto Sans Symbols",
            "DejaVu Sans",
        };

        /// <param name="baseFontName">The font's <c>/BaseFont</c>, subset tag included.</param>
        /// <param name="cidOrdering">
        /// The descendant font's <c>/CIDSystemInfo /Ordering</c> (<c>Japan1</c>, <c>GB1</c>,
        /// <c>CNS1</c>, <c>Korea1</c>), or null for a font that declares none.
        /// </param>
        /// <remarks>
        /// <b>The ordering is a better signal than the name.</b>
        /// <see cref="LooksLikeCjkFont"/> keys on substrings of the base font name, so a CID font
        /// with an opaque or subset-tagged name missed the CJK path entirely and fell into the
        /// Western name-derivation heuristics — which return a DirectWrite substitute that has no
        /// CJK glyphs, i.e. squares. The ordering says which script it is outright, and
        /// <c>LoadTypeface</c> has already read it a few lines above (it was feeding the
        /// <c>MissingPredefinedCMap</c> diagnostic and nothing else).
        /// </remarks>
        private static SKTypeface ResolveByName(string baseFontName, string cidOrdering = null)
        {
            string orderingScript = CjkScriptFromOrdering(cidOrdering);
            if (baseFontName == null)
                return orderingScript != null
                    ? ResolveCjkSystemFont(null, orderingScript) ?? FallbackTypeface()
                    : FallbackTypeface();

            // Strip subset prefix (e.g. "ABCDEF+Arial" → "Arial"), through the one §9.6.4 rule.
            // A bare "ABCDEF+" does not become the EMPTY STRING, which would reach
            // SKTypeface.FromFamilyName("") and silently become the platform default face.
            baseFontName = SubsetTag.Strip(baseFontName);

            // ZapfDingbats / ZaDb: pick the first system font that actually has U+2713
            // (CHECK MARK), since "ZapfDingbats" is not a built-in Windows/Mac system font.
            // We verify by GID probe so we don't silently use a font that lacks the glyphs.
            if (baseFontName == StandardFonts.ZapfDingbats ||
                baseFontName == StandardFonts.ZapfDingbatsAbbrev)
            {
                foreach (string candidate in DingbatsFallbacks)
                {
                    var tf2 = SKTypeface.FromFamilyName(candidate);
                    if (GlyphIdResolver.Glyph(tf2, 0x2713) != 0)
                        return tf2;
                    tf2?.Dispose();
                }

                // Same probe against font files, for hosts with no platform font manager to ask.
                // The GID check still applies — a symbol font without U+2713 is no use here.
                foreach (string candidate in DingbatsFallbacks)
                {
                    var tf2 = ResolveFromFontFile(candidate, bold: false, italic: false);
                    if (GlyphIdResolver.Glyph(tf2, 0x2713) != 0)
                        return tf2;
                    tf2?.Dispose();
                }
                return FallbackTypeface();
            }

            // Check standard 14 mapping. This is a GATE: a name that resolves here takes the
            // substitute branch, and anything else falls through to the CJK fast path and the
            // name-derivation heuristics below — which is why the shared helper deliberately does
            // not canonicalise aliases like "ArialMT" on the caller's behalf.
            string family = Standard14Names.SystemFamily(baseFontName);
            if (family != null)
            {
                bool bold    = FontNameStyle.IsBold(baseFontName);
                bool italic  = FontNameStyle.IsItalic(baseFontName);
                var style    = StyleNamedBy(baseFontName);

                SKTypeface std = SKTypeface.FromFamilyName(family, style);
                if (IsFamilyMatch(std, family)) return std;

                // The families above are the Windows names, so this misses on a bare Linux
                // container — where the file lookup finds the metric-compatible Liberation
                // substitute instead, keeping the PDF /Widths meaningful.
                SKTypeface fromFile = ResolveFromFontFile(family, bold, italic);
                if (fromFile != null)
                {
                    std?.Dispose();
                    return fromFile;
                }

                return std ?? FallbackTypeface();
            }

            // CJK fast path: for CJK-named fonts, bypass the Western name-derivation
            // heuristics (dash-split, PsNameToFamily) which can return a non-CJK
            // DirectWrite substitute for unknown CJK family names, blocking the real
            // CJK system font fallback and producing squares for all Japanese text.
            // Try direct lookup first (in case the font is actually installed), then
            // fall back to known CJK system fonts, both with U+4E00 glyph verification.
            if (orderingScript != null || LooksLikeCjkFont(baseFontName))
            {
                var cjkDirect = SKTypeface.FromFamilyName(baseFontName);
                if (GlyphIdResolver.Glyph(cjkDirect, 0x4E00) != 0) return cjkDirect;
                cjkDirect?.Dispose();
                return ResolveCjkSystemFont(baseFontName, orderingScript) ?? FallbackTypeface();
            }

            // Non-CJK path: attempt direct lookup then PS name-derivation heuristics.
            //
            // NOTE the match test rather than a null test. On Windows (DirectWrite)
            // SKTypeface.FromFamilyName NEVER returns null for an unknown family — it returns the
            // system default, Segoe UI. Accepting that substitute as a successful lookup would make
            // the whole PostScript-name derivation that follows unreachable on Windows: "ArialMT", "TimesNewRomanPSMT" and "CourierNewPSMT" — the
            // standard PostScript names carried by a large share of real PDFs — all rendered as
            // Segoe UI instead of Arial, Times New Roman and Courier New.
            SKTypeface substitute = null;

            SKTypeface tf = SKTypeface.FromFamilyName(baseFontName);
            if (IsFamilyMatch(tf, baseFontName)) return tf;
            substitute = tf;

            // PDF PostScript font names often embed the style as a suffix after the last '-'
            // (e.g. "FreestyleScript-Regular", "TimesNewRoman-BoldItalic").
            // When the full name doesn't resolve, try the family part alone so the system
            // font is found even if its registered family name omits the style component.
            int dashIdx = baseFontName.LastIndexOf('-');
            if (dashIdx > 0)
            {
                string familyPart = baseFontName.Substring(0, dashIdx);
                string stylePart  = baseFontName.Substring(dashIdx + 1);
                var style = StyleNamedBy(stylePart);
                tf = SKTypeface.FromFamilyName(familyPart, style);
                if (IsFamilyMatch(tf, familyPart)) return tf;
                substitute ??= tf;

                // Also try de-camelCasing the family part after stripping PS suffixes.
                // Handles e.g. "TimesNewRomanPS-BoldMT" → "Times New Roman" bold.
                string spacedFamily = PsNameToFamily(familyPart);
                if (spacedFamily != familyPart)
                {
                    tf = SKTypeface.FromFamilyName(spacedFamily, style);
                    if (IsFamilyMatch(tf, spacedFamily)) return tf;
                    substitute ??= tf;
                }
            }

            // Last resort: de-camelCase the whole name after stripping PostScript suffixes.
            // Handles e.g. "TimesNewRomanPSMT" → "Times New Roman",
            //              "ArialMT" → "Arial",  "CourierNewPSMT" → "Courier New".
            {
                string spaced = PsNameToFamily(baseFontName);
                if (spaced != baseFontName)
                {
                    tf = SKTypeface.FromFamilyName(spaced);
                    if (IsFamilyMatch(tf, spaced)) return tf;
                    substitute ??= tf;
                }
            }

            // No platform match by any spelling. Try to find the family as a file before settling
            // for a substitute: an exact filename hit is the font that was asked for, whereas
            // `substitute` is by definition the platform's guess for a name it did not recognise.
            {
                bool bold   = FontNameStyle.IsBold(baseFontName);
                bool italic = FontNameStyle.IsItalic(baseFontName);

                SKTypeface fromFile = ResolveFromFontFile(baseFontName, bold, italic);
                if (fromFile == null)
                {
                    string spaced = PsNameToFamily(baseFontName);
                    if (spaced != baseFontName)
                        fromFile = ResolveFromFontFile(spaced, bold, italic);
                }
                if (fromFile != null)
                {
                    substitute?.Dispose();
                    return fromFile;
                }
            }

            // Nothing matched by name. The platform substitute is still better than nothing —
            // it is what a null test would return unconditionally.
            if (substitute != null) return substitute;

            return FallbackTypeface();
        }

        /// <summary>
        /// Maps a <c>/CIDSystemInfo /Ordering</c> to the script it names, or null when the
        /// ordering is absent or is not one of the four Adobe CJK registries.
        /// </summary>
        /// <remarks>
        /// <b>Only the four CJK orderings return non-null.</b> <c>Identity</c> is deliberately not
        /// among them: it declares nothing about the script — it says the CIDs are glyph indices —
        /// so treating it as CJK would push every Identity-encoded Latin CID font down the CJK
        /// path. The registry half (<c>Adobe-</c>) is not examined, because <c>/Ordering</c> is the
        /// entry that names the character collection.
        /// </remarks>
        internal static string CjkScriptFromOrdering(string ordering) => ordering switch
        {
            "Japan1" => "ja",
            "GB1"    => "zh-Hans",
            "CNS1"   => "zh-Hant",
            "Korea1" => "ko",
            _        => null,
        };

        private static bool LooksLikeCjkFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            // Japanese identifiers
            if (n.Contains("hira") || n.Contains("mincho") || n.Contains("gothic") ||
                n.Contains("meiryo") || n.Contains("kochi") || n.Contains("ipaex") ||
                n.Contains("ipafont") || n.Contains("ipa ex") || n.Contains("morisawa") ||
                n.Contains("kozuka") || n.Contains("source han") || n.Contains("yumin") ||
                n.Contains("yugoth") || n.Contains("yu min") || n.Contains("yu goth") ||
                n.Contains("ms mincho") || n.Contains("ms gothic") || n.Contains("heiseikaku"))
                return true;
            // Chinese identifiers
            if (n.Contains("simsun") || n.Contains("simhei") || n.Contains("fangso") ||
                n.Contains("kaiti") || n.Contains("songti") || n.Contains("heiti") ||
                n.Contains("yahei") || n.Contains("hei") && (n.Contains("cn") || n.Contains("tc") || n.Contains("sc")) ||
                n.Contains("noto") && (n.Contains("cjk") || n.Contains("sc") || n.Contains("tc") || n.Contains("jp") || n.Contains("kr")))
                return true;
            // Korean identifiers
            if (n.Contains("batang") || n.Contains("gulim") || n.Contains("dotum") ||
                n.Contains("malgun") || n.Contains("nanum"))
                return true;
            return false;
        }

        /// <param name="hint">The base font name, used only when <paramref name="script"/> is null.</param>
        /// <param name="script">
        /// The script named by <c>/CIDSystemInfo /Ordering</c> — <c>"ja"</c>, <c>"zh-Hans"</c>,
        /// <c>"zh-Hant"</c> or <c>"ko"</c> — or null to fall back to guessing from
        /// <paramref name="hint"/>.
        /// </param>
        /// <remarks>
        /// <b>The ordering wins where it is present</b>, because it is a declaration and the name
        /// is a guess. It also separates the two Chinese cases, which the name heuristic cannot:
        /// <c>GB1</c> is Simplified and <c>CNS1</c> is Traditional, and a Simplified face renders
        /// Traditional text with the wrong glyph shapes rather than with blanks — wrong in the
        /// quiet direction.
        /// </remarks>
        private static SKTypeface ResolveCjkSystemFont(string hint, string script = null)
        {
            string hn = hint?.ToLowerInvariant() ?? "";
            bool isKorean  = script == "ko" || (script == null && (hn.Contains("batang") || hn.Contains("gulim") || hn.Contains("dotum") || hn.Contains("malgun") || hn.Contains("nanum")));
            bool isTraditional = script == "zh-Hant";
            bool isChinese = script == "zh-Hans" || isTraditional
                          || (script == null && (hn.Contains("simsun") || hn.Contains("simhei") || hn.Contains("fangso") || hn.Contains("songti") || hn.Contains("heiti") || hn.Contains("yahei") || hn.Contains("kaiti")));

            // Build candidate list ordered by best match first
            string[] japanese = {
                "Meiryo", "Yu Gothic", "MS Gothic", "MS Mincho", "Yu Mincho",
                "Hiragino Mincho Pro", "Hiragino Kaku Gothic Pro",
                "Noto Serif CJK JP", "Noto Sans CJK JP",
                "Source Han Serif JP", "Source Han Sans JP",
                "IPA Mincho", "IPA Gothic", "Kochi Mincho"
            };
            string[] chinese = {
                "Microsoft YaHei", "SimSun", "SimHei", "NSimSun", "FangSong",
                "KaiTi", "STSong", "STHeiti",
                "Noto Serif CJK SC", "Noto Sans CJK SC",
                "Source Han Serif CN", "Source Han Sans CN"
            };
            string[] korean = {
                "Malgun Gothic", "Batang", "Gulim", "Dotum", "BatangChe",
                "Noto Serif CJK KR", "Noto Sans CJK KR",
                "Source Han Serif KR", "Source Han Sans KR"
            };

            // Traditional Chinese had no candidates at all before the ordering could name it:
            // every entry in `chinese` above is a Simplified face.
            string[] traditional = {
                "Microsoft JhengHei", "PMingLiU", "MingLiU", "DFKai-SB",
                "Noto Serif CJK TC", "Noto Sans CJK TC",
                "Source Han Serif TW", "Source Han Sans TW"
            };

            IEnumerable<string[]> groups =
                isKorean       ? new[] { korean, japanese, chinese } :
                isTraditional  ? new[] { traditional, chinese, japanese, korean } :
                isChinese      ? new[] { chinese, traditional, japanese, korean } :
                                 new[] { japanese, chinese, korean };

            foreach (var group in groups)
                foreach (var family in group)
                {
                    var candidate = SKTypeface.FromFamilyName(family);
                    // Verify the resolved typeface actually has CJK glyphs — DirectWrite may
                    // return a non-CJK substitute (e.g. Segoe UI) for installed-but-mismatched
                    // font names.  U+4E00 (一) is the first CJK Unified Ideograph and is present
                    // in every real CJK font.
                    if (GlyphIdResolver.Glyph(candidate, 0x4E00) != 0) return candidate;
                    candidate?.Dispose();
                }

            // Second pass against font files. The Noto / Source Han entries above are the ones
            // that actually exist off Windows, and they are the ones the platform font manager
            // cannot report when there is no fontconfig to ask.
            foreach (var group in groups)
                foreach (var family in group)
                {
                    var candidate = ResolveFromFontFile(family, bold: false, italic: false);
                    if (GlyphIdResolver.Glyph(candidate, 0x4E00) != 0) return candidate;
                    candidate?.Dispose();
                }
            return null;
        }

        /// <summary>
        /// True when <paramref name="tf"/> is really the family that was asked for, rather than the
        /// platform's substitute for an unknown name.
        ///
        /// <para>Needed because <c>SKTypeface.FromFamilyName</c> does not report failure on
        /// Windows: an unknown family yields the system default with a non-null handle. The
        /// comparison is <see cref="FontFileLocator.IsFamilyMatch"/>'s — the one implementation,
        /// shared with <c>SystemFontResolver</c> and the Accessibility font remediator; this type
        /// held its own normaliser, which kept '.' where the shared one drops it.</para>
        /// </summary>
        private static bool IsFamilyMatch(SKTypeface tf, string requested)
            => tf != null && FontFileLocator.IsFamilyMatch(tf.FamilyName, requested);

        /// <summary>
        /// The platform style a font name asks for, by the one reading of a name
        /// (<see cref="FontNameStyle"/>) — the reading that also picks a standard-14 font's widths, so
        /// a face drawn here is the face it is measured as.
        /// </summary>
        internal static SKFontStyle StyleNamedBy(string name)
        {
            bool bold = FontNameStyle.IsBold(name), italic = FontNameStyle.IsItalic(name);
            return bold && italic ? SKFontStyle.BoldItalic
                 : bold           ? SKFontStyle.Bold
                 : italic         ? SKFontStyle.Italic
                 :                  SKFontStyle.Normal;
        }

        /// <summary>
        /// Converts a PostScript/PDF font name to the system font family name it most
        /// likely corresponds to.  Strips common PostScript suffixes (PSMT, MT, PS)
        /// then inserts spaces at camelCase boundaries so e.g.
        /// "TimesNewRomanPSMT" → "Times New Roman" and "CourierNewPSMT" → "Courier New".
        /// Returns the original string unchanged when no transformation was possible.
        /// </summary>
        private static string PsNameToFamily(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // Strip PostScript font suffixes added by Monotype and other foundries.
            string stripped = name;
            foreach (string suf in new[] { "PSMT", "MT", "PS" })
            {
                if (stripped.EndsWith(suf, System.StringComparison.OrdinalIgnoreCase)
                    && stripped.Length > suf.Length)
                {
                    stripped = stripped.Substring(0, stripped.Length - suf.Length);
                    break;
                }
            }

            // Insert a space before each uppercase letter that immediately follows a
            // lowercase letter or digit — this converts PascalCase font names like
            // "TimesNewRoman" into the system family name "Times New Roman".
            var sb = new System.Text.StringBuilder(stripped.Length + 4);
            for (int i = 0; i < stripped.Length; i++)
            {
                if (i > 0
                    && char.IsUpper(stripped[i])
                    && (char.IsLower(stripped[i - 1]) || char.IsDigit(stripped[i - 1])))
                    sb.Append(' ');
                sb.Append(stripped[i]);
            }
            return sb.ToString();
        }

        private PdfFont LoadPdfFont(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null) return new FallbackFont();
            PdfFont font = PdfFont.Create(fontDict, doc);
            string unread = PdfFont.LastToUnicodeError;
            if (unread != null)
                RenderDiagnostics.Report(RenderDiagnosticKind.FontToUnicodeUnread, resourceName + ": " + unread);
            return font;
        }

        /// <summary>
        /// Returns the vertical advance width (in em units, i.e. 0–1) for the given CID char code
        /// by reading /DW2 (default) and /W2 (per-CID overrides) from the CIDFont dictionary.
        /// Falls back to <paramref name="defaultEmAdvance"/> when no entry is found.
        /// </summary>
        /// <remarks>
        /// A thin projection of <see cref="GetVerticalMetrics"/>, which also carries the position
        /// vector this discards. Kept because the advance is what most callers want.
        /// </remarks>
        public float GetVerticalAdvance(string resourceName, int charCode,
            PdfDictionary resources, PdfDocument doc, float defaultEmAdvance)
            => GetVerticalMetrics(resourceName, charCode, resources, doc, defaultEmAdvance).AdvanceEm;

        /// <summary>
        /// The <b>full</b> vertical writing metrics of one CID (§9.7.4.3, Table 117): the vertical
        /// displacement <c>w1y</c> AND the position vector <c>v</c> that maps the glyph's horizontal
        /// origin onto its vertical one.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the vector matters.</b> In vertical writing the glyph is placed at
        /// <c>currentPoint − v</c>, not at the current point: <c>v</c> is what centres a CJK glyph
        /// on the column and lifts it to the vertical baseline. Reading only <c>w1y</c> — which is
        /// all <see cref="GetVerticalAdvance"/> ever exposed — advances the pen correctly and then
        /// draws every glyph on the <i>horizontal</i> origin.</para>
        /// <para>Defaults per Table 117: <c>/DW2</c> is <c>[880 −1000]</c>, and where <c>/W2</c>
        /// gives no entry the position vector is <c>(w0 / 2, DW2[0])</c> — the horizontal half-width
        /// of that glyph, so it needs the horizontal metrics too.</para>
        /// </remarks>
        public VerticalGlyphMetrics GetVerticalMetrics(string resourceName, int charCode,
            PdfDictionary resources, PdfDocument doc, float defaultEmAdvance)
        {
            float defaultVy = CidVerticalMetrics.DefaultPositionY;
            float defaultVx = HorizontalHalfWidth(resourceName, charCode, resources, doc);

            var fontDict = LookupFontDict(resourceName, resources, doc);
            if (fontDict == null)
                return VerticalGlyphMetrics.FromGlyphUnits(defaultEmAdvance, defaultVx, defaultVy, false);

            var resolved  = doc.ResolveObject(fontDict) as PdfDictionary ?? fontDict;
            var cidFont   = GetDescendantFont(resolved, doc) ?? resolved;

            // DW2 = [vy w1y].  w1y is the y-displacement per glyph in glyph units; per spec it is
            // negative (downward in PDF Y-up space) and callers use the absolute magnitude
            // (positive = one em of downward advance).  DW2[0] is the position vector's y — read
            // into a comment for years and never applied, which is the defect this closes.
            // The elements resolved, as /W's are (PdfType0Font.ParseWArray takes the resolver):
            // read as stored, a `/DW2 [880 7 0 R]` fell to the −1000 default and a /W2 entry written
            // with referenced numbers was passed over, in this cache and in the PDF/UA-2 check alike.
            if (doc.ResolveObject(cidFont[PdfName.DW2]) is PdfArray dw2 && dw2.Count >= 2)
            {
                if (doc.ResolveObject(dw2[0]) is PdfNumber vy) defaultVy = (float)vy.DoubleValue;
                double vw = (doc.ResolveObject(dw2[1]) as PdfNumber)?.DoubleValue ?? -PdfUnits.GlyphSpaceUnitsPerEm;
                defaultEmAdvance = (float)(Math.Abs(vw) / PdfUnits.GlyphSpaceUnitsPerEm);
            }

            // W2: per-CID vertical metrics — same two formats as /W, but each entry carries the
            // position vector as well as the advance. The font model's index where the font is one
            // (PdfType0Font reads /W2 once, when it is made, for the extractor's step), else read
            // once here for the life of the cache.
            CidVerticalMetrics.Index index = (GetPdfFont(resourceName, resources, doc) as PdfType0Font)?.VerticalMetrics;
            if (index == null && doc.ResolveObject(cidFont[PdfName.W2]) is PdfArray w2)
                index = VerticalMetricsOf(w2, doc);
            if (index != null && index.TryFind(charCode, out float w1y, out float v1x, out float v1y))
            {
                return VerticalGlyphMetrics.FromGlyphUnits(
                    (float)(Math.Abs(w1y) / PdfUnits.GlyphSpaceUnitsPerEm), v1x, v1y, true);
            }

            return VerticalGlyphMetrics.FromGlyphUnits(defaultEmAdvance, defaultVx, defaultVy, false);
        }

        // Each /W2 array read once for the life of the cache: it was scanned for every glyph shown.
        private readonly Dictionary<PdfArray, CidVerticalMetrics.Index> _verticalMetrics =
            new Dictionary<PdfArray, CidVerticalMetrics.Index>();

        private CidVerticalMetrics.Index VerticalMetricsOf(PdfArray w2, PdfDocument doc)
        {
            if (!_verticalMetrics.TryGetValue(w2, out var index))
                _verticalMetrics[w2] = index = CidVerticalMetrics.Index.Read(w2, doc.ResolveObject);
            return index;
        }

        /// <summary>
        /// Half the glyph's <b>horizontal</b> displacement w0, which Table 117 makes the default
        /// position-vector x. Half an em when there is no font to ask.
        /// </summary>
        private float HorizontalHalfWidth(string resourceName, int charCode,
            PdfDictionary resources, PdfDocument doc)
        {
            var font = GetPdfFont(resourceName, resources, doc);
            if (font == null) return (float)PdfUnits.GlyphSpaceUnitsPerEm / 2f;
            // A composite font's width is /W's, else /DW's, else 1000 (§9.7.4.3) — always the
            // file's or the standard's. A zero here is a declared /DW 0, a real width whose half is
            // 0; a half-em substitute would put such a glyph half an em off its column.
            return (float)(font.GetWidth(charCode) / 2.0);
        }

        /// <remarks>
        /// <b>All three arguments are guarded, and the <c>resourceName</c> one is not defensive
        /// padding.</b> <c>PdfName</c>'s constructor throws <c>ArgumentNullException</c>, so
        /// <c>new PdfName(null)</c> below turned a missing font name into an exception out of
        /// <c>IsEmbedded</c> — which the SVG writer calls with <c>_gs.FontName</c>, null before the
        /// first <c>Tf</c> and after a malformed one. The sibling of this method in
        /// <c>PdfStructuredTextExtractor</c> guarded all three from the start; this copy guarded
        /// one, and a content stream that shows text without setting a font crashed the export.
        /// </remarks>
        private PdfDictionary LookupFontDict(string resourceName, PdfDictionary resources, PdfDocument doc)
        {
            if (resourceName == null || resources == null || doc == null) return null;
            var fontRes = resources[PdfName.Font];
            if (fontRes == null) return null;
            var fontDict = doc.ResolveObject(fontRes) as PdfDictionary;
            if (fontDict == null) return null;
            var entry = fontDict[new PdfName(resourceName)];
            if (entry == null) return null;
            return doc.ResolveObject(entry) as PdfDictionary ?? entry as PdfDictionary;
        }

        // ================================================================
        //  Type 1 PFB wrapping
        // ================================================================

        /// <summary>
        /// Wraps raw Type 1 PostScript data (as stored in a PDF /FontFile stream:
        /// <paramref name="l1"/> ASCII bytes followed by <paramref name="l2"/>
        /// binary-encrypted bytes) into a valid PFB container with sector headers.
        /// Skia requires these markers to distinguish the text and binary sections.
        /// </summary>
        /// <remarks>
        /// <b>The assembler is <see cref="Core.Fonts.PfbAssembler"/></b>, shared with the font
        /// exporter, which needs the same three-segment wrapping — a <c>/FontFile</c> is not a
        /// <c>.pfb</c>, and handing a caller the stream's bytes produces a file nothing opens. It is
        /// in Core as a leaf rather than being called sideways off this type, which has a static
        /// initializer.
        /// </remarks>
        private static byte[] WrapType1AsPfb(byte[] raw, int l1, int l2, int l3)
            => Core.Fonts.PfbAssembler.Build(raw, l1, l2, l3);

        // ================================================================
        //  Fallbacks
        // ================================================================

        /// <summary>
        /// Loads a typeface from a font file on disk, bypassing the platform font manager, or
        /// returns <c>null</c> when no file matches.
        ///
        /// <para>Every other resolution path here goes through <c>SKTypeface.FromFamilyName</c>,
        /// which asks DirectWrite / fontconfig / CoreText. That answers well on a desktop and
        /// answers with nothing on a bare Linux container, where none of the Windows families the
        /// tables below name are installed. Matching a real file gives the same answer everywhere
        /// the fonts exist.</para>
        ///
        /// <para><b>The result is deliberately not cached.</b> <see cref="Dispose"/> disposes every
        /// typeface stored in a <see cref="FontEntry"/>, so a process-shared instance would be
        /// destroyed by whichever <see cref="FontCache"/> finished first and every later render
        /// would get a disposed handle. A fresh <c>FromData</c> per call keeps ownership exactly as
        /// it is for the <c>FromFamilyName</c> paths. <see cref="FontFileLocator"/> memoises the
        /// path lookup, so the repeated cost is one file read.</para>
        /// </summary>
        private static SKTypeface ResolveFromFontFile(string family, bool bold, bool italic)
        {
            if (string.IsNullOrEmpty(family)) return null;

            string path;
            try
            {
                path = FontFileLocator.FindFamilyFile(family, bold, italic);
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                    $"scanning system font directories for '{family}' failed", ex);
                return null;
            }
            if (path == null) return null;

            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                using var data = SKData.CreateCopy(bytes);
                return SKTypeface.FromData(data);
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                    $"could not load system font file '{path}' for family '{family}'", ex);
                return null;
            }
        }

        /// <summary>
        /// Terminal substitute when nothing matched by name. "Arial" first (correct wherever it is
        /// installed), then a real file for the sans-serif family — on a bare Linux container the
        /// <c>FromFamilyName</c> call resolves to nothing useful, and <c>SKTypeface.Default</c>
        /// renders every standard-14 document in whatever the platform default happens to be.
        /// </summary>
        private static SKTypeface FallbackTypeface()
        {
            SKTypeface arial = SKTypeface.FromFamilyName("Arial");
            if (IsFamilyMatch(arial, "Arial")) return arial;

            SKTypeface fromFile = ResolveFromFontFile("Arial", bold: false, italic: false);
            if (fromFile != null)
            {
                arial?.Dispose();
                return fromFile;
            }

            return arial ?? SKTypeface.Default;
        }

        // Text with no font decodes as a font that failed to load does: Core's FallbackFont owns
        // the CP-1252 decoder (and the report of why it fell back to ISO-8859-1).
        private static readonly FallbackFont _fallbackFont = new FallbackFont();

        private static string FallbackDecode(byte[] bytes)
            => _fallbackFont.Decode(bytes);

        // ================================================================
        //  IDisposable
        // ================================================================

        public void Dispose()
        {
            foreach (var entry in _entries.Values)
            {
                entry.Typeface?.Dispose();
                entry.CffSource?.Dispose();
                entry.HintingProbe?.Dispose();
                entry.Type1Source?.Dispose();
            }
            _entries.Clear();
        }
    }

    /// <summary>
    /// Font metrics extracted from a PDF /FontDescriptor dictionary.
    /// Values are in PDF glyph units (1/1000 em).  All fields are zero when
    /// the font has no /FontDescriptor.
    /// </summary>
    internal readonly struct FontDescriptorMetrics
    {
        /// <summary>Typographic ascender (positive, above baseline).</summary>
        public readonly float Ascent;
        /// <summary>Typographic descender (typically negative, below baseline).</summary>
        public readonly float Descent;
        /// <summary>Height of capital letters (positive).</summary>
        public readonly float CapHeight;
        /// <summary>Height of lower-case 'x' (positive).</summary>
        public readonly float XHeight;

        public FontDescriptorMetrics(float ascent, float descent, float capHeight, float xHeight)
        {
            Ascent    = ascent;
            Descent   = descent;
            CapHeight = capHeight;
            XHeight   = xHeight;
        }

        /// <summary>
        /// True when all fields are zero (font has no /FontDescriptor, or descriptor
        /// omits all metric entries).
        /// </summary>
        public bool IsEmpty => Ascent == 0f && Descent == 0f && CapHeight == 0f && XHeight == 0f;
    }

    /// <summary>
    /// One CID's vertical writing metrics (ISO 32000-1 §9.7.4.3, Table 117), in <b>em</b> units so
    /// a caller multiplies straight by the font size.
    /// </summary>
    /// <remarks>
    /// <para><b>How a renderer uses this.</b> Advance the text matrix downward by
    /// <see cref="AdvanceEm"/> × font size, and draw each glyph translated by
    /// <c>−(<see cref="PositionXEm"/>, <see cref="PositionYEm"/>)</c> × font size. Omitting the
    /// translation leaves a vertical CJK glyph sitting on the horizontal origin — off to the right
    /// of its column and a full ascender too low.</para>
    /// <para><see cref="AdvanceEm"/> is a positive magnitude, matching the long-standing contract
    /// of <see cref="FontCache.GetVerticalAdvance"/>: the file states <c>w1y</c> as a negative
    /// (downward, in Y-up space) number and callers apply the direction themselves. The position
    /// vector is <b>not</b> normalised — its sign is the file's.</para>
    /// </remarks>
    internal readonly struct VerticalGlyphMetrics
    {
        /// <summary>Downward advance per glyph, positive, in em units.</summary>
        public readonly float AdvanceEm;

        /// <summary>Position vector x (v1x) in em units — typically half the glyph's width.</summary>
        public readonly float PositionXEm;

        /// <summary>Position vector y (v1y) in em units — 0.88 by default (Table 117).</summary>
        public readonly float PositionYEm;

        /// <summary>
        /// True when the values came from a <c>/W2</c> entry for this CID rather than from the
        /// <c>/DW2</c> default. Lets a caller tell "the file said so" from "nothing was stated".
        /// </summary>
        public readonly bool IsExplicit;

        public VerticalGlyphMetrics(float advanceEm, float positionXEm, float positionYEm, bool isExplicit)
        {
            AdvanceEm   = advanceEm;
            PositionXEm = positionXEm;
            PositionYEm = positionYEm;
            IsExplicit  = isExplicit;
        }

        /// <summary>
        /// The downward step the pen takes after this glyph, in text-space units: §9.4.4's
        /// <c>ty = w1·Tfs + Tc + Tw</c> with <c>w1</c> negative (down), so the spacings
        /// <b>shorten</b> the step — §9.3.2: in vertical writing a negative <c>Tc</c> spreads the
        /// glyphs — and <c>Tw</c> applies to the single-byte code 32 alone (§9.3.3). Both renderers
        /// and the redaction engine step by this; a site adding <c>Tc</c> moves the next glyph the
        /// wrong way.
        /// </summary>
        public float Step(float fontSize, float charSpacing, float wordSpacing, bool isWordSpaceCode)
            => AdvanceEm * fontSize - charSpacing - (isWordSpaceCode ? wordSpacing : 0f);

        /// <summary>
        /// Builds from glyph-space values (1/1000 em) — the units every <c>/DW2</c> and
        /// <c>/W2</c> number is written in. <paramref name="advanceEm"/> is already in em units
        /// because its caller has to take a magnitude first.
        /// </summary>
        internal static VerticalGlyphMetrics FromGlyphUnits(
            float advanceEm, float v1x, float v1y, bool isExplicit)
            => new VerticalGlyphMetrics(
                advanceEm,
                (float)(v1x / PdfUnits.GlyphSpaceUnitsPerEm),
                (float)(v1y / PdfUnits.GlyphSpaceUnitsPerEm),
                isExplicit);
    }
}
