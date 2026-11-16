// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="FontEmbeddingLicense"/> — the <c>OS/2</c> <c>fsType</c> field, which nothing in this
/// library read until 2026-09-02.
/// </summary>
/// <remarks>
/// <para>Embedding a font redistributes it, and <c>fsType</c> is where a foundry records what it
/// permits. <c>PdfFontEmbedder</c> parsed the field and discarded it on the next line, under a
/// comment naming it "the embedding-permissions field" — so a font whose licence forbids embedding
/// was embedded like any other.</para>
///
/// <para><b>Bits 0–3 are one mutually-exclusive value, not four flags</b>, and that is the part a
/// naive implementation gets wrong. It is asserted here directly rather than through a resolver,
/// because a resolver test can only reach the values its fixtures happen to use.</para>
/// </remarks>
public class FontEmbeddingLicenseTests
{
    /// <summary>0x0000 — installable. The state most fonts are in.</summary>
    [Fact]
    public void Installable_PermitsEverything()
    {
        var licence = FontEmbeddingLicense.FromFsType(0x0000);

        Assert.True(licence.IsDeclared);
        Assert.True(licence.PermitsOutlineEmbedding);
        Assert.False(licence.RestrictedLicense);
        Assert.False(licence.SubsettingProhibited);
        Assert.Null(licence.Describe());
    }

    /// <summary>0x0002 — the one value that forbids embedding outright.</summary>
    [Fact]
    public void RestrictedLicense_ForbidsEmbedding()
    {
        var licence = FontEmbeddingLicense.FromFsType(0x0002);

        Assert.True(licence.RestrictedLicense);
        Assert.False(licence.PermitsOutlineEmbedding);
        Assert.Contains("Restricted License", licence.Describe());
    }

    /// <summary>
    /// <b>The mutually-exclusive rule, and the case a bit test gets wrong.</b> 0x0006 has bit 1
    /// <i>and</i> bit 2 set; the least restrictive wins, so it is Preview &amp; Print — embeddable.
    /// An implementation testing <c>(fsType &amp; 2) != 0</c> refuses a font the foundry permits.
    /// </summary>
    [Theory]
    [InlineData(0x0004)]  // Preview & Print
    [InlineData(0x0008)]  // Editable
    [InlineData(0x0006)]  // Restricted bit set alongside Preview & Print — Preview & Print wins
    [InlineData(0x000A)]  // …alongside Editable
    [InlineData(0x000E)]  // …all three
    public void ALessRestrictiveBitAlongsideBitOne_IsNotRestricted(int fsType)
    {
        var licence = FontEmbeddingLicense.FromFsType(fsType);

        Assert.False(licence.RestrictedLicense);
        Assert.True(licence.PermitsOutlineEmbedding);
    }

    /// <summary>
    /// The high bits are independent flags and are read as such — 0x0102 is restricted <i>and</i>
    /// no-subsetting, and the licence value in bits 0–3 is unaffected by them.
    /// </summary>
    [Fact]
    public void TheHighBitsAreFlagsAndDoNotDisturbTheLicenceValue()
    {
        var noSubset = FontEmbeddingLicense.FromFsType(0x0104);
        Assert.True(noSubset.SubsettingProhibited);
        Assert.True(noSubset.PermitsOutlineEmbedding);
        Assert.Contains("subsetting", noSubset.Describe());

        var bitmapOnly = FontEmbeddingLicense.FromFsType(0x0204);
        Assert.True(bitmapOnly.BitmapEmbeddingOnly);
        Assert.False(bitmapOnly.PermitsOutlineEmbedding);
        Assert.Contains("bitmap", bitmapOnly.Describe());
    }

    /// <summary>
    /// A font that carried no <c>OS/2</c> table said nothing — which is not the same as permitting,
    /// but is the only honest answer, and every predicate must read false rather than true.
    /// </summary>
    [Fact]
    public void Undeclared_SaysNothingAndPermits()
    {
        var licence = FontEmbeddingLicense.Undeclared;

        Assert.False(licence.IsDeclared);
        Assert.False(licence.RestrictedLicense);
        Assert.False(licence.SubsettingProhibited);
        Assert.True(licence.PermitsOutlineEmbedding);
        Assert.Null(licence.Describe());
    }

    // ------------------------------------------------------------------
    //  Reading it off a real font program
    // ------------------------------------------------------------------

    /// <summary>
    /// The field is read from the font, at <c>OS/2</c> offset 8 — not assumed. A wrong offset here
    /// would read <c>usWidthClass</c> (5) or <c>ySubscriptXSize</c ​> and quietly answer about the
    /// wrong bytes.
    /// </summary>
    [Theory]
    [InlineData(0x0000, false)]
    [InlineData(0x0002, true)]
    [InlineData(0x0004, false)]
    public void Read_TakesTheValueTheFontActuallyCarries(int fsType, bool restricted)
    {
        var licence = FontEmbeddingLicense.Read(MinimalTrueType.BuildWithFsType("AB", fsType));

        Assert.True(licence.IsDeclared);
        Assert.Equal(fsType, licence.FsType);
        Assert.Equal(restricted, licence.RestrictedLicense);
    }

    /// <summary>The parsed program carries it too, so a caller holding one need not re-read bytes.</summary>
    [Fact]
    public void TrueTypeFontProgram_ExposesTheLicence()
    {
        var program = TrueTypeFontProgram.Parse(MinimalTrueType.BuildWithFsType("AB", 0x0002));

        Assert.NotNull(program);
        Assert.True(program!.EmbeddingLicense.RestrictedLicense);
    }

    /// <summary>Bytes this library cannot parse as an sfnt declare nothing, rather than throwing.</summary>
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Read_OfMalformedBytes_IsUndeclared(byte[] data)
    {
        Assert.False(FontEmbeddingLicense.Read(data).IsDeclared);
    }

    /// <summary>
    /// A table directory can declare an <c>OS/2</c> that runs past the end of the file, and the
    /// field reader is handed that offset by three callers who each located the table themselves.
    /// A font that does not contain the field has not made a statement about embedding, so the
    /// answer is <see cref="FontEmbeddingLicense.Undeclared"/> — never an exception out of a font
    /// parse, and never two bytes of whatever follows.
    ///
    /// <para><b>Written because the guard survived a mutation.</b> Loosening the bound left all nine
    /// no-subsetting tests green: every fixture in the suite is a well-formed font, so nothing
    /// reached the check.</para>
    /// </summary>
    [Theory]
    [InlineData(0, 4)]     // the whole buffer is shorter than the field
    [InlineData(20, 24)]   // the table starts inside the buffer and the field runs past its end
    [InlineData(-1, 64)]   // a negative offset, which a corrupt directory can produce
    public void FromOs2Table_OutOfRange_IsUndeclaredRatherThanThrowing(int offset, int bufferLength)
    {
        var licence = FontEmbeddingLicense.FromOs2Table(new byte[bufferLength], offset);

        Assert.False(licence.IsDeclared);
        Assert.True(licence.PermitsOutlineEmbedding);
    }
}

/// <summary>
/// The licence gate in <see cref="SystemFontResolver"/>: a font the <b>library</b> picked off the
/// host, rather than one the caller handed over.
/// </summary>
/// <remarks>
/// <para><b>The two cases differ on purpose and the tests say which is which.</b> A caller who
/// supplies a font program has made a licensing decision this code cannot see and may hold a licence
/// the bits know nothing about; overruling them would be this library overruling its own user. A
/// font found on the machine is the opposite case — nobody decided anything.</para>
///
/// <para>Host-independent by construction: <c>TETITULZ_PDF_FONTDIR</c> <i>replaces</i> the OS search
/// path, so these run against fonts this file wrote. That matters especially here, since a stray
/// installed font that also covered the text would satisfy a "found something" assertion for
/// entirely the wrong reason.</para>
/// </remarks>
[Collection(SystemFontCacheCollection.Name)]
public sealed class SystemFontLicenceGateTests : IDisposable
{
    private readonly string _dir;
    private readonly string _previousEnv;

    public SystemFontLicenceGateTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(FontFileLocator.FontDirEnvVar);
        _dir = Path.Combine(Path.GetTempPath(), "tetitulz-fstype-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _previousEnv);
        FontFileLocator.ResetCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The claim. The only font on the host forbids embedding, so the search finds nothing rather
    /// than redistributing it.
    /// </summary>
    [Fact]
    public void ARestrictedHostFont_IsNotSubstituted()
    {
        GivenFonts(("restricted.ttf", MinimalTrueType.BuildWithFsType("AB", 0x0002)));

        var result = Resolve("AB");

        Assert.False(result.Success);
    }

    /// <summary>
    /// The control that stops the test above passing because the fixture simply did not work: the
    /// identical font with an installable licence <i>is</i> substituted.
    /// </summary>
    [Fact]
    public void AnInstallableHostFont_IsSubstituted()
    {
        GivenFonts(("installable.ttf", MinimalTrueType.BuildWithFsType("AB", 0x0000)));

        var result = Resolve("AB");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("installable.ttf", Path.GetFileName(result.SourcePath));
    }

    /// <summary>
    /// The search continues past a restricted candidate rather than stopping at it — the decoy sorts
    /// first, so an implementation that refuses the whole search on the first restricted file would
    /// never reach the usable one.
    /// </summary>
    [Fact]
    public void ARestrictedCandidate_DoesNotEndTheSearch()
    {
        GivenFonts(
            ("aaa-restricted.ttf",  MinimalTrueType.BuildWithFsType("AB", 0x0002)),
            ("zzz-installable.ttf", MinimalTrueType.BuildWithFsType("AB", 0x0000)));

        var result = Resolve("AB");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("zzz-installable.ttf", Path.GetFileName(result.SourcePath));
    }

    /// <summary>
    /// The opt-out, for a deployment that owns the fonts it installed.
    /// </summary>
    [Fact]
    public void AllowRestrictedSystemFonts_TakesTheRestrictedFontAnyway()
    {
        GivenFonts(("restricted.ttf", MinimalTrueType.BuildWithFsType("AB", 0x0002)));

        var result = Resolve("AB", allowRestricted: true);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(result.EmbeddingLicense.RestrictedLicense);
    }

    /// <summary>
    /// <b>A skip must be explicable.</b> Without this the failure reads "no font on this host covers
    /// AB" while a font that covers AB sits on disk — a true sentence pointing at the wrong problem,
    /// which sends the caller looking for a font they already have.
    /// </summary>
    [Fact]
    public void TheFailureReasonNamesWhatTheLicenceGateRejected()
    {
        GivenFonts(("restricted.ttf", MinimalTrueType.BuildWithFsType("AB", 0x0002)));

        var result = Resolve("AB");

        Assert.False(result.Success);
        Assert.Contains("licence-restricted", result.FailureReason);
        Assert.Contains("restricted.ttf", result.FailureReason);
        Assert.Contains("AllowRestrictedSystemFonts", result.FailureReason);
    }

    /// <summary>
    /// <c>fsType</c> is a 16-bit field: bits above it are not part of the value, so 0x10002 is
    /// Restricted License and reports itself as 0x0002.
    /// </summary>
    [Fact]
    public void FromFsType_KeepsOnlyTheSixteenBitField()
    {
        var licence = FontEmbeddingLicense.FromFsType(0x10002);
        Assert.Equal(0x0002, licence.FsType);
        Assert.True(licence.RestrictedLicense);
    }

    /// <summary>
    /// The field ends at offset 10 of the table: a table holding exactly ten bytes is read, nine is
    /// a truncated table and says nothing.
    /// </summary>
    [Fact]
    public void FromOs2Table_ReadsATableEndingAtTheField_AndNotOneByteShort()
    {
        var data = new byte[4 + 10];
        data[4 + 8] = 0x00; data[4 + 9] = 0x02;               // fsType = Restricted License
        var exact = FontEmbeddingLicense.FromOs2Table(data, 4);
        Assert.True(exact.IsDeclared);
        Assert.True(exact.RestrictedLicense);

        Assert.False(FontEmbeddingLicense.FromOs2Table(data[..13], 4).IsDeclared);
    }

    /// <summary>
    /// The gate is for the <b>system search</b>. A font the caller supplies is their licensing
    /// decision, so a restricted program from their provider is used — and its licence is surfaced
    /// on the result rather than acted on.
    /// </summary>
    [Fact]
    public void ARestrictedFontFromTheCallersProvider_IsUsedAndReported()
    {
        GivenFonts();  // nothing on the host at all
        byte[] supplied = MinimalTrueType.BuildWithFsType("AB", 0x0002);

        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest { RequiredCodePoints = new[] { 'A', 'B' }.Select(c => (int)c).ToList() },
            _ => supplied);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(result.FromSystem);
        Assert.True(result.EmbeddingLicense.RestrictedLicense);
    }

    // ----------------------------------------------------------------

    private static FontResolution Resolve(string text, bool allowRestricted = false) =>
        SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = text.Select(c => (int)c).ToList(),
                AllowSystemFontSubstitution = true,
                AllowRestrictedSystemFonts = allowRestricted,
            },
            fontProvider: null);

    private void GivenFonts(params (string name, byte[] bytes)[] fonts)
    {
        foreach (var (name, bytes) in fonts)
            File.WriteAllBytes(Path.Combine(_dir, name), bytes);

        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _dir);
        FontFileLocator.ResetCache();
    }
}
