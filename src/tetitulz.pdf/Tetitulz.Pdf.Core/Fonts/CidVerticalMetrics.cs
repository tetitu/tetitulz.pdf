// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A CIDFont's vertical writing metrics (ISO 32000-2 §9.7.4.3): the <c>/W2</c> lookup and the
    /// Table 117 defaults — one implementation for both renderers (through <c>Base.Rendering.FontCache</c>)
    /// and the PDF/UA-2 vertical-metrics check, so the check judges the metrics a reader draws with.
    /// </summary>
    internal static class CidVerticalMetrics
    {
        /// <summary>The vertical displacement <c>w1y</c> when <c>/DW2</c> gives none: <c>[880 −1000]</c>'s second number.</summary>
        internal const float DefaultDisplacementY = (float)-PdfUnits.GlyphSpaceUnitsPerEm;

        /// <summary>The position vector's y when <c>/DW2</c> gives none: the vertical origin sits 880 units above the horizontal one.</summary>
        internal const float DefaultPositionY = 880f;

        /// <summary>
        /// Finds the <c>/W2</c> entry covering <paramref name="cid"/> and returns all three of its
        /// numbers. Both forms of Table 117 are handled: <c>c [w1y v1x v1y …]</c>, triplets for
        /// consecutive CIDs, and <c>cfirst clast w1y v1x v1y</c>.
        /// </summary>
        /// <param name="w2">The <c>/W2</c> array, resolved.</param>
        /// <param name="cid">The CID looked up.</param>
        /// <param name="w1y">The entry's vertical displacement, in glyph units.</param>
        /// <param name="v1x">The entry's position vector x, in glyph units.</param>
        /// <param name="v1y">The entry's position vector y, in glyph units.</param>
        /// <param name="resolve">
        /// Follows an indirect reference among the array's elements, as <c>PdfType0Font.ParseWArray</c>
        /// takes one for <c>/W</c>; <c>null</c> reads the elements as stored. Every element — a CID, a
        /// nested array, a number inside one — is read through it: the three readers of a CIDFont's
        /// metrics (<c>/W</c>, <c>/DW2</c>, <c>/W2</c>) resolved only the first, so a <c>/W2</c> written
        /// with a referenced number fell back to the defaults in the renderers and in the PDF/UA-2 check.
        /// </param>
        internal static bool TryFind(PdfArray w2, int cid, out float w1y, out float v1x, out float v1y,
                                     Func<PdfObject, PdfObject> resolve = null)
            => Index.Read(w2, resolve).TryFind(cid, out w1y, out v1x, out v1y);

        /// <summary>
        /// A <c>/W2</c> array read once, for a caller that asks it for many CIDs: each CID's entry is
        /// the first in the array that covers it, found by a binary search.
        /// </summary>
        /// <remarks>
        /// The array was scanned for every glyph shown, so a vertical font with a long <c>/W2</c>
        /// cost glyphs × entries in both renderers and in the PDF/UA-2 check — where <c>/W</c> has
        /// been read once into an index all along (<c>PdfType0Font.ParseWArray</c>). A range entry may
        /// name tens of thousands of CIDs, so the index keeps intervals, never one slot a CID: the
        /// entries' intervals in array order, swept into disjoint stretches each held by the earliest
        /// entry covering it — the answer the scan gave for a malformed array whose entries overlap.
        /// </remarks>
        internal sealed class Index
        {
            // Disjoint stretches of CIDs, ascending, and the entry each one takes.
            private readonly long[] _lo;
            private readonly long[] _hi;
            private readonly Entry[] _entries;
            private readonly Func<PdfObject, PdfObject> _resolve;

            private Index(long[] lo, long[] hi, Entry[] entries, Func<PdfObject, PdfObject> resolve)
            {
                _lo = lo;
                _hi = hi;
                _entries = entries;
                _resolve = resolve;
            }

            /// <summary>The stretches the index holds: its evidence, which a lookup's answer cannot show.</summary>
            internal int Stretches => _lo.Length;

            // An entry: a range's three numbers, or a triplet array and the CID its first triplet is for.
            private sealed class Entry
            {
                internal long Lo, Hi;
                internal PdfArray Triplets;
                internal long FirstCid;
                internal float W1y, V1x, V1y;
            }

            /// <summary>How many arrays this thread has read: a caller's evidence that it reads one once.</summary>
            [ThreadStatic] internal static long ReadCount;

            /// <summary>Reads <paramref name="w2"/>, both forms of Table 117, through <paramref name="resolve"/>.</summary>
            internal static Index Read(PdfArray w2, Func<PdfObject, PdfObject> resolve = null)
            {
                ReadCount++;
                Func<PdfObject, PdfObject> r = resolve == null ? (o => o) : (o => resolve(o) ?? o);
                var entries = new List<Entry>();
                int i = 0;
                while (w2 != null && i + 1 < w2.Count)
                {
                    // IntValue saturates where `(int)LongValue` would wrap a CID past 2^31 onto a small
                    // one; the triplet span is formed in long so a wide gap cannot wrap either.
                    int c1 = (r(w2[i]) as PdfNumber)?.IntValue ?? 0;
                    var second = r(w2[i + 1]);

                    if (second is PdfArray arr)
                    {
                        // Format: c1 [w1y v1x v1y  w1y v1x v1y  …] — triplets per §9.7.4.3; a CID is
                        // covered while its whole triplet is there.
                        long triplets = arr.Count / 3;
                        if (triplets > 0)
                            entries.Add(new Entry { Lo = c1, Hi = c1 + triplets - 1, Triplets = arr, FirstCid = c1 });
                        i += 2;
                    }
                    else if (second is PdfNumber c2Num && i + 4 < w2.Count)
                    {
                        // Format: cfirst clast w1y v1x v1y — FIVE numbers, not three (Table 117).
                        // /W2's range form carries the position vector as well as the advance, unlike
                        // /W's `c1 c2 w`. Advancing by 3 read the next entry from the middle of this one,
                        // so every entry after the first range was garbage — usually a v1x value
                        // interpreted as a starting CID.
                        int c2 = c2Num.IntValue;
                        if (c1 <= c2)
                            entries.Add(new Entry
                            {
                                Lo = c1, Hi = c2,
                                W1y = (float)((r(w2[i + 2]) as PdfNumber)?.DoubleValue ?? -PdfUnits.GlyphSpaceUnitsPerEm),
                                V1x = (float)((r(w2[i + 3]) as PdfNumber)?.DoubleValue ?? 0),
                                V1y = (float)((r(w2[i + 4]) as PdfNumber)?.DoubleValue ?? DefaultPositionY),
                            });
                        i += 5;
                    }
                    else { i++; }
                }

                // The sweep: at each boundary the entries that start there join the active set and
                // those that ended leave it; the earliest active entry holds the stretch up to the
                // next boundary.
                var bounds = new List<(long At, bool Starts, int Order)>(entries.Count * 2);
                for (int k = 0; k < entries.Count; k++)
                {
                    bounds.Add((entries[k].Lo, true, k));
                    bounds.Add((entries[k].Hi + 1, false, k));
                }
                bounds.Sort((a, b) => a.At.CompareTo(b.At));

                var active = new SortedSet<int>();
                var lo = new List<long>();
                var hi = new List<long>();
                var held = new List<Entry>();
                for (int b = 0; b < bounds.Count;)
                {
                    long at = bounds[b].At;
                    for (; b < bounds.Count && bounds[b].At == at; b++)
                    {
                        if (bounds[b].Starts) active.Add(bounds[b].Order);
                        else active.Remove(bounds[b].Order);
                    }
                    if (active.Count == 0 || b == bounds.Count) continue;

                    var entry = entries[active.Min];
                    long end = bounds[b].At - 1;
                    if (held.Count > 0 && ReferenceEquals(held[held.Count - 1], entry) && hi[hi.Count - 1] == at - 1)
                        hi[hi.Count - 1] = end;
                    else
                    {
                        lo.Add(at);
                        hi.Add(end);
                        held.Add(entry);
                    }
                }

                return new Index(lo.ToArray(), hi.ToArray(), held.ToArray(), r);
            }

            /// <summary>The three numbers of the entry covering <paramref name="cid"/>, or <c>false</c>.</summary>
            internal bool TryFind(int cid, out float w1y, out float v1x, out float v1y)
            {
                w1y = 0f; v1x = 0f; v1y = 0f;
                int at = Array.BinarySearch(_lo, (long)cid);
                if (at < 0) at = ~at - 1;
                if (at < 0 || cid > _hi[at]) return false;

                var entry = _entries[at];
                if (entry.Triplets == null)
                {
                    w1y = entry.W1y; v1x = entry.V1x; v1y = entry.V1y;
                    return true;
                }

                int triplet = (int)((cid - entry.FirstCid) * 3);
                w1y = (float)((_resolve(entry.Triplets[triplet])     as PdfNumber)?.DoubleValue ?? -PdfUnits.GlyphSpaceUnitsPerEm);
                v1x = (float)((_resolve(entry.Triplets[triplet + 1]) as PdfNumber)?.DoubleValue ?? 0);
                v1y = (float)((_resolve(entry.Triplets[triplet + 2]) as PdfNumber)?.DoubleValue ?? DefaultPositionY);
                return true;
            }
        }
    }
}
