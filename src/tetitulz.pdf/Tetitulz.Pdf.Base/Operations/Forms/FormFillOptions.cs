// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Per-call policy for <see cref="PdfFormFiller"/>.
    /// </summary>
    public sealed class FormFillOptions
    {
        /// <summary>
        /// Standard-14 font used for a generated appearance when the field has no usable
        /// <c>/DA</c> (or when <see cref="HonorDefaultAppearance"/> is false). Default Helvetica.
        /// </summary>
        public string FontName { get; set; } = StandardFonts.Helvetica;

        /// <summary>
        /// Font size for generated appearances; <c>0</c> means auto-size to the widget height,
        /// shrinking further when the text would otherwise overflow the box. A <c>/DA</c> size
        /// wins over this unless <see cref="HonorDefaultAppearance"/> is false.
        /// </summary>
        public float FontSize { get; set; }

        /// <summary>
        /// Read the field's (inheritable) <c>/DA</c> default-appearance string for the font,
        /// size and fill colour, resolving the named font through the AcroForm's <c>/DR</c>
        /// resource dictionary. Default <c>true</c>. Set false to force <see cref="FontName"/> /
        /// <see cref="FontSize"/> on every field.
        /// </summary>
        public bool HonorDefaultAppearance { get; set; } = true;

        /// <summary>
        /// Generate <c>/AP</c> appearance streams. Default <c>true</c>. When false only the field
        /// values are written and <c>/NeedAppearances</c> is left true, deferring the drawing to
        /// the viewer.
        /// </summary>
        public bool GenerateAppearances { get; set; } = true;

        /// <summary>
        /// Supplies a font <b>program</b> (TrueType/OpenType bytes) for text that the field's own
        /// font cannot encode — anything outside Latin-1, e.g. Hebrew. Called with the base font
        /// name the field asks for; return <c>null</c> to decline.
        ///
        /// <para>Embedding redistributes the font, so this is the caller's decision to make: with
        /// no provider and <see cref="AllowSystemFontSubstitution"/> off, the value is still
        /// written correctly but no appearance is drawn and the omission is reported through
        /// <see cref="FormFillResult.Warnings"/> — never silently mis-drawn.</para>
        /// </summary>
        public Func<string, byte[]> FontProvider { get; set; }

        /// <summary>
        /// Allow falling back to a font installed on this machine (subject to
        /// <c>TETITULZ_PDF_FONTDIR</c>) when <see cref="FontProvider"/> declines or is unset.
        /// Default <c>false</c> — the same licensing-conservative default the accessibility
        /// font-embedding remediator applies.
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// Consider a host font whose <c>OS/2</c> <c>fsType</c> forbids embedding its outlines.
        /// Default <c>false</c>, so such a font is skipped and the search continues.
        /// </summary>
        /// <remarks>
        /// <para><b>Same name and same default as
        /// <see cref="Core.Fonts.FontResolutionRequest.AllowRestrictedSystemFonts"/></b>, whose own
        /// doc says a third spelling of one concept would be a documentation bug. It applies to the
        /// system search only, never to <see cref="FontProvider"/>: a caller who hands over a font
        /// program has made a licensing decision this library cannot see.</para>
        ///
        /// <para>It exists because the refusal a caller receives already told them to set it. When
        /// the gate skips every candidate, the reason that reaches
        /// <see cref="FormFillResult.Warnings"/> ends "set
        /// <c>FontResolutionRequest.AllowRestrictedSystemFonts</c> if this deployment holds a
        /// licence for them" — a property a form-filling caller reaches here. <b>An unactionable
        /// remedy is worse than none</b>: it reads as a supported opt-in
        /// and sends the caller looking for an API that is not there.</para>
        ///
        /// <para>Set it where the deployment owns the fonts it installed — the situation
        /// <c>TETITULZ_PDF_FONTDIR</c> exists for.</para>
        /// </remarks>
        public bool AllowRestrictedSystemFonts { get; set; }

        /// <summary>
        /// Paragraph direction for the value when generating an appearance. Defaults to
        /// <see cref="Core.Text.BaseDirection.Auto"/>, which takes it from the value's first strong
        /// character and reproduces the historical behaviour exactly.
        ///
        /// <para><b>Set this to <see cref="Core.Text.BaseDirection.RightToLeft"/> for a Hebrew or
        /// Arabic form.</b> Auto-detection is wrong for precisely the values a form field holds: a
        /// Hebrew field containing <c>"Tel Aviv 12"</c> or <c>"1,234 ₪"</c> begins with a Latin word
        /// or a digit, so P2/P3 call the paragraph left-to-right and the currency sign and trailing
        /// punctuation land on the wrong side. The direction is a property of the <i>field</i>, and
        /// no amount of inspecting the value recovers it — which is why it has to be supplied.</para>
        /// </summary>
        public Core.Text.BaseDirection BaseDirection { get; set; } = Core.Text.BaseDirection.Auto;

        /// <summary>
        /// Text to draw in a text or choice field's appearance in place of its value, by fully
        /// qualified name — what the field's Format script (§12.6.3 Table 196, <c>/F</c>) made of the
        /// value, as <see cref="PdfFieldScripts"/> returns it. The value itself is what is stored in
        /// <c>/V</c>: a field holding <c>1234.5</c> shows <c>$1,234.50</c>. A field not named here
        /// draws its value. Default null.
        /// </summary>
        public IDictionary<string, string> DisplayValues { get; set; }
    }

    /// <summary>
    /// What <see cref="PdfFormFiller.Fill(System.IO.Stream, System.IO.Stream,
    /// System.Collections.Generic.IDictionary{string,string}, FormFillOptions)"/> actually did.
    ///
    /// <para><b>Invariant:</b> a name in <see cref="FieldsWithoutAppearance"/> had its value
    /// written but not drawn, and <see cref="NeedAppearances"/> is then true so a viewer
    /// regenerates it.</para>
    /// </summary>
    public sealed class FormFillResult
    {
        internal readonly List<string> FilledList = new List<string>();
        internal readonly List<string> UnmatchedList = new List<string>();
        internal readonly List<string> NoAppearanceList = new List<string>();
        internal readonly List<string> WarningList = new List<string>();

        /// <summary>Fully-qualified names of the fields whose value was written.</summary>
        public IReadOnlyList<string> FilledFields => FilledList;

        /// <summary>Requested names that matched no field in the document.</summary>
        public IReadOnlyList<string> UnmatchedFields => UnmatchedList;

        /// <summary>
        /// Fields whose value was written but whose appearance could not be generated (no font
        /// covering the text, a widget with no <c>/Rect</c>, …). Each has a matching entry in
        /// <see cref="Warnings"/>.
        /// </summary>
        public IReadOnlyList<string> FieldsWithoutAppearance => NoAppearanceList;

        /// <summary>Non-fatal issues, one per tolerated failure.</summary>
        public IReadOnlyList<string> Warnings => WarningList;

        /// <summary>
        /// The value written to the AcroForm's <c>/NeedAppearances</c>. True when at least one
        /// appearance is missing, so viewers are asked to draw the values themselves.
        /// </summary>
        public bool NeedAppearances { get; internal set; }

        internal void Warn(string fieldName, string message)
        {
            WarningList.Add(fieldName == null ? message : fieldName + ": " + message);
        }
    }

    /// <summary>
    /// The objects a fill would write, and what it would report — computed without touching an
    /// output stream. See <see cref="PdfFormFiller.Plan"/> for why the two steps are separable.
    /// </summary>
    public sealed class FormFillPlan
    {
        internal FormFillPlan(
            IReadOnlyList<Core.Writing.IndirectObject> objects, FormFillResult result, int nextObjectNumber)
        {
            Objects = objects;
            Result = result;
            NextObjectNumber = nextObjectNumber;
        }

        /// <summary>The new and rewritten objects, ready to append as one revision.</summary>
        public IReadOnlyList<Core.Writing.IndirectObject> Objects { get; }

        /// <summary>What the fill matched, skipped and could not draw.</summary>
        public FormFillResult Result { get; }

        /// <summary>
        /// One past the highest object number the plan allocated — the value the appended
        /// revision's trailer <c>/Size</c> must be at least.
        /// </summary>
        public int NextObjectNumber { get; }
    }
}
