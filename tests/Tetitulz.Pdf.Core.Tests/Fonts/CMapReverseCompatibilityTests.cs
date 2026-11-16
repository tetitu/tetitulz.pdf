// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// An encoding CMap read backwards for CID-to-Unicode: where several characters encode one CID and one
/// is the other's compatibility decomposition (U+2F00, the Kangxi radical, and U+4E00, the ideograph,
/// both CID 1200 in Adobe-Japan1), the CID stands for the canonical character, in either order — the
/// radical comes first in code order, and kept it extracted every such ideograph as a radical.
/// </summary>
public sealed class CMapReverseCompatibilityTests
{
    private static string Range(int unicode, int cid) => $"1 begincidrange\n<{unicode:X4}> <{unicode:X4}> {cid}\nendcidrange\n";

    [Fact]
    public void TheCanonicalCharacter_IsTheCidsText_WhicheverComesFirst()
    {
        var radicalFirst = CMapParser.Parse(Encoding.ASCII.GetBytes(Range(0x2F00, 1200) + Range(0x4E00, 1200)));
        var ideographFirst = CMapParser.Parse(Encoding.ASCII.GetBytes(Range(0x4E00, 1200) + Range(0x2F00, 1200)));

        Assert.Equal("一", radicalFirst[1200]);
        Assert.Equal("一", ideographFirst[1200]);
    }

    [Fact]
    public void TwoCharactersNeitherTheOthersForm_KeepTheFirst()
    {
        var map = CMapParser.Parse(Encoding.ASCII.GetBytes(Range(0x4E00, 7) + Range(0x4E8C, 7)));
        Assert.Equal("一", map[7]);
    }

    [Fact]
    public void AProducersBfchar_IsNeverDisplacedByAReversedEntry()
    {
        string cmap = "1 beginbfchar\n<04B0> <2F00>\nendbfchar\n" + Range(0x4E00, 0x04B0);
        Assert.Equal("⼀", CMapParser.Parse(Encoding.ASCII.GetBytes(cmap))[0x04B0]);
    }
}
