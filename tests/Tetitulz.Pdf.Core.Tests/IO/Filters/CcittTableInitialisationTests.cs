// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The CCITT Huffman tables are built once, by the runtime, before anything can read them.
/// </summary>
/// <remarks>
/// <para><b>Not double-checked locking with the check on the wrong side of the fill.</b> A
/// dictionary assigned <i>empty</i> inside the lock and populated afterwards, while the fast path
/// reads the field without the lock, would let a second thread see the non-null empty dictionary,
/// skip the lock, and decode against run lengths that are not there yet.</para>
///
/// <para><b>This is not only a test-ordering problem.</b> <c>RenderAllPagesCore</c> renders pages on
/// parallel workers, so two workers meeting a CCITT image for the first time at the same moment is
/// the production form of it. What it would produce is a decoded bitmap with wrong pixels, which no
/// caller can distinguish from a correct one.</para>
///
/// <para><b>It is pinned structurally rather than by racing threads, because a race cannot be
/// tested for after the fact.</b> The window is the very first access in the process, and by the
/// time any test runs the type is already initialised — so a concurrency test would pass whether
/// the defect were present or not, which is the worst kind of green. A <c>readonly</c> static
/// assigned only by a type initialiser <i>cannot</i> be observed half-built: the CLR runs the
/// initialiser once and orders it before every access. Asserting that property is asserting the
/// guarantee.</para>
/// </remarks>
public class CcittTableInitialisationTests
{
    /// <summary>The tables are <c>readonly</c> statics, so no code path can publish one empty.</summary>
    [Fact]
    public void TheHuffmanTablesAreReadOnlyStatics()
    {
        Type decoder = DecoderType();

        foreach (string name in new[] { "_whiteTable", "_blackTable" })
        {
            FieldInfo field = decoder.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

            Assert.True(field != null, $"{name} is gone; this test needs updating with it");
            Assert.True(field.IsInitOnly,
                $"{name} is assignable, so something can publish it before it is filled — which is "
                + "the defect this test exists for. Build it in the type initialiser.");
        }
    }

    /// <summary>No lazy-initialisation lock survives.</summary>
    /// <remarks>
    /// The lock is the tell. A <c>readonly</c> table needs none, so one reappearing means someone
    /// has reintroduced a build-on-first-use path — and the readonly check alone would not
    /// necessarily catch a new field added beside it.
    /// </remarks>
    [Fact]
    public void NoLazyInitialisationLockRemains()
    {
        FieldInfo[] locks = DecoderType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(object))
            .ToArray();

        Assert.True(locks.Length == 0,
            "a static lock object is back on the CCITT decoder: "
            + string.Join(", ", locks.Select(f => f.Name)));
    }

    /// <summary>Both tables are fully populated the first time anything looks at them.</summary>
    /// <remarks>
    /// The counts are the spec's, not this decoder's: T.4 Annex A defines 64 terminating codes and
    /// 27 make-up codes per colour, plus 13 extended make-ups shared by both — 104 entries each.
    /// A partially built table would be shorter, which is precisely what the race produced.
    /// </remarks>
    [Fact]
    public void BothTablesAreFullyPopulated()
    {
        Type decoder = DecoderType();

        foreach (string name in new[] { "_whiteTable", "_blackTable" })
        {
            object table = decoder
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

            Assert.NotNull(table);
            Assert.Equal(104, (int)table.GetType().GetProperty("Count").GetValue(table));
        }
    }

    /// <summary>Decoding the same image from many threads gives every thread the same answer.</summary>
    /// <remarks>
    /// <b>Not a proof, and it is worth saying which part is not.</b> The race was in first-time
    /// initialisation, which has already happened by the time this runs, so a green result here
    /// says nothing about the original defect — the structural tests above are what say that. What
    /// this covers is the rest of the decoder: that a decode holds no other shared mutable state,
    /// which is the assumption `MaxRenderThreads` above 1 rests on.
    /// </remarks>
    [Fact]
    public void ConcurrentDecodesOfTheSameImageAgree()
    {
        byte[] encoded = { 0b00110101, 0b00110101, 0b00110101, 0b00110101 };
        var parameters = new PdfDictionary();
        parameters[new PdfName("K")] = new PdfNumber(-1);
        parameters[new PdfName("Columns")] = new PdfNumber(64);
        parameters[new PdfName("Rows")] = new PdfNumber(4);

        var filter = new CcittFaxDecodeFilter();
        byte[] reference = filter.Decode(encoded, parameters);

        var results = new byte[16][];
        Parallel.For(0, results.Length,
            i => results[i] = new CcittFaxDecodeFilter().Decode(encoded, parameters));

        foreach (byte[] result in results) Assert.Equal(reference, result);
    }

    private static Type DecoderType()
    {
        Type type = typeof(CcittFaxDecodeFilter).Assembly
            .GetType("Tetitulz.Pdf.Core.IO.Filters.CcittFaxDecoder");

        Assert.True(type != null, "CcittFaxDecoder has moved; this test needs updating with it");
        return type;
    }
}
