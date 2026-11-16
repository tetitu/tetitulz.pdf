// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.CMap;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>charpath</c> on a composite font: each code through its predefined CMap to a CID, the CID
/// through the character collection's Unicode map to its character, and the character's outline in
/// the face the text is drawn with — where a code the maps or the face cannot carry keeps its metric box.
/// </summary>
public sealed class CompositeCharPathTests
{
    static CompositeCharPathTests()
    {
        new AdobeCMapPlugin().Initialize();   // idempotent
    }

    private const string Composed =
        "/KozMinPro-Regular-UniJIS-UCS2-H findfont 10 scalefont setfont ";

    /// <summary>One glyph, a box from (100, 0) to (500, 700) in thousandths, for U+4E00 alone.</summary>
    private static readonly byte[] Face = OutlineTrueType.Build(new[]
    {
        OutlineTrueType.Simple(new[]
        {
            new OutlineTrueType.Point(100, 0), new OutlineTrueType.Point(500, 0),
            new OutlineTrueType.Point(500, 700), new OutlineTrueType.Point(100, 700),
        }),
    }, characters: "一");

    private static ContentOp[] PathOps(string ps)
        => ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(Composed + ps),
                new PostScriptConversionOptions { OutlineFontProvider = _ => Face }).Content)
            .Where(o => o.Operator is "m" or "l").ToArray();

    private static double[] At(ContentOp op)
        => op.Operands.Select(t => System.Math.Round(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture), 6)).ToArray();

    [Fact]
    public void ACodeDecodes_ToItsCid_AndTheCollectionsCharacter()
    {
        var codes = Core.Fonts.CidCollectionText.Decode("UniJIS-UCS2-H", "Japan1", new byte[] { 0x4E, 0x00, 0x00, 0x41 });

        Assert.NotNull(codes);
        Assert.Equal(new[] { 2, 2 }, codes!.Select(c => c.Length));
        Assert.Equal(new[] { "一", "A" }, codes.Select(c => c.Text));
        Assert.Null(Core.Fonts.CidCollectionText.Decode("No-Such-CMap", "Japan1", new byte[] { 1 }));
    }

    [Fact]
    public void ACodeTheFaceCarries_IsItsOutline_AndTheNextIsAnEmOn()
    {
        ContentOp[] ops = PathOps("newpath 0 0 moveto <4E004E00> true charpath fill");
        var moves = ops.Where(o => o.Operator == "m").Select(At).ToArray();

        // Each glyph's outline begins at its first point, (100, 0) of the em at size 10 — the second
        // one em along — where a metric box would begin at the descender, below the baseline.
        Assert.Contains(moves, m => m.SequenceEqual(new[] { 1.0, 0.0 }));
        Assert.Contains(moves, m => m.SequenceEqual(new[] { 11.0, 0.0 }));
        Assert.DoesNotContain(moves, m => m[1] < 0);
        Assert.Contains(ops, o => o.Operator == "l" && At(o).SequenceEqual(new[] { 5.0, 7.0 }));
    }

    [Fact]
    public void ACodeTheFaceLacks_KeepsItsMetricBox_BesideTheOutlinedOne()
    {
        // U+4E8C is mapped by the collection, but the face has no glyph for it.
        ContentOp[] ops = PathOps("newpath 0 0 moveto <4E004E8C> true charpath fill");
        var moves = ops.Where(o => o.Operator == "m").Select(At).ToArray();

        Assert.Contains(moves, m => m.SequenceEqual(new[] { 1.0, 0.0 }));                      // the outline
        Assert.Contains(moves, m => m[0] == 10.0 && m[1] < 0);           // the box, one em on, from the descender
    }

    [Fact]
    public void VerticalText_KeepsItsBoxes()
    {
        string vertical = Composed.Replace("UniJIS-UCS2-H", "UniJIS-UCS2-V");
        var ops = ContentStreamAssert.Parse(PostScriptConversion.ConvertFragment(
                Encoding.Latin1.GetBytes(vertical + "newpath 0 0 moveto <4E00> true charpath fill"),
                new PostScriptConversionOptions { OutlineFontProvider = _ => Face }).Content)
            .Where(o => o.Operator == "m").Select(At).ToArray();

        Assert.Contains(ops, m => m[1] < 0);              // a box, from the descender
        Assert.DoesNotContain(ops, m => m.SequenceEqual(new[] { 1.0, 0.0 }));   // and no outline
    }
}
