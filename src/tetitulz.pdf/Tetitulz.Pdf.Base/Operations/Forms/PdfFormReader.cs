// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Encryption;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Reads an AcroForm's fields into plain data: what the fields are called, what type they are,
    /// what they currently hold, and where their widgets sit on the page.
    ///
    /// <para>This is the read half of <see cref="PdfFormFiller"/> — the names it returns in
    /// <see cref="PdfFormFieldInfo.FullyQualifiedName"/> are exactly the keys
    /// <see cref="PdfFormFiller.Fill(Stream, Stream, IDictionary{string, string}, FormFillOptions)"/>
    /// expects, and for a check box or radio group
    /// <see cref="PdfFormFieldInfo.OnStates"/> lists the values it will accept.</para>
    ///
    /// <para>Nothing here mutates the document, and the field tree walk is depth- and
    /// cycle-guarded, so a hostile <c>/Kids</c> graph cannot hang it.</para>
    /// </summary>
    public static class PdfFormReader
    {
        /// <summary>Reads every terminal field of the document's AcroForm; empty when it has none.</summary>
        public static IReadOnlyList<PdfFormFieldInfo> GetFields(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the form fields", "GetFields(Stream, string)");
            return Read(doc);
        }

        /// <summary>
        /// Reads every terminal field of an <b>encrypted</b> document's AcroForm, decrypting with
        /// <paramref name="password"/>.
        /// </summary>
        /// <remarks>
        /// <para>An unencrypted document reaches the same code by either overload.</para>
        /// <para>An encrypted document need not route through
        /// <c>Tetitulz.Pdf.Encryption.PdfEncryptedForms.GetFields</c>, which decrypts and then calls
        /// straight back into this class. That mirror exists because Base cannot reference
        /// Encryption — but the inversion through <see cref="PdfDecryptorHolder"/> was already
        /// there for the renderer, so the read never actually needed the other assembly. The
        /// <b>write</b> side still does, and stays there: filling a field has to re-encrypt.</para>
        /// <para>Requires a registered decryptor; without one this throws rather than returning an
        /// empty list, since an empty list is what a document with no fields returns.</para>
        /// </remarks>
        /// <param name="input">The document.</param>
        /// <param name="password">User or owner password.</param>
        public static IReadOnlyList<PdfFormFieldInfo> GetFields(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var decrypted = PdfDecryptedInput.OpenDecrypted(input, password);
            using var doc = PdfDocument.Open(decrypted);
            return Read(doc);
        }

        /// <summary>Reads every terminal field of the PDF at <paramref name="inputPath"/>.</summary>
        public static IReadOnlyList<PdfFormFieldInfo> GetFields(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var doc = PdfDocument.Open(inputPath);
            EncryptedRead.Refuse(doc, "the form fields", "GetFields(Stream, string)");
            return Read(doc);
        }

        /// <summary>
        /// <c>true</c> when the document's <c>/AcroForm</c> carries an <c>/XFA</c> entry — that is,
        /// when it is an XFA form and not only an AcroForm.
        /// </summary>
        /// <param name="input">The document.</param>
        /// <remarks>
        /// <para><b>Why this is a question worth asking before anything else.</b> An XFA document
        /// holds its form twice (§12.7.8): as XML, and as an AcroForm whose widgets are what a
        /// non-XFA viewer draws. <see cref="GetFields(Stream)"/> reads the second one and cannot
        /// tell the caller that the first exists — so a filling workflow that reads fields, writes
        /// values and hands the result on has silently updated one of the two copies. Every
        /// operation here that <i>drops</i> the packet now says so
        /// (<c>PdfSanitizer.LastXfaWarning</c>, <c>PdfMerger.LastXfaWarning</c>,
        /// <c>PdfSplitter.LastXfaWarning</c>); this is how a caller asks in advance.</para>
        ///
        /// <para><b>Presence, not readability.</b> A packet this build cannot parse is still a
        /// packet, and answering <c>false</c> for it would be the one wrong answer — the caller
        /// would proceed as though there were nothing to keep in step.</para>
        /// </remarks>
        public static bool HasXfa(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "whether the form is XFA", "HasXfa(Stream, string)");
            return XfaPacket.IsPresent(doc.AcroForm?.Dictionary);
        }

        /// <summary>
        /// <c>true</c> when an <b>encrypted</b> document's <c>/AcroForm</c> carries an <c>/XFA</c>
        /// entry, decrypting with <paramref name="password"/>.
        /// </summary>
        /// <param name="input">The document.</param>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// Requires a registered decryptor, like the field read beside it. Without one the catalog
        /// never loads and the answer would be <c>false</c> for every encrypted document — which is
        /// indistinguishable from a correct <c>false</c>, so it throws instead.
        /// </remarks>
        public static bool HasXfa(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var decrypted = PdfDecryptedInput.OpenDecrypted(input, password);
            using var doc = PdfDocument.Open(decrypted);
            return XfaPacket.IsPresent(doc.AcroForm?.Dictionary);
        }

        /// <summary>
        /// The values held in the XFA <c>datasets</c> packet, keyed by their dotted path, or
        /// <see langword="null"/> when the document has no readable datasets packet.
        /// </summary>
        /// <param name="input">The document.</param>
        /// <remarks>
        /// <para><b>These are not <see cref="GetFields(Stream)"/>' names.</b> The keys are datasets
        /// paths (<c>form1.total</c>); the AcroForm's fully-qualified names carry SOM occurrence
        /// indices (<c>form1[0].total[0]</c>). <c>PdfXfaFormFiller.Fill</c> takes the AcroForm names
        /// and maps them, so a caller filling a form does not need this — it is here for a caller
        /// that wants to see what the two copies of the form each say, which is the only way to
        /// notice that a previous tool updated one of them.</para>
        /// <para><b>Null, not empty, when there is nothing to read</b>: an XFA form whose every
        /// field is blank returns an empty map, and that is a different answer.</para>
        /// </remarks>
        public static IReadOnlyDictionary<string, string> ReadXfaValues(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the XFA values", "ReadXfaValues(Stream, string)");
            return ReadXfaValues(doc);
        }

        /// <summary>
        /// The XFA <c>datasets</c> values of an <b>encrypted</b> document, decrypting with
        /// <paramref name="password"/>.
        /// </summary>
        /// <param name="input">The document.</param>
        /// <param name="password">User or owner password.</param>
        public static IReadOnlyDictionary<string, string> ReadXfaValues(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var decrypted = PdfDecryptedInput.OpenDecrypted(input, password);
            using var doc = PdfDocument.Open(decrypted);
            return ReadXfaValues(doc);
        }

        private static IReadOnlyDictionary<string, string> ReadXfaValues(PdfDocument doc)
        {
            var packet = XfaPacket.Read(doc.AcroForm?.Dictionary, doc);
            byte[] datasets = packet?.GetPacket(XfaPacket.DatasetsPacket);
            return datasets == null ? null : XfaDatasets.ReadValues(datasets);
        }

        // ----------------------------------------------------------------

        internal static IReadOnlyList<PdfFormFieldInfo> Read(PdfDocument doc)
        {
            var result = new List<PdfFormFieldInfo>();
            if (doc.AcroForm == null) return result;

            var read = new FormRead(MapAnnotationsToPages(doc));
            Walk(doc.AcroForm.Fields, doc, read, result, depth: 0);
            return result;
        }

        /// <summary>
        /// What one read of a form takes once however many of its nodes name it.
        /// </summary>
        /// <remarks>
        /// <b>Counts a file chooses multiply</b>: a <c>/Kids</c> array several parents name, an
        /// <c>/Opt</c> array or a <c>/V</c> array every choice field names, an appearance-state
        /// dictionary every widget names — each read per name, a form of a few hundred kilobytes
        /// describes a hundred thousand widgets or holds a list per field of everything shared. So a
        /// node is described once, under the first parent that reaches it (a node has one parent),
        /// and a shared list is one list every field it names holds. Shared text is decoded once by
        /// the string itself (<see cref="PdfString.GetText"/>).
        /// </remarks>
        private sealed class FormRead
        {
            internal FormRead(IReadOnlyDictionary<int, int> pageByAnnotation) => PageByAnnotation = pageByAnnotation;

            internal readonly IReadOnlyDictionary<int, int> PageByAnnotation;

            /// <summary>Field nodes walked, by object number: a <c>/Kids</c> cycle through indirect nodes.</summary>
            internal readonly HashSet<int> Visited = new HashSet<int>();

            /// <summary>Field and widget dictionaries described, by identity: a direct node in a shared array has no number.</summary>
            internal readonly HashSet<object> Fields = new HashSet<object>(PdfReferenceEquality.Instance);
            internal readonly HashSet<object> Widgets = new HashSet<object>(PdfReferenceEquality.Instance);

            internal readonly Dictionary<object, List<ChoiceOption>> Options = new Dictionary<object, List<ChoiceOption>>(PdfReferenceEquality.Instance);
            internal readonly Dictionary<object, List<string>> Selections = new Dictionary<object, List<string>>(PdfReferenceEquality.Instance);
            internal readonly Dictionary<object, List<string>> States = new Dictionary<object, List<string>>(PdfReferenceEquality.Instance);
        }

        /// <summary>
        /// The most appearance states one widget's <c>/AP /N</c> dictionary is read for. A check box
        /// or a radio button has one on-state and <c>/Off</c>; a dictionary of a million names every
        /// widget shares would otherwise be listed once per field.
        /// </summary>
        internal const int MaxAppearanceStatesPerWidget = 256;

        private static void Walk(
            IReadOnlyList<PdfFormField> fields,
            PdfDocument doc,
            FormRead read,
            List<PdfFormFieldInfo> result,
            int depth)
        {
            if (fields == null || depth >= PdfLimits.MaxTreeDepth) return;

            foreach (var field in fields)
            {
                // A /Kids cycle would otherwise re-describe the same field forever; the depth cap
                // alone is not enough, because a two-node cycle stays well under it while the
                // lazily-created wrappers are all distinct objects. A node a second parent names is
                // the first parent's.
                int objNum = field.Dictionary.ObjectNumber;
                if (objNum > 0 && !read.Visited.Add(objNum)) continue;
                if (!read.Fields.Add(field.Dictionary)) continue;

                if (field.IsTerminal)
                {
                    result.Add(Describe(field, doc, read));
                    continue;
                }

                Walk(field.Kids, doc, read, result, depth + 1);
            }
        }

        private static PdfFormFieldInfo Describe(PdfFormField field, PdfDocument doc, FormRead read)
        {
            int flags = field.Flags;
            var info = new PdfFormFieldInfo
            {
                FullyQualifiedName = field.FullyQualifiedName,
                PartialName = field.FieldName,
                AlternateName = (doc.ResolveObject(field.Dictionary[PdfName.TU]) as PdfString)?.GetText(),
                FieldType = field.FieldType,
                Flags = flags,
                MaxLength = field.MaxLength,
                Quadding = AnnotationFallbackModel.QuaddingOrFormDefault(
                    doc.ResolveObject(field.GetInherited(PdfName.Q, o => o is PdfNumber)), doc) ?? PdfQuadding.Left,
                DefaultAppearance = AnnotationFallbackModel.DefaultAppearanceOrFormDefault(
                    doc.ResolveObject(field.GetInherited(PdfName.DA, o => o is PdfString)), doc),
            };

            ReadValues(field, doc, read, info);
            ReadOptions(field, doc, read, info);
            ReadWidgets(field, doc, read, info);
            return info;
        }

        /// <summary>An array element, following a reference — §7.3.10 covers elements too.</summary>
        // One implementation, in PdfResolve.ArrayDouble. This existed twice, byte-identical down to
        // the doc comment above it — and neither copy bounds-checked, so a short /Rect threw
        // IndexOutOfRangeException out of a public form call.
        private static double Num(PdfDocument doc, PdfArray arr, int index)
            => PdfResolve.ArrayDouble(doc, arr, index);

        private static void ReadValues(PdfFormField field, PdfDocument doc, FormRead read, PdfFormFieldInfo info)
        {
            var value = doc.ResolveObject(field.Value);

            switch (value)
            {
                case PdfString s:
                    info.Value = s.GetText();
                    info.SelectedValuesList.Add(info.Value);
                    break;
                case PdfName n:
                    info.Value = n.Value;
                    info.SelectedValuesList.Add(info.Value);
                    break;
                case PdfArray array:
                    // A multi-select list box stores every selection; an array several fields
                    // name is read once and its list shared.
                    if (!read.Selections.TryGetValue(array, out List<string> selected))
                    {
                        selected = new List<string>();
                        for (int i = 0; i < array.Count; i++)
                            if (doc.ResolveObject(array[i]) is PdfString item)
                                selected.Add(item.GetText());
                        read.Selections[array] = selected;
                    }
                    info.SelectedValuesList = selected;
                    info.Value = selected.Count > 0 ? selected[0] : null;
                    break;
            }

            var defaultValue = doc.ResolveObject(field.DefaultValue);
            info.DefaultValue = defaultValue switch
            {
                PdfString s => s.GetText(),
                PdfName n => n.Value,
                _ => null,
            };

            // A button is on when its value names a state other than /Off.
            info.IsChecked = info.FieldType == PdfFieldType.Button &&
                             info.Value != null &&
                             !string.Equals(info.Value, PdfName.Off.Value, StringComparison.Ordinal);
        }

        private static void ReadOptions(PdfFormField field, PdfDocument doc, FormRead read, PdfFormFieldInfo info)
        {
            if (field.FieldType != PdfFieldType.Choice) return;

            var options = doc.ResolveObject(field.Dictionary[PdfName.Opt]) as PdfArray;
            if (options == null) return;

            // An /Opt array several fields name is read once, and its list shared.
            if (read.Options.TryGetValue(options, out List<ChoiceOption> shared))
            {
                info.OptionList = shared;
                return;
            }

            for (int i = 0; i < options.Count; i++)
            {
                var entry = doc.ResolveObject(options[i]);
                if (entry is PdfString display)
                {
                    info.OptionList.Add(new ChoiceOption(display.GetText()));
                    continue;
                }

                // [ (export) (display) ] — the export value is what /V holds.
                if (entry is PdfArray pair && pair.Count >= 2)
                {
                    string export = (doc.ResolveObject(pair[0]) as PdfString)?.GetText();
                    string text = (doc.ResolveObject(pair[1]) as PdfString)?.GetText();
                    if (text != null) info.OptionList.Add(new ChoiceOption(text, export));
                }
            }
            read.Options[options] = info.OptionList;
        }

        private static void ReadWidgets(PdfFormField field, PdfDocument doc, FormRead read, PdfFormFieldInfo info)
        {
            IReadOnlyDictionary<int, int> pageByAnnotation = read.PageByAnnotation;
            foreach (var widget in field.WidgetDictionaries)
            {
                // A widget a second field names is the first field's.
                if (!read.Widgets.Add(widget)) continue;

                // Resolved. Unresolved, x1..y2 stay at 0 and the widget is reported at the page
                // origin with zero size — indistinguishable from a genuinely degenerate one. This
                // is a documented public entry point, so a caller drawing an overlay, hit-testing
                // a click or exporting field geometry places every widget of that field wrongly.
                var rect = PdfResolve.Array(doc, widget, PdfName.Rect);
                double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                if (rect != null && rect.Count >= 4)
                {
                    x1 = Num(doc, rect, 0);
                    y1 = Num(doc, rect, 1);
                    x2 = Num(doc, rect, 2);
                    y2 = Num(doc, rect, 3);
                }

                int pageIndex = -1;
                if (widget.ObjectNumber > 0 && pageByAnnotation.TryGetValue(widget.ObjectNumber, out int p))
                    pageIndex = p;

                var w = new PdfFormWidgetInfo
                {
                    PageIndex = pageIndex,
                    X = Math.Min(x1, x2),
                    Y = Math.Min(y1, y2),
                    Width = Math.Abs(x2 - x1),
                    Height = Math.Abs(y2 - y1),
                    // Resolved. GetName does not follow a reference, so an indirect /AS reported
                    // no appearance state at all — the same non-resolving-read class as /Rect
                    // above, and the one PdfFormFlattener records as able to invert a checkbox.
                    AppearanceState = PdfResolve.Name(doc, widget, PdfName.AS)?.Value,
                    // A flag word: the low 32 bits, never IntValue, which saturates (CLAUDE.md).
                    Flags = PdfResolve.Number(doc, widget, PdfName.F)?.FlagWord ?? 0,
                };
                if (doc.ResolveObject(widget[PdfName.MK]) is PdfDictionary mk)
                {
                    w.NormalCaption   = (doc.ResolveObject(mk[PdfName.MKCaption]) as PdfString)?.GetText();
                    w.DownCaption     = (doc.ResolveObject(mk[PdfName.AC]) as PdfString)?.GetText();
                    w.RolloverCaption = (doc.ResolveObject(mk[PdfName.RC]) as PdfString)?.GetText();
                }

                // §12.5.5: /N is EITHER one appearance stream (no states) OR a state
                // sub-dictionary. Asking only `as PdfDictionary` — PdfStream DERIVES from
                // PdfDictionary — the single-stream case would match too and this loop publish the
                // stream's own keys (/Length, /Filter, /BBox, /Resources,
                // /Type, /Subtype) as though they were appearance states. Every text field with
                // an appearance stream reported six nonsense "on states".
                var states = NormalAppearance.GetStates(doc, widget);
                if (states != null)
                {
                    // A state dictionary several widgets name is read once and its list shared, at
                    // most MaxAppearanceStatesPerWidget names.
                    if (!read.States.TryGetValue(states, out List<string> onStates))
                    {
                        onStates = new List<string>();
                        foreach (var kvp in states)
                        {
                            if (kvp.Key == PdfName.Off) continue;
                            if (onStates.Count == MaxAppearanceStatesPerWidget) break;
                            onStates.Add(kvp.Key.Value);
                        }
                        read.States[states] = onStates;
                    }
                    w.OnStateList = onStates;
                    // A set beside the list: a field's kids are the file's, and a search of the
                    // list before each addition cost their states squared.
                    foreach (string state in onStates) info.OnStateSet.Add(state);
                }

                info.WidgetList.Add(w);
            }
        }

        /// <summary>
        /// Maps a widget's object number to the page it is annotated on — through the one annotation
        /// walk, so the entries the pages name are bounded together (pages may share one array); a
        /// widget past that ceiling has no page in the map, as one no page lists.
        /// </summary>
        private static IReadOnlyDictionary<int, int> MapAnnotationsToPages(PdfDocument doc)
        {
            var map = new Dictionary<int, int>();
            foreach (var (pageIndex, entry) in Extraction.AnnotationSubtypeWalk.Entries(doc))
                if (entry is PdfReference reference)
                    map[reference.ReferenceObjectNumber] = pageIndex;
            return map;
        }
    }

    /// <summary>One terminal AcroForm field, as data.</summary>
    public sealed class PdfFormFieldInfo
    {
        // Shared with every field that names the same /Opt or /V array (FormRead): read, never
        // added to after the read that made them.
        internal List<ChoiceOption> OptionList = new List<ChoiceOption>();
        internal readonly Tetitulz.Pdf.Core.Util.DistinctStrings OnStateSet = new Tetitulz.Pdf.Core.Util.DistinctStrings();
        internal List<string> SelectedValuesList = new List<string>();
        internal readonly List<PdfFormWidgetInfo> WidgetList = new List<PdfFormWidgetInfo>();

        /// <summary>
        /// The name to pass to <see cref="PdfFormFiller"/> — partial names joined by <c>.</c> from
        /// the root of the field tree.
        /// </summary>
        public string FullyQualifiedName { get; internal set; }

        /// <summary>This node's own <c>/T</c>, without its ancestors.</summary>
        public string PartialName { get; internal set; }

        /// <summary>The <c>/TU</c> alternate name — the tooltip, and what a screen reader announces.</summary>
        public string AlternateName { get; internal set; }

        /// <summary>Text, button, choice or signature (<c>/FT</c>, resolved through <c>/Parent</c>).</summary>
        public PdfFieldType FieldType { get; internal set; }

        /// <summary>The current value as text: a string value, or a button's state name.</summary>
        public string Value { get; internal set; }

        /// <summary>Every selection of a multi-select list box; one entry, or none, for other fields.</summary>
        public IReadOnlyList<string> SelectedValues => SelectedValuesList;

        /// <summary>The <c>/DV</c> default value, if any.</summary>
        public string DefaultValue { get; internal set; }

        /// <summary>Raw <c>/Ff</c> bits — see <see cref="PdfFieldFlags"/>.</summary>
        public int Flags { get; internal set; }

        /// <summary>True when the field is a check box or radio button whose value is not <c>/Off</c>.</summary>
        public bool IsChecked { get; internal set; }

        /// <summary>
        /// The appearance states this field's widgets can display, <c>/Off</c> excluded. For a check
        /// box or radio group these are exactly the values <see cref="PdfFormFiller"/> accepts.
        /// </summary>
        public IReadOnlyList<string> OnStates => OnStateSet.Items;

        /// <summary>The <c>/Opt</c> entries of a choice field; empty for other types.</summary>
        public IReadOnlyList<ChoiceOption> Options => OptionList;

        /// <summary>Where this field appears; a field may have several widgets (a radio group always does).</summary>
        public IReadOnlyList<PdfFormWidgetInfo> Widgets => WidgetList;

        /// <summary>The inherited <c>/MaxLen</c>, or 0 when unlimited.</summary>
        public int MaxLength { get; internal set; }

        /// <summary>
        /// The inherited <c>/Q</c> justification, else the form-wide <c>/AcroForm /Q</c> — see
        /// <see cref="PdfQuadding"/>.
        /// </summary>
        public int Quadding { get; internal set; }

        /// <summary>
        /// The inherited <c>/DA</c> string, e.g. <c>/Helv 9 Tf 0 g</c>, else the form-wide
        /// <c>/AcroForm /DA</c> — the string the filler draws the value with.
        /// </summary>
        public string DefaultAppearance { get; internal set; }

        /// <summary>
        /// The font resource <see cref="DefaultAppearance"/> names (its <c>Tf</c> operand, <c>Helv</c>
        /// for <c>/Helv 8 Tf</c>), or <c>null</c> when it names none.
        /// </summary>
        public string DefaultAppearanceFont => DefaultAppearanceString.Parse(DefaultAppearance)?.FontResourceName;

        /// <summary>
        /// The font size <see cref="DefaultAppearance"/> sets — <c>0</c> for an auto-sized field — or
        /// <c>null</c> when it sets none.
        /// </summary>
        public float? DefaultAppearanceFontSize => DefaultAppearanceString.Parse(DefaultAppearance)?.FontSize;

        /// <summary>The user may not change this field.</summary>
        public bool IsReadOnly => PdfFieldFlags.Has(Flags, PdfFieldFlags.ReadOnly);
        /// <summary>The field must hold a value when the form is submitted.</summary>
        public bool IsRequired => PdfFieldFlags.Has(Flags, PdfFieldFlags.Required);
        /// <summary>A text field that accepts more than one line.</summary>
        public bool IsMultiline => FieldType == PdfFieldType.Text &&
                                   PdfFieldFlags.Has(Flags, PdfFieldFlags.Multiline);
        /// <summary>A text field whose value must not be echoed.</summary>
        public bool IsPassword => FieldType == PdfFieldType.Text &&
                                  PdfFieldFlags.Has(Flags, PdfFieldFlags.Password);
        /// <summary>A text field laid out as <see cref="MaxLength"/> equally spaced cells.</summary>
        public bool IsComb => FieldType == PdfFieldType.Text && PdfFieldFlags.Has(Flags, PdfFieldFlags.Comb);
        /// <summary>A radio button group rather than a check box.</summary>
        public bool IsRadio => FieldType == PdfFieldType.Button && PdfFieldFlags.Has(Flags, PdfFieldFlags.Radio);
        /// <summary>A push button, which holds no value.</summary>
        public bool IsPushButton => FieldType == PdfFieldType.Button &&
                                    PdfFieldFlags.Has(Flags, PdfFieldFlags.Pushbutton);
        /// <summary>A drop-down rather than a scrolling list box.</summary>
        public bool IsComboBox => FieldType == PdfFieldType.Choice && PdfFieldFlags.Has(Flags, PdfFieldFlags.Combo);
        /// <summary>A list box that accepts more than one selection.</summary>
        public bool IsMultiSelect => FieldType == PdfFieldType.Choice &&
                                     PdfFieldFlags.Has(Flags, PdfFieldFlags.MultiSelect);

        /// <inheritdoc/>
        public override string ToString() =>
            FieldType + ": " + FullyQualifiedName + (Value == null ? string.Empty : " = " + Value);
    }

    /// <summary>One widget annotation of a field — where it is drawn.</summary>
    public sealed class PdfFormWidgetInfo
    {
        // Shared with every widget that names the same state dictionary: read, never added to.
        internal List<string> OnStateList = new List<string>();

        /// <summary>Zero-based page index, or <c>-1</c> when the widget is on no page's <c>/Annots</c>.</summary>
        public int PageIndex { get; internal set; }
        /// <summary>Lower-left X of <c>/Rect</c>, in points from the page's lower-left corner.</summary>
        public double X { get; internal set; }
        /// <summary>Lower-left Y of <c>/Rect</c>, in points from the page's lower-left corner.</summary>
        public double Y { get; internal set; }
        /// <summary>Width of <c>/Rect</c> in points.</summary>
        public double Width { get; internal set; }
        /// <summary>Height of <c>/Rect</c> in points.</summary>
        public double Height { get; internal set; }
        /// <summary>The widget's currently selected <c>/AS</c> appearance state, if it has one.</summary>
        public string AppearanceState { get; internal set; }
        /// <summary>
        /// The widget's annotation flags (<c>/F</c>, ISO 32000-2 Table 167), 0 when absent — read as
        /// a flag word, so an unsigned spelling keeps its bits. <see cref="PdfAnnotationFlags"/> names them.
        /// </summary>
        public int Flags { get; internal set; }
        /// <summary>
        /// Whether the flags keep the widget off a screen — Hidden or NoView (Table 167: not displayed,
        /// no interaction) — so a viewer neither draws nor edits it. The optional content a widget may
        /// belong to is not consulted here.
        /// </summary>
        public bool IsHiddenOnScreen => PdfAnnotationFlags.IsHiddenOnScreen(Flags);
        /// <summary>A button's normal caption, <c>/MK /CA</c> (Table 192), or <c>null</c>.</summary>
        public string NormalCaption { get; internal set; }
        /// <summary>A button's down caption, <c>/MK /AC</c>, or <c>null</c>.</summary>
        public string DownCaption { get; internal set; }
        /// <summary>A button's rollover caption, <c>/MK /RC</c>, or <c>null</c>.</summary>
        public string RolloverCaption { get; internal set; }
        /// <summary>
        /// The non-<c>/Off</c> states this particular widget can display — at most 256 of them, the
        /// most one appearance dictionary is read for.
        /// </summary>
        public IReadOnlyList<string> OnStates => OnStateList;
    }
}
