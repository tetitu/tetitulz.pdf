// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Collects a document's headings into table-of-contents entries, and answers what a heading's
    /// level and anchor are.
    /// </summary>
    /// <remarks>
    /// <para><b>A heading is a paragraph whose <see cref="Paragraph.Role"/> says so.</b> The model
    /// has no separate heading type — the HTML importer already writes <c>H1</c>–<c>H6</c> there,
    /// and the tagged PDF export already reads it — so the contents block asks the same question
    /// the tag tree does rather than inventing a second definition of "heading" that could
    /// disagree with it.</para>
    ///
    /// <para><b>One collector for all four exporters.</b> They disagree about what to do with an
    /// entry — a page number, a link, a field — and must not disagree about which paragraphs are
    /// entries or in what order: a contents list that differs between two exports of one document
    /// is a defect no single export can show.</para>
    /// </remarks>
    internal static class TocEntries
    {
        /// <summary>One heading, as the contents block sees it.</summary>
        internal sealed class Entry
        {
            internal Entry(Paragraph heading, int level, string text, string anchor)
            {
                Heading = heading;
                Level = level;
                Text = text;
                Anchor = anchor;
            }

            /// <summary>The paragraph this entry points at.</summary>
            internal Paragraph Heading { get; }

            /// <summary>1 for <c>H1</c>, 6 for <c>H6</c>.</summary>
            internal int Level { get; }

            /// <summary>The heading's text, flattened.</summary>
            internal string Text { get; }

            /// <summary>
            /// The heading's anchor name, which this assigns when the heading had none.
            /// </summary>
            /// <remarks>
            /// <b>Assigned onto the paragraph, not merely returned.</b> The entry links to the
            /// heading, so the heading has to carry the name the link uses — and a name computed
            /// here and not written back would leave every link pointing at nothing.
            /// </remarks>
            internal string Anchor { get; }
        }

        /// <summary>Every heading in the document, in reading order, down to <paramref name="maxLevel"/>.</summary>
        internal static List<Entry> Collect(Document? document, int maxLevel)
        {
            var entries = new List<Entry>();
            if (document == null) return entries;

            foreach (Section section in document.Sections)
                foreach (IDocElement child in section.Children)
                    Walk(child, maxLevel, entries, 0);

            return entries;
        }

        /// <summary>
        /// Collects from one block and everything nested inside it.
        /// </summary>
        /// <remarks>
        /// A heading inside a table cell is still a heading — it is in the tag tree either way, and
        /// a contents list that skipped it would disagree with the document's own structure. The
        /// depth cap is the one the model uses everywhere: it is a public mutable graph, so a cell
        /// can hold a table that holds the cell.
        /// </remarks>
        private static void Walk(IDocElement? block, int maxLevel, List<Entry> into, int depth)
        {
            if (block == null || depth > ModelRules.MaxListDepth) return;

            switch (block)
            {
                case Paragraph paragraph:
                    int level = LevelOf(paragraph.Role, SectionNestingDepth);
                    if (level >= 1 && level <= maxLevel) into.Add(Build(paragraph, level, into.Count));
                    break;

                case Table table:
                    foreach (TableRow row in table.Rows)
                        foreach (TableCell cell in row.Cells)
                            foreach (IDocElement content in cell.Content())
                                Walk(content, maxLevel, into, depth + 1);
                    break;
            }
        }

        private static Entry Build(Paragraph heading, int level, int index)
        {
            if (string.IsNullOrEmpty(heading.AnchorName))
                heading.AnchorName = "toc" + index.ToString(CultureInfo.InvariantCulture);

            return new Entry(heading, level, TextOf(heading), heading.AnchorName!);
        }

        /// <summary>
        /// How many grouping elements enclose a heading in a <see cref="Section"/>: one, the
        /// <c>/Sect</c> the PDF exporter writes each section as. Nothing else in the model nests one
        /// — a table cell is a cell, not a section — so this is the whole derivation.
        /// </summary>
        internal const int SectionNestingDepth = 1;

        /// <summary>
        /// PDF/UA's strongly structured heading role. <c>PdfStructureTypes</c> names <c>H1</c>–<c>H6</c>
        /// and not this one; it belongs there beside them, and moves there the next time Core is open.
        /// </summary>
        internal const string StrongStructureHeading = "H";

        /// <summary>The level a role names, or 0 when it names no heading.</summary>
        /// <remarks>
        /// The six numbered roles carry their level in their name. A bare <c>H</c> is PDF/UA's
        /// "strong structure" heading, whose level is how deeply its <c>/Sect</c> elements nest
        /// (ISO 32000-1 §14.8.4.3.2); this model nests exactly <see cref="SectionNestingDepth"/>,
        /// so that is the level it is placed at, and a heading the tag tree names is in the
        /// contents.
        /// </remarks>
        internal static int LevelOf(string? role) => LevelOf(role, SectionNestingDepth);

        /// <summary>
        /// The level a role names inside <paramref name="sectionDepth"/> nested grouping elements.
        /// </summary>
        internal static int LevelOf(string? role, int sectionDepth)
        {
            if (role == null) return 0;
            if (role == StrongStructureHeading) return sectionDepth < 1 ? 1 : sectionDepth;
            if (role.Length != 2 || role[0] != 'H') return 0;
            if (role[1] < '1' || role[1] > '6') return 0;

            return role[1] - '0';
        }

        /// <summary>A heading's text, from its runs.</summary>
        private static string TextOf(Paragraph heading)
        {
            var sb = new StringBuilder();

            foreach (IInlineElement run in heading.Runs)
            {
                // A footnote's mark belongs beside the heading on the page and not in the contents,
                // where it would point at a note the reader has not reached; its text belongs even
                // less.
                if (run is TextRun text) sb.Append(text.Text);
            }

            return sb.ToString().Trim();
        }
    }
}
