// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Exports a Document to ODF/ODT format.
    /// ODT is a ZIP file containing content.xml, styles.xml, and META-INF/manifest.xml.
    /// Uses System.IO.Compression — no external dependencies.
    /// </summary>
    public sealed class OdtExporter : IDocumentExporter
    {
        public void Export(Document document, Stream output)
        {
            // Cleared per export, on every path — see the identical note in DocxExporter.Export.
            // A reporting exporter that never clears hands the caller the previous export's answer.
            BuilderDiagnostics.Clear();
            XmlText.BeginDocument();
            ModelMeasurements.Validate(document);

            // Numbered before anything is written, and on every path, so a mark read during the
            // write always belongs to this document.
            _footnotes = FootnoteNumbering.For(document);

            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

            // 1. mimetype (must be first, uncompressed)
            var mimeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mimeEntry.Open(), Encoding.ASCII)) w.Write("application/vnd.oasis.opendocument.text");

            // 2. META-INF/manifest.xml
            var manifest = zip.CreateEntry("META-INF/manifest.xml");
            using (var w = new StreamWriter(manifest.Open(), Encoding.UTF8))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
                w.Write("<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\">\n");
                w.Write("  <manifest:file-entry manifest:media-type=\"application/vnd.oasis.opendocument.text\" manifest:full-path=\"/\"/>\n");
                w.Write("  <manifest:file-entry manifest:media-type=\"text/xml\" manifest:full-path=\"content.xml\"/>\n");
                w.Write("  <manifest:file-entry manifest:media-type=\"text/xml\" manifest:full-path=\"styles.xml\"/>\n");
                w.Write("</manifest:manifest>");
            }

            // 3. styles.xml (minimal)
            var styles = zip.CreateEntry("styles.xml");
            using (var w = new StreamWriter(styles.Open(), Encoding.UTF8))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
                w.Write("<office:document-styles xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" ");
                w.Write("xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" ");
                w.Write("xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\">\n");
                w.Write("</office:document-styles>");
            }

            // 4. content.xml
            var content = zip.CreateEntry("content.xml");
            using (var w = new StreamWriter(content.Open(), Encoding.UTF8))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
                w.Write("<office:document-content ");
                w.Write("xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" ");
                w.Write("xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" ");
                w.Write("xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" ");
                w.Write("xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" ");
                w.Write("xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" ");
                // xlink is used by a table-of-contents entry's <text:a xlink:href="#anchor">. Without
                // the declaration the whole content.xml was not well-formed — XDocument.Load and every
                // namespace-aware ODF consumer rejected it with "xlink is an undeclared prefix" — so a
                // document with a TOC did not open at all.
                w.Write("xmlns:xlink=\"http://www.w3.org/1999/xlink\">\n");

                // Run styling has to be DECLARED before the body: ODF puts automatic styles in
                // their own element ahead of <office:body>, and a <text:span> can only reference a
                // style by name. So the document is walked once to collect the distinct run
                // styles, then again to write it. Streaming straight through leaves nowhere for a style
                // to be declared, so every run would be written as bare text with its formatting
                // silently dropped.
                var runStyles = new OdtRunStyleTable();
                foreach (var section in document.Sections) runStyles.Collect(section.Children);

                // A heading's anchor is assigned by TocEntries.Collect, which WriteToc runs only
                // when it reaches the contents block. But the headings are written before that block,
                // so at WriteParagraph time their AnchorName is still null and no <text:bookmark>
                // would be written — every TOC link would then dangle. Assign the anchors up front,
                // each contents block with its own depth. Collect is idempotent: a heading that
                // already carries an anchor keeps it, so WriteToc's later call reuses these names.
                foreach (var section in document.Sections)
                    foreach (var child in ModelRules.InFlow(section.Children))
                        if (child is TocBlock pre)
                            TocEntries.Collect(document, pre.MaxLevel);

                // Automatic style for RTL paragraphs: right-to-left writing mode + end-aligned.
                w.Write("<office:automatic-styles>\n");
                w.Write("<style:style style:name=\"RTLp\" style:family=\"paragraph\">");
                w.Write("<style:paragraph-properties style:writing-mode=\"rl-tb\" fo:text-align=\"end\"/>");
                w.Write("</style:style>\n");
                WriteListStyle(w);
                runStyles.WriteDeclarations(w);
                w.Write("</office:automatic-styles>\n");

                w.Write("<office:body><office:text>\n");

                // ODF table:name must be unique across the whole document, nested tables included.
                int tableNumber = 1;
                foreach (var section in document.Sections)
                {
                    // Through the shared resolver: `== RightToLeft` answers "left-to-right" for
                    // TextDirection.Auto, which the PDF exporter resolves from the content.
                    bool rtl = ModelRules.IsRightToLeft(section);
                    foreach (var child in ModelRules.InFlow(section.Children))
                    {
                        if (child is Paragraph para)
                        {
                            ReportFloatDropped(para);
                            WriteParagraph(w, para, rtl, runStyles);
                        }
                        else if (child is Table table) WriteTable(w, table, rtl, ref tableNumber, runStyles, 0);
                        else if (child is ListItem li) WriteListItem(w, li, rtl, runStyles);
                        else if (child is TocBlock toc) WriteToc(w, toc, document);
                        else if (child is ListBlock lb)
                            WriteList(w, lb, rtl, ref tableNumber, runStyles, 0);
                    }
                }

                w.Write("</office:text></office:body>\n");
                w.Write("</office:document-content>");
            }
        }

        /// <summary>
        /// Says so when a floated paragraph is written as an ordinary block.
        /// </summary>
        /// <remarks>
        /// <para><b>This format has no float this exporter writes.</b> A real one means a framed or
        /// anchored object in ODT's own vocabulary, which is a separate piece of work; until then a
        /// floated paragraph becomes a block in the flow, so text that would have run alongside it
        /// runs underneath instead.</para>
        ///
        /// <para><b>Reported rather than silent, because the two look identical in the output.</b>
        /// A caller who set <c>Paragraph.Float</c> and got a stacked document has no way to tell
        /// the property was dropped from the property never having applied. The PDF and HTML
        /// exports honour it, so the same model produces different layouts across formats — which
        /// is exactly the case a diagnostic exists for.</para>
        ///
        /// <para>Fires only for a paragraph that really asked to float, so it cannot go off on a
        /// document that never used the feature.</para>
        /// </remarks>
        private static void ReportFloatDropped(Paragraph para)
        {
            if (para.Float == FloatSide.None) return;

            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                "float: a floated paragraph was written as an ordinary block, because ODT has no "
                + "float this exporter writes. Text that would have run alongside it runs below it. "
                + "The PDF and HTML exports honour Paragraph.Float.");
        }

        private static void WriteParagraph(StreamWriter w, Paragraph para, bool sectionRtl,
                                           OdtRunStyleTable runStyles)
        {
            ReportDroppedOpacity(para.Opacity, "a paragraph");
            // ODF can express a paragraph background image (style:background-image inside a
            // paragraph style), but it needs the bytes as a separate package part plus a manifest
            // entry, and this exporter writes a fixed four-entry package with one hand-rolled
            // automatic style. Dropping it and saying so beats writing a style that references a
            // part that is not there — which opens as a document with a broken background.
            if (para.BackgroundImage?.Data is { Length: > 0 } bgBytes)
                BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                    $"a paragraph background image of {bgBytes.Length:N0} bytes was dropped: this ODT " +
                    "exporter writes no image parts, so style:background-image has nothing to point at.");

            bool rtl = ModelRules.IsRightToLeft(
                para.Direction ?? (sectionRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                ModelRules.SampleText(para));
            w.Write(rtl ? "<text:p text:style-name=\"RTLp\">" : "<text:p>");
            // A paragraph that names a destination (a heading the TOC links to, any internal-link
            // target) writes a <text:bookmark>; the TOC's <text:a xlink:href="#anchor"> resolved to
            // nothing before, because no element carried the name.
            if (!string.IsNullOrEmpty(para.AnchorName))
            {
                w.Write("<text:bookmark text:name=\"");
                w.Write(Xml(para.AnchorName!));
                w.Write("\"/>");
            }
            WriteInline(w, para.Runs, runStyles);
            w.Write("</text:p>\n");
        }


        /// <summary>Style name of the shared list style declared in the automatic styles.</summary>
        private const string ListStyleName = "TzList";

        /// <summary>
        /// Declares one list style whose every level suppresses ODF's own marker.
        /// </summary>
        /// <remarks>
        /// <para><b>The structure is native and the marker is still ours, deliberately.</b> Emitting
        /// <c>&lt;text:list&gt;</c> and <c>&lt;text:list-item&gt;</c> is what makes the document a
        /// list to a reader that asks — which a run of <c>&lt;text:p&gt;</c> with bullet characters
        /// in it never was. Handing ODF the <i>numbering</i> as well is a second step: the model
        /// admits a pre-resolved <see cref="ListItem.Marker"/> that no
        /// <c>&lt;text:list-level-style-number&gt;</c> can reproduce (<c>"1.2."</c>, <c>"א."</c>,
        /// a per-item override), and a level style that renumbered those would silently change the
        /// document.</para>
        ///
        /// <para>An empty <c>style:num-format</c> is ODF's way of saying "this level has no
        /// number", which is what leaves the marker this exporter writes as the only one.</para>
        /// </remarks>
        private static void WriteListStyle(StreamWriter w)
        {
            w.Write("<text:list-style style:name=\"" + ListStyleName + "\">");
            for (int level = 1; level <= ModelRules.MaxListDepth; level++)
            {
                w.Write("<text:list-level-style-number text:level=\""
                        + level.ToString(CultureInfo.InvariantCulture)
                        + "\" style:num-format=\"\"/>");
            }
            w.Write("</text:list-style>\n");
        }

        /// <summary>Writes a list as a native <c>&lt;text:list&gt;</c>.</summary>
        private static void WriteList(StreamWriter w, ListBlock list, bool containerRtl,
                                      ref int tableNumber, OdtRunStyleTable runStyles, int depth)
        {
            if (list == null || depth > ModelRules.MaxListDepth) return;

            bool rtl = ModelRules.IsRightToLeft(
                list.Direction ?? (containerRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                string.Empty);

            w.Write("<text:list text:style-name=\"" + ListStyleName + "\">\n");

            for (int i = 0; i < list.Items.Count; i++)
            {
                ListItem li = list.Items[i];
                if (li == null) continue;

                w.Write("<text:list-item>");
                WriteListItemContent(w, li, ModelRules.ListMarker(list, i), rtl, runStyles);

                if (li.Sublist != null)
                {
                    w.Write("\n");
                    WriteList(w, li.Sublist, rtl, ref tableNumber, runStyles, depth + 1);
                }

                w.Write("</text:list-item>\n");
            }

            w.Write("</text:list>\n");
        }

        /// <summary>One item's paragraph: its marker, then its runs.</summary>
        private static void WriteListItemContent(StreamWriter w, ListItem li, string marker,
                                                 bool containerRtl, OdtRunStyleTable runStyles)
        {
            bool rtl = ModelRules.IsRightToLeft(
                li.Direction ?? (containerRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                li.Text);

            w.Write(rtl ? "<text:p text:style-name=\"RTLp\">" : "<text:p>");
            w.Write(Xml(marker));
            WriteInline(w, ModelRules.ListRuns(li), runStyles);
            w.Write("</text:p>");
        }

        /// <summary>
        /// Writes a list item, honouring the two overrides the model documents —
        /// <see cref="ListItem.Marker"/> and <see cref="ListItem.Runs"/>, so an item whose content
        /// lives in <c>Runs</c> exports with its text, not as a bare bullet.
        /// </summary>
        private static void WriteListItem(StreamWriter w, ListItem li, bool sectionRtl,
                                          OdtRunStyleTable runStyles)
        {
            bool rtl = ModelRules.IsRightToLeft(
                li.Direction ?? (sectionRtl ? TextDirection.RightToLeft : TextDirection.LeftToRight),
                li.Text);
            w.Write(rtl ? "<text:p text:style-name=\"RTLp\">" : "<text:p>");
            w.Write(Xml(ModelRules.ListMarker(li)));
            WriteInline(w, ModelRules.ListRuns(li), runStyles);
            w.Write("</text:p>\n");
        }

        /// <summary>
        /// Reports an <c>Opacity</c> this format cannot express. Silent at full opacity.
        /// </summary>
        /// <remarks>
        /// ODF does have <c>draw:opacity</c>, but it applies to a drawing object's fill — there is
        /// no text or paragraph constant-alpha property for this exporter to write, and this
        /// exporter writes no drawing objects at all (it already declines images for that reason).
        /// </remarks>
        private static void ReportDroppedOpacity(double opacity, string what)
            => BuilderDiagnostics.ReportDroppedOpacity(opacity, what, "this ODT exporter writes no constant-alpha property");

        /// <summary>
        /// Writes a footnote as ODF's own <c>&lt;text:note&gt;</c> — the mark and the note
        /// together, in place.
        /// </summary>
        /// <remarks>
        /// <para><b>ODF puts the note where the mark is, and the application does the rest.</b>
        /// Unlike the PDF export, which has to reserve room at the foot of a page, and unlike the
        /// HTML one, which has no page to put it at the foot of, an ODF consumer lays the note out
        /// itself — so the whole of the exporter's job is stating the relationship.</para>
        ///
        /// <para><b>The citation is written even though the application renumbers it.</b> A
        /// consumer that applies its own numbering overwrites it; one that does not — or that
        /// is only reading the file — finds the mark this document was numbered with, and it
        /// matches the PDF and HTML exports of the same model.</para>
        /// </remarks>
        private static void WriteFootnote(StreamWriter w, Footnote note, OdtRunStyleTable? runStyles, int depth)
        {
            string mark = _footnotes.MarkOf(note);
            if (mark.Length == 0) return;

            // A note's body may reference a note, and a numbered note that contains itself (or a chain
            // of body-referenced notes that close into a cycle) recurses here forever — the mark gate
            // above does not stop it, since such a note is numbered. Bound it at the exporter's shared
            // nesting ceiling; a depth, not a "being written" set, because a chain of distinct notes
            // overflows the stack just the same. (HTML does not have this: there a mark is only a link.)
            if (depth > PdfLimits.MaxTableNestingDepth)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    "Footnotes nested more than " + PdfLimits.MaxTableNestingDepth + " deep were not written.");
                return;
            }

            // ODF's own distinction: a note of class endnote is laid out at the document's end by
            // the application, as a footnote is at the page's foot.
            bool endnote = note.Placement == NotePlacement.DocumentEnd;
            w.Write(endnote ? "<text:note text:note-class=\"endnote\" text:id=\"edn" : "<text:note text:note-class=\"footnote\" text:id=\"ftn");
            w.Write(Xml(mark));
            w.Write("\"><text:note-citation>");
            w.Write(Xml(mark));
            w.Write("</text:note-citation><text:note-body><text:p>");
            WriteInline(w, note.Runs, runStyles, depth + 1);
            w.Write("</text:p></text:note-body></text:note>");
        }

        /// <summary>
        /// Writes a table of contents as ODF's own <c>&lt;text:table-of-content&gt;</c>, with the
        /// entries this document has today inside it.
        /// </summary>
        /// <remarks>
        /// <para><b>Both halves, because a consumer may do either.</b> The element tells an office
        /// application that this is a contents list it owns and can regenerate; the entries inside
        /// are what a reader — or a converter that does not regenerate — sees. Writing
        /// only the element gives a document that is empty until someone opens it in Writer and
        /// presses update.</para>
        ///
        /// <para><b>No page numbers.</b> ODF's own contents carries them in a field the
        /// application fills from its layout, and this exporter has no layout: a number written
        /// here would be about a PDF the reader is not looking at. The entries link instead.</para>
        /// </remarks>
        private static void WriteToc(StreamWriter w, TocBlock toc, Document document)
        {
            var entries = TocEntries.Collect(document, toc.MaxLevel);
            if (entries.Count == 0) return;

            w.Write("<text:table-of-content text:name=\"Contents\">");
            w.Write("<text:table-of-content-source text:outline-level=\"");
            w.Write(toc.MaxLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.Write("\">");
            // The title template belongs in the source — it describes how an application that
            // regenerates the list heads it, not what the list shows now.
            if (!string.IsNullOrEmpty(toc.Title))
            {
                w.Write("<text:index-title-template>");
                w.Write(Xml(toc.Title!));
                w.Write("</text:index-title-template>");
            }
            w.Write("</text:table-of-content-source>");
            w.Write("<text:index-body>");

            // The shown title is a <text:index-title> paragraph inside the body. The template above
            // is not rendered by a consumer that does not regenerate, so without this a reader saw a
            // contents list with no heading.
            if (!string.IsNullOrEmpty(toc.Title))
            {
                w.Write("<text:index-title text:name=\"Contents_Head\"><text:p>");
                w.Write(Xml(toc.Title!));
                w.Write("</text:p></text:index-title>");
            }

            foreach (var entry in entries)
            {
                w.Write("<text:p text:style-name=\"Contents_20_");
                w.Write(entry.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.Write("\"><text:a xlink:type=\"simple\" xlink:href=\"#");
                w.Write(Xml(entry.Anchor));
                w.Write("\">");
                w.Write(Xml(entry.Text));
                w.Write("</text:a></text:p>");
            }

            w.Write("</text:index-body></text:table-of-content>");
        }

        /// <summary>Marks assigned before the document is written; see <c>FootnoteNumbering</c>.</summary>
        /// <remarks>
        /// <b>[ThreadStatic] because the write chain is static and an export is a call, not an
        /// object.</b> Two threads exporting two documents would otherwise share one numbering and
        /// interleave their marks. It is assigned at the top of every export, on every path, so a
        /// value read here always belongs to the document being written — the rule CLAUDE.md
        /// states for every <c>Last*</c> slot in this tree, for the same reason.
        /// </remarks>
        [System.ThreadStatic] private static FootnoteNumbering? _footnotesSlot;

        private static FootnoteNumbering _footnotes
        {
            get => _footnotesSlot ??= new FootnoteNumbering();
            set => _footnotesSlot = value;
        }

        /// <summary>
        /// Writes inline content, reporting anything this exporter cannot carry.
        /// </summary>
        /// <remarks>
        /// <see cref="ImageRun"/> and <see cref="PageBreak"/> matched no arm and vanished with no
        /// diagnostic, while the paragraph <b>background</b> image above — the far smaller loss —
        /// was already reported. "The same model exported to four formats" is exactly when a caller
        /// needs to be told which one lost something.
        /// </remarks>
        private static void WriteInline(StreamWriter w,
            System.Collections.Generic.IEnumerable<IInlineElement> runs, OdtRunStyleTable? runStyles, int depth = 0)
        {
            foreach (var run in runs)
            {
                if (run is TextRun tr)
                {
                    ReportDroppedOpacity(tr.Opacity, "a text run");
                    string? styleName = runStyles?.NameFor(tr);
                    // Only styled runs get a span. An unstyled one writes bare text exactly as
                    // before, so a plain document's output is byte-for-byte unchanged and no
                    // reader has to skip an empty wrapper on every word.
                    if (styleName == null) { w.Write(Xml(tr.Text)); continue; }

                    w.Write("<text:span text:style-name=\"");
                    w.Write(styleName);
                    w.Write("\">");
                    w.Write(Xml(tr.Text));
                    w.Write("</text:span>");
                }
                else if (run is Footnote note) WriteFootnote(w, note, runStyles, depth);
                else if (run is LineBreak) w.Write("<text:line-break/>");
                else if (run is TabRun) w.Write("<text:tab/>");
                else if (run is ImageRun img)
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                        (img.Drawing != null ? "an inline vector drawing" : $"an inline image of {img.Data?.Length ?? 0:N0} bytes") + " was dropped: this ODT " +
                        "exporter writes no image parts" +
                        (string.IsNullOrEmpty(img.Alt) ? "." : $" (alt: '{img.Alt}')."));
                else
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                        $"an inline element of type {run.GetType().Name} was dropped: this ODT " +
                        "exporter has no mapping for it.");
            }
        }

        /// <summary>
        /// Writes one table, numbering it uniquely within the document.
        /// </summary>
        /// <remarks>
        /// <c>table:name</c> is an ODF identifier that must be unique in the document, and every
        /// table — nested ones included — was written as the literal "Table1". LibreOffice silently
        /// renames the duplicates on load, so a round trip does not reproduce the file, and a
        /// validating consumer rejects it. The counter travels by reference so a nested table takes
        /// the next number rather than restarting.
        /// </remarks>
        private static void WriteTable(StreamWriter w, Table table, bool rtl, ref int tableNumber,
                                       OdtRunStyleTable runStyles, int depth)
        {
            // A cell may hold its own table; each re-entry carries depth + 1, so a self-reference and a
            // long distinct chain are both refused at the ceiling, before the uncatchable stack overflow.
            if (depth > PdfLimits.MaxTableNestingDepth)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    "Tables nested more than " + PdfLimits.MaxTableNestingDepth + " deep were not written.");
                return;
            }
            w.Write("<table:table table:name=\"Table"
                + tableNumber++.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\">\n");
            if (table.Rows.Count > 0)
            {
                for (int i = 0; i < table.Rows[0].Cells.Count; i++)
                    w.Write("<table:table-column/>\n");
            }
            foreach (var row in table.Rows)
            {
                w.Write("<table:table-row>\n");
                // Mirror the visual column order for RTL so cell 0 is the rightmost column.
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    var cell = row.Cells[rtl ? row.Cells.Count - 1 - c : c];
                    w.Write("<table:table-cell>");
                    bool any = false;
                    foreach (var block in ModelRules.InFlow(cell.Content()))
                    {
                        if (block is Paragraph cp) { WriteParagraph(w, cp, rtl, runStyles); any = true; }
                        else if (block is Table nt) { WriteTable(w, nt, rtl, ref tableNumber, runStyles, depth + 1); any = true; }
                        else if (block is ListItem li) { WriteListItem(w, li, rtl, runStyles); any = true; }
                        else if (block is ListBlock lb)
                        { WriteList(w, lb, rtl, ref tableNumber, runStyles, 0); any = true; }
                    }
                    if (!any) w.Write(rtl ? "<text:p text:style-name=\"RTLp\"/>" : "<text:p/>");
                    w.Write("</table:table-cell>\n");
                }
                w.Write("</table:table-row>\n");
            }
            w.Write("</table:table>\n");
        }

        // Strips XML-forbidden characters (U+000B, lone surrogates, …) and escapes the five delimiters,
        // for both element text and attribute values; reports once per document if anything was replaced.
        private static string Xml(string s) => XmlText.ForOdt(s);
    }
}
