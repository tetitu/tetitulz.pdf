// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Writes the <c>numbering.xml</c> part a DOCX needs for a list Word will renumber, and
    /// allocates one numbering definition per exported list.
    /// </summary>
    /// <remarks>
    /// <para><b>Not the marker as a literal text run.</b> That produces a document which
    /// <i>looks</i> like a list and is a run of paragraphs beginning with
    /// characters: inserting an item renumbers nothing, deleting one leaves a gap, Word's list
    /// commands do not apply, and the numbers are picked up as text by a copy. A native list needs
    /// a <c>w:numPr</c> on each paragraph pointing at a <c>w:num</c>, which points at a
    /// <c>w:abstractNum</c> in a part this package had never written at all.</para>
    ///
    /// <para><b>An item that overrides its marker gets an instance of its own, not a typed run.</b>
    /// <c>ListItem.Marker</c> is an arbitrary string — and so is Word's level text
    /// (<c>w:lvlText</c>). A bullet-format level whose text is the marker draws exactly the marker
    /// the model recorded, and the paragraph stays a list item Word indents and recognises. A
    /// resolved string has no scheme to rebuild a definition from, but a level text needs
    /// none.</para>
    ///
    /// <para><b>The instance points at the list's own abstract definition and overrides one
    /// level</b>, with no <c>w:startOverride</c>. Instances sharing an abstract definition without a
    /// start override are one list for counting purposes (§17.9.16), so the overriding paragraph
    /// still consumes its ordinal — which is what <c>ModelRules.ListMarker</c> gives the item after
    /// it (<c>Start + index</c>) and what the PDF and HTML exports draw. A private abstract
    /// definition would number that item one lower here than in the other three exports.</para>
    ///
    /// <para><b>What a level text cannot carry</b> is a percent sign — <c>%N</c> is Word's
    /// placeholder for level N's counter and the format has no escape for a literal one — or a
    /// control character. Those markers keep the typed run, and the exporter says so.</para>
    ///
    /// <para><b>Nine levels, because that is what Word has.</b> The model's nesting cap is higher
    /// (<c>ModelRules.MaxListDepth</c>), so a deeper item is clamped onto the last level rather
    /// than referring to one that does not exist — an out-of-range <c>w:ilvl</c> is a repair prompt,
    /// and a deeper indent is not worth one.</para>
    /// </remarks>
    internal sealed class DocxNumbering
    {
        /// <summary>Levels Word's numbering model defines, 0 through 8.</summary>
        internal const int MaxLevels = 9;

        /// <summary>Left indent per level, in twips — Word's own default list step.</summary>
        private const int LevelIndentTwips = 360;

        private readonly MainDocumentPart _main;
        private Numbering? _numbering;
        private int _nextId = 1;

        /// <summary>
        /// The abstract definition each list instance points at, so an overriding item's instance
        /// can share it.
        /// </summary>
        private readonly Dictionary<int, int> _abstractOf = new Dictionary<int, int>();

        /// <summary>Literal-marker instances already minted, keyed by list instance, level and text.</summary>
        private readonly Dictionary<(int listNumId, int level, string text), int> _literal =
            new Dictionary<(int listNumId, int level, string text), int>();

        internal DocxNumbering(MainDocumentPart main) => _main = main;

        /// <summary>
        /// A numbering id for <paramref name="list"/>, or null when there is no list to describe.
        /// </summary>
        /// <remarks>
        /// Called once per top-level list. Every list gets its own definition so that
        /// <see cref="Model.ListBlock.Start"/> and the sequence belong to it alone: sharing one
        /// would make a second list continue the first's numbers.
        /// </remarks>
        internal int? Allocate(Model.ListBlock? list)
        {
            if (list == null || list.Items.Count == 0) return null;

            Numbering numbering = EnsurePart();

            int abstractId = _nextId;
            int numId = _nextId;
            _nextId++;

            AddAbstractNum(numbering, BuildAbstractNum(abstractId, list));
            numbering.Append(new NumberingInstance(new AbstractNumId { Val = abstractId })
            {
                NumberID = numId
            });

            _abstractOf[numId] = abstractId;
            return numId;
        }

        /// <summary>
        /// An instance of <paramref name="listNumId"/>'s definition whose level
        /// <paramref name="level"/> draws <paramref name="marker"/> literally, or null when no level
        /// text can carry it.
        /// </summary>
        /// <remarks>
        /// <para>One instance per distinct (list, level, text): twenty items marked "—" are twenty
        /// paragraphs and one <c>w:num</c>.</para>
        /// <para>The marker is trimmed. The level's own hanging indent separates it from the text
        /// — that is what the tab after a Word marker is — so a trailing space, which the model's
        /// derived markers all carry, would sit inside the tab and draw nothing.</para>
        /// </remarks>
        internal int? LiteralMarkerInstance(int listNumId, int level, string marker)
        {
            string text = (marker ?? "").Trim();
            if (!CanBeLevelText(text)) return null;
            if (!_abstractOf.TryGetValue(listNumId, out int abstractId)) return null;

            var key = (listNumId, level, text);
            if (_literal.TryGetValue(key, out int existing)) return existing;

            Numbering numbering = EnsurePart();
            int numId = _nextId++;

            numbering.Append(new NumberingInstance(
                new AbstractNumId { Val = abstractId },
                new LevelOverride(BuildLevel(level, NumberFormatValues.Bullet, text, start: 1))
                {
                    LevelIndex = level,
                })
            {
                NumberID = numId
            });

            _literal[key] = numId;
            return numId;
        }

        /// <summary>Whether Word's level text can carry <paramref name="text"/> literally.</summary>
        /// <remarks>
        /// <c>%</c> introduces a counter placeholder (<c>%1</c>) and the format has no escape for a
        /// literal one; a control character is not text. Everything else is a level text — including
        /// the empty string, which Word draws as a list item with no visible marker, exactly as the
        /// other three exporters draw an empty <c>Marker</c>.
        /// </remarks>
        internal static bool CanBeLevelText(string text)
        {
            foreach (char c in text)
                if (c == '%' || char.IsControl(c)) return false;
            return true;
        }

        /// <summary>The <c>w:ilvl</c> for a model depth, clamped to what Word defines.</summary>
        internal static int LevelFor(int depth)
            => depth < 0 ? 0 : depth >= MaxLevels ? MaxLevels - 1 : depth;

        /// <summary>
        /// Adds a <c>w:abstractNum</c> keeping the schema's element order: every <c>w:abstractNum</c>
        /// before any <c>w:num</c> (<c>CT_Numbering</c>, ISO/IEC 29500 §17.9.17).
        /// </summary>
        /// <remarks>
        /// Appending it at the end was wrong whenever a <c>w:num</c> was already there — a second
        /// list's definition, or any list allocated after a literal-marker instance wrote its
        /// <c>w:num</c>, landed after that instance. The out-of-order part makes Word repair it on
        /// open and a strict OOXML validator reject it. Inserting after the last existing
        /// <c>w:abstractNum</c> (or before the first <c>w:num</c> when there is none yet) keeps the
        /// two groups apart however the two allocators interleave; this exporter writes no
        /// <c>w:numPicBullet</c>, the only element that would precede an abstract definition.
        /// </remarks>
        private static void AddAbstractNum(Numbering numbering, AbstractNum abstractNum)
        {
            AbstractNum? lastAbstract = numbering.Elements<AbstractNum>().LastOrDefault();
            if (lastAbstract != null) { numbering.InsertAfter(abstractNum, lastAbstract); return; }

            NumberingInstance? firstNum = numbering.Elements<NumberingInstance>().FirstOrDefault();
            if (firstNum != null) numbering.InsertBefore(abstractNum, firstNum);
            else numbering.Append(abstractNum);
        }

        /// <summary>Creates the part on first use and returns its root.</summary>
        /// <remarks>
        /// <b>There is deliberately no Save.</b> Assigning <c>part.Numbering</c> is enough:
        /// <c>WordprocessingDocument.Dispose</c> flushes every part it owns, which is why
        /// <c>mainPart.Document</c> is not saved explicitly either. An explicit
        /// <c>_numbering.Save()</c> was written here first and deleting it changed no exported
        /// byte — recorded so the next reader does not add it back as a safeguard against a
        /// hazard that does not exist.
        /// </remarks>
        private Numbering EnsurePart()
        {
            if (_numbering != null) return _numbering;

            NumberingDefinitionsPart part = _main.NumberingDefinitionsPart
                                            ?? _main.AddNewPart<NumberingDefinitionsPart>();
            _numbering = part.Numbering ?? new Numbering();
            part.Numbering = _numbering;
            return _numbering;
        }

        /// <summary>One definition, nine levels deep, in the list's own scheme.</summary>
        /// <remarks>
        /// Only the first level takes <see cref="Model.ListBlock.Start"/>: it is the list's
        /// starting ordinal, and a sublist restarts at 1 the way every renderer of this model
        /// already draws it.
        /// </remarks>
        private static AbstractNum BuildAbstractNum(int abstractId, Model.ListBlock list)
        {
            var abstractNum = new AbstractNum { AbstractNumberId = abstractId };

            for (int level = 0; level < MaxLevels; level++)
            {
                abstractNum.Append(BuildLevel(level,
                    list.IsOrdered ? NumberFormatValues.Decimal : NumberFormatValues.Bullet,
                    LevelTextFor(list, level),
                    start: level == 0 ? StartOf(list) : 1));
            }

            return abstractNum;
        }

        /// <summary>
        /// One level: its start, format, text, and Word's default indent step for that depth.
        /// </summary>
        /// <remarks>
        /// Shared by the abstract definition and by a literal-marker override, so an overriding
        /// item keeps the indent of the level it replaces rather than sitting flush with its parent.
        /// </remarks>
        private static Level BuildLevel(int level, NumberFormatValues format, string text, int start)
        {
            int indent = (level + 1) * LevelIndentTwips;

            return new Level(
                new StartNumberingValue { Val = start },
                new NumberingFormat { Val = format },
                new LevelText { Val = text },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation
                {
                    Left = indent.ToString(CultureInfo.InvariantCulture),
                    Hanging = LevelIndentTwips.ToString(CultureInfo.InvariantCulture),
                }))
            { LevelIndex = level };
        }

        /// <summary>
        /// <c>%N.</c> for an ordered level, or the bullet the model would have drawn at that depth.
        /// </summary>
        /// <remarks>
        /// The bullet comes from <c>ModelRules</c> rather than from a literal here, so the DOCX,
        /// PDF, HTML and ODT exports of one document all cycle disc/circle/square together. Writing
        /// <c>"•"</c> at every level would be the second copy of that decision and would
        /// disagree with the other three at depth.
        /// </remarks>
        private static string LevelTextFor(Model.ListBlock list, int level)
            => list.IsOrdered
                ? "%" + (level + 1).ToString(CultureInfo.InvariantCulture) + "."
                : Model.ModelRules.BulletForDepth(level);

        /// <summary>The list's first ordinal, floored at 1 — Word rejects a start below it.</summary>
        private static int StartOf(Model.ListBlock list) => list.Start < 1 ? 1 : list.Start;
    }
}
