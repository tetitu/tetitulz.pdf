// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>How many objects, direct ones included, the implementation-limit walk reads before it stops and says so.</summary>
        internal const int MaxLimitWalkNodes = 2_000_000;

        /// <summary>
        /// The implementation limits, PDF/A-1 to -3 (ISO 19005-4 has none). A65 — every object of the
        /// file, and every operand of the content the pages draw: an integer outside 32 bits; a real
        /// beyond ±32 767 (PDF/A-1), or beyond ±3.403 × 10³⁸ or nearer zero than ±1.175 × 10⁻³⁸
        /// (PDF/A-2, -3); a string longer than 65 535 bytes (PDF/A-1) or 32 767; a name longer than 127
        /// bytes; in PDF/A-1 an array of more than 8 191 elements and a dictionary of more than 4 095
        /// entries; more than 8 388 607 indirect objects; <c>q</c> nested deeper than 28 in a stream; a
        /// DeviceN space of more than 8 colorants (PDF/A-1) or 32 (ISO 19005-1 6.1.12, 19005-2/3 6.1.13;
        /// rules -1 to -9 of each); an embedded CMap mapping a code to a CID above 65 535 (rule
        /// -10, through <c>Core.Fonts.CMapCids.MaximalCid</c>). A66 — PDF/A-2
        /// and -3: a page boundary narrower or shorter than 3 units or wider or taller than 14 400, the
        /// MediaBox and CropBox as the page inherits them (6.1.13, rule 6.1.13-11). A4-06 — PDF/A-4, the
        /// same walk: a name whose bytes are not UTF-8 (ISO 19005-4 6.1.7, ISO 32000-2 §7.3.5). A87 —
        /// PDF/A-2 and -3, the same walk: a name whose bytes are not UTF-8 where 6.1.8 asks for UTF-8 —
        /// a font's <c>/BaseFont</c> or a descriptor's <c>/FontName</c>, a <c>/Separation</c> or
        /// <c>/DeviceN</c> colourant, a structure element's <c>/S</c> (rule 6.1.8-1, which reads
        /// these as Unicode names and no other).
        /// </summary>
        /// <remarks>
        /// A name's length is its bytes': the UTF-8 count of its value, or for a name whose bytes were
        /// not UTF-8 (<see cref="PdfName.BytesNotUtf8"/>, the value their Latin-1 reading) one a
        /// character. A real written with a period and an integral value (<c>40000.0</c>) reads as an
        /// integer, so PDF/A-1's real limit sees only a real with a fraction.
        /// </remarks>
        private static void CheckImplementationLimits(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (doc == null) return;
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            var limits = new Occurrences();
            var notUtf8 = new Occurrences();
            var unicodeNames = new Occurrences();
            var walk = new LimitWalk(part1, part4, limits, notUtf8, unicodeNames);

            if (doc.Parser != null)
            {
                int unreadable = 0;
                long indirect = 0;
                foreach (var kvp in doc.Parser.EnumerateAllObjects((number, ex) => unreadable++))
                {
                    indirect++;
                    walk.Visit(kvp.Value, "object " + kvp.Key.ToString(CultureInfo.InvariantCulture), 0);
                    if (!part4 && kvp.Value is PdfStream cmap && (doc.ResolveObject(cmap[PdfName.Type]) as PdfName)?.Value == "CMap")
                        CheckCMapLargestCid(cmap, kvp.Key, limits);
                    if (walk.Exhausted) break;
                }
                if (!part4 && indirect > 8_388_607)
                    limits.Add(indirect.ToString(CultureInfo.InvariantCulture) + " indirect objects");
                if (unreadable > 0)
                    ReportPartial($"The A65 implementation-limit check could not read {unreadable.ToString(CultureInfo.InvariantCulture)} object(s).", "A65");
            }

            var seenStreams = new HashSet<PdfStream>();
            long documentBytes = 0;
            for (int p = 0; doc.Pages != null && p < doc.Pages.Count && !walk.Exhausted; p++)
            {
                string page = "page " + (p + 1).ToString(CultureInfo.InvariantCulture);
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A65 implementation-limit check did not run in full — {scan.Incomplete}.", "A65");
                bool skip = false;
                int nesting = 0;
                string where = null;
                WalkDrawnOperations(doc, scan,
                    (i, initialState) =>
                    {
                        var source = scan.ContentStreamSources[i];
                        skip = source != null && !seenStreams.Add(source);
                        nesting = 0;
                        where = source == null ? page + " content"
                            : page + ", object " + source.ObjectNumber.ToString(CultureInfo.InvariantCulture);
                        return true;
                    },
                    (i, op) =>
                    {
                        if (skip) return true;
                        if (!part4 && op.Operator == PdfOperators.Save && ++nesting == 29)
                            limits.Add(where + ": q nested deeper than 28");
                        else if (op.Operator == PdfOperators.Restore && nesting > 0)
                            nesting--;
                        foreach (var operand in op.Operands)
                            if (!(operand is PdfStream)) walk.Visit(operand, where + ": " + op.Operator, 0);
                        return !walk.Exhausted;
                    });
            }
            if (walk.Exhausted)
                ReportPartial($"The A65 implementation-limit check stopped after {MaxLimitWalkNodes.ToString(CultureInfo.InvariantCulture)} objects.", "A65");

            if (part4 && notUtf8.Count > 0)
                v.Add(new PdfAViolation("A4-06",
                    $"A name's bytes are not UTF-8: {notUtf8} (ISO 19005-4 6.1.7, Name objects; ISO 32000-2 §7.3.5)."));
            if (part4) return;
            if (!part1 && unicodeNames.Count > 0)
                v.Add(new PdfAViolation("A87",
                    $"A font, colourant or structure type name's bytes are not UTF-8: {unicodeNames} " +
                    $"(ISO 19005-{(int)part} 6.1.8; rule 6.1.8-1)."));

            string iso = part1 ? "ISO 19005-1 6.1.12; rules 6.1.12-1 to -10" : $"ISO 19005-{(int)part} 6.1.13; rules 6.1.13-1 to -10";
            if (limits.Count > 0)
                v.Add(new PdfAViolation("A65", $"The file exceeds an implementation limit: {limits} ({iso})."));

            if (!part1) CheckPageBoundaries(doc, part, v);
        }

        /// <summary>
        /// A non-UTF-8 name as the file spelled it: its value is the bytes' Latin-1 reading, one
        /// character a byte, so each byte outside printable ASCII (or a delimiter) is written <c>#XX</c>.
        /// Re-escaping the value would write its UTF-8 — bytes the file never held.
        /// </summary>
        private static string FileBytes(PdfName name)
        {
            var sb = new StringBuilder(name.Value.Length);
            foreach (char c in name.Value)
            {
                // §7.3.5: a byte outside ! to ~, the delimiters and # itself are written #XX.
                if (c < 0x21 || c > 0x7E || "#()<>[]{}/%".IndexOf(c) >= 0)
                    sb.Append('#').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Rule -10: an embedded CMap's largest CID is at most 65 535.</summary>
        private static void CheckCMapLargestCid(PdfStream cmap, int objectNumber, Occurrences limits)
        {
            byte[] data;
            try { data = Core.Fonts.FontStreamBytes.CMap(cmap); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Report("A65", "object " + objectNumber.ToString(CultureInfo.InvariantCulture) + ": a CMap that does not decode (" + ex.GetType().Name + ")");
                return;
            }
            long largest = Tetitulz.Pdf.Core.Fonts.CMapCids.MaximalCid(data);
            if (largest > 65535)
                limits.Add("object " + objectNumber.ToString(CultureInfo.InvariantCulture) + ": a CMap mapping to CID " +
                           largest.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The object half of A65 (PDF/A-1 to -3) and of A4-06 (PDF/A-4, names alone): one value, its
        /// elements and entries, references not followed.
        /// </summary>
        private sealed class LimitWalk
        {
            private readonly bool _part1;
            private readonly bool _namesOnly;
            private readonly Occurrences _found;
            private readonly Occurrences _notUtf8;
            private readonly Occurrences _unicodeNames;
            private int _nodes;

            internal LimitWalk(bool part1, bool namesOnly, Occurrences found, Occurrences notUtf8, Occurrences unicodeNames)
            {
                _part1 = part1;
                _namesOnly = namesOnly;
                _found = found;
                _notUtf8 = notUtf8;
                _unicodeNames = unicodeNames;
            }

            /// <summary>
            /// The names 6.1.8 of PDF/A-2 and -3 asks to be UTF-8, as the rule 6.1.8-1 reads them:
            /// a font's <c>/BaseFont</c>, a descriptor's <c>/FontName</c>, a structure element's
            /// <c>/S</c> — one whose <c>/Type</c> says so, or that has a <c>/P</c> parent, since an
            /// action and a transition carry an <c>/S</c> too — and a colourant space's names.
            /// </summary>
            private void UnicodeNames(PdfDictionary dictionary, string where)
            {
                Unicode(dictionary[PdfName.BaseFont] as PdfName, where, "/BaseFont");
                Unicode(dictionary[PdfName.FontName] as PdfName, where, "/FontName");
                var type = dictionary[PdfName.Type] as PdfName;
                if (type == PdfName.StructElem || (type == null && dictionary.ContainsKey(PdfName.P)))
                    Unicode(dictionary[PdfName.S] as PdfName, where, "/S");
            }

            private void UnicodeNames(PdfArray array, string where)
            {
                if (array.Count < 2 || !(array[0] is PdfName family)) return;
                if (family.Value == PdfColorSpaceNames.Separation)
                    Unicode(array[1] as PdfName, where, "a /Separation colourant");
                else if (family.Value == PdfColorSpaceNames.DeviceN && array[1] is PdfArray colorants)
                    for (int i = 0; i < colorants.Count; i++)
                        Unicode(colorants[i] as PdfName, where, "a /DeviceN colourant");
            }

            private void Unicode(PdfName name, string where, string role)
            {
                if (name != null && name.BytesNotUtf8) _unicodeNames.Add($"{where}: {role} /{FileBytes(name)}");
            }

            internal bool Exhausted => _nodes >= MaxLimitWalkNodes;

            internal void Visit(PdfObject value, string where, int depth)
            {
                if (value == null || Exhausted || depth > PdfLimits.MaxObjectGraphDepth) return;
                _nodes++;
                switch (value)
                {
                    case PdfNumber number:
                        if (!_namesOnly) Number(number, where);
                        break;
                    case PdfString text when _namesOnly:
                        break;
                    case PdfString text:
                        int max = _part1 ? 65_535 : 32_767;
                        if (text.RawBytes != null && text.RawBytes.Length > max)
                            _found.Add($"{where}: a string of {text.RawBytes.Length.ToString(CultureInfo.InvariantCulture)} bytes");
                        break;
                    case PdfName name:
                        Name(name, where);
                        break;
                    case PdfArray array when _namesOnly:
                        for (int i = 0; i < array.Count; i++) Visit(array[i], where, depth + 1);
                        break;
                    case PdfArray array:
                        UnicodeNames(array, where);
                        if (_part1 && array.Count > 8_191)
                            _found.Add($"{where}: an array of {array.Count.ToString(CultureInfo.InvariantCulture)} elements");
                        if (array.Count > 1 && array[0] is PdfName family && family.Value == PdfColorSpaceNames.DeviceN
                            && array[1] is PdfArray colorants && colorants.Count > (_part1 ? 8 : 32))
                            _found.Add($"{where}: a DeviceN space of {colorants.Count.ToString(CultureInfo.InvariantCulture)} colorants");
                        for (int i = 0; i < array.Count; i++) Visit(array[i], where, depth + 1);
                        break;
                    case PdfDictionary dictionary:   // a stream's dictionary too; its data is not read
                        if (!_namesOnly) UnicodeNames(dictionary, where);
                        if (!_namesOnly && _part1 && dictionary.Count > 4_095)
                            _found.Add($"{where}: a dictionary of {dictionary.Count.ToString(CultureInfo.InvariantCulture)} entries");
                        foreach (var kvp in dictionary)
                        {
                            Name(kvp.Key, where);
                            Visit(kvp.Value, where, depth + 1);
                        }
                        break;
                }
            }

            /// <remarks>
            /// An integer literal past a <c>long</c> reaches here saturated (the parser's reading), so
            /// it is still reported, by its saturated value. A zero, written <c>0.0</c> or not, reads as
            /// an integer, which is why the real branch needs no exemption for it.
            /// </remarks>
            private void Number(PdfNumber number, string where)
            {
                double x = number.DoubleValue;
                if (number.IsInteger)
                {
                    if (x > int.MaxValue || x < int.MinValue)
                        _found.Add($"{where}: the integer {number.LongValue.ToString(CultureInfo.InvariantCulture)}");
                    return;
                }
                double magnitude = Math.Abs(x);
                if (_part1 ? magnitude > 32_767.0 : magnitude > 3.403e38 || magnitude < 1.175e-38)
                    _found.Add($"{where}: the real {x.ToString("R", CultureInfo.InvariantCulture)}");
            }

            private void Name(PdfName name, string where)
            {
                if (name.BytesNotUtf8) _notUtf8.Add($"{where}: /{FileBytes(name)}");
                if (_namesOnly) return;
                int bytes = name.BytesNotUtf8 ? name.Value.Length : Encoding.UTF8.GetByteCount(name.Value);
                if (bytes > 127)
                    _found.Add($"{where}: a name of {bytes.ToString(CultureInfo.InvariantCulture)} bytes");
            }
        }

        /// <summary>A66: each page's boundaries, the MediaBox and CropBox as inherited (ISO 32000-1 14.11.2).</summary>
        private static void CheckPageBoundaries(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (doc.Pages == null) return;
            var boxes = new Occurrences();
            for (int p = 0; p < doc.Pages.Count; p++)
            {
                var page = doc.Pages[p].Dictionary;
                if (page == null) continue;
                foreach (var key in new[] { PdfName.MediaBox, PdfName.CropBox, PdfName.BleedBox, PdfName.TrimBox, PdfName.ArtBox })
                {
                    bool inheritable = key == PdfName.MediaBox || key == PdfName.CropBox;
                    if (!(doc.ResolveObject(inheritable ? Inherited(doc, page, key) : page[key]) is PdfArray box) || box.Count != 4)
                        continue;
                    var n = new double[4];
                    bool numeric = true;
                    for (int i = 0; i < 4; i++)
                        if (doc.ResolveObject(box[i]) is PdfNumber number) n[i] = number.DoubleValue;
                        else numeric = false;
                    if (!numeric) continue;
                    double width = Math.Abs(n[2] - n[0]), height = Math.Abs(n[3] - n[1]);
                    if (width < 3 || height < 3 || width > 14_400 || height > 14_400)
                        boxes.Add($"page {(p + 1).ToString(CultureInfo.InvariantCulture)} /{key.Value} " +
                                  $"{width.ToString("R", CultureInfo.InvariantCulture)} × {height.ToString("R", CultureInfo.InvariantCulture)}");
                }
            }
            if (boxes.Count > 0)
                v.Add(new PdfAViolation("A66",
                    $"A page boundary is smaller than 3 units or larger than 14 400 in a dimension: {boxes} " +
                    $"(ISO 19005-{(int)part} 6.1.13; rule 6.1.13-11)."));
        }

        /// <summary>An inheritable page attribute: the page's own, else the nearest ancestor's.</summary>
        private static PdfObject Inherited(PdfDocument doc, PdfDictionary page, PdfName key)
        {
            var node = page;
            for (int depth = 0; node != null && depth <= PdfLimits.MaxObjectGraphDepth; depth++)
            {
                var value = node[key];
                if (value != null) return value;
                node = doc.ResolveObject(node[PdfName.Parent]) as PdfDictionary;
            }
            return null;
        }
    }
}
