// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Creates new interactive form fields (AcroForm) in a PDF.
    ///
    /// <para>
    /// Supported field types:
    /// <list type="bullet">
    ///   <item>Text field (single-line or multi-line)</item>
    ///   <item>Checkbox</item>
    ///   <item>Combo box (drop-down list)</item>
    ///   <item>List box (scrollable list)</item>
    ///   <item>Radio button group — one field, one widget per button (<see cref="FormRadioGroupField"/>)</item>
    /// </list>
    /// Push buttons are <b>not</b> built here: they hold no value, and an action is what makes one
    /// mean anything. This list once named them and radio groups both, with no method that could
    /// add either.
    /// </para>
    ///
    /// <para>
    /// <b>A field whose appearance is incomplete sets <c>/NeedAppearances</c>.</b> A combo or list box
    /// gets no <c>/AP</c> at all, and a text field's default value is drawn only when every character
    /// fits the single-byte encoding the appearance font uses. Either way the viewer is asked to
    /// draw it — on an existing form as well as a new one, or a list box added to an existing form
    /// would show nothing and a Hebrew default value be drawn as question marks.
    /// </para>
    ///
    /// <para>
    /// Each field is represented as an annotation widget on a page. The widget is added
    /// to the page /Annots array and the AcroForm /Fields array in the catalog.
    /// </para>
    /// </summary>
    public static class PdfFormFieldBuilder
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Adds a text input field to the document.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="field"/> is a password field with a default value. ISO 32000-2 Table 229
        /// bit 14 says a processor must never store a password field's value in the file.
        /// </exception>
        public static void AddTextField(Stream input, Stream output, FormTextField field)
            => AddFields(input, output, new FormFieldBase[] { field ?? throw new ArgumentNullException(nameof(field)) });

        /// <summary>Adds a checkbox field to the document.</summary>
        public static void AddCheckbox(Stream input, Stream output, FormCheckboxField field)
            => AddFields(input, output, new FormFieldBase[] { field ?? throw new ArgumentNullException(nameof(field)) });

        /// <summary>Adds a combo box (drop-down) field to the document.</summary>
        public static void AddComboBox(Stream input, Stream output, FormComboField field)
            => AddFields(input, output, new FormFieldBase[] { field ?? throw new ArgumentNullException(nameof(field)) });

        /// <summary>Adds a list box (scrollable list) field to the document.</summary>
        public static void AddListBox(Stream input, Stream output, FormListField field)
            => AddFields(input, output, new FormFieldBase[] { field ?? throw new ArgumentNullException(nameof(field)) });

        /// <summary>
        /// Adds several fields to the document in <b>one</b> appended revision.
        /// </summary>
        /// <remarks>
        /// <para><b>One revision, not one per field.</b> The single-field methods each append a
        /// revision, so a form of fifty fields built through them carries fifty cross-reference
        /// sections — and a document that is signed afterwards, then compared revision by
        /// revision, reads as fifty separate edits. Every field here shares one page update per
        /// page touched, one <c>/AcroForm</c> update, and one set of appearance fonts.</para>
        ///
        /// <para><b>Everything is checked before anything is written</b>: a page index out of
        /// range, a password field with a default value, or two fields in the batch sharing a
        /// name refuses the whole call. Two terminal fields under one fully qualified name are
        /// not two fields — §12.7.4.2 makes them one field, which is not what either caller
        /// asked for. Names already in the document are not checked, as for the single-field
        /// methods.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">A field is invalid, or two share a name.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A field names a page the document does not have.</exception>
        public static void AddFields(Stream input, Stream output, IReadOnlyList<FormFieldBase> fields)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (fields == null) throw new ArgumentNullException(nameof(fields));

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (field == null) throw new ArgumentException("A field in the batch is null.", nameof(fields));
                if (field is FormTextField tf && tf.Password && !string.IsNullOrEmpty(tf.DefaultValue))
                    throw new ArgumentException(
                        "A password field cannot carry a default value: ISO 32000-2 Table 229 says a PDF " +
                        "processor must never store a password field's value in the file.", nameof(fields));
                if (!string.IsNullOrEmpty(field.FieldName) && !names.Add(field.FieldName))
                    throw new ArgumentException(
                        $"Two fields in the batch are named '{field.FieldName}'; §12.7.4.2 would read them " +
                        "as one field.", nameof(fields));
                if (field is FormRadioGroupField group) ValidateRadioGroup(group);
                RequireFiniteNumbers(field);
            }

            AddFieldsCore(input, output, fields);
        }

        /// <summary>
        /// A field's geometry and font size refused for NaN and ±∞, a size below 0 too
        /// (<c>NumericArguments</c>): a NaN width reached the appearance stream as a literal
        /// <c>NaN</c> operand, a NaN font size the <c>/DA</c> as <c>NaN Tf</c>, and the call returned.
        /// </summary>
        private static void RequireFiniteNumbers(FormFieldBase field)
        {
            if (field is FormRadioGroupField group)
            {
                foreach (var button in group.Buttons)
                {
                    NumericArguments.RequireFinite(button.X, "fields", nameof(RadioButtonOption.X));
                    NumericArguments.RequireFinite(button.Y, "fields", nameof(RadioButtonOption.Y));
                    NumericArguments.RequireNonNegative(button.Width, "fields", nameof(RadioButtonOption.Width));
                    NumericArguments.RequireNonNegative(button.Height, "fields", nameof(RadioButtonOption.Height));
                }
                return;
            }

            NumericArguments.RequireFinite(field.X, "fields", nameof(FormFieldBase.X));
            NumericArguments.RequireFinite(field.Y, "fields", nameof(FormFieldBase.Y));
            NumericArguments.RequireNonNegative(field.Width, "fields", nameof(FormFieldBase.Width));
            NumericArguments.RequireNonNegative(field.Height, "fields", nameof(FormFieldBase.Height));
            if (field is FormTextField text)
                NumericArguments.RequireNonNegative(text.FontSize, "fields", nameof(FormTextField.FontSize));
            else if (field is FormChoiceFieldBase choice)
                NumericArguments.RequireNonNegative(choice.FontSize, "fields", nameof(FormChoiceFieldBase.FontSize));
        }

        /// <summary>Adds a radio button group — one field, one widget per button — to the document.</summary>
        public static void AddRadioGroup(Stream input, Stream output, FormRadioGroupField field)
            => AddFields(input, output, new FormFieldBase[] { field ?? throw new ArgumentNullException(nameof(field)) });

        /// <summary>
        /// A group the writer could not represent faithfully is refused before anything is written.
        /// </summary>
        /// <remarks>
        /// <b>Each button's export value is its on-state name, so the values must be distinct and none
        /// may be <c>Off</c></b> — §12.7.4.2.3 reserves that name for the unselected state. Two buttons
        /// sharing a value would share a state, so choosing either chooses both: the RadiosInUnison
        /// behaviour, which the caller did not ask for.
        /// </remarks>
        private static void ValidateRadioGroup(FormRadioGroupField group)
        {
            if (group.Buttons == null || group.Buttons.Count == 0)
                throw new ArgumentException($"The radio group '{group.FieldName}' has no buttons.", "fields");

            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var button in group.Buttons)
            {
                if (button == null || string.IsNullOrEmpty(button.ExportValue))
                    throw new ArgumentException(
                        $"A button of the radio group '{group.FieldName}' has no export value.", "fields");
                if (button.ExportValue == PdfName.Off.Value)
                    throw new ArgumentException(
                        $"A button of the radio group '{group.FieldName}' has the export value 'Off', which " +
                        "§12.7.4.2.3 reserves for the unselected state.", "fields");
                if (!values.Add(button.ExportValue))
                    throw new ArgumentException(
                        $"Two buttons of the radio group '{group.FieldName}' share the export value " +
                        $"'{button.ExportValue}', so choosing one would choose both.", "fields");
            }

            if (group.SelectedValue != null && !values.Contains(group.SelectedValue))
                throw new ArgumentException(
                    $"The radio group '{group.FieldName}' selects '{group.SelectedValue}', which none of its " +
                    "buttons offers.", "fields");
        }

        // ----------------------------------------------------------------
        //  Core logic
        // ----------------------------------------------------------------

        private static void AddFieldsCore(Stream input, Stream output, IReadOnlyList<FormFieldBase> fields)
        {
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "add form fields to", "the fields and their appearances");

            foreach (var field in fields)
            {
                // A radio group has no rectangle of its own; its buttons are what sit on pages.
                IEnumerable<int> pages = field is FormRadioGroupField g
                    ? g.Buttons.ConvertAll(b => b.PageIndex)
                    : new[] { field.PageIndex };
                foreach (int page in pages)
                    if (page < 0 || page >= doc.Pages.Count)
                        throw new ArgumentOutOfRangeException(nameof(FormFieldBase.PageIndex),
                            $"Field '{field.FieldName}' names page {page}; the document has {doc.Pages.Count}.");
            }

            int maxObj = doc.CrossReferenceTable.GetMaxObjectNumber();
            int nextObjNum = maxObj + 1;
            var objects = new List<IndirectObject>();

            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the new fields");

            // One appearance-font set for the whole batch, so the fields that name the same font
            // share one font object — and the /DR merged below names that same object.
            var fonts = new AppearanceFonts();
            var widgetRefs = new List<PdfReference>();
            // Each touched page's /Annots, seeded from the page's own on first touch, in the order
            // pages were first touched so the revision's object order does not depend on hashing.
            var annotsByPage = new Dictionary<int, PdfArray>();
            var pageOrder = new List<int>();

            PdfArray AnnotsOf(int pageIndex)
            {
                if (annotsByPage.TryGetValue(pageIndex, out var annots)) return annots;
                annots = new PdfArray();
                var existingAnnotsObj = doc.Pages[pageIndex].Dictionary[PdfName.Annots];
                if (existingAnnotsObj != null)
                {
                    // An unreadable /Annots is refused, as an unreadable /AcroForm is below: the
                    // array written for the page replaces it, holding only the new widgets, and every
                    // link, comment and widget the page had left the current revision.
                    var existingAnnots = EntryRead.Resolve(doc.ResolveObject, existingAnnotsObj, out string unreadableAnnots) as PdfArray;
                    if (unreadableAnnots != null)
                        throw new Core.PdfException(
                            "Page " + (pageIndex + 1) + "'s /Annots could not be read (" + unreadableAnnots +
                            "), so adding a field there would replace every annotation on the page. No changes were written.");
                    if (existingAnnots != null)
                        for (int i = 0; i < existingAnnots.Count; i++)
                            annots.Add(existingAnnots[i]);
                }
                annotsByPage[pageIndex] = annots;
                pageOrder.Add(pageIndex);
                return annots;
            }

            foreach (var field in fields)
            {
                if (field is FormRadioGroupField group)
                {
                    widgetRefs.Add(BuildRadioGroup(group, doc, ref nextObjNum, objects, AnnotsOf));
                    continue;
                }

                // ---- Widget annotation dictionary ----
                // The terminal field = widget pattern: the widget dictionary also carries the
                // field's own entries (§12.7.4.1's merged form).
                int widgetObjNum = nextObjNum++;
                var widgetDict = BuildWidgetDict(field, doc, ref nextObjNum, objects, fonts);
                widgetDict.ObjectNumber = widgetObjNum;
                widgetDict.GenerationNumber = 0;
                objects.Add(new IndirectObject(widgetObjNum, 0, widgetDict));
                widgetRefs.Add(new PdfReference(widgetObjNum, 0));
                AnnotsOf(field.PageIndex).Add(new PdfReference(widgetObjNum, 0));
            }

            // ---- Each touched page, once, with every widget added to it ----
            foreach (int pageIndex in pageOrder)
            {
                var page = doc.Pages[pageIndex];
                var updatedPage = PdfPageContentAppender.CloneDictionary(page.Dictionary);
                updatedPage.Set(PdfName.Annots, annotsByPage[pageIndex]);
                updatedPage.ObjectNumber = page.Dictionary.ObjectNumber;
                updatedPage.GenerationNumber = 0;
                objects.Add(new IndirectObject(page.Dictionary.ObjectNumber, 0, updatedPage));
            }

            // ---- Update AcroForm in catalog ----
            var catalog = doc.Catalog.Dictionary;
            var updatedCatalog = PdfPageContentAppender.CloneDictionary(catalog);

            PdfDictionary acroForm = null;
            int acroFormObjNum = 0;
            var acroFormObj = catalog[PdfName.AcroForm];
            if (acroFormObj is PdfReference acroRef)
            {
                acroFormObjNum = acroRef.ReferenceObjectNumber;
                // An unreadable form is refused, not rebuilt: the form written below would replace it
                // with one holding only the new fields (Core.Document.EntryRead).
                acroForm = EntryRead.Resolve(doc.ResolveObject, acroFormObj, out string unreadableForm) as PdfDictionary;
                if (unreadableForm != null)
                    throw new Core.PdfException(
                        "The document's /AcroForm could not be read (" + unreadableForm + "), so adding " +
                        "fields would replace the whole form. No changes were written.");
            }
            else if (acroFormObj is PdfDictionary acroInline)
            {
                acroForm = acroInline;
            }

            PdfDictionary updatedAcroForm;
            if (acroForm != null)
                updatedAcroForm = PdfPageContentAppender.CloneDictionary(acroForm);
            else
            {
                updatedAcroForm = new PdfDictionary();
                updatedAcroForm.Set(PdfName.NeedAppearances, new PdfBoolean(true));
            }

            // Append to /Fields array.
            var fieldsArr = new PdfArray();
            // Resolved: the array built here REPLACES /Fields below, so reading an indirect one as
            // absent would drop every field the form already has - the same shape the Signing
            // writers guard (EntryRead).
            var existingFields = EntryRead.Resolve(doc.ResolveObject, updatedAcroForm[PdfName.Fields], out string unreadableFields) as PdfArray;
            if (unreadableFields != null)
                throw new Core.PdfException(
                    "The form's /Fields array could not be read (" + unreadableFields + "), so adding " +
                    "fields would replace every field the form has. No changes were written.");
            if (existingFields != null)
                for (int i = 0; i < existingFields.Count; i++)
                    fieldsArr.Add(existingFields[i]);
            foreach (var widgetRef in widgetRefs) fieldsArr.Add(widgetRef);
            updatedAcroForm.Set(PdfName.Fields, fieldsArr);

            // A field whose own appearance is incomplete needs the viewer to draw it — whether or
            // not this call created the form. See the type remarks. One such field is enough.
            foreach (var field in fields)
                if (!AppearanceIsComplete(field))
                {
                    updatedAcroForm.Set(PdfName.NeedAppearances, new PdfBoolean(true));
                    break;
                }

            // The /DR the generated appearances depend on. Merged rather than set, so a form that
            // already defines these names keeps its own meaning for them.
            fonts.MergeIntoDefaultResources(updatedAcroForm, doc);

            if (acroFormObjNum > 0)
            {
                updatedAcroForm.ObjectNumber = acroFormObjNum;
                updatedAcroForm.GenerationNumber = 0;
                objects.Add(new IndirectObject(acroFormObjNum, 0, updatedAcroForm));
                updatedCatalog.Set(PdfName.AcroForm, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, acroFormObjNum));
            }
            else
            {
                // Embed inline or create new indirect.
                int newAcroNum = nextObjNum++;
                updatedAcroForm.ObjectNumber = newAcroNum;
                updatedAcroForm.GenerationNumber = 0;
                objects.Add(new IndirectObject(newAcroNum, 0, updatedAcroForm));
                updatedCatalog.Set(PdfName.AcroForm, new PdfReference(newAcroNum, 0));
            }

            updatedCatalog.ObjectNumber = catalogObjNum;
            updatedCatalog.GenerationNumber = 0;
            objects.Add(new IndirectObject(catalogObjNum, 0, updatedCatalog));

            int newSize = nextObjNum;
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, newSize);
        }

        /// <summary>
        /// Whether the appearance this builder writes shows everything the field holds: false for a
        /// choice field, which gets no <c>/AP</c>, and for a text field whose default value the
        /// appearance font cannot encode, or is laid out on one line where the field is multiline
        /// or the value holds a line break — the appearance draws one <c>Tj</c>, so the viewer is
        /// asked to draw the lines.
        /// </summary>
        private static bool AppearanceIsComplete(FormFieldBase field) => field switch
        {
            FormChoiceFieldBase _ => false,
            FormTextField tf => string.IsNullOrEmpty(tf.DefaultValue)
                                || (FormAppearance.IsSingleByteEncodable(tf.DefaultValue)
                                    && !tf.Multiline
                                    && tf.DefaultValue.IndexOf('\n') < 0 && tf.DefaultValue.IndexOf('\r') < 0),
            _ => true,
        };

        // ----------------------------------------------------------------
        //  Widget dict builder (field + annotation merged)
        // ----------------------------------------------------------------

        private static PdfDictionary BuildWidgetDict(
            FormFieldBase field,
            PdfDocument doc,
            ref int nextObjNum,
            List<IndirectObject> objects,
            AppearanceFonts fonts)
        {
            var d = new PdfDictionary();

            // Annotation properties.
            d.Set(PdfName.Type, PdfName.Annot);
            d.Set(PdfName.Subtype, PdfName.Widget);
            d.Set(PdfName.T, new PdfString(field.FieldName));   // partial field name
            d.Set(PdfName.F, new PdfNumber(PdfAnnotationFlags.Print));

            if (!string.IsNullOrEmpty(field.AlternateFieldName))
                d.Set(PdfName.TU, new PdfString(field.AlternateFieldName));

            // Rect.
            var rect = new PdfArray();
            rect.Add(new PdfNumber(field.X));
            rect.Add(new PdfNumber(field.Y));
            rect.Add(new PdfNumber(field.X + field.Width));
            rect.Add(new PdfNumber(field.Y + field.Height));
            d.Set(PdfName.Rect, rect);

            // Page ref (P entry in widget).
            var page = doc.Pages[field.PageIndex];
            if (page.Dictionary.ObjectNumber > 0)
                d.Set(PdfName.P, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, page.Dictionary.ObjectNumber));

            // Field-type-specific properties.
            switch (field)
            {
                case FormTextField tf:
                    BuildTextField(d, tf, ref nextObjNum, objects, fonts);
                    break;
                case FormCheckboxField cb:
                    BuildCheckboxField(d, cb, ref nextObjNum, objects, fonts);
                    break;
                case FormComboField cf:
                    BuildChoiceField(d, cf, true);
                    break;
                case FormListField lf:
                    BuildChoiceField(d, lf, false);
                    break;
            }

            return d;
        }

        private static void BuildTextField(PdfDictionary d, FormTextField tf,
            ref int nextObjNum, List<IndirectObject> objects, AppearanceFonts fonts)
        {
            d.Set(PdfName.FT, new PdfName(PdfFieldTypes.Text));

            int ff = 0;
            if (tf.Multiline) ff |= PdfFieldFlags.Multiline;
            if (tf.Password)  ff |= PdfFieldFlags.Password;
            if (tf.ReadOnly)  ff |= PdfFieldFlags.ReadOnly;
            if (ff != 0) d.Set(PdfName.Ff, new PdfNumber(ff));

            if (!string.IsNullOrEmpty(tf.DefaultValue))
                d.Set(PdfName.V, new PdfString(tf.DefaultValue));
            if (tf.MaxLength > 0)
                d.Set(PdfName.MaxLen, new PdfNumber(tf.MaxLength));

            // Default appearance string.
            string da = BuildDA(tf.FontName, tf.FontSize);
            d.Set(PdfName.DA, new PdfString(da));

            // Build a simple appearance stream for the normal state. The font the content names is
            // defined on the stream AND on the form's /DR, so the appearance stands on its own
            // rather than on /NeedAppearances.
            string fontResource = tf.FontName ?? StandardFonts.HelveticaAbbrev;
            fonts.Require(fontResource, ref nextObjNum, objects);

            var apStream = BuildTextFieldAP(tf, fonts.Resources());
            int apObjNum = nextObjNum++;
            apStream.ObjectNumber = apObjNum;
            apStream.GenerationNumber = 0;
            objects.Add(new IndirectObject(apObjNum, 0, apStream));

            var apN = new PdfDictionary();
            apN.Set(PdfName.N, new PdfReference(apObjNum, 0));
            d.Set(PdfName.AP, apN);
        }

        private static void BuildCheckboxField(PdfDictionary d, FormCheckboxField cb,
            ref int nextObjNum, List<IndirectObject> objects, AppearanceFonts fonts)
        {
            d.Set(PdfName.FT, new PdfName(PdfFieldTypes.Button));

            if (cb.ReadOnly)
                d.Set(PdfName.Ff, new PdfNumber(PdfFieldFlags.ReadOnly));

            // Value: /Yes or /Off.
            d.Set(PdfName.V, cb.IsChecked ? PdfName.Yes : PdfName.Off);
            d.Set(PdfName.DV, PdfName.Off);

            // Appearance streams: /Yes (checked) and /Off (unchecked). Both draw through /ZaDb,
            // which the content names and which /DR and /Resources now define.
            fonts.Require(StandardFonts.ZapfDingbatsAbbrev, ref nextObjNum, objects);

            var apYes = BuildCheckboxAP(cb, true, fonts.Resources());
            int apYesNum = nextObjNum++;
            apYes.ObjectNumber = apYesNum;
            apYes.GenerationNumber = 0;
            objects.Add(new IndirectObject(apYesNum, 0, apYes));

            var apOff = BuildCheckboxAP(cb, false, fonts.Resources());
            int apOffNum = nextObjNum++;
            apOff.ObjectNumber = apOffNum;
            apOff.GenerationNumber = 0;
            objects.Add(new IndirectObject(apOffNum, 0, apOff));

            // The state subdictionary hangs off /N, not off /AP itself. Table 170 makes /N
            // REQUIRED and its own example is this exact case — "/AP <</N <</On formXObject1 ..."
            // for a check box — so writing /AP << /Yes ... /Off ... >> leaves a conforming reader
            // with no normal appearance at all. It looked harmless only because a newly created
            // form carries /NeedAppearances true, which asks the processor to regenerate and so
            // masks the missing entry; a consumer that honours the supplied appearance, this
            // library's own renderers included, finds nothing to draw.
            var states = new PdfDictionary();
            states.Set(PdfName.Yes, new PdfReference(apYesNum, 0));
            states.Set(PdfName.Off, new PdfReference(apOffNum, 0));

            var apN = new PdfDictionary();
            apN.Set(PdfName.N, states);
            d.Set(PdfName.AP, apN);
            d.Set(PdfName.AS, cb.IsChecked ? PdfName.Yes : PdfName.Off);
        }

        private static void BuildChoiceField(PdfDictionary d, FormChoiceFieldBase cf, bool isCombo)
        {
            d.Set(PdfName.FT, new PdfName(PdfFieldTypes.Choice));

            int ff = isCombo ? PdfFieldFlags.Combo : 0;
            if (cf.MultiSelect && !isCombo) ff |= PdfFieldFlags.MultiSelect;
            if (ff != 0) d.Set(PdfName.Ff, new PdfNumber(ff));

            // /Opt array: [(display value) ...]  or  [(export value) (display value)]
            var optArr = new PdfArray();
            foreach (var opt in cf.Options)
            {
                if (opt.ExportValue != null)
                {
                    var pair = new PdfArray();
                    pair.Add(new PdfString(opt.ExportValue));
                    pair.Add(new PdfString(opt.DisplayText));
                    optArr.Add(pair);
                }
                else
                {
                    optArr.Add(new PdfString(opt.DisplayText));
                }
            }
            d.Set(PdfName.Opt, optArr);

            if (!string.IsNullOrEmpty(cf.DefaultValue))
                d.Set(PdfName.V, new PdfString(cf.DefaultValue));

            string da = BuildDA(cf.FontName, cf.FontSize);
            d.Set(PdfName.DA, new PdfString(da));
        }

        /// <summary>
        /// A radio group as §12.7.4.2.4 lays it out: a parent field carrying the value and the
        /// flags, and one widget per button in its <c>/Kids</c>, each with its own on state and
        /// <c>/Off</c>. Returns the parent, which is what the form's <c>/Fields</c> names.
        /// </summary>
        private static PdfReference BuildRadioGroup(FormRadioGroupField group, PdfDocument doc,
            ref int nextObjNum, List<IndirectObject> objects, Func<int, PdfArray> annotsOf)
        {
            int parentNum = nextObjNum++;
            var parentRef = new PdfReference(parentNum, 0);

            var parent = new PdfDictionary();
            parent.Set(PdfName.FT, new PdfName(PdfFieldTypes.Button));
            int ff = PdfFieldFlags.Radio | PdfFieldFlags.NoToggleToOff;
            if (group.ReadOnly) ff |= PdfFieldFlags.ReadOnly;
            parent.Set(PdfName.Ff, new PdfNumber(ff));
            parent.Set(PdfName.T, new PdfString(group.FieldName));
            if (!string.IsNullOrEmpty(group.AlternateFieldName))
                parent.Set(PdfName.TU, new PdfString(group.AlternateFieldName));
            parent.Set(PdfName.V, group.SelectedValue != null ? new PdfName(group.SelectedValue) : PdfName.Off);

            var kids = new PdfArray();
            foreach (var button in group.Buttons)
            {
                var onState = new PdfName(button.ExportValue);

                int onNum = nextObjNum++;
                var onAp = BuildRadioAP(button.Width, button.Height, on: true);
                onAp.ObjectNumber = onNum;
                onAp.GenerationNumber = 0;
                objects.Add(new IndirectObject(onNum, 0, onAp));

                int offNum = nextObjNum++;
                var offAp = BuildRadioAP(button.Width, button.Height, on: false);
                offAp.ObjectNumber = offNum;
                offAp.GenerationNumber = 0;
                objects.Add(new IndirectObject(offNum, 0, offAp));

                // The states hang off /N — Table 170 makes /N required, and a state dictionary
                // straight under /AP leaves a conforming reader with no normal appearance.
                var states = new PdfDictionary();
                states.Set(onState, new PdfReference(onNum, 0));
                states.Set(PdfName.Off, new PdfReference(offNum, 0));
                var ap = new PdfDictionary();
                ap.Set(PdfName.N, states);

                int kidNum = nextObjNum++;
                var kid = new PdfDictionary();
                kid.Set(PdfName.Type, PdfName.Annot);
                kid.Set(PdfName.Subtype, PdfName.Widget);
                kid.Set(PdfName.Parent, parentRef);
                kid.Set(PdfName.F, new PdfNumber(PdfAnnotationFlags.Print));
                kid.Set(PdfName.Rect, RectArray(button.X, button.Y, button.Width, button.Height));
                var page = doc.Pages[button.PageIndex];
                if (page.Dictionary.ObjectNumber > 0)
                    kid.Set(PdfName.P, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, page.Dictionary.ObjectNumber));
                kid.Set(PdfName.AP, ap);
                kid.Set(PdfName.AS, button.ExportValue == group.SelectedValue ? onState : PdfName.Off);
                kid.ObjectNumber = kidNum;
                kid.GenerationNumber = 0;
                objects.Add(new IndirectObject(kidNum, 0, kid));

                kids.Add(new PdfReference(kidNum, 0));
                annotsOf(button.PageIndex).Add(new PdfReference(kidNum, 0));
            }

            parent.Set(PdfName.Kids, kids);
            parent.ObjectNumber = parentNum;
            parent.GenerationNumber = 0;
            objects.Add(new IndirectObject(parentNum, 0, parent));
            return parentRef;
        }

        private static PdfArray RectArray(double x, double y, double w, double h)
        {
            var rect = new PdfArray();
            rect.Add(new PdfNumber(x));
            rect.Add(new PdfNumber(y));
            rect.Add(new PdfNumber(x + w));
            rect.Add(new PdfNumber(y + h));
            return rect;
        }

        // ----------------------------------------------------------------
        //  Appearance stream builders
        // ----------------------------------------------------------------

        /// <summary>
        /// A radio button's appearance: a white disc with a thin border, and — when
        /// <paramref name="on"/> — a filled dot. Drawn with paths rather than a ZapfDingbats glyph,
        /// so it names no font and needs no resources.
        /// </summary>
        private static PdfStream BuildRadioAP(double w, double h, bool on)
        {
            double cx = w / 2, cy = h / 2, r = Math.Max(0.5, Math.Min(w, h) / 2 - 0.5);
            var sb = new StringBuilder();
            sb.Append("q\n");
            sb.Append("1 g\n").Append(CirclePath(cx, cy, r)).Append("f\n");
            sb.Append("0 g 0 G 0.5 w\n").Append(CirclePath(cx, cy, r)).Append("S\n");
            if (on) sb.Append(CirclePath(cx, cy, r * 0.45)).Append("f\n");
            sb.Append("Q\n");
            return FormStream(sb.ToString(), w, h, null);
        }

        /// <summary>A closed circle of four cubic Béziers — control points at 4/3·(√2−1)·r.</summary>
        private static string CirclePath(double cx, double cy, double r)
        {
            const double K = 0.5522847498307936;
            double c = r * K;
            return InvFmt("{0:F4} {1:F4} m\n", cx + r, cy)
                 + InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} c\n", cx + r, cy + c, cx + c, cy + r, cx, cy + r)
                 + InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} c\n", cx - c, cy + r, cx - r, cy + c, cx - r, cy)
                 + InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} c\n", cx - r, cy - c, cx - c, cy - r, cx, cy - r)
                 + InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} c\n", cx + c, cy - r, cx + r, cy - c, cx + r, cy)
                 + "h\n";
        }

        private static PdfStream BuildTextFieldAP(FormTextField tf, PdfDictionary resources)
        {
            double w = tf.Width;
            double h = tf.Height;
            double fs = tf.FontSize > 0 ? tf.FontSize : 10;
            double textY = (h - fs) / 2.0 + 1;

            var sb = new StringBuilder();
            sb.Append("/Tx BMC\n");
            sb.Append("q\n");
            // Border & background.
            sb.Append(InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} re W n\n", 0, 0, w, h));
            sb.Append("0.9 0.9 0.9 rg\n");
            sb.Append(InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} re f\n", 1, 1, w - 2, h - 2));
            sb.Append("0 g\n");
            sb.Append(InvFmt("0.5 w {0:F4} {1:F4} {2:F4} {3:F4} re S\n", 0.5, 0.5, w - 1, h - 1));
            // Text — only when every character fits the single-byte encoding the font is written
            // with (the rule FormAppearance applies). Anything else was Latin-1 encoded into '?'s;
            // it is now left out and /NeedAppearances asks the viewer to draw it.
            if (!string.IsNullOrEmpty(tf.DefaultValue) && FormAppearance.IsSingleByteEncodable(tf.DefaultValue))
            {
                sb.Append("BT\n");
                sb.Append(InvFmt("/{0} {1:F4} Tf\n", Core.Objects.PdfNameEscape.Escape(tf.FontName ?? StandardFonts.HelveticaAbbrev), fs));
                sb.Append("0 g\n");
                sb.Append(InvFmt("{0:F4} {1:F4} Td\n", 4.0, textY));
                sb.AppendFormat("({0}) Tj\n", EscapePdf(tf.DefaultValue));
                sb.Append("ET\n");
            }
            sb.Append("Q\n");
            sb.Append("EMC\n");

            return FormStream(sb.ToString(), w, h, resources);
        }

        private static PdfStream BuildCheckboxAP(FormCheckboxField cb, bool checked_, PdfDictionary resources)
        {
            double w = cb.Width;
            double h = cb.Height;
            var sb = new StringBuilder();
            sb.Append("q\n");
            // Background.
            sb.Append("1 g\n");
            sb.Append(InvFmt("{0:F4} {1:F4} {2:F4} {3:F4} re f\n", 0, 0, w, h));
            // Border.
            sb.Append("0 g\n");
            sb.Append(InvFmt("0.5 w {0:F4} {1:F4} {2:F4} {3:F4} re S\n", 0.5, 0.5, w - 1, h - 1));
            if (checked_)
            {
                // Draw a checkmark glyph via the ZapfDingbats font (✓ = 0x34 in Zapf).
                double fs = Math.Min(w, h) * 0.7;
                double tx = (w - fs * 0.5) / 2.0;
                double ty = (h - fs) / 2.0;
                sb.Append("BT\n");
                sb.Append(InvFmt("/ZaDb {0:F4} Tf\n", fs));
                sb.Append("0 g\n");
                sb.Append(InvFmt("{0:F4} {1:F4} Td\n", tx, ty));
                sb.Append("(4) Tj\n");  // ZapfDingbats checkmark
                sb.Append("ET\n");
            }
            sb.Append("Q\n");

            return FormStream(sb.ToString(), w, h, resources);
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// The generated appearance as a form XObject: the content Latin-1 encoded, a
        /// <c>/FormType 1</c> form, and a <c>/BBox</c> anchored at the origin and sized to the
        /// widget.
        /// </summary>
        /// <remarks>
        /// <para>The text-field and checkbox builders emitted this eleven-line tail byte for byte.
        /// The cluster is exactly those two: the annotation and form appearance generators build a
        /// form too, but each also writes <c>/Type /XObject</c> and a <c>/Resources</c>, and one a
        /// <c>/Matrix</c> — folding those in would change the keys these two emit, so the boundary
        /// is deliberate rather than an oversight.</para>
        ///
        /// <para><b><c>/Resources</c> names the font the content uses</b>, so the appearance stands
        /// on its own. Without it — and without a <c>/DR</c> on the form — the file would be legal only
        /// because a new form is written with <c>/NeedAppearances true</c> and a conforming processor
        /// regenerates it; a consumer that honours the supplied appearance, or a caller that clears
        /// the flag, would be left with an unresolvable font name. See <see cref="AppearanceFonts"/>: the same object is named
        /// here and in the form's <c>/DR</c>.</para>
        /// </remarks>
        private static PdfStream FormStream(string content, double w, double h, PdfDictionary resources)
        {
            byte[] bytes = Core.Text.Latin1Text.Encode(content);
            var stream = new PdfStream();
            stream.Set(PdfName.Subtype, PdfName.Form);
            stream.Set(PdfName.FormType, new PdfNumber(1));
            // The font the content names, defined on the stream itself — see AppearanceFonts for
            // what leaning on /NeedAppearances for this cost.
            if (resources != null) stream.Set(PdfName.Resources, resources);
            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(0)); bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(w)); bbox.Add(new PdfNumber(h));
            stream.Set(PdfName.BBox, bbox);
            stream.RawData = bytes;
            // Inert, and kept only for a reader of this method: PdfWriter assigns /Length
            // from the bytes it emits, after encryption, for every stream it writes. This
            // value never reaches the file, and would be wrong if it did — an encrypted
            // stream's length is not its plaintext length.
            stream.Set(PdfName.Length, new PdfNumber(bytes.Length));
            return stream;
        }


        /// <summary>
        /// The standard-14 font objects a generated appearance names, one per resource name, so the
        /// form's <c>/DR</c> and every appearance's <c>/Resources</c> point at the <b>same</b>
        /// object.
        /// </summary>
        /// <remarks>
        /// <para><b>What this closes.</b> The generated content names a font — <c>/Helv</c> for text,
        /// <c>/ZaDb</c> for a check box — that nothing in the file defined: the appearance carried no
        /// <c>/Resources</c> and a newly created form carried no <c>/DR</c>. It was legal only
        /// because the builder also writes <c>/NeedAppearances true</c>, asking the processor to
        /// regenerate; a consumer that honours the supplied appearance, or a caller that clears the
        /// flag, got an unresolvable font. The same dependency hid a missing <c>/AP /N</c> level for
        /// as long as it existed.</para>
        ///
        /// <para><b>One object, named twice.</b> Writing the font into the appearance and again into
        /// the <c>/DR</c> would be two dictionaries that must agree — the shape this repository
        /// treats as a defect waiting to happen. The resource name is resolved to its base font
        /// through <see cref="Standard14Names.Canonical"/>, which already owns the AcroForm short
        /// names.</para>
        ///
        /// <para><b>Symbolic faces get no <c>/Encoding</c>.</b> ZapfDingbats and Symbol carry their
        /// own, and §9.6.6.2 makes a named encoding override it — so writing
        /// <c>/WinAnsiEncoding</c> onto <c>/ZaDb</c> would replace the check-mark glyph a check box
        /// draws with whatever Latin character shares its code.</para>
        /// </remarks>
        private sealed class AppearanceFonts
        {
            private readonly Dictionary<string, int> _byResourceName =
                new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>The object number of the font <paramref name="resourceName"/> denotes.</summary>
            /// <remarks>
            /// <b>The memo is live since <see cref="AddFields"/>, and was not before.</b> A mutation
            /// sweep once found no caller reaching it: the type was constructed per single-field
            /// call and each field asks for one font, so the early return could not fire. One
            /// instance now serves a whole batch, so two text fields ask for <c>/Helv</c> twice and
            /// must get <i>the</i> object — one font in the file, named by both appearances'
            /// <c>/Resources</c> and by the form's <c>/DR</c>. Without it a second request mints a
            /// second font object and leaves the first unreferenced by the <c>/DR</c>.
            /// </remarks>
            internal int Require(string resourceName, ref int nextObjNum, List<IndirectObject> objects)
            {
                if (_byResourceName.TryGetValue(resourceName, out int existing)) return existing;

                string baseFont = Standard14Names.Canonical(resourceName) ?? StandardFonts.Helvetica;

                var font = new PdfDictionary();
                font.Set(PdfName.Type, PdfName.Font);
                font.Set(PdfName.Subtype, PdfName.Type1);
                font.Set(PdfName.BaseFont, new PdfName(baseFont));
                if (baseFont != StandardFonts.ZapfDingbats && baseFont != StandardFonts.Symbol)
                    font.Set(PdfName.Encoding, PdfName.WinAnsiEncoding);

                int number = nextObjNum++;
                font.ObjectNumber = number;
                font.GenerationNumber = 0;
                objects.Add(new IndirectObject(number, 0, font));

                _byResourceName[resourceName] = number;
                return number;
            }

            /// <summary>A <c>/Resources</c> naming every font allocated so far, or null for none.</summary>
            internal PdfDictionary Resources()
            {
                if (_byResourceName.Count == 0) return null;

                var fonts = new PdfDictionary();
                foreach (var pair in _byResourceName)
                    fonts.Set(new PdfName(pair.Key), new PdfReference(pair.Value, 0));

                var resources = new PdfDictionary();
                resources.Set(PdfName.Font, fonts);
                return resources;
            }

            /// <summary>
            /// Merges the allocated fonts into <paramref name="acroForm"/>'s <c>/DR</c>, keeping
            /// whatever it already defines.
            /// </summary>
            /// <remarks>
            /// An existing entry wins: a form that already defines <c>/Helv</c> has made a statement
            /// about which font that name means, and replacing it would change how every field
            /// already in the document draws.
            /// </remarks>
            internal void MergeIntoDefaultResources(PdfDictionary acroForm, PdfDocument doc)
            {
                if (_byResourceName.Count == 0) return;

                // Unreadable is refused, not rebuilt: the /DR and /Font written here replace them,
                // and the fonts the other fields' /DA name would stop resolving.
                var existingDr = Readable(doc, acroForm[PdfName.DR], "/DR");
                PdfDictionary dr = existingDr != null
                    ? PdfDictionary.CloneShallow(existingDr) : new PdfDictionary();

                var existingFonts = Readable(doc, dr[PdfName.Font], "/DR /Font");
                PdfDictionary fonts = existingFonts != null
                    ? PdfDictionary.CloneShallow(existingFonts) : new PdfDictionary();

                foreach (var pair in _byResourceName)
                {
                    var name = new PdfName(pair.Key);
                    if (fonts[name] == null) fonts.Set(name, new PdfReference(pair.Value, 0));
                }

                dr.Set(PdfName.Font, fonts);
                acroForm.Set(PdfName.DR, dr);
            }

            private static PdfDictionary Readable(PdfDocument doc, PdfObject value, string what)
            {
                var read = EntryRead.Resolve(doc.ResolveObject, value, out string unreadable) as PdfDictionary;
                if (unreadable != null)
                    throw new Core.PdfException(
                        "The form's " + what + " could not be read (" + unreadable + "), so adding fields " +
                        "would replace the fonts the form's fields draw with. No changes were written.");
                return read;
            }
        }

        private static string BuildDA(string fontName, double fontSize) =>
            $"/{Core.Objects.PdfNameEscape.Escape(fontName ?? StandardFonts.HelveticaAbbrev)} {fontSize.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} Tf 0 g";

        // Formats with InvariantCulture so {0:F4} etc. always emit '.' as the
        // decimal separator; CurrentCulture would produce commas — invalid content-stream
        // operators — on locales like de-DE / fr-FR.
        private static string InvFmt(string fmt, params object[] args) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args);

        /// <summary>
        /// §7.3.4.2 literal-string escaping, through the single implementation in Core.
        /// </summary>
        /// <remarks>
        /// This was the three-<c>Replace</c> chain that escapes <c>\ ( )</c> and nothing else — one
        /// of the four copies that outlived the consolidation <see cref="PdfStringEscape"/> was
        /// created for. It leaves control bytes raw, and a raw CR inside a literal string is read
        /// back as LF, so a caller-supplied field value containing a carriage return round-tripped
        /// as different text.
        /// </remarks>
        private static string EscapePdf(string s) => PdfStringEscape.EscapeLiteral(s);
    }

    // ----------------------------------------------------------------
    //  Field descriptor classes
    // ----------------------------------------------------------------

    /// <summary>Base class for all form field descriptors.</summary>
    public abstract class FormFieldBase
    {
        /// <summary>Partial field name (T entry). Must be unique within the form.</summary>
        public string FieldName { get; set; }
        /// <summary>Alternate (tooltip) field name (TU entry).</summary>
        public string AlternateFieldName { get; set; }
        /// <summary>Zero-based page index where the widget appears.</summary>
        public int PageIndex { get; set; }
        /// <summary>X position of the widget in points from the lower-left of the page.</summary>
        public double X { get; set; }
        /// <summary>Y position of the widget in points from the lower-left of the page.</summary>
        public double Y { get; set; }
        /// <summary>Widget width in points.</summary>
        public double Width { get; set; } = 120;
        /// <summary>Widget height in points.</summary>
        public double Height { get; set; } = 20;
        /// <summary>Whether the field is read-only.</summary>
        public bool ReadOnly { get; set; }
    }

    /// <summary>Text input field descriptor.</summary>
    public sealed class FormTextField : FormFieldBase
    {
        /// <summary>Whether this is a multi-line text field.</summary>
        public bool Multiline { get; set; }
        /// <summary>Whether this is a password field (value masked).</summary>
        public bool Password { get; set; }
        /// <summary>Maximum number of characters (0 = unlimited).</summary>
        public int MaxLength { get; set; }
        /// <summary>Default (initial) value.</summary>
        public string DefaultValue { get; set; }
        /// <summary>PDF font resource name for the default appearance (default: Helv).</summary>
        public string FontName { get; set; } = StandardFonts.HelveticaAbbrev;
        /// <summary>Font size in points for the default appearance (default: 10).</summary>
        public double FontSize { get; set; } = 10;
    }

    /// <summary>Checkbox field descriptor.</summary>
    public sealed class FormCheckboxField : FormFieldBase
    {
        /// <summary>Whether the checkbox is initially checked.</summary>
        public bool IsChecked { get; set; }
    }

    /// <summary>Base for choice fields (combo box and list box).</summary>
    public abstract class FormChoiceFieldBase : FormFieldBase
    {
        /// <summary>Options to display in the list.</summary>
        public List<ChoiceOption> Options { get; set; } = new List<ChoiceOption>();
        /// <summary>Default selected value.</summary>
        public string DefaultValue { get; set; }
        /// <summary>Allow multiple selections (list box only).</summary>
        public bool MultiSelect { get; set; }
        /// <summary>PDF font resource name (default: Helv).</summary>
        public string FontName { get; set; } = StandardFonts.HelveticaAbbrev;
        /// <summary>Font size in points (default: 10).</summary>
        public double FontSize { get; set; } = 10;
    }

    /// <summary>Combo box (drop-down) field descriptor.</summary>
    public sealed class FormComboField : FormChoiceFieldBase { }

    /// <summary>List box (scrollable list) field descriptor.</summary>
    public sealed class FormListField : FormChoiceFieldBase { }

    /// <summary>
    /// A radio button group: one field, whose value is the chosen button's export value, and one
    /// widget per button.
    /// </summary>
    /// <remarks>
    /// <para><b>A group has no rectangle of its own</b>, so the inherited
    /// <see cref="FormFieldBase.PageIndex"/>, <see cref="FormFieldBase.X"/>,
    /// <see cref="FormFieldBase.Y"/>, <see cref="FormFieldBase.Width"/> and
    /// <see cref="FormFieldBase.Height"/> are not read: each <see cref="Buttons"/> entry carries its
    /// own, and the buttons may sit on different pages.</para>
    ///
    /// <para>Written as §12.7.4.2.4 describes — a parent field with <c>/FT /Btn</c> and the Radio flag,
    /// and each button a widget in its <c>/Kids</c> whose <c>/AP /N</c> holds the button's own on
    /// state and <c>/Off</c>. NoToggleToOff is set, which is how an HTML radio group behaves: once one
    /// is chosen, clicking it again does not clear the group.</para>
    /// </remarks>
    public sealed class FormRadioGroupField : FormFieldBase
    {
        /// <summary>The buttons, in the order they are offered.</summary>
        public List<RadioButtonOption> Buttons { get; set; } = new List<RadioButtonOption>();

        /// <summary>The export value of the button initially chosen, or null for none.</summary>
        public string SelectedValue { get; set; }
    }

    /// <summary>One button of a <see cref="FormRadioGroupField"/>: its export value and where it is drawn.</summary>
    public sealed class RadioButtonOption
    {
        /// <summary>Creates a button.</summary>
        public RadioButtonOption(string exportValue, int pageIndex, double x, double y, double width, double height)
        {
            ExportValue = exportValue;
            PageIndex = pageIndex;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>The group's value when this button is chosen — the button's on-state name.</summary>
        public string ExportValue { get; }
        /// <summary>Zero-based page index where the button appears.</summary>
        public int PageIndex { get; }
        /// <summary>X position in points from the lower-left of the page.</summary>
        public double X { get; }
        /// <summary>Y position in points from the lower-left of the page.</summary>
        public double Y { get; }
        /// <summary>Width in points.</summary>
        public double Width { get; }
        /// <summary>Height in points.</summary>
        public double Height { get; }
    }

    /// <summary>An option entry in a choice field.</summary>
    public sealed class ChoiceOption
    {
        /// <summary>Text displayed in the UI.</summary>
        public string DisplayText { get; set; }
        /// <summary>Export (submission) value. Null means same as DisplayText.</summary>
        public string ExportValue { get; set; }

        public ChoiceOption(string displayText, string exportValue = null)
        {
            DisplayText = displayText;
            ExportValue = exportValue;
        }
    }
}
