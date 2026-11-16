// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Builder.Model
{
    /// <summary>
    /// The model questions every exporter has to answer the same way.
    /// </summary>
    /// <remarks>
    /// <para>One <see cref="Document"/> goes out as PDF, HTML, DOCX and ODT, so a rule implemented
    /// in one exporter and not the others is not a missing feature — it is the <i>same document
    /// rendering differently in four files</i>, which is the case a caller has no way to diagnose.
    /// Three rules drift exactly that way when each exporter answers them itself, so they are
    /// collected here:</para>
    /// <list type="bullet">
    /// <item><see cref="TextDirection.Auto"/> was wired into <c>PdfExporter</c> and the other three
    /// still tested <c>== RightToLeft</c>, so an auto-detected Hebrew paragraph came out
    /// right-to-left in the PDF and left-to-right everywhere else.</item>
    /// <item><see cref="ListItem.Marker"/> and <see cref="ListItem.Runs"/> — both documented as
    /// overriding their plain counterparts — were honoured by the PDF and HTML exporters and
    /// ignored by DOCX and ODT, which dropped the item's entire text when it lived in
    /// <c>Runs</c>.</item>
    /// <item><see cref="TextAlignment.Start"/> and <see cref="TextAlignment.End"/> post-date the
    /// alignment switches that were never given an arm for them.</item>
    /// </list>
    /// <para><b>Internal on purpose.</b> These are one implementation of a decision, not new public
    /// surface: adding them to <see cref="ListItem"/> and <see cref="TextDirection"/> would change
    /// the package's exported API for no caller-visible gain.</para>
    /// </remarks>
    internal static class ModelRules
    {
        /// <summary>
        /// <paramref name="blocks"/> with each placed paragraph followed by its placement's
        /// <see cref="AbsolutePlacement.Following"/> paragraphs and its
        /// <see cref="AbsolutePlacement.FollowingBlocks"/> — what an exporter that draws no placement
        /// (HTML, DOCX, ODT) writes in the flow, so the blocks of a positioned element after its first
        /// are not lost there. A following block the PDF exporter would not draw is reported and
        /// passed over there too (<see cref="AbsolutePlacement.CanFollow"/>).
        /// </summary>
        public static IEnumerable<IDocElement> InFlow(IEnumerable<IDocElement> blocks) => InFlow(blocks, 0);

        /// <summary>How deep placements may nest in one another's following blocks before the rest are passed over.</summary>
        internal const int MaxPlacementNesting = 32;

        private static IEnumerable<IDocElement> InFlow(IEnumerable<IDocElement> blocks, int depth)
        {
            foreach (IDocElement block in blocks)
            {
                yield return block;
                if (block is not Paragraph { Placement: AbsolutePlacement place } || depth >= MaxPlacementNesting) continue;
                foreach (Paragraph following in place.Following)
                    if (following != null) yield return following;
                // A placement among them, against a box among them, brings its own following blocks.
                foreach (IDocElement following in InFlow(PlaceableFollowingBlocks(place, report: true), depth + 1))
                    yield return following;
            }
        }

        /// <summary>
        /// A placement's <see cref="AbsolutePlacement.FollowingBlocks"/> that are drawn
        /// (<see cref="AbsolutePlacement.CanFollow(IDocElement?, IReadOnlyList{IDocElement})"/>), the others
        /// reported when <paramref name="report"/> — the boxes among them collected once, so the question
        /// is linear in the list: each block's scan of it was quadratic (a turned XFA flow of forty
        /// thousand positioned instances, 3.2e9 comparisons a pass).
        /// </summary>
        internal static List<IDocElement> PlaceableFollowingBlocks(AbsolutePlacement place, bool report)
        {
            HashSet<BoxStart> boxes = AbsolutePlacement.BoxesAmong(place.FollowingBlocks);
            var blocks = new List<IDocElement>(place.FollowingBlocks.Count);
            foreach (IDocElement block in place.FollowingBlocks)
            {
                if (AbsolutePlacement.CanFollowBoxes(block, boxes)) blocks.Add(block);
                else if (report) ReportUnplaceable(block);
            }
            return blocks;
        }

        /// <summary>
        /// Whether a block may follow a placed paragraph (<see cref="AbsolutePlacement.FollowingBlocks"/>):
        /// what a table cell holds — a paragraph in the flow (not placed itself), a table, a list or a
        /// list item, a box's start or end.
        /// </summary>
        public static bool IsPlaceableBlock(IDocElement? block)
            => AbsolutePlacement.CanFollow(block, System.Array.Empty<IDocElement>());

        /// <summary>
        /// Whether <paramref name="block"/> may follow a placed paragraph among <paramref name="among"/>:
        /// what <see cref="IsPlaceableBlock(IDocElement)"/> admits, and a paragraph placed against a box
        /// that opens among them — a positioned element inside a positioned one, which a cell's layout
        /// draws with the box's fragment, measured from its padding box (CSS 2.1 §10.1).
        /// </summary>
        public static bool IsPlaceableBlock(IDocElement? block, IReadOnlyList<IDocElement> among)
            => AbsolutePlacement.CanFollow(block, among);

        /// <summary>Reports a following block of a kind no exporter draws there; a null one is nothing to report.</summary>
        public static void ReportUnplaceable(IDocElement? block)
        {
            if (block == null) return;
            Export.BuilderDiagnostics.Report(Export.BuilderDiagnosticKind.UnsupportedByExportFormat,
                "AbsolutePlacement.FollowingBlocks holds a " + (block is Paragraph ? "placed paragraph" : block.GetType().Name) +
                "; only what a table cell holds, a paragraph in the flow among it, follows a placed paragraph, and it was not written.");
        }

        /// <summary>
        /// Whether a direction resolves right-to-left, detecting <see cref="TextDirection.Auto"/>
        /// from <paramref name="sample"/> per UAX #9 P2/P3 (the first strong character decides).
        /// </summary>
        public static bool IsRightToLeft(TextDirection direction, string sample)
        {
            switch (direction)
            {
                case TextDirection.RightToLeft: return true;
                case TextDirection.Auto:
                    return UnicodeBidi.IsRightToLeftParagraph(sample ?? "", BaseDirection.Auto);
                default: return false;
            }
        }

        /// <summary>Whether a paragraph resolves right-to-left, falling back to its section.</summary>
        public static bool IsRightToLeft(Paragraph para, Section section)
            => IsRightToLeft(para?.Direction ?? section?.Direction ?? TextDirection.LeftToRight,
                             SampleText(para));

        /// <summary>Whether a section resolves right-to-left, sampling its own content for Auto.</summary>
        /// <remarks>
        /// Sampled only for Auto. The PDF exporter asks once per box and table, and the sample was
        /// taken whatever the direction: sixty thousand empty boxes in one section were sixty
        /// thousand walks of its 120 000 blocks.
        /// </remarks>
        public static bool IsRightToLeft(Section section)
            => section != null && (section.Direction == TextDirection.Auto
                ? IsRightToLeft(TextDirection.Auto, SampleText(section))
                : IsRightToLeft(section.Direction, ""));

        /// <summary>
        /// The CSS/DOCX/ODT-facing direction of a document element, as one of the three states an
        /// exporter has to write: right-to-left, left-to-right, or "detect it from the content".
        /// </summary>
        public static TextDirection Declared(Paragraph para, Section section)
            => para?.Direction ?? section?.Direction ?? TextDirection.LeftToRight;

        /// <summary>
        /// The marker a <b>loose</b> list item is drawn with: its explicit
        /// <see cref="ListItem.Marker"/> when set, else the bullet or number derived from
        /// <see cref="ListItem.IsNumbered"/>.
        /// </summary>
        /// <remarks>
        /// <b>The derived number is the item's <i>level</i>, which is not an ordinal</b>, so ten
        /// sibling items all render "1. ". That is not a defect to fix here: a loose item has no
        /// siblings to count, because nothing says which items belong together. The fix is to put
        /// them in a <see cref="ListBlock"/>, which is what <see cref="ListMarker(ListBlock, int)"/>
        /// numbers.
        /// </remarks>
        public static string ListMarker(ListItem li)
        {
            if (li == null) return "";
            return li.Marker ?? (li.IsNumbered ? (li.Level + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture) + ". " : "• ");
        }

        /// <summary>
        /// The marker for item <paramref name="index"/> of <paramref name="list"/> — the ordinal a
        /// container can compute and a loose item cannot.
        /// </summary>
        /// <remarks>
        /// <para><b>An explicit <see cref="ListItem.Marker"/> still wins.</b> The model documents it
        /// as overriding what is derived, and a producer that has already resolved "1.2." or "א."
        /// knows something this cannot: <c>HtmlToBuilderMapper</c> resolves CSS's
        /// <c>list-style-type</c>, and DOCX numbering can restart, skip and share a sequence across
        /// paragraphs.</para>
        ///
        /// <para><b>The ordinal counts from <see cref="ListBlock.Start"/></b>, which is HTML's
        /// <c>&lt;ol start&gt;</c> and the reason a list continued after an interruption does not
        /// begin again at one.</para>
        ///
        /// <para>The bullet cycles with depth the way browsers do — disc, circle, square — using the
        /// item's own <see cref="ListItem.Level"/>, so a nested list is visually distinguishable
        /// even in the formats that draw the marker as literal text.</para>
        /// </remarks>
        public static string ListMarker(ListBlock list, int index)
        {
            if (list == null) return "";
            if (index < 0 || index >= list.Items.Count) return "";

            ListItem li = list.Items[index];
            if (li?.Marker != null) return li.Marker;

            if (list.IsOrdered)
            {
                int ordinal = list.Start + index;
                return ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". ";
            }

            return Bullets[System.Math.Max(0, li?.Level ?? 0) % Bullets.Length] + " ";
        }

        /// <summary>The bullet a list draws at <paramref name="depth"/>.</summary>
        /// <remarks>
        /// <b>Its own member because the DOCX exporter writes the cycle rather than drawing it</b>: it
        /// goes into a <c>numbering.xml</c> level definition, where a literal would be a second
        /// copy of the decision and would disagree with the other three exporters from the second
        /// level down. Reachable to the assembly only — <c>ModelRules</c> is internal, so this
        /// adds nothing to the public surface.
        /// </remarks>
        public static string BulletForDepth(int depth)
            => Bullets[System.Math.Max(0, depth) % Bullets.Length];

        /// <summary>Bullet glyphs by nesting depth, cycling — the browser default sequence.</summary>
        private static readonly string[] Bullets = { "\u2022", "\u25e6", "\u25aa" };

        /// <summary>
        /// Every list item in <paramref name="list"/> and its sublists, in reading order, paired
        /// with the marker each is drawn with.
        /// </summary>
        /// <remarks>
        /// For the three exporters that draw a marker as literal text rather than declaring a list
        /// structure. Flattening loses the nesting, which those formats could not express anyway;
        /// the indent that stands in for it is the item's <see cref="ListItem.Level"/>, which
        /// this fills in from
        /// the depth it walked.
        /// </remarks>
        public static List<(ListItem Item, string Marker)> Flatten(ListBlock list)
        {
            var flat = new List<(ListItem, string)>();
            Flatten(list, 0, flat);
            return flat;
        }

        private static void Flatten(ListBlock? list, int depth, List<(ListItem, string)> into)
        {
            if (list == null || depth > MaxListDepth) return;

            for (int i = 0; i < list.Items.Count; i++)
            {
                ListItem li = list.Items[i];
                if (li == null) continue;

                // The depth walked is authoritative: a producer that set Level itself may have set
                // it inconsistently with where it actually put the item, and the indent has to
                // follow the structure or a sublist is drawn flush with its parent.
                li.Level = depth;
                into.Add((li, ListMarker(list, i)));
                Flatten(li.Sublist, depth + 1, into);
            }
        }

        /// <summary>
        /// Nesting cap, so a cyclic <see cref="ListItem.Sublist"/> cannot walk for ever.
        /// </summary>
        /// <remarks>
        /// The model is a public mutable graph, so an item can be made its own descendant by a
        /// caller — accidentally or otherwise — and every walk of it needs a bound. Twenty is far
        /// past any real document: HTML's own list nesting bottoms out well before it, and a deeper
        /// one is unreadable regardless.
        /// </remarks>
        internal const int MaxListDepth = 20;

        /// <summary>
        /// A list item's inline content: <see cref="ListItem.Runs"/> when non-empty (which the
        /// model documents as overriding <see cref="ListItem.Text"/>), else a single run carrying
        /// <c>Text</c> in the item's own format.
        /// </summary>
        public static IList<IInlineElement> ListRuns(ListItem li)
        {
            if (li == null) return new List<IInlineElement>();
            if (li.Runs.Count > 0) return li.Runs;

            var run = li.TextFormat != null ? li.TextFormat.Clone() : new TextRun();
            run.Text = li.Text ?? "";
            return new List<IInlineElement> { run };
        }

        /// <summary>
        /// The physical alignment an element resolves to, or <c>null</c> when it is the natural
        /// alignment for its direction and the format should therefore write nothing.
        /// </summary>
        /// <remarks>
        /// Returning <c>null</c> rather than the natural value is what keeps an unset alignment
        /// unset in the file. Writing it explicitly is not harmless: an explicit "left" on every
        /// paragraph overrides the right alignment a DOCX <c>&lt;w:bidi/&gt;</c> would otherwise
        /// give an RTL paragraph.
        /// </remarks>
        public static TextAlignment? ResolvePhysicalAlignment(TextAlignment alignment, bool rtl)
        {
            TextAlignment resolved;
            switch (alignment)
            {
                case TextAlignment.Center:  return TextAlignment.Center;
                case TextAlignment.Justify: return TextAlignment.Justify;
                case TextAlignment.Left:    resolved = TextAlignment.Left; break;
                case TextAlignment.Right:   resolved = TextAlignment.Right; break;
                case TextAlignment.Start:   resolved = rtl ? TextAlignment.Right : TextAlignment.Left; break;
                default:                    resolved = rtl ? TextAlignment.Left : TextAlignment.Right; break;
            }
            TextAlignment natural = rtl ? TextAlignment.Right : TextAlignment.Left;
            return resolved == natural ? (TextAlignment?)null : resolved;
        }

        /// <summary>Text of a paragraph's runs, for first-strong direction detection.</summary>
        public static string SampleText(Paragraph? para)
        {
            if (para == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var run in para.Runs)
            {
                if (run is TextRun tr) sb.Append(tr.Text);
                if (sb.Length >= SampleLimit) break;
            }
            return sb.ToString();
        }

        /// <summary>Text of a section's first blocks, for first-strong direction detection.</summary>
        public static string SampleText(Section? section)
        {
            if (section == null) return "";
            var sb = new System.Text.StringBuilder();
            int examined = 0;
            foreach (var child in section.Children)
            {
                if (child is Paragraph p) sb.Append(SampleText(p));
                else if (child is ListItem li) sb.Append(li.Text);
                // A prefix of blocks as well as of characters: a section of text-free blocks — boxes,
                // tables — was walked to its end on every Auto question.
                SampledBlocks++;
                if (sb.Length >= SampleLimit || ++examined >= SampleLimit) break;
            }
            return sb.ToString();
        }

        /// <summary>The section blocks <see cref="SampleText(Section)"/> has looked at on this thread — the evidence for its bound.</summary>
        [System.ThreadStatic] internal static long SampledBlocks;

        /// <summary>
        /// How much text the Auto detection reads, and how many of a section's blocks it looks
        /// through for it. The first <i>strong</i> character decides, so a prefix is enough, and a
        /// whole 5 000-page section's text is not worth concatenating to find it.
        /// </summary>
        private const int SampleLimit = 512;
    }
}
