// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="SystemFontResolver"/>'s family-name pass — the step between "match the filename" and
/// "take anything that covers the text".
///
/// <para><b>The gap it closes is a wrong answer, not a slow one.</b> Filename matching is the right
/// first question and answers most requests for free, but a font's family is the one property its
/// <i>file</i> need not agree with. When the filename tells nothing, falling straight through to
/// the exhaustive scan would return the first installed font that happens to cover the required
/// code points — the right glyphs in the wrong typeface, with nothing anywhere reporting a
/// substitution had been made.</para>
///
/// <para><b>Host-independent by construction.</b> <c>TETITULZ_PDF_FONTDIR</c> <i>replaces</i> the OS
/// font search path, so these tests run against two fonts this file wrote and never against
/// whatever the machine has installed. That matters more than usual here: the assertion is about
/// which of two candidates wins, and a stray system font that also covered "AB" could satisfy it
/// for the wrong reason.</para>
/// </summary>
[Collection(SystemFontCacheCollection.Name)]
public sealed class SystemFontResolverFamilyNameTests : IDisposable
{
    private readonly string _dir;
    private readonly string _previousEnv;

    public SystemFontResolverFamilyNameTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(FontFileLocator.FontDirEnvVar);
        _dir = Path.Combine(Path.GetTempPath(),
            "tetitulz-familyname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _previousEnv);
        FontFileLocator.ResetCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The claim. Two fonts cover the text equally well; only one of them <i>is</i> the requested
    /// family, and it says so in its <c>name</c> table rather than in its filename. The decoy is
    /// named so it sorts first, so an implementation that takes the first covering file picks it.
    ///
    /// <para>Mutation: delete the family-name pass from <c>SystemFontResolver.Resolve</c> and the
    /// decoy wins — which is the behaviour this closes.</para>
    /// </summary>
    [Fact]
    public void AFamilyItsFilenameDoesNotName_IsFoundBeforeAnyOtherCoveringFont()
    {
        GivenFonts(
            ("aaa-decoy.ttf",       MinimalTrueType.BuildNamed("AB", "Some Other Face")),
            ("zzz-mystery-01.ttf",  MinimalTrueType.BuildNamed("AB", "Mystery Sans")));

        var result = Resolve("Mystery Sans", "AB");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("zzz-mystery-01.ttf", Path.GetFileName(result.SourcePath));
        Assert.Equal("Mystery Sans", result.Program.FamilyName);
    }

    /// <summary>
    /// The control, and it is what stops the test above from passing for the wrong reason: with the
    /// same two files and a family neither of them claims, the resolver still has to return
    /// <i>something</i> that covers the text. A family-name pass that had quietly become the only
    /// pass would fail here.
    /// </summary>
    [Fact]
    public void AFamilyNoInstalledFontClaims_StillFallsThroughToTheCoveringScan()
    {
        GivenFonts(
            ("aaa-decoy.ttf",      MinimalTrueType.BuildNamed("AB", "Some Other Face")),
            ("zzz-mystery-01.ttf", MinimalTrueType.BuildNamed("AB", "Mystery Sans")));

        var result = Resolve("Nothing Claims This", "AB");

        Assert.True(result.Success, result.FailureReason);
        Assert.NotNull(result.Program);
    }

    /// <summary>
    /// A name match is not a licence to return a font that cannot draw the text. The requested
    /// family is present and covers only "AB"; the text needs "Z", which only the other file has.
    /// Coverage still decides.
    /// </summary>
    [Fact]
    public void AFamilyThatMatchesByNameButLacksTheGlyphs_IsNotReturned()
    {
        GivenFonts(
            ("zzz-mystery-01.ttf", MinimalTrueType.BuildNamed("AB", "Mystery Sans")),
            ("aaa-other.ttf",      MinimalTrueType.BuildNamed("Z",  "Some Other Face")));

        var result = Resolve("Mystery Sans", "Z");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("aaa-other.ttf", Path.GetFileName(result.SourcePath));
    }

    /// <summary>
    /// The pass compares names the way <see cref="FontFileLocator"/> compares filenames — case and
    /// separators folded — rather than with a second convention of its own. "MYSTERYSANS" and
    /// "Mystery Sans" are the same family, and a PDF is entirely free to write either.
    /// </summary>
    [Fact]
    public void FamilyComparison_FoldsCaseAndSeparators()
    {
        GivenFonts(
            ("aaa-decoy.ttf",      MinimalTrueType.BuildNamed("AB", "Some Other Face")),
            ("zzz-mystery-01.ttf", MinimalTrueType.BuildNamed("AB", "Mystery Sans")));

        var result = Resolve("MYSTERY-SANS", "AB");

        Assert.Equal("zzz-mystery-01.ttf", Path.GetFileName(result.SourcePath));
    }

    // ----------------------------------------------------------------

    private static FontResolution Resolve(string family, string text) =>
        SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                FamilyHint = family,
                RequiredCodePoints = text.Select(c => (int)c).ToList(),
                AllowSystemFontSubstitution = true,
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
