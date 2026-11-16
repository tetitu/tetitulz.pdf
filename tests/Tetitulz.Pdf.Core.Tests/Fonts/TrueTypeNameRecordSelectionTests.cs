// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Which <c>name</c>-table record supplies <see cref="TrueTypeFontProgram.FamilyName"/> and
/// <c>PostScriptName</c>.
///
/// <para><b>The defect.</b> <c>ParseName</c> parsed the encoding and language IDs out of every
/// record and discarded them (<c>_ = encodingId; _ = languageId;</c>), so which record won was
/// decided by table order alone — the <i>first</i> match for the family name, because the
/// assignment was guarded by <c>IsNullOrEmpty</c>, and the <i>last</i> for the PostScript name,
/// because that one overwrote unconditionally. A <c>name</c> table carries one record per
/// language and is conventionally sorted by platform, so <b>Macintosh records come first</b>. A
/// font whose Macintosh records are localised therefore reported a localised family name — and
/// since platform 1 is decoded as ASCII, a CJK localised name came back as mojibake.</para>
///
/// <para><b>Why it matters beyond cosmetics.</b> <c>FamilyName</c> is what font substitution
/// matches on — <c>FontCache</c>, <c>FontFileLocator</c> and <c>IsFamilyMatch</c> all key off it —
/// so a localised or mangled value silently resolves to the wrong face, or to none, with no
/// diagnostic anywhere.</para>
///
/// <para><b>The fixture has to offer a choice.</b> The default builder emits one Windows/en-US
/// record per name ID, which makes "picked the English name" and "picked the only name present"
/// the same observation — so these use a four-record table with the Macintosh pair written first.
/// </para>
/// </summary>
public class TrueTypeNameRecordSelectionTests
{
    private const string Chars = "AB";

    /// <summary>
    /// The Windows English family name wins over a Macintosh localised one that precedes it.
    /// </summary>
    [Fact]
    public void TheWindowsEnglishFamilyName_WinsOverAnEarlierLocalisedRecord()
    {
        byte[] font = MinimalTrueType.BuildWithLocalisedNameTable(
            Chars, macFamilyName: "LocalisedFamily", windowsFamilyName: "EnglishFamily");

        var parsed = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(parsed);
        Assert.Equal("EnglishFamily", parsed!.FamilyName);
    }

    /// <summary>
    /// The same for the PostScript name, which failed the opposite way round — it took the last
    /// record rather than the first, so it happened to be right whenever the Windows records came
    /// last and wrong as soon as they did not.
    /// </summary>
    [Fact]
    public void TheWindowsEnglishPostScriptName_WinsOverAnEarlierLocalisedRecord()
    {
        byte[] font = MinimalTrueType.BuildWithLocalisedNameTable(
            Chars, macFamilyName: "LocalisedFamily", windowsFamilyName: "EnglishFamily");

        var parsed = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(parsed);
        Assert.Equal("EnglishFamily-PS", parsed!.PostScriptName);
    }

    /// <summary>
    /// The localised string must not merely lose — it must be absent. Asserting only that the
    /// English name won would still pass if both were concatenated or if the loser leaked into the
    /// other field.
    /// </summary>
    [Fact]
    public void TheLocalisedName_DoesNotAppearInEitherField()
    {
        byte[] font = MinimalTrueType.BuildWithLocalisedNameTable(
            Chars, macFamilyName: "LocalisedFamily", windowsFamilyName: "EnglishFamily");

        var parsed = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(parsed);
        Assert.DoesNotContain("Localised", parsed!.FamilyName ?? string.Empty);
        Assert.DoesNotContain("Localised", parsed.PostScriptName ?? string.Empty);
    }

    /// <summary>
    /// The preference is for <b>English</b>, not for the Windows platform as such: a Macintosh
    /// <i>English</i> record is a legitimate source when nothing better exists. This is the case
    /// that stops the fix from being "always take platform 3", which would report no family name
    /// at all for a Macintosh-only font.
    /// </summary>
    [Fact]
    public void AMacintoshEnglishRecord_IsStillUsedWhenItIsTheEnglishOne()
    {
        // Both records now claim English; the Windows one still outranks, but the Macintosh string
        // must remain readable rather than being rejected outright.
        byte[] font = MinimalTrueType.BuildWithLocalisedNameTable(
            Chars, macFamilyName: "MacFamily", windowsFamilyName: "WinFamily", macLanguageId: 0);

        var parsed = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(parsed);
        Assert.Equal("WinFamily", parsed!.FamilyName);
    }

    /// <summary>
    /// The fixture-validity control: the ordinary single-record builder is unaffected, so the
    /// change is about choosing between records rather than about how one is read.
    /// </summary>
    [Fact]
    public void ASingleWindowsEnglishRecord_IsReadUnchanged()
    {
        byte[] font = MinimalTrueType.BuildNamed(Chars, "PlainFamily");

        var parsed = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(parsed);
        Assert.Equal("PlainFamily", parsed!.FamilyName);
    }
}
