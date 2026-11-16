// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using SKColor = SkiaSharp.SKColor;
using SKColors = SkiaSharp.SKColors;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Every <i>decision</i> behind a synthetic no-<c>/AP</c> annotation appearance (§12.5.5): what
    /// the file asked for, and where the shape goes. Both renderers ask this type; each keeps only
    /// its emit step — Skia paint calls on one side, SVG elements on the other.
    /// </summary>
    /// <remarks>
    /// <para>Seven subtypes — Text, PrinterMark, Caret, FreeText, Stamp, Widget and 3D — had a
    /// fallback in the raster path and <b>none</b> in the SVG exporter, which dropped all seven into
    /// its <c>default</c> arm: a dashed grey placeholder plus an
    /// <see cref="RenderDiagnosticKind.AnnotationSubtypeUnsupported"/> report that was untrue of
    /// every one of them. A <c>PrinterMark</c>, which the raster path deliberately draws
    /// <b>nothing</b> for, exported as a dashed box over the crop marks.</para>
    ///
    /// <para><b>Sharing the paint call is not the point; sharing the reading is.</b> The two
    /// renderers cannot share a paint call — one has an <c>SKCanvas</c> and the other a
    /// <see cref="System.Text.StringBuilder"/> — so what is shared is everything <i>before</i> the
    /// paint: the <c>/DA</c> parse and its three inheritable lookups, the <c>/Contents</c>-then-
    /// <c>/RC</c> choice, the stamp palette, the border width, and the placement arithmetic whose
    /// magic numbers would otherwise be typed twice and drift apart. That drift is this repository's
    /// recurring defect class, and a duplicated <c>0.55f</c> advance estimate is exactly how it
    /// starts.</para>
    ///
    /// <para><b>A leaf by construction</b> — no static fields, so no type initializer and no
    /// initialization edge between the renderers (the hazard recorded in CLAUDE.md that once crashed
    /// the Base test host mid-run). <c>const</c> carries no initializer. Same shape as
    /// <see cref="AnnotationQuadPoints"/>.</para>
    /// </remarks>
    internal static class AnnotationFallbackModel
    {
        // ------------------------------------------------------------------
        //  /DA — the default appearance string (§12.7.3.3)
        // ------------------------------------------------------------------

        /// <summary>
        /// The font size and fill colour a <c>/DA</c> string asks for. <c>/DA</c> strings look like
        /// <c>"/Helv 12 Tf 0 0 0 rg"</c>; a fallback needs only those two, and everything else is
        /// best-effort.
        /// </summary>
        /// <remarks>
        /// <para>The quantisation goes through <see cref="PdfColor.Channel"/>, which clamps <b>and</b>
        /// rounds, so a <c>/DA</c> of <c>0.5 g</c> renders 128 as the identical <c>g</c> operator in a
        /// content stream does. It is a third reader of <c>rg</c>/<c>g</c>, reached only through a
        /// <c>/DA</c> <i>string</i>, which a sweep over the operator handlers does not see.</para>
        /// <para><b>The reading is <see cref="Base.Operations.Forms.DefaultAppearanceString"/></b>, shared with
        /// the form appearance generator and the annotation appearance builder. This copy read
        /// <c>g</c> and <c>rg</c> and not <c>k</c>, so a CMYK <c>/DA</c> drew black in both renderers
        /// while the appearance generated for the same annotation used the colour. CMYK goes through
        /// <see cref="PdfColor.CmykToRgb"/>, the renderers' one conversion.</para>
        /// </remarks>
        internal static (float size, SKColor fill) ParseDefaultAppearance(string da)
        {
            var parsed = Base.Operations.Forms.DefaultAppearanceString.Parse(da);
            float size = parsed.FontSize ?? Base.Operations.Forms.DefaultAppearanceString.DefaultFontSize;

            double[] c = parsed.Fill;
            SKColor fill = c == null ? SKColors.Black
                : c.Length == 1 ? Gray(PdfColor.Channel((float)c[0]))
                : c.Length == 3 ? new SKColor(PdfColor.Channel((float)c[0]), PdfColor.Channel((float)c[1]),
                                              PdfColor.Channel((float)c[2]))
                : PdfColor.CmykToRgb((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
            return (size, fill);

            static SKColor Gray(byte v) => new SKColor(v, v, v);
        }

        /// <summary>
        /// An inheritable AcroForm entry, from the nearest node up the widget's <c>/Parent</c> chain
        /// that supplies a value <paramref name="accept"/> can use.
        /// </summary>
        /// <remarks>
        /// <para><b>One climb behind all three reads</b> — <c>/FT</c>, <c>/V</c> and <c>/DA</c>.
        /// Three hand-written copies of the loop side by side would let one stay un-climbed while the
        /// others are fixed: nothing there looks like a missing case.</para>
        ///
        /// <para>The cap is <see cref="PdfLimits.MaxFieldInheritanceDepth"/>. The walk is
        /// <b>single-successor</b>, so cost is strictly O(cap) per widget, never branching; the cap
        /// is also the only guard against a <c>/Parent</c> ring, so it may be raised but must never
        /// be removed.</para>
        /// </remarks>
        internal static PdfObject ResolveWidgetEntry(
            PdfDictionary annot, PdfDocument doc, PdfName key, Func<PdfObject, bool> accept)
        {
            var raw = AcroFieldInheritance.Find(
                annot, key,
                d => doc?.ResolveObject(d[PdfName.Parent]) as PdfDictionary,
                doc == null ? (Func<PdfObject, PdfObject>)null : doc.ResolveObject,
                accept,
                PdfLimits.MaxFieldInheritanceDepth,
                out _);

            return doc?.ResolveObject(raw) ?? raw;
        }

        /// <summary>
        /// The <c>/DA</c> that governs a widget's text: the nearest one up the <c>/Parent</c>
        /// chain, then the document-wide <c>/AcroForm /DA</c> (§12.7.3.3).
        /// </summary>
        /// <remarks>
        /// <para><b>Not read off the widget alone, which is the one place it usually is not.</b>
        /// §12.7.3.3 marks <c>/DA</c> "Required; inheritable" and a producer writes it on the
        /// <i>field</i> — or once on <c>/AcroForm</c> for the whole document — leaving the widget with
        /// only its appearance. Read off the widget, the string would be null on a conformant file,
        /// and <c>ParseDefaultAppearance(null)</c> yields the default size in black: every field
        /// without an <c>/AP</c> would draw at the fallback size in black, whatever the file asks
        /// for.</para>
        ///
        /// <para><b><c>/DA</c> is Table 222, not Table 220</b> — §12.7.3.3's variable-text entries,
        /// not §12.7.3.1's common ones. It inherits up the same field hierarchy, but a reader
        /// checking Table 220 for the inheritable list will not find it there.</para>
        ///
        /// <para>The <c>/AcroForm</c> step is the half a <c>/Parent</c> climb structurally cannot
        /// reach: the root field's parent is the form, not another field. Without it a document
        /// that sets one default for every field still renders none of them.</para>
        /// </remarks>
        internal static string InheritedDefaultAppearance(PdfDictionary annot, PdfDocument doc)
            => DefaultAppearanceOrFormDefault(
                   ResolveWidgetEntry(annot, doc, PdfName.DA, o => o is PdfString), doc);

        /// <summary>
        /// The <c>/Q</c> that governs a widget's text: the nearest usable one up the
        /// <c>/Parent</c> chain, then the document-wide <c>/AcroForm /Q</c>, or
        /// <see langword="null"/> when neither says.
        /// </summary>
        internal static int? InheritedQuadding(PdfDictionary annot, PdfDocument doc)
            => QuaddingOrFormDefault(
                   ResolveWidgetEntry(annot, doc, PdfName.Q, o => o is PdfNumber), doc);

        /// <summary>
        /// The <c>/DA</c> a field's own climb found, else the document-wide <c>/AcroForm /DA</c>.
        /// </summary>
        /// <param name="climbed">
        /// The resolved result of the caller's climb. The climb stays with the caller because there
        /// are two — a widget's <c>/Parent</c> chain here, a field's arrival chain in
        /// <c>PdfFormField.GetInherited</c> — and only this step is the same for both.
        /// </param>
        /// <param name="doc">The document whose <c>/AcroForm</c> supplies the default.</param>
        internal static string DefaultAppearanceOrFormDefault(PdfObject climbed, PdfDocument doc)
            => ((climbed as PdfString) ?? PdfResolve.Str(doc, AcroFormDictionary(doc), PdfName.DA))?.GetText();

        /// <summary>
        /// The <c>/Q</c> a field's own climb found, else the document-wide <c>/AcroForm /Q</c>, or
        /// <see langword="null"/> when neither says.
        /// </summary>
        /// <param name="climbed">The resolved result of the caller's climb, as for <see cref="DefaultAppearanceOrFormDefault"/>.</param>
        /// <param name="doc">The document whose <c>/AcroForm</c> supplies the default.</param>
        /// <remarks>
        /// <para><b>ISO 32000-2 Table 224 gives <c>/AcroForm</c> a <c>/Q</c> as well as a
        /// <c>/DA</c></b> — "a document-wide default value for the Q attribute of variable text
        /// fields" — and five sites answered "how is this field's text aligned" three ways. The
        /// filler fell back to it, but through <c>GetInteger</c>, which does not follow a reference;
        /// the reader, the flattener and both renderers never consulted it at all. So a form that
        /// centres every field once, at the form level, was filled centred, flattened left-aligned,
        /// reported by <c>PdfFormReader</c> as left-aligned, and drawn left-aligned by the no-/AP
        /// fallback <i>without</i> the <c>FormFieldQuaddingIgnored</c> report that exists so a wrong
        /// alignment does not look like a file that never asked.</para>
        ///
        /// <para><b>The fallback is taken only when the climb found nothing.</b> A field's own
        /// <c>/Q 0</c> is an answer — left-aligned on purpose — and must not be overridden by the
        /// form's default, which is why this tests the climbed object and not its value.</para>
        /// </remarks>
        internal static int? QuaddingOrFormDefault(PdfObject climbed, PdfDocument doc)
            => ((climbed as PdfNumber) ?? PdfResolve.Number(doc, AcroFormDictionary(doc), PdfName.Q))?.IntValue;

        /// <summary>The document's <c>/AcroForm</c> dictionary, resolved, or <c>null</c>.</summary>
        private static PdfDictionary AcroFormDictionary(PdfDocument doc)
            => doc?.Catalog == null
                ? null
                : doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;

        /// <summary>Walks <c>/V</c> up the <c>/Parent</c> chain, taking a string or a name value.</summary>
        internal static string ReadWidgetValue(PdfDictionary annot, PdfDocument doc)
        {
            var v = ResolveWidgetEntry(annot, doc, PdfName.V, o => o is PdfString || o is PdfName);
            if (v is PdfString ps) return ps.GetText();
            if (v is PdfName pn)   return pn.Value;
            return null;
        }

        /// <summary>
        /// The text a widget <b>shows</b> for its value: <see cref="ReadWidgetValue"/>, with a password
        /// text field's characters replaced by asterisks.
        /// </summary>
        /// <remarks>
        /// <para>ISO 32000-2 Table 229, bit 14: a password field's characters "shall instead be echoed
        /// in some unreadable form, such as asterisks or bullet characters". Both no-<c>/AP</c>
        /// fallbacks and the flattener's §12.7.3.3 regeneration drew the value itself — and the
        /// flattener makes what it draws permanent page content. All three draw through this, so what
        /// a password looks like is decided once. The staleness test keeps
        /// <see cref="ReadWidgetValue"/>: whether there is a value is not a question about how it
        /// looks.</para>
        /// <para><b>Asterisks, not bullets</b>, because U+2022 is outside the single-byte encoding a
        /// generated appearance can draw without a supplied font. <b>One per text element</b>, so a
        /// surrogate pair or a combining sequence is one asterisk, as it is one character typed.</para>
        /// </remarks>
        internal static string DisplayedWidgetValue(PdfDictionary annot, PdfDocument doc)
        {
            string value = ReadWidgetValue(annot, doc);
            if (string.IsNullOrEmpty(value)) return value;

            string ft = (ResolveWidgetEntry(annot, doc, PdfName.FT, o => o is PdfName) as PdfName)?.Value;
            if (ft != PdfFieldTypes.Text) return value;

            int flags = (ResolveWidgetEntry(annot, doc, PdfName.Ff, o => o is PdfNumber) as PdfNumber)?.FlagWord ?? 0;
            return PdfFieldFlags.Has(flags, PdfFieldFlags.Password)
                ? new string('*', new System.Globalization.StringInfo(value).LengthInTextElements)
                : value;
        }

        // ------------------------------------------------------------------
        //  §12.7.3.3 /NeedAppearances — is the file's own appearance stale?
        // ------------------------------------------------------------------

        /// <summary>
        /// Whether the document's interactive form sets <c>/NeedAppearances</c> (§12.7.3.3): the
        /// producer wrote field values and is asking the viewer to construct the appearance streams.
        /// </summary>
        /// <remarks>
        /// Read once per page render and handed to <see cref="WidgetAppearanceIsStale"/>, because it
        /// is a document-scoped answer and both renderers would otherwise re-resolve the catalog for
        /// every annotation on the page.
        /// </remarks>
        internal static bool FormNeedsAppearances(PdfDocument doc)
            => PdfResolve.Bool(doc, AcroFormDictionary(doc), PdfName.NeedAppearances)?.Value == true;

        /// <summary>
        /// Whether a widget's existing normal appearance must be <b>discarded and regenerated</b>
        /// under §12.7.3.3, rather than drawn.
        /// </summary>
        /// <param name="annot">The widget annotation.</param>
        /// <param name="doc">The document, for the inheritable <c>/FT</c> and <c>/V</c> reads.</param>
        /// <param name="normalAppearance">
        /// The appearance the renderer has already selected from <c>/AP</c> <c>/N</c> — after the
        /// <c>/AS</c> sub-state choice. Passed in rather than re-selected here so that a third copy
        /// of that selection cannot drift from the two that draw it.
        /// </param>
        /// <param name="formNeedsAppearances">The document-scoped answer from
        /// <see cref="FormNeedsAppearances"/>.</param>
        /// <remarks>
        /// <para><b>The defect this closes.</b> A form filled by a producer that relies on the flag
        /// carries the blank template's appearance stream beside a written <c>/V</c>. Neither
        /// renderer read <c>/NeedAppearances</c> at all, so that appearance was drawn and the field
        /// rendered <b>empty</b> — the same picture a form nobody filled in produces.</para>
        ///
        /// <para><b>Narrowed on purpose, in three ways</b>, because the synthetic fallback is a
        /// rougher drawing than a real appearance stream and replacing a good one is a regression:</para>
        /// <list type="number">
        ///   <item><b>Variable-text fields only</b> — <c>/FT /Tx</c> and <c>/FT /Ch</c>. A
        ///   <c>/Btn</c>'s appearance is selected by <c>/AS</c> naming a sub-state that the file
        ///   supplies, so it is correct by construction and regenerating it would put this
        ///   library's synthetic tick where a designed checkbox, radio or push-button face is.</item>
        ///   <item><b>Only when the field has a value.</b> With no <c>/V</c> the fallback draws a
        ///   bare border, while the template appearance carries the field's <c>/MK</c> background
        ///   and border — strictly the better of the two.</item>
        ///   <item><b>Only when the appearance shows nothing.</b> A producer that sets the flag
        ///   <i>and</i> generates appearances anyway is common; its streams are not stale, and
        ///   §12.7.3.3 asking a viewer to regenerate is not a reason to draw worse. So the stream is
        ///   scanned, and any text-showing operator — or any <c>Do</c>, which may show text through
        ///   a form XObject — means "this appearance draws something" and it is kept.</item>
        /// </list>
        ///
        /// <para>An appearance that will not decode or parse is likewise kept: an unreadable stream
        /// is not evidence of a blank one, and the drawing path reports its own failure.</para>
        ///
        /// <para><b>It returns the reason rather than reporting it</b>, and that is not cosmetic:
        /// there are three callers and they do not share a sink. Both renderers report to
        /// <see cref="RenderDiagnostics"/>; <c>PdfFormFlattener</c> is a document <i>edit</i> and
        /// reports to <c>DocumentEditDiagnostics</c>. Reporting inside would have forced the
        /// flattener to inherit a render diagnostic — the sink a host filters for telemetry about
        /// drawing — for something it wrote permanently to a file. One wording, three sinks; the
        /// decision and the sentence stay single-sourced, only the channel differs.</para>
        /// </remarks>
        /// <returns>
        /// <c>null</c> when the file's own appearance stands, else the reason it does not — suitable
        /// as a diagnostic detail, and non-null is the "regenerate" signal.
        /// </returns>
        internal static string WidgetAppearanceIsStale(
            PdfDictionary annot, PdfDocument doc, PdfObject normalAppearance, bool formNeedsAppearances)
        {
            if (!formNeedsAppearances || annot == null) return null;
            if ((PdfResolve.Name(doc, annot, PdfName.Subtype))?.Value != PdfAnnotationSubtypes.Widget)
                return null;

            string ft = (ResolveWidgetEntry(annot, doc, PdfName.FT, o => o is PdfName) as PdfName)?.Value;
            if (ft != PdfFieldTypes.Text && ft != PdfFieldTypes.Choice) return null;

            string value = ReadWidgetValue(annot, doc);
            if (string.IsNullOrEmpty(value)) return null;

            if (AppearanceDrawsSomething(normalAppearance as PdfStream)) return null;

            return "/NeedAppearances is set and the /" + ft + " field's appearance showed no text, "
                 + "so its value (" + value.Length + " chars) was regenerated rather than drawn "
                 + "from the file";
        }

        /// <summary>
        /// Whether an appearance stream contains any operator that could put marks a form value
        /// would appear as: the four text-showing operators, or <c>Do</c>.
        /// </summary>
        /// <remarks>
        /// Conservative in every failure direction — an absent stream, one over
        /// <see cref="PdfLimits.MaxWidgetAppearanceScanBytes"/>, one that will not decode and one
        /// that will not parse all answer <c>true</c>, which keeps the file's own appearance.
        /// </remarks>
        private static bool AppearanceDrawsSomething(PdfStream appearance)
        {
            if (appearance == null) return true;

            byte[] data;
            try
            {
                data = appearance.GetDecodedData();
                if (data == null) return true;
                if (data.Length > PdfLimits.MaxWidgetAppearanceScanBytes) return true;

                foreach (var op in Core.Text.ContentStreamParser.Parse(data))
                {
                    switch (op.Operator)
                    {
                        case PdfOperators.ShowText:
                        case PdfOperators.ShowTextArray:
                        case PdfOperators.NextLineShowText:
                        case PdfOperators.NextLineSetSpacingShowText:
                        case PdfOperators.InvokeXObject:
                            return true;
                    }
                }
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(
                    RenderDiagnosticKind.ContentStreamError,
                    "a widget appearance could not be scanned for /NeedAppearances staleness; "
                    + "the file's own appearance was kept", ex);
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        //  Border geometry — /BS, else the legacy /Border
        // ------------------------------------------------------------------

        /// <summary>
        /// The border width (§12.5.4): <c>/BS /W</c> when a border-style dictionary is present, else
        /// the third element of the legacy <c>/Border [Hr Vr W]</c>, else 1.
        /// </summary>
        /// <param name="annot">The annotation dictionary.</param>
        /// <param name="doc">The document, for resolving indirect entries.</param>
        /// <param name="dash">
        /// <c>/BS /D</c>, or <c>null</c>. The legacy <c>/Border</c>'s optional fourth element is
        /// deliberately not read here — neither renderer has ever honoured it, and inventing that
        /// here would change what one of them draws.
        /// </param>
        internal static float BorderWidth(PdfDictionary annot, PdfDocument doc, out float[] dash)
        {
            dash = null;
            float width = 1f;
            if (annot == null) return width;

            var bs = doc?.ResolveObject(annot[PdfName.BS]) as PdfDictionary;
            if (bs != null)
            {
                if (MatrixGuard.TryFinite(PdfResolve.Number(doc, bs, PdfName.W), out float bw)) width = bw;
                var dArr = PdfResolve.Array(doc, bs, PdfName.D);
                if (dArr != null && dArr.Count > 0)
                {
                    var lengths = new float[dArr.Count];
                    for (int i = 0; i < dArr.Count; i++) lengths[i] = PdfResolve.ArrayFloat(doc, dArr, i);
                    // A length past float's range leaves the border solid, as a `d` with such an operand is ignored.
                    if (MatrixGuard.AllFinite(lengths)) dash = lengths;
                }
                return width;
            }

            var borderArr = PdfResolve.Array(doc, annot, PdfName.Border);
            if (borderArr != null && borderArr.Count >= 3
                && MatrixGuard.TryFinite(PdfResolve.Number(doc, borderArr[2]), out float borderWidth))
                width = borderWidth;
            return width;
        }

        // ------------------------------------------------------------------
        //  Text — the sticky note (§12.5.6.4)
        // ------------------------------------------------------------------

        /// <summary>Icon size a sticky note draws at; <c>/Rect</c> on a Text annotation is often tiny.</summary>
        internal const float NoteIconWidth = 18f;

        /// <inheritdoc cref="NoteIconWidth"/>
        internal const float NoteIconHeight = 20f;

        /// <summary>Whether the annotation carries a <c>/C</c> entry at all.</summary>
        /// <remarks>
        /// Deliberately a presence test rather than a colour test: a sticky note that asks for
        /// black must get black, not the default yellow.
        /// </remarks>
        internal static bool DeclaresColor(PdfDictionary annot) => annot != null && annot[PdfName.C] != null;

        /// <summary>The conventional sticky-note yellow, used when the note declares no colour.</summary>
        internal static SKColor NoteDefaultColor => new SKColor(0xFF, 0xCC, 0x00);

        /// <summary>
        /// A sticky note's colour: its <c>/C</c> when it has one, else the conventional yellow.
        /// </summary>
        internal static SKColor NoteColor(PdfDictionary annot, SKColor borderColor)
            => DeclaresColor(annot) ? borderColor : NoteDefaultColor;

        // ------------------------------------------------------------------
        //  Caret (§12.5.6.11)
        // ------------------------------------------------------------------

        /// <summary>Height of the caret's apex as a fraction of its <c>/Rect</c>.</summary>
        internal const float CaretApexFactor = 0.6f;

        // ------------------------------------------------------------------
        //  FreeText (§12.5.6.6)
        // ------------------------------------------------------------------

        /// <summary>
        /// The text a FreeText annotation shows: <c>/Contents</c>, else <c>/RC</c> stripped of its
        /// XHTML-lite markup, else <c>null</c>.
        /// </summary>
        internal static string FreeTextContents(PdfDictionary annot, PdfDocument doc)
        {
            string contents = PdfResolve.Str(doc, annot, PdfName.Contents)?.GetText();
            if (!string.IsNullOrEmpty(contents)) return contents;

            string rc = PdfResolve.Str(doc, annot, PdfName.RC)?.GetText();
            return string.IsNullOrEmpty(rc) ? null : StripRichTextToPlain(rc);
        }

        /// <summary>A FreeText line's height as a multiple of its font size — the renderers' fallback's and the appearance builder's leading alike.</summary>
        internal const float FreeTextLineHeightFactor = 1.1f;

        /// <summary>
        /// The baseline of a FreeText's first line: one line-height below the rect top, padded in
        /// from the left.
        /// </summary>
        internal static void FreeTextBaseline(
            float rx0, float ry1, float fontSize, out float x, out float y)
        {
            x = rx0 + 2f;
            y = ry1 - fontSize * FreeTextLineHeightFactor;
        }

        /// <summary>
        /// Converts PDF <c>/RC</c> (XHTML-lite rich-text content, §12.5.6.4) to plain text by
        /// stripping every tag and decoding the entity subset those payloads use.
        /// </summary>
        internal static string StripRichTextToPlain(string rc)
        {
            if (string.IsNullOrEmpty(rc)) return string.Empty;

            var sb = new System.Text.StringBuilder(rc.Length);
            bool inTag = false;
            for (int i = 0; i < rc.Length; i++)
            {
                char c = rc[i];
                if (c == '<') { inTag = true;  continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag)   sb.Append(c);
            }

            string text = sb.ToString()
                .Replace("&amp;",  "&")
                .Replace("&lt;",   "<")
                .Replace("&gt;",   ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&nbsp;", " ");

            // Collapse consecutive whitespace (tags often introduce line breaks that look weird
            // when rendered inline).
            var collapsed = new System.Text.StringBuilder(text.Length);
            bool prevSpace = false;
            foreach (char c in text)
            {
                bool isSpace = char.IsWhiteSpace(c);
                if (isSpace)
                {
                    if (!prevSpace) collapsed.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    collapsed.Append(c);
                    prevSpace = false;
                }
            }
            return collapsed.ToString().Trim();
        }

        // ------------------------------------------------------------------
        //  Stamp (§12.5.6.12)
        // ------------------------------------------------------------------

        /// <summary>A stamp's outline is never thinner than this, whatever <c>/BS</c> says.</summary>
        internal const float StampMinLineWidth = 2f;

        /// <summary>The stamp's <c>/Name</c>, defaulting to the one viewers write.</summary>
        internal static string StampName(PdfDictionary annot, PdfDocument doc)
            => PdfResolve.Name(doc, annot, PdfName.Name)?.Value ?? "Draft";

        /// <summary>
        /// The colour a stamp takes from its <c>/Name</c> — green for approval, red for rejection,
        /// blue for everything else.
        /// </summary>
        internal static SKColor StampPalette(string name)
            => name switch
            {
                "Approved"  or "AsIs"    or "Completed" => new SKColor(0x20, 0x80, 0x40),
                "NotApproved" or "Confidential" or "Draft" or
                "Void" or "Expired"                     => new SKColor(0xC0, 0x20, 0x20),
                _                                        => new SKColor(0x30, 0x50, 0xB0),
            };

        /// <summary>
        /// Whether the stamp colours itself from its <c>/Name</c> rather than from <c>/C</c>.
        /// </summary>
        /// <remarks>
        /// <para>One question, asked in each renderer's own currency, kept side by side so they
        /// cannot answer it differently. The raster path holds an <see cref="SKColor"/> and the SVG
        /// path a CSS string, and the CSS overload has to accept <b>both</b> spellings: <c>black</c>
        /// is the exporter's absent-<c>/C</c> fallback literal, while an explicit <c>/C [0 0 0]</c>
        /// formats as <c>rgb(0,0,0)</c>. Testing only the literal would give a stamp with
        /// <c>/C [0 0 0]</c> the palette in one renderer and plain black in the other.</para>
        ///
        /// <para><b>They still differ on one degenerate input</b>, and that is a pre-existing raster
        /// quirk rather than something introduced here: <c>/C []</c> means "no colour" (§12.5.2), so
        /// the raster path reads it as transparent, fails the <c>== Black</c> test and strokes the
        /// stamp in nothing at all; the exporter reaches its fallback literal and takes the palette.
        /// Recorded rather than papered over.</para>
        /// </remarks>
        internal static bool UsesDefaultColor(SKColor borderColorOverride)
            => borderColorOverride == SKColors.Black;

        /// <inheritdoc cref="UsesDefaultColor(SKColor)"/>
        internal static bool UsesDefaultColor(string borderCss)
            => borderCss == "black" || borderCss == "rgb(0,0,0)";

        /// <summary>
        /// An annotation colour array (<c>/C</c> or <c>/IC</c>, §12.5.2 Table 164) as a colour: no
        /// components is transparent, and 1, 3 or 4 are DeviceGray, DeviceRGB or DeviceCMYK through
        /// <see cref="PdfColor"/>. <paramref name="fallback"/> when the key is absent or the count is
        /// malformed.
        /// </summary>
        /// <param name="annot">The annotation.</param>
        /// <param name="doc">The document, for an indirect array; may be null.</param>
        /// <param name="key"><c>/C</c> or <c>/IC</c>.</param>
        /// <param name="fallback">The colour an absent or malformed array reads as.</param>
        /// <remarks>
        /// Moved here from the raster renderer, which kept it private, so that the annotation
        /// appearance builder colours a stamp by the rule the fallback does rather than by a copy
        /// of it. Count 2 stays a fallback rather than being padded to RGB.
        /// </remarks>
        internal static SKColor AnnotationColor(
            PdfDictionary annot, PdfDocument doc, PdfName key, SKColor fallback)
        {
            var cObj = PdfResolve.Value(doc, annot?[key]) as PdfArray;
            if (cObj == null) return fallback;
            if (cObj.Count == 0) return SKColors.Transparent;

            if (cObj.Count != PdfColorSpaceNames.GrayComponents
             && cObj.Count != PdfColorSpaceNames.RgbComponents
             && cObj.Count != PdfColorSpaceNames.CmykComponents) return fallback;

            // Routed through the shared converter rather than open-coded. This and its SVG twin
            // (PdfSvgRenderer.AnnotColorToSvg) agreed with each other and disagreed with PdfColor,
            // so raster/SVG parity testing could never surface the difference. GAPS.md §2b.
            var comps = new float[cObj.Count];
            for (int i = 0; i < comps.Length; i++)
                comps[i] = PdfResolve.ArrayFloat(doc, cObj, i);

            return PdfColor.ComponentsToColor(comps, PdfColor.DeviceSpaceForComponentCount(comps.Length));
        }

        /// <summary>
        /// A stamp's colour: its <c>/C</c> when the file set one, else the <see cref="StampPalette"/>
        /// colour for its name.
        /// </summary>
        internal static SKColor StampColor(string name, SKColor borderColorOverride)
            => UsesDefaultColor(borderColorOverride)
                ? StampPalette(name)
                : borderColorOverride;

        /// <summary>
        /// Size and baseline of the stamp-name caption, centred in the rect by the same
        /// advance-width estimate both renderers must use to land it in the same place.
        /// </summary>
        /// <returns><c>false</c> when the rect leaves no room for a caption.</returns>
        internal static bool StampCaption(
            string name, float rx0, float ry0, float rx1, float ry1,
            out float fontSize, out float baselineX, out float baselineY)
        {
            fontSize = baselineX = baselineY = 0f;
            if (string.IsNullOrEmpty(name)) return false;

            float rw = rx1 - rx0, rh = ry1 - ry0;
            float nameLen = Math.Max(4, name.Length);
            fontSize = Math.Min(rh * 0.5f, rw / nameLen * 1.5f);
            if (fontSize <= 0f) return false;

            float textWidth = fontSize * 0.55f * name.Length;
            baselineX = rx0 + (rw - textWidth) * 0.5f;
            baselineY = ry0 + (rh - fontSize) * 0.5f + fontSize * 0.15f;
            return true;
        }

        // ------------------------------------------------------------------
        //  Widget (§12.5.6.19 / §12.7)
        // ------------------------------------------------------------------

        /// <summary>The tick (U+2713) a checked check box or radio button draws.</summary>
        internal const string CheckMark = "\u2713";

        /// <summary>Size and baseline of the check mark, centred in the widget rect.</summary>
        internal static void CheckMarkPlacement(
            float rx0, float ry0, float rx1, float ry1,
            out float markSize, out float baselineX, out float baselineY)
        {
            float rw = rx1 - rx0, rh = ry1 - ry0;
            markSize  = Math.Min(rw, rh) * 0.85f;
            baselineX = rx0 + (rw - markSize * 0.55f) * 0.5f;
            baselineY = ry0 + (rh - markSize) * 0.5f + markSize * 0.15f;
        }

        /// <summary>
        /// Baseline of a text/choice field's value: inset from the left, vertically centred.
        /// </summary>
        /// <remarks>
        /// Left-aligned whatever <c>/Q</c> says — honouring it needs text measurement this fallback
        /// does not do. Both renderers report that rather than dropping it silently.
        /// </remarks>
        internal static void WidgetValueBaseline(
            float rx0, float ry0, float ry1, float fontSize, out float x, out float y)
        {
            x = rx0 + 2f;
            y = ry0 + ((ry1 - ry0) - fontSize) * 0.5f + fontSize * 0.15f;
        }

        /// <summary>
        /// The size a field's value draws at: the <c>/DA</c> size, or — when <c>/DA</c> asked for
        /// auto-size (0) — a fraction of the rect height.
        /// </summary>
        internal static float WidgetFontSize(float daSize, float ry0, float ry1)
            => daSize > 0f ? daSize : Math.Max(6f, (ry1 - ry0) * 0.65f);

        /// <summary>
        /// The colour a widget's synthesised border is stroked in, or <see langword="null"/> when
        /// no border is drawn at all.
        /// </summary>
        /// <param name="annot">The widget annotation.</param>
        /// <param name="doc">The document, for resolving indirect entries.</param>
        /// <param name="borderWidth">The width <see cref="BorderWidth"/> read for it.</param>
        /// <remarks>
        /// <para><b>Both renderers framed every no-<c>/AP</c> widget in black.</b> The colour came
        /// from <c>/C</c> — Table 166's colour for a markup icon's background, a popup's title bar
        /// and a <i>link's</i> border (ISO 32000-2, lines 38446–38451 of
        /// <c>docs/iso/iso32000-2.txt</c>) — defaulting to black, and the width was floored at 1 pt
        /// even when the file said 0. A widget's border colour is Table 192's <c>/MK /BC</c>
        /// (41229–41236), whose empty form means "No colour; transparent"; and a width of 0 means no
        /// border — Table 168's <c>/W</c> (38780–38781: "If this value is 0, no border shall be
        /// drawn") and the legacy <c>/Border</c> (38404–38405) agree. So every unsigned signature
        /// field without an appearance stream was boxed in black, in both renderers, and comparing
        /// them reported the shared defect as agreement.</para>
        ///
        /// <para>Absent <c>/MK</c>, absent <c>/BC</c> and an empty <c>/BC</c> are all "no border":
        /// Table 192 gives <c>/BC</c> no default, and a widget declares its border through it or
        /// not at all. A malformed component count (2, or 5 and up) answers the same way rather
        /// than black — the file has not said what colour it wants. Components are read resolved,
        /// since §7.3.10 lets any of them be indirect.</para>
        /// </remarks>
        internal static SKColor? WidgetBorderColor(PdfDictionary annot, PdfDocument doc, float borderWidth)
        {
            if (!(borderWidth > 0f)) return null;

            var bc = PdfResolve.Array(doc, PdfResolve.Dict(doc, annot?[PdfName.MK]), PdfName.BC);
            if (bc == null) return null;

            int n = bc.Count;
            if (n != PdfColorSpaceNames.GrayComponents
             && n != PdfColorSpaceNames.RgbComponents
             && n != PdfColorSpaceNames.CmykComponents) return null;

            var comps = new float[n];
            for (int i = 0; i < n; i++) comps[i] = PdfResolve.ArrayFloat(doc, bc, i);
            return PdfColor.ComponentsToColor(comps, PdfColor.DeviceSpaceForComponentCount(n));
        }

        // ------------------------------------------------------------------
        //  3D (§13.6)
        // ------------------------------------------------------------------

        /// <summary>The caption drawn in the middle of a 3D annotation's placeholder.</summary>
        internal const string ThreeDCaption = "3D";

        /// <summary>The placeholder's dash pattern, so "this is not real content" reads as such.</summary>
        internal static float[] ThreeDDashPattern() => new float[] { 4f, 2f };

        /// <summary>The soft blue a 3D placeholder falls back to, so it reads as "3D viewport".</summary>
        internal static SKColor ThreeDDefaultColor => new SKColor(0x30, 0x60, 0xB0);

        /// <summary>
        /// The placeholder outline colour: the annotation's <c>/C</c> when it set one, else
        /// <see cref="ThreeDDefaultColor"/>.
        /// </summary>
        internal static SKColor ThreeDOutlineColor(SKColor borderColorOverride)
            => borderColorOverride.Alpha > 0 && !UsesDefaultColor(borderColorOverride)
                ? borderColorOverride
                : ThreeDDefaultColor;

        /// <summary>Size and baseline of the "3D" caption.</summary>
        /// <returns><c>false</c> when the rect is too small to caption legibly.</returns>
        internal static bool ThreeDCaptionPlacement(
            float x, float y, float w, float h,
            out float fontSize, out float baselineX, out float baselineY)
        {
            baselineX = baselineY = 0f;
            fontSize = Math.Min(h, w) * 0.45f;
            if (fontSize < 4f) return false;

            float textWidth = fontSize * 0.6f * ThreeDCaption.Length;
            baselineX = x + (w - textWidth) * 0.5f;
            baselineY = y + (h - fontSize) * 0.5f + fontSize * 0.2f;
            return true;
        }
    }
}
