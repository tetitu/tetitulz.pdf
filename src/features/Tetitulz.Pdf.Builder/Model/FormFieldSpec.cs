// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Builder.Model
{
    /// <summary>The kinds of interactive field a <see cref="FormFieldSpec"/> can describe.</summary>
    /// <remarks>
    /// Exactly the kinds <c>Base.Operations.Forms.PdfFormFieldBuilder</c> can author, because that
    /// is where a placed field becomes a real one.
    /// </remarks>
    public enum FormFieldKind
    {
        /// <summary>A text field — single-line, multi-line or password.</summary>
        Text,
        /// <summary>A check box.</summary>
        Checkbox,
        /// <summary>A drop-down choice.</summary>
        ComboBox,
        /// <summary>A scrollable choice list.</summary>
        ListBox,
        /// <summary>
        /// One button of a radio group. Every run of the group carries the same
        /// <see cref="FormFieldSpec.Name"/> — the group's — its own
        /// <see cref="FormFieldSpec.Value"/> as the button's export value, and
        /// <see cref="FormFieldSpec.IsChecked"/> on at most one of them. The group is the field;
        /// each run places one of its widgets.
        /// </summary>
        RadioButton,
    }

    /// <summary>One option of a choice field: the value submitted, and the text shown.</summary>
    public sealed class FormFieldOption
    {
        /// <summary>Creates an option.</summary>
        public FormFieldOption(string value, string text)
        {
            Value = value ?? "";
            Text = text ?? "";
        }

        /// <summary>The value the field takes when this option is chosen.</summary>
        public string Value { get; }

        /// <summary>What the option displays.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// An interactive form field a <see cref="TextRun"/> stands for.
    /// </summary>
    /// <remarks>
    /// <para><b>The PDF exporter lays the run out and reports where it landed; it does not write the
    /// field.</b> <see cref="Export.PdfExporter.PlacedFormFields"/> lists each one with its page and
    /// rectangle, and <c>Base.Operations.Forms.PdfFormFieldBuilder.AddFields</c> turns the list into
    /// fields with appearances. Builder may reference only Core, and the field builder and its
    /// appearance generation are in Base — writing a second generator here to avoid the hop is the
    /// duplication this repository refuses.</para>
    ///
    /// <para>The run's own text is what the page draws. A producer that wants the field's
    /// appearance to be the only drawing of its value makes the text a placeholder — the HTML
    /// mapper uses no-break spaces, which reserve the width and cannot wrap.</para>
    /// </remarks>
    public sealed class FormFieldSpec
    {
        /// <summary>What kind of field this is.</summary>
        public FormFieldKind Kind { get; set; } = FormFieldKind.Text;

        /// <summary>
        /// The field's partial name (<c>/T</c>). Unique within the document — except that every
        /// button of one radio group carries the group's name, which is what makes them a group.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// The initial value: a text field's text, a choice field's selected option value, a radio
        /// button's export value.
        /// </summary>
        public string? Value { get; set; }

        /// <summary>A check box's or a radio button's initial state.</summary>
        public bool IsChecked { get; set; }

        /// <summary>A multi-line text field.</summary>
        public bool Multiline { get; set; }

        /// <summary>A password text field. Its value is never stored (ISO 32000-2 Table 229, bit 14).</summary>
        public bool Password { get; set; }

        /// <summary>The field cannot be edited.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>A list box that allows several selections.</summary>
        public bool MultiSelect { get; set; }

        /// <summary>A text field's maximum length; 0 for none.</summary>
        public int MaxLength { get; set; }

        /// <summary>A choice field's options, in display order.</summary>
        public IList<FormFieldOption> Options { get; } = new List<FormFieldOption>();
    }
}
