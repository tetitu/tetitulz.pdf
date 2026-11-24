// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Font and text operators (PLRM §8.12).
    ///
    /// <para><b>A font resolves one of three ways, in this order of preference.</b>
    /// <list type="number">
    ///   <item>The document's <b>own Type 1 program</b>, when its <c>eexec</c> section was captured
    ///     — reached by <c>findfont</c> through the header's <c>/FontName</c>, since a real program's
    ///     own <c>definefont</c> is inside the encrypted section, or claimed by a clear-text
    ///     <c>definefont</c>. Embedded verbatim as a <c>/FontFile</c> — no charstring is ever
    ///     decrypted — which is the only thing that can carry Hebrew, Cyrillic or any other script
    ///     the standard 14 lack. A <b>Type 42</b> font's TrueType program, from its own
    ///     <c>/sfnts</c>, is attached at its <c>definefont</c> and shown by glyph index through a
    ///     Type0 font (<see cref="Emit.PsType42Font"/>).</item>
    ///   <item>A <b>CJK composite</b>, emitted as a Type0 naming the same Adobe predefined CMap the
    ///     program composed with, so the reader performs the identical byte-to-CID decoding.</item>
    ///   <item>One of the <b>standard 14</b>, whose metrics come from
    ///     <c>Core.Fonts.Standard14Metrics</c> — carrying across a <c>definefont</c>'s
    ///     <c>/Encoding</c> array as a PDF <c>/Differences</c> where there is one.</item>
    /// </list></para>
    ///
    /// <para>Only <i>interpreting</i> a font program is out of scope. A name that maps to nothing
    /// still yields Helvetica and a diagnostic, because raising <c>undefined</c> from
    /// <c>findfont</c> would kill the program at the first line of its prologue; and text the
    /// resolved font cannot draw is counted and reported by
    /// <see cref="ReportUnrepresentable"/> rather than passing silently.</para>
    /// </summary>
    internal static class PsTextOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("findfont",   FindFont);
            op("scalefont",  ScaleFont);
            op("makefont",   MakeFont);
            op("setfont",    SetFont);
            op("currentfont", CurrentFont);
            op("rootfont",   CurrentFont);

            op("selectfont", SelectFont);

            op("show",       i => Show(i, "show"));
            op("glyphshow",  GlyphShow);
            op("xshow",      i => PositionedShow(i, "xshow"));
            op("yshow",      i => PositionedShow(i, "yshow"));
            op("xyshow",     i => PositionedShow(i, "xyshow"));
            op("ashow",      AShow);
            op("widthshow",  WidthShow);
            op("awidthshow", AWidthShow);
            op("stringwidth", StringWidth);
            op("charpath",   CharPath);
            op("definefont", DefineFont);
            op("undefinefont", UndefineFont);
            op("composefont", ComposeFont);
            op("findencoding", FindEncoding);

            // The Type 3 width operators. These MUST be registered here and DELETED from
            // PsSystemDict's arity table, not shadowed by it: Build() calls this Register before
            // RegisterKnownUnimplemented, and PsDict.Put overwrites — so leaving the stubs in the
            // table silently keeps the stubs, and every glyph then builds with no declared width,
            // /Widths fills with zeros and every glyph overprints the previous one at one point.
            op("setcachedevice",  i => DeclareGlyphWidth(i, "setcachedevice",  6));
            op("setcachedevice2", i => DeclareGlyphWidth(i, "setcachedevice2", 10));
            op("setcharwidth",    i => DeclareGlyphWidth(i, "setcharwidth",    2));
        }

        /// <summary>
        /// <c>setcachedevice</c> / <c>setcachedevice2</c> / <c>setcharwidth</c> — a glyph procedure
        /// declaring its advance.
        ///
        /// <para>They map onto PDF's two glyph-metric operators, and the distinction is real:
        /// <c>setcachedevice</c> is <c>d1</c> (the glyph describes <i>shape only</i> and paints in
        /// the text fill colour, so it also carries a bounding box), <c>setcharwidth</c> is
        /// <c>d0</c> (the glyph carries its own colour). <c>setcachedevice2</c> is d1 plus a report,
        /// because PDF has no <c>d2</c> and its vertical-writing metrics have nowhere to go.</para>
        ///
        /// <para>Outside a glyph build they keep the arity table's old behaviour — consume the
        /// operands and report — because there they genuinely mean the glyph cache, which this
        /// converter does not have.</para>
        /// </summary>
        private static void DeclareGlyphWidth(PsInterpreter i, string op, int operands)
        {
            var values = new double[operands];
            for (int k = operands - 1; k >= 0; k--) values[k] = i.Operands.PopNumber(op);

            var build = i.GlyphBuild;
            if (build == null)
            {
                PostScriptDiagnostics.ReportUnimplemented(op);
                return;
            }

            build.Wx = values[0];
            build.Wy = values[1];
            build.WidthDeclared = true;

            if (op == "setcharwidth") { build.ShapeOnly = false; return; }

            build.ShapeOnly = true;
            build.CacheBox = new[] { values[2], values[3], values[4], values[5] };

            if (op == "setcachedevice2")
                PostScriptDiagnostics.Report("PsTextOps",
                    "'setcachedevice2' was emitted as PDF's 'd1': PDF has no vertical-writing glyph " +
                    "metric operator, so the w1x/w1y/vx/vy half is dropped");
        }

        // A PostScript font is a dictionary; these are the keys this interpreter puts in it.
        private const string KeyBaseFont = "TetitulzBaseFont";
        private const string KeyMatrix   = "FontMatrix";
        private const string KeyEncoding = "Encoding";
        private const string KeyCMap     = "TetitulzCMapName";

        /// <summary>
        /// The key under which a font dictionary names its embedded program — a literal name that
        /// <c>setfont</c> looks up in <see cref="_embeddedByKey"/>.
        ///
        /// <para><b>In the dictionary, so it travels with the font.</b> A thread-static "the
        /// program the font <c>findfont</c> just returned carries", consumed by the next
        /// <c>setfont</c>, would be wrong for every font that is not set at once: two fonts looked
        /// up before either was set would swap programs, and a prologue's
        /// <c>/F1 … findfont def</c> would lose its program to a later <c>findfont</c>. A name in
        /// the dictionary survives <c>scalefont</c>,
        /// <c>makefont</c>, a <c>def</c> and the re-encoding idiom's copy, because each copies every
        /// entry. A CLR object cannot go into a <see cref="PsDict"/>, so the dictionary holds the
        /// key and the per-conversion map holds the program.</para>
        /// </summary>
        private const string KeyEmbedded = "TetitulzEmbeddedFont";

        /// <summary>
        /// What a Type 42 font's key in <see cref="_embeddedByKey"/> begins with, so a font defined under
        /// the name a captured Type 1 header declares does not replace that program.
        /// </summary>
        private const string Type42KeyPrefix = "Type42/";

        /// <summary>What a Type 2 CIDFont's key in <see cref="_embeddedByKey"/> begins with: a CIDFont and a font may share a name.</summary>
        private const string CidFontKeyPrefix = "CIDFont/";

        /// <summary>
        /// Fonts the program defined with <c>definefont</c>, by name.
        ///
        /// <para><c>[ThreadStatic]</c> and cleared per conversion, like every other slot in this
        /// assembly: a font one document defines must not be visible to the next.</para>
        /// </summary>
        [ThreadStatic] private static Dictionary<string, PsDict> _definedFonts;

        /// <summary>
        /// Captured programs by the key a font dictionary names them under: the header's
        /// <c>/FontName</c> when <c>findfont</c> reached the program by it, or the name a
        /// <c>definefont</c> claimed it under. <c>[ThreadStatic]</c>, cleared per conversion.
        /// </summary>
        [ThreadStatic] private static Dictionary<string, PsEmbeddedFont> _embeddedByKey;

        /// <summary>The embedded program <paramref name="font"/> names, or null.</summary>
        private static PsEmbeddedFont EmbeddedProgramOf(PsDict font)
            => font != null
               && font.TryGetName(KeyEmbedded, out var key) && key.Type == PsType.Name
               && _embeddedByKey != null && _embeddedByKey.TryGetValue(key.AsName(), out var program)
                ? program
                : null;

        /// <summary>Makes <paramref name="font"/> name <paramref name="program"/> under <paramref name="key"/>.</summary>
        private static void AttachEmbedded(PsDict font, string key, PsEmbeddedFont program)
        {
            _embeddedByKey ??= new Dictionary<string, PsEmbeddedFont>(StringComparer.Ordinal);
            _embeddedByKey[key] = program;
            font.PutName(KeyEmbedded, PsObject.LitName(key));
        }

        /// <summary>
        /// <c>findfont</c> — a font dictionary for the named font. The dictionary is real, so a
        /// program can <c>get</c> from it and pass it around, but only a few keys are meaningful.
        /// Internal for <c>/Font findresource</c>, which is this operator under another name.
        /// </summary>
        internal static void FindFont(PsInterpreter i)
        {
            var nameObj = i.Operands.Pop();
            string requested = nameObj.Type switch
            {
                PsType.Name   => nameObj.AsName(),
                PsType.String => i.LatinFromOperand(nameObj.AsString(), "findfont"),
                _ => throw new PsErrorException(PsErrorKind.TypeCheck, "findfont")
            };

            // A font the program defined with definefont wins over the standard-14 mapping. That
            // is the entire point of defining one: a re-encoded font is how every legacy Greek,
            // Hebrew or Cyrillic document selects its glyphs. The dictionary carries its own
            // embedded program, when it has one, under KeyEmbedded.
            if (_definedFonts != null && _definedFonts.TryGetValue(requested, out var defined))
            {
                i.Operands.Push(PsObject.Dict(defined));
                return;
            }

            // A font the document EMBEDDED. A real Type 1 program calls definefont inside its
            // encrypted section — the Type 1 format's own example ends the eexec portion with
            // `dup /FontName get exch definefont pop` — and the scanner skips that section whole,
            // so the only clear-text trace of the font is its header's /FontName. That is what the
            // document's `/CMR10 findfont` has to find; without this lookup every embedded font would
            // draw as the Helvetica substitute.
            if (i.EmbeddedFonts.TryGetValue(requested, out var captured))
            {
                i.ChargeAllocation(3 * PsAllocationBudget.BytesPerDictEntry, "findfont");
                var embedded = new PsDict(3);

                // The base face is the standard-14 stand-in the pen advance is measured against —
                // the real widths are in charstrings this path never reads (reported once, with the
                // /Widths omission, by the resource collector). Not a substitution: the glyphs the
                // reader draws are the document's own.
                embedded.PutName(KeyBaseFont, PsObject.LitName(PsFontMapper.Resolve(requested, out _)));
                embedded.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Identity));
                AttachEmbedded(embedded, requested, captured);
                i.Operands.Push(PsObject.Dict(embedded));
                return;
            }

            // A CJK composite name carries its CMap: KozMinPro-Regular-83pv-RKSJ-H is the face
            // KozMinPro-Regular under the CMap 83pv-RKSJ-H. Only the registry can say where the
            // split is, because both halves contain hyphens.
            if (PsCidFonts.TrySplitCompositeName(requested, out string cjkFace, out string cjkCMap))
            {
                i.ChargeAllocation(3 * PsAllocationBudget.BytesPerDictEntry, "findfont");
                var composite = new PsDict(3);
                composite.PutName(KeyBaseFont, PsObject.LitName(cjkFace));
                composite.PutName(KeyCMap, PsObject.LitName(cjkCMap));
                composite.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Identity));
                i.Operands.Push(PsObject.Dict(composite));
                return;
            }

            string baseFont = PsFontMapper.Resolve(requested, out bool exact);
            if (!exact)
                PostScriptDiagnostics.Report("PsTextOps.FindFont",
                    $"font '{requested}' is not one of the standard 14 and no metric-compatible " +
                    $"substitute is known; '{baseFont}' was used instead");

            i.ChargeAllocation(2 * PsAllocationBudget.BytesPerDictEntry, "findfont");
            var dict = new PsDict(2);
            dict.PutName(KeyBaseFont, PsObject.LitName(baseFont));
            dict.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Identity));
            i.Operands.Push(PsObject.Dict(dict));
        }

        /// <summary><c>font scale scalefont font'</c> — a uniformly scaled copy.</summary>
        private static void ScaleFont(PsInterpreter i)
        {
            double scale = i.Operands.PopNumber("scalefont");
            var font = i.Operands.PopDict("scalefont");
            i.Operands.Push(PsObject.Dict(WithMatrix(i, font,
                FontMatrix(font).Concat(PsMatrix.Scale(scale, scale)))));
        }

        /// <summary>
        /// <c>font matrix makefont font'</c> — an arbitrarily transformed copy. This is how
        /// condensed, expanded and obliqued text is expressed, so the matrix has to survive all the
        /// way into the emitted <c>Tm</c> rather than being collapsed to a size.
        ///
        /// <para><b>The existing matrix first, then the operand.</b> PLRM: the new FontMatrix is
        /// "the original FontMatrix concatenated with matrix" — <c>FontMatrix matrix concatmatrix</c>
        /// — so a glyph is transformed by what the font already was and then by the new matrix.
        /// It was composed the other way round, which two non-commuting transforms show: a
        /// horizontal stretch followed by a skew is <c>[2 0 1 1]</c> and came out <c>[2 0 2 1]</c>.
        /// For a uniform scale the two orders agree, which is why nothing noticed.</para>
        /// </summary>
        private static void MakeFont(PsInterpreter i)
        {
            var matrixArr = i.Operands.PopArray("makefont");
            var font = i.Operands.PopDict("makefont");

            var m = PsMatrix.FromOperands(matrixArr, "makefont");
            i.Operands.Push(PsObject.Dict(WithMatrix(i, font, FontMatrix(font).Concat(m))));
        }

        private static void SetFont(PsInterpreter i)
        {
            var font = i.Operands.PopDict("setfont");

            // The dictionary itself is what `currentfont` answers with (PLRM §8.2), so a font
            // rescaled through it keeps everything it was. Kept even with no engine attached.
            i.CurrentFontDict = font;

            var gs = i.Graphics?.Gs;
            if (gs == null) return;

            gs.FontResourceName = font.TryGetName(KeyBaseFont, out var bf) && bf.Type == PsType.Name
                ? bf.AsName()
                : null;

            var m = FontMatrix(font);
            i.CurrentFontMatrix = m;        // the full matrix: the emitted Tm, and every advance
            i.CurrentFontEncoding = EncodingDifferences(font);
            i.CurrentFontCMap = font.TryGetName(KeyCMap, out var cm) && cm.Type == PsType.Name
                ? cm.AsName()
                : null;
            i.CurrentEmbeddedFont = EmbeddedProgramOf(font);

            // A program may `setfont` a dictionary it never `definefont`ed, so the type is read
            // from the dictionary itself rather than from the defined-font registry.
            i.CurrentType3Font   = Type3For(font);
            i.CurrentFontIsType3 = i.CurrentType3Font != null;
        }

        /// <summary>
        /// <c>key scale selectfont</c> / <c>key matrix selectfont</c> — the Level 2 shorthand for
        /// <c>key findfont scale scalefont setfont</c>.
        ///
        /// <para><b>It was undefined, and that was expensive.</b> Generated PostScript uses this
        /// constantly, and an undefined name makes <c>PsInterpreter.Run</c> abandon the whole
        /// top-level object — so for a page whose body is one procedure call, every mark after the
        /// first <c>selectfont</c> was lost, not just the font.</para>
        ///
        /// <para>Implemented by <b>composing</b> the existing helpers rather than resolving the font
        /// itself, so the three resolutions <see cref="FindFont"/> makes — defined, embedded,
        /// composite, standard — and everything <see cref="SetFont"/> reads off the dictionary stay
        /// in one place each. Calling them as plain methods is not "re-entering the interpreter":
        /// none of them touches the exec stack or runs a procedure.</para>
        ///
        /// <para>The second operand is a <b>number or a matrix</b>, and dispatching on it is
        /// mandatory: treating a matrix as a scale collapses every condensed, expanded or obliqued
        /// font to a uniform one.</para>
        /// </summary>
        private static void SelectFont(PsInterpreter i)
        {
            var second = i.Operands.Pop();
            var key    = i.Operands.Pop();

            // Validated before anything is pushed back, so a bad program cannot leave a
            // half-consumed stack behind.
            if (key.Type != PsType.Name && key.Type != PsType.String)
                throw new PsErrorException(PsErrorKind.TypeCheck, "selectfont",
                    "the font must be named by a name or a string");

            bool isMatrix = second.Type == PsType.Array;
            if (!isMatrix && !second.IsNumber)
                throw new PsErrorException(PsErrorKind.TypeCheck, "selectfont",
                    "the second operand must be a scale or a matrix");

            i.Operands.Push(key);
            FindFont(i);                       // pops the key, pushes the font dictionary

            i.Operands.Push(second);
            if (isMatrix) MakeFont(i); else ScaleFont(i);

            SetFont(i);                        // pops the dictionary, pushes nothing
        }

        /// <summary>
        /// <c>name glyphshow</c> — paints one glyph selected by <b>glyph name</b>, bypassing the
        /// font's encoding vector entirely. That is its whole purpose: it reaches glyphs no
        /// character code maps to.
        ///
        /// <para>Two routes. If the current encoding already contains the name, the matching byte is
        /// shown through the ordinary path and the output is byte-identical to <c>show</c>. If it
        /// does not, a <b>one-entry <c>/Differences</c></b> is synthesised and the byte 1 is shown
        /// through it. The resource collector already keys its cache on the encoding, so each
        /// distinct glyph name costs exactly one small font resource however often it is used.</para>
        /// </summary>
        private static void GlyphShow(PsInterpreter i)
        {
            var operand = i.Operands.Pop();

            var g = i.Graphics;
            if (g == null) return;

            if (!i.Options.AllowText) { PostScriptDiagnostics.ReportUnimplemented("glyphshow"); return; }

            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined, "glyphshow", "no current point");

            // An integer operand is the CID form — a CID-font extension, not PLRM. Inverting an
            // Adobe CMap to find the code for a CID is out of scope, so it is reported and the pen
            // advanced by one em, consistent with the descendant font's /DW 1000.
            if (operand.Type == PsType.Integer || i.CurrentFontCMap != null)
            {
                PostScriptDiagnostics.ReportUnimplemented("glyphshow (composite font)");
                AdvancePen(g, i.CurrentFontMatrix, 1.0);
                return;
            }

            if (operand.Type != PsType.Name)
                throw new PsErrorException(PsErrorKind.TypeCheck, "glyphshow");

            string glyph = operand.AsName();

            // A Type 3 font's glyph is a procedure, reached by the code its /Encoding gives the
            // name. Routed before the base-font path below, which would draw the Helvetica glyph
            // of that name — silently, with no base face to say so.
            if (i.CurrentType3Font != null)
            {
                GlyphShowType3(i, g, i.CurrentType3Font, glyph);
                return;
            }

            // A Type 42 font names the glyph directly: its index through /CharStrings, whatever the
            // encoding holds.
            if (i.CurrentEmbeddedFont?.TrueType is { } type42)
            {
                int index = type42.Glyph(i.CurrentFontDict ?? new PsDict(), glyph,
                                         n => i.ChargeAllocation(n, "glyphshow"), out double advance);
                var (gx, gy) = g.Path.CurrentPoint.Value;
                g.Emitter.ShowText(g.Resources.GetOrAddGlyphIndexedFont(i.CurrentEmbeddedFont),
                                   TextMatrixAt(i.CurrentFontMatrix, gx, gy),
                                   new[] { (byte)(index >> 8), (byte)index }, g.Gs.Color, 0, 0);
                AdvancePen(g, i.CurrentFontMatrix, advance * GlyphSpace);
                return;
            }

            string baseFont = g.Gs.FontResourceName ?? Core.Constants.StandardFonts.Helvetica;

            // The encoding passed to the COLLECTOR must be the same value Paint would pass, or the
            // cache key differs and a second font resource is minted for the same program — one
            // carrying an explicit /Differences that would override an embedded font's own built-in
            // encoding. The built-in map is used only to look the glyph up and to measure it.
            var collectorEncoding = i.CurrentFontEncoding;
            var lookupEncoding = collectorEncoding ?? i.CurrentEmbeddedFont?.BuiltInEncoding;

            byte[] bytes;
            IReadOnlyDictionary<int, string> encoding;

            // Reachable already? Through the font's own encoding when it has one, otherwise through
            // the WinAnsi the collector declares for a font that does not. Either way the emitted
            // bytes are what `show` would have produced and no new resource is minted.
            int existing = FindCode(lookupEncoding, glyph);
            if (existing < 0 && lookupEncoding == null && i.CurrentEmbeddedFont == null)
                existing = PsFontMapper.DefaultCodeFor(baseFont, glyph);

            if (existing >= 0)
            {
                bytes = new[] { (byte)existing };
                encoding = collectorEncoding;
            }
            else
            {
                if (!TryReserveSyntheticGlyph(glyph))
                {
                    AdvancePen(g, i.CurrentFontMatrix, MeasureGlyphEm(baseFont, glyph));
                    return;
                }
                bytes = new byte[] { 1 };
                encoding = new Dictionary<int, string> { [1] = glyph };
            }

            string resource = i.CurrentEmbeddedFont != null
                ? g.Resources.GetOrAddEmbeddedFont(i.CurrentEmbeddedFont, encoding)
                : g.Resources.GetOrAddFont(baseFont, encoding);

            var (x, y) = g.Path.CurrentPoint.Value;
            var fm = i.CurrentFontMatrix;
            g.Emitter.ShowText(resource, TextMatrixAt(fm, x, y),
                               bytes, g.Gs.Color, 0, 0);

            AdvancePen(g, fm, MeasureGlyphEm(baseFont, glyph));
        }

        /// <summary>The code a glyph name occupies in an encoding, or -1.</summary>
        private static int FindCode(IReadOnlyDictionary<int, string> encoding, string glyph)
        {
            if (encoding == null) return -1;
            foreach (var kv in encoding)
                if (kv.Value == glyph && kv.Key >= 0 && kv.Key <= 255) return kv.Key;
            return -1;
        }

        /// <summary>
        /// <c>glyphshow</c> in a Type 3 font: the glyph is reached through the code the font's own
        /// <c>/Encoding</c> gives its name — a PDF CharProc is addressed by code — and built and
        /// shown exactly as <c>show</c> would show that code. A name the encoding does not carry
        /// has no code to show it by; it is reported and the pen stands still.
        /// </summary>
        private static void GlyphShowType3(PsInterpreter i, PsGraphicsEngine g, PsType3Font font,
                                           string glyph)
        {
            int code = FindCode(font.Encoding, glyph);
            if (code < 0)
            {
                PostScriptDiagnostics.ReportUnimplemented(
                    "glyphshow (a Type 3 glyph outside the font's /Encoding)");
                return;
            }

            var built = PsType3GlyphBuilder.Build(i, font, code);
            if (built == null) return;

            string resource = g.Resources.AddType3Font(font, font.Built, font.GlyphResources);
            var (x, y) = g.Path.CurrentPoint.Value;
            var fm = i.CurrentFontMatrix;
            g.Emitter.ShowText(resource, TextMatrixAt(fm, x, y),
                               new[] { (byte)code }, g.Gs.Color, 0, 0);
            AdvancePen(g, fm, built.Wx * font.GlyphMatrix.A);
        }

        /// <summary>
        /// Records a glyph name as having a synthesised font, refusing past the cap.
        ///
        /// <para>Counted by <b>name</b>, not by call: the collector de-duplicates internally and
        /// returns nothing that says "this one was new", so counting calls would not count fonts.
        /// Without the cap, <c>{ /g1 glyphshow } loop</c> over generated names turns
        /// <c>/Resources</c> itself into the memory attack.</para>
        /// </summary>
        private static bool TryReserveSyntheticGlyph(string glyph)
        {
            _syntheticGlyphs ??= new HashSet<string>(StringComparer.Ordinal);
            if (_syntheticGlyphs.Contains(glyph)) return true;

            if (_syntheticGlyphs.Count >= PsLimits.MaxSyntheticGlyphFonts)
            {
                if (!_syntheticGlyphCapReported)
                {
                    _syntheticGlyphCapReported = true;
                    PostScriptDiagnostics.Report("PsTextOps.GlyphShow",
                        $"the {PsLimits.MaxSyntheticGlyphFonts}-font cap for 'glyphshow' glyph names " +
                        "outside the current encoding was reached; later ones advanced the pen but " +
                        "drew nothing. Reported once per conversion.");
                }
                return false;
            }

            _syntheticGlyphs.Add(glyph);
            return true;
        }

        [ThreadStatic] private static HashSet<string> _syntheticGlyphs;
        [ThreadStatic] private static bool _syntheticGlyphCapReported;

        private static double MeasureGlyphEm(string baseFont, string glyph)
        {
            var one = new Dictionary<int, string> { [1] = glyph };
            return PsFontMapper.MeasureEmWidth(baseFont, new byte[] { 1 }, 0, 1, one, out _);
        }

        /// <summary>
        /// Moves the pen past a run: <paramref name="along"/> text-space units through the font
        /// matrix — PLRM §5.3's width vector, so a rotated or skewed <c>makefont</c> advances along
        /// its own baseline — plus the user-space displacement <c>ashow</c>/<c>widthshow</c> add
        /// (PLRM: "in the user coordinate system, not in the character coordinate system").
        ///
        /// <para>Every text operator moves the pen through here. The sites that read only the
        /// matrix's A term piled a rotated line's strings up along x, and a program positioning
        /// the rest of its page by <c>currentpoint</c> laid it out from the wrong place.</para>
        /// </summary>
        private static void AdvancePen(Graphics.PsGraphicsEngine g, in PsMatrix fm, double along,
                                       double userDx = 0, double userDy = 0)
        {
            var (x, y) = g.Path.CurrentPoint.Value;
            g.NoteTextExtent(x, y, along, fm);
            var (dx, dy) = fm.ApplyDelta(along, 0);
            // The spacing ashow and widthshow add is in user space, outside the glyphs' box: the
            // text reaches where the pen ends, which the box at its end point covers.
            if (userDx != 0 || userDy != 0) g.NoteTextExtent(x + dx + userDx, y + dy + userDy, 0, fm);
            g.Path.MoveTo(x + dx + userDx, y + dy + userDy);
        }

        /// <summary>
        /// <c>string numarray xshow</c> / <c>yshow</c> / <c>xyshow</c> — show the string, placing
        /// each glyph by an explicit displacement rather than by its own width.
        ///
        /// <para>Each glyph becomes its own text object at its own computed position, which is what
        /// makes the displacement exact without needing PDF's <c>TJ</c> (whose units are thousandths
        /// of the font size and whose sign is inverted — two conversions to get wrong).</para>
        ///
        /// <para>The displacements are consumed as PostScript defines them: <c>xshow</c> takes one
        /// number per glyph, <c>yshow</c> one, <c>xyshow</c> a <b>pair</b> per glyph. A short array
        /// falls back to the glyph's own width for the remainder and reports.</para>
        /// </summary>
        private static void PositionedShow(PsInterpreter i, string op)
        {
            var displacements = i.Operands.PopArray(op);
            var text = i.Operands.PopString(op);

            var g = i.Graphics;
            if (g == null) return;

            // Gated exactly like Paint: a new show path that bypassed this would silently re-enable
            // text for a caller that turned it off.
            if (!i.Options.AllowText) { PostScriptDiagnostics.ReportUnimplemented(op); return; }

            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined, op, "no current point");

            // Charged before the copy rather than after it, so a 1 MB operand is refused before it
            // is copied whole.
            ChargeTextWork(i, op, text);

            byte[] bytes = text.ToArray();
            if (bytes.Length == 0) return;

            // One positioned text object per CODE, in whatever the current font is, dispatched on the
            // font's kind: the simple-font path for every kind would draw a Type 3 font's xshow in
            // Helvetica's letters and split a composite font's bytes one at a time.
            var glyphs = PositionedGlyphs(i, g, bytes, out string resource);
            if (glyphs == null) return;

            var fm = i.CurrentFontMatrix;
            var (x, y) = g.Path.CurrentPoint.Value;
            int perGlyph = op == "xyshow" ? 2 : 1;
            bool shortArray = displacements.Length < glyphs.Count * perGlyph;

            for (int k = 0; k < glyphs.Count; k++)
            {
                var (code, em) = glyphs[k];
                g.Emitter.ShowText(resource, TextMatrixAt(fm, x, y),
                                   code, g.Gs.Color, 0, 0);
                g.NoteTextExtent(x, y, em, fm);

                int at = k * perGlyph;
                if (at + perGlyph <= displacements.Length)
                {
                    switch (op)
                    {
                        case "xshow":  x += Number(displacements, at); break;
                        case "yshow":  y += Number(displacements, at); break;
                        default:       x += Number(displacements, at);
                                       y += Number(displacements, at + 1); break;
                    }
                }
                else
                {
                    // The glyph's own width, through the font matrix like every other advance.
                    var (dx, dy) = fm.ApplyDelta(em, 0);
                    x += dx;
                    y += dy;
                }
            }

            if (shortArray)
                PostScriptDiagnostics.Report("PsTextOps." + op,
                    $"'{op}' was given {displacements.Length} displacement(s) for {glyphs.Count} " +
                    "character(s); the remainder used each glyph's own width");

            g.Path.MoveTo(x, y);
        }

        /// <summary>
        /// The codes a positioned show places, each with the bytes <c>Tj</c> shows it by and its own
        /// advance in text space, and the font resource they are shown in — by the font's kind. A
        /// Type 3 font's glyphs are built (memoised) and registered as <c>show</c> registers them; a
        /// composite font's bytes are split into codes (<see cref="PsCidFonts.SplitCodes"/>) at one
        /// em each; a simple font takes one byte per glyph through the embedded program or the
        /// standard face. Null when nothing can be shown — a Type 3 font none of whose glyphs built.
        /// </summary>
        private static List<(byte[] Code, double Em)> PositionedGlyphs(
            PsInterpreter i, PsGraphicsEngine g, byte[] bytes, out string resource)
        {
            var glyphs = new List<(byte[], double)>(bytes.Length);

            if (i.CurrentType3Font is { } type3)
            {
                bool any = false;
                foreach (byte b in bytes)
                {
                    var built = PsType3GlyphBuilder.Build(i, type3, b);
                    any |= built != null;
                    glyphs.Add((new[] { b }, (built?.Wx ?? 0) * type3.GlyphMatrix.A));
                }

                if (!any) { resource = null; return null; }   // every code refused, each said why
                resource = g.Resources.AddType3Font(type3, type3.Built, type3.GlyphResources);
                return glyphs;
            }

            string baseFont = g.Gs.FontResourceName ?? Core.Constants.StandardFonts.Helvetica;

            if (TrueTypeGlyphs(i, bytes, "xshow") is { } trueType)
            {
                resource = g.Resources.GetOrAddGlyphIndexedFont(i.CurrentEmbeddedFont);
                return trueType.ConvertAll(t => (t.Code, t.Em));
            }

            if (i.CurrentFontCMap is { } cffCmap && i.CurrentEmbeddedFont?.Cff is { IsCidKeyed: true }
                && CffCidCodes(i, bytes) is { } cffCodes)
            {
                resource = g.Resources.GetOrAddCidCffFont(i.CurrentEmbeddedFont, cffCmap, PsCMapOps.Current(i));
                int at = 0;
                foreach (var (length, em, _) in cffCodes)
                {
                    var code = new byte[length];
                    Array.Copy(bytes, at, code, 0, length);
                    glyphs.Add((code, em));
                    at += length;
                }
                return glyphs;
            }

            if (i.CurrentFontCMap is { } cmap)
            {
                resource = g.Resources.GetOrAddCidFont(baseFont, cmap, PsCMapOps.Current(i));
                ReportCidMetrics(baseFont, cmap);
                foreach (var code in PsCMapOps.SplitCodes(i, cmap, bytes)) glyphs.Add((code, 1.0));
                return glyphs;
            }

            var encoding = i.CurrentFontEncoding;
            resource = i.CurrentEmbeddedFont != null
                ? g.Resources.GetOrAddEmbeddedFont(i.CurrentEmbeddedFont, encoding)
                : g.Resources.GetOrAddFont(baseFont, encoding);

            // Same honesty rule as Paint: text the mapped font cannot draw must not pass as clean.
            if (i.CurrentEmbeddedFont == null) ReportUnrepresentable(baseFont, bytes, encoding);
            else encoding ??= i.CurrentEmbeddedFont.BuiltInEncoding;

            foreach (byte b in bytes)
            {
                var one = new[] { b };
                glyphs.Add((one, MeasureEm(i, baseFont, one, encoding)));
            }
            return glyphs;
        }

        private static double Number(PsArray a, int index)
            => index >= 0 && index < a.Length && a[index].IsNumber ? a[index].Num : 0;

        /// <summary>
        /// <c>currentfont</c> — the font dictionary in the graphics state: the object
        /// <c>setfont</c> was given (PLRM §8.2), so <c>currentfont 0.8 scalefont setfont</c> keeps
        /// the font's encoding, its Type 3 procedures, its CMap and its embedded program, and
        /// <c>currentfont F eq</c> is true for the very dictionary just set. With no font set —
        /// an error in PLRM, tolerated here as <c>show</c> tolerates it — a dictionary standing for
        /// Helvetica at the current matrix is answered, so a program's <c>currentfont … setfont</c>
        /// still runs.
        /// </summary>
        private static void CurrentFont(PsInterpreter i)
        {
            if (i.CurrentFontDict != null)
            {
                i.Operands.Push(PsObject.Dict(i.CurrentFontDict));
                return;
            }

            var gs = i.Graphics?.Gs;
            i.ChargeAllocation(2 * PsAllocationBudget.BytesPerDictEntry, "currentfont");

            var dict = new PsDict(2);
            dict.PutName(KeyBaseFont,
                PsObject.LitName(gs?.FontResourceName ?? Core.Constants.StandardFonts.Helvetica));
            dict.PutName(KeyMatrix, MatrixObject(i, i.CurrentFontMatrix));
            i.Operands.Push(PsObject.Dict(dict));
        }

        /// <summary><c>key font /Font defineresource</c>: <c>definefont</c> itself (PLRM §3.9.2).</summary>
        internal static void DefineFontFromResource(PsInterpreter i) => DefineFont(i);

        /// <summary><c>key /Font undefineresource</c>: <c>undefinefont</c> itself.</summary>
        internal static void UndefineFontFromResource(PsInterpreter i) => UndefineFont(i);

        /// <summary>
        /// <c>key font definefont font</c> — registers a font under a name.
        ///
        /// <para><b>The valuable part is the <c>/Encoding</c> array.</b> The standard idiom copies a
        /// base font dictionary, replaces <c>/Encoding</c> with a 256-element array of glyph names,
        /// and defines the result. That is how every legacy Greek, Hebrew, Cyrillic and
        /// Central-European document selects its glyphs: the text bytes are a single-byte codepage,
        /// and the encoding array is what says which glyph each byte means. Carrying it through to a
        /// PDF <c>/Differences</c> encoding is what makes such a document convert correctly instead
        /// of drawing the WinAnsi letters at those code points.</para>
        ///
        /// <para>The font <i>program</i> is still not built — glyphs come from whichever standard-14
        /// face the base font mapped to, so a glyph name that face does not have is still counted as
        /// unrepresentable and reported.</para>
        /// </summary>
        private static void DefineFont(PsInterpreter i)
        {
            var font = i.Operands.PopDict("definefont");
            var key  = i.Operands.Pop();

            string name = key.Type switch
            {
                PsType.Name   => key.AsName(),
                PsType.String => i.LatinFromOperand(key.AsString(), "definefont"),
                _ => null
            };

            if (name == null)
            {
                PostScriptDiagnostics.Report("PsTextOps.DefineFont",
                    "definefont was given a non-name key; the font was not registered");
                i.Operands.Push(PsObject.Dict(font));
                return;
            }

            // A Type 3 font's glyphs are PostScript PROCEDURES, not a font program. Without this
            // branch such a font would fall through to the standard-14 substitution below and draw
            // Helvetica's letters with NOTHING reported — a wrong picture behind a clean report,
            // which is the worst outcome in this codebase.
            if (IsType3(font))
            {
                // The font's own /FontMatrix and this interpreter's size matrix COLLIDE: KeyMatrix is
                // literally the string "FontMatrix", so a program-built dictionary's [0.001 …] lands
                // in the slot `scalefont` accumulates into — and `12 scalefont` would then produce
                // `0.012 0 0 0.012 0 0 Tm`, a document rendered at 1/1000 scale with a clean report.
                // Resetting the size matrix is what makes `scalefont` mean the same thing it does for
                // every other font.
                //
                // Deliberately NOT generalised to any dictionary carrying a /FontMatrix: every
                // dictionary findfont produces has an identity KeyMatrix, and the re-encoding tests
                // depend on scalefont accumulating into it. A dictionary built from scratch around
                // an embedded program has the same collision, and is put right below when the
                // program is attached.
                //
                // PARK THE GLYPH MATRIX FIRST. Resetting the slot before copying it aside loses it
                // for good, and the value read back later is then the point SIZE — the same
                // collision seen from the other end, which is how /FontMatrix came out as 12.
                PsType3Font.PreserveGlyphMatrix(font);
                font.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Identity));

                // No report here. A Type 3 font CONVERTS — its glyph procedures are run and
                // become the emitted font's /CharProcs — so reporting one would make every such
                // document unclean for doing nothing wrong. What reports is a real failure:
                // a glyph that declares no width, one that nests past the cap, or a cap reached.

                // Deliberately NOT given a KeyBaseFont: there is no standard-14 base for it, and
                // writing one is what made the substitution silent.
                _definedFonts ??= new Dictionary<string, PsDict>(StringComparer.Ordinal);
                _definedFonts[name] = font;
                i.FontDirectory.PutName(name, PsObject.Dict(font));

                i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "definefont");
                i.Operands.Push(PsObject.Dict(font));
                return;
            }

            // A Type 42 font carries a TrueType program in its /sfnts. Its /FontMatrix maps the em of
            // the program's glyph space, which is the size matrix's own unit, so it stays as it is
            // (the Type 1 program's thousandths below do not apply). A program this converter cannot
            // carry leaves the font on the standard face its name maps to, said.
            if (Emit.PsType42Font.IsCidFontType2(font))
            {
                // A Type 2 CIDFont defined with definefont (PLRM Table 5.12 names both operators): its
                // glyphs are reached by CID, never through the .notdef-only /CharStrings it carries.
                DefineCidFont(i, name, font);
            }
            else if (Emit.PsType42Font.IsType42(font) && EmbeddedProgramOf(font) == null)
            {
                if (!font.TryGetName(KeyBaseFont, out _))
                    font.PutName(KeyBaseFont, PsObject.LitName(PsFontMapper.Resolve(name, out _)));

                var type42 = Emit.PsType42Font.TryRead(font, n => i.ChargeAllocation(n, "definefont"), out string refusal);
                if (type42 != null)
                    AttachEmbedded(font, Type42KeyPrefix + name, new PsEmbeddedFont { FontName = name, TrueType = type42 });
                else
                    PostScriptDiagnostics.Report("PsTextOps.DefineFont",
                        $"Type 42 font '{name}' was not carried — {refusal} — so its text is drawn in the standard face its name maps to");
            }

            // Built by the program, not copied from a findfont result: its /FontMatrix, if any, is
            // the program's glyph matrix rather than this interpreter's size matrix.
            bool ownGlyphMatrix = !font.TryGetName(KeyBaseFont, out _) && font.TryGetName(KeyMatrix, out _);

            // A dictionary copied from a base font carries whatever this interpreter put in it, so
            // the base face and matrix survive. When the program built the dictionary from scratch
            // they are absent, and Helvetica at identity is the honest default.
            if (!font.TryGetName(KeyBaseFont, out _))
                font.PutName(KeyBaseFont, PsObject.LitName(Core.Constants.StandardFonts.Helvetica));
            if (!font.TryGetName(KeyMatrix, out _))
                font.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Identity));

            // The program the font carries. A dictionary copied from a findfont result already
            // names its program (KeyEmbedded travels with the entries, so a re-encoded copy of an
            // embedded font keeps it); a fresh dictionary defined under a captured header's
            // /FontName is that program; and a program whose header declared no name can only be
            // claimed by the next definefont — once. Nothing else is attached. Handing the LAST
            // capture to whatever was defined next, which is what this did, gave a re-encoded
            // Helvetica the embedded Hebrew font's program.
            if (EmbeddedProgramOf(font) == null)
            {
                if (i.EmbeddedFonts.TryGetValue(name, out var byName))
                {
                    AttachEmbedded(font, name, byName);
                }
                else if (i.LastEmbeddedFont is { FontName: null } unnamed)
                {
                    AttachEmbedded(font, name, unnamed);
                    i.LastEmbeddedFont = null;
                }

                // A program's own font dictionary — `N dict begin … /FontMatrix [0.001 …] def` —
                // puts its glyph matrix in the slot scalefont accumulates into, so `100 scalefont`
                // made 0.1 and the text came out at 1/1000 of its size. The size matrix every other
                // font carries is in thousandths of glyph space (the PDF writer's 1/1000 too), so
                // the glyph matrix is taken into it once, here, where the program is attached: a
                // re-encoded or scaled copy already names its program and is not taken again.
                // Thousandths first, then the glyph matrix (row vectors): a translation it carries
                // stays in text space.
                if (ownGlyphMatrix && EmbeddedProgramOf(font) != null
                    && font.TryGetName(KeyMatrix, out var glyphArray) && glyphArray.Type == PsType.Array
                    && PsMatrix.TryFromOperands(glyphArray.AsArray(), out var glyphMatrix))
                    font.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Scale(1000, 1000).Concat(glyphMatrix)));
            }

            _definedFonts ??= new Dictionary<string, PsDict>(StringComparer.Ordinal);
            _definedFonts[name] = font;

            // FontDirectory is the PostScript-visible half of the same registration, and it is what
            // the standard "define it once" guard reads:
            //     FontDirectory /Foo known not { /Foo … definefont pop } if
            // Keeping the two in step here rather than making FontDirectory the storage keeps
            // _definedFonts's [ThreadStatic] per-conversion reset as the single lifetime rule.
            
            i.FontDirectory.PutName(name, PsObject.Dict(font));

            i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "definefont");
            i.Operands.Push(PsObject.Dict(font));
        }

        /// <summary>A Type 3 font dictionary: its glyphs are PostScript procedures.</summary>
        private static bool IsType3(PsDict font) =>
            font != null && font.TryGetName("FontType", out var t) && t.IsNumber && t.AsInt() == 3;

        /// <summary>
        /// Recognised Type 3 fonts, by the dictionary instance itself.
        ///
        /// <para>Keyed by reference rather than by name because a program may <c>setfont</c> a
        /// dictionary it never <c>definefont</c>ed, and because <c>scalefont</c> hands back a
        /// <i>copy</i> — so the copy is registered too, sharing the original's built glyphs.</para>
        /// </summary>
        [ThreadStatic]
        private static Dictionary<PsDict, PsType3Font> _type3Fonts;

        /// <summary>The recognised Type 3 font for a dictionary, registering it on first sight.</summary>
        private static PsType3Font Type3For(PsDict font)
        {
            if (font == null) return null;

            _type3Fonts ??= new Dictionary<PsDict, PsType3Font>();
            if (_type3Fonts.TryGetValue(font, out var known)) return known;

            if (!PsType3Font.TryRecognise(font, out var recognised)) return null;

            _type3Fonts[font] = recognised;
            return recognised;
        }

        private static void UndefineFont(PsInterpreter i)
        {
            var key = i.Operands.Pop();
            if (key.Type != PsType.Name) return;

            _definedFonts?.Remove(key.AsName());
            i.FontDirectory.Remove(PsObject.LitName(key.AsName()));
        }

        /// <summary>
        /// <c>composefont</c> — builds a CJK composite font from a face and a CMap.
        ///
        /// <para>The operand arrangement varies between producers, so this takes up to three
        /// operands and picks out whichever is a CMap the program defined (a CMap dictionary, or its
        /// key in the CMap category) or names a <b>registered</b> one, rather than insisting on one
        /// order. What matters is which CMap the font is composed with: a registered one's name goes
        /// straight into the emitted Type0 font's <c>/Encoding</c>, a program's own is embedded there
        /// as a CMap stream, and the reader then performs exactly the byte-to-CID decoding the
        /// PostScript intended.</para>
        /// </summary>
        private static void ComposeFont(PsInterpreter i)
        {
            var operands = new List<PsObject>();
            for (int k = 0; k < 3 && i.Operands.Count > 0; k++) operands.Add(i.Operands.Pop());

            string cmapName = null;
            string face = null;
            PsDict cmapDict = null;

            foreach (var o in operands)
            {
                // A CMap the program defined comes first: its definition is the one the program composes
                // with, under a predefined name too.
                if (cmapName == null && PsCMapOps.Named(i, o) is { CMap: { } defined } named)
                {
                    cmapName = defined.Name;
                    cmapDict = named.Dict;
                    continue;
                }
                string text = o.Type switch
                {
                    PsType.Name   => o.AsName(),
                    PsType.String => i.LatinFromOperand(o.AsString(), "composefont"),
                    PsType.Array  => FirstNameIn(i, o.AsArray()),
                    _             => null
                };
                if (text == null) continue;

                if (cmapName == null && PsCidFonts.IsKnownCMap(text)) { cmapName = text; continue; }
                face ??= text;
            }

            if (cmapName == null)
            {
                PostScriptDiagnostics.Report("PsTextOps.ComposeFont",
                    "'composefont' named neither a CMap the program defined nor a registered one. CJK conversion needs the Adobe CMap " +
                    "plugin registered — PdfPluginHost.Instance.Register(new AdobeCMapPlugin()) — " +
                    "without which the composite font cannot be built and its text will not render");
                i.Operands.Push(PsObject.Dict(new PsDict()));
                return;
            }

            // The descendant: the CIDFont the array holds or names (PLRM: looked up in the CIDFont
            // category). One the document embedded — a Type 2 CIDFont — is what the composite's text
            // draws in: the composite names its program and keeps the CIDFont as /FDepVector, whose
            // /CIDMap, /GlyphDirectory and /Metrics the text operators read.
            PsDict descendant = null;
            foreach (var o in operands)
                if (o.Type == PsType.Array && o.AsArray().Length > 0) descendant = CidFontNamed(i, o.AsArray()[0]);
            PsEmbeddedFont program = EmbeddedProgramOf(descendant) is { } cid
                                     && (cid.TrueType is { IsCidKeyed: true } || cid.Cff is { IsCidKeyed: true } || cid.CidType0 != null)
                ? cid : null;

            i.ChargeAllocation(7 * PsAllocationBudget.BytesPerDictEntry, "composefont");

            var font = new PsDict(7);
            font.PutName(KeyBaseFont, PsObject.LitName(program?.FontName ?? face ?? "Unknown"));
            font.PutName(KeyCMap, PsObject.LitName(cmapName));
            if (cmapDict != null) font.PutName("CMap", PsObject.Dict(cmapDict));   // PLRM Table 5.8: a Type 0 font's CMap dictionary
            font.PutName(KeyMatrix, MatrixObject(i, PsMatrix.Identity));
            if (program != null)
            {
                font.PutName(KeyEmbedded, descendant.TryGetName(KeyEmbedded, out PsObject key) ? key : PsObject.Null);
                font.PutName("FDepVector", PsObject.Arr(new PsArray(new[] { PsObject.Dict(descendant) })));
                font.PutName("FontType", PsObject.Int(0));
                // The glyph space of a Type 2 CIDFont is its program's em, mapped by its /FontMatrix
                // (identity as a rule), which the composite's own identity matrix composes with. A CFF
                // CIDFont's widths are read in thousandths of the em already, through its own matrix.
                if (program.TrueType != null
                    && descendant.TryGetName(KeyMatrix, out PsObject cidMatrix) && cidMatrix.Type == PsType.Array
                    && PsMatrix.TryFromOperands(cidMatrix.AsArray(), out PsMatrix glyphMatrix))
                    font.PutName(KeyMatrix, MatrixObject(i, glyphMatrix));
            }

            // composefont defines the font under its key in the Font category (PLRM §8.2).
            if (operands.Count == 3 && operands[2].Type is PsType.Name or PsType.String)
            {
                string name = operands[2].Type == PsType.Name ? operands[2].AsName() : i.LatinFromOperand(operands[2].AsString(), "composefont");
                _definedFonts ??= new Dictionary<string, PsDict>(StringComparer.Ordinal);
                _definedFonts[name] = font;
                i.FontDirectory.PutName(name, PsObject.Dict(font));
            }
            i.Operands.Push(PsObject.Dict(font));
        }

        /// <summary>Makes a CIDFont dictionary <c>StartData</c> built name its program, as <see cref="DefineCidFont"/> does for a Type 2 one.</summary>
        internal static void AttachCidProgram(PsDict cidFont, string name, PsEmbeddedFont program)
            => AttachEmbedded(cidFont, CidFontKeyPrefix + name, program);

        /// <summary>
        /// The CIDFont dictionary <paramref name="element"/> is, or names in the CIDFont category
        /// (PLRM <c>composefont</c>: CIDFont first), or null.
        /// </summary>
        private static PsDict CidFontNamed(PsInterpreter i, PsObject element)
        {
            if (element.Type == PsType.Dictionary) return element.AsDict();
            if (element.Type is PsType.Name or PsType.String
                && PsResourceOps.TryFindInstance(i, "CIDFont", element, out PsObject found) && found.Type == PsType.Dictionary)
                return found.AsDict();
            return null;
        }

        /// <summary>
        /// <c>key dict /CIDFont defineresource</c>: a Type 2 CIDFont's program — a Type 42 program
        /// reached by CID through <c>/CIDMap</c> — read and attached, its <c>/FontType</c> 42 made 11 as
        /// PLRM's <c>defineresource</c> makes it (Table 5.12); a Type 0 CIDFont's, Type 1 charstrings by
        /// CID in the data <c>StartData</c> read (<see cref="Emit.PsCidType0Font"/>), likewise. One this
        /// converter cannot carry, and a CIDFont of another type, is said: its text draws in the face its
        /// name maps to.
        /// </summary>
        internal static void DefineCidFont(PsInterpreter i, string name, PsDict font)
        {
            if (EmbeddedProgramOf(font) != null) return;
            if (Emit.PsCidType0Font.IsCidFontType0(font))
            {
                var type0 = Emit.PsCidType0Font.TryRead(name, font, n => i.ChargeAllocation(n, "defineresource"), out string reason);
                if (type0 == null)
                    PostScriptDiagnostics.Report("PsTextOps.DefineCidFont",
                        $"Type 0 CIDFont '{name}' was not carried — {reason} — so its text is drawn in the face its name maps to");
                else
                    AttachEmbedded(font, CidFontKeyPrefix + name, new PsEmbeddedFont { FontName = name, CidType0 = type0 });
                return;
            }
            if (!Emit.PsType42Font.IsCidFontType2(font))
            {
                PostScriptDiagnostics.ReportUnimplemented("CIDFont resource (a CIDFontType other than 0 or 2)");
                return;
            }

            var program = Emit.PsType42Font.TryReadCid(font, n => i.ChargeAllocation(n, "defineresource"), out string refusal);
            if (program == null)
            {
                PostScriptDiagnostics.Report("PsTextOps.DefineCidFont",
                    $"Type 2 CIDFont '{name}' was not carried — {refusal} — so its text is drawn in the face its name maps to");
                return;
            }
            AttachEmbedded(font, CidFontKeyPrefix + name, new PsEmbeddedFont { FontName = name, TrueType = program });
            font.PutName("FontType", PsObject.Int(11));
        }

        /// <summary>
        /// <c>findencoding</c> — <c>key findencoding array</c>.
        ///
        /// <para>For the two Appendix E vectors this pushes the <b>array</b> the PLRM specifies, so
        /// <c>/ISOLatin1Encoding findencoding</c> and the bare name <c>ISOLatin1Encoding</c> agree.
        /// Pushing the key back as a literal name for <i>every</i> input would be a
        /// <c>typecheck</c> waiting to happen at the first <c>get</c> or <c>aload</c> — and, worse,
        /// a font dictionary whose <c>/Encoding</c> is a name rather than an array, which
        /// <see cref="EncodingDifferences"/> declines, dropping the re-encoding and leaving the font
        /// on WinAnsi with nothing reported.</para>
        ///
        /// <para><b>The name-passthrough is kept for anything else</b>, and is not a fallback for
        /// tidiness: a CJK <c>composefont</c> reaches this operator with a predefined <b>CMap</b>
        /// name, which is not an encoding vector at all and must survive as a name for
        /// <see cref="ComposeFont"/> to recognise. That case is not reported, because passing the
        /// name through is what makes it work.</para>
        /// </summary>
        private static void FindEncoding(PsInterpreter i)
        {
            var key = i.Operands.Pop();
            if (key.Type != PsType.Name) { i.Operands.Push(key); return; }

            var encoding = PsEncodings.Build(key.AsName());
            i.Operands.Push(encoding != null ? PsObject.Arr(encoding) : PsObject.LitName(key.AsName()));
        }

        /// <summary>
        /// The first name in <paramref name="array"/>, charged a tick a 64 elements examined, as a
        /// copy is: a loop of <c>composefont</c> over a large array of numbers scanned it whole for
        /// one tick.
        /// </summary>
        private static string FirstNameIn(PsInterpreter i, PsArray array)
        {
            int k = 0;
            while (k < array.Length && array[k].Type != PsType.Name) k++;
            i.Tick(k / 64);
            return k < array.Length ? array[k].AsName() : null;
        }

        /// <summary>
        /// The font dictionary <c>/Encoding</c> array as code to glyph name, or null when it carries
        /// none.
        ///
        /// <para>Only entries that name a real glyph are carried: an array whose every slot is
        /// <c>.notdef</c> says nothing, and emitting 256 <c>.notdef</c> names would make the PDF font
        /// <i>worse</i> than leaving it on WinAnsi.</para>
        /// </summary>
        private static Dictionary<int, string> EncodingDifferences(PsDict font)
        {
            if (font == null || !font.TryGetName(KeyEncoding, out var enc)) return null;
            if (enc.Type != PsType.Array) return null;

            var array = enc.AsArray();
            var map = new Dictionary<int, string>();

            for (int code = 0; code < array.Length && code < 256; code++)
            {
                var entry = array[code];
                if (entry.Type != PsType.Name) continue;

                string glyph = entry.AsName();
                if (string.IsNullOrEmpty(glyph) || glyph == ".notdef") continue;
                map[code] = glyph;
            }

            return map.Count > 0 ? map : null;
        }

        // ------------------------------------------------------------------
        //  Showing
        // ------------------------------------------------------------------

        private static void Show(PsInterpreter i, string op,
                                 double charSpacing = 0, double wordSpacing = 0)
        {
            var text = i.Operands.PopString(op);
            Paint(i, op, text, charSpacing, wordSpacing);
        }

        /// <summary><c>ax ay string ashow</c> — extra spacing added after every glyph.</summary>
        private static void AShow(PsInterpreter i)
        {
            var text = i.Operands.PopString("ashow");
            double ay = i.Operands.PopNumber("ashow");
            double ax = i.Operands.PopNumber("ashow");

            if (ay != 0)
                PostScriptDiagnostics.Report("PsTextOps.AShow",
                    "'ashow' with a non-zero vertical adjustment was approximated: PDF's Tc adds " +
                    "horizontal spacing only");

            Paint(i, "ashow", text, charSpacing: ax, wordSpacing: 0, charSpacingY: ay);
        }

        /// <summary><c>cx cy char string widthshow</c> — extra spacing after one specific byte.</summary>
        private static void WidthShow(PsInterpreter i)
        {
            var text = i.Operands.PopString("widthshow");
            int ch = i.Operands.PopInt("widthshow");
            double cy = i.Operands.PopNumber("widthshow");
            double cx = i.Operands.PopNumber("widthshow");

            // PDF's Tw applies to byte 32 only. For any other character the adjustment cannot be
            // expressed, so it is reported rather than silently applied to the wrong glyph.
            if (ch != 32 && cx != 0)
                PostScriptDiagnostics.Report("PsTextOps.WidthShow",
                    $"'widthshow' adjusts byte {ch}; PDF's Tw applies only to the space character, " +
                    "so the adjustment was dropped");
            if (cy != 0)
                PostScriptDiagnostics.Report("PsTextOps.WidthShow",
                    "'widthshow' with a non-zero vertical adjustment was approximated");

            Paint(i, "widthshow", text, charSpacing: 0, wordSpacing: ch == 32 ? cx : 0,
                  wordSpacingY: ch == 32 ? cy : 0);
        }

        /// <summary>
        /// <c>cx cy char ax ay string awidthshow</c> — <c>widthshow</c>'s per-character adjustment
        /// plus <c>ashow</c>'s per-glyph one.
        ///
        /// <para>The pop order is the operand order reversed. Read as
        /// <c>ax ay cx cy char string</c>, the word-spacing amount would be applied to every glyph,
        /// the letter spacing dropped, and a non-integral <c>ay</c> raise a typecheck from the
        /// <c>char</c> slot.</para>
        /// </summary>
        private static void AWidthShow(PsInterpreter i)
        {
            var text = i.Operands.PopString("awidthshow");
            double ay = i.Operands.PopNumber("awidthshow");
            double ax = i.Operands.PopNumber("awidthshow");
            int ch = i.Operands.PopInt("awidthshow");
            double cy = i.Operands.PopNumber("awidthshow");
            double cx = i.Operands.PopNumber("awidthshow");

            // Same report widthshow makes: PDF's Tw applies to byte 32 only.
            if (ch != 32 && cx != 0)
                PostScriptDiagnostics.Report("PsTextOps.AWidthShow",
                    $"'awidthshow' adjusts byte {ch}; PDF's Tw applies only to the space character, " +
                    "so the adjustment was dropped");

            if (cy != 0 || ay != 0)
                PostScriptDiagnostics.Report("PsTextOps.AWidthShow",
                    "'awidthshow' with a non-zero vertical adjustment was approximated");

            Paint(i, "awidthshow", text, charSpacing: ax, wordSpacing: ch == 32 ? cx : 0,
                  charSpacingY: ay, wordSpacingY: ch == 32 ? cy : 0);
        }

        /// <summary>
        /// Emits the text and advances the current point by its width, so a following
        /// <c>currentpoint</c> or relative move lands where PostScript would put it.
        /// </summary>
        private static void Paint(PsInterpreter i, string op, PsString text,
                                  double charSpacing, double wordSpacing,
                                  double charSpacingY = 0, double wordSpacingY = 0)
        {
            text = PsCShowSelection.Resolve(i, op, text);
            var g = i.Graphics;
            if (g == null) return;

            if (!i.Options.AllowText)
            {
                PostScriptDiagnostics.ReportUnimplemented(op);
                return;
            }

            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined, op, "no current point");

            // A Type 3 font's glyphs are procedures: they are RUN, and what they draw becomes the
            // emitted font's CharProcs. Routed before the base-font fallback below, which would
            // silently make such a font Helvetica.
            if (i.CurrentType3Font != null)
            {
                PaintType3(i, g, i.CurrentType3Font, text, charSpacing, wordSpacing,
                           charSpacingY, wordSpacingY);
                return;
            }

            string baseFont = g.Gs.FontResourceName;
            if (string.IsNullOrEmpty(baseFont))
            {
                // PLRM says showing with no font set is an error. Reporting and substituting keeps
                // the rest of the illustration, which is worth more than the strict verdict.
                baseFont = Core.Constants.StandardFonts.Helvetica;

                // A Type 3 font cannot reach here: PaintType3 above returns before this. So this
                // means what it says — no `setfont` ran at all.
                PostScriptDiagnostics.Report("PsTextOps",
                    $"'{op}' ran with no font set; Helvetica at size {DefaultSize} was used");
                g.Gs.FontResourceName = baseFont;
                i.CurrentFontMatrix = PsMatrix.Scale(DefaultSize, DefaultSize);
            }

            var (x, y) = g.Path.CurrentPoint.Value;

            // Charged before the copy is made, not after. Everything below is O(string length) — a
            // byte-array copy, one metric lookup per byte, and one Tj operand byte per byte — while
            // a single tick for `show` would let `/s 1000000 string def { s show } loop` buy
            // ~100 000 iterations of megabyte work for a 40-byte program. PsArrayStringOps.Search and
            // PositionedShow charge their counts the same way.
            ChargeTextWork(i, op, text);
            byte[] bytes = text.ToArray();

            // An embedded TrueType program — a Type 42 font, or a Type 2 CIDFont under its composite
            // font — draws its own glyphs by index.
            if (TrueTypeGlyphs(i, bytes, op) is { } glyphs)
            {
                PaintTrueType(i, g, glyphs, charSpacing, wordSpacing, charSpacingY, wordSpacingY);
                return;
            }

            if (i.CurrentFontCMap is { } cffCmap && i.CurrentEmbeddedFont?.Cff is { IsCidKeyed: true })
            {
                PaintCidCff(i, g, cffCmap, bytes, x, y, charSpacing, wordSpacing, charSpacingY, wordSpacingY);
                return;
            }

            if (i.CurrentFontCMap != null)
            {
                PaintCid(i, g, baseFont, i.CurrentFontCMap, bytes, x, y, charSpacing, wordSpacing);
                return;
            }

            var encoding = i.CurrentFontEncoding;

            // The document's own font program wins over any standard-14 substitution: it is the only
            // thing that can carry Hebrew, Cyrillic or any other script the standard 14 lack.
            string resource;
            if (i.CurrentEmbeddedFont != null)
            {
                resource = g.Resources.GetOrAddEmbeddedFont(i.CurrentEmbeddedFont, encoding);
                encoding ??= i.CurrentEmbeddedFont.BuiltInEncoding;
            }
            else
            {
                resource = g.Resources.GetOrAddFont(baseFont, encoding);
            }

            // The text matrix carries the pen position AND the font matrix, so `Tf` can use size 1
            // and a makefont skew or non-uniform scale survives — PDF has no other way to say it.
            var fm = i.CurrentFontMatrix;
            var tm = TextMatrixAt(fm, x, y);

            g.Emitter.ShowText(resource, tm, bytes, g.Gs.Color,
                               ToTextSpace(fm, charSpacing), ToTextSpace(fm, wordSpacing));

            // An embedded program brings its own glyphs, so measuring them against a standard-14
            // table would report a problem that does not exist.
            if (i.CurrentEmbeddedFont == null) ReportUnrepresentable(baseFont, bytes, encoding);

            // The glyph widths through the font matrix (PLRM §5.3); the spacing in user space.
            double along = MeasureEm(i, baseFont, bytes, encoding);
            int spaces = CountSpaces(bytes);
            AdvancePen(g, fm, along,
                       charSpacing  * bytes.Length + wordSpacing  * spaces,
                       charSpacingY * bytes.Length + wordSpacingY * spaces);
        }

        /// <summary>
        /// Shows text in a Type 3 font: builds each glyph's CharProc by running its procedure, then
        /// emits an ordinary <c>Tj</c> against the assembled PDF Type 3 font.
        ///
        /// <para>The font is registered <b>after</b> the glyphs are built, because its
        /// <c>/CharProcs</c>, <c>/Widths</c>, <c>/FirstChar</c> and <c>/LastChar</c> all depend on
        /// which codes were built — and because a glyph that fails to build must not leave a font
        /// resource behind that no <c>Tj</c> names.</para>
        ///
        /// <para>The advance is <c>wx × FontMatrix[0] × size</c>: the width is in glyph space, and
        /// the size is carried in the text matrix rather than in <c>Tf</c> (which stays 1), so the
        /// two multiply exactly once.</para>
        /// </summary>
        private static void PaintType3(PsInterpreter i, PsGraphicsEngine g, PsType3Font font,
                                       PsString text, double charSpacing, double wordSpacing,
                                       double charSpacingY, double wordSpacingY)
        {
            ChargeTextWork(i, "show", text);
            byte[] bytes = text.ToArray();

            var built = new Dictionary<int, PsBuiltGlyph>();
            foreach (byte b in bytes)
            {
                if (built.ContainsKey(b)) continue;
                var glyph = PsType3GlyphBuilder.Build(i, font, b);
                if (glyph != null) built[b] = glyph;
            }

            var (x, y) = g.Path.CurrentPoint.Value;
            var fm = i.CurrentFontMatrix;

            if (built.Count == 0)
            {
                // Nothing built — every code was refused, and each said why. Advance by nothing
                // rather than substituting: a substituted face here would be the silent Helvetica
                // this whole path exists to stop.
                g.Path.MoveTo(x, y);
                return;
            }

            // font.Built, NOT the per-string `built`: the font carries every glyph built so far.
            // Passing only this string's made each `show` register a font holding one glyph.
            string resource = g.Resources.AddType3Font(font, font.Built, font.GlyphResources);

            var tm = TextMatrixAt(fm, x, y);
            g.Emitter.ShowText(resource, tm, bytes, g.Gs.Color,
                               ToTextSpace(fm, charSpacing), ToTextSpace(fm, wordSpacing));

            // Glyph-space widths into text space through the glyph matrix, then through the font
            // matrix like every other advance; the spacing in user space.
            double along = 0;
            foreach (byte b in bytes)
                along += (built.TryGetValue(b, out var glyph) ? glyph.Wx : 0) * font.GlyphMatrix.A;

            int spaces = CountSpaces(bytes);
            AdvancePen(g, fm, along,
                       charSpacing  * bytes.Length + wordSpacing  * spaces,
                       charSpacingY * bytes.Length + wordSpacingY * spaces);
        }

        /// <summary>
        /// Converts a user-space spacing adjustment into the text space <c>Tc</c>/<c>Tw</c> live in.
        ///
        /// <para>PostScript's <c>ashow</c>/<c>widthshow</c> adjustments are <b>user-space</b>
        /// distances. PDF computes the glyph displacement as
        /// <c>(w0·Tfs + Tc + Tw)·Th</c> in <i>text</i> space and only then maps it through
        /// <c>Tm</c> (§9.4.4) — and this emitter deliberately puts the font size in <c>Tm</c> and
        /// uses <c>Tf</c> size 1, so a raw <c>5 Tc</c> under <c>12 0 0 12 … Tm</c> became 60 points
        /// per glyph. The run came out 12x too loose, scaling with the font size, and disagreed with
        /// the interpreter's own pen advance, so anything positioned after the show landed on top of
        /// the text.</para>
        ///
        /// <para>The divisor is the length of the matrix's x basis vector, so a <c>makefont</c> skew
        /// or rotation is handled rather than only a uniform scale. A degenerate matrix drops the
        /// adjustment instead of dividing by zero.</para>
        /// </summary>
        private static double ToTextSpace(in PsMatrix fontMatrix, double userSpaceAmount)
        {
            if (userSpaceAmount == 0) return 0;

            double scale = Math.Sqrt(fontMatrix.A * fontMatrix.A + fontMatrix.B * fontMatrix.B);
            return scale > 1e-9 ? userSpaceAmount / scale : 0;
        }

        /// <summary>
        /// Shows text through a CJK composite font.
        ///
        /// <para>The bytes go into <c>Tj</c> <b>unchanged</b>: the PDF Type0 font names the same
        /// predefined CMap the PostScript composed with, or embeds the one the program defined, so the
        /// reader performs the identical byte-to-CID decoding. Nothing here needs to understand
        /// Shift-JIS, Big5 or EUC.</para>
        ///
        /// <para>The advance uses the descendant font's <c>/DW 1000</c>, which is right for the
        /// full-width glyphs that dominate CJK text and wrong for a proportional Latin run inside
        /// one. Reported once, because per-CID widths would have to come from the font program.</para>
        /// </summary>
        private static void PaintCid(PsInterpreter i, Graphics.PsGraphicsEngine g, string face,
                                     string cmapName, byte[] bytes, double x, double y,
                                     double charSpacing, double wordSpacing)
        {
            string resource = g.Resources.GetOrAddCidFont(face, cmapName, PsCMapOps.Current(i));

            var fm = i.CurrentFontMatrix;
            var tm = TextMatrixAt(fm, x, y);
            g.Emitter.ShowText(resource, tm, bytes, g.Gs.Color,
                               ToTextSpace(fm, charSpacing), ToTextSpace(fm, wordSpacing));

            ReportCidMetrics(face, cmapName);

            // One em per code (/DW 1000), through the font matrix; the spacing in user space.
            int codes = PsCMapOps.CountCodes(i, cmapName, bytes);
            AdvancePen(g, fm, codes, charSpacing * codes);
        }

        /// <summary>
        /// The glyphs a Type 42 font shows for <paramref name="bytes"/>: each code's glyph name through
        /// the <c>/Encoding</c> of the font in use as it stands now — a driver fills it after
        /// <c>setfont</c> — its index through <c>/CharStrings</c>, written as the two bytes the Type0
        /// font's <c>/Identity-H</c> reads, and its advance in em (<see cref="Emit.PsType42Font.Glyph"/>
        /// takes each glyph the first time it is used).
        /// </summary>
        private static List<(byte[] Code, double Em, bool Space)> Type42Glyphs(PsInterpreter i, Emit.PsType42Font font,
                                                                               byte[] bytes, string op)
        {
            PsDict dict = i.CurrentFontDict ?? new PsDict();
            var glyphs = new List<(byte[], double, bool)>(bytes.Length);
            foreach (byte b in bytes)
            {
                int glyph = font.Glyph(dict, Type42Name(dict, b), n => i.ChargeAllocation(n, op), out double advance);
                glyphs.Add((GlyphCode(glyph), advance * GlyphSpace, b == 32));
            }
            return glyphs;
        }

        /// <summary>A glyph index as the two bytes the Type0 font's <c>/Identity-H</c> reads.</summary>
        private static byte[] GlyphCode(int glyph) => new[] { (byte)(glyph >> 8), (byte)glyph };

        /// <summary>
        /// The glyphs a Type 2 CIDFont shows for <paramref name="bytes"/> under the composite font's
        /// CMap: each code's CID through Core's <see cref="Core.Fonts.CidCollectionText"/> (the CMap's
        /// codespace and mappings, as the reader decodes them), its glyph through the CIDFont's
        /// <c>/CIDMap</c>, or a Type 0 CIDFont's charstring (<see cref="Emit.IPsGlyphIndexedProgram.GlyphOfCid"/>), with its text in the CIDFont's
        /// collection; a code that is the one byte 32 is a space for word spacing. Null when the CMap
        /// cannot be read here — neither Identity nor registered.
        /// </summary>
        private static List<(byte[] Code, double Em, bool Space)> CidGlyphs(PsInterpreter i, Emit.IPsGlyphIndexedProgram font,
                                                                            string cmap, byte[] bytes, string op)
        {
            PsDict cidFont = DescendantOf(i.CurrentFontDict) ?? new PsDict();
            string ordering = cidFont.TryGetName("CIDSystemInfo", out PsObject info) && info.Type == PsType.Dictionary
                              && info.AsDict().TryGetName("Ordering", out PsObject o) && o.Type == PsType.String
                ? o.AsString().AsLatin1()
                : PsCMapOps.Ordering(i, cmap);
            var codes = PsCMapOps.Decode(i, cmap, ordering, bytes);
            if (codes == null) return null;

            var glyphs = new List<(byte[], double, bool)>(codes.Count);
            int at = 0;
            foreach (var code in codes)
            {
                int glyph = font.GlyphOfCid(cidFont, code.Cid, code.Text, n => i.ChargeAllocation(n, op), out double advance);
                glyphs.Add((GlyphCode(glyph), advance * GlyphSpace, code.Length == 1 && bytes[at] == 32));
                at += code.Length;
            }
            return glyphs;
        }

        /// <summary>
        /// The codes a CID-keyed CFF font shows for <paramref name="bytes"/> under the composite font's
        /// CMap — each code's length in the string, its advance in em (the font's own width for its CID,
        /// through Core's <see cref="Core.Fonts.CidCollectionText"/> and the font's charset, recorded for
        /// its <c>/W</c>), and whether it is the one byte 32 — or null when the font is no CID-keyed CFF
        /// font, or the CMap cannot be read here. The bytes themselves pass into <c>Tj</c> unchanged: the
        /// reader decodes them through the same CMap.
        /// </summary>
        private static List<(int Length, double Em, bool Space)> CffCidCodes(PsInterpreter i, byte[] bytes)
        {
            if (i.CurrentFontCMap is not { } cmap || i.CurrentEmbeddedFont is not { Cff: { IsCidKeyed: true } } font)
                return null;
            var codes = PsCMapOps.Decode(i, cmap, font.Cff.Ordering, bytes);
            if (codes == null) return null;

            var list = new List<(int, double, bool)>(codes.Count);
            int at = 0;
            foreach (var code in codes)
            {
                list.Add((code.Length, font.CidAdvance(code.Cid) * GlyphSpace, code.Length == 1 && bytes[at] == 32));
                at += code.Length;
            }
            return list;
        }

        /// <summary>
        /// Shows text in a CID-keyed CFF font under its composite font's CMap: the bytes unchanged, in the
        /// Type0 font naming that CMap over the font's own program; the pen moved by the font's widths.
        /// A CMap Core cannot read leaves the advance at an em a code, said once as the predefined-CMap
        /// path says it.
        /// </summary>
        private static void PaintCidCff(PsInterpreter i, PsGraphicsEngine g, string cmap, byte[] bytes, double x, double y,
                                        double charSpacing, double wordSpacing, double charSpacingY, double wordSpacingY)
        {
            string resource = g.Resources.GetOrAddCidCffFont(i.CurrentEmbeddedFont, cmap, PsCMapOps.Current(i));
            var fm = i.CurrentFontMatrix;
            g.Emitter.ShowText(resource, TextMatrixAt(fm, x, y), bytes, g.Gs.Color,
                               ToTextSpace(fm, charSpacing), ToTextSpace(fm, wordSpacing));

            if (CffCidCodes(i, bytes) is not { } codes)
            {
                ReportCidMetrics(i.CurrentEmbeddedFont.FontName, cmap);
                int count = PsCMapOps.CountCodes(i, cmap, bytes);
                AdvancePen(g, fm, count, charSpacing * count, charSpacingY * count);
                return;
            }

            double along = 0;
            int spaces = 0;
            foreach (var (_, em, space) in codes)
            {
                along += em;
                if (space) spaces++;
            }
            AdvancePen(g, fm, along,
                       charSpacing  * codes.Count + wordSpacing  * spaces,
                       charSpacingY * codes.Count + wordSpacingY * spaces);
        }

        /// <summary>A composite font's descendant CIDFont — the first of its <c>/FDepVector</c> — or null.</summary>
        private static PsDict DescendantOf(PsDict font)
            => font != null && font.TryGetName("FDepVector", out PsObject vector) && vector.Type == PsType.Array
               && vector.AsArray().Length > 0 && vector.AsArray()[0].Type == PsType.Dictionary
                ? vector.AsArray()[0].AsDict()
                : null;

        /// <summary>
        /// The glyphs an embedded TrueType program shows for <paramref name="bytes"/>: a Type 2
        /// CIDFont's through the composite font's CMap, a Type 42 font's through its encoding — or null
        /// when the font has no such program, or its CMap cannot be read.
        /// </summary>
        private static List<(byte[] Code, double Em, bool Space)> TrueTypeGlyphs(PsInterpreter i, byte[] bytes, string op)
        {
            if (i.CurrentEmbeddedFont?.GlyphIndexed is not { } program) return null;
            if (program is Emit.PsType42Font { IsCidKeyed: false } type42)
                return i.CurrentFontCMap == null ? Type42Glyphs(i, type42, bytes, op) : null;
            return i.CurrentFontCMap is { } cmap ? CidGlyphs(i, program, cmap, bytes, op) : null;
        }

        /// <summary>The glyph name <paramref name="code"/> selects through <paramref name="font"/>'s <c>/Encoding</c> array, StandardEncoding's without one.</summary>
        private static string Type42Name(PsDict font, byte code)
        {
            if (!font.TryGetName(KeyEncoding, out var encoding) || encoding.Type != PsType.Array)
                return PsEncodings.StandardName(code) ?? ".notdef";
            PsArray names = encoding.AsArray();
            return code < names.Length && names[code].Type == PsType.Name ? names[code].AsName() : ".notdef";
        }

        /// <summary>
        /// Shows text in an embedded TrueType program — a Type 42 font, or a Type 2 CIDFont under its
        /// composite font — as <paramref name="glyphs"/>' indices, two bytes each, in the Type0 font the
        /// collector makes for it. <c>Tw</c> spaces a one-byte code 32 alone (ISO 32000-1 §9.3.3), so
        /// where word spacing applies the run is split after each space and each piece is placed at its
        /// own pen; <c>Tc</c> applies to every glyph and carries the letter spacing.
        /// </summary>
        private static void PaintTrueType(PsInterpreter i, PsGraphicsEngine g, List<(byte[] Code, double Em, bool Space)> glyphs,
                                          double charSpacing, double wordSpacing,
                                          double charSpacingY, double wordSpacingY)
        {
            string resource = g.Resources.GetOrAddGlyphIndexedFont(i.CurrentEmbeddedFont);
            var fm = i.CurrentFontMatrix;
            double tc = ToTextSpace(fm, charSpacing);
            bool split = wordSpacing != 0 || wordSpacingY != 0;

            var (x, y) = g.Path.CurrentPoint.Value;
            double along = 0, runEm = 0;
            int from = 0, spaces = 0;
            for (int k = 0; k < glyphs.Count; k++)
            {
                along += glyphs[k].Em;
                runEm += glyphs[k].Em;
                bool space = glyphs[k].Space;
                if (space) spaces++;
                if (k < glyphs.Count - 1 && !(split && space)) continue;

                int count = k - from + 1;
                var run = new byte[count * 2];
                for (int n = 0; n < count; n++) Array.Copy(glyphs[from + n].Code, 0, run, n * 2, 2);
                // The advance a reader moves by: the /W widths are these glyphs' advances (the
                // collector writes them from what the pen moved), and Tc once a glyph.
                g.Emitter.ShowText(resource, TextMatrixAt(fm, x, y), run, g.Gs.Color, tc, 0,
                                   advance: runEm + tc * count);

                var (dx, dy) = fm.ApplyDelta(runEm, 0);
                x += dx + charSpacing * count + (space ? wordSpacing : 0);
                y += dy + charSpacingY * count + (space ? wordSpacingY : 0);
                runEm = 0;
                from = k + 1;
            }

            AdvancePen(g, fm, along,
                       charSpacing  * glyphs.Count + wordSpacing  * spaces,
                       charSpacingY * glyphs.Count + wordSpacingY * spaces);
        }

        /// <summary>The once-per-conversion note that composite advances are /DW 1000.</summary>
        private static void ReportCidMetrics(string face, string cmapName)
        {
            if (_cidMetricsReported) return;
            _cidMetricsReported = true;
            PostScriptDiagnostics.Report("PsTextOps",
                $"composite font '{face}' under CMap '{cmapName}' was emitted as a Type0 font " +
                "naming that CMap, so the reader decodes the bytes exactly as the PostScript " +
                "did — but per-CID widths need the font program, so advances use the default " +
                "/DW 1000. Full-width CJK is correct; a proportional Latin run inside the same " +
                "font will be spaced as full-width. Reported once per conversion.");
        }

        [ThreadStatic] private static bool _cidMetricsReported;

        private const double DefaultSize = 12.0;

        // One report per conversion: a Hebrew page shows hundreds of strings and every one of them
        // has the same cause, so reporting each would bury everything else in the diagnostics.
        [ThreadStatic] private static bool _unrepresentableReported;

        /// <summary>
        /// Reports text the mapped font cannot draw.
        ///
        /// <para>This is the one text failure that is otherwise <b>invisible</b>: the conversion
        /// succeeds, the content stream is valid, and the reader draws real glyphs — just the wrong
        /// ones. A legacy Hebrew, Greek or Cyrillic document encodes its text in a single-byte
        /// codepage and re-encodes a base font to match with <c>definefont</c>; with neither
        /// embedded fonts nor <c>definefont</c> implemented, those bytes are handed to the reader as
        /// WinAnsi and <c>שלום</c> is drawn as <c>éìåí</c>.</para>
        ///
        /// <para>Without this the result would report <c>IsClean</c>, which would be a lie about the
        /// one thing a caller most needs to know.</para>
        /// </summary>
        private static void ReportUnrepresentable(string baseFont, byte[] bytes,
                                                  IReadOnlyDictionary<int, string> encoding)
        {
            if (_unrepresentableReported) return;

            int count = PsFontMapper.CountUnrepresentable(baseFont, bytes, encoding);
            if (count == 0) return;

            _unrepresentableReported = true;

            string cause = encoding != null && encoding.Count > 0
                ? $"the font was re-encoded and its /Differences names glyphs that '{baseFont}' does " +
                  "not contain. Hebrew and Cyrillic need an embedded font program, which is out of " +
                  "scope; Greek works when the base font is Symbol"
                : Core.Fonts.Standard14Metrics.HasBuiltInEncoding(baseFont)
                ? $"'{baseFont}' draws through its own built-in encoding (ISO 32000-1 §9.6.6.4), " +
                  "and those code points are empty in it"
                : $"'{baseFont}' is emitted with WinAnsiEncoding, so the reader will draw the " +
                  "WinAnsi glyphs at those code points. A non-Latin document normally re-encodes a " +
                  "font with 'definefont' and a custom /Encoding array — this one did not";

            PostScriptDiagnostics.Report("PsTextOps",
                $"{count} byte(s) of shown text have no glyph: {cause}. " +
                "Reported once per conversion.");
        }

        /// <summary>Clears the per-conversion one-shot guards. Called by the facade on entry.</summary>
        internal static void ResetPerConversionState()
        {
            _unrepresentableReported = false;
            _cidMetricsReported = false;
            _definedFonts = null;
            _embeddedByKey = null;
            _syntheticGlyphs = null;
            _syntheticGlyphCapReported = false;
            _charPathReported = false;

            // The Type 3 registry was the one slot this reset missed: keyed by dictionary instance,
            // it could never give a later conversion a false hit, but it held every Type 3 font —
            // built CharProcs included — of every conversion the thread had run, for ever.
            _type3Fonts = null;
        }

        /// <summary>
        /// Whether <c>findfont</c> would answer <paramref name="name"/> with the font asked for —
        /// defined, embedded, composite, or a standard face by one of its names — rather than the
        /// Helvetica substitute. What <c>resourcestatus</c> reports for the <c>/Font</c> category.
        /// </summary>
        internal static bool FontExists(PsInterpreter i, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (_definedFonts != null && _definedFonts.ContainsKey(name)) return true;
            if (i.EmbeddedFonts.ContainsKey(name)) return true;
            if (PsCidFonts.TrySplitCompositeName(name, out _, out _)) return true;

            PsFontMapper.Resolve(name, out bool exact);
            return exact;
        }

        /// <summary>
        /// The advance of <paramref name="bytes"/> in text space — font-matrix units — in the
        /// current font, by its kind: a Type 3 font's declared glyph widths through its glyph matrix
        /// (the glyphs are built and memoised exactly as <c>show</c> builds them), one em per code
        /// for a composite font (its descendant declares <c>/DW 1000</c>), and the standard-14
        /// metrics otherwise — for an embedded program too, whose real widths are in charstrings
        /// this path never reads. <c>stringwidth</c> and <c>charpath</c> answered the last of these
        /// whatever the font was, so a Type 3 font's <c>stringwidth</c> was Helvetica's and a
        /// composite font's counted bytes.
        /// </summary>
        private static double MeasureAlong(PsInterpreter i, string baseFont, byte[] bytes)
        {
            if (i.CurrentType3Font is { } type3)
            {
                double along = 0;
                foreach (byte b in bytes)
                    along += (PsType3GlyphBuilder.Build(i, type3, b)?.Wx ?? 0) * type3.GlyphMatrix.A;
                return along;
            }

            if (TrueTypeGlyphs(i, bytes, "stringwidth") is { } trueType)
            {
                double along = 0;
                foreach (var (_, em, _) in trueType) along += em;
                return along;
            }

            if (CffCidCodes(i, bytes) is { } cffCodes)
            {
                double along = 0;
                foreach (var (_, em, _) in cffCodes) along += em;
                return along;
            }

            if (i.CurrentFontCMap is { } cmap) return PsCMapOps.CountCodes(i, cmap, bytes);

            var encoding = i.CurrentFontEncoding ?? i.CurrentEmbeddedFont?.BuiltInEncoding;
            return MeasureEm(i, baseFont, bytes, encoding);
        }

        /// <summary>
        /// The advance of <paramref name="bytes"/> in em fractions: by the embedded program's own glyphs
        /// where the font has one Core can read, else by the standard face <paramref name="baseFont"/>.
        /// </summary>
        private static double MeasureEm(PsInterpreter i, string baseFont, byte[] bytes, IReadOnlyDictionary<int, string> encoding)
            => PsFontMapper.TryMeasureEmbeddedEm(i.CurrentEmbeddedFont, bytes, 0, bytes.Length, encoding, out double em)
                ? em
                : PsFontMapper.MeasureEmWidth(baseFont, bytes, 0, bytes.Length, encoding, out _);

        private static int CountSpaces(byte[] bytes)
        {
            int n = 0;
            foreach (byte b in bytes) if (b == 32) n++;
            return n;
        }

        /// <summary>
        /// <c>string stringwidth wx wy</c> — the advance the string would produce.
        ///
        /// <para>This needs real AFM metrics; returning zero or a constant would silently ruin every
        /// centred or right-aligned label, which is most of them.</para>
        /// </summary>
        private static void StringWidth(PsInterpreter i)
        {
            var text = i.Operands.PopString("stringwidth");
            var (wx, wy) = WidthOf(i, "stringwidth", text);
            i.Operands.Push(PsObject.Real(wx));
            i.Operands.Push(PsObject.Real(wy));
        }

        /// <summary>
        /// The advance <paramref name="text"/> would make in the current font — <c>stringwidth</c>'s
        /// answer, and <c>cshow</c>'s for each character.
        /// </summary>
        internal static (double Wx, double Wy) WidthOf(PsInterpreter i, string op, PsString text)
        {
            string baseFont = i.Graphics?.Gs.FontResourceName
                              ?? Core.Constants.StandardFonts.Helvetica;

            // The worst of the three: `stringwidth` touches neither the path nor the emitter, so
            // nothing else in the interpreter bounds it at all. Uncharged,
            // `/s 1000000 string def { s stringwidth pop pop } loop` bought ~2.5 million iterations
            // of a million dictionary lookups each from a 45-byte program.
            ChargeTextWork(i, op, text);
            byte[] bytes = text.ToArray();
            double em = MeasureAlong(i, baseFont, bytes);

            // The width VECTOR through the font matrix (PLRM §5.3): a rotated font's wy is not 0.
            return i.CurrentFontMatrix.ApplyDelta(em, 0);
        }

        /// <summary>Shows <paramref name="text"/> as <c>show</c> does — <c>kshow</c>'s each character.</summary>
        internal static void ShowText(PsInterpreter i, string op, PsString text) => Paint(i, op, text, 0, 0);

        /// <summary>
        /// <c>charpath</c> — appends the glyph outlines to the current path.
        ///
        /// <para><b>Real outlines where the document embedded the program</b> — each glyph's
        /// charstring run by Core's <see cref="Core.Fonts.Type1.Type1Outlines"/>, or for a CFF font by
        /// Core's Type 2 evaluator (<see cref="Core.Fonts.CffFontSet.Outline"/>); a Type 3 glyph's painted
        /// paths; a standard face's, and a composite font's through its CMap and its collection's
        /// Unicode map, from the host face the text is drawn with. What none of those carries gets its
        /// metric box: what programs do with <c>charpath</c> is clip to text and stroke or fill it, and
        /// a box serves the first and is found by the second. That use is reported once per conversion,
        /// because the result is a rectangle where the author expected letterforms.</para>
        /// </summary>
        private static void CharPath(PsInterpreter i)
        {
            bool stroked = i.Operands.PopBool("charpath");
            var text = PsCShowSelection.Resolve(i, "charpath", i.Operands.PopString("charpath"));

            var g = i.Graphics;
            if (g == null) return;

            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined, "charpath", "no current point");

            string baseFont = g.Gs.FontResourceName ?? Core.Constants.StandardFonts.Helvetica;

            ChargeTextWork(i, "charpath", text);
            byte[] bytes = text.ToArray();

            var fm = i.CurrentFontMatrix;

            // A program shown by glyph index — a Type 42 font, a Type 2 or a Type 0 CIDFont: each
            // glyph's outline from the program, the glyphs a driver downloaded included, the pen
            // advancing as show's does. A glyph the program does not hold keeps its metric box,
            // counted in the report.
            if (i.CurrentType3Font == null && i.CurrentEmbeddedFont?.GlyphIndexed is { } indexed
                && TrueTypeGlyphs(i, bytes, "charpath") is { } glyphs)
            {
                AppendOutlinesOrBoxes(i, g.Path, fm, glyphs.Count, stroked, k =>
                    (glyphs[k].Em, indexed.Outline((glyphs[k].Code[0] << 8) | glyphs[k].Code[1], glyphs[k].Em / GlyphSpace,
                                                   n => i.ChargeAllocation(n, "charpath"))));
                return;
            }

            // A font whose program the document embedded: each glyph's own outline, its charstring
            // run, laid through the font matrix at the pen, the pen advancing by its width; the current
            // point left at the end, as show leaves it (PLRM §8.2). Stroked or not, an outline font's
            // path is its outline. The font matrix is in thousandths of glyph space (DefineFont took
            // the program's own matrix into it).
            if (i.CurrentEmbeddedFont is { Outlines: { } outlines } embedded && i.CurrentFontCMap == null && i.CurrentType3Font == null)
            {
                var encoding = i.CurrentFontEncoding;   // GlyphNameFor falls back to the program's own
                var (px, py) = g.Path.CurrentPoint.Value;
                foreach (byte b in bytes)
                {
                    if (!outlines.TryGetGlyph(embedded.GlyphNameFor(b, encoding), out var glyph)
                        && !outlines.TryGetGlyph(".notdef", out glyph)) continue;
                    AppendOutline(i, g.Path, fm, glyph, px, py);
                    var (ax, ay) = fm.ApplyDelta(glyph.Advance * GlyphSpace, 0);
                    px += ax;
                    py += ay;
                }
                g.Path.MoveTo(px, py);
                return;
            }

            // A CFF font a font set carried: each glyph's charstring run by Core's Type 2 evaluator
            // (CffFontSet), the glyph named as show names it — the font in use's /Encoding, else the
            // set's built-in one — or its .notdef, the pen advancing by the widths show advances by.
            if (i.CurrentEmbeddedFont is { Cff: { IsCidKeyed: false } cff } cffFont && i.CurrentFontCMap == null && i.CurrentType3Font == null)
            {
                var encoding = i.CurrentFontEncoding;
                AppendOutlinesOrBoxes(i, g.Path, fm, bytes, stroked, b =>
                {
                    string name = cffFont.GlyphNameFor(b, encoding);
                    return (cffFont.GlyphAdvance(name) * GlyphSpace, cff.Outline(name) ?? cff.Outline(".notdef"));
                });
                return;
            }

            // A CID-keyed CFF font under its composite font's CMap: each code's CID as show decodes it,
            // its glyph through the font's charset run as above — CID 0's for a CID it lacks — the pen
            // advancing by the font's width for the CID.
            if (i.CurrentFontCMap is { } cidCMap && i.CurrentType3Font == null
                && i.CurrentEmbeddedFont is { Cff: { IsCidKeyed: true } cidCff } cidFont
                && PsCMapOps.Decode(i, cidCMap, cidCff.Ordering, bytes) is { } cids)
            {
                AppendOutlinesOrBoxes(i, g.Path, fm, cids.Count, stroked, k =>
                    (cidFont.CidAdvance(cids[k].Cid) * GlyphSpace, cidCff.CidOutline(cids[k].Cid) ?? cidCff.CidOutline(0)));
                return;
            }

            // A Type 3 font: each glyph built as show builds it, its outline the paths its procedure
            // paints (PsType3Outlines), the pen advancing by its width through the glyph matrix as
            // PaintType3's does. A glyph that paints no path — an image, text, or nothing — and a code
            // with no glyph keep their own metric box, counted in the report.
            if (i.CurrentType3Font is { } type3 && i.CurrentFontCMap == null)
            {
                AppendOutlinesOrBoxes(i, g.Path, fm, bytes, stroked, b =>
                {
                    var built = PsType3GlyphBuilder.Build(i, type3, b);
                    double advance = (built?.Wx ?? 0) * type3.GlyphMatrix.A;
                    return (advance, built != null ? Emit.PsType3Outlines.Of(built, type3.GlyphMatrix) : null);
                });
                return;
            }

            // A standard face: the outlines of the face its text is drawn with (PsStandardOutlines),
            // each code's glyph by the character it denotes through that face's cmap, the pen advancing
            // by the AFM width show advances by and a reader draws with. A code the face has no glyph
            // for keeps its own metric box, and is counted in the report.
            if (i.CurrentEmbeddedFont == null && i.CurrentFontCMap == null && i.CurrentType3Font == null
                && Emit.PsStandardOutlines.For(baseFont) is { } face)
            {
                var differences = i.CurrentFontEncoding;
                AppendOutlinesOrBoxes(i, g.Path, fm, bytes, stroked, b =>
                {
                    double advance = PsFontMapper.MeasureEmWidth(baseFont, new[] { b }, 0, 1, differences, out _);
                    return TryStandardGlyph(face, baseFont, b, differences, out ushort glyphId)
                           && face.Outlines.TryGetGlyph(glyphId, out var glyph)
                        ? (advance, glyph)
                        : (advance, null);
                });
                return;
            }

            // A composite font through a predefined CMap: each code's CID through the CMap, the CID's
            // character through the collection's Unicode map (the one the reader's text extraction
            // reads, CidCollectionText), and that character's outline in the host face the text is
            // drawn with — the face named, else one that covers it. A code's pen advance is the em
            // show gives it (/DW 1000). A code the maps or the face cannot carry keeps its metric box,
            // counted in the report; vertical text, whose pen runs down, keeps its boxes whole.
            if (i.CurrentFontCMap is { } cmap && i.CurrentType3Font == null
                && (PsCMapOps.Current(i) is { } defined ? defined.WMode == 0 : !cmap.EndsWith("-V", StringComparison.Ordinal))
                && PsCMapOps.Decode(i, cmap, PsCMapOps.Ordering(i, cmap), bytes) is { } codes)
            {
                var characters = new int[codes.Count];
                var points = new HashSet<int>();
                for (int k = 0; k < codes.Count; k++)
                {
                    characters[k] = OneCharacter(codes[k].Text);
                    if (characters[k] >= 0) points.Add(characters[k]);
                }
                Emit.PsStandardOutlines.Face cjkFace = points.Count > 0 ? Emit.PsStandardOutlines.ForComposite(baseFont, points) : null;
                AppendOutlinesOrBoxes(i, g.Path, fm, codes.Count, stroked, k =>
                    characters[k] >= 0 && cjkFace != null
                    && cjkFace.Program.TryGetGlyph(characters[k], out ushort glyphId)
                    && cjkFace.Outlines.TryGetGlyph(glyphId, out var glyph)
                        ? (1.0, glyph)
                        : (1.0, null));
                return;
            }

            var (x, y) = g.Path.CurrentPoint.Value;
            AppendMetricBox(g.Path, fm, x, y, MeasureAlong(i, baseFont, bytes));
            ReportMetricBox(bytes.Length, stroked);
        }

        /// <summary>The one Unicode scalar <paramref name="text"/> is, or -1 for none, several, or a lone surrogate.</summary>
        private static int OneCharacter(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            if (text.Length == 1) return char.IsSurrogate(text[0]) ? -1 : text[0];
            return text.Length == 2 && char.IsSurrogatePair(text[0], text[1]) ? char.ConvertToUtf32(text[0], text[1]) : -1;
        }

        /// <summary>
        /// The glyph of <paramref name="face"/> that <paramref name="code"/> draws in
        /// <paramref name="baseFont"/>: the character it denotes through the face's <c>cmap</c> — WinAnsi
        /// or the re-encoding's name for a Latin face (<see cref="PsFontMapper.ResolveChar"/>), and for
        /// Symbol and ZapfDingbats, which draw a code through their built-in encoding (§9.6.6.4), the
        /// character the glyph's name stands for (a re-encoding's name, else the built-in one), and
        /// failing that a symbol font's (3,0) <c>cmap</c> entry for the built-in code, at U+F000 + code.
        /// </summary>
        private static bool TryStandardGlyph(Emit.PsStandardOutlines.Face face, string baseFont, byte code,
                                             IReadOnlyDictionary<int, string> differences, out ushort glyphId)
        {
            if (!Core.Fonts.Standard14Metrics.HasBuiltInEncoding(baseFont))
                return face.Program.TryGetGlyph(PsFontMapper.ResolveChar(code, differences), out glyphId);

            if (differences != null && differences.TryGetValue(code, out string named))
            {
                glyphId = 0;
                return Core.Fonts.PdfGlyphNames.TryGetUnicode(named, out char renamed)
                       && face.Program.TryGetGlyph(renamed, out glyphId);
            }

            if (Core.Fonts.Standard14Metrics.TryGetBuiltInGlyph(baseFont, code, out string builtIn, out _)
                && Core.Fonts.PdfGlyphNames.TryGetUnicode(builtIn, out char character)
                && face.Program.TryGetGlyph(character, out glyphId))
                return true;
            return face.Program.TryGetGlyph(0xF000 + code, out glyphId);
        }

        /// <summary>A glyph space unit in the font matrix's thousandths (DefineFont took a program's own matrix into it).</summary>
        private const double GlyphSpace = 0.001;

        /// <summary>
        /// The text matrix of a glyph shown at the pen (<paramref name="x"/>, <paramref name="y"/>):
        /// the font matrix with the pen added to its translation. PLRM §5.3 maps a glyph's point through
        /// the font matrix, translation included, and then puts it at the current point, so a
        /// <c>makefont</c> with a translation moves the text by it — which writing the matrix's linear
        /// part with the pen alone dropped, at every site. Advances are differences and take no
        /// translation (<see cref="PsMatrix.ApplyDelta"/>).
        /// </summary>
        private static PsMatrix TextMatrixAt(in PsMatrix fm, double x, double y)
            => new PsMatrix(fm.A, fm.B, fm.C, fm.D, x + fm.Tx, y + fm.Ty);

        /// <summary>
        /// Each code's outline at the pen, or its metric box where <paramref name="glyphFor"/> has none
        /// (counted in the report), the pen advancing by the code's width through the font matrix and
        /// the current point left at the end, as <c>show</c> leaves it.
        /// </summary>
        private static void AppendOutlinesOrBoxes(PsInterpreter i, Graphics.PsPath path, in Graphics.PsMatrix fm,
            byte[] bytes, bool stroked, Func<byte, (double Advance, Core.Fonts.GlyphOutline Outline)> glyphFor)
            => AppendOutlinesOrBoxes(i, path, fm, bytes.Length, stroked, k => glyphFor(bytes[k]));

        /// <summary>
        /// The byte-per-code loop above over <paramref name="count"/> codes a composite font's CMap split the string into, the
        /// <paramref name="glyphFor"/> of each by its index.
        /// </summary>
        private static void AppendOutlinesOrBoxes(PsInterpreter i, Graphics.PsPath path, in Graphics.PsMatrix fm,
            int count, bool stroked, Func<int, (double Advance, Core.Fonts.GlyphOutline Outline)> glyphFor)
        {
            var (px, py) = path.CurrentPoint.Value;
            int boxed = 0;
            for (int k = 0; k < count; k++)
            {
                var (advance, outline) = glyphFor(k);
                if (outline != null)
                    AppendOutline(i, path, fm, outline, px, py);
                else
                {
                    AppendMetricBox(path, fm, px, py, advance);
                    boxed++;
                }
                var (ax, ay) = fm.ApplyDelta(advance, 0);
                px += ax;
                py += ay;
            }
            path.MoveTo(px, py);
            if (boxed > 0) ReportMetricBox(boxed, stroked);
        }

        /// <summary>
        /// <paramref name="glyph"/>'s outline at the pen (<paramref name="px"/>, <paramref name="py"/>),
        /// through the font matrix, charged by its segments.
        /// </summary>
        private static void AppendOutline(PsInterpreter i, Graphics.PsPath path, in Graphics.PsMatrix fm,
                                          Core.Fonts.GlyphOutline glyph, double px, double py)
        {
            i.Tick(1 + glyph.Segments.Count);
            foreach (var s in glyph.Segments)
            {
                var (x1, y1) = fm.Apply(s.X1 * GlyphSpace, s.Y1 * GlyphSpace);   // points, so the translation too
                var (x2, y2) = fm.Apply(s.X2 * GlyphSpace, s.Y2 * GlyphSpace);
                var (x3, y3) = fm.Apply(s.X3 * GlyphSpace, s.Y3 * GlyphSpace);
                switch (s.Kind)
                {
                    case Core.Fonts.GlyphOutlineSegmentKind.MoveTo: path.MoveTo(px + x3, py + y3); break;
                    case Core.Fonts.GlyphOutlineSegmentKind.LineTo: path.LineTo(px + x3, py + y3); break;
                    case Core.Fonts.GlyphOutlineSegmentKind.CurveTo:
                        path.CurveTo(px + x1, py + y1, px + x2, py + y2, px + x3, py + y3); break;
                    default: path.ClosePath(); break;
                }
            }
        }

        /// <summary>
        /// The metric box of <paramref name="em"/> ems of text at (<paramref name="x"/>, <paramref name="y"/>)
        /// — the em along the baseline, the engine's ascender and descender across it — laid through
        /// the font matrix, so a rotated or skewed font's box follows its letters. Close enough for a
        /// clip box, and the report makes clear it is one.
        /// </summary>
        private static void AppendMetricBox(Graphics.PsPath path, in Graphics.PsMatrix fm, double x, double y, double em)
        {
            var (ax, ay) = fm.Apply(0, Graphics.PsGraphicsEngine.TextDescender);
            var (bx, by) = fm.Apply(em, Graphics.PsGraphicsEngine.TextDescender);
            var (cx, cy) = fm.Apply(em, Graphics.PsGraphicsEngine.TextAscender);
            var (dx, dy) = fm.Apply(0, Graphics.PsGraphicsEngine.TextAscender);
            path.MoveTo(x + ax, y + ay);
            path.LineTo(x + bx, y + by);
            path.LineTo(x + cx, y + cy);
            path.LineTo(x + dx, y + dy);
            path.ClosePath();
        }

        /// <summary>
        /// Says <c>charpath</c> appended metric boxes. Once per conversion, like the unrepresentable-text
        /// warning: a per-call report is the flood ReportOversizedNumericToken's doc comment warns about —
        /// `{ (a) true charpath } loop` is bounded in work by the text charge and would still compose a
        /// message and fire OnEvent on every iteration.
        /// </summary>
        private static void ReportMetricBox(int count, bool stroked)
        {
            if (_charPathReported) return;
            _charPathReported = true;
            PostScriptDiagnostics.Report("PsTextOps.CharPath",
                $"'charpath' appended the metric box of {count} character(s) rather than glyph " +
                "outlines: no font program or face outline was had for them (a composite font, a Type 3 " +
                "glyph that paints no path, or a standard face the host has no outline for)" +
                (stroked ? " (the stroked form was requested)" : "") +
                ". Reported once per conversion.");
        }

        [System.ThreadStatic] private static bool _charPathReported;

        /// <summary>
        /// Charges one text operator for the work its operand actually costs.
        ///
        /// <para>Every one of <c>show</c>, <c>stringwidth</c> and <c>charpath</c> does work
        /// proportional to the string's length — a <see cref="PsString.ToArray"/> copy plus one
        /// metric lookup per byte — so each is charged by its length, never one tick like
        /// <c>pop</c>, as <c>PositionedShow</c> and <c>PsArrayStringOps.Search</c> charge their
        /// counts.</para>
        ///
        /// <para>The copy is charged to the allocation budget as well as the operation budget,
        /// because that is what this interpreter does with every other transient composite it builds
        /// (<c>PsInterpreter</c>'s own <c>AsLatin1</c> charge, <c>gsave</c>'s state clone): the
        /// budget measures traffic, not residency. 64 MiB of shown text in one conversion is far
        /// past any real document.</para>
        /// </summary>
        private static void ChargeTextWork(PsInterpreter i, string op, PsString text)
        {
            if (text == null || text.Length == 0) return;
            i.Tick(text.Length);
            i.ChargeAllocation(text.Length, op);
        }

        // ------------------------------------------------------------------
        //  Font-dictionary helpers
        // ------------------------------------------------------------------

        private static PsMatrix FontMatrix(PsDict font)
            => font.TryGetName(KeyMatrix, out var m) && m.Type == PsType.Array
                ? PsMatrix.FromOperands(m.AsArray(), "font")
                : PsMatrix.Identity;

        private static PsDict WithMatrix(PsInterpreter i, PsDict font, in PsMatrix m)
        {
            // Charged for what is actually copied. A flat 2-entry charge meant `scalefont` on a
            // 50 000-entry dictionary cost 96 bytes of a 64 MiB budget while copying every entry,
            // so `{ D 1 scalefont pop } loop` ran for tens of minutes with terabytes of GC traffic
            // and BudgetExhausted never set. This matches PsArrayStringOps.CopyComposite.
            i.Tick(font.Count);
            i.ChargeAllocation((long)(font.Count + 1) * PsAllocationBudget.BytesPerDictEntry,
                               "scalefont");

            var copy = new PsDict(font.Count + 1);
            foreach (var kv in font.Entries) copy.Put(kv.Key, kv.Value);
            copy.PutName(KeyMatrix, MatrixObject(i, m));
            return copy;
        }

        private static PsObject MatrixObject(PsInterpreter i, in PsMatrix m)
        {
            i.ChargeAllocation(6 * PsAllocationBudget.BytesPerArrayElement, "font matrix");
            var items = new[]
            {
                PsObject.Real(m.A), PsObject.Real(m.B), PsObject.Real(m.C),
                PsObject.Real(m.D), PsObject.Real(m.Tx), PsObject.Real(m.Ty)
            };
            return PsObject.Arr(new PsArray(items));
        }

    }
}
