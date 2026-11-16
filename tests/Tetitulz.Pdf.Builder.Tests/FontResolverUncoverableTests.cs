// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A code point no installed font has is remembered after the first failed host scan, so a later run
/// whose code points include it is declined without re-walking every font file (Builder-1:1 / 2:4).
/// </summary>
/// <remarks>
/// <para>Without this, a document that uses an uncoverable glyph (an emoji, a rare CJK character) in
/// many different words paid a full host scan per distinct code-point set, because a failed
/// resolution was remembered only under its exact set. The short-circuit's answer is the same
/// <c>null</c> the scan would give, so it is asserted through the liveness counter
/// <see cref="BuilderFontResolver.UncoverableShortCircuits"/>, with a control that a coverable set is
/// never short-circuited.</para>
/// <para>Host-independent: the one candidate is a synthetic Latin-only font named through
/// <c>HostFontFilesForTest</c>, so the scan sees exactly it and nothing the runner has installed. No
/// <c>FontProvider</c> is set, which is the only case the short-circuit is sound in — a provider
/// could cover anything.</para>
/// </remarks>
public sealed class FontResolverUncoverableTests : IDisposable
{
    private const int HebrewAlef = 0x05D0;   // nothing a Latin-only font has
    private readonly List<string> _written = new List<string>();

    public void Dispose()
    {
        foreach (string path in _written)
            try { File.Delete(path); } catch { /* a leftover temp file is not a test failure */ }
    }

    /// <summary>A synthetic font on disk covering exactly <paramref name="chars"/>.</summary>
    private string HostFont(string chars)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "tetitulz-uncoverable-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, MinimalTrueType.Build(chars));
        _written.Add(path);
        return path;
    }

    private BuilderFontResolver LatinOnlyHost() =>
        new BuilderFontResolver(new PdfExportOptions { AllowSystemFontSubstitution = true })
        {
            HostFontFilesForTest = new[] { HostFont("ab") },
        };

    [Fact]
    public void ARepeatedUncoverablePoint_IsDeclinedWithoutRescanning()
    {
        BuilderFontResolver resolver = LatinOnlyHost();

        // First set: a full scan finds no font with the Hebrew letter and fails, learning it is
        // uncoverable. The 'a' is coverable — the point is that ONE uncoverable member fails the set.
        Assert.Null(resolver.Resolve(null, false, false, new[] { (int)'a', HebrewAlef }));
        Assert.Equal(0, resolver.UncoverableShortCircuits);   // the first time really scanned

        // A different set that also contains the Hebrew letter: declined from the cache, no scan.
        Assert.Null(resolver.Resolve(null, false, false, new[] { (int)'b', HebrewAlef }));
        Assert.Equal(1, resolver.UncoverableShortCircuits);
    }

    /// <summary>
    /// The control: a coverable set resolves and is never short-circuited, so the cache declines only
    /// what no font can draw — it is not a blanket "second lookup returns null".
    /// </summary>
    [Fact]
    public void ACoverableSet_IsResolved_AndNeverShortCircuited()
    {
        BuilderFontResolver resolver = LatinOnlyHost();

        Assert.NotNull(resolver.Resolve(null, false, false, new[] { (int)'a' }));
        Assert.NotNull(resolver.Resolve(null, false, false, new[] { (int)'b' }));
        Assert.Equal(0, resolver.UncoverableShortCircuits);
    }

    /// <summary>
    /// With a <c>FontProvider</c> present the short-circuit must not fire — the provider is an opaque
    /// source that may cover a point the host does not, so a learned "uncoverable" would be unsound.
    /// Here the provider supplies the Hebrew letter the host lacks, and both resolutions succeed.
    /// </summary>
    [Fact]
    public void WithAProvider_TheUncoverableCacheDoesNotFire()
    {
        // One font covering every point of both sets ('a', 'b' and the Hebrew letter), since a
        // resolution needs ONE font for the whole run. The host lacks the Hebrew letter; the point
        // is that the provider covering it keeps the uncoverable cache from ever marking it.
        byte[] providerFont = MinimalTrueType.Build("abא");
        var resolver = new BuilderFontResolver(new PdfExportOptions
        {
            AllowSystemFontSubstitution = true,
            FontProvider = _ => providerFont,
        })
        { HostFontFilesForTest = new[] { HostFont("ab") } };

        Assert.NotNull(resolver.Resolve(null, false, false, new[] { (int)'a', HebrewAlef }));
        Assert.NotNull(resolver.Resolve(null, false, false, new[] { (int)'b', HebrewAlef }));
        Assert.Equal(0, resolver.UncoverableShortCircuits);
    }
}
