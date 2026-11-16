// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>The font, size and fill colour a field's <c>/DA</c> string asks for.</summary>
    internal sealed class DefaultAppearance
    {
        /// <summary>Resource name from the <c>Tf</c> operator (no leading slash), e.g. <c>Helv</c>.</summary>
        internal string FontResourceName;
        /// <summary>Size from the <c>Tf</c> operator; <c>0</c> is the spec's "auto-size" value.</summary>
        internal float FontSize;
        /// <summary>The colour operator verbatim, e.g. <c>0 g</c> or <c>0 0 0.6 rg</c>.</summary>
        internal string ColorOperators = "0 g";
    }

    /// <summary>How a field's value is laid out inside its widget.</summary>
    internal sealed class FieldLayout
    {
        /// <summary>Multi-line text field (<c>/Ff</c> bit 13): wraps, and breaks on newlines.</summary>
        internal bool Multiline;
        /// <summary>Comb field (<c>/Ff</c> bit 25): one character per equally spaced cell.</summary>
        internal bool Comb;
        /// <summary><c>/MaxLen</c> — the number of comb cells. Comb is inert without it.</summary>
        internal int MaxLength;
        /// <summary><c>/Q</c> quadding.</summary>
        internal int Quadding;

        /// <summary>
        /// The layout a field's flags ask for — the one reading of §12.7.4.3, for the filler and
        /// the flattener alike.
        /// </summary>
        /// <param name="isTextField">Whether the field is a text field (<c>/FT /Tx</c>).</param>
        /// <param name="flags">The field's resolved <c>/Ff</c>.</param>
        /// <param name="maxLength">The field's resolved <c>/MaxLen</c>, or 0.</param>
        /// <param name="quadding">The field's resolved <c>/Q</c>.</param>
        /// <remarks>
        /// <b>Multiline and comb are text-field flags</b> (Table 226): bits 13 and 25 mean nothing on
        /// a choice field. Comb also needs a single line, and is ignored on a password or
        /// file-select field. This was written out twice, beside a comment in the flattener
        /// reading "same clause as the filler's" — and the flattener's copy had dropped the
        /// text-field test, so a choice widget whose <c>/Ff</c> happened to carry those bits was
        /// regenerated as multiline or comb when flattened and as a single line when filled.
        /// </remarks>
        internal static FieldLayout For(bool isTextField, int flags, int maxLength, int quadding)
        {
            bool multiline = isTextField && PdfFieldFlags.Has(flags, PdfFieldFlags.Multiline);
            return new FieldLayout
            {
                Multiline = multiline,
                Comb = isTextField
                       && PdfFieldFlags.Has(flags, PdfFieldFlags.Comb)
                       && !multiline
                       && !PdfFieldFlags.Has(flags, PdfFieldFlags.Password)
                       && !PdfFieldFlags.Has(flags, PdfFieldFlags.FileSelect),
                MaxLength = maxLength,
                Quadding = quadding,
            };
        }
    }

    /// <summary>
    /// One way of turning text into show-text operators: how wide a string is, and what to emit for
    /// it. The two encoding strategies (single-byte font, embedded composite font) differ only in
    /// these two functions, which is what lets one layout implementation serve both — including
    /// wrapping and comb layout, where arbitrary substrings have to be measured and emitted.
    /// </summary>
    internal sealed class TextStrategy
    {
        /// <summary>Width of a string in em units; multiply by the font size for points.</summary>
        internal Func<string, double> Measure;
        /// <summary>
        /// Whether <see cref="Measure"/> of two strings joined is the sum of theirs — a simple font's
        /// widths add; a shaped run's may not, its kerning and joins spanning the seam.
        /// </summary>
        internal bool Additive;
        /// <summary>
        /// The show-text operators for a string at a font size, e.g. <c>(abc) Tj</c> or
        /// <c>&lt;00A3&gt; Tj</c> — or, for a composed run whose marks the font attaches, the
        /// <c>TJ</c>/<c>Ts</c> that place them (<see cref="GlyphRunShowText"/>), whose rise is in
        /// points and so needs the size.
        /// </summary>
        internal Func<string, double, string> ShowText;
        /// <summary>Resource name the appearance's <c>Tf</c> refers to.</summary>
        internal string ResourceName;
    }

    /// <summary>
    /// Builds a variable-text <c>/AP</c> appearance stream for a filled field (ISO 32000-1 §12.7.3.3).
    ///
    /// <para>Two encoding strategies, chosen by what the text needs:</para>
    /// <list type="number">
    ///   <item><b>Simple font</b> — every character is representable in a single-byte encoding, so
    ///   the text is emitted as a literal string against either the font the field's <c>/DA</c>
    ///   names in the AcroForm <c>/DR</c> resources, or a synthesised Standard-14 Type 1.</item>
    ///   <item><b>Embedded composite font</b> — anything outside that range (Hebrew, Arabic, CJK,
    ///   the euro sign) needs real glyphs, so a font program supplied by the caller is embedded as
    ///   a Type0 / Identity-H font via <see cref="Type0FontBuilder"/> and the text is emitted as
    ///   hex glyph ids in <b>visual</b> order (<see cref="UnicodeBidi"/>).</item>
    /// </list>
    ///
    /// <para><b>Shaping.</b> The composite path composes through <see cref="TextRunComposer"/>, which
    /// shapes (Arabic joining, through <c>OpenTypeShaper</c>) and then reorders, so Hebrew and Arabic
    /// are both drawn joined and in visual order — see <c>ArabicValue_IsShapedIntoJoinedForms</c>.
    /// This paragraph once said Arabic joining was not applied, which stopped being true when the
    /// pipeline moved into Core. Combining marks are placed by the font's own <c>GPOS</c> mark
    /// attachment, which the composed run carries and <see cref="GlyphRunShowText"/> writes.</para>
    /// </summary>
    internal static class FormAppearance
    {
        /// <summary>Padding between the widget border and the text, in points.</summary>
        private const double Margin = 2.0;

        /// <summary>Line advance as a multiple of the font size, for multiline fields.</summary>
        private const double LineHeightFactor = 1.15;

        /// <summary>Fraction of the widget height used when auto-sizing a single-line field.</summary>
        private const double AutoSizeHeightFraction = 0.6;

        /// <summary>Smallest auto-size the fitting pass will shrink to.</summary>
        private const float MinAutoFontSize = 4f;

        /// <summary>Largest auto-size for a single-line field before the height rule applies.</summary>
        private const float DefaultAutoFontSize = 8f;

        /// <summary>
        /// Step by which an auto-sized multiline block shrinks while searching for a size whose
        /// wrapped result fits the box. Wrapping changes the line count, which changes the required
        /// height, so the fit cannot be solved in closed form — it is searched.
        /// </summary>
        private const float AutoSizeShrinkStep = 0.5f;

        /// <summary>
        /// Bound on that search. From the height-derived starting size down to
        /// <see cref="MinAutoFontSize"/> in <see cref="AutoSizeShrinkStep"/> steps this is never
        /// reached for a real widget; it exists so a pathological box cannot spin.
        /// </summary>
        private const int MaxAutoSizeAttempts = 200;

        // ----------------------------------------------------------------
        //  /DA parsing
        // ----------------------------------------------------------------

        /// <summary>
        /// Parses a default-appearance string. Only <c>Tf</c> and the device colour operators
        /// (<c>g</c> / <c>rg</c> / <c>k</c>) are recognised — everything else a <c>/DA</c> may
        /// legally contain is graphics state that a fresh appearance stream re-establishes anyway.
        /// Returns <c>null</c> when the string names no font.
        /// </summary>
        /// <remarks>
        /// The reading is <see cref="DefaultAppearanceString"/>, shared with the
        /// renderers' fallback and the annotation appearance builder. This copy pasted the colour
        /// operands into the appearance stream as it found them. They are numbers now, written by
        /// the one PDF formatter, and a colour with a component that is not a number is ignored.
        /// </remarks>
        internal static DefaultAppearance ParseDefaultAppearance(string da)
        {
            var parsed = DefaultAppearanceString.Parse(da);
            if (parsed.FontResourceName == null) return null;

            return new DefaultAppearance
            {
                FontResourceName = parsed.FontResourceName,
                FontSize = parsed.FontSize ?? 0f,
                ColorOperators = parsed.FillOperator() ?? "0 g",
            };
        }

        // ----------------------------------------------------------------
        //  Appearance construction
        // ----------------------------------------------------------------

        /// <summary>
        /// Builds and registers the <c>/AP /N</c> stream for a filled text or choice widget.
        /// Returns the numbered stream (already added to <paramref name="objects"/>), or
        /// <c>null</c> with <paramref name="error"/> set when no appearance could be drawn —
        /// the caller then leaves <c>/NeedAppearances</c> true rather than writing something wrong.
        /// </summary>
        /// <param name="text">The value to draw.</param>
        /// <param name="width">The widget rectangle's width.</param>
        /// <param name="height">The widget rectangle's height.</param>
        /// <param name="rotation">
        /// The widget's <c>/MK /R</c> (Table 192), from <see cref="WidgetRotation.Read"/>:
        /// the value is laid out in the rectangle turned back upright — its width and height swapped
        /// for 90 and 270 — and the form's <c>/Matrix</c> turns that box onto the rectangle, which
        /// is where §12.5.5's fit expects the turn. The renderers apply no turn of their own.
        /// </param>
        /// <param name="da">The field's default appearance.</param>
        /// <param name="layout">Multiline, comb, quadding and the rest of the field's layout.</param>
        /// <param name="doc">The document, for resolving font resources.</param>
        /// <param name="acroFormDict">The form dictionary, for its default resources.</param>
        /// <param name="options">Fill options.</param>
        /// <param name="nextObjNum">Next free object number; advanced for every object added.</param>
        /// <param name="objects">Receives the appearance and any font objects it needs.</param>
        /// <param name="error">Why nothing was drawn, when the result is null.</param>
        /// <param name="warning">A substitution or truncation the caller should report.</param>
        internal static PdfStream Build(
            string text,
            double width,
            double height,
            int rotation,
            DefaultAppearance da,
            FieldLayout layout,
            PdfDocument doc,
            PdfDictionary acroFormDict,
            FormFillOptions options,
            ref int nextObjNum,
            List<IndirectObject> objects,
            out string error,
            out string warning)
        {
            error = null;
            warning = null;
            text ??= string.Empty;
            layout ??= new FieldLayout();

            if (width <= 0 || height <= 0)
            {
                error = "the widget rectangle is empty, so there is nowhere to draw the value";
                return null;
            }

            // A turned widget is laid out upright in a box of the rectangle's swapped dimensions;
            // the /Matrix below turns it back (Table 192 /MK /R, §12.5.5).
            var (boxWidth, boxHeight) = WidgetRotation.LayoutBox(rotation, width, height);

            var resourceFont = new PdfDictionary();
            string content;

            int droppedLines;
            // The simple path carries text the WinAnsi font this writer makes can hold, and text
            // the form's own /DR font encodes through its /Encoding (§9.6.6) — Acrobat's /Helv puts
            // the euro at 160, outside Latin-1's printable range.
            Dictionary<char, int> drCodes = DefaultResourceFontCodes(da, doc, acroFormDict, options, out PdfObject drFontRef, out PdfDictionary drFontDict);
            bool drFontEncodes = drCodes != null && Encodes(drCodes, text);
            if (IsSingleByteEncodable(text) || drFontEncodes)
            {
                TextStrategy strategy = BuildSimpleStrategy(
                    text, da, doc, options, ref nextObjNum, objects, resourceFont,
                    drCodes == null || drFontEncodes ? drFontRef : null, drFontDict, drFontEncodes ? drCodes : null);
                content = Compose(text, boxWidth, boxHeight, da, layout, strategy, options, out droppedLines);
            }
            else
            {
                CompositeStrategy composite = BuildCompositeStrategy(text, da, options, out error);
                if (composite == null) return null;

                content = Compose(text, boxWidth, boxHeight, da, layout, composite.Strategy, options,
                                  out droppedLines);

                // Reported even though the appearance IS drawn, and reported here rather than as an
                // error, for the same reason the truncation warning below is: the drawing is
                // correct, but it is not the font the caller asked for.
                warning = composite.SubstitutionWarning;

                // NO INPUT REACHES THIS, and it is kept deliberately. The only program that gets
                // here is one SystemFontResolver accepted as covering
                // TextRunComposer.CodePointsToCover(text) — the very set ComposeRun then looks up —
                // so a missing glyph would mean the resolver's coverage test and the composer's
                // lookup disagreed. It WAS reachable until font resolution asked that question:
                // resolved by the source text's code points, a face covering the Arabic letters and
                // not their joined forms was chosen here and refused one line later. A mutation
                // sweep reports this, ComposeRun's Refuse policy and MissingCodepoint's first-wins
                // as three survivors; that is the expected result, recorded so the next sweep does
                // not re-derive it.
                if (composite.MissingCodepoint.HasValue)
                {
                    error = string.Format(CultureInfo.InvariantCulture,
                        "{0} has no glyph for U+{1:X4}, so no appearance was drawn",
                        composite.Source, composite.MissingCodepoint.Value);
                    return null;
                }

                // Built after composing: only the glyphs actually drawn go into /W and /ToUnicode.
                var (type0ObjNum, fontObjects) = Type0FontBuilder.BuildObjects(
                    ref nextObjNum, composite.Program, composite.GidToUnicode, composite.GidToText);
                foreach (var (objNum, obj) in fontObjects)
                    objects.Add(new IndirectObject(objNum, 0, obj));
                resourceFont.Set(new PdfName(composite.Strategy.ResourceName), new PdfReference(type0ObjNum, 0));
            }

            if (droppedLines > 0)
            {
                // Not an error: the appearance IS produced and everything that fits is drawn. But
                // the value is longer than the widget can show even at the minimum font size, so
                // the caller has to be told rather than shipping a visibly complete document with
                // a truncated value in it.
                string truncated = string.Format(CultureInfo.InvariantCulture,
                    "the value does not fit the widget at the minimum font size ({0}pt); " +
                    "{1} line{2} at the end {3} not drawn",
                    Fmt(MinAutoFontSize), droppedLines,
                    droppedLines == 1 ? string.Empty : "s",
                    droppedLines == 1 ? "was" : "were");

                // Appended, not assigned, so a value that both overflowed the widget AND was drawn in
                // a substituted font reports both.
                warning = warning == null ? truncated : warning + "; " + truncated;
            }

            var resources = new PdfDictionary();
            resources.Set(PdfName.Font, resourceFont);

            byte[] contentBytes = Core.Text.Latin1Text.Encode(content);

            var apStream = new PdfStream();
            apStream.Set(PdfName.Type, PdfName.XObject);
            apStream.Set(PdfName.Subtype, PdfName.Form);
            apStream.Set(PdfName.FormType, new PdfNumber(1));

            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(boxWidth));
            bbox.Add(new PdfNumber(boxHeight));
            apStream.Set(PdfName.BBox, bbox);
            if (WidgetRotation.Matrix(rotation, width, height) is PdfArray matrix)
                apStream.Set(PdfName.Matrix, matrix);
            apStream.Set(PdfName.Resources, resources);
            apStream.RawData = contentBytes;
            apStream.Set(PdfName.Length, new PdfNumber(contentBytes.Length));

            int apObjNum = nextObjNum++;
            apStream.ObjectNumber = apObjNum;
            apStream.GenerationNumber = 0;
            objects.Add(new IndirectObject(apObjNum, 0, apStream));

            return apStream;
        }

        // ----------------------------------------------------------------
        //  Strategy 1 — single-byte encodable text
        // ----------------------------------------------------------------

        /// <summary>
        /// The font the field's <c>/DA</c> names in the AcroForm's <c>/DR</c> resources, when the
        /// options honour it, and the code each character takes under its encoding (<see cref="CodesOf"/>);
        /// null codes for no such font, or one that cannot be read as a simple font.
        /// </summary>
        private static Dictionary<char, int> DefaultResourceFontCodes(DefaultAppearance da, PdfDocument doc,
            PdfDictionary acroFormDict, FormFillOptions options, out PdfObject drFontRef, out PdfDictionary drFontDict)
        {
            drFontRef = null;
            drFontDict = null;
            if (options.HonorDefaultAppearance && da?.FontResourceName != null)
                drFontRef = ResolveDefaultResourceFont(doc, acroFormDict, da.FontResourceName, out drFontDict);
            return drFontRef != null ? CodesOf(drFontDict, doc) : null;
        }

        /// <summary>
        /// The simple-font strategy: the text drawn with <paramref name="drFontRef"/>, the <c>/DR</c>
        /// font the field's <c>/DA</c> names — the document's own font, so the filled value matches
        /// the rest of the form — as the codes <paramref name="drCodes"/> gives each character
        /// under its encoding, measured by <paramref name="drFontDict"/>; with that font as Latin-1
        /// bytes when the codes are null (a font this writer could not read as a simple font); or,
        /// with no <c>/DR</c> font, with the WinAnsi standard font this writer makes.
        /// </summary>
        private static TextStrategy BuildSimpleStrategy(
            string text, DefaultAppearance da, PdfDocument doc, FormFillOptions options,
            ref int nextObjNum, List<IndirectObject> objects, PdfDictionary resourceFont,
            PdfObject drFontRef, PdfDictionary drFontDict, Dictionary<char, int> drCodes)
        {
            // The /DR font draws a byte through its own /Encoding (§9.6.6), so the value is written
            // as that encoding's codes and measured by them — never as Latin-1 bytes, which Acrobat's
            // /Helv with its /Differences [160 /Euro] draws as € for a no-break space. A character
            // the encoding has no code for sends the value to the WinAnsi font this writer makes.
            string baseFont = options.FontName ?? StandardFonts.Helvetica;
            string resourceName;

            if (drFontRef != null)
            {
                resourceName = da.FontResourceName;
                resourceFont.Set(new PdfName(resourceName), drFontRef);
                string drBase = (doc.ResolveObject(drFontDict?[PdfName.BaseFont]) as PdfName)?.Value;
                if (drBase != null) baseFont = drBase;
                if (drCodes != null)
                {
                    return new TextStrategy
                    {
                        ResourceName = resourceName,
                        Measure = s => MeasureSimple(s, drFontDict, baseFont, doc, drCodes),
                        Additive = true,
                        ShowText = (s, _) => "(" + PdfFormFiller.EscapePdfString(Recoded(s, drCodes)) + ") Tj",
                    };
                }
            }
            else
            {
                resourceName = "F1";
                int fontObjNum = nextObjNum++;
                var fontDict = new PdfDictionary();
                fontDict.Set(PdfName.Type, PdfName.Font);
                fontDict.Set(PdfName.Subtype, PdfName.Type1);
                fontDict.Set(PdfName.BaseFont, new PdfName(baseFont));
                fontDict.Set(PdfName.Encoding, PdfName.WinAnsiEncoding);
                fontDict.ObjectNumber = fontObjNum;
                fontDict.GenerationNumber = 0;
                objects.Add(new IndirectObject(fontObjNum, 0, fontDict));
                resourceFont.Set(new PdfName(resourceName), new PdfReference(fontObjNum, 0));
            }

            // Measured by the font that draws: the /DR font's /Widths, or the standard face's metrics.
            PdfDictionary measuredFont = drFontRef != null ? drFontDict : null;
            string measuredBase = baseFont;

            return new TextStrategy
            {
                ResourceName = resourceName,
                Measure = s => MeasureSimple(s, measuredFont, measuredBase, doc),
                Additive = true,
                ShowText = (s, _) => "(" + PdfFormFiller.EscapePdfString(s) + ") Tj",
            };
        }

        /// <summary>
        /// The code each character takes under a simple <c>/DR</c> font's own encoding — its
        /// <c>/Differences</c>, then its base encoding (§9.6.6) — the first code for a character
        /// named twice; null when the font cannot be read as a simple font, which keeps the writer's
        /// Latin-1 pass-through for it.
        /// </summary>
        private static Dictionary<char, int> CodesOf(PdfDictionary fontDict, PdfDocument doc)
        {
            if (fontDict == null || doc == null) return null;
            PdfSimpleFont font;
            try { font = PdfFont.Create(fontDict, doc) as PdfSimpleFont; }
            catch (Exception) { return null; }
            if (font == null) return null;
            var codes = new Dictionary<char, int>();
            for (int code = 0; code < 256; code++)
                if (font.TryGetEncodedCharacter(code, out char c) && !codes.ContainsKey(c)) codes[c] = code;
            return codes;
        }

        /// <summary>Whether every character of <paramref name="text"/> but a line break has a code.</summary>
        private static bool Encodes(Dictionary<char, int> codes, string text)
        {
            foreach (char c in text)
                if (c != '\r' && c != '\n' && !codes.ContainsKey(c)) return false;
            return true;
        }

        /// <summary>
        /// The string's codes under the encoding, each as the character of its byte — the literal
        /// string the appearance writes as Latin-1 then holds the codes; a character with no code
        /// (a line break) is left out.
        /// </summary>
        private static string Recoded(string text, Dictionary<char, int> codes)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (codes.TryGetValue(c, out int code)) sb.Append((char)code);
            return sb.ToString();
        }

        /// <summary>
        /// Looks up <c>/DR /Font /&lt;name&gt;</c> on the AcroForm. A composite (Type0) resource is
        /// rejected: its codes are not the single bytes this path emits, so reusing it would draw
        /// the wrong glyphs.
        /// </summary>
        private static PdfObject ResolveDefaultResourceFont(
            PdfDocument doc, PdfDictionary acroFormDict, string resourceName, out PdfDictionary fontDict)
        {
            fontDict = null;
            if (acroFormDict == null) return null;

            var dr = doc.ResolveObject(acroFormDict[PdfName.DR]) as PdfDictionary;
            var fonts = doc.ResolveObject(dr?[PdfName.Font]) as PdfDictionary;
            var entry = fonts?[new PdfName(resourceName)];
            if (entry == null) return null;

            fontDict = doc.ResolveObject(entry) as PdfDictionary;
            if (fontDict == null) return null;
            // Resolved, and this one is a GUARD rather than a lookup: the method refuses a Type0
            // font outright because everything downstream measures single bytes. Read raw, a legal
            // /Subtype 12 0 R came back null, null is not /Type0, and the refusal never fired — so
            // a composite font was measured as a simple one and every advance was wrong.
            if ((doc.ResolveObject(fontDict[PdfName.Subtype]) as PdfName) == PdfName.Type0) { fontDict = null; return null; }

            // Keep the indirect reference when there is one; the appearance must not copy the
            // font dictionary, or the file grows one duplicate per filled field.
            return entry is PdfReference ? entry : fontDict;
        }

        // ----------------------------------------------------------------
        //  Strategy 2 — text needing an embedded composite font
        // ----------------------------------------------------------------

        /// <summary>State the composite path accumulates while the content is composed.</summary>
        private sealed class CompositeStrategy
        {
            internal TextStrategy Strategy;
            internal TrueTypeFontProgram Program;
            internal Dictionary<ushort, int> GidToUnicode;
            /// <summary>A ligature glyph's typed letters, for <c>/ToUnicode</c> (<see cref="GlyphMapping.Text"/>).</summary>
            internal Dictionary<ushort, string> GidToText = new Dictionary<ushort, string>();
            internal string Source;
            /// <summary>First code point with no glyph, if any — the appearance is then refused.</summary>
            internal int? MissingCodepoint;
            /// <summary>
            /// Set when <see cref="FormFillOptions.FontProvider"/> was consulted and a font from
            /// <b>this machine</b> was embedded instead of the one it offered — see
            /// <see cref="BuildCompositeStrategy"/> for why an unreported fall-through there is a
            /// licensing event rather than a detail.
            /// </summary>
            internal string SubstitutionWarning;
            /// <summary>Paragraph direction, from <see cref="FormFillOptions.BaseDirection"/>.</summary>
            internal BaseDirection BaseDirection;
        }

        private static CompositeStrategy BuildCompositeStrategy(
            string text, DefaultAppearance da, FormFillOptions options, out string error)
        {
            error = null;

            string hint = da?.FontResourceName ?? options.FontName ?? StandardFonts.Helvetica;

            // The caller's provider is WRAPPED rather than handed straight to the resolver, so this
            // path can say which font was actually embedded and why the supplied one was not.
            //
            // `SystemFontResolver` coverage-tests a provider's font like any other candidate and,
            // when it throws or covers only part of the value, falls through to a host font. That
            // is what `AllowSystemFontSubstitution` is for and it stays — but it was SILENT, and a
            // silent fall-through here is a licensing event: a caller supplies a font program
            // precisely because they hold a licence for it, and shipping a different one under the
            // same call is the one outcome they cannot check for. The resolver cannot report it
            // either, because `FontResolution` says only that the winner came `FromSystem`, never
            // what the provider did to lose.
            //
            // Three outcomes, three sentences, because a provider that THREW is the caller's bug
            // and a provider that returned null is the caller's decision. Collapsing them into
            // "the provider did not supply a font" would name a remedy that fits only one of them.
            byte[] providerBytes = null;
            string providerThrew = null;
            bool providerConsulted = false;
            Func<string, byte[]> provider = options.FontProvider == null ? null : name =>
            {
                providerConsulted = true;
                try
                {
                    return providerBytes = options.FontProvider(name);
                }
                catch (Exception ex)
                {
                    // Re-thrown, not swallowed: the resolver's own catch decides whether to carry
                    // on to the fallbacks, and two layers deciding that would be two rules.
                    providerThrew = ex.GetType().Name + ": " + ex.Message;
                    throw;
                }
            };

            // One resolver, in Core, including its `OS/2` `fsType` gate, so
            // `AllowSystemFontSubstitution` never embeds a host font whose own licence bits forbid
            // embedding.
            var resolution = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = hint,
                    // The code points the DRAWING needs, which for Arabic are the shaped forms —
                    // see TextRunComposer.CodePointsToCover. Asked about the base letters, this
                    // accepted a font that covers them and then refused the run it could not draw.
                    RequiredCodePoints = TextRunComposer.CodePointsToCover(text),
                    AllowSystemFontSubstitution = options.AllowSystemFontSubstitution,
                    AllowRestrictedSystemFonts = options.AllowRestrictedSystemFonts,
                },
                provider);

            if (!resolution.Success)
            {
                // Appended on BOTH branches — see the note in SignatureAppearanceGenerator. The
                // resolver's reason carries the offending code point, which is the actionable half
                // whenever the caller supplied a font that was one glyph short.
                error = (options.AllowSystemFontSubstitution
                    ? "no font on this machine covers the value's characters, so no appearance was drawn " +
                      "(the value itself was written; set FormFillOptions.FontProvider to supply one)"
                    : "the value needs characters outside the field font's single-byte encoding and no " +
                      "usable font program was available, so no appearance was drawn (set " +
                      "FormFillOptions.FontProvider, or AllowSystemFontSubstitution to use an installed " +
                      "font)") + ": " + resolution.FailureReason;
                return null;
            }

            TrueTypeFontProgram program = resolution.Program;
            string source = resolution.SourcePath ?? "the supplied font program";

            string substitution = null;
            if (providerConsulted && resolution.FromSystem)
            {
                substitution = string.Format(CultureInfo.InvariantCulture,
                    "FormFillOptions.FontProvider {0}, so the appearance embeds {1} — a font found on "
                    + "this machine, not the one supplied",
                    providerThrew != null
                        ? "threw " + providerThrew
                        : providerBytes == null || providerBytes.Length == 0
                            ? "returned no font for '" + hint + "'"
                            : "returned a font that cannot draw every character of the value",
                    source);
            }

            var composite = new CompositeStrategy
            {
                Program = program,
                GidToUnicode = new Dictionary<ushort, int>(),
                Source = source,
                BaseDirection = options.BaseDirection,
                SubstitutionWarning = substitution,
            };

            composite.Strategy = new TextStrategy
            {
                ResourceName = "F1",
                Measure = s => MeasureComposite(s, composite),
                ShowText = (s, size) => GlyphRunShowText.Operators(ComposeRun(s, composite), size),
            };

            return composite;
        }

        /// <summary>
        /// Composes a string into glyphs in <b>visual</b> order, and folds the run's `/ToUnicode`
        /// pairs and first missing code point back into <paramref name="composite"/>.
        ///
        /// <para>The shape-then-reorder pipeline lives in <see cref="TextRunComposer"/>, which is reachable from Core so the other producers in
        /// this tree — and the templating plugin, which cannot see Base internals — share one
        /// implementation instead of four. Reordering does not change the total advance, so
        /// measuring and showing can still share one composition.</para>
        /// </summary>
        private static ShapedGlyphRun ComposeRun(string s, CompositeStrategy composite)
        {
            // Refuse, not SubstituteSpace: this path already reported an uncovered code point
            // through MissingCodepoint and drew nothing for it, and FormFillResult carries that up
            // to the caller as a field whose value was written but not drawn.
            var run = TextRunComposer.Compose(s, composite.Program, new TextRunOptions
            {
                BaseDirection = composite.BaseDirection,
                MissingGlyphPolicy = MissingGlyphPolicy.Refuse,
            });

            run.RecordUsedGlyphs(composite.GidToUnicode, composite.GidToText);

            if (run.MissingCodePoint.HasValue)
                composite.MissingCodepoint ??= run.MissingCodePoint.Value;

            return run;
        }

        private static double MeasureComposite(string s, CompositeStrategy composite)
            => ComposeRun(s, composite).AdvanceGlyphSpaceUnits / PdfUnits.GlyphSpaceUnitsPerEm;

        // ----------------------------------------------------------------
        //  Layout
        // ----------------------------------------------------------------

        private static string Compose(
            string text, double width, double height,
            DefaultAppearance da, FieldLayout layout, TextStrategy strategy, FormFillOptions options,
            out int droppedLines)
        {
            // MaxLength > 0 is the cell count this divides by. Multiline is not re-tested here:
            // FieldLayout.For already clears Comb for a multiline field, and a second copy of that
            // rule could only ever agree with it or be the defect.
            bool comb = layout.Comb && layout.MaxLength > 0;

            float size = ResolveSize(text, width, height, da, layout, strategy, comb, options,
                                     out List<string> lines);

            var sb = new StringBuilder();
            sb.Append("/Tx BMC\n");
            sb.Append("q\n");
            sb.Append(Inv("0 0 {0} {1} re W n\n", Fmt(width), Fmt(height)));
            sb.Append("BT\n");
            sb.Append(da?.ColorOperators ?? "0 g").Append('\n');
            // The name may be the file's own, from its /DA: as written it could close the operand and
            // add operators to the appearance this writes.
            sb.Append(Inv("/{0} {1} Tf\n", Core.Objects.PdfNameEscape.Escape(strategy.ResourceName), Fmt(size)));

            droppedLines = 0;
            if (comb)
                AppendCombCells(sb, text, width, height, size, layout, strategy);
            else
                droppedLines = AppendLines(sb, lines, width, height, size, layout, strategy);

            sb.Append("ET\n");
            sb.Append("Q\n");
            sb.Append("EMC\n");
            return sb.ToString();
        }

        /// <summary>
        /// Ordinary layout: one <c>Td</c> + show-text per line, positioned by
        /// <see cref="FieldLayout.Quadding"/>.
        /// </summary>
        /// <returns>
        /// How many lines fell outside the clip and were <b>not drawn</b>.
        ///
        /// <para>Auto-sizing shrinks a multiline block until it fits, but it stops at
        /// <see cref="MinAutoFontSize"/>; a value long enough to overflow the box even at 4 pt still
        /// exists, and the lines past the bottom edge simply are not emitted. A bare <c>break</c>
        /// reporting nothing would let a field lose the tail of its value while the fill returned
        /// success — silent data loss on exactly the input most likely to hit it. The count travels out to <c>FormFillResult.Warnings</c>.</para>
        ///
        /// <para>Note the sibling case was already guarded: <see cref="WrapParagraph"/> breaks a
        /// too-wide <i>word</i> by character rather than letting it overflow, under a comment saying
        /// that would "silently hide the rest of the value". This is that same rule applied to the
        /// vertical axis, which it had never been.</para>
        /// </returns>
        private static int AppendLines(
            StringBuilder sb, IReadOnlyList<string> lines, double width, double height,
            float size, FieldLayout layout, TextStrategy strategy)
        {
            double lineHeight = size * LineHeightFactor;
            double firstBaseline = layout.Multiline
                ? height - Margin - size
                : (height - size) / 2.0 + 1;

            double previousX = 0, previousY = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                double y = firstBaseline - i * lineHeight;
                if (y < -size) return lines.Count - i;   // the rest of the block is outside the clip

                double x = HorizontalOffset(strategy.Measure(lines[i]) * size, width, layout.Quadding);
                sb.Append(Inv("{0} {1} Td\n", Fmt(x - previousX), Fmt(y - previousY)));
                previousX = x;
                previousY = y;
                sb.Append(strategy.ShowText(lines[i], size)).Append('\n');
            }
            return 0;
        }

        /// <summary>
        /// Comb layout (§12.7.4.3, <c>/Ff</c> bit 25): the box is divided into <c>/MaxLen</c> equal
        /// cells and one character is centred in each. Emitted as a separate positioned run per
        /// character, because that is exactly what a comb is — the glyphs do not advance naturally.
        ///
        /// <para>A value longer than <c>/MaxLen</c> is drawn up to the cell count; the remainder has
        /// nowhere to go, and the filler already warns about the over-long value separately.</para>
        /// </summary>
        private static void AppendCombCells(
            StringBuilder sb, string text, double width, double height,
            float size, FieldLayout layout, TextStrategy strategy)
        {
            double cellWidth = width / layout.MaxLength;
            double baseline = (height - size) / 2.0 + 1;

            // Quadding shifts which cells are used when the value is shorter than the comb.
            int cells = layout.MaxLength;
            // Capped at the cell count, which is what makes the offsets below non-negative: a
            // `if (firstCell < 0) firstCell = 0;` stood here and could not fire, because
            // glyphs.Count never exceeds cells. Deleted rather than kept as a guard against a state
            // its own argument rules out.
            var glyphs = SplitTextElements(text, cells);
            int firstCell = layout.Quadding switch
            {
                PdfQuadding.Centred => (cells - glyphs.Count) / 2,
                PdfQuadding.Right => cells - glyphs.Count,
                _ => 0,
            };

            double previousX = 0, previousY = 0;
            for (int i = 0; i < glyphs.Count; i++)
            {
                string glyph = glyphs[i];
                double cellCentre = (firstCell + i + 0.5) * cellWidth;
                double x = cellCentre - strategy.Measure(glyph) * size / 2.0;

                sb.Append(Inv("{0} {1} Td\n", Fmt(x - previousX), Fmt(baseline - previousY)));
                previousX = x;
                previousY = baseline;
                sb.Append(strategy.ShowText(glyph, size)).Append('\n');
            }
        }

        /// <summary>
        /// Splits into text elements — a surrogate pair is one comb cell, not two — capped at the
        /// cell count.
        /// </summary>
        private static List<string> SplitTextElements(string text, int max)
        {
            var result = new List<string>(Math.Min(text.Length, max));
            for (int i = 0; i < text.Length && result.Count < max; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    result.Add(text.Substring(i, 2));
                    i++;
                }
                else
                {
                    result.Add(text[i].ToString());
                }
            }
            return result;
        }

        private static double HorizontalOffset(double textWidth, double boxWidth, int quadding)
        {
            double available = boxWidth - 2 * Margin;
            if (textWidth <= 0 || textWidth >= available) return Margin;

            switch (quadding)
            {
                case PdfQuadding.Centred: return Margin + (available - textWidth) / 2.0;
                case PdfQuadding.Right: return Margin + (available - textWidth);
                default: return Margin;
            }
        }

        /// <summary>
        /// Resolves the font size and, with it, the wrapped lines — the two cannot be settled
        /// separately for a multiline field, because wrapping at one size produces a line count
        /// that may not fit, and shrinking changes where the wraps fall.
        /// </summary>
        private static float ResolveSize(
            string text, double width, double height, DefaultAppearance da, FieldLayout layout,
            TextStrategy strategy, bool comb, FormFillOptions options, out List<string> lines)
        {
            // The /DA size wins; failing that the caller's, which is what FormFillOptions.FontSize
            // is for when /DA is absent or HonorDefaultAppearance is off. Only then auto-size.
            float explicitSize = da is { FontSize: > 0 } ? da.FontSize
                               : options?.FontSize > 0 ? options.FontSize
                               : 0f;

            if (explicitSize > 0)
            {
                lines = LayoutLines(text, layout, width, explicitSize, strategy);
                return explicitSize;
            }

            // Comb cells set the size: a character has to fit its cell, so the box width divided by
            // the cell count is the ceiling however tall the widget is.
            if (comb)
            {
                lines = new List<string> { text };
                double cell = width / layout.MaxLength;
                return (float)Math.Max(MinAutoFontSize, Math.Min(height * AutoSizeHeightFraction, cell));
            }

            float size = (float)Math.Max(DefaultAutoFontSize, height * AutoSizeHeightFraction);

            if (!layout.Multiline)
            {
                lines = LayoutLines(text, layout, width, size, strategy);
                double widest = 0;
                foreach (string line in lines) widest = Math.Max(widest, strategy.Measure(line));
                if (widest > 0)
                {
                    double available = width - 2 * Margin;
                    if (widest * size > available)
                        size = (float)Math.Max(MinAutoFontSize, available / widest);
                }
                return size;
            }

            // Multiline: search downwards for a size whose wrapped block fits the box.
            double usableHeight = height - 2 * Margin;
            for (int attempt = 0; attempt < MaxAutoSizeAttempts; attempt++)
            {
                lines = LayoutLines(text, layout, width, size, strategy);
                if (lines.Count * size * LineHeightFactor <= usableHeight || size <= MinAutoFontSize)
                    return size;

                size -= AutoSizeShrinkStep;
                if (size < MinAutoFontSize) size = MinAutoFontSize;
            }

            lines = LayoutLines(text, layout, width, size, strategy);
            return size;
        }

        /// <summary>
        /// Splits on explicit newlines, then word-wraps each paragraph to the box width when the
        /// field is multiline. A single-line field never wraps: it scrolls in a viewer, and its
        /// auto-size shrinks to fit instead.
        /// </summary>
        private static List<string> LayoutLines(
            string text, FieldLayout layout, double width, float size, TextStrategy strategy)
        {
            if (!layout.Multiline)
                return new List<string> { text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ') };

            var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            double available = width - 2 * Margin;
            var lines = new List<string>();

            foreach (string paragraph in paragraphs)
                WrapParagraph(paragraph, available, size, strategy, lines);

            return lines;
        }

        /// <summary>
        /// Longest line, in characters, a shaped run is measured whole for; past it the next word's
        /// width is added to the line's. A line of a real field is a few dozen characters.
        /// </summary>
        internal const int MaxExactlyMeasuredLine = 1024;

        /// <summary>Width measurements made on the calling thread's wraps: the evidence that each word is measured once.</summary>
        [ThreadStatic] internal static long WrapMeasuredChars;

        // A line's width with one more piece, in em: the strategy's sum for an additive measure (a
        // simple font's widths add) and for a shaped line past MaxExactlyMeasuredLine, the whole
        // candidate measured otherwise.
        private static double Widened(TextStrategy strategy, StringBuilder line, double lineWidth, string piece)
        {
            if (strategy.Additive || line.Length > MaxExactlyMeasuredLine)
            {
                WrapMeasuredChars += piece.Length;
                return line.Length == 0 ? strategy.Measure(piece) : lineWidth + strategy.Measure(piece);
            }
            WrapMeasuredChars += line.Length + piece.Length;
            return strategy.Measure(line.Length == 0 ? piece : line + piece);
        }

        /// <summary>
        /// Greedy word wrap. A single word too wide for the box is broken by character rather than
        /// left to overflow the clip, which would silently hide the rest of the value.
        /// </summary>
        /// <remarks>
        /// The line's width is carried from word to word (<see cref="Widened"/>): each word copied and
        /// measured the whole line, so a value whose line never filled — a font measuring 0, a very
        /// wide box — cost its length squared over its word length.
        /// </remarks>
        private static void WrapParagraph(
            string paragraph, double available, float size, TextStrategy strategy, List<string> lines)
        {
            if (paragraph.Length == 0 || available <= 0) { lines.Add(paragraph); return; }

            var current = new StringBuilder();
            double currentWidth = 0;
            foreach (string word in SplitKeepingSpaces(paragraph))
            {
                bool empty = current.Length == 0;
                string piece = empty ? word.TrimStart() : word;
                if (piece.Length == 0 && empty) continue;

                double width = Widened(strategy, current, currentWidth, piece);
                if (width * size <= available)
                {
                    current.Append(piece);
                    currentWidth = width;
                    continue;
                }

                if (empty)
                {
                    // The word alone does not fit — break it across lines by character.
                    foreach (string part in BreakWord(piece, available, size, strategy))
                    {
                        if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }
                        current.Append(part);
                    }
                    currentWidth = strategy.Measure(current.ToString());
                    continue;
                }

                lines.Add(current.ToString());
                current.Clear();

                // The word starting the new line is held to the box as a first word is: one wider
                // than the box after other text was started on its own line whole and clipped.
                string head = word.TrimStart();
                if (strategy.Measure(head) * size <= available)
                {
                    current.Append(head);
                    currentWidth = strategy.Measure(head);
                    continue;
                }
                foreach (string part in BreakWord(head, available, size, strategy))
                {
                    if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }
                    current.Append(part);
                }
                currentWidth = strategy.Measure(current.ToString());
            }

            lines.Add(current.ToString());
        }

        /// <summary>Splits into words, keeping the space that precedes each so widths stay honest.</summary>
        private static IEnumerable<string> SplitKeepingSpaces(string paragraph)
        {
            int start = 0;
            for (int i = 1; i < paragraph.Length; i++)
            {
                if (paragraph[i] != ' ') continue;
                yield return paragraph.Substring(start, i - start);
                start = i;
            }
            yield return paragraph.Substring(start);
        }

        private static IEnumerable<string> BreakWord(
            string word, double available, float size, TextStrategy strategy)
        {
            var piece = new StringBuilder();
            double pieceWidth = 0;
            foreach (char c in word)
            {
                string ch = c.ToString();
                double width = Widened(strategy, piece, pieceWidth, ch);
                if (piece.Length > 0 && width * size > available)
                {
                    yield return piece.ToString();
                    piece.Clear();
                    width = Widened(strategy, piece, 0, ch);
                }
                piece.Append(c);
                pieceWidth = width;
            }
            if (piece.Length > 0) yield return piece.ToString();
        }

        // ----------------------------------------------------------------
        //  Measurement / encoding helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Width of <paramref name="text"/> in em units (multiply by the font size for points).
        /// Uses the font's own <c>/Widths</c> when present, else the Standard-14 AFM metrics.
        /// Returns 0 when neither is available — the caller then leaves the text left-aligned and
        /// un-shrunk rather than guessing.
        /// </summary>
        /// <param name="text">The value measured.</param>
        /// <param name="fontDict">The font's dictionary, for its <c>/Widths</c>; null for a font this writer makes.</param>
        /// <param name="baseFont">The font's <c>/BaseFont</c>, for the Standard-14 metrics.</param>
        /// <param name="doc">Resolves the dictionary's entries.</param>
        /// <param name="codes">
        /// The code each character is written as under the font's encoding, which <c>/Widths</c> is
        /// indexed by; null when the bytes are the characters' Latin-1 values.
        /// </param>
        private static double MeasureSimple(string text, PdfDictionary fontDict, string baseFont, PdfDocument doc,
                                            Dictionary<char, int> codes = null)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            var widths = doc?.ResolveObject(fontDict?[PdfName.Widths]) as PdfArray;
            // `?? 0` is the ARRAY ORIGIN, not a neutral default: the loop below indexes
            // `code - firstChar`, or a font whose real /FirstChar is 32 would measure every glyph 32
            // entries early and return a real width belonging to a different one. This is the read
            // that decides where text lands in a GENERATED appearance stream, which is
            // then baked into the document.
            int firstChar = (doc?.ResolveObject(fontDict?[PdfName.FirstChar]) as PdfNumber)?.IntValue ?? 0;
            bool standard14 = Standard14Metrics.IsStandard14(baseFont);
            if (widths == null && !standard14) return 0;

            double total = 0;
            foreach (char c in text)
            {
                double w = 0;
                int code = codes != null && codes.TryGetValue(c, out int encoded) ? encoded : c;
                int index = code - firstChar;
                if (widths != null && index >= 0 && index < widths.Count)
                    w = (widths[index] as PdfNumber)?.DoubleValue ?? 0;
                if (w == 0 && standard14)
                    w = Standard14Metrics.GetWidthByUnicode(baseFont, c);
                total += w;
            }
            return total / PdfUnits.GlyphSpaceUnitsPerEm;
        }

        /// <summary>
        /// True when every character can be written as one byte in a Standard-14 / WinAnsi font.
        /// <c>0x80</c>–<c>0x9F</c> are excluded on purpose: WinAnsiEncoding maps that block to
        /// typographic glyphs (€, ‰, œ …) rather than to the Latin-1 control codes, so passing the
        /// raw byte through would draw the wrong glyph. Those route to the composite path instead.
        /// </summary>
        /// <remarks>
        /// The rule is <see cref="TextRunComposer.NeedsCompositeFont"/>'s, inverted, rather than a
        /// second copy of the same two ranges: the watermarker carried a third.
        /// </remarks>
        internal static bool IsSingleByteEncodable(string text)
            => !TextRunComposer.NeedsCompositeFont(text);


        private static string Fmt(double v) => PdfRealFormat.Format(v, "F2");

        private static string Inv(string format, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
