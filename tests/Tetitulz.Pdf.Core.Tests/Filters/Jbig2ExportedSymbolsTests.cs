// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Filters;

/// <summary>
/// A symbol dictionary's export flags (T.88 §6.5.10) — run lengths alternating exclude and include —
/// read through one loop for the arithmetic and the Huffman dictionary alike.
/// </summary>
/// <remarks>
/// A run of zero advances nothing, and both decoders read past their data rather than fail (the MQ
/// decoder pads 0xFF, the bit reader zeros), so a stream decoding zero runs spun the loop for ever.
/// </remarks>
public sealed class Jbig2ExportedSymbolsTests
{
    private static List<Jbig2Decoder.Jbig2Bitmap> Symbols(int count) =>
        Enumerable.Range(1, count).Select(w => new Jbig2Decoder.Jbig2Bitmap(w, 1)).ToList();

    [Fact]
    public void AdversarialEndlessZeroRuns_EndAfterTwo_AndExportEverythingClampedToTheCount()
    {
        var symbols = Symbols(5);
        int asked = 0;

        var exported = Jbig2Decoder.ExportedSymbols(symbols, () => { asked++; return 0; }, oob: -1, sdNumExSyms: 3);

        Assert.Equal(2, asked);
        Assert.Equal(new[] { 1, 2, 3 }, exported.Select(s => s.Width).ToArray());
    }

    [Fact]
    public void AValidRunSequence_ExportsTheIncludedSymbols_ALeadingZeroIncluded()
    {
        // The control: exclude 0, include 2, exclude 1, include 2 — a first zero run is how a
        // dictionary exports its first symbol, and it does not end the sequence.
        var symbols = Symbols(5);
        var runs = new Queue<int>(new[] { 0, 2, 1, 2 });

        var exported = Jbig2Decoder.ExportedSymbols(symbols, () => runs.Dequeue(), oob: -1, sdNumExSyms: 5);

        Assert.Equal(new[] { 1, 2, 4, 5 }, exported.Select(s => s.Width).ToArray());
        Assert.Empty(runs);
    }

    [Fact]
    public void AdversarialRunNearIntMax_IsClampedToTheSymbolsThatRemain()
    {
        // idx + runLen wrapped negative in int, and the loop then marked nothing for ever.
        var symbols = Symbols(4);
        var runs = new Queue<int>(new[] { 1, int.MaxValue });

        var exported = Jbig2Decoder.ExportedSymbols(symbols, () => runs.Count > 0 ? runs.Dequeue() : 0, oob: -1, sdNumExSyms: 4);

        Assert.Equal(new[] { 2, 3, 4 }, exported.Select(s => s.Width).ToArray());
    }
}
