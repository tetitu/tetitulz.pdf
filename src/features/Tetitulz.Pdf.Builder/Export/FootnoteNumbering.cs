// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Assigns every footnote in a document its reference mark, once, before anything is laid out.
    /// </summary>
    /// <remarks>
    /// <para><b>A footnote's number is a property of the document, not of the page it lands on.</b>
    /// It depends on how many notes precede it, which a producer building one section at a time
    /// cannot know — the same reason <see cref="ListBlock"/> numbers its own items rather than
    /// making the caller do it.</para>
    ///
    /// <para><b>Numbered here rather than during layout, because layout runs more than once.</b>
    /// <c>MeasureParagraph</c> lays a paragraph out to find its height and throws the result away,
    /// and a table cell is measured before it is drawn — so a counter advanced at layout time
    /// counts some notes twice and produces a document numbered 1, 3, 4, 7. The map is keyed on
    /// the <see cref="Footnote"/> instance, so asking twice gives the same answer.</para>
    ///
    /// <para><b>An explicit <see cref="Footnote.Mark"/> is used verbatim and does not advance the
    /// sequence.</b> A dagger beside note 3 leaves the next automatic note numbered 4: the caller
    /// who set a symbol was stepping outside the numbering, not renumbering it.</para>
    /// </remarks>
    internal sealed class FootnoteNumbering
    {
        /// <remarks>
        /// Keyed by identity, so two footnotes with identical content are still two footnotes.
        /// <c>Footnote</c> has no <c>Equals</c> override today, so the default comparer would do
        /// the same, and that is exactly what a later change breaks silently: a record-style rewrite
        /// of the model would merge every note that says "ibid.". Named through
        /// <c>Core.Objects.PdfReferenceEquality</c>, the one identity comparer.
        /// </remarks>
        private readonly Dictionary<Footnote, string> _marks =
            new Dictionary<Footnote, string>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);

        private int _next = 1;
        private int _nextEndnote = 1;
        private readonly List<Footnote> _endnotes = new List<Footnote>();

        // Beside the list, so a note is listed once without searching it: a Contains per mark was n²/2 over n endnotes.
        private readonly HashSet<Footnote> _listed = new HashSet<Footnote>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);

        /// <summary>
        /// Every endnote (<see cref="NotePlacement.DocumentEnd"/>) the walk reached, in document
        /// order, each once — the list the PDF export draws after the last section's content.
        /// </summary>
        /// <remarks>
        /// Collected by the model walk rather than during layout: layout measures a block before it
        /// draws it and may look ahead at the next, so a list built there would hold a note twice or
        /// out of order; the walk is the one pass that sees the document once, in order.
        /// </remarks>
        internal IReadOnlyList<Footnote> Endnotes => _endnotes;

        /// <summary>The mark for a footnote, or the empty string when it was never numbered.</summary>
        /// <remarks>
        /// Empty rather than a fresh number for one this walk did not reach: a note that appears
        /// during layout but not in the model walk is a bug in the walk, and inventing a mark for
        /// it would hide that behind a document numbered out of order.
        /// </remarks>
        internal string MarkOf(Footnote? note)
        {
            if (note == null) return "";
            if (note.Mark != null) return note.Mark;
            return _marks.TryGetValue(note, out string? mark) ? mark ?? "" : "";
        }

        /// <summary>Numbers every footnote reachable from <paramref name="document"/>, in order.</summary>
        internal static FootnoteNumbering For(Document? document)
        {
            var numbering = new FootnoteNumbering();
            if (document == null) return numbering;

            foreach (Section section in document.Sections)
            {
                numbering.Walk(section.Header?.Content());
                numbering.Walk(section.Children);
                numbering.Walk(section.Footer?.Content());
            }

            return numbering;
        }

        private void Walk(IEnumerable<IDocElement>? blocks)
        {
            if (blocks == null) return;
            foreach (IDocElement block in blocks) WalkBlock(block, 0);
        }

        /// <summary>Numbers the footnotes of one block and everything nested inside it.</summary>
        /// <remarks>
        /// Depth-bounded for the reason every walk of this model is: it is a public mutable graph,
        /// so a cell can hold a table that holds the cell. <see cref="ModelRules.MaxListDepth"/> is
        /// the cap the model already uses for the same hazard.
        /// </remarks>
        private void WalkBlock(IDocElement? block, int depth)
        {
            if (block == null || depth > ModelRules.MaxListDepth) return;

            switch (block)
            {
                case Paragraph paragraph:
                    Number(paragraph.Runs);
                    break;

                case ListItem item:
                    Number(item.Runs);
                    WalkBlock(item.Sublist, depth + 1);
                    break;

                case ListBlock list:
                    foreach (ListItem li in list.Items) WalkBlock(li, depth + 1);
                    break;

                case Table table:
                    foreach (TableRow row in table.Rows)
                        foreach (TableCell cell in row.Cells)
                            foreach (IDocElement content in cell.Content())
                                WalkBlock(content, depth + 1);
                    break;
            }
        }

        private void Number(IEnumerable<IInlineElement>? runs)
        {
            if (runs == null) return;

            foreach (IInlineElement run in runs)
            {
                if (run is not Footnote note) continue;
                bool endnote = note.Placement == NotePlacement.DocumentEnd;
                if (endnote && _listed.Add(note)) _endnotes.Add(note);

                if (note.Mark != null) continue;          // the caller's own mark; not a sequence
                if (_marks.ContainsKey(note)) continue;   // the same instance used twice

                // Two sequences, as Word keeps them: the footnotes' and the endnotes'.
                if (endnote) _marks[note] = (_nextEndnote++).ToString(CultureInfo.InvariantCulture);
                else _marks[note] = (_next++).ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
