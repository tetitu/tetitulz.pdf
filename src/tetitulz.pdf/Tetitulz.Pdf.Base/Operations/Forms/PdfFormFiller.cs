// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Fills AcroForm field values and regenerates the <c>/AP</c> appearance streams that make the
    /// new values visible, through an incremental PDF update — the original bytes are preserved, so
    /// an approval signature taken over them still verifies.
    ///
    /// <para><b>Per field type:</b></para>
    /// <list type="bullet">
    ///   <item><b>Text / choice</b> — the value is stored (UTF-16BE when it needs to be) and drawn
    ///   into every widget of the field, honouring the inheritable <c>/DA</c> (font, size, colour,
    ///   resolved through the AcroForm's <c>/DR</c>) and <c>/Q</c> quadding. Text the field's font
    ///   cannot encode needs an embedded font program — see <see cref="FormFillOptions.FontProvider"/>.</item>
    ///   <item><b>Check box / radio button</b> — the value is stored as a <i>name</i> and each
    ///   widget's <c>/AS</c> is set to the matching state from its own <c>/AP /N</c>, or to
    ///   <c>/Off</c>. Setting <c>/V</c> alone leaves the box looking unchanged in every viewer that
    ///   draws from <c>/AS</c>, which is all of them.</item>
    ///   <item><b>Signature</b> — never filled here; a signature is written by
    ///   <c>Tetitulz.Pdf.Signing</c>, and stuffing a string into <c>/V</c> would corrupt the field.</item>
    /// </list>
    ///
    /// <para><b>A value is never quietly half-applied.</b> When an appearance cannot be drawn the
    /// value is still written, <c>/NeedAppearances</c> is left <c>true</c> so the viewer draws it,
    /// and the field is named in <see cref="FormFillResult.FieldsWithoutAppearance"/> with the
    /// reason in <see cref="FormFillResult.Warnings"/>.</para>
    /// </summary>
    public static class PdfFormFiller
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Sets one or more form field values by fully-qualified field name, generating
        /// appearances with a Standard-14 font.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="values">Map of fully-qualified field name → value string.</param>
        /// <param name="fontName">Standard font for generated appearances (default Helvetica).</param>
        /// <param name="fontSize">Font size; 0 = auto-size (default).</param>
        /// <remarks>
        /// This overload discards the detail of what happened. Use
        /// <see cref="Fill(Stream, Stream, IDictionary{string, string}, FormFillOptions)"/> to learn
        /// which fields matched, and which values were stored but could not be drawn.
        /// </remarks>
        public static void Fill(
            Stream input,
            Stream output,
            IDictionary<string, string> values,
            string fontName = StandardFonts.Helvetica,
            float fontSize = 0)
        {
            Fill(input, output, values, new FormFillOptions { FontName = fontName, FontSize = fontSize });
        }

        /// <summary>
        /// Sets one or more form field values by fully-qualified field name and reports what was
        /// filled, what was not matched, and what could not be drawn.
        /// </summary>
        public static FormFillResult Fill(
            Stream input,
            Stream output,
            IDictionary<string, string> values,
            FormFillOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (values == null) throw new ArgumentNullException(nameof(values));
            options ??= new FormFillOptions();

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Checked before the AcroForm: an encrypted document loads no catalog, so its
            // AcroForm always reads as absent and "no AcroForm" would be the wrong diagnosis.
            //
            // An encrypted document's strings and streams are ciphertext under a per-object key.
            // The values and appearance streams written here are clear text, so the reader would
            // "decrypt" them into garbage. Fail closed rather than emit a form full of noise.
            if (doc.IsEncrypted)
                throw new Core.PdfException(
                    "Cannot fill an encrypted document here: the field values and appearance " +
                    "streams would be written in clear text where the reader expects per-object " +
                    "ciphertext, and this assembly cannot decrypt. Use " +
                    "Tetitulz.Pdf.Encryption.PdfEncryptedForms.Fill, which reads the form from a " +
                    "decrypted view and appends the revision encrypted with the document's own key.");

            FormFillPlan plan = Plan(doc, values, options);

            int newSize = plan.NextObjectNumber;
            PdfPageContentAppender.AppendRevision(
                ms, output, doc, new List<IndirectObject>(plan.Objects), newSize);

            return plan.Result;
        }

        /// <summary>
        /// Works out which objects a fill would write, without writing anything.
        ///
        /// <para><b>Why this is public.</b> Filling an <i>encrypted</i> form needs the field tree
        /// read from a decrypted view of the document while the revision is appended to the
        /// original — still-encrypted — bytes. Those are two different documents, so the step that
        /// decides <i>what</i> to write has to be separable from the step that writes it.
        /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedForms</c> is the caller that needs it; this
        /// assembly cannot do the job itself, because it has no way to decrypt.</para>
        /// </summary>
        /// <param name="document">The document to read the form from.</param>
        /// <param name="values">Fully-qualified field name → value.</param>
        /// <param name="options">Fill policy; may be null.</param>
        /// <param name="firstFreeObjectNumber">
        /// Lowest object number the plan may allocate. 0 (the default) means "one past the highest
        /// number <paramref name="document"/> declares". Pass an explicit value when the objects
        /// will be appended to a <i>different</i> file whose numbering is higher — a decrypted copy
        /// drops the original's object and cross-reference streams, so its highest number can be
        /// lower than the original's, and reusing a number would overwrite a live object.
        /// </param>
        public static FormFillPlan Plan(
            PdfDocument document,
            IDictionary<string, string> values,
            FormFillOptions options,
            int firstFreeObjectNumber = 0)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (values == null) throw new ArgumentNullException(nameof(values));
            options ??= new FormFillOptions();

            if (document.AcroForm == null)
                throw new Core.PdfException("PDF has no AcroForm.");

            int next = Math.Max(firstFreeObjectNumber,
                                document.CrossReferenceTable.GetMaxObjectNumber() + 1);

            var state = new FillState
            {
                Document = document,
                AcroFormDict = document.AcroForm.Dictionary,
                Values = values,
                Options = options,
                Result = new FormFillResult(),
                NextObjectNumber = next,
            };

            FillFields(document.AcroForm.Fields, state, depth: 0);

            foreach (var kvp in values)
                if (!state.Matched.Contains(kvp.Key))
                    state.Result.UnmatchedList.Add(kvp.Key);

            // Never LOWER the flag. /NeedAppearances is document-scoped (§12.7.2 Table 218) and
            // this pass knows only about the fields it was asked to fill. A form that arrived with
            // it true did so because some field holds a /V with no /AP — the standard "let the
            // viewer draw it" pattern — and clearing it as a side effect of filling one unrelated
            // field made every one of those other fields render blank, with nothing in
            // FormFillResult mentioning them (they were never in the values dictionary, so they
            // are not in Filled, Unmatched or FieldsWithoutAppearance either).
            state.Result.NeedAppearances =
                ExistingNeedAppearances(state)
                || !options.GenerateAppearances
                || state.Result.FieldsWithoutAppearance.Count > 0;

            UpdateAcroFormDict(state);
            state.Updates.EmitInto(state.NewObjects);

            return new FormFillPlan(state.NewObjects, state.Result, state.NextObjectNumber);
        }

        // ----------------------------------------------------------------
        //  Field traversal
        // ----------------------------------------------------------------

        /// <summary>
        /// Walks the field tree and fills the terminal fields whose fully-qualified name was asked
        /// for. Only terminal fields are matched: a widget kid inherits its parent's name, so
        /// matching it too would apply the value twice to the same field.
        /// </summary>
        private static void FillFields(IReadOnlyList<PdfFormField> fields, FillState state, int depth)
        {
            if (fields == null || depth >= PdfLimits.MaxTreeDepth) return;

            foreach (var field in fields)
            {
                // The guard PdfFormReader.Walk already applies, in the same shape and for the same
                // reason it states: a /Kids cycle re-visits the same field forever, and the depth
                // cap alone does NOT stop it — a two-node cycle stays far under 100 while the
                // lazily-created PdfFormField wrappers are all distinct objects, so nothing else
                // notices the repetition. The filler had the cap and not the set, so a cyclic field
                // tree re-applied the same edit up to the depth limit, growing the incremental
                // update with duplicate objects on every pass.
                int objNum = field.Dictionary?.ObjectNumber ?? 0;
                if (objNum > 0 && !state.VisitedFields.Add(objNum)) continue;

                if (field.IsTerminal)
                {
                    string fqn = field.FullyQualifiedName;
                    if (fqn != null && state.Values.TryGetValue(fqn, out string newValue))
                    {
                        state.Matched.Add(fqn);
                        ApplyValue(field, fqn, newValue ?? string.Empty, state);
                    }
                    continue;
                }

                FillFields(field.Kids, state, depth + 1);
            }
        }

        private static void ApplyValue(PdfFormField field, string fqn, string newValue, FillState state)
        {
            if (field.Dictionary.ObjectNumber <= 0)
            {
                state.Result.Warn(fqn, "the field dictionary is a direct object, which an incremental " +
                                       "update cannot replace, so it was skipped");
                return;
            }

            switch (field.FieldType)
            {
                case PdfFieldType.Signature:
                    state.Result.Warn(fqn, "signature fields are not filled here — sign the document with " +
                                           "Tetitulz.Pdf.Signing instead");
                    return;

                case PdfFieldType.Button:
                    ApplyButtonValue(field, fqn, newValue, state);
                    return;

                // "The file did not say what this is" is not "this is text". Unknown means no /FT
                // resolved anywhere in the chain, and writing a /V onto such a node is exactly the
                // damage ResolveFieldType's downward widget-kid repair exists to prevent: a text
                // value laid over what may well be a signature field. It reached here because the
                // switch had no arm for it and `default:` is the variable-text arm.
                case PdfFieldType.Unknown:
                    state.Result.Warn(fqn, "no /FT could be resolved for this field, so its type is " +
                                           "unknown and no value was written — a field the file does " +
                                           "not type must not be filled as text");
                    return;

                default:
                    // ISO 32000-2 Table 229 bit 14: "it is imperative that PDF processors never store
                    // the value of the text field in the PDF file if this flag is set." Refused like a
                    // signature field: written to /V and drawn in clear text in the appearance, a
                    // flattener would make the drawing permanent.
                    if (field.FieldType == PdfFieldType.Text
                        && PdfFieldFlags.Has(field.Flags, PdfFieldFlags.Password))
                    {
                        state.Result.Warn(fqn, "a password field is never filled: ISO 32000-2 Table 229 says " +
                                               "a PDF processor must never store its value in the file, so " +
                                               "nothing was written");
                        return;
                    }

                    ApplyVariableTextValue(field, fqn, newValue, state);
                    return;
            }
        }

        // ----------------------------------------------------------------
        //  Buttons — check boxes and radio groups
        // ----------------------------------------------------------------

        /// <summary>Values that select a check box when the on-state is not named explicitly.</summary>
        private static readonly HashSet<string> TruthyValues =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "on", "true", "yes", "1", "x", "checked" };

        /// <summary>Values that clear a check box or radio group.</summary>
        private static readonly HashSet<string> FalsyValues =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "", "off", "false", "no", "0", "unchecked", "none" };

        private static void ApplyButtonValue(PdfFormField field, string fqn, string newValue, FillState state)
        {
            if (PdfFieldFlags.Has(field.Flags, PdfFieldFlags.Pushbutton))
            {
                state.Result.Warn(fqn, "a push button holds no value, so nothing was written");
                return;
            }

            var widgets = field.WidgetDictionaries;
            var statesByWidget = new List<List<string>>(widgets.Count);
            // Each state once, a set deciding: a search of the list was widgets² over a field whose
            // kids the file chooses.
            var allStates = new Core.Util.DistinctStrings();

            foreach (var widget in widgets)
            {
                var onStates = ReadOnStates(widget, state.Document);
                statesByWidget.Add(onStates);
                foreach (string s in onStates)
                    allStates.Add(s);
            }

            string requested = ResolveButtonState(newValue, allStates, out string problem);
            if (problem != null)
            {
                state.Result.Warn(fqn, problem);
                return;
            }

            var fieldUpdate = state.Updates.Edit(field.Dictionary);
            fieldUpdate.Set(PdfName.V, requested == null ? PdfName.Off : new PdfName(requested));

            for (int i = 0; i < widgets.Count; i++)
            {
                if (widgets[i].ObjectNumber <= 0)
                {
                    state.Result.Warn(fqn, "a widget is a direct object and its /AS could not be set");
                    continue;
                }

                bool widgetHoldsState = requested != null && statesByWidget[i].Contains(requested);
                var widgetUpdate = state.Updates.Edit(widgets[i]);
                widgetUpdate.Set(PdfName.AS, widgetHoldsState ? new PdfName(requested) : PdfName.Off);
            }

            if (allStates.Count == 0)
            {
                state.Result.NoAppearanceList.Add(fqn);
                state.Result.Warn(fqn, "the widget has no /AP appearance states, so the value was stored " +
                                       "but there is nothing to switch on");
            }

            state.Result.FilledList.Add(fqn);
        }

        /// <summary>
        /// Returns the <c>/AP /N</c> sub-dictionary keys other than <c>/Off</c> — the states this
        /// widget can actually display.
        /// </summary>
        /// <remarks>
        /// Through <see cref="NormalAppearance"/>, as <c>PdfFormReader</c> reads them: a lone
        /// appearance stream is one appearance and no states. Read as a dictionary here, the stream's
        /// own keys — <c>/Type</c>, <c>/BBox</c>, <c>/Length</c> — were the states, so filling "Yes"
        /// was refused as ambiguous among them and filling "BBox" wrote <c>/V /BBox</c>.
        /// </remarks>
        private static List<string> ReadOnStates(PdfDictionary widget, PdfDocument doc)
        {
            var result = new List<string>();
            var normal = NormalAppearance.GetStates(doc, widget);
            if (normal == null) return result;

            foreach (var kvp in normal)
                if (kvp.Key != PdfName.Off)
                    result.Add(kvp.Key.Value);

            return result;
        }

        /// <summary>
        /// Maps a caller's string onto an appearance-state name. Returns the state name, or
        /// <c>null</c> meaning <c>/Off</c>; <paramref name="problem"/> is set when the value cannot
        /// be resolved, in which case nothing is written. The states are named in it twenty at most,
        /// the rest counted: a field's kids are the file's to choose.
        /// </summary>
        private static string ResolveButtonState(string value, Core.Util.DistinctStrings onStates, out string problem)
        {
            problem = null;
            value ??= string.Empty;

            foreach (string s in onStates.Items)
                if (string.Equals(s, value, StringComparison.Ordinal)) return s;
            foreach (string s in onStates.Items)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return s;

            if (FalsyValues.Contains(value)) return null;

            if (TruthyValues.Contains(value))
            {
                if (onStates.Count == 1) return onStates.Items[0];
                if (onStates.Count == 0) return PdfName.Yes.Value;

                problem = string.Format(
                    "'{0}' does not say which of the {1} appearance states ({2}) to select — " +
                    "pass the state name itself",
                    value, onStates.Count, onStates.Joined(", "));
                return null;
            }

            problem = onStates.Count == 0
                ? string.Format("'{0}' is not an on/off value and the widget declares no appearance states", value)
                : string.Format("'{0}' matches none of the widget's appearance states ({1})",
                                value, onStates.Joined(", "));
            return null;
        }

        // ----------------------------------------------------------------
        //  Text and choice fields
        // ----------------------------------------------------------------

        private static void ApplyVariableTextValue(PdfFormField field, string fqn, string newValue, FillState state)
        {
            var fieldUpdate = state.Updates.Edit(field.Dictionary);

            // §12.7.4.4: a multi-select list box's several selections are an array /V, with /I the
            // selected options' indices in ascending order; one selection is a string, as for any
            // other field, and a stale /I from the file would then disagree with it.
            string[] selections = IsMultiSelectListBox(field) ? SplitSelections(newValue) : null;
            if (selections != null && selections.Length > 1)
            {
                var values = new PdfArray();
                foreach (string s in selections) values.Add(EncodeFieldValue(s));
                fieldUpdate.Set(PdfName.V, values);
                PdfArray indices = SelectedIndices(field, selections, state);
                if (indices != null) fieldUpdate.Set(PdfName.ChoiceSelectedIndices, indices);
                else fieldUpdate.Remove(PdfName.ChoiceSelectedIndices);
            }
            else
            {
                fieldUpdate.Set(PdfName.V, EncodeFieldValue(selections != null && selections.Length == 1 ? selections[0] : newValue));
                fieldUpdate.Remove(PdfName.ChoiceSelectedIndices);
            }

            if (selections != null) foreach (string s in selections) WarnOnValueConstraints(field, fqn, s, state);
            else WarnOnValueConstraints(field, fqn, newValue, state);

            state.Result.FilledList.Add(fqn);
            if (!state.Options.GenerateAppearances) return;

            var da = state.Options.HonorDefaultAppearance
                ? FormAppearance.ParseDefaultAppearance(ReadDefaultAppearance(field, state))
                : null;

            // The value is stored; what is drawn may be its formatted text (FormFillOptions.DisplayValues).
            string drawn = state.Options.DisplayValues != null
                           && state.Options.DisplayValues.TryGetValue(fqn, out string display) && display != null
                ? display
                : selections != null ? string.Join("\n", selections) : newValue;

            // §12.7.4.3, read once for the filler and the flattener — FieldLayout.For.
            var layout = FieldLayout.For(field.FieldType == PdfFieldType.Text, field.Flags,
                                         field.MaxLength, ReadQuadding(field, state));
            // A multi-select list box's selections are drawn one under another.
            if (selections != null && selections.Length > 1) layout.Multiline = true;

            bool reported = false;
            foreach (var widget in field.WidgetDictionaries)
            {
                if (widget.ObjectNumber <= 0)
                {
                    NoteMissingAppearance(fqn, "a widget is a direct object, which an incremental update " +
                                                "cannot replace", state, ref reported);
                    continue;
                }

                var (width, height) = ReadRectSize(widget, state.Document);
                int rotation = WidgetRotation.Read(widget, state.Document.ResolveObject);
                int nextObjNum = state.NextObjectNumber;
                var apStream = FormAppearance.Build(
                    drawn, width, height, rotation, da, layout,
                    state.Document, state.AcroFormDict, state.Options,
                    ref nextObjNum, state.NewObjects, out string error, out string warning);
                state.NextObjectNumber = nextObjNum;

                if (apStream == null)
                {
                    NoteMissingAppearance(fqn, error, state, ref reported);
                    continue;
                }

                // The appearance was produced, but not all of the value reached it. Reported
                // WITHOUT setting /NeedAppearances: the drawing is correct as far as it goes, and
                // asking a viewer to redo it would not make the widget any taller.
                if (warning != null) state.Result.Warn(fqn, warning);

                var apDict = new PdfDictionary();
                apDict.Set(PdfName.N, new PdfReference(apStream.ObjectNumber, 0));
                state.Updates.Edit(widget).Set(PdfName.AP, apDict);
            }
        }

        /// <summary>
        /// What separates a multi-select list box's selections in the value a caller passes
        /// (§12.7.4.4): <c>langs = "en" + MultiSelectSeparator + "fr"</c> selects both. A newline can
        /// be no part of an option's export value, which is one line of text.
        /// </summary>
        public const string MultiSelectSeparator = "\n";

        /// <summary>A list box (<c>/FT /Ch</c>, not a combo) that admits several selections (Table 230, bit 22).</summary>
        private static bool IsMultiSelectListBox(PdfFormField field)
            => field.FieldType == PdfFieldType.Choice
               && PdfFieldFlags.Has(field.Flags, PdfFieldFlags.MultiSelect)
               && !PdfFieldFlags.Has(field.Flags, PdfFieldFlags.Combo);

        /// <summary>
        /// The selections a multi-select value names, each once in its order; an empty value names
        /// none. Kept distinct by a set beside the list: the value may come from an exchange file,
        /// and a search of the list before each addition cost its selections squared.
        /// </summary>
        private static string[] SplitSelections(string value)
        {
            var list = new Tetitulz.Pdf.Core.Util.DistinctStrings();
            foreach (string part in value.Split(new[] { MultiSelectSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.TrimEnd('\r');
                if (s.Length > 0) list.Add(s);
            }
            return list.Items.ToArray();
        }

        /// <summary>
        /// <c>/I</c> for <paramref name="selections"/>: each one's index among the field's <c>/Opt</c>
        /// entries (by export value, else display text), ascending; null when a selection is no option.
        /// </summary>
        private static PdfArray SelectedIndices(PdfFormField field, string[] selections, FillState state)
        {
            if (!(state.Document.ResolveObject(field.Dictionary[PdfName.Opt]) is PdfArray options)) return null;
            // The options indexed once (the first option a text names wins), so the selections —
            // the caller's — cost one lookup each, not a pass over the file's options.
            var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < options.Count; i++)
            {
                var entry = state.Document.ResolveObject(options[i]);
                if (entry is PdfString text) Note(indexOf, text.GetText(), i);
                else if (entry is PdfArray pair)
                    for (int j = 0; j < pair.Count; j++)
                        if (state.Document.ResolveObject(pair[j]) is PdfString t) Note(indexOf, t.GetText(), i);
            }
            var indices = new List<long>();
            foreach (string s in selections)
            {
                if (!indexOf.TryGetValue(s, out int at)) return null;
                indices.Add(at);
            }
            indices.Sort();
            return PdfArray.OfNumbers(indices.ConvertAll(i => (double)i).ToArray());

            static void Note(Dictionary<string, int> map, string text, int index)
            {
                if (text != null && !map.ContainsKey(text)) map[text] = index;
            }
        }

        private static void NoteMissingAppearance(string fqn, string reason, FillState state, ref bool reported)
        {
            state.Result.Warn(fqn, reason);
            if (reported) return;
            state.Result.NoAppearanceList.Add(fqn);
            reported = true;
        }

        /// <summary>
        /// Reports the value constraints this filler stores but does not enforce, so a caller is
        /// never left believing a value was adjusted to fit.
        /// </summary>
        private static void WarnOnValueConstraints(PdfFormField field, string fqn, string newValue, FillState state)
        {
            int maxLen = field.MaxLength;
            if (maxLen > 0 && newValue.Length > maxLen)
                state.Result.Warn(fqn, string.Format(
                    "the value is {0} characters but /MaxLen is {1}; it was stored unchanged, not truncated",
                    newValue.Length, maxLen));

            // A comb field with no /MaxLen has no cell count, so there is no comb to lay out — the
            // value is drawn as ordinary text and the caller is told why.
            if (PdfFieldFlags.Has(field.Flags, PdfFieldFlags.Comb) && maxLen <= 0)
                state.Result.Warn(fqn, "the field sets the comb flag but has no /MaxLen, so there is no " +
                                       "cell count to lay out; the value was drawn as ordinary text");

            if (field.FieldType != PdfFieldType.Choice || newValue.Length == 0) return;

            var options = state.Document.ResolveObject(field.Dictionary[PdfName.Opt]) as PdfArray;
            if (options == null || options.Count == 0) return;

            for (int i = 0; i < options.Count; i++)
            {
                var entry = state.Document.ResolveObject(options[i]);
                if (entry is PdfString s && s.GetText() == newValue) return;
                if (entry is PdfArray pair)
                    for (int j = 0; j < pair.Count; j++)
                        if ((state.Document.ResolveObject(pair[j]) as PdfString)?.GetText() == newValue) return;
            }

            state.Result.Warn(fqn, "the value is not one of the field's /Opt entries");
        }

        private static string ReadDefaultAppearance(PdfFormField field, FillState state)
            => AnnotationFallbackModel.DefaultAppearanceOrFormDefault(
                   state.Document.ResolveObject(field.GetInherited(PdfName.DA, o => o is PdfString)),
                   state.Document);

        /// <remarks>
        /// The form-wide fallback is not read through <c>GetInteger</c>, which does not follow a
        /// reference — <c>/AcroForm &lt;&lt; /Q 9 0 R &gt;&gt;</c> would draw left-aligned. It is the
        /// one rule in <c>AnnotationFallbackModel.QuaddingOrFormDefault</c>, which the
        /// reader, the flattener and both renderers reach too.
        /// </remarks>
        private static int ReadQuadding(PdfFormField field, FillState state)
            => AnnotationFallbackModel.QuaddingOrFormDefault(
                   state.Document.ResolveObject(field.GetInherited(PdfName.Q, o => o is PdfNumber)),
                   state.Document) ?? PdfQuadding.Left;

        /// <remarks>
        /// <c>doc</c> is a parameter because <c>GetArray</c> does not follow a reference. An
        /// indirect <c>/Rect</c> returned (0, 0), which trips <c>FormAppearance.Build</c>'s first
        /// guard — "the widget rectangle is empty, so there is nowhere to draw the value" — so
        /// <c>/V</c> was written, <b>no appearance was generated</b>, and <c>/NeedAppearances</c>
        /// was not set for it either. The value is then invisible in every viewer that honours
        /// appearance streams, and the field is reported in <c>NoAppearanceList</c> as if the
        /// document were at fault.
        /// </remarks>
        private static (double Width, double Height) ReadRectSize(PdfDictionary widget, PdfDocument doc)
        {
            var rect = PdfResolve.Array(doc, widget, PdfName.Rect);
            if (rect == null || rect.Count < 4) return (0, 0);

            double x1 = Num(doc, rect, 0);
            double y1 = Num(doc, rect, 1);
            double x2 = Num(doc, rect, 2);
            double y2 = Num(doc, rect, 3);
            return (Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }

        // One implementation, in PdfResolve.ArrayDouble. This existed twice, byte-identical down to
        // the doc comment above it — and neither copy bounds-checked, so a short /Rect threw
        // IndexOutOfRangeException out of a public form call.
        private static double Num(PdfDocument doc, PdfArray arr, int index)
            => PdfResolve.ArrayDouble(doc, arr, index);

        // ----------------------------------------------------------------
        //  AcroForm dictionary update
        // ----------------------------------------------------------------

        /// <summary>
        /// The document's incoming <c>/NeedAppearances</c>, resolved through an indirect reference.
        /// </summary>
        /// <remarks>
        /// Read through <c>ResolveObject</c>: <see cref="PdfDictionary"/>'s indexer does not follow
        /// a reference, so a producer writing <c>/NeedAppearances 9 0 R</c> would read as absent —
        /// which here means "false", the exact value that loses the other fields' appearances.
        /// </remarks>
        private static bool ExistingNeedAppearances(FillState state)
        {
            var dict = state.AcroFormDict;
            if (dict == null) return false;
            return (state.Document?.ResolveObject(dict[PdfName.NeedAppearances]) as PdfBoolean)?.Value == true;
        }

        /// <summary>
        /// Writes <c>/NeedAppearances</c> to match what was actually drawn: <c>false</c> only when
        /// every value carries its own appearance <i>and</i> the document did not already ask for
        /// viewer-generated appearances; <c>true</c> when at least one does not, so the viewer is
        /// asked to draw the ones this pass could not.
        /// </summary>
        private static void UpdateAcroFormDict(FillState state)
        {
            var acroFormDict = state.AcroFormDict;
            if (acroFormDict == null || acroFormDict.ObjectNumber <= 0) return;

            state.Updates.Edit(acroFormDict)
                 .Set(PdfName.NeedAppearances, new PdfBoolean(state.Result.NeedAppearances));
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Encodes a field value as a PDF text string (ISO 32000-1 §7.9.2.2).
        /// </summary>
        /// <remarks>
        /// One implementation, in <see cref="PdfString(string)"/>.
        /// <para><b>This delegation changes no bytes.</b> The behaviour that did change is the
        /// constructor's: it routes the characters PDFDocEncoding and Latin-1 <i>disagree</i> about
        /// (0x18-0x1F, 0x80-0xA0) through UTF-16BE, so a field value containing a NO-BREAK SPACE is
        /// not written as the byte 0xA0 and read back as the EURO SIGN.</para>
        /// </remarks>
        private static PdfString EncodeFieldValue(string text) => new PdfString(text);

        // One implementation, in Core.Objects.PdfStringEscape (ISO 32000-1 7.3.4.2).
        internal static string EscapePdfString(string s) => PdfStringEscape.EscapeLiteral(s);

        // ----------------------------------------------------------------
        //  Per-call state
        // ----------------------------------------------------------------

        private sealed class FillState
        {
            internal PdfDocument Document;
            internal PdfDictionary AcroFormDict;
            internal IDictionary<string, string> Values;
            internal FormFillOptions Options;
            internal FormFillResult Result;
            internal int NextObjectNumber;
            internal readonly HashSet<string> Matched = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>
            /// Field object numbers already walked, so a <c>/Kids</c> cycle terminates. Per-call
            /// (this state is created per fill), never static — <c>Matched</c> beside it dedups
            /// requested NAMES, which is a different question and does not bound the traversal.
            /// </summary>
            internal readonly HashSet<int> VisitedFields = new HashSet<int>();
            internal readonly List<IndirectObject> NewObjects = new List<IndirectObject>();
            internal readonly ObjectUpdates Updates = new ObjectUpdates();
        }

        /// <summary>
        /// Accumulates edits to existing objects, one clone per object number.
        ///
        /// <para>This exists because a merged field/widget is <b>one</b> object that receives both
        /// <c>/V</c> and <c>/AS</c> (or <c>/AP</c>). Writing an indirect object twice in a single
        /// incremental update leaves the earlier copy dead in the file and the xref pointing at
        /// whichever came last, so the edits have to be merged before they are written.</para>
        /// </summary>
        private sealed class ObjectUpdates
        {
            private readonly Dictionary<int, PdfDictionary> _byObjectNumber = new Dictionary<int, PdfDictionary>();

            internal PdfDictionary Edit(PdfDictionary source)
            {
                int objNum = source.ObjectNumber;
                if (_byObjectNumber.TryGetValue(objNum, out var existing)) return existing;

                var clone = new PdfDictionary();
                foreach (var kvp in source)
                    clone.Set(kvp.Key, kvp.Value);
                clone.ObjectNumber = objNum;
                clone.GenerationNumber = 0;
                _byObjectNumber[objNum] = clone;
                return clone;
            }

            internal void EmitInto(List<IndirectObject> objects)
            {
                foreach (var kvp in _byObjectNumber)
                    objects.Add(new IndirectObject(kvp.Key, 0, kvp.Value));
            }
        }
    }
}

