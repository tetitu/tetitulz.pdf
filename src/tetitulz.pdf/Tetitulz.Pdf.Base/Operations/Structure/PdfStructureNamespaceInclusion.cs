// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Whether a PDF tagged-structure parent type may contain a given child type, per
    /// ISO/TS 32005:2023 Table 5 ("Parent-child relationships between the PDF 1.7 elements
    /// and PDF 2.0 elements") -- the hierarchical inclusion rules TS 32005 §7.2 makes
    /// normative for both namespaces at once: "PDF 1.7 elements and PDF 2.0 elements shall
    /// not have child or parent PDF 1.7 elements or PDF 2.0 elements that are not explicitly
    /// listed in Table 5." See the enum values below for what each cell of the table means.
    /// </summary>
    public enum StructureContainmentRule
    {
        /// <summary>Not recognised as a structure type this table covers -- neither a
        /// PDF 1.7 nor PDF 2.0 standard type, nor "Content Item". A caller reaching this
        /// for a role-mapped custom type must resolve the mapping to a standard type first;
        /// TS 32005 has nothing to say about an unmapped name directly.</summary>
        Unknown,
        /// <summary>&#8709; -- shall not occur.</summary>
        Forbidden,
        /// <summary>&#8709;* -- shall not occur unless the parent element is used as a grouping
        /// level element.</summary>
        ForbiddenUnlessGroupingLevel,
        /// <summary>0..n -- may be a child element with one or several occurrences, but is
        /// not required to be present.</summary>
        Optional,
        /// <summary>1..n -- shall be present as a child element with one or several
        /// occurrences.</summary>
        Required,
        /// <summary>0..1 -- may occur, but not more than once.</summary>
        OptionalAtMostOnce,
        /// <summary>The bare numeral "1" -- not itself in TS 32005's own Table 4 legend,
        /// but unambiguous, and used in the source table for exactly one relationship
        /// (Document's StructTreeRoot parent): required, and occurring exactly once.
        /// </summary>
        RequiredExactlyOnce,
        /// <summary>&#8225; -- for containment rules, refer to the respective structure element
        /// type's own description rather than this table.</summary>
        SeeElementDescription,
        /// <summary>[a] -- governed by ISO 32000-2:2020 14.8.4.7.3's Ruby-specific
        /// provisions rather than this table's ordinary values.</summary>
        RubySpecific,
        /// <summary>[b] -- governed by ISO 32000-2:2020 14.8.4.7.3's Warichu-specific
        /// provisions rather than this table's ordinary values.</summary>
        WarichuSpecific,
        /// <summary>The bare letter "c" -- appears in the source spreadsheet at exactly one
        /// cell (Figure as a child of WP) and is undocumented even in the PDF Association's
        /// own legend; open as of the PDF Association's own tracker
        /// (github.com/pdf-association/pdf-issues, issue #440, "undocumented "c" entry in
        /// megatable"). Reported as its own value rather than folded into
        /// <see cref="Unknown"/> or <see cref="Forbidden"/> so a caller can tell "the table
        /// has an entry here the source itself has not explained" from "this table says
        /// nothing about this pair" -- guessing which of the other nine values it secretly
        /// means would be exactly the kind of fabricated certainty this mechanism exists to
        /// avoid.</summary>
        Undocumented,
    }

    /// <summary>
    /// The one implementation of ISO/TS 32005's hierarchical inclusion rules -- a pure
    /// data/logic leaf with no static fields beyond its own table, the same shape as
    /// <c>Type3Encoding.cs</c>. Lives in Base, not in either Conformance or Accessibility,
    /// because both need it and only Base is reachable from both -- the same reason
    /// <c>VerifiedWrite</c>/<c>PdfObjectIndex</c>/<c>PdfSanitizer</c> are here.
    /// </summary>
    /// <remarks>
    /// <para><b>Source and verification.</b> ISO/TS 32005:2023(E) Table 5 itself was not
    /// reliably machine-readable in this environment -- <c>pdftotext -layout</c> visibly
    /// interleaves its Children/Parents columns across every page break the table crosses.
    /// The data below comes instead from the PDF Association's own published "compact
    /// representation of the hierarchical inclusion rules" spreadsheet (TS 32005's own
    /// bibliography names it as the authoritative machine-readable source), specifically its
    /// "Errata" worksheet -- the PDF/UA TWG-approved corrected version, which differs from the
    /// as-published worksheet in exactly one cell (Sub's occurrence as a child of Code,
    /// corrected from &#8709; to 0..n) -- confirmed by diffing the two worksheets' extracted values
    /// directly, not by trusting the "changes marked in red" note alone. Cross-verified
    /// against TS 32005's own prose (§7.2, read via <c>pdftotext</c> without <c>-layout</c>,
    /// which does not suffer the column-interleaving defect): the megatable's row/column
    /// direction (row = candidate CHILD, column = candidate PARENT) was confirmed by matching
    /// the Document/StructTreeRoot cell ("1") against the prose table's independent statement
    /// that Document's parent StructTreeRoot occurs exactly once.</para>
    /// <para><b>"Content Item" is a 53rd child-only row</b> -- not a structure type, but the
    /// marked-content leaf a structure element's <c>/K</c> array can name directly instead of
    /// a further structure element. It can never be a parent (nothing can be its own child),
    /// which is exactly why it is absent from the column list.</para>
    /// </remarks>
    public static class PdfStructureNamespaceInclusion
    {
        /// <summary>Every parent-candidate type this table covers, in the source table's own
        /// column order. "StructTreeRoot" first, since it can never be a child of anything.
        /// </summary>
        public static IReadOnlyList<string> ParentTypes { get; } = new List<string>
        {
            "StructTreeRoot",
            "Document",
            "DocumentFragment",
            "Part",
            "Div",
            "Art",
            "Sect",
            "TOC",
            "TOCI",
            "Aside",
            "BlockQuote",
            "NonStruct",
            "Private",
            "Title",
            "Sub",
            "P",
            "Note",
            "Code",
            "Hn",
            "H",
            "Lbl",
            "Em",
            "Strong",
            "Span",
            "Quote",
            "Link",
            "Reference",
            "Annot",
            "Form",
            "Ruby",
            "RB",
            "RT",
            "RP",
            "Warichu",
            "WT",
            "WP",
            "FENote",
            "Index",
            "L",
            "LI",
            "LBody",
            "BibEntry",
            "Table",
            "TR",
            "TH",
            "TD",
            "THead",
            "TBody",
            "TFoot",
            "Caption",
            "Figure",
            "Formula",
            "Artifact",
        };

        /// <summary>Every child-candidate type this table covers, in the source table's own
        /// row order -- the same 52 structure types as <see cref="ParentTypes"/> minus
        /// "StructTreeRoot", plus "Content Item" appended.</summary>
        public static IReadOnlyList<string> ChildTypes { get; } = new List<string>
        {
            "Document",
            "DocumentFragment",
            "Part",
            "Art",
            "Div",
            "Sect",
            "TOC",
            "TOCI",
            "Aside",
            "BlockQuote",
            "NonStruct",
            "Private",
            "P",
            "Note",
            "Code",
            "Hn",
            "H",
            "Title",
            "Sub",
            "Lbl",
            "Em",
            "Strong",
            "Span",
            "Quote",
            "Link",
            "Reference",
            "Annot",
            "Form",
            "Ruby",
            "RB",
            "RT",
            "RP",
            "Warichu",
            "WT",
            "WP",
            "FENote",
            "Index",
            "L",
            "LI",
            "LBody",
            "BibEntry",
            "Table",
            "TR",
            "TH",
            "TD",
            "THead",
            "TBody",
            "TFoot",
            "Caption",
            "Figure",
            "Formula",
            "Artifact",
            "Content Item",
        };

        private static readonly Dictionary<string, int> ParentIndex = BuildIndex(ParentTypes);
        private static readonly Dictionary<string, int> ChildIndex = BuildIndex(ChildTypes);

        private static Dictionary<string, int> BuildIndex(IReadOnlyList<string> types)
        {
            var map = new Dictionary<string, int>(types.Count, StringComparer.Ordinal);
            for (int i = 0; i < types.Count; i++) map[types[i]] = i;
            return map;
        }

        /// <summary>
        /// One row per <see cref="ChildTypes"/> entry, in the same order; each row has
        /// exactly <see cref="ParentTypes"/>.Count characters, one per parent-candidate
        /// column, coded '0'=Forbidden '1'=ForbiddenUnlessGroupingLevel '2'=Optional
        /// '3'=Required '4'=OptionalAtMostOnce '5'=RequiredExactlyOnce
        /// '6'=SeeElementDescription '7'=RubySpecific '8'=WarichuSpecific
        /// '9'=Undocumented. Kept as single-character codes rather than an enum array so a
        /// row here can be diffed directly, character for character, against the source
        /// spreadsheet row it was generated from.
        /// </summary>
        private static readonly string[] Rows =
        {
            // Document
            "52266000022620000000000000000000000000000000000000002",
            // DocumentFragment
            "02266220022620001100000001010000000010000000000001002",
            // Part
            "02266222022622002200000001011000000022002200000002222",
            // Art
            "02266020022620002044000002020000000020002000220002202",
            // Div
            "02266220222622002200000002022000000022022200220002222",
            // Sect
            "02266220022620002044000002020000000022002000220002202",
            // TOC
            "02266222222620000000000000000000000000000000000000002",
            // TOCI
            "00066002000620000000000000000000000000000000000000002",
            // Aside
            "02266220000622002000000001010000000020002000000002222",
            // BlockQuote
            "02266220022620002000000001010000000020002000000002222",
            // NonStruct
            "02266222222622222222222222222222222222222222222222222",
            // Private
            "02266222222622222222222222202222222222222222222222222",
            // P
            "02266220222622002000000001010000000022002200220002222",
            // Note
            "02266220022622222222222222222000000022002200220002222",
            // Code
            "02266220022622222022222222021000000020002000220002222",
            // Hn
            "02266220022620000000000001010000000002002000220002222",
            // H
            "04466440044620000000000001010000000002004000440004444",
            // Title
            "02266220000620000000000001010000000000000000000000002",
            // Sub
            "00066000000620022222222222020022202220002000000002122",
            // Lbl
            "00066220222622222022022222222000000020020200220002222",
            // Em
            "00006000000622222222222222220022202220002200220002222",
            // Strong
            "00006000000622222222222222220022202220002200220002222",
            // Span
            "00006000000622222222222222220022202220002200220002222",
            // Quote
            "00006000000622222022222222020022202220002000220002222",
            // Link
            "02266220022622222222222220220022202220002200220002222",
            // Reference
            "00066000222622222222222222011022202222002200220002222",
            // Annot
            "02266220022622222222222222220022202222002200220002222",
            // Form
            "02266220022622222022222221010022202220002000220002222",
            // Ruby
            "00006000000622222022222222020000000020002000220002222",
            // RB
            "00006000000620000000000000000700000000000000000000002",
            // RT
            "00006000000620000000000000000700000000000000000000002",
            // RP
            "00006000000620000000000000000700000000000000000000002",
            // Warichu
            "00006000000622222022222222020000000020002000220002222",
            // WT
            "00006000000620000000000000000000080000000000000000002",
            // WP
            "00006000000620000000000000000000080000000000000000002",
            // FENote
            "02266220022622222222222222222000000022002200220002222",
            // Index
            "02266220022620002000000000000000000000002000220002222",
            // L
            "02266220022622222000000001011000000022202000220002222",
            // LI
            "00006000000620000000000000000000000000200000000000002",
            // LBody
            "00006000000620000000000000000000000000020000000000002",
            // BibEntry
            "00066000000622222222222222222000000000002000220002222",
            // Table
            "02266220022622022000000001011000000022002000220002222",
            // TR
            "00006000000620000000000000000000000000000020002220002",
            // TH
            "00006000000620000000000000000000000000000002000000002",
            // TD
            "00006000000620000000000000000000000000000002000000002",
            // THead
            "00006000000620000000000000000000000000000040000000002",
            // TBody
            "00006000000620000000000000000000000000000020000000002",
            // TFoot
            "00006000000620000000000000000000000000000040000000002",
            // Caption
            "00066224044624001000000001014000000012404040000000444",
            // Figure
            "02266220022622222022222222221000000922002200220002222",
            // Formula
            "02266220022622222022222222021000000022002000220002222",
            // Artifact
            "02266222222622222222222222222022202222222222222222222",
            // Content Item
            "00000000022622222222222222222266626620022200220002222",
        };

        /// <summary>
        /// Whether <paramref name="parentType"/> may contain <paramref name="childType"/> as
        /// a direct structure-tree child, per ISO/TS 32005 Table 5. Both names are matched
        /// literally against <see cref="ParentTypes"/>/<see cref="ChildTypes"/> -- a caller
        /// holding a role-mapped custom type must resolve it to a standard type first
        /// (<see cref="StructureContainmentRule.Unknown"/> is the answer for a name this
        /// table does not recognise, never a guess).
        /// </summary>
        public static StructureContainmentRule CanContain(string parentType, string childType)
        {
            if (parentType == null || childType == null) return StructureContainmentRule.Unknown;
            if (!ParentIndex.TryGetValue(parentType, out int p)) return StructureContainmentRule.Unknown;
            if (!ChildIndex.TryGetValue(childType, out int c)) return StructureContainmentRule.Unknown;

            return Rows[c][p] switch
            {
                '0' => StructureContainmentRule.Forbidden,
                '1' => StructureContainmentRule.ForbiddenUnlessGroupingLevel,
                '2' => StructureContainmentRule.Optional,
                '3' => StructureContainmentRule.Required,
                '4' => StructureContainmentRule.OptionalAtMostOnce,
                '5' => StructureContainmentRule.RequiredExactlyOnce,
                '6' => StructureContainmentRule.SeeElementDescription,
                '7' => StructureContainmentRule.RubySpecific,
                '8' => StructureContainmentRule.WarichuSpecific,
                '9' => StructureContainmentRule.Undocumented,
                _ => StructureContainmentRule.Unknown,
            };
        }

        /// <summary>
        /// True only for the values that mean "yes, unconditionally" --
        /// <see cref="StructureContainmentRule.Optional"/>,
        /// <see cref="StructureContainmentRule.Required"/>,
        /// <see cref="StructureContainmentRule.OptionalAtMostOnce"/> and
        /// <see cref="StructureContainmentRule.RequiredExactlyOnce"/>. Deliberately excludes
        /// <see cref="StructureContainmentRule.ForbiddenUnlessGroupingLevel"/> (conditional),
        /// <see cref="StructureContainmentRule.SeeElementDescription"/>,
        /// <see cref="StructureContainmentRule.RubySpecific"/> and
        /// <see cref="StructureContainmentRule.WarichuSpecific"/> (all three need more context
        /// than a type-name pair carries), and
        /// <see cref="StructureContainmentRule.Undocumented"/>/<see cref="StructureContainmentRule.Unknown"/>
        /// (not knowing is not the same as knowing it is fine) -- a caller that only wants
        /// "is this definitely OK" must not get <c>true</c> for any of those six.
        /// </summary>
        public static bool IsDefinitelyAllowed(StructureContainmentRule rule)
            => rule == StructureContainmentRule.Optional
            || rule == StructureContainmentRule.Required
            || rule == StructureContainmentRule.OptionalAtMostOnce
            || rule == StructureContainmentRule.RequiredExactlyOnce;
    }
}
