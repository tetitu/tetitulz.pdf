// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that skips — visibly, with a reason — when the repository
/// sources are not on disk, so source-scanning hygiene tests report <i>Skipped</i> rather than
/// the fake green that <see cref="NoHardcodedPathsTests"/> exists to forbid.
/// </summary>
public sealed class RepoSourcesFactAttribute : FactAttribute
{
    public RepoSourcesFactAttribute()
    {
        if (RepoSources.SrcRoot == null)
            Skip = "Repository sources are not available next to the test assembly; nothing to scan.";
    }
}

/// <summary>Locates the repository's <c>src</c> tree for source-scanning hygiene tests.</summary>
internal static class RepoSources
{
    /// <summary>The repository's <c>src</c> directory, or <c>null</c> when not present.</summary>
    internal static string? SrcRoot =>
        TestCorpus.RepoRoot is { } repo && Directory.Exists(Path.Combine(repo, "src"))
            ? Path.Combine(repo, "src")
            : null;

    internal static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>
    /// Blanks out <c>//</c> and <c>/* */</c> comments while preserving offsets, so prose that
    /// describes a banned pattern is not itself flagged and reported line numbers stay accurate.
    /// </summary>
    internal static string StripComments(string src)
    {
        var buf = src.ToCharArray();
        bool inString = false, inChar = false, inVerbatim = false;

        for (int i = 0; i < buf.Length; i++)
        {
            char c = buf[i];
            char next = i + 1 < buf.Length ? buf[i + 1] : '\0';

            if (inVerbatim)
            {
                if (c == '"' && next == '"') { i++; continue; }
                if (c == '"') inVerbatim = false;
                continue;
            }
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '@' && next == '"') { inVerbatim = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }

            if (c == '/' && next == '/')
            {
                while (i < buf.Length && buf[i] != '\n') { buf[i] = ' '; i++; }
                continue;
            }
            if (c == '/' && next == '*')
            {
                while (i + 1 < buf.Length && !(buf[i] == '*' && buf[i + 1] == '/'))
                {
                    if (buf[i] != '\n') buf[i] = ' ';
                    i++;
                }
                if (i < buf.Length) buf[i] = ' ';
                if (i + 1 < buf.Length) buf[i + 1] = ' ';
                i++;
                continue;
            }
        }

        return new string(buf);
    }
}

/// <summary>
/// Guards the <c>PdfName</c> cache against re-growing the problem it exists to solve.
///
/// <para><c>Tetitulz.Pdf.Core.Objects.PdfName</c> pre-allocates every well-known PDF name as a
/// <c>public static readonly</c> field. Despite that, the tree once carried roughly 1,150 inline
/// <c>new PdfName("...")</c> calls, ~85 distinct names of which already had a static. Each one is a
/// heap allocation on a parsing or rendering hot path, and — more importantly for a reader — it
/// hides the fact that a canonical spelling of that name already exists.</para>
///
/// <para>This fails when a call site allocates a name that is already cached. It deliberately does
/// <b>not</b> object to <c>new PdfName(...)</c> for names with no static (legitimate, and often
/// dynamic), nor to non-literal arguments.</para>
///
/// <para><b>It scans <c>src</c> only, and that is deliberate — not an oversight.</b> Roughly 240
/// call sites under <c>tests</c> allocate cached names, and they should stay that way. A test that
/// builds its fixture with <c>new PdfName("Type")</c> is stating the literal bytes it expects in
/// the PDF, independently of the production constant. If the test used <c>PdfName.Type</c> instead,
/// then a typo in that constant's <i>value</i> would change the fixture and the assertion together
/// and the test would still pass — the exact "green for the wrong reason" failure mode the sibling
/// <see cref="NoHardcodedPathsTests"/> exists to prevent. The allocation cost that motivates this
/// rule in production is irrelevant in a test fixture, so the trade goes the other way.</para>
/// </summary>
public sealed class NoRedundantPdfNameAllocationTests
{
    /// <summary>The one file that legitimately contains every cached-name declaration.</summary>
    private const string DeclaringFile = "PdfName.cs";

    private static readonly Regex Declaration = new(
        @"static\s+readonly\s+PdfName\s+(?<field>\w+)\s*=\s*new\s+PdfName\s*\(\s*""(?<value>[^""\\]*)""\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>new PdfName("X")</c> <b>and</b> its namespace-qualified spelling,
    /// <c>new Tetitulz.Pdf.Core.Objects.PdfName("X")</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The optional qualifier is a fix, not a nicety.</b> This pattern required the bare
    /// type name, so every fully-qualified allocation was invisible to the guard — and a file
    /// that never adds a <c>using</c> for <c>Core.Objects</c> writes them that way throughout.
    /// <c>PdfAConverter</c> had carried
    /// <c>new Tetitulz.Pdf.Core.Objects.PdfName("OutputConditionIdentifier")</c> for its whole
    /// life; the guard only fired when that code was moved into <c>OutputIntentWriter</c> and the
    /// spelling shortened, which is to say it was found by accident rather than by the check that
    /// exists to find it.</para>
    /// <para>The qualifier cannot produce a false positive on a differently-named type: the
    /// optional group ends in a literal dot, so <c>new SomeOtherPdfName("X")</c> matches neither
    /// branch.</para>
    /// </remarks>
    private static readonly Regex Allocation = new(
        @"new\s+(?:[\w.]+\.)?PdfName\s*\(\s*""(?<value>[^""\\]*)""\s*\)",
        RegexOptions.Compiled);

    /// <summary>Maps each cached PDF name string to the static field that already holds it.</summary>
    private static Dictionary<string, string> CachedNames(string srcRoot)
    {
        var declaring = Directory
            .EnumerateFiles(srcRoot, DeclaringFile, SearchOption.AllDirectories)
            .FirstOrDefault(p => !RepoSources.IsGenerated(p));

        Assert.True(declaring != null, $"Could not locate {DeclaringFile} under {srcRoot}.");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Declaration.Matches(RepoSources.StripComments(File.ReadAllText(declaring!))))
            map[m.Groups["value"].Value] = m.Groups["field"].Value;

        return map;
    }

    /// <summary>
    /// A sanity floor. Without it, a change to the declaration style would stop the regex matching
    /// and every other assertion here would pass while checking nothing.
    /// </summary>
    [RepoSourcesFact]
    public void PdfName_cache_is_populated()
    {
        var cached = CachedNames(RepoSources.SrcRoot!);

        Assert.True(
            cached.Count > 200,
            $"Expected PdfName to cache more than 200 names, found {cached.Count}. The declaration " +
            "style has probably changed and this guard is no longer reading it.");
    }

    [RepoSourcesFact]
    public void No_source_file_allocates_an_already_cached_PdfName()
    {
        var srcRoot = RepoSources.SrcRoot!;
        var cached = CachedNames(srcRoot);
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (RepoSources.IsGenerated(file)) continue;
            if (Path.GetFileName(file) == DeclaringFile) continue;

            var text = RepoSources.StripComments(File.ReadAllText(file));

            foreach (Match m in Allocation.Matches(text))
            {
                if (!cached.TryGetValue(m.Groups["value"].Value, out var field)) continue;

                int line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
                violations.Add(
                    $"{Path.GetRelativePath(srcRoot, file)}:{line} — " +
                    $"new PdfName(\"{m.Groups["value"].Value}\") should be PdfName.{field}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} redundant PdfName allocation(s); use the cached static instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations.Take(60)) +
            (violations.Count > 60 ? $"{Environment.NewLine}… and {violations.Count - 60} more" : ""));
    }
}
