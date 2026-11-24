// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA12 — Table structure and regularity
        // ================================================================

        private static void CheckTableStructure(CheckContext ctx)
        {
            var noHeaders   = new List<Occurrence>();
            var badCell     = new List<Occurrence>();
            var unaddressed = new List<Occurrence>();
            var unaddressedAdvice = new List<Occurrence>();
            var ragged      = new List<Occurrence>();
            var abandoned   = new List<Occurrence>();

            // As in UA11, the role tested is the /RoleMap-resolved one: a producer's /HeaderCell
            // mapped to /TH is a TH, and comparing the raw /S reported a conformant table as
            // having illegal row children *and* no header cells at all.
            foreach (StructNode table in ctx.Elements)
            {
                if (RoleOf(ctx, table) != StructureTypes.Table) continue;

                var rows = new List<StructNode>();
                CollectRows(ctx, table, rows, 0);
                if (rows.Count == 0) continue;

                // /Headers values across the whole table: a TH with no /Scope is still
                // addressable if data cells point at it by /ID.
                var referencedHeaderIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (StructNode row in rows)
                    foreach (StructNode cell in ctx.ChildrenOf(row))
                        CollectHeaderIds(ctx, cell, referencedHeaderIds);

                // ISO 14289-1 7.5: "if the table's structure is not determinable via Headers and IDs",
                // TH elements shall have a /Scope. When every TD names its headers, it is determinable,
                // and a TH without /Scope is advice, not a fault (test 7.5-t01-pass-c to -e).
                bool headersDetermineTable = true;
                foreach (StructNode row in rows)
                {
                    foreach (StructNode cell in ctx.ChildrenOf(row))
                    {
                        if (RoleOf(ctx, cell) != StructureTypes.TableDataCell) continue;
                        var named = new HashSet<string>(StringComparer.Ordinal);
                        CollectHeaderIds(ctx, cell, named);
                        if (named.Count == 0) { headersDetermineTable = false; break; }
                    }
                    if (!headersDetermineTable) break;
                }

                bool hasAnyTh = false;
                var rowWidths = new List<int>(rows.Count);
                var carry = new Dictionary<int, int>();

                // MaxTableRows bounds the rows; nothing bounded the COLUMN space, and the grid walk
                // below is quadratic in it. `column` accumulates /ColSpan per cell without limit, so
                // one row of 2 000 cells each declaring /ColSpan 1000 /RowSpan 1000 inserts two
                // million carry entries — and every later row then walks the skip loop across all of
                // them, copies their keys into a fresh list and sweeps them again. That is ~6×10^9
                // dictionary operations from a ~500 KB file, and RunCheck catches exceptions, not
                // time, so the audit hangs rather than fails. A WalkBudget (rather than a
                // Core.Util.WorkBudget) because the right answer here is a partial verdict that
                // SAYS it is partial, not an aborted audit.
                var gridBudget = new WalkBudget(MaxTableGridWork);
                bool tableAbandoned = false;

                for (int r = 0; r < rows.Count && r < MaxTableRows && !tableAbandoned; r++)
                {
                    IReadOnlyList<StructNode> cells = ctx.ChildrenOf(rows[r]);
                    int column = 0;

                    foreach (StructNode cell in cells)
                    {
                        if (!gridBudget.Take()) { tableAbandoned = true; break; }

                        string role = RoleOf(ctx, cell);
                        if (role != StructureTypes.TableDataCell && role != StructureTypes.TableHeaderCell)
                        {
                            var occurrence = Occurrence.For(ctx, cell);
                            occurrence.Detail = DescribeRole(ctx, cell);
                            badCell.Add(occurrence);
                            continue;
                        }

                        if (role == StructureTypes.TableHeaderCell)
                        {
                            hasAnyTh = true;
                            if (!IsHeaderAddressable(ctx, cell, referencedHeaderIds))
                                (headersDetermineTable ? unaddressedAdvice : unaddressed).Add(Occurrence.For(ctx, cell));
                        }

                        int colSpan = GetSpan(ctx, cell, NameColSpan);
                        int rowSpan = GetSpan(ctx, cell, NameRowSpan);

                        // Skip columns still occupied by a /RowSpan from an earlier row.
                        while (carry.TryGetValue(column, out int remaining) && remaining > 0)
                        {
                            if (!gridBudget.Take()) { tableAbandoned = true; break; }
                            column++;
                        }
                        if (tableAbandoned) break;

                        if (rowSpan > 1)
                            for (int c = 0; c < colSpan; c++)
                            {
                                if (!gridBudget.Take()) { tableAbandoned = true; break; }
                                carry[column + c] = rowSpan;
                            }
                        if (tableAbandoned) break;

                        column += colSpan;
                    }

                    if (tableAbandoned) break;

                    // A row can also end with columns held by row-spans from above.
                    while (carry.TryGetValue(column, out int trailing) && trailing > 0)
                    {
                        if (!gridBudget.Take()) { tableAbandoned = true; break; }
                        column++;
                    }
                    if (tableAbandoned) break;

                    rowWidths.Add(column);

                    // One row consumed: every outstanding span is one row closer to expiry.
                    if (carry.Count > 0)
                    {
                        if (!gridBudget.Take(carry.Count)) { tableAbandoned = true; break; }

                        var expired = new List<int>();
                        var keys = new List<int>(carry.Keys);
                        foreach (int key in keys)
                        {
                            int left = carry[key] - 1;
                            if (left <= 0) expired.Add(key);
                            else carry[key] = left;
                        }
                        foreach (int key in expired) carry.Remove(key);
                    }
                }

                if (tableAbandoned)
                {
                    // The grid is only partly built, so `rowWidths` is a prefix and comparing its
                    // entries would accuse a table of raggedness this walk manufactured. The
                    // header verdicts below rest on cells that WERE read, so they still stand;
                    // regularity does not, and is reported as not run rather than guessed.
                    var refusal = Occurrence.For(ctx, table);
                    refusal.Detail = MaxTableGridWork.ToString(CultureInfo.InvariantCulture);
                    abandoned.Add(refusal);

                    AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CheckTableStructure",
                        "a table's /ColSpan//RowSpan grid exceeded the " +
                        MaxTableGridWork.ToString(CultureInfo.InvariantCulture) +
                        "-step per-table budget; its regularity was not checked");

                    if (!hasAnyTh) noHeaders.Add(Occurrence.For(ctx, table));
                    continue;
                }

                if (!hasAnyTh) noHeaders.Add(Occurrence.For(ctx, table));

                int expected = rowWidths.Count > 0 ? rowWidths[0] : 0;
                int firstBadRow = -1;
                for (int r = 1; r < rowWidths.Count; r++)
                {
                    if (rowWidths[r] == expected) continue;
                    firstBadRow = r;
                    break;
                }

                if (firstBadRow >= 0)
                {
                    var occurrence = Occurrence.For(ctx, table);
                    occurrence.Detail = "row 1 spans " + expected.ToString(CultureInfo.InvariantCulture) +
                                        " column(s) but row " + (firstBadRow + 1).ToString(CultureInfo.InvariantCulture) +
                                        " spans " + rowWidths[firstBadRow].ToString(CultureInfo.InvariantCulture);
                    ragged.Add(occurrence);
                }
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA12", IssueCategory.Tables, WcagLevel.A,
                "15-003", "1.3.1", ragged,
                o => "Table is not regular: " + o.Detail +
                     " (after /ColSpan and /RowSpan are applied). A ragged table cannot be navigated " +
                     "cell-by-cell by a screen reader.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " table(s) are not regular — their rows span " +
                    "differing numbers of columns once /ColSpan and /RowSpan are applied (e.g. " + objects + ").",
                "Give every row the same total column count. Where cells merge, declare /ColSpan and " +
                "/RowSpan in the cell's /A << /O /Table … >> attribute dictionary.");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA12", IssueCategory.Tables, WcagLevel.A,
                null, "1.3.1", badCell,
                o => "Table row (TR) contains a child of type " + o.Detail +
                     "; PDF/UA-1 requires TR to contain only TD and TH.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " child element(s) of table rows are neither " +
                    "TD nor TH (e.g. " + objects + "). PDF/UA-1 requires TR to contain only TD and TH.",
                "Fix the structure tree so TR only contains TD and TH children.");

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA12", IssueCategory.Tables, WcagLevel.A,
                null, "1.3.1", noHeaders,
                o => "Table has no TH (header) cells; screen readers cannot announce which row or column " +
                     "a data cell belongs to.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " table(s) have no TH (header) cells " +
                    "(e.g. " + objects + ").",
                "Add TH elements for column and row headers and set /Scope (Column, Row or Both) on each.");

            // ISO 14289-1 7.5: where a table's structure is not determinable through /Headers and
            // /ID, its TH elements shall have a /Scope — a TH with neither is an error (rules
            // 7.5-1 and -2).
            EmitOccurrences(ctx, IssueSeverity.Error, "UA12", IssueCategory.Tables, WcagLevel.A,
                null, "1.3.1", unaddressed,
                o => "Header cell (TH) has neither a /Scope attribute nor an /ID that any data cell " +
                     "references through /Headers, so no data cell is associated with it (ISO 14289-1 7.5).",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " header cell(s) have neither /Scope nor an " +
                    "/ID referenced by a data cell's /Headers (e.g. " + objects + ").",
                "Add /A << /O /Table /Scope /Column >> (or /Row, or /Both) to each TH, or give the TH an " +
                "/ID and list it in each associated cell's /A << /O /Table /Headers [ … ] >>.");

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA12", IssueCategory.Tables, WcagLevel.A,
                null, "1.3.1", unaddressedAdvice,
                o => "Header cell (TH) has neither a /Scope attribute nor an /ID that any data cell " +
                     "references; every data cell names its headers, so the table is still determinable.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " header cell(s) have neither /Scope nor a " +
                    "referenced /ID, in tables whose data cells all name their headers (e.g. " + objects + ").",
                "Add a /Scope to each such TH, or reference it from the cells it heads.");

            // A check that neither ran nor said so is indistinguishable from one that ran and found
            // nothing, so the abandoned tables are named — Info, and UA99 rather than UA12, because
            // the statement is "this was not checked", not "the document is wrong".
            EmitOccurrences(ctx, IssueSeverity.Info, "UA99", IssueCategory.Tables, WcagLevel.None,
                null, null, abandoned,
                o => "This table's /ColSpan//RowSpan grid needed more than " + o.Detail +
                     " steps to lay out, so its column regularity was not checked. Any UA12 " +
                     "raggedness it contains is missing from this report.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " table(s) declare a /ColSpan//RowSpan " +
                    "grid too large to lay out within the per-table budget (e.g. " + objects + "), so their " +
                    "column regularity was not checked.",
                "Reduce the declared /ColSpan and /RowSpan values to the table's real column count; a " +
                "span far larger than the table is a producer defect in its own right.");
        }

        private static void CollectRows(CheckContext ctx, StructNode node, List<StructNode> rows, int depth)
        {
            if (depth > 8) return;   // Table > THead/TBody/TFoot > TR is 2 levels; 8 is generous.

            foreach (StructNode child in ctx.ChildrenOf(node))
            {
                string role = RoleOf(ctx, child);
                if (role == StructureTypes.TableRow) rows.Add(child);
                else if (role == StructureTypes.TableHead ||
                         role == StructureTypes.TableBody ||
                         role == StructureTypes.TableFoot)
                    CollectRows(ctx, child, rows, depth + 1);
            }
        }

        private static void CollectHeaderIds(CheckContext ctx, StructNode cell, HashSet<string> into)
        {
            PdfObject headers = GetTableAttribute(ctx, cell.Dict, PdfName.Headers);
            if (headers is not PdfArray array) return;

            for (int i = 0; i < array.Count; i++)
            {
                PdfObject item = array[i];
                if (item is PdfReference) item = ResolveObject(ctx.Doc, item);
                if (item is PdfString s)
                {
                    string text = s.GetText();
                    if (!string.IsNullOrEmpty(text)) into.Add(text);
                }
                else if (item is PdfName n)
                {
                    into.Add(n.Value);
                }
            }
        }

        private static bool IsHeaderAddressable(CheckContext ctx, StructNode cell, HashSet<string> referencedHeaderIds)
        {
            if (GetTableAttribute(ctx, cell.Dict, PdfName.Scope) != null) return true;

            // /ID is a key on the structure element itself (PDF 32000-1 Table 323), not a
            // table attribute.
            PdfObject id = cell.Dict?[PdfName.ID];
            if (id is PdfReference) id = ResolveObject(ctx.Doc, id);

            string idText = (id as PdfString)?.GetText() ?? (id as PdfName)?.Value;
            if (string.IsNullOrEmpty(idText)) return false;
            if (referencedHeaderIds.Contains(idText)) return true;

            // Strings are encrypted with a key derived from the object they live in
            // (ISO 32000-1 §7.6.2), so one logical /ID stored in a TH and quoted in a data cell's
            // /Headers decrypts from two different byte sequences — the comparison above can never
            // match on an encrypted document, whatever the document says. Reporting every such TH
            // would be reporting the encryption rather than the file, so a TH that carries an /ID
            // is accepted here and the weakening is stated in the report. A /Name /ID is not
            // encrypted and is still matched normally.
            if (ctx.Encrypted && id is PdfString)
            {
                NoteEncryptionLimit(ctx,
                    "UA12's header-addressability rule was weakened: a data cell associates itself " +
                    "with a TH by quoting the TH's /ID string in /Headers, and encrypted strings " +
                    "from two different objects never compare equal, so any TH carrying an /ID was " +
                    "accepted as addressable without verifying that a cell references it");
                return true;
            }

            return false;
        }

        /// <summary>
        /// The cell's span, read and clamped by <see cref="TableAttributes.ReadSpan"/> — the one
        /// reading PDF/UA-2's §8.2.5.26 shares, so the two regularity models cannot disagree about
        /// what a cell's span is.
        /// </summary>
        private static int GetSpan(CheckContext ctx, StructNode cell, PdfName key)
            => TableAttributes.ReadSpan(cell.Dict, key, ctx.ClassMap, o => ResolveObject(ctx.Doc, o));

        /// <summary>
        /// Reads a table attribute off a structure element, from <b>both</b> places ISO 32000-1
        /// §14.7.5 lets a producer put one — <c>/A</c> inline and <c>/C</c> through the
        /// <c>/ClassMap</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The engine is <see cref="PdfStructureAttributes"/>, in Core</b>, so
        /// <c>Base.Operations.Structure.PdfStructureTreeReader</c> — the public way to read a tagged
        /// document — reads attributes through it too. <c>Base → Accessibility</c> would invert the
        /// ring model, so the code sits below both, and this is a one-line adapter that supplies the
        /// owner and the resolver.</para>
        ///
        /// <para><b>Why the class form cannot be skipped.</b> Two UA12 rules rest entirely on this —
        /// <see cref="GetSpan"/>, which feeds the grid-regularity computation, and
        /// <see cref="IsHeaderAddressable"/>, which reads <c>/Scope</c> and <c>/Headers</c>. With
        /// <c>/C</c> unread, a checker-clean table that declares its merges and its header scopes
        /// through a ClassMap would have every span read as 1 and every <c>/Scope</c> read as absent,
        /// and be reported as ragged and as having unaddressable headers.</para>
        ///
        /// <para><b><c>/O</c> is resolved.</b> Read raw, an indirect owner would read as absent and
        /// fall into the lenient "no owner declared" branch — a <c>/Layout</c> attribute silently
        /// accepted as a <c>/Table</c> one.</para>
        /// </remarks>
        private static PdfObject GetTableAttribute(CheckContext ctx, PdfDictionary element, PdfName key)
            => TableAttributes.Lookup(element, key, ctx.ClassMap, o => ResolveObject(ctx.Doc, o));
    }
}
