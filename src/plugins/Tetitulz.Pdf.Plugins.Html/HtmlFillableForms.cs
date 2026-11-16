// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.Html
{
    /// <summary>
    /// HTML to a <b>fillable</b> PDF: the form controls become AcroForm fields rather than drawings
    /// of them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this lives in a plugin.</b> It is two steps in two assemblies that cannot
    /// reference each other. The managed HTML layout (HtmlToPdf, over Builder) knows where each
    /// control landed but may reference only Core; the one field author,
    /// <see cref="PdfFormFieldBuilder"/>, and its appearance generation are in Base. This package
    /// references both — the sanctioned plugin-to-feature edge — so it is where the two meet.</para>
    ///
    /// <para><b>The default HTML conversion is unchanged</b> and still draws controls as graphics,
    /// which is what a browser prints; the two engines agree unless a caller asks for this.</para>
    /// </remarks>
    public static class HtmlFillableForms
    {
        /// <summary>
        /// Converts an HTML string with the managed engine and makes its form controls into fields,
        /// all added in one appended revision.
        /// </summary>
        /// <remarks>
        /// What is mapped, and what stays a drawing, is listed on
        /// <see cref="HtmlToPdfConverter.ConvertHtmlWithFormFields"/>; each control left static is
        /// reported through <see cref="HtmlToPdfOptions.OnDiagnostic"/>. A document with no
        /// mappable control comes back exactly as the managed engine wrote it, with no
        /// <c>/AcroForm</c>.
        /// </remarks>
        public static byte[] ConvertHtmlToFillablePdf(string html, HtmlToPdfOptions? options = null)
        {
            if (html == null) throw new ArgumentNullException(nameof(html));

            HtmlFormConversionResult laidOut = HtmlToPdfConverter.ConvertHtmlWithFormFields(html, options);
            if (laidOut.FormFields.Count == 0) return laidOut.Pdf;

            using var input = new MemoryStream(laidOut.Pdf, writable: false);
            using var output = new MemoryStream();
            PdfFormFieldBuilder.AddFields(input, output, ToFormFields(laidOut.FormFields));
            return output.ToArray();
        }

        /// <summary>
        /// The Base field descriptors for a conversion's placements: one per placement, except that
        /// the buttons of a radio group — every placement of kind
        /// <see cref="FormFieldKind.RadioButton"/> under one name — become <b>one</b>
        /// <see cref="FormRadioGroupField"/>, standing where the group's first button stood in
        /// document order.
        /// </summary>
        internal static IReadOnlyList<FormFieldBase> ToFormFields(IReadOnlyList<PlacedFormField> placements)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));

            var fields = new List<FormFieldBase>(placements.Count);
            var groups = new Dictionary<string, FormRadioGroupField>(StringComparer.Ordinal);
            foreach (PlacedFormField placed in placements)
            {
                FormFieldSpec spec = placed.Field;
                if (spec.Kind != FormFieldKind.RadioButton)
                {
                    fields.Add(ToFormField(placed));
                    continue;
                }

                if (!groups.TryGetValue(spec.Name, out FormRadioGroupField? group))
                {
                    group = new FormRadioGroupField { FieldName = spec.Name, ReadOnly = spec.ReadOnly };
                    groups[spec.Name] = group;
                    fields.Add(group);
                }
                // HTML can disable one radio; a PDF group is read-only or not as a whole. Only when
                // EVERY button is disabled is the group locked — locking the buttons a user could
                // still choose, because one of them could not be, is the worse of the two errors.
                group.ReadOnly &= spec.ReadOnly;
                group.Buttons.Add(new RadioButtonOption(spec.Value ?? "on", placed.PageIndex,
                                                        placed.X, placed.Y, placed.Width, placed.Height));
                if (spec.IsChecked) group.SelectedValue = spec.Value;
            }
            return fields;
        }

        /// <summary>
        /// The Base field descriptor for one placed field: the same name, rectangle and state,
        /// with the appearance drawn at the size the placeholder was laid out at. A radio button is
        /// not a field on its own — <see cref="ToFormFields"/> gathers a group's buttons.
        /// </summary>
        internal static FormFieldBase ToFormField(PlacedFormField placed)
        {
            if (placed == null) throw new ArgumentNullException(nameof(placed));
            FormFieldSpec spec = placed.Field;
            if (spec.Kind == FormFieldKind.RadioButton)
                throw new ArgumentException("A radio button is one widget of a group; map the placements through ToFormFields.",
                                            nameof(placed));

            FormFieldBase field;
            switch (spec.Kind)
            {
                case FormFieldKind.Checkbox:
                    field = new FormCheckboxField { IsChecked = spec.IsChecked };
                    break;

                case FormFieldKind.ComboBox:
                case FormFieldKind.ListBox:
                {
                    FormChoiceFieldBase choice = spec.Kind == FormFieldKind.ComboBox
                        ? new FormComboField()
                        : new FormListField();
                    foreach (FormFieldOption option in spec.Options)
                        // An export value equal to the display text is left out: ChoiceOption
                        // documents null as "the same as the display text", and writing both makes
                        // every option a pair for nothing.
                        choice.Options.Add(new ChoiceOption(
                            option.Text,
                            string.Equals(option.Value, option.Text, StringComparison.Ordinal) ? null : option.Value));
                    choice.DefaultValue = spec.Value;
                    choice.MultiSelect = spec.MultiSelect;
                    choice.FontSize = placed.FontSize;
                    field = choice;
                    break;
                }

                default:
                    field = new FormTextField
                    {
                        Multiline = spec.Multiline,
                        Password = spec.Password,
                        MaxLength = spec.MaxLength,
                        // Forwarded as it is, password or not. The mapper gives a password field no
                        // value (ISO 32000-2 Table 229), and the field builder REFUSES one — so a
                        // mapper that regresses fails this call loudly. Blanking it here as well would
                        // turn that loud failure into a silently dropped value.
                        DefaultValue = spec.Value,
                        FontSize = placed.FontSize,
                    };
                    break;
            }

            field.FieldName = spec.Name;
            field.ReadOnly = spec.ReadOnly;
            field.PageIndex = placed.PageIndex;
            field.X = placed.X;
            field.Y = placed.Y;
            field.Width = placed.Width;
            field.Height = placed.Height;
            return field;
        }
    }
}
