// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="ToUnicodeCMapWriter"/>, the one <c>/ToUnicode</c> writer the composite-font path and
/// the OCR text layer now share.
/// </summary>
public class ToUnicodeCMapWriterTests
{
    /// <summary>
    /// Read back by <see cref="CMapParser"/> — an independent implementation of the format — across
    /// the 100-entry section boundary and a code point outside the Basic Multilingual Plane, with a
    /// code that has no text left out.
    /// </summary>
    [Fact]
    public void TheParserReadsBackWhatTheWriterMapped_AcrossSectionsAndPlanes()
    {
        var map = new Dictionary<ushort, int>();
        for (ushort code = 1; code <= 150; code++) map[code] = 'A' + code % 26;
        map[200] = 0x1F600;   // written as its surrogate pair
        map[201] = 0;         // no text

        byte[] cmap = ToUnicodeCMapWriter.Build(map);
        Dictionary<int, string> parsed = CMapParser.Parse(cmap);

        Assert.Equal(151, parsed.Count);
        Assert.All(Enumerable.Range(1, 150),
            code => Assert.Equal(((char)('A' + code % 26)).ToString(), parsed[code]));
        Assert.Equal(char.ConvertFromUtf32(0x1F600), parsed[200]);
        Assert.False(parsed.ContainsKey(201));

        string text = Encoding.ASCII.GetString(cmap);
        Assert.Contains("\n100 beginbfchar\n", text, StringComparison.Ordinal);
        Assert.Contains("\n51 beginbfchar\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exact program, pinned. Both copies this replaced wrote these bytes, so a change on the
    /// shared side — which every relative assertion would miss, because both consumers move with
    /// it — shows here.
    /// </summary>
    [Fact]
    public void TheProgramIsTheOneBothCopiesWrote()
    {
        byte[] cmap = ToUnicodeCMapWriter.Build(new Dictionary<ushort, int> { [0x0041] = 0x05D0 });

        Assert.Equal(
            "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
            "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 beginbfchar\n<0041> <05D0>\nendbfchar\n" +
            "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n",
            Encoding.ASCII.GetString(cmap));
    }
}
