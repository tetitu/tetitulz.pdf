// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Writes the <c>footnotes.xml</c> part a DOCX needs for a note Word will maintain — or, for
    /// endnotes, the <c>endnotes.xml</c> part — and allocates an id per note.
    /// </summary>
    /// <remarks>
    /// <para><b>A Word footnote is a reference, not a run of text.</b> The body carries a
    /// <c>w:footnoteReference</c> naming an id; the note itself lives in a separate part. Writing
    /// the note's text inline instead would put it in the middle of the sentence — the one
    /// place a footnote must not be — and dropping it would lose it.</para>
    ///
    /// <para><b>Ids 0 and 1 are reserved and the part must define them.</b> §17.11.17 gives
    /// them to the separator and continuation separators, and Word repairs a document whose
    /// <c>footnotes.xml</c> omits them even when nothing references them. Real notes therefore
    /// start at 2.</para>
    ///
    /// <para><b>One writer for both kinds.</b> An endnote (§17.11.2–17.11.9) is the same shape in
    /// its own part — <c>w:endnotes</c>, <c>w:endnote</c>, <c>w:endnoteRef</c>, the
    /// <c>EndnoteText</c> and <c>EndnoteReference</c> styles — so the kind is a constructor choice
    /// and the element types are the only thing that differs; a second class for endnotes would be
    /// the copy that drifts.</para>
    /// </remarks>
    internal sealed class DocxFootnotes
    {
        /// <summary>The first id a document's own notes may use; 0 and 1 are the separators.</summary>
        private const int FirstNoteId = 2;

        private readonly MainDocumentPart _main;
        private readonly bool _endnotes;
        private readonly Dictionary<Model.Footnote, int> _ids =
            new Dictionary<Model.Footnote, int>();

        private OpenXmlCompositeElement? _part;
        private int _nextId = FirstNoteId;

        /// <param name="main">The document the part belongs to.</param>
        /// <param name="endnotes">Write the endnotes part rather than the footnotes part.</param>
        internal DocxFootnotes(MainDocumentPart main, bool endnotes = false)
        {
            _main = main;
            _endnotes = endnotes;
        }

        /// <summary>Whether this writes endnotes (<c>w:endnoteReference</c>) rather than footnotes.</summary>
        internal bool IsEndnotes => _endnotes;

        /// <summary>
        /// The id for <paramref name="note"/>, writing it into the part the first time it is asked
        /// for.
        /// </summary>
        /// <param name="note">The note to place.</param>
        /// <param name="writeContent">
        /// Fills the note's paragraph. Called once per note, by the exporter, so the note's runs go
        /// through the same run writer as body text and gain its formatting, links and RTL
        /// handling.
        /// </param>
        internal int IdFor(Model.Footnote note, System.Action<Paragraph> writeContent)
        {
            if (_ids.TryGetValue(note, out int existing)) return existing;

            OpenXmlCompositeElement part = EnsurePart();

            int id = _nextId++;
            _ids[note] = id;

            var paragraph = new Paragraph(new ParagraphProperties(
                new ParagraphStyleId { Val = _endnotes ? EndnoteTextStyleId : FootnoteTextStyleId }));

            // The reference mark inside the note, which is what makes Word draw the number in
            // front of the note's own text rather than leaving it bare. A note with an explicit
            // Mark writes that text here instead of the automatic number (w:footnoteRef): with
            // w:customMarkFollows on the body's reference, this is the half that keeps the dagger
            // in the body from pointing at a "1" at the foot of the page.
            var mark = new Run(new RunProperties(new RunStyle { Val = _endnotes ? EndnoteReferenceStyleId : FootnoteReferenceStyleId }));
            if (note.Mark != null)
                mark.Append(new Text(XmlText.ForOpenXml(note.Mark)) { Space = SpaceProcessingModeValues.Preserve });
            else if (_endnotes)
                mark.Append(new EndnoteReferenceMark());
            else
                mark.Append(new FootnoteReferenceMark());
            paragraph.Append(mark);

            writeContent(paragraph);

            part.Append(NoteElement(paragraph, id, null));
            return id;
        }

        /// <summary>The style a note's paragraph names, and the one its mark does.</summary>
        /// <remarks>
        /// Named rather than defined: Word supplies both from its built-in style table, and a
        /// definition written here would override the user's own template for every note.
        /// </remarks>
        private const string FootnoteTextStyleId = "FootnoteText";

        /// <inheritdoc cref="FootnoteTextStyleId"/>
        private const string FootnoteReferenceStyleId = "FootnoteReference";

        /// <inheritdoc cref="FootnoteTextStyleId"/>
        private const string EndnoteTextStyleId = "EndnoteText";

        /// <inheritdoc cref="FootnoteTextStyleId"/>
        private const string EndnoteReferenceStyleId = "EndnoteReference";

        /// <summary>A <c>w:footnote</c> or <c>w:endnote</c> element holding <paramref name="content"/>.</summary>
        private OpenXmlElement NoteElement(OpenXmlElement content, int id, FootnoteEndnoteValues? type)
        {
            if (_endnotes)
            {
                var endnote = new Endnote(content) { Id = id };
                if (type.HasValue) endnote.Type = type.Value;
                return endnote;
            }
            var footnote = new Footnote(content) { Id = id };
            if (type.HasValue) footnote.Type = type.Value;
            return footnote;
        }

        /// <summary>Creates the part with its two mandatory separators on first use.</summary>
        private OpenXmlCompositeElement EnsurePart()
        {
            if (_part != null) return _part;

            if (_endnotes)
            {
                EndnotesPart endnotes = _main.EndnotesPart ?? _main.AddNewPart<EndnotesPart>();
                Endnotes root = endnotes.Endnotes ?? new Endnotes();
                endnotes.Endnotes = root;
                _part = root;
            }
            else
            {
                FootnotesPart footnotes = _main.FootnotesPart ?? _main.AddNewPart<FootnotesPart>();
                Footnotes root = footnotes.Footnotes ?? new Footnotes();
                footnotes.Footnotes = root;
                _part = root;
            }

            // §17.11.17: id 0 is the separator drawn above the notes, id 1 the one drawn when a
            // note continues onto the next page. Word repairs a part that omits them, whether or
            // not any note references them; the endnotes part needs the same two (§17.11.9).
            _part.Append(NoteElement(new Paragraph(new Run(new SeparatorMark())), 0, FootnoteEndnoteValues.Separator));
            _part.Append(NoteElement(new Paragraph(new Run(new ContinuationSeparatorMark())), 1, FootnoteEndnoteValues.ContinuationSeparator));

            return _part;
        }
    }
}
