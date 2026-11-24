// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <c>OS/2</c> <c>fsType</c> bit 8 — "the font may be embedded whole but not subset" — honoured by
/// both subsetters.
/// </summary>
/// <remarks>
/// <para><b>It is honoured by declining, not by a second code path.</b> Every caller of either
/// subsetter already falls back to the whole program when it declines, <i>and</i> writes no
/// <c>ABCDEF+</c> subset tag, because §9.6.4 makes that tag a claim that only some of the glyphs are
/// present. Embedding the whole font is exactly what the licence asks for, so the existing decline
/// path is the correct behaviour and the only question is whether the bit reaches it.</para>
///
/// <para><b>The assertions are on the emitted font dictionary, not on the subsetters.</b> Both are
/// internal and both have a decline path that already existed; what a licence-honouring
/// implementation has to produce is a <i>document</i> whose embedded program is complete and whose
/// <c>/BaseFont</c> does not lie about it. Asserting the subsetter's return value would pass for an
/// implementation that declined and then tagged the font anyway.</para>
/// </remarks>
public class NoSubsettingLicenceTests
{
    /// <summary>Preview &amp; Print (0x0004) plus no-subsetting (0x0100).</summary>
    private const int NoSubsetting = 0x0104;

    // ------------------------------------------------------------------
    //  TrueType (glyf) — Type0FontBuilder's CIDFontType2 branch
    // ------------------------------------------------------------------

    /// <summary>
    /// The claim: the whole program is embedded, and the name does not claim otherwise.
    /// </summary>
    [Fact]
    public void TrueType_NoSubsettingFont_IsEmbeddedWholeAndUntagged()
    {
        byte[] font = MinimalTrueType.BuildWithFsType("ABC", NoSubsetting);
        var (baseFont, length1, hasCidToGidStream) = Embed(font);

        Assert.Equal(font.Length, length1);
        Assert.DoesNotContain("+", baseFont);
        Assert.False(hasCidToGidStream, "a whole program needs no glyph remapping");
    }

    /// <summary>
    /// The control, and the thing that stops the test above passing for an implementation that
    /// simply never subsets: the identical font with an installable licence <i>is</i> subset, is
    /// tagged, and carries the remap.
    /// </summary>
    [Fact]
    public void TrueType_AnInstallableFont_IsStillSubsetAndTagged()
    {
        byte[] font = MinimalTrueType.BuildWithFsType("ABC", 0x0000);
        var (baseFont, length1, hasCidToGidStream) = Embed(font);

        Assert.NotEqual(font.Length, length1);
        Assert.Contains("+", baseFont);
        Assert.True(hasCidToGidStream);
    }

    /// <summary>
    /// Bit 8 alone is what stops subsetting — the licence value in bits 0–3 is a separate question,
    /// and a font that is both editable and no-subsetting must still not be subset.
    /// </summary>
    [Theory]
    [InlineData(0x0100)]  // installable + no subsetting
    [InlineData(0x0108)]  // editable + no subsetting
    public void TrueType_TheNoSubsettingBitActsIndependentlyOfTheLicenceValue(int fsType)
    {
        byte[] font = MinimalTrueType.BuildWithFsType("ABC", fsType);

        Assert.Equal(font.Length, Embed(font).length1);
    }

    /// <summary>
    /// The decline says which of its reasons applied. <c>(null, null)</c> means "embed the whole
    /// program" for both "no <c>glyf</c> table" and "the licence forbids it", and only the second is
    /// something a caller may have to explain to someone.
    /// </summary>
    [Fact]
    public void TrueType_TheDeclineNamesTheLicence()
    {
        TrueTypeSubsetter.Subset(MinimalTrueType.BuildWithFsType("ABC", NoSubsetting), new ushort[] { 1 });

        Assert.Contains("subsetting", TrueTypeSubsetter.LastDeclineReason);
    }

    /// <summary>And it is cleared by a call that succeeds, rather than left for the next reader.</summary>
    [Fact]
    public void TrueType_TheDeclineReasonIsClearedOnSuccess()
    {
        TrueTypeSubsetter.Subset(MinimalTrueType.BuildWithFsType("ABC", NoSubsetting), new ushort[] { 1 });
        TrueTypeSubsetter.Subset(MinimalTrueType.BuildWithFsType("ABC", 0x0000), new ushort[] { 1 });

        Assert.Null(TrueTypeSubsetter.LastDeclineReason);
    }

    // ------------------------------------------------------------------
    //  CFF / OpenType — charstring blanking
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>Blanking a charstring is subsetting, whatever the file length says.</b> The CFF subsetter
    /// overwrites unused charstrings in place, so its output is byte-for-byte as long as its input —
    /// which makes "did it subset?" invisible to a size check and is exactly why this asserts on the
    /// charstrings themselves.
    /// </summary>
    [Fact]
    public void Cff_NoSubsettingFont_KeepsEveryCharstring()
    {
        byte[] font = MinimalOpenTypeCff.Build("ABC", fsType: NoSubsetting);

        var result = Tetitulz.Pdf.Core.Fonts.Cff.CffSubsetter.BlankUnusedGlyphs(font, new[] { 0, 1 });

        Assert.Null(result.Font);
        Assert.Contains("subsetting", result.Reason);
    }

    /// <summary>The control: the same font with an installable licence really does get blanked.</summary>
    [Fact]
    public void Cff_AnInstallableFont_IsStillBlanked()
    {
        byte[] font = MinimalOpenTypeCff.Build("ABC", fsType: 0x0000);

        var result = Tetitulz.Pdf.Core.Fonts.Cff.CffSubsetter.BlankUnusedGlyphs(font, new[] { 0, 1 });

        Assert.NotNull(result.Font);
        var charstrings = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.Contains(charstrings, MinimalOpenTypeCff.IsBlanked);
    }

    /// <summary>
    /// A <b>bare</b> CFF carries no <c>OS/2</c> table, so it makes no statement about embedding and
    /// must not be refused as though it had. The gate lives in the sfnt branch for exactly this
    /// reason.
    /// </summary>
    [Fact]
    public void Cff_ABareProgramWithNoOs2_IsStillSubsettable()
    {
        byte[] bare = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("ABC"));

        var result = Tetitulz.Pdf.Core.Fonts.Cff.CffSubsetter.BlankUnusedGlyphs(bare, new[] { 0, 1 });

        // Asserting the charstrings, not that something came back: a refusal returns null and a
        // successful no-op would return an untouched program, and "non-null" cannot tell those from
        // a real subset. (The build-failing pattern-3 guard caught the weaker first draft of this.)
        Assert.Null(result.Reason);
        var charstrings = MinimalOpenTypeCff.ReadCharstrings(result.Font);
        Assert.Contains(charstrings, MinimalOpenTypeCff.IsBlanked);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Embeds <paramref name="font"/> through <see cref="Type0FontBuilder"/> using one glyph, and
    /// reports the three things that say whether it was subset: the name, the embedded program's
    /// declared length, and whether a <c>/CIDToGIDMap</c> remap was written.
    /// </summary>
    private static (string baseFont, int length1, bool hasCidToGidStream) Embed(byte[] font)
    {
        var program = TrueTypeFontProgram.Parse(font);
        Assert.NotNull(program);

        program!.TryGetGlyph('A', out ushort gid);
        Assert.True(gid > 0, "the fixture must actually map 'A' or nothing is being subset");

        int next = 1;
        var (type0Num, objects) = Type0FontBuilder.BuildObjects(
            ref next, program, new Dictionary<ushort, int> { { gid, 'A' } });

        var byNum = objects.ToDictionary(o => o.objNum, o => o.obj);
        var type0 = (PdfDictionary)byNum[type0Num];
        string baseFont = ((PdfName)type0[PdfName.BaseFont]).Value;

        var descendant = (PdfDictionary)Deref(byNum, ((PdfArray)type0[PdfName.DescendantFonts])[0]);
        bool hasCidToGidStream = descendant[PdfName.CIDToGIDMap] is PdfReference;

        var descriptor = (PdfDictionary)Deref(byNum, descendant[PdfName.FontDescriptor]);
        var fontFile = (PdfStream)Deref(byNum, descriptor[PdfName.FontFile2]);
        int length1 = (int)((PdfNumber)fontFile[PdfName.Length1]).DoubleValue;

        return (baseFont, length1, hasCidToGidStream);
    }

    private static PdfObject Deref(Dictionary<int, PdfObject> byNum, PdfObject obj)
        => obj is PdfReference r ? byNum[r.ReferenceObjectNumber] : obj;
}
