// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// The distinct <see cref="TextRun"/> formattings a document uses, as ODF automatic text
    /// styles.
    ///
    /// <para><b>Why a table and not inline attributes.</b> ODF has no inline style attribute: a
    /// <c>&lt;text:span&gt;</c> can only name a style, and automatic styles live in their own
    /// element <i>before</i> <c>&lt;office:body&gt;</c>. A single-pass writer therefore has nowhere
    /// to put a style at the moment it discovers it needs one — which is exactly why the ODT
    /// exporter wrote every run as bare text and dropped bold, italic, underline, strike, colour,
    /// size, family and super/subscript <b>without so much as a diagnostic</b>. Collecting first
    /// and writing second is what makes the declaration site available.</para>
    ///
    /// <para><b>Deduplicated by formatting, not by run.</b> A document that bolds forty words
    /// declares one style, not forty. The key is the formatting itself, so two runs that look the
    /// same share a style however far apart they are.</para>
    /// </summary>
    internal sealed class OdtRunStyleTable
    {
        /// <summary>
        /// Ceiling on distinct declared styles. The count is driven by document content, and a
        /// generated document can hold a great many one-off formattings (a colour per row, say);
        /// past this, runs keep their text and lose their formatting rather than the file growing a
        /// style declaration per word. Reported once so the loss is never silent.
        /// </summary>
        internal const int MaxStyles = 4096;

        private readonly Dictionary<string, string> _namesByKey =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<(string name, string properties)> _declared =
            new List<(string, string)>();
        private bool _reportedOverflow;

        /// <summary>Walks blocks, recording every distinct run formatting it finds.</summary>
        /// <param name="blocks">The blocks to walk.</param>
        /// <param name="depth">
        /// How many tables enclose these blocks. A cell may hold its own table, so this style-gathering
        /// pre-pass is bounded at the same <see cref="PdfLimits.MaxTableNestingDepth"/> the writer uses —
        /// otherwise a cyclic or very deep nesting overflows the stack here, before the writer is reached.
        /// The dropped tables are not written, so their styles need not be collected; the user-facing
        /// truncation is reported once, by the writer.
        /// </param>
        internal void Collect(IEnumerable<IDocElement> blocks, int depth = 0)
        {
            if (blocks == null || depth > PdfLimits.MaxTableNestingDepth) return;
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph p: CollectRuns(p.Runs); break;
                    case ListItem li: CollectRuns(ModelRules.ListRuns(li)); break;
                    // A native list (<text:list>) is written by WriteList — section-level and in a
                    // cell — but was never walked here, so every styled run inside a list item, and
                    // in every sublist, reached the body write with no declared style: NameFor then
                    // minted a name whose declaration block was already closed, and the span
                    // referenced a style the file did not contain. Walk it as WriteList does.
                    case ListBlock lb: CollectList(lb, 0); break;
                    case Table t:
                        foreach (var row in t.Rows)
                            foreach (var cell in row.Cells)
                            {
                                // BOTH cell shapes. Blocks supersedes Paragraphs when non-empty for
                                // rendering, but a table can mix the two across its cells, and a
                                // style missed here becomes a run written unformatted — so this
                                // walks each, rather than picking one the way a renderer must.
                                foreach (var cp in cell.Paragraphs) CollectRuns(cp.Runs);
                                Collect(cell.Blocks, depth + 1);
                            }
                        break;
                }
            }
        }

        private void CollectRuns(IEnumerable<IInlineElement> runs)
        {
            if (runs == null) return;
            foreach (var run in runs)
                if (run is TextRun tr) NameFor(tr);
        }

        /// <summary>
        /// Walks a native list and every sublist, recording each item's run formatting. Bounded by
        /// <see cref="ModelRules.MaxListDepth"/> — the same ceiling <c>WriteList</c> stops at — so a
        /// cyclic or very deep list cannot overflow the stack in this pre-pass, and the items the
        /// writer drops at that depth are not collected here either.
        /// </summary>
        private void CollectList(ListBlock? list, int depth)
        {
            if (list == null || depth > ModelRules.MaxListDepth) return;
            foreach (var li in list.Items)
            {
                if (li == null) continue;
                CollectRuns(ModelRules.ListRuns(li));
                if (li.Sublist != null) CollectList(li.Sublist, depth + 1);
            }
        }

        /// <summary>
        /// The style name for this run's formatting, registering it if new — or <c>null</c> when
        /// the run carries no formatting at all, so an unstyled document is written exactly as it
        /// was before and no reader has to step through an empty span on every word.
        /// </summary>
        internal string? NameFor(TextRun run)
        {
            if (run == null) return null;
            string properties = PropertiesFor(run);
            if (properties.Length == 0) return null;

            if (_namesByKey.TryGetValue(properties, out string? existing)) return existing;

            if (_declared.Count >= MaxStyles)
            {
                if (!_reportedOverflow)
                {
                    _reportedOverflow = true;
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                        $"the document uses more than {MaxStyles} distinct run formattings; the " +
                        "remainder were written as unformatted text rather than growing one style " +
                        "declaration per run.");
                }
                return null;
            }

            string name = "T" + (_declared.Count + 1).ToString(CultureInfo.InvariantCulture);
            _namesByKey[properties] = name;
            _declared.Add((name, properties));
            return name;
        }

        /// <summary>Writes the <c>style:style</c> declarations, inside the caller's automatic-styles element.</summary>
        internal void WriteDeclarations(StreamWriter w)
        {
            foreach (var (name, properties) in _declared)
            {
                w.Write("<style:style style:name=\"");
                w.Write(name);
                w.Write("\" style:family=\"text\"><style:text-properties ");
                w.Write(properties);
                w.Write("/></style:style>\n");
            }
        }

        /// <summary>
        /// The ODF text properties for a run, or an empty string when it has none.
        /// </summary>
        /// <remarks>
        /// <para>Doubles as the deduplication key, which is why it is built in a fixed order: two
        /// runs with the same formatting must produce byte-identical strings or they would declare
        /// two identical styles under different names.</para>
        /// <para><c>fo:font-weight</c> and friends are written only when they DEVIATE from the
        /// default. Writing <c>fo:font-weight="normal"</c> on every unbolded run would make every
        /// run styled, defeating the "no span for a plain run" rule above and quadrupling the file
        /// for nothing.</para>
        /// </remarks>
        private static string PropertiesFor(TextRun run)
        {
            var sb = new StringBuilder();

            if (run.IsBold) sb.Append("fo:font-weight=\"bold\" ");
            if (run.IsItalic) sb.Append("fo:font-style=\"italic\" ");

            // ODF splits what CSS joins: underline and line-through are separate property groups,
            // so a run that is both carries both rather than one combined value.
            if (run.IsUnderline)
                sb.Append("style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" ");
            if (run.IsDoubleStrike)
                sb.Append("style:text-line-through-style=\"solid\" style:text-line-through-type=\"double\" ");
            else if (run.IsStrikethrough)
                sb.Append("style:text-line-through-style=\"solid\" ");

            if (run.IsAllCaps) sb.Append("fo:text-transform=\"uppercase\" ");
            else if (run.IsSmallCaps) sb.Append("fo:font-variant=\"small-caps\" ");

            // Colour, size and family are compared against the MODEL'S defaults, not against
            // "is it set at all". A TextRun is constructed with FontName = Helvetica, FontSize = 12
            // and Color = the default text colour, so every run in the document carries values —
            // and emitting them would give every run a style, defeating the no-span-for-plain-text
            // rule above and inflating a plain document with a declaration per formatting.
            string? colour = OdfColor(run.Color);
            if (colour != null && !SameColor(run.Color, DocDefaults.TextColorHex))
                sb.Append("fo:color=\"").Append(colour).Append("\" ");

            // Highlight has no default — "" means none — so any parseable value is a deviation.
            string? highlight = OdfColor(run.Highlight);
            if (highlight != null) sb.Append("fo:background-color=\"").Append(highlight).Append("\" ");

            if (run.FontSize > 0 && Math.Abs(run.FontSize - DocDefaults.FontSizePt) > 0.01)
                sb.Append("fo:font-size=\"")
                  .Append(run.FontSize.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append("pt\" ");

            if (!string.IsNullOrEmpty(run.FontName)
                && !string.Equals(run.FontName, StandardFonts.Helvetica, StringComparison.Ordinal))
                sb.Append("style:font-name=\"").Append(Escape(run.FontName)).Append("\" ");

            // ODF expresses super/subscript as a position plus a size percentage, in one attribute.
            if (run.VerticalAlign == VerticalAlign.Superscript)
                sb.Append("style:text-position=\"super 58%\" ");
            else if (run.VerticalAlign == VerticalAlign.Subscript)
                sb.Append("style:text-position=\"sub 58%\" ");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// A colour ODF will accept: <c>#rrggbb</c>, or <c>null</c> for absent or unparseable.
        /// </summary>
        /// <remarks>
        /// Refusing what cannot be parsed rather than passing it through matters here: an invalid
        /// <c>fo:color</c> makes the whole automatic style invalid, and LibreOffice drops the style
        /// — so one bad colour anywhere would silently take the bold and italic off every run that
        /// shared it.
        /// </remarks>
        private static string? OdfColor(string? value)
        {
            // THE LEADING '#' IS OPTIONAL, matching the model grammar the PDF exporter accepts.
            // Mandatory, `Color = "ff0000"` — which ExportDetailTests pins as valid for the PDF path,
            // and which the PDF exporter draws red — would return null, the `colour != null` guard
            // would drop fo:color, and the run would export as default black: the same model, the
            // same string, red on one path and black on the other.
            // Trim first, then test the RESULT. IsNullOrWhiteSpace(value) says the same thing, but
            // netstandard2.0's reference assemblies carry no [NotNullWhen(false)] on it, so `value`
            // stays possibly-null for the Trim() on the next line and this assembly builds for both
            // legs. Same outcome for null and for whitespace-only input.
            if (value == null) return null;
            string v = value.Trim();
            if (v.Length == 0) return null;
            if (v[0] == '#') v = v.Substring(1);

            // 8 digits is the CSS #rrggbbaa form. ODF has no alpha channel in fo:color, so the
            // alpha is dropped — which is what the PDF exporter already does with it silently.
            if (v.Length == 8) v = v.Substring(0, 6);
            if (v.Length != 6 && v.Length != 3) return null;

            for (int i = 0; i < v.Length; i++)
                if (!Uri.IsHexDigit(v[i])) return null;

            if (v.Length == 6) return ("#" + v).ToLowerInvariant();
            // rgb → rrggbb, which ODF requires in full.
            return ("#" + v[0] + v[0] + v[1] + v[1] + v[2] + v[2]).ToLowerInvariant();
        }

        /// <summary>
        /// Whether two colour strings name the same colour, comparing the <i>normalised</i> forms
        /// so <c>#000</c>, <c>#000000</c> and <c>#000000</c> in another case all count as the
        /// default. A textual comparison would treat a run explicitly set to the default colour in
        /// a different spelling as a deviation, and give it a style saying nothing.
        /// </summary>
        private static bool SameColor(string? a, string? b)
        {
            string? na = OdfColor(a), nb = OdfColor(b);
            return na != null && nb != null && string.Equals(na, nb, StringComparison.Ordinal);
        }

        private static string Escape(string s)
            => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
