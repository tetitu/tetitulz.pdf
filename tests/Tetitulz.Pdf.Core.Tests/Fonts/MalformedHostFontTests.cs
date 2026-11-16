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
/// A file on the host that is named like a font and is not one must not be resolved <i>as</i> one.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The three system-search passes each ended with the same five lines
/// — read, parse, licence gate, coverage, succeed — and merging them onto one <c>TryAccept</c> asked
/// which fixtures reach each step. The parse check was reached by none: removing it left the whole
/// Core suite green. Nothing in the tree had ever put a malformed <c>.ttf</c> on the host.</para>
///
/// <para><b>What removing it actually costs is the search, not the answer.</b>
/// <c>FontResolution.Success</c> is <c>Program != null</c>, so a resolution built from an unparsed
/// file still reports failure — but <c>TryAccept</c> returns it <i>non-null</i>, which is the
/// caller's signal to stop looking. One unreadable file in the font directory then ends the search,
/// and a perfectly good font later in the walk is never reached. That is why the decoy test below
/// is the one that catches it and the single-file test cannot.</para>
///
/// <para>Host-independent by construction, like the licence-gate suite beside it:
/// <c>TETITULZ_PDF_FONTDIR</c> <b>replaces</b> the OS search path, so these run only against files
/// this test wrote. A stray installed font that happened to cover the text would otherwise satisfy
/// the second assertion for entirely the wrong reason.</para>
/// </remarks>
[Collection(SystemFontCacheCollection.Name)]
public sealed class MalformedHostFontTests : IDisposable
{
    private readonly string _dir;
    private readonly string _previousEnv;

    public MalformedHostFontTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(FontFileLocator.FontDirEnvVar);
        _dir = Path.Combine(Path.GetTempPath(), "tetitulz-malformed-font-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FontFileLocator.FontDirEnvVar, _previousEnv);
        FontFileLocator.ResetCache();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The only "font" on the host is a file of rubbish with a <c>.ttf</c> name: the search fails
    /// and hands back no program, rather than throwing out of the resolver.
    /// </summary>
    /// <remarks>
    /// <b>This one does not discriminate the parse check</b> — see the type's remarks. It states the
    /// outcome for a host with nothing usable on it, which is worth pinning on its own, and the
    /// decoy test below is what makes the check itself falsifiable.
    /// </remarks>
    [Fact]
    public void AMalformedFontFileIsNotResolved()
    {
        GivenFonts(("broken.ttf", Rubbish()));

        var result = Resolve("AB");

        Assert.False(result.Success);
        Assert.Null(result.Program);
    }

    /// <summary>
    /// A malformed file sorting <b>before</b> a usable one does not end the search — the same claim
    /// the licence gate's decoy test makes, for the other reason a candidate can be rejected.
    /// </summary>
    [Fact]
    public void AMalformedFontFile_DoesNotEndTheSearch()
    {
        GivenFonts(
            ("aaa-broken.ttf", Rubbish()),
            ("zzz-usable.ttf", MinimalTrueType.Build("AB")));

        var result = Resolve("AB");

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("zzz-usable.ttf", Path.GetFileName(result.SourcePath));
    }

    /// <summary>
    /// The acceptance control: the usable font on its own resolves, so the two tests above are about
    /// the malformed file and not about a fixture that never worked.
    /// </summary>
    [Fact]
    public void TheUsableFontOnItsOwn_IsResolved()
    {
        GivenFonts(("zzz-usable.ttf", MinimalTrueType.Build("AB")));

        var result = Resolve("AB");

        Assert.True(result.Success, result.FailureReason);
        Assert.NotNull(result.Program);
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// Bytes that are not an sfnt by any reading: the leading four are not a recognised version, so
    /// neither the cheap probe nor the full parse can make anything of them.
    /// </summary>
    private static byte[] Rubbish()
    {
        var bytes = new byte[512];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static FontResolution Resolve(string text) =>
        SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
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
