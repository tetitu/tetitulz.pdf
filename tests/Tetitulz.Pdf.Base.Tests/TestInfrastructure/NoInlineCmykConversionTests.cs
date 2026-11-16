// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Fails the build if any file in <c>Tetitulz.Pdf.Base</c> other than <c>PdfColor.cs</c> inlines the
/// DeviceCMYK → sRGB formula <c>r = (1−c)(1−k)</c> instead of calling
/// <c>PdfColor.CmykToRgb</c>.
///
/// <para><b>Why this exists.</b> That one expression had <b>nine</b> implementations across Base
/// under <b>three</b> rounding conventions (GAPS.md §2b). Two of them had already been reconciled by
/// hand once, with a comment explaining the fix — and the comment was still true while seven other
/// copies quietly disagreed. The failure mode is specific: a fix applied to one copy looks complete,
/// and the copies are individually so short that nobody reads them as duplication.</para>
///
/// <para><b>It has no exemption list, by design.</b> CLAUDE.md: "An empty exemption set is an
/// invitation; no exemption set is a rule." That is affordable because of the discriminator below,
/// not because the pattern is rare.</para>
/// </summary>
public sealed class NoInlineCmykConversionTests
{
    /// <summary>The one file that legitimately implements the conversion.</summary>
    private const string DeclaringFile = "PdfColor.cs";

    /// <summary>
    /// <c>(1 − x) * (1 − y)</c>, allowing one nested call in either operand.
    /// </summary>
    private static readonly Regex SubtractivePair = new(
        @"\(\s*1f?\s*-\s*[^()]*(?:\([^()]*\)[^()]*)*\)\s*\*\s*\(\s*1f?\s*-\s*[^()]*(?:\([^()]*\)[^()]*)*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// The discriminator that lets this guard run without exemptions.
    ///
    /// <para><c>(1−a)(1−b)</c> is not unique to colour — Base uses it for Coons-patch surface
    /// interpolation and for cubic Bézier basis functions, both in
    /// <c>ContentStreamInterpreter.cs</c>. What separates a <i>colour</i> conversion is that it
    /// scales the product to a channel, so the same line also mentions 255 or
    /// <c>MaxChannelValueF</c>. The geometry sites never do. Widening the pattern without keeping a
    /// discriminator this sharp would force an exemption list, which is the thing to avoid.</para>
    /// </summary>
    private static readonly Regex ChannelScale = new(
        @"\b255f?\b|MaxChannelValueF", RegexOptions.Compiled);

    /// <summary>
    /// The <c>Tetitulz.Pdf.Base</c> project directory, located by finding its <c>.csproj</c>
    /// anywhere under <c>src/</c>.
    ///
    /// <para>This used to hard-code <c>src/tetitulz.pdf/Tetitulz.Pdf.Base</c>. That is a real
    /// coupling, not a stylistic one: it pins the project's position in the tree, so any
    /// reorganisation makes the guard's floor assertion fail with a message that reads like a
    /// broken test rather than like a moved directory. A guard should be sensitive to the rule it
    /// enforces and to nothing else. The find-by-name pattern is
    /// <see cref="NoRedundantPdfNameAllocationTests"/>'s.</para>
    /// </summary>
    private static string BaseRoot
    {
        get
        {
            string? src = RepoSources.SrcRoot;
            if (src == null) return "";
            string? csproj = Directory
                .EnumerateFiles(src, "Tetitulz.Pdf.Base.csproj", SearchOption.AllDirectories)
                .FirstOrDefault();
            return csproj == null ? "" : Path.GetDirectoryName(csproj)!;
        }
    }

    /// <summary>
    /// A floor: proves the detector fires on a violation, and that the file it exempts is real.
    /// </summary>
    /// <remarks>
    /// This deliberately validates the <i>detector</i> against synthetic input rather than asserting
    /// that <c>PdfColor.cs</c> itself matches the pattern. The first draft did the latter and it was
    /// wrong within the hour: <c>CmykToRgb</c> factors <c>1 − k</c> into a local, so the canonical
    /// implementation does not have two parenthesised subtractions on one line even though every
    /// copy the guard exists to catch did. Keying a guard on the shape of the <i>correct</i> code
    /// couples it to a refactor that ought to be free.
    /// </remarks>
    [RepoSourcesFact]
    public void The_guard_fires_on_a_violation_and_ignores_the_geometry_it_must_not_flag()
    {
        Assert.True(Directory.Exists(BaseRoot),
            $"Could not locate Tetitulz.Pdf.Base under {RepoSources.SrcRoot}.");
        // Located by name rather than by path, for the same reason as BaseRoot: which folder
        // PdfColor.cs sits in is not this guard's business, only that the exempted file exists.
        Assert.True(
            Directory.EnumerateFiles(BaseRoot, DeclaringFile, SearchOption.AllDirectories).Any(),
            $"Could not locate the exempted {DeclaringFile}; the exemption would silently cover nothing.");

        // Every shape the nine real copies were written in.
        foreach (var banned in new[]
        {
            "(byte)((1f - c) * (1f - k) * MaxChannelValueF),",
            "byte rb = (byte)((1f - c)  * (1f - k) * 255f);",
            "r = (int)((1 - cf) * (1 - k) * 255 + 0.5f);",
            "(byte)Math.Round((1 - c) * (1 - k) * 255),",
        })
        {
            Assert.True(SubtractivePair.IsMatch(banned) && ChannelScale.IsMatch(banned),
                $"The guard would not catch this violation: {banned}");
        }

        // The geometry it shares an expression shape with, and must leave alone.
        foreach (var allowed in new[]
        {
            "0 => (1 - t) * (1 - t) * (1 - t),",
            "- ((1 - u) * (1 - v) * P00.x + u * (1 - v) * P03.x",
        })
        {
            Assert.True(SubtractivePair.IsMatch(allowed),
                $"Expected the shape to match so the discriminator is what excludes it: {allowed}");
            Assert.False(ChannelScale.IsMatch(allowed),
                $"The guard would falsely flag geometry: {allowed}");
        }
    }

    [RepoSourcesFact]
    public void No_file_in_Base_inlines_the_cmyk_conversion()
    {
        Assert.True(Directory.Exists(BaseRoot), $"Could not locate Tetitulz.Pdf.Base.");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(BaseRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (RepoSources.IsGenerated(file)) continue;
            if (Path.GetFileName(file) == DeclaringFile) continue;

            var lines = RepoSources.StripComments(File.ReadAllText(file))
                                   .Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (!SubtractivePair.IsMatch(lines[i])) continue;
                if (!ChannelScale.IsMatch(lines[i])) continue;   // geometry, not colour

                violations.Add($"{Path.GetRelativePath(BaseRoot, file)}:{i + 1} — {lines[i].Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            $"{violations.Count} inline DeviceCMYK→sRGB conversion(s). Call PdfColor.CmykToRgb " +
            "instead — it is the single implementation, and copies of it have drifted apart on " +
            "rounding twice already (GAPS.md §2b):" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
