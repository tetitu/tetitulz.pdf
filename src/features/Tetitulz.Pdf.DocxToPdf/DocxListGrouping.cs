// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using M = Tetitulz.Pdf.Builder.Model;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf
{
    /// <summary>
    /// Collects the consecutive numbered paragraphs of a DOCX body into
    /// <see cref="M.ListBlock"/> containers, nesting by <c>w:ilvl</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>OOXML has no list element.</b> A list is a run of paragraphs that happen to share
    /// a <c>w:numId</c>, each carrying its own level — the grouping exists only in the numbering
    /// they reference, so somebody has to reconstruct it. The rendering path never needed to: it
    /// draws a marker in front of each paragraph and moves on. The Builder-model path does, because
    /// its exporters emit <c>&lt;ul&gt;</c>, <c>&lt;text:list&gt;</c> and <c>/L</c>, none of which
    /// can be written one paragraph at a time.</para>
    ///
    /// <para><b>Not a hard-coded bullet.</b> Giving every numbered paragraph <c>"• "</c> and an
    /// indent derived from its level, whatever <c>numbering.xml</c> said, would turn an ordered list
    /// into a bulleted one, lose both numbers of a multi-level <c>"1.1"</c> scheme, and leave
    /// nothing downstream able to tell a list from a paragraph. The numbering was
    /// already being read correctly one output path over; this reaches the same reader rather than
    /// starting a second.</para>
    ///
    /// <para><b>A marker is always written here, unlike the HTML importer.</b> Word's level text is
    /// arbitrary — <c>%1.%2</c>, roman, letters, a literal prefix — and the model numbers in
    /// decimals from a single <c>Start</c>, so there is no case where the container could
    /// reproduce it. What the container adds is the structure, and <c>IsOrdered</c>, which the
    /// marker string cannot carry: a level whose text is <c>"1."</c> and a bullet that happens to
    /// be the character <c>1</c> produce the same prefix.</para>
    /// </remarks>
    internal sealed class DocxListGrouping
    {
        private readonly WordprocessingDocument _wordDoc;
        private readonly StyleResolver _resolver;

        /// <summary>
        /// Numbering counters for the whole conversion, so a list interrupted by a paragraph, a table
        /// cell or a section break and resumed keeps counting.
        /// </summary>
        /// <remarks>
        /// Word restarts a sequence only where <c>numbering.xml</c> says so, not wherever a
        /// paragraph interrupts one, so the counters must outlive any single group. They belong to
        /// the conversion for the same reason <c>DocxRenderContext.ListCounters</c> does: the mapper
        /// hands every grouping it makes, a section's or a cell's, the one dictionary.
        /// </remarks>
        private readonly Dictionary<int, int> _counters;

        /// <summary>The open list stack, innermost last; each entry is one <c>w:ilvl</c>.</summary>
        private readonly List<(int Level, M.ListBlock List)> _open =
            new List<(int, M.ListBlock)>();

        /// <summary>The numbering id the open stack belongs to, or 0 when nothing is open.</summary>
        private int _numId;

        /// <param name="wordDoc">The document holding the numbering definitions.</param>
        /// <param name="resolver">The style resolver, for a numbering a paragraph style gives a paragraph; null reads a paragraph's own alone.</param>
        /// <param name="counters">The conversion's counters, shared by every grouping; null starts a fresh set.</param>
        internal DocxListGrouping(WordprocessingDocument wordDoc, StyleResolver resolver = null, Dictionary<int, int> counters = null)
        {
            _wordDoc = wordDoc;
            _resolver = resolver;
            _counters = counters ?? new Dictionary<int, int>();
        }

        /// <summary>
        /// Offers a paragraph to the group. Returns true when it was taken as a list item, in which
        /// case the caller must not add <paramref name="mapped"/> to the section itself.
        /// </summary>
        /// <remarks>
        /// A non-numbered paragraph, a table, or a numbered paragraph belonging to a different
        /// <c>w:numId</c> all end the run: the first two because the list stopped, the third
        /// because two adjacent ids are two lists and merging them would renumber both.
        /// </remarks>
        internal bool TryTake(W.Paragraph source, M.Paragraph mapped, List<M.IDocElement> sink)
        {
            (int NumId, int Level)? numbering = ListRenderer.NumberingOf(source, _resolver);
            if (numbering == null) return false;

            (int numId, int level) = numbering.Value;
            if (_open.Count > 0 && numId != _numId) Flush(sink);

            var item = new M.ListItem
            {
                Level = level,
                IsNumbered = ListRenderer.IsOrderedLevel(source, _wordDoc, _resolver),
                // A numbered paragraph with no marker — a level of format none — gets the empty
                // marker, which the exporter draws as none; null would be its default bullet.
                Marker = ListRenderer.GetListPrefix(source, _wordDoc, _counters, _resolver) ?? "",
                Direction = mapped.Direction,
                TextFormat = FirstTextRun(mapped),
            };
            item.Runs.AddRange(mapped.Runs);

            Place(item, numId, level, sink);
            return true;
        }

        /// <summary>Closes any open list into <paramref name="sink"/>.</summary>
        /// <remarks>
        /// Called when a non-list block arrives and again at the end of the section: a document
        /// ending on a list item would otherwise drop the whole list, which is the failure mode a
        /// flush-on-interruption design has and a flush-on-every-block design does not.
        /// </remarks>
        internal void Flush(List<M.IDocElement> sink)
        {
            if (_open.Count == 0) return;

            sink.Add(_open[0].List);
            _open.Clear();
            _numId = 0;
        }

        /// <summary>Puts an item on the stack at its level, opening or closing lists to reach it.</summary>
        private void Place(M.ListItem item, int numId, int level, List<M.IDocElement> sink)
        {
            // A deeper level than anything open starts a sublist of the item that precedes it; a
            // shallower one closes back to it. Word writes levels as absolute depths and allows a
            // jump of more than one, so this walks to the level rather than assuming ±1.
            while (_open.Count > 0 && _open[_open.Count - 1].Level > level)
                _open.RemoveAt(_open.Count - 1);

            if (_open.Count == 0)
            {
                var root = new M.ListBlock { IsOrdered = item.IsNumbered };
                root.Items.Add(item);
                _open.Add((level, root));
                _numId = numId;
                return;
            }

            (int openLevel, M.ListBlock openList) = _open[_open.Count - 1];

            if (openLevel == level)
            {
                openList.Items.Add(item);
                return;
            }

            // Deeper. It hangs off the last item of the list one level up — and if that list has no
            // item yet (a document whose first numbered paragraph is at level 2), the item is taken
            // at the open level instead, because a sublist with no parent item is not expressible
            // and inventing an empty parent would put an /LI with no content in the tag tree.
            if (openList.Items.Count == 0)
            {
                openList.Items.Add(item);
                return;
            }

            M.ListItem parent = openList.Items[openList.Items.Count - 1];
            var nested = parent.Sublist ?? new M.ListBlock { IsOrdered = item.IsNumbered };
            nested.Items.Add(item);
            parent.Sublist = nested;
            _open.Add((level, nested));
        }

        /// <summary>The run whose formatting the marker copies, or null.</summary>
        /// <remarks>
        /// The marker is drawn in the item's own size and colour, so it takes them from the first
        /// text run rather than from the document default — a heading-sized list item whose bullet
        /// was body-sized is the visible symptom of getting this from the wrong place.
        /// </remarks>
        private static M.TextRun FirstTextRun(M.Paragraph mapped)
        {
            foreach (M.IInlineElement run in mapped.Runs)
                if (run is M.TextRun text) return text;

            return null;
        }
    }
}
