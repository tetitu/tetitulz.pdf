// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// An invoice's item table, read from the tables <see cref="PdfTableExtractor"/> finds: the
    /// header row names the columns, and the rows under it are the items.
    /// </summary>
    /// <remarks>
    /// <para><b>The header is found by what its cells say</b>, in Hebrew or English: a row with a
    /// price or amount column and a description or quantity column is a header, wherever it sits in
    /// a detected table — the table detector often takes the address block above it into the same
    /// table. A header cell's role is the first that matches and is not yet taken, in the order
    /// discount, quantity, catalogue number, tax, amount, unit price, description, so <c>מס' פריט</c>
    /// (item number) is a catalogue number before it is a description and <c>Total price</c> an
    /// amount before a price. A qualifier is no role: <c>סה"כ לאחר הנחה</c> (total after discount)
    /// is an amount, not a discount, and <c>מחיר כולל מע"מ</c> (price including VAT) a price, not
    /// tax; a tax column (<c>VAT Rate</c>) is recognised so that it is nobody's price, and a second
    /// one (<c>סה"כ מע"מ</c> beside <c>אחוז מע"מ</c>) is nobody's amount. A total outranks a sum
    /// (<c>סה"כ</c> over <c>סכום בסיסי</c>); an amount qualified as holding its VAT is marked so
    /// (<see cref="InvoiceLineItem.AmountIncludesVat"/>). Columns liable to VAT and free of it
    /// (<c>חייב מע"מ</c>, <c>לא חייב מע"מ</c>) are the parts of a line's amount where no amount
    /// column stands. A header set on two lines takes its upper line's headings for the columns its
    /// lower line leaves empty when the upper line is no header of its own; a header cell holding a
    /// figure names nothing.</para>
    /// <para><b>The items end at the totals</b>: a row with no quantity whose label — the first
    /// words of a cell holding letters — names a total, the VAT or the amount to pay. A description
    /// that merely contains such a word ("Renovation", <c>הכנת דוח מע"מ</c>) is an item. A row
    /// carrying a page's subtotal to the next (<c>סה"כ להעברה</c>, "brought forward") is neither an
    /// item nor the end. A row with a description and no numbers continues the item above it — a
    /// description wrapped onto a second row; rows with no numbers straight under the header are
    /// the header's own second line.</para>
    /// <para><b>A table carries over to the next page</b> when that page has a table with no
    /// header of its own and the totals have not been reached; its columns are given the header's
    /// roles by where they sit, since a page with an empty column detects one column fewer. Once
    /// a table with items has reached its totals, reading stops: an invoice printed as original and
    /// copy holds the same table twice.</para>
    /// </remarks>
    internal static class InvoiceLineItems
    {
        private enum Role { Discount, Quantity, CatalogNumber, Tax, Amount, UnitPrice, Description, Date }

        private static readonly (Role Role, string[] Keys)[] RoleKeys =
        {
            (Role.Discount, new[] { "הנחה", "discount" }),
            (Role.Quantity, new[] { "כמות", "כמ'", "יחידות", "שעות", "qty", "quantity", "units", "hours" }),
            (Role.CatalogNumber, new[] { "מק\"ט", "מקט", "קוד", "מס' פריט", "מספר פריט", "ברקוד", "sku", "code", "item no", "item #", "part no", "catalog" }),
            (Role.Tax, new[] { "מע\"מ", "vat", "tax" }),
            (Role.Amount, new[] { "סה\"כ", "סכום", "total", "amount", "sum" }),
            (Role.UnitPrice, new[] { "מחיר", "ליחידה", "תעריף", "price", "rate" }),
            (Role.Date, new[] { "תאריך", "date" }),
            (Role.Description, new[] { "תיאור", "תאור", "פירוט", "פרטים", "פריט", "מוצר", "שירות", "description", "item", "details", "product", "service" }),
        };

        // Qualifiers taken out of a header cell before its role is read: what a column is after or
        // before is not what it is.
        private static readonly string[] Qualifiers =
        {
            "לאחר הנחה", "אחרי הנחה", "after discount", "net of discount",
            "כולל מע\"מ", "לפני מע\"מ", "ללא מע\"מ", "אחרי מע\"מ",
            "incl. VAT", "including VAT", "excl. VAT", "excluding VAT", "before VAT", "after VAT",
        };

        // A header's amount qualified as holding the VAT: its items add up to the total.
        private static readonly string[] WithVatQualifiers =
            { "כולל מע\"מ", "אחרי מע\"מ", "incl. VAT", "including VAT", "after VAT" };

        // The words of a total column: a total outranks a sum ("סכום בסיסי", a base amount).
        private static readonly string[] TotalKeys = { "סה\"כ", "total" };

        // A column holding the part of a line's amount liable to VAT, or the part free of it — a
        // parking operator's "חייב מע"מ" beside "לא חייב מע"מ": the line's amount is its parts'.
        private static readonly string[] VatPartKeys =
            { "חייב מע\"מ", "חייב במע\"מ", "חייבים במע\"מ", "פטור ממע\"מ", "פטור מע\"מ", "taxable", "exempt" };

        // The labels that open a totals row, each as its words.
        private static readonly string[] TotalsLabels =
            { "סה\"כ", "סכום", "סכומים", "מע\"מ", "לתשלום", "subtotal", "total", "vat", "amount due" };

        // Labels of a row carrying a page's subtotal over, anywhere in a cell's words.
        private static readonly string[] CarryLabels =
        {
            "להעברה", "מהעברה", "מעמוד קודם", "לעמוד הבא", "carried forward", "brought forward",
            "carry forward", "balance forward", "c/f", "b/f",
        };

        private static readonly List<(Role Role, string Key)> FoldedRoleKeys =
            RoleKeys.SelectMany(r => r.Keys.Select(k => (r.Role, InvoiceValueParser.LabelKey(k)))).ToList();

        private static readonly List<string> FoldedQualifiers =
            Qualifiers.Select(InvoiceValueParser.LabelKey).OrderByDescending(q => q.Length).ToList();

        private static readonly List<string> FoldedWithVat = WithVatQualifiers.Select(InvoiceValueParser.LabelKey).ToList();
        private static readonly List<string> FoldedTotalKeys = TotalKeys.Select(InvoiceValueParser.LabelKey).ToList();
        private static readonly List<string> FoldedVatParts = VatPartKeys.Select(InvoiceValueParser.LabelKey).ToList();

        private static readonly List<string[]> TotalsWords = TotalsLabels.Select(Words).ToList();
        private static readonly List<string[]> CarryWords = CarryLabels.Select(Words).ToList();

        /// <summary>
        /// The columns a role's header cell covers, and where it stands across the page — a header
        /// over two columns ("מחיר יח'" overhanging the unit's) answers for both.
        /// </summary>
        private readonly struct ColumnSpan
        {
            internal readonly int First, Last;
            internal readonly float Left, Right;
            /// <summary>The header cell's place among its row's filled cells, of <see cref="HeaderCells"/>; -1 when unknown.</summary>
            internal readonly int Ordinal, HeaderCells;
            /// <summary>
            /// The role's place when the header cell naming two roles is read as two, of
            /// <see cref="SplitCells"/>; -1 when no cell of the header names two.
            /// </summary>
            internal readonly int SplitOrdinal, SplitCells;

            internal ColumnSpan(int first, int last, float left, float right, int ordinal = -1, int headerCells = 0,
                                int splitOrdinal = -1, int splitCells = 0)
            {
                First = first;
                Last = last;
                Left = left;
                Right = right;
                Ordinal = ordinal;
                HeaderCells = headerCells;
                SplitOrdinal = splitOrdinal;
                SplitCells = splitCells;
            }

            internal static ColumnSpan Of(PdfTableCell cell, int ordinal, int headerCells)
                => new ColumnSpan(cell.Column, cell.Column + cell.ColSpan - 1, cell.X, cell.X + cell.Width, ordinal, headerCells);

            internal ColumnSpan WithSplit(int splitOrdinal, int splitCells)
                => new ColumnSpan(First, Last, Left, Right, Ordinal, HeaderCells, splitOrdinal, splitCells);

            internal bool Meets(PdfTableCell cell) => cell.Column <= Last && cell.Column + cell.ColSpan - 1 >= First;
        }

        /// <summary>
        /// A header's columns: each role's, the parts of the amount liable to VAT and free of it
        /// (<see cref="VatPartKeys"/>), and whether the amount holds the VAT.
        /// </summary>
        private sealed class Columns : Dictionary<Role, ColumnSpan>
        {
            internal readonly List<ColumnSpan> VatParts = new List<ColumnSpan>();
            internal bool AmountIncludesVat;
        }

        /// <summary>
        /// The item table of <paramref name="pages"/>, in order; empty when none is found. A page's
        /// rulings are read first; where they give no item — a box ruled round the document's fields
        /// and the item header alike makes them one row — its text lines are read as rows.
        /// </summary>
        internal static List<InvoiceLineItem> Read(IReadOnlyList<PdfPageTextLayout> pages,
                                                   IReadOnlyList<PdfPageGraphics> graphics)
        {
            var items = ReadTables(Tables(pages, graphics, TableStrategy.Auto));
            if (items.Count == 0 && graphics != null && graphics.Count > 0)
                items = ReadTables(Tables(pages, graphics, TableStrategy.Text));
            return items;
        }

        private static IReadOnlyList<PdfTable> Tables(IReadOnlyList<PdfPageTextLayout> pages,
                                                      IReadOnlyList<PdfPageGraphics> graphics, TableStrategy strategy)
            => PdfTableExtractor.ExtractFromLayouts(pages, graphics, new TableExtractionOptions
            {
                Strategy = strategy,
                ApplyBidi = true,
                RightToLeftColumns = true,
            });

        private static List<InvoiceLineItem> ReadTables(IReadOnlyList<PdfTable> tables)
        {
            var items = new List<InvoiceLineItem>();
            Columns columns = null;
            List<(Role? Role, float From, float To)> regions = null;
            int lastPage = -1;
            bool ended = false;

            foreach (var table in tables)
            {
                // An original and its copy: the second table is the first again.
                if (ended && items.Count > 0) break;

                int start = -1;
                for (int r = 0; r < table.Rows.Count; r++)
                {
                    var cells = HeaderCells(table.Rows, r);
                    var roles = HeaderRoles(cells);
                    if (roles == null) continue;
                    columns = roles;
                    regions = Regions(cells, roles);
                    ended = false;
                    start = r + 1;
                    // The header's own second line: no numbers before the first item.
                    while (start < table.Rows.Count && !HasDigit(table.Rows[start])) start++;
                    break;
                }

                var tableColumns = columns;
                if (start < 0)
                {
                    // No header: a continuation of the table on the previous page, or not items.
                    bool continues = columns != null && !ended && table.PageIndex == lastPage + 1;
                    if (!continues) continue;
                    tableColumns = ColumnsByPosition(table, regions, columns);
                    start = 0;
                }

                for (int r = start; r < table.Rows.Count && !ended; r++)
                {
                    // A second header — a garage's labour under its parts — names its own columns.
                    var againCells = HeaderCells(table.Rows, r);
                    var again = HeaderRoles(againCells);
                    if (again != null)
                    {
                        tableColumns = columns = again;
                        regions = Regions(againCells, again);
                        continue;
                    }
                    ended = ReadRow(table.Rows[r], table.PageIndex, tableColumns, items);
                }
                lastPage = table.PageIndex;
            }

            for (int i = 0; i < items.Count; i++) items[i].Line = i + 1;
            return items;
        }

        /// <summary>
        /// A header row's filled cells, in column order, with its line above — a header set on two
        /// lines, "סה"כ" over the total's column and the other headings on the line under it — when
        /// that line holds no figure and is no header of its own (the header over the first item is
        /// no upper line of that item): the line above heads the columns this one leaves empty.
        /// </summary>
        private static List<PdfTableCell> HeaderCells(IReadOnlyList<PdfTableRow> rows, int r)
        {
            var cells = Filled(rows[r]);
            if (r == 0 || HasDigit(rows[r - 1]) || HeaderRoles(Filled(rows[r - 1])) != null) return cells;
            var below = cells.ToList();
            foreach (var upper in Filled(rows[r - 1]))
            {
                int first = upper.Column, last = upper.Column + upper.ColSpan - 1;
                if (!below.Any(c => c.Column <= last && c.Column + c.ColSpan - 1 >= first)) cells.Add(upper);
            }
            return cells.OrderBy(c => c.Column).ToList();
        }

        /// <summary>The roles a header's cells name, or <c>null</c> when they are not a header.</summary>
        private static Columns HeaderRoles(List<PdfTableCell> filled)
        {
            var roles = new Columns();
            bool amountIsTotal = false;
            foreach (var cell in filled)
            {
                string folded = InvoiceValueParser.LabelKey(cell.Text);
                string key = WithoutQualifiers(folded);
                if (key.Length == 0 || key.Any(char.IsDigit)) continue;
                // The part of the amount liable to VAT, or free of it, is no role's: not the tax's.
                if (FoldedVatParts.Any(key.Contains))
                {
                    roles.VatParts.Add(ColumnSpan.Of(cell, filled.IndexOf(cell), filled.Count));
                    continue;
                }
                foreach (var (role, roleKey) in FoldedRoleKeys)
                {
                    if (!key.Contains(roleKey)) continue;
                    // A cell naming the tax is the tax's or nobody's: "סה"כ מע"מ" beside "אחוז
                    // מע"מ" is the VAT's total, no line's amount.
                    if (role == Role.Tax && roles.ContainsKey(Role.Tax)) break;
                    bool total = role == Role.Amount && FoldedTotalKeys.Contains(roleKey);
                    // A cell whose first role is taken may still be the next one it names; an
                    // amount taken by a sum is a total's still.
                    if (roles.ContainsKey(role) && !(total && !amountIsTotal)) continue;
                    roles[role] = ColumnSpan.Of(cell, filled.IndexOf(cell), filled.Count);
                    if (role == Role.Amount)
                    {
                        amountIsTotal = total;
                        roles.AmountIncludesVat = FoldedWithVat.Any(folded.Contains);
                    }
                    break;
                }
            }
            SplitTwoRoleCells(filled, roles);

            // A statement of uses — parkings, tunnel passages — is described by each use's date.
            bool priced = roles.ContainsKey(Role.Amount) || roles.ContainsKey(Role.UnitPrice) || roles.VatParts.Count > 0;
            bool described = roles.ContainsKey(Role.Description) || roles.ContainsKey(Role.Quantity)
                             || roles.ContainsKey(Role.Date);
            return priced && described && roles.Count + roles.VatParts.Count >= 2 ? roles : null;
        }

        /// <summary>
        /// Gives every role a place among the header's cells read with each cell naming two roles
        /// word by word as two — two headers a page's separators ran together (<c>תעריף סה"כ</c>,
        /// the labour table's columns set elsewhere than the parts table's above it). The place is
        /// taken only by a row with a cell for each (<see cref="InHeadersPlace"/>); a heading of
        /// two words for one column ("Item code") keeps its row's count and its one role.
        /// </summary>
        private static void SplitTwoRoleCells(List<PdfTableCell> filled, Dictionary<Role, ColumnSpan> roles)
        {
            var named = filled.Select(WordRoles).ToList();
            if (!named.Any(n => n.Count == 2)) return;
            int cells = named.Sum(n => n.Count == 2 ? 2 : 1);
            int place = 0;
            for (int i = 0; i < filled.Count; i++)
            {
                if (named[i].Count != 2)
                {
                    foreach (var role in roles.Where(kv => kv.Value.Ordinal == i).Select(kv => kv.Key).ToList())
                        roles[role] = roles[role].WithSplit(place, cells);
                    place++;
                    continue;
                }
                foreach (var role in named[i])
                {
                    // The role the cell did not take is its only when it is read as two: one cell
                    // is no row's quantity and price at once.
                    if (!roles.TryGetValue(role, out ColumnSpan span))
                        roles[role] = ColumnSpan.Of(filled[i], -1, 0).WithSplit(place, cells);
                    else if (span.Ordinal == i)
                        roles[role] = span.WithSplit(place, cells);
                    place++;
                }
            }
        }

        // The distinct roles a header cell's words name one by one, in its text's order — a word of
        // a qualifier (the מע"מ of "סה"כ לפני מע"מ") naming none, and a cell holding a figure none,
        // as it names none whole: a header's words run together with the document's dates are no
        // heading (HeaderRoles).
        private static List<Role> WordRoles(PdfTableCell cell)
        {
            string whole = WithoutQualifiers(InvoiceValueParser.LabelKey(cell.Text));
            var named = new List<Role>();
            if (whole.Any(char.IsDigit)) return named;
            foreach (string word in cell.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                string key = InvoiceValueParser.LabelKey(word);
                foreach (var (role, roleKey) in FoldedRoleKeys)
                {
                    if (!key.Contains(roleKey) || !whole.Contains(roleKey)) continue;
                    if (!named.Contains(role)) named.Add(role);
                    break;
                }
            }
            return named;
        }

        private static string WithoutQualifiers(string key)
        {
            foreach (string q in FoldedQualifiers)
                key = key.Replace(q, string.Empty);
            return key;
        }

        /// <summary>
        /// The horizontal stretch each header cell answers for: from halfway to its left neighbour
        /// to halfway to its right one, the outermost reaching to the page's edges — with the role
        /// the cell names, if any.
        /// </summary>
        private static List<(Role? Role, float From, float To)> Regions(List<PdfTableCell> header, Dictionary<Role, ColumnSpan> roles)
        {
            var cells = header.OrderBy(c => c.X).ToList();
            var regions = new List<(Role? Role, float From, float To)>();
            for (int i = 0; i < cells.Count; i++)
            {
                float from = i == 0 ? float.MinValue : (cells[i - 1].X + cells[i - 1].Width + cells[i].X) / 2f;
                float to = i == cells.Count - 1 ? float.MaxValue : (cells[i].X + cells[i].Width + cells[i + 1].X) / 2f;
                Role? role = roles.Where(kv => kv.Value.First == cells[i].Column).Select(kv => (Role?)kv.Key).FirstOrDefault();
                regions.Add((role, from, to));
            }
            return regions;
        }

        /// <summary>
        /// The roles of a headerless continuation table's columns: each column takes the role of
        /// the header region the centres of the cells starting in it fall in, and answers for that
        /// region — a row's value for the role is then the cell under it (<see cref="CellFor"/>).
        /// </summary>
        private static Columns ColumnsByPosition(PdfTable table, List<(Role? Role, float From, float To)> regions, Columns header)
        {
            var columns = new Columns { AmountIncludesVat = header.AmountIncludesVat };
            if (regions == null) return columns;
            for (int c = 0; c < table.ColumnCount; c++)
            {
                var centres = table.Rows.Select(r => r.Cells.FirstOrDefault(x => x.Column == c))
                    .Where(x => x != null && x.Text.Trim().Length > 0)
                    .Select(x => x.X + x.Width / 2f)
                    .ToList();
                if (centres.Count == 0) continue;
                float centre = centres.Average();
                foreach (var (role, from, to) in regions)
                    if (role != null && centre >= from && centre < to && !columns.ContainsKey(role.Value))
                        columns[role.Value] = new ColumnSpan(c, c, from, to);
            }
            return columns;
        }

        /// <summary>
        /// A row's cell under a role: the one meeting the role's columns — when several do, the one
        /// most of whose width lies under the role's header — or <c>null</c>.
        /// </summary>
        private static PdfTableCell CellFor(PdfTableRow row, ColumnSpan span)
        {
            PdfTableCell best = null;
            float bestOverlap = 0f;
            foreach (var cell in row.Cells)
            {
                if (!span.Meets(cell)) continue;
                float overlap = Math.Min(cell.X + cell.Width, span.Right) - Math.Max(cell.X, span.Left);
                if (best == null || overlap > bestOverlap) { best = cell; bestOverlap = overlap; }
            }
            return best;
        }

        /// <summary>
        /// Whether a cell can hold a number for <paramref name="role"/> (<c>null</c> for a part of
        /// the amount): it covers no other role's columns — a note across the table is no row's
        /// quantity, price and amount at once — and is no phrase: two words with letters ("ראה הערה
        /// 3") are a remark, where a number has at most its currency or unit beside it.
        /// </summary>
        private static bool HoldsANumber(PdfTableCell cell, Role? role, Dictionary<Role, ColumnSpan> columns)
        {
            foreach (var kv in columns)
                if (kv.Key != role && kv.Value.Meets(cell)) return false;
            return IsNoPhrase(cell);
        }

        // At most one word with letters: a number's currency or unit, not a remark.
        private static bool IsNoPhrase(PdfTableCell cell) => IsNoPhrase(cell.Text);

        private static bool IsNoPhrase(string text)
            => text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter)) < 2;

        // A row's filled cells, in the table's column order.
        private static List<PdfTableCell> Filled(PdfTableRow row)
            => row.Cells.Where(c => c.Text.Trim().Length > 0).OrderBy(c => c.Column).ToList();

        /// <summary>
        /// A row holding as many filled cells as its header: the cell in the header cell's place.
        /// A header set right over numbers set left lies in another column than its numbers — the
        /// page's separators also come from the tables beside it — and is its numbers' all the same.
        /// A row holding one more for each header cell naming two roles takes the place the split
        /// gives (<see cref="SplitTwoRoleCells"/>).
        /// </summary>
        private static PdfTableCell InHeadersPlace(PdfTableRow row, ColumnSpan span)
        {
            var filled = Filled(row);
            if (span.Ordinal >= 0 && filled.Count == span.HeaderCells) return filled[span.Ordinal];
            return span.SplitOrdinal >= 0 && filled.Count == span.SplitCells ? filled[span.SplitOrdinal] : null;
        }

        /// <summary>Reads one row into <paramref name="items"/>; returns <c>true</c> at the totals.</summary>
        private static bool ReadRow(PdfTableRow row, int pageIndex, Columns columns, List<InvoiceLineItem> items)
        {
            var used = new HashSet<PdfTableCell>();
            string Under(ColumnSpan span, Role? role)
            {
                var placed = InHeadersPlace(row, span);
                var cell = placed ?? CellFor(row, span);
                if (cell == null || (role == null && used.Contains(cell))) return string.Empty;
                used.Add(cell);
                bool text = role == Role.Description || role == Role.CatalogNumber || role == Role.Date;
                bool number = placed != null ? IsNoPhrase(cell) : HoldsANumber(cell, role, columns);
                return text || number ? cell.Text.Trim() : string.Empty;
            }
            string Cell(Role role) => columns.TryGetValue(role, out ColumnSpan span) ? Under(span, role) : string.Empty;

            string description = Cell(Role.Description);
            string date = Cell(Role.Date);
            // A catalog number has a digit: a section's title in its column ("עבודה") is none; and
            // it is a code, no phrase — a remark across a toll bill's "מקטע נסיעה" (its folded מקט
            // a catalog number's) is none.
            string catalog = Cell(Role.CatalogNumber);
            if (!catalog.Any(char.IsDigit) || !IsNoPhrase(catalog)) catalog = string.Empty;
            // A count, with no currency: a totals row's "₪ 99.00" in the quantity's column is none.
            decimal? quantity = InvoiceValueParser.TryParseAmount(Cell(Role.Quantity), out decimal q, out string currency)
                                && currency == null ? q : (decimal?)null;
            decimal? price = Number(Cell(Role.UnitPrice));
            decimal? amount = Number(Cell(Role.Amount));
            string discount = Cell(Role.Discount);
            if (!columns.ContainsKey(Role.Amount))
            {
                // A line's amount in parts — liable to VAT, free of it — is their sum: each part's
                // cell counted once.
                foreach (var part in columns.VatParts)
                    if (Number(Under(part, null)) is decimal value) amount = (amount ?? 0m) + value;
            }

            // A use with no description column is its date and the words beside it: "12/06/2024
            // כפר סבא", not its times or its vehicle's number.
            if (!columns.ContainsKey(Role.Description) && date.Length > 0)
                description = string.Join(" ", new[] { date }.Concat(
                    row.Cells.Where(c => !used.Contains(c) && c.Text.Any(char.IsLetter))
                             .OrderBy(c => c.Column).Select(c => c.Text.Trim())));

            if (quantity == null)
            {
                // The label is looked for in the cells holding letters, whichever column the
                // detector put them in — and the totals' amounts may have landed in any numeric
                // column.
                var labels = row.Cells.Where(c => c.Text.Any(char.IsLetter)).Select(c => Words(c.Text)).ToList();

                // A subtotal carried to the next page, or brought from the last: neither an item
                // nor the end of the items.
                if (labels.Any(words => CarryWords.Any(carry => ContainsRun(words, carry)))) return false;

                // The totals: a cell that opens with a total's label.
                if (labels.Any(words => TotalsWords.Any(total => StartsWith(words, total)))) return true;
            }

            bool hasNumbers = quantity != null || price != null || amount != null;
            if (!hasNumbers && catalog.Length == 0)
            {
                if (description.Length > 0 && items.Count > 0 && items[items.Count - 1].PageIndex == pageIndex)
                    items[items.Count - 1].Description = (items[items.Count - 1].Description + " " + description).Trim();
                return false;
            }

            items.Add(new InvoiceLineItem
            {
                PageIndex = pageIndex,
                CatalogNumber = catalog.Length > 0 ? catalog : null,
                Description = description.Length > 0 ? description : null,
                Quantity = quantity,
                UnitPrice = price,
                Discount = discount.Length > 0 ? discount : null,
                Amount = amount,
                AmountIncludesVat = amount != null && columns.AmountIncludesVat,
            });
            return false;
        }

        // A text's words, each a label key; a word the folding empties (a colon alone) is dropped.
        private static string[] Words(string text)
            => (text ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(InvoiceValueParser.LabelKey)
                                     .Where(w => w.Length > 0)
                                     .ToArray();

        private static bool StartsWith(string[] words, string[] prefix)
        {
            if (prefix.Length == 0 || words.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (words[i] != prefix[i]) return false;
            return true;
        }

        private static bool ContainsRun(string[] words, string[] run)
        {
            for (int start = 0; start + run.Length <= words.Length; start++)
            {
                int k = 0;
                while (k < run.Length && words[start + k] == run[k]) k++;
                if (k == run.Length && run.Length > 0) return true;
            }
            return false;
        }

        // Any digit, Arabic-Indic included: whether the row holds figures, not what they are.
        private static bool HasDigit(PdfTableRow row) => row.Cells.Any(c => c.Text.Any(char.IsDigit));

        private static decimal? Number(string text)
            => InvoiceValueParser.TryParseAmount(text, out decimal v, out _) ? v : (decimal?)null;
    }
}
