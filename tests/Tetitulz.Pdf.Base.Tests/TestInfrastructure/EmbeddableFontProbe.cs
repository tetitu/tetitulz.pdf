// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Finds a font <b>file</b> on this host whose <c>cmap</c> covers a sample string, for tests that
/// need real glyph outlines to embed (form-field appearances for Hebrew, say).
///
/// <para>This is not <see cref="SystemFontProbe"/>: that one asks Skia whether a <i>family</i>
/// resolves, which is a question about rendering. Here the test needs bytes it can hand to
/// <see cref="TrueTypeFontProgram.Parse"/>, and coverage of specific code points — a font that
/// resolves by name but has no Hebrew block is useless to it.</para>
///
/// <para>Missing coverage produces a <b>Skipped</b> test, never a weakened assertion: a host with
/// no Hebrew font cannot observe the behaviour at all (CLAUDE.md, banned pattern 3).</para>
///
/// <para><b>It scans through <see cref="SystemFontResolver"/> rather than walking the font
/// directories itself, and it did the latter until 2026-09-03 — making it the fifth copy of the
/// coverage search</b> that the resolver's own doc comment was written to replace. Test
/// infrastructure is exactly where such a copy hides: no production code path reaches it, so the
/// duplication audits that found the other four never listed it. Its options spell out the two
/// things it wants that the resolver's defaults do not give: <c>glyf</c> outlines only, and no
/// budget small enough to miss a font a real caller would have found.</para>
/// </summary>
internal static class EmbeddableFontProbe
{
    /// <summary>Bounds the scan; a font server can expose thousands of files.</summary>
    private const int MaxFilesScanned = 400;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, FontResolution> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Path of a font covering every code point of <paramref name="sample"/>, or <c>null</c>.
    /// </summary>
    internal static string FindPathCovering(string sample) => Resolve(sample)?.SourcePath;

    /// <summary>Bytes of a font covering <paramref name="sample"/>; throws if the probe said none exists.</summary>
    internal static byte[] BytesCovering(string sample)
        => Resolve(sample)?.FontData
           ?? throw new InvalidOperationException(
               "No font on this host covers the sample; the test should have been skipped.");

    private static FontResolution Resolve(string sample)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(sample, out var cached)) return cached;

            var resolution = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    RequiredCodePoints = FontResolutionRequest.CodePointsOf(sample),
                    AllowSystemFontSubstitution = true,

                    // The callers hand the bytes to TrueTypeSubsetter and read `glyf` outlines, so
                    // a CFF/OpenType face covering the sample is not usable to them.
                    AllowCffOutlines = false,

                    MaxScannedFiles = MaxFilesScanned,
                },
                fontProvider: null);

            // A failed resolution is cached too — the scan is the expensive part, and a host with
            // no Hebrew font would otherwise pay it once per skipped test.
            var result = resolution.Success ? resolution : null;
            Cache[sample] = result;
            return result;
        }
    }
}

/// <summary>
/// <see cref="FactAttribute"/> that skips unless some installed font covers the given sample text.
/// </summary>
public sealed class EmbeddableFontFactAttribute : FactAttribute
{
    /// <param name="sample">Text whose every character the font must have a glyph for.</param>
    public EmbeddableFontFactAttribute(string sample)
    {
        if (EmbeddableFontProbe.FindPathCovering(sample) == null)
            Skip = $"requires an installed TrueType font covering '{sample}', and none was found";
    }
}
