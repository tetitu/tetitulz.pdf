// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.JavaScript
{
    /// <summary>
    /// The one way <c>AcrobatObjectModel.js</c> reaches the host: field values and descriptions, and
    /// the places a script's alerts, console lines and unsupported requests are kept.
    /// </summary>
    /// <remarks>
    /// <para><b>Strings, numbers and booleans only, in and out</b>, so no CLR object reaches a script;
    /// the object model captures this bridge in a closure and deletes its global before any document
    /// script runs.</para>
    /// <para><b>Everything a script can make it keep is capped</b> — the alerts, the console lines,
    /// each one's length — because the script decides how many times it is called.</para>
    /// </remarks>
    internal sealed class JsHost
    {
        /// <summary>The alerts kept for one context; past it, one note counts the rest.</summary>
        internal const int MaxAlerts = 256;

        /// <summary>The console lines kept for one context; past it, one note counts the rest.</summary>
        internal const int MaxConsoleLines = 1024;

        /// <summary>The characters of one alert or console line kept; the rest are cut.</summary>
        internal const int MaxMessageChars = 4096;

        private PdfJsExecutionContext _ctx;
        private int _alertsDropped;
        private int _consoleDropped;
        private string[] _names;

        /// <summary>The field writes of the current execution, applied after its event's value.</summary>
        internal readonly Dictionary<string, string> PendingValues = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Points the bridge at the context the next script runs for.</summary>
        internal void Begin(PdfJsExecutionContext ctx)
        {
            _ctx = ctx;
            _names = null;
            _alertsDropped = 0;
            _consoleDropped = 0;
            PendingValues.Clear();
            ResultValue = null;
            ResultChange = string.Empty;
            ResultRc = true;
        }

        public int NumPages() => _ctx.NumPages;

        public bool KnowsFields() => _ctx.FieldDescriptions.Count > 0 || _ctx.FieldValues.Count > 0;

        public bool HasField(string name) =>
            name != null && (_ctx.FieldDescriptions.ContainsKey(name) || _ctx.FieldValues.ContainsKey(name));

        /// <summary>The document's field names in ordinal order, as a JSON array — Acrobat numbers them alphabetically.</summary>
        public string FieldNames()
        {
            if (_names == null)
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string n in _ctx.FieldDescriptions.Keys) set.Add(n);
                foreach (string n in _ctx.FieldValues.Keys) set.Add(n);
                _names = new string[set.Count];
                set.CopyTo(_names);
            }
            var sb = new StringBuilder("[");
            for (int i = 0; i < _names.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendJsonString(sb, _names[i]);
            }
            return sb.Append(']').ToString();
        }

        /// <summary>The value a field holds now — a write earlier in this script first — or null.</summary>
        public string GetValue(string name)
        {
            if (name == null) return null;
            if (PendingValues.TryGetValue(name, out string pending)) return pending;
            if (_ctx.ModifiedFieldValues.TryGetValue(name, out string modified)) return modified;
            return _ctx.FieldValues.TryGetValue(name, out string value) ? value : null;
        }

        public void SetValue(string name, string value)
        {
            if (name == null) return;
            PendingValues[name] = value ?? string.Empty;
        }

        /// <summary>A field's description as JSON, or null when the document does not describe it.</summary>
        public string Describe(string name)
        {
            if (name == null || !_ctx.FieldDescriptions.TryGetValue(name, out PdfFormFieldInfo f) || f == null) return null;

            var listing = new Listing();
            var sb = new StringBuilder(256);
            sb.Append("{\"type\":");
            AppendJsonString(sb, TypeName(f));
            sb.Append(",\"flags\":").Append(f.Flags.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"maxLength\":").Append(f.MaxLength.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"defaultValue\":");
            listing.Text(sb, f.DefaultValue);
            sb.Append(",\"userName\":");
            listing.Text(sb, f.AlternateName);
            sb.Append(",\"terminal\":true,\"fontResource\":");
            listing.Text(sb, f.DefaultAppearanceFont);
            sb.Append(",\"fontSize\":").Append(Num(f.DefaultAppearanceFontSize ?? 0));

            // Acrobat's rect is [left, top, right, bottom], of the first widget.
            sb.Append(",\"rect\":[");
            if (f.Widgets.Count > 0)
            {
                PdfFormWidgetInfo w = f.Widgets[0];
                sb.Append(Num(w.X)).Append(',').Append(Num(w.Y + w.Height)).Append(',')
                  .Append(Num(w.X + w.Width)).Append(',').Append(Num(w.Y));
            }
            else sb.Append("0,0,0,0");
            // The pages in the order the widgets name them, each once: a set beside the list,
            // since a field's widgets are the file's and a search of the list cost widgets × pages.
            sb.Append("],\"pages\":[");
            var seenPages = new HashSet<int>();
            bool firstPage = true;
            foreach (PdfFormWidgetInfo w in f.Widgets)
            {
                if (!seenPages.Add(w.PageIndex)) continue;
                sb.Append(firstPage ? "" : ",").Append(w.PageIndex.ToString(CultureInfo.InvariantCulture));
                firstPage = false;
            }

            // Each widget's rect, page and captions (normal, down, rollover), for the "name.N" form
            // that names one widget and for buttonGetCaption — at most MaxListedObjects, under the
            // listing's budget.
            sb.Append("],\"widgets\":[");
            int widgets = 0;
            for (; widgets < f.Widgets.Count && widgets < MaxListedObjects && !listing.Spent; widgets++)
            {
                PdfFormWidgetInfo w = f.Widgets[widgets];
                sb.Append(widgets > 0 ? ",[" : "[").Append(Num(w.X)).Append(',').Append(Num(w.Y + w.Height)).Append(',')
                  .Append(Num(w.X + w.Width)).Append(',').Append(Num(w.Y)).Append(',')
                  .Append(w.PageIndex.ToString(CultureInfo.InvariantCulture)).Append(",[");
                listing.Text(sb, w.NormalCaption); sb.Append(',');
                listing.Text(sb, w.DownCaption); sb.Append(',');
                listing.Text(sb, w.RolloverCaption);
                sb.Append("]]");
            }

            sb.Append("],\"onStates\":[");
            for (int i = 0; i < f.OnStates.Count && !listing.Spent; i++)
            {
                if (i > 0) sb.Append(',');
                listing.Text(sb, f.OnStates[i]);
            }
            sb.Append("],\"items\":[");
            int items = 0;
            for (; items < f.Options.Count && items < MaxListedObjects && !listing.Spent; items++)
            {
                if (items > 0) sb.Append(',');
                sb.Append('[');
                listing.Text(sb, f.Options[items].DisplayText ?? f.Options[items].ExportValue);
                sb.Append(',');
                listing.Text(sb, f.Options[items].ExportValue ?? f.Options[items].DisplayText);
                sb.Append(']');
            }
            if (widgets < f.Widgets.Count || items < f.Options.Count || listing.Spent)
                PdfJsEngine.RecordUnsupported("field " + ShortName(name) + ": " +
                    (f.Widgets.Count - widgets).ToString(CultureInfo.InvariantCulture) + " widget(s) and " +
                    (f.Options.Count - items).ToString(CultureInfo.InvariantCulture) + " item(s) not listed" +
                    (listing.Spent ? ", its texts cut at " + MaxListingChars.ToString(CultureInfo.InvariantCulture) + " characters" : ""));
            return sb.Append("]}").ToString();
        }

        /// <summary>A name as a note carries it: cut to the characters a recorded request keeps.</summary>
        private static string ShortName(string name)
            => name.Length > PdfJsEngine.MaxRecordedRequestChars ? name.Substring(0, PdfJsEngine.MaxRecordedRequestChars) + "…" : name;

        /// <summary>
        /// The document's text <c>/Info</c> entries, as a JSON object — and <c>Authors</c>, the
        /// list the XMP packet names (else the <c>/Author</c> alone), when the document names any.
        /// </summary>
        public string Info()
        {
            var listing = new Listing();
            var sb = new StringBuilder("{");
            bool first = true;
            int listed = 0;
            foreach (var kv in _ctx.DocumentInfo)
            {
                if (listing.Spent) break;
                if (!first) sb.Append(',');
                first = false;
                listing.Text(sb, kv.Key);
                sb.Append(':');
                listing.Text(sb, kv.Value);
                listed++;
            }
            if (_ctx.Authors.Count > 0 && !listing.Spent)
            {
                if (!first) sb.Append(',');
                sb.Append("\"Authors\":[");
                for (int i = 0; i < _ctx.Authors.Count && !listing.Spent; i++)
                {
                    if (i > 0) sb.Append(',');
                    listing.Text(sb, _ctx.Authors[i]);
                }
                sb.Append(']');
            }
            if (listing.Spent)
                PdfJsEngine.RecordUnsupported("info: " + (_ctx.DocumentInfo.Count - listed).ToString(CultureInfo.InvariantCulture) +
                                              " entr(ies) past " + MaxListingChars.ToString(CultureInfo.InvariantCulture) + " characters not listed");
            return sb.Append('}').ToString();
        }

        /// <summary>
        /// The most annotations, optional content groups and attachments, and the most widgets and
        /// items of one field, a script is handed: a document's inventory runs to a million, and each
        /// listed is a JSON row the engine then parses into an object under its memory limit, so a
        /// listing past the limit would fail every script that asks; the rest are counted on
        /// <see cref="PdfJsEngine.LastUnsupportedRequest"/>.
        /// </summary>
        internal const int MaxListedObjects = 10_000;

        /// <summary>
        /// The document's annotations a script may see — not a widget, a link or a popup — as a JSON
        /// array of [page, type, name, author, contents, modification date (a PDF date), flags, left,
        /// bottom, right, top], at most <see cref="MaxListedObjects"/> of them.
        /// </summary>
        public string Annotations()
        {
            var listing = new Listing();
            var sb = new StringBuilder("[");
            bool first = true;
            int listed = 0, beyond = 0;
            foreach (PdfAnnotationInfo a in _ctx.Annotations)
            {
                if (a == null || !IsScriptAnnotation(a.Subtype)) continue;
                if (listed == MaxListedObjects || listing.Spent) { beyond++; continue; }
                listed++;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('[').Append(a.PageIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
                listing.Text(sb, a.Subtype); sb.Append(',');
                listing.Text(sb, a.Name); sb.Append(',');
                listing.Text(sb, a.Author); sb.Append(',');
                listing.Text(sb, a.Contents); sb.Append(',');
                listing.Text(sb, a.ModifiedDate); sb.Append(',');
                sb.Append(a.Flags.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(Num(a.X)).Append(',').Append(Num(a.Y)).Append(',')
                  .Append(Num(a.X + a.Width)).Append(',').Append(Num(a.Y + a.Height)).Append(']');
            }
            if (beyond > 0)
                PdfJsEngine.RecordUnsupported("getAnnots: " + beyond.ToString(CultureInfo.InvariantCulture) +
                                              " annotation(s) past the first " + listed.ToString(CultureInfo.InvariantCulture) + " not listed");
            return sb.Append(']').ToString();
        }

        /// <summary>Acrobat's Annotation objects are the markup kinds: a widget, a link and a popup are not among them.</summary>
        private static bool IsScriptAnnotation(string subtype) =>
            subtype != PdfAnnotationSubtypes.Widget && subtype != PdfAnnotationSubtypes.Link && subtype != PdfAnnotationSubtypes.Popup;

        /// <summary>The document's optional content groups, as a JSON array of [name, on by default], at most <see cref="MaxListedObjects"/>.</summary>
        public string Ocgs()
        {
            var listing = new Listing();
            var sb = new StringBuilder("[");
            int listed = 0;
            for (int i = 0; i < _ctx.OptionalContentGroups.Count; i++)
            {
                OcgInfo g = _ctx.OptionalContentGroups[i];
                if (g == null) continue;
                if (listed == MaxListedObjects || listing.Spent)
                {
                    PdfJsEngine.RecordUnsupported("getOCGs: " + (_ctx.OptionalContentGroups.Count - i).ToString(CultureInfo.InvariantCulture) +
                                                  " group(s) past the first " + listed.ToString(CultureInfo.InvariantCulture) + " not listed");
                    break;
                }
                listed++;
                if (sb.Length > 1) sb.Append(',');
                sb.Append('[');
                listing.Text(sb, g.Name);
                sb.Append(',').Append(g.DefaultVisible ? "true" : "false").Append(']');
            }
            return sb.Append(']').ToString();
        }

        /// <summary>The catalog's XMP packet as text, or null when the document has none.</summary>
        public string Metadata() => _ctx.Metadata;

        /// <summary>Page <paramref name="page"/>'s rotation; 0 for a page the document lacks.</summary>
        public int PageRotation(double page)
        {
            int i = PageIndex(page);
            return i >= 0 && i < _ctx.Pages.Count && _ctx.Pages[i] != null ? _ctx.Pages[i].Rotate : 0;
        }

        /// <summary>
        /// Page <paramref name="page"/>'s box named <paramref name="box"/> (Art, Bleed, Crop, Media,
        /// Trim) as a JSON array [left, bottom, right, top], or null for a page the document lacks, a
        /// name that is no box, or a box the page has none of.
        /// </summary>
        public string PageBox(double page, string box)
        {
            int i = PageIndex(page);
            if (i < 0 || i >= _ctx.Pages.Count || _ctx.Pages[i] == null) return null;
            PdfJsPageInfo p = _ctx.Pages[i];
            IReadOnlyList<double> b;
            switch (box)
            {
                case "Media": b = p.MediaBox; break;
                case "Crop": b = p.CropBox; break;
                case "Bleed": b = p.BleedBox; break;
                case "Trim": b = p.TrimBox; break;
                case "Art": b = p.ArtBox; break;
                default: return null;
            }
            if (b == null || b.Count < 4) return null;
            return "[" + Num(b[0]) + "," + Num(b[1]) + "," + Num(b[2]) + "," + Num(b[3]) + "]";
        }

        /// <summary>How many words page <paramref name="page"/> holds; 0 for a page the document lacks, or when the host hands no page text over.</summary>
        public int PageWordCount(double page) => _ctx.PageWords?.Invoke(PageIndex(page))?.Count ?? 0;

        /// <summary>Word <paramref name="index"/> of page <paramref name="page"/>, or null past the page's words.</summary>
        public string PageWord(double page, double index)
        {
            IReadOnlyList<string> words = _ctx.PageWords?.Invoke(PageIndex(page));
            int i = PageIndex(index);
            return words != null && i >= 0 && i < words.Count ? words[i] : null;
        }

        /// <summary>A script's number as an index: truncated, saturated, -1 for NaN — never a cast.</summary>
        private static int PageIndex(double n) => double.IsNaN(n) ? -1 : SaturatingCast.ToInt(Math.Floor(n));

        /// <summary>
        /// The document's attachments as a JSON array of [name, size, MIME type, description, creation
        /// date, modification date] — the dates as written, PDF dates.
        /// </summary>
        public string DataObjects()
        {
            var listing = new Listing();
            var sb = new StringBuilder("[");
            int listed = 0;
            for (; listed < _ctx.Attachments.Count && listed < MaxListedObjects && !listing.Spent; listed++)
            {
                var a = _ctx.Attachments[listed];
                sb.Append(listed > 0 ? ",[" : "[");
                listing.Text(sb, a.Filename);
                sb.Append(',').Append(a.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
                listing.Text(sb, a.MimeType);
                sb.Append(',');
                listing.Text(sb, a.Description);
                sb.Append(',');
                listing.Text(sb, a.CreationDate);
                sb.Append(',');
                listing.Text(sb, a.ModDate);
                sb.Append(']');
            }
            if (listed < _ctx.Attachments.Count)
                PdfJsEngine.RecordUnsupported("dataObjects: " + (_ctx.Attachments.Count - listed).ToString(CultureInfo.InvariantCulture) +
                                              " attachment(s) past the first " + listed.ToString(CultureInfo.InvariantCulture) + " not listed");
            return sb.Append(']').ToString();
        }

        /// <summary>
        /// An attachment's contents as hex, or <c>null</c> when the document has no such attachment
        /// or it is larger than a script is handed.
        /// </summary>
        public string DataObjectContents(string name)
        {
            if (name == null || !_ctx.AttachmentContents.TryGetValue(name, out byte[] data) || data == null) return null;
            var sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public void Record(string request) => PdfJsEngine.RecordUnsupported(request);

        /// <summary>
        /// What joins a multi-select list box's selections in a value the host hands over or takes
        /// (<see cref="PdfFormFiller.MultiSelectSeparator"/>) — read once by the object model, so the
        /// script side splits and joins with the filler's own separator.
        /// </summary>
        public string MultiSelectSeparator() => PdfFormFiller.MultiSelectSeparator;

        /// <summary>The longest PDF date text <see cref="ParseDate"/> reads; a longer one does not read.</summary>
        internal const int MaxDateText = 64;

        /// <summary>
        /// A PDF date (§7.9.4) read by Core's one parser (<see cref="PdfDate.TryParse(string, out bool)"/>)
        /// as <c>"Y,M,D,h,m,s,offset"</c> — the components as written and the zone's offset in
        /// minutes, the offset empty when the date states no zone — or null when it does not read.
        /// The object model builds the script's <c>Date</c> from it: a stated zone is that instant,
        /// no zone the local time the date names, as a viewer shows it.
        /// </summary>
        public string ParseDate(string text)
        {
            if (text == null || text.Length > MaxDateText) return null;
            DateTimeOffset? parsed = PdfDate.TryParse(text, out bool zoneStated);
            if (parsed == null) return null;
            DateTimeOffset d = parsed.Value;
            var inv = CultureInfo.InvariantCulture;
            string offset = zoneStated ? (d.Offset.Hours * 60 + d.Offset.Minutes).ToString(inv) : string.Empty;
            return d.Year.ToString(inv) + "," + d.Month.ToString(inv) + "," + d.Day.ToString(inv) + "," +
                   d.Hour.ToString(inv) + "," + d.Minute.ToString(inv) + "," + d.Second.ToString(inv) + "," + offset;
        }

        /// <summary>
        /// A script's write of a field's <c>readonly</c>, <c>required</c>, <c>hidden</c> or
        /// <c>display</c>: kept on <see cref="PdfJsExecutionContext.FieldStateWrites"/> for the host
        /// to apply, the last write of a property winning.
        /// </summary>
        public void FieldState(string name, string key, string value)
        {
            if (name == null || key == null) return;
            if (!_ctx.FieldStateWrites.TryGetValue(name, out PdfJsFieldState state))
                _ctx.FieldStateWrites[name] = state = new PdfJsFieldState();
            state.Take(key, value ?? "");
        }

        public void Alert(string message) =>
            Keep(_ctx.AlertMessages, message, MaxAlerts, ref _alertsDropped, " further alert(s) not kept");

        public void Console(string message) =>
            Keep(_ctx.ConsoleMessages, message, MaxConsoleLines, ref _consoleDropped, " further console line(s) not kept");

        public void ConsoleClear()
        {
            _ctx.ConsoleMessages.Clear();
            _consoleDropped = 0;
        }

        /// <summary>The event's value when the script finished, or null when it had none.</summary>
        internal string ResultValue { get; private set; }

        /// <summary>The event's change when the script finished.</summary>
        internal string ResultChange { get; private set; } = string.Empty;

        /// <summary>The event's rc when the script finished.</summary>
        internal bool ResultRc { get; private set; } = true;

        /// <summary>Takes the event's outcome from the object model at the end of a script.</summary>
        public void Finish(string value, string change, bool rc)
        {
            ResultValue = value;
            ResultChange = change ?? string.Empty;
            ResultRc = rc;
        }

        /// <summary>A validation the script refused, with its reason.</summary>
        public void Rejected(string message)
        {
            _ctx.ValidationPassed = false;
            _ctx.ValidationMessage = Cut(message);
        }

        /// <summary>Adds a message, or — past <paramref name="max"/> — makes the last slot a note counting what was not kept.</summary>
        private static void Keep(List<string> list, string message, int max, ref int dropped, string note)
        {
            if (dropped == 0 && list.Count < max)
            {
                list.Add(Cut(message));
                return;
            }
            if (dropped == 0) dropped = 1;   // the message the note takes the place of
            dropped++;
            list[max - 1] = "(" + dropped.ToString(CultureInfo.InvariantCulture) + note + ")";
        }

        private static string Cut(string message)
        {
            message ??= string.Empty;
            return message.Length > MaxMessageChars ? message.Substring(0, MaxMessageChars) + "…" : message;
        }

        private static string TypeName(PdfFormFieldInfo f)
        {
            switch (f.FieldType)
            {
                case PdfFieldType.Button:
                    return PdfFieldFlags.Has(f.Flags, PdfFieldFlags.Pushbutton) ? "button"
                         : PdfFieldFlags.Has(f.Flags, PdfFieldFlags.Radio) ? "radiobutton" : "checkbox";
                case PdfFieldType.Choice:
                    return PdfFieldFlags.Has(f.Flags, PdfFieldFlags.Combo) ? "combobox" : "listbox";
                case PdfFieldType.Signature:
                    return "signature";
                default:
                    return "text";
            }
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The characters of one text a listing hands a script — a caption, an option, an
        /// <c>/Info</c> entry, an annotation's contents, an attachment's description; the rest are cut.
        /// </summary>
        internal const int MaxHandedTextChars = 64 * 1024;

        /// <summary>
        /// The characters one listing hands a script together — a field's description, the
        /// <c>/Info</c> entries, the annotations, the groups, the attachments; past it, what is left
        /// is not listed, and <see cref="PdfJsEngine.LastUnsupportedRequest"/> says so.
        /// </summary>
        internal const int MaxListingChars = 4 * 1024 * 1024;

        /// <summary>
        /// One listing's text budget. A file names one string from as many nodes as it likes — every
        /// widget's caption, every <c>/Info</c> entry, every annotation's contents — and the listing
        /// writes each where it is named, so written whole it would be the string's size times its
        /// references: each text is cut to <see cref="MaxHandedTextChars"/>, and the listing stops
        /// once <see cref="MaxListingChars"/> are written.
        /// </summary>
        private sealed class Listing
        {
            private int _left = MaxListingChars;

            internal bool Spent => _left <= 0;

            internal void Text(StringBuilder sb, string s)
            {
                s ??= string.Empty;
                int take = Math.Min(Math.Min(s.Length, MaxHandedTextChars), Math.Max(0, _left));
                _left -= Math.Max(1, take);
                AppendJsonString(sb, take == s.Length ? s : s.Substring(0, take));
            }
        }

        internal static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
