// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Text;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= form controls

        /// <summary>
        /// Renders a form control as static graphics and returns <c>true</c> when
        /// <paramref name="tag"/> was one.
        ///
        /// <para><b>Static, not AcroForm fields — deliberately.</b> Chromium's own
        /// <c>page.pdf()</c> renders form controls as flattened graphics, so emitting real fields
        /// here would make the managed and browser engines <i>disagree</i> about what the same
        /// HTML produces. Matching the browser is the point of having two engines at all.</para>
        /// </summary>
        private bool MapFormControl(IElement el, string tag, M.Paragraph para, Style st)
        {
            if (_interactiveFormFields && TryMapFormField(el, tag, para, st)) return true;

            // A control the author gave a border or padding (Bootstrap's .form-control) is drawn in
            // that box, not in the plain shading that stands in for the browser's default look.
            if (tag == HtmlTags.Input || tag == HtmlTags.TextArea || tag == HtmlTags.Select)
                st = ControlStyle(el, st);

            switch (tag)
            {
                case HtmlTags.Input:
                    MapInput(el, para, st);
                    return true;

                case HtmlTags.TextArea:
                    // The element's text content IS its value, which is why letting the generic
                    // walk reach it spills the default into the document as prose.
                    AppendFieldBox(para, st, el.TextContent ?? "");
                    return true;

                case HtmlTags.Select:
                    AppendFieldBox(para, st, SelectedOptionText(el));
                    return true;

                // Never rendered on their own: <option> text is reachable only through the
                // <select> above, and <optgroup> only labels them.
                case HtmlTags.Option:
                case HtmlTags.OptGroup:
                    return true;

                default:
                    return false;
            }
        }

        private void MapInput(IElement el, M.Paragraph para, Style st)
        {
            string type = (el.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
            switch (type)
            {
                case "hidden":
                    return;                                   // no visual representation at all

                case "checkbox":
                    AppendGlyph(para, st, el.HasAttribute("checked") ? "☑" : "☐");
                    return;

                case "radio":
                    AppendGlyph(para, st, el.HasAttribute("checked") ? "◉" : "○");
                    return;

                default:
                    AppendFieldBox(para, st, InputText(el, type));
                    return;
            }
        }

        /// <summary>
        /// The text a static <c>&lt;input&gt;</c> of <paramref name="type"/> shows — inline or as a
        /// block (<see cref="MapBlockControl"/>) alike.
        /// </summary>
        /// <remarks>
        /// A password's stored value is echoed as bullets, never in clear text: a PDF is a durable
        /// artefact, and printing a password because the markup happened to carry one would be a
        /// disclosure the author did not ask for. A button's caption is its <c>value</c>, or the
        /// type's conventional default; an image or file input is an empty box.
        /// </remarks>
        private static string InputText(IElement el, string type) => type switch
        {
            "submit" => el.GetAttribute("value") ?? "Submit",
            "reset" => el.GetAttribute("value") ?? "Reset",
            "button" => el.GetAttribute("value") ?? "",
            "password" => new string('•', Math.Min((el.GetAttribute("value") ?? "").Length, 32)),
            "image" or "file" => "",
            _ => el.GetAttribute("value") ?? "",
        };

        /// <summary>
        /// Whether <paramref name="el"/> is a text-like control its CSS makes a block —
        /// Bootstrap's <c>.form-control</c> is <c>display: block; width: 100%</c> — in a static
        /// conversion. Such a control is laid out as its own box (<see cref="MapBlockControl"/>):
        /// drawn inline, a run of text in an inline box, it could take neither the width of its
        /// column nor a textarea's rows.
        /// </summary>
        private bool IsBlockControl(IElement el)
        {
            if (_interactiveFormFields) return false;
            string tag = el.LocalName.ToLowerInvariant();
            bool textLike = tag == HtmlTags.TextArea || tag == HtmlTags.Select
                || (tag == HtmlTags.Input
                    && (el.GetAttribute("type") ?? "text").Trim().ToLowerInvariant() is not
                        ("hidden" or "checkbox" or "radio" or "submit" or "reset" or "button" or "image" or "file"));
            return textLike && IsBlockDisplay(StyleValue(el, "display"));
        }

        /// <summary>
        /// A block-level text control as a paragraph in its own box — its CSS margins, border,
        /// padding, background and corners, the full width of its column — holding the text it
        /// shows; a textarea keeps its line breaks and is at least its <c>rows</c> lines tall (HTML's
        /// default 2).
        /// </summary>
        private void MapBlockControl(IElement el, M.Section sec, List<M.IDocElement> sink,
            Style inherited, M.TextAlignment? align)
        {
            var st = DeriveStyle(inherited, el, null, false);
            var p = NewPara(el, sec, align, st);
            ApplyBlockStyle(el, p, st.Size);

            string tag = el.LocalName.ToLowerInvariant();
            bool textArea = tag == HtmlTags.TextArea;
            string text = textArea ? el.TextContent ?? ""
                : tag == HtmlTags.Select ? SelectedOptionText(el)
                : InputText(el, (el.GetAttribute("type") ?? "text").Trim().ToLowerInvariant());

            string[] lines = textArea
                ? text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n')
                : new[] { text.Trim() };
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) p.Runs.Add(new M.LineBreak());
                if (lines[i].Length > 0) p.Runs.Add(MakeRun(lines[i], st));
            }
            if (p.Runs.Count == 0) p.Runs.Add(MakeRun("", st));

            if (textArea)
            {
                double line = st.LineHeight > 0 ? st.LineHeight : st.Size * NormalLineHeightFactor;
                double extra = Math.Max(0, SizeAttribute(el, "rows", DefaultTextAreaRows) - lines.Length) * line;
                p.BoxInsetBottom += extra;
                p.SpacingAfter += extra;
            }
            sink.Add(p);
        }

        /// <summary>A textarea's height in lines when it declares no <c>rows</c> — HTML's own default.</summary>
        private const int DefaultTextAreaRows = 2;

        /// <summary>
        /// CSS's <c>line-height: normal</c> as a multiple of the font size: the ascent, descent and
        /// gap of the fonts a browser prints the standard faces in (Arial 1.149, Times 1.150 of the
        /// em), where the exporter's own default floors every line at 14pt.
        /// </summary>
        internal const double NormalLineHeightFactor = 1.15;

        /// <summary>
        /// The text of the option a browser would show: the one marked <c>selected</c>, else the
        /// first. Emitting all of them — which the generic walk did — turns a one-line control
        /// into a paragraph listing every choice.
        /// </summary>
        private static string SelectedOptionText(IElement select)
        {
            IElement? first = null;
            foreach (var option in select.QuerySelectorAll(HtmlTags.Option))
            {
                first ??= option;
                if (option.HasAttribute("selected")) return (option.TextContent ?? "").Trim();
            }
            return (first?.TextContent ?? "").Trim();
        }

        /// <summary>
        /// <paramref name="st"/>, with the control's own inline box when it declares one — and then
        /// no shading, because the box is the control's look (an empty highlight, which the
        /// <c>??=</c> in <see cref="AppendFieldBox"/> leaves alone).
        /// </summary>
        private Style ControlStyle(IElement el, Style st)
        {
            M.InlineBox? box = ResolveInlineBox(el, st.Size);
            if (box == null) return st;
            var boxed = st.Clone();
            boxed.Box = box;
            boxed.Highlight = "";
            return boxed;
        }

        /// <summary>Draws a control's value between light bracket glyphs, so it reads as a field rather than prose.</summary>
        private static void AppendFieldBox(M.Paragraph para, Style st, string value)
        {
            var boxed = st.Clone();
            boxed.Highlight ??= FormFieldShading;
            para.Runs.Add(MakeRun(" " + (value ?? "").Trim() + " ", boxed));
        }

        private static void AppendGlyph(M.Paragraph para, Style st, string glyph)
            => para.Runs.Add(MakeRun(glyph, st));

        /// <summary>Background behind a static form field, so its extent is visible when empty.</summary>
        private const string FormFieldShading = "#EFEFEF";

        // ============================================================= interactive fields

        /// <summary>A text input's width in characters when it declares no <c>size</c> — HTML's own default.</summary>
        private const int DefaultInputSizeChars = 20;

        /// <summary>A textarea's width in characters when it declares no <c>cols</c> — HTML's own default.</summary>
        private const int DefaultTextAreaCols = 20;

        /// <summary>The widest placeholder any control gets, in characters, so a hostile <c>size</c> cannot push one off the page.</summary>
        private const int MaxPlaceholderChars = 80;

        /// <summary>
        /// The field names already issued in this conversion. §12.7.4.2 reads two terminal fields
        /// under one fully qualified name as one field, so a second control reusing a name — which
        /// HTML allows and a checkbox group does routinely — is renamed rather than merged.
        /// </summary>
        private readonly HashSet<string> _fieldNames = new(StringComparer.Ordinal);

        /// <summary>The control kinds already reported as left static, so each is reported once.</summary>
        private readonly HashSet<string> _staticControlsReported = new(StringComparer.Ordinal);

        /// <summary>
        /// The next numeric suffix to try for each colliding field base name, and for each
        /// (radio group, base value). A run of N controls sharing a name probed <c>name_2</c>,
        /// <c>name_3</c> … from 2 every time — O(N²) string builds and set look-ups (minutes for
        /// 100 000 inputs). Starting each probe past the last suffix issued makes it O(1) each, and
        /// since names are only ever added, never removed, the suffix chosen is identical.
        /// </summary>
        private readonly Dictionary<string, int> _nextFieldSuffix = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Group, string Base), int> _nextRadioValueSuffix = new(RadioValueKeys);

        /// <summary>The page's own names, hashed under a key it cannot see (<see cref="KeyedHash"/>).</summary>
        private static readonly IEqualityComparer<(string Group, string Base)> RadioValueKeys =
            KeyedHash.Comparer<(string Group, string Base)>(k => unchecked(KeyedHash.Of(k.Group) * 31 + KeyedHash.Of(k.Base)));

        /// <summary>
        /// Maps a control to a form-field placeholder run and returns <c>true</c>, or returns
        /// <c>false</c> for a control that stays static graphics (the caller then draws it as
        /// always). Reached only in interactive mode.
        /// </summary>
        /// <remarks>
        /// <para><b>The placeholder is no-break spaces in Courier.</b> No-break spaces are one
        /// token to the layout, so a field cannot wrap across lines, and they draw nothing, so the
        /// value appears once — in the field's appearance — rather than once on the page and again
        /// on top of it. Courier because every character in it, U+00A0 included, advances exactly
        /// 0.6 em: the width is then exact without this mapper measuring anything, which would mean
        /// restating the exporter's missing-glyph rule here.</para>
        /// </remarks>
        private bool TryMapFormField(IElement el, string tag, M.Paragraph para, Style st)
        {
            M.FormFieldSpec? spec;
            int widthChars;

            switch (tag)
            {
                case HtmlTags.Input:
                {
                    string type = (el.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
                    switch (type)
                    {
                        case "hidden":
                            return true;                                  // nothing to draw or fill

                        case "checkbox":
                            spec = new M.FormFieldSpec { Kind = M.FormFieldKind.Checkbox, IsChecked = el.HasAttribute("checked") };
                            widthChars = 2;
                            break;

                        case "radio":
                            spec = RadioSpec(el);
                            widthChars = 2;
                            break;

                        case "submit": case "reset": case "button": case "image": case "file":
                            ReportLeftStatic("button, file and image inputs", "they carry no value to edit");
                            return false;

                        case "password":
                            // Never with a value: ISO 32000-2 Table 229 bit 14 forbids storing it.
                            spec = new M.FormFieldSpec { Kind = M.FormFieldKind.Text, Password = true };
                            widthChars = SizeAttribute(el, "size", DefaultInputSizeChars);
                            break;

                        default:
                            spec = new M.FormFieldSpec { Kind = M.FormFieldKind.Text, Value = el.GetAttribute("value") };
                            widthChars = SizeAttribute(el, "size", DefaultInputSizeChars);
                            break;
                    }
                    spec.MaxLength = SizeAttribute(el, "maxlength", 0);
                    break;
                }

                case HtmlTags.TextArea:
                    spec = new M.FormFieldSpec
                    {
                        Kind = M.FormFieldKind.Text,
                        Multiline = true,
                        Value = el.TextContent ?? "",
                    };
                    widthChars = SizeAttribute(el, "cols", DefaultTextAreaCols);
                    break;

                case HtmlTags.Select:
                {
                    bool multiple = el.HasAttribute("multiple");
                    bool list = multiple || SizeAttribute(el, "size", 1) > 1;
                    spec = new M.FormFieldSpec
                    {
                        Kind = list ? M.FormFieldKind.ListBox : M.FormFieldKind.ComboBox,
                        MultiSelect = multiple,
                    };
                    int longest = 0;
                    string? selected = null, first = null;
                    foreach (var option in el.QuerySelectorAll(HtmlTags.Option))
                    {
                        string text = (option.TextContent ?? "").Trim();
                        string value = option.GetAttribute("value") ?? text;
                        spec.Options.Add(new M.FormFieldOption(value, text));
                        longest = Math.Max(longest, text.Length);
                        first ??= value;
                        if (selected == null && option.HasAttribute("selected")) selected = value;
                    }
                    // What a browser shows: the option marked selected, else — for a drop-down —
                    // the first. A list box shows no selection until one is made.
                    spec.Value = selected ?? (list ? null : first);
                    widthChars = longest + 3;                         // room for the drop-down arrow
                    break;
                }

                case HtmlTags.Option:
                case HtmlTags.OptGroup:
                    return true;                                      // reached through their <select>

                default:
                    return false;
            }

            // A radio button's name is its group's, already settled by RadioSpec.
            if (spec.Kind != M.FormFieldKind.RadioButton) spec.Name = FieldNameFor(el, spec.Kind);
            spec.ReadOnly = el.HasAttribute("readonly") || el.HasAttribute("disabled");

            var boxed = st.Clone();
            boxed.Highlight ??= FormFieldShading;
            var run = MakeRun(new string(' ', Math.Max(1, Math.Min(widthChars, MaxPlaceholderChars))), boxed);
            // Nothing inherited may change the placeholder's width or draw on it: the width is the
            // field's, and the field's appearance is the only thing that should show inside it.
            run.FontName = StandardFonts.Courier;
            run.IsBold = run.IsItalic = false;
            run.IsUnderline = run.IsStrikethrough = false;
            run.IsAllCaps = run.IsSmallCaps = false;
            run.CharSpacing = 0;
            run.VerticalAlign = M.VerticalAlign.Baseline;
            run.LinkUri = null;
            run.FormField = spec;
            para.Runs.Add(run);
            return true;
        }

        /// <summary>The radio groups seen so far: the markup's <c>name</c> → the field name the group took.</summary>
        private readonly Dictionary<string, string> _radioGroups = new(StringComparer.Ordinal);

        /// <summary>Each radio group's export values so far, so no two of its buttons share a state.</summary>
        private readonly Dictionary<string, HashSet<string>> _radioValues = new(StringComparer.Ordinal);

        /// <summary>Each radio group's currently checked button, so the last <c>checked</c> wins.</summary>
        private readonly Dictionary<string, M.FormFieldSpec> _checkedRadio = new(StringComparer.Ordinal);

        /// <summary>
        /// One radio button: the group it belongs to, its export value, and whether it is chosen.
        /// </summary>
        /// <remarks>
        /// <para><b>Radios sharing a <c>name</c> are one group</b>, and the group is the field: the
        /// first button claims a field name through <see cref="FieldNameFor"/> — so a text field
        /// already called <c>size</c> pushes the group to <c>size_2</c> rather than merging with it —
        /// and the rest of the group reuse it. A radio with no <c>name</c> is a group of its own, as
        /// in HTML.</para>
        ///
        /// <para><b>A button's value is its on-state name</b>, so within a group the values must be
        /// distinct and none may be <c>Off</c>, which §12.7.4.2.3 reserves. A repeat or an
        /// <c>Off</c> gets a numeric suffix and a report — two buttons sharing a state would be
        /// chosen together. A radio with no <c>value</c> takes HTML's own default, <c>on</c>.</para>
        ///
        /// <para><b>The last <c>checked</c> in a group wins</b>, as it does in a browser, which
        /// un-checks an earlier one.</para>
        /// </remarks>
        private M.FormFieldSpec RadioSpec(IElement el)
        {
            string? htmlName = el.GetAttribute("name");
            string groupName;
            if (string.IsNullOrWhiteSpace(htmlName))
                groupName = FieldNameFor(el, M.FormFieldKind.RadioButton);
            else if (!_radioGroups.TryGetValue(htmlName!, out groupName!))
                _radioGroups[htmlName!] = groupName = FieldNameFor(el, M.FormFieldKind.RadioButton);

            if (!_radioValues.TryGetValue(groupName, out var used))
                _radioValues[groupName] = used = new HashSet<string>(StringComparer.Ordinal);

            string written = el.GetAttribute("value") ?? "";
            string baseValue = written.Length == 0 ? "on" : written;
            string state = baseValue;
            if (state == Tetitulz.Pdf.Core.Objects.PdfName.Off.Value || used.Contains(state))
            {
                var key = (groupName, baseValue);
                int n = _nextRadioValueSuffix.TryGetValue(key, out int next) ? next : 2;
                while (true)
                {
                    state = baseValue + "_" + n.ToString(CultureInfo.InvariantCulture);
                    if (state != Tetitulz.Pdf.Core.Objects.PdfName.Off.Value && !used.Contains(state)) break;
                    n++;
                }
                _nextRadioValueSuffix[key] = n + 1;
            }
            used.Add(state);
            if (!string.Equals(state, baseValue, StringComparison.Ordinal))
                ReportApproximation($"radio button '{Excerpt(baseValue)}' in the group '{groupName}' became " +
                                    $"'{state}': a button's value is its on state, so two in one group cannot " +
                                    "share it and none may be 'Off'.");

            var spec = new M.FormFieldSpec { Kind = M.FormFieldKind.RadioButton, Name = groupName, Value = state };
            if (el.HasAttribute("checked"))
            {
                if (_checkedRadio.TryGetValue(groupName, out var earlier)) earlier.IsChecked = false;
                spec.IsChecked = true;
                _checkedRadio[groupName] = spec;
            }
            return spec;
        }

        /// <summary>A non-negative integer attribute, or <paramref name="fallback"/> when absent or unreadable.</summary>
        private static int SizeAttribute(IElement el, string name, int fallback)
            => int.TryParse(el.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 0
                ? n : fallback;

        /// <summary>
        /// The control's field name — its <c>name</c>, else its <c>id</c>, else one made from its
        /// kind — made legal and unique.
        /// </summary>
        /// <remarks>
        /// A period becomes an underscore: §12.7.4.2 uses it to separate the levels of a fully
        /// qualified name, so <c>name="address.city"</c> would otherwise address a field
        /// <c>city</c> under a parent <c>address</c> that does not exist. A repeated name gets a
        /// numeric suffix and a report, because the caller submitting the form will see a name the
        /// markup did not use.
        /// </remarks>
        private string FieldNameFor(IElement el, M.FormFieldKind kind)
        {
            string? declared = el.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(declared)) declared = el.GetAttribute("id");
            string baseName = string.IsNullOrWhiteSpace(declared)
                ? kind.ToString().ToLowerInvariant()
                : declared!.Trim().Replace('.', '_');

            string name = baseName;
            if (!_fieldNames.Add(name))
            {
                int n = _nextFieldSuffix.TryGetValue(baseName, out int next) ? next : 2;
                while (!_fieldNames.Add(name = baseName + "_" + n.ToString(CultureInfo.InvariantCulture))) n++;
                _nextFieldSuffix[baseName] = n + 1;
            }

            if (!string.IsNullOrWhiteSpace(declared) && !string.Equals(name, declared!.Trim(), StringComparison.Ordinal))
                ReportApproximation($"form control '{Excerpt(declared!)}' became the field '{name}': a PDF field " +
                                    "name cannot contain a period, and two fields cannot share one.");
            return name;
        }

        /// <summary>Reports, once per kind, a control that stays a drawing rather than becoming a field.</summary>
        private void ReportLeftStatic(string what, string why)
        {
            if (!_staticControlsReported.Add(what)) return;
            ReportApproximation($"form fields: {what} were drawn as static graphics, not made fields — {why}.");
        }
    }
}
