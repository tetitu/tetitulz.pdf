// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Whether two PDF objects say the same thing, "compression and whether or not an object is
    /// direct or indirect … ignored" — ISO 19005-2 6.2.4.4's wording for comparing spot-colour
    /// definitions, which PDF/X's X18 asks the same way.
    /// </summary>
    /// <remarks>
    /// <para>References are followed. Numbers compare by value (<c>1</c> and <c>1.0</c> are equal),
    /// strings by their bytes, dictionaries by key set and values. A stream compares its
    /// dictionary without the keys that describe its encoding (<c>/Length</c>, <c>/Filter</c>,
    /// <c>/DecodeParms</c>, <c>/DL</c>) and then its decoded bytes.</para>
    /// <para>Bounded: a node budget the caller carries across comparisons, a depth cap, a cap on
    /// each stream's decoded size, and a byte budget the caller carries across comparisons that
    /// every pair of decoded streams is charged — a node is one object whatever its size, so a
    /// node budget alone priced a 16 MiB comparison as one node. Running into any of them, or a
    /// stream that will not decode, answers <see cref="Result.Undecided"/> rather than guessing
    /// either way.</para>
    /// </remarks>
    internal static class PdfObjectEquivalence
    {
        internal enum Result { Same, Different, Undecided }

        internal static Result Compare(PdfDocument doc, PdfObject a, PdfObject b,
            ref int budget, long maxStreamBytes)
        {
            long bytes = long.MaxValue;
            return Compare(doc, a, b, ref budget, maxStreamBytes, ref bytes, 0);
        }

        /// <summary>As above, every pair of decoded streams charged to <paramref name="byteBudget"/>; <see cref="Result.Undecided"/> once it is spent.</summary>
        internal static Result Compare(PdfDocument doc, PdfObject a, PdfObject b,
            ref int budget, long maxStreamBytes, ref long byteBudget)
            => Compare(doc, a, b, ref budget, maxStreamBytes, ref byteBudget, 0);

        private static Result Compare(PdfDocument doc, PdfObject a, PdfObject b,
            ref int budget, long maxStreamBytes, ref long byteBudget, int depth)
        {
            if (depth > PdfLimits.MaxResourceDepth || --budget < 0) return Result.Undecided;

            a = a is PdfReference ? doc.ResolveObject(a) : a;
            b = b is PdfReference ? doc.ResolveObject(b) : b;
            if (ReferenceEquals(a, b)) return Result.Same;
            if (a == null || b == null) return Result.Different;

            switch (a)
            {
                case PdfName na:
                    return b is PdfName nb && na.Value == nb.Value ? Result.Same : Result.Different;
                case PdfNumber xa:
                    return b is PdfNumber xb && xa.DoubleValue == xb.DoubleValue ? Result.Same : Result.Different;
                case PdfString sa:
                    return b is PdfString sb && sa.RawBytes.SequenceEqual(sb.RawBytes) ? Result.Same : Result.Different;
                case PdfBoolean ba:
                    return b is PdfBoolean bb && ba.Value == bb.Value ? Result.Same : Result.Different;
                case PdfNull _:
                    return b is PdfNull ? Result.Same : Result.Different;
                case PdfArray aa:
                {
                    if (!(b is PdfArray ab) || aa.Count != ab.Count) return Result.Different;
                    var verdict = Result.Same;
                    for (int i = 0; i < aa.Count; i++)
                    {
                        var r = Compare(doc, aa[i], ab[i], ref budget, maxStreamBytes, ref byteBudget, depth + 1);
                        if (r == Result.Different) return r;
                        if (r == Result.Undecided) verdict = r;
                    }
                    return verdict;
                }
                case PdfStream ta:
                {
                    if (!(b is PdfStream tb)) return Result.Different;
                    var entries = CompareEntries(doc, ta, tb, ref budget, maxStreamBytes, ref byteBudget, depth, skipEncoding: true);
                    if (entries == Result.Different) return entries;
                    if (!ta.TryGetDecodedData(maxStreamBytes, out byte[] da) ||
                        !tb.TryGetDecodedData(maxStreamBytes, out byte[] db))
                        return Result.Undecided;
                    // Charged before the comparison, by what was decoded: the price is the bytes.
                    byteBudget -= (long)da.Length + db.Length;
                    if (byteBudget < 0) return Result.Undecided;
                    if (da.Length != db.Length || !da.AsSpan().SequenceEqual(db)) return Result.Different;
                    return entries;
                }
                case PdfDictionary da2:
                    return b is PdfDictionary db2 && !(b is PdfStream)
                        ? CompareEntries(doc, da2, db2, ref budget, maxStreamBytes, ref byteBudget, depth, skipEncoding: false)
                        : Result.Different;
                default:
                    return Result.Undecided;
            }
        }

        private static bool IsEncodingKey(PdfName key)
            => key.Value == PdfName.Length.Value || key.Value == PdfName.Filter.Value
            || key.Value == PdfName.DecodeParms.Value || key.Value == PdfName.DL.Value;

        private static Result CompareEntries(PdfDocument doc, PdfDictionary a, PdfDictionary b,
            ref int budget, long maxStreamBytes, ref long byteBudget, int depth, bool skipEncoding)
        {
            int countA = 0, countB = 0;
            foreach (var kvp in b) if (!(skipEncoding && IsEncodingKey(kvp.Key))) countB++;

            var verdict = Result.Same;
            foreach (var kvp in a)
            {
                if (skipEncoding && IsEncodingKey(kvp.Key)) continue;
                countA++;
                var other = b[kvp.Key];
                if (other == null) return Result.Different;
                var r = Compare(doc, kvp.Value, other, ref budget, maxStreamBytes, ref byteBudget, depth + 1);
                if (r == Result.Different) return r;
                if (r == Result.Undecided) verdict = r;
            }
            return countA == countB ? verdict : Result.Different;
        }
    }
}
