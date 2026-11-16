// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// What a consumer sees on nuget.org for each of the 27 packages: a description that is about
/// <i>that</i> package, and a README that is about <i>that</i> package.
/// </summary>
/// <remarks>
/// <para><b>Both were inherited, and inheriting them is indistinguishable from writing them.</b>
/// <c>src/Directory.Build.props</c> sets one <c>Description</c> for the whole tree and packed the
/// repository <c>README.md</c> unconditionally — so every package carried the same blurb, and the
/// seven projects that had written their own README shipped the repository one anyway. That was
/// found by unzipping a <c>.nupkg</c>, not by reading the props file, which is why this guard reads
/// the tree rather than trusting either.</para>
///
/// <para><b><see cref="KnownWithoutReadme"/> may only shrink.</b> An entry that has since gained a
/// README fails <see cref="No_stale_entry_on_the_burn_down"/>, so the list cannot quietly become a
/// description of the tree instead of a backlog. <b>Do not add to it</b> — a new packable project
/// ships with a README or it does not ship.</para>
/// </remarks>
public class PackageMetadataCoverageTests
{
    /// <summary>
    /// The generic description in <c>src/Directory.Build.props</c>. A project that still shows this
    /// has not described itself.
    /// </summary>
    private const string InheritedDescriptionMarker =
        "a pure-managed .NET PDF library (no native PDF engine)";

    /// <summary>
    /// Packable projects with no README of their own. <b>This list may only shrink.</b>
    /// </summary>
    private static readonly string[] KnownWithoutReadme =
    {
        // EMPTY, and that is the finished state. All 27 packages carry their own README and their
        // own <Description>; the list stays so the guard keeps working - a NEW packable project
        // that ships without one fails the build naming itself, and the burn-down cannot be
        // re-populated without the stale/ghost checks noticing.
    };

    [RepoSourceFact]
    public void Every_packable_project_describes_itself()
    {
        var generic = PackableProjects()
            .Where(p => !HasOwnDescription(p))
            .Select(ProjectName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(generic.Count == 0,
            "These projects have no <Description> of their own, so nuget.org shows them the "
          + "tree-wide one from src/Directory.Build.props and every package reads alike:\n  "
          + string.Join("\n  ", generic));
    }

    [RepoSourceFact]
    public void Every_packable_project_has_its_own_readme()
    {
        var missing = PackableProjects()
            .Where(p => !File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "README.md")))
            .Select(ProjectName)
            .Where(n => !KnownWithoutReadme.Contains(n, StringComparer.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "These packable projects have no README.md beside them, so their package page shows the "
          + "repository README instead:\n  " + string.Join("\n  ", missing)
          + "\n\nWrite one rather than adding the name to KnownWithoutReadme — that list may only "
          + "shrink.");
    }

    /// <summary>
    /// A name on the burn-down that now has a README must come off it, or the list stops being a
    /// backlog and starts being a second, stale description of the tree.
    /// </summary>
    [RepoSourceFact]
    public void No_stale_entry_on_the_burn_down()
    {
        var stale = KnownWithoutReadme
            .Where(n => PackableProjects().Any(
                p => ProjectName(p) == n
                  && File.Exists(Path.Combine(Path.GetDirectoryName(p)!, "README.md"))))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These now have a README and must be deleted from KnownWithoutReadme:\n  "
          + string.Join("\n  ", stale));
    }

    /// <summary>
    /// A name on the burn-down that matches no project excludes nothing — the same ghost check
    /// <c>FacadePasswordCoverageTests</c> carries.
    /// </summary>
    [RepoSourceFact]
    public void No_ghost_entry_on_the_burn_down()
    {
        var known = PackableProjects().Select(ProjectName).ToHashSet(StringComparer.Ordinal);
        var ghosts = KnownWithoutReadme.Where(n => !known.Contains(n))
                                       .OrderBy(n => n, StringComparer.Ordinal)
                                       .ToList();

        Assert.True(ghosts.Count == 0,
            "These are on KnownWithoutReadme but match no packable project:\n  "
          + string.Join("\n  ", ghosts));
    }

    /// <summary>
    /// The floor. Every test above passes trivially if the scan finds no projects — banned pattern 3
    /// wearing a directory walk — so pin that it really sees the tree.
    /// </summary>
    [RepoSourceFact]
    public void The_scan_is_reading_the_real_source_tree()
    {
        var projects = PackableProjects().ToList();

        Assert.True(projects.Count >= 25,
            $"only {projects.Count} packable projects found under src/");
        Assert.Contains("Tetitulz.Pdf.Core", projects.Select(ProjectName));
        Assert.Contains("Tetitulz.Pdf.Plugins.Ocr", projects.Select(ProjectName));

        // ...and that "has its own description" really discriminates: Core writes one.
        Assert.True(HasOwnDescription(projects.Single(p => ProjectName(p) == "Tetitulz.Pdf.Core")),
            "the description check cannot tell an authored description from the inherited one");
    }

    // ---------------------------------------------------------------- helpers

    private static string? RepoRoot =>
        TestCorpus.RepoRoot is { } repo && Directory.Exists(Path.Combine(repo, "src"))
            ? repo
            : null;

    private static IEnumerable<string> PackableProjects() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot!, "src"), "*.csproj", SearchOption.AllDirectories);

    private static string ProjectName(string projectPath) =>
        Path.GetFileNameWithoutExtension(projectPath);

    private static bool HasOwnDescription(string projectPath)
    {
        string text = File.ReadAllText(projectPath);
        int open = text.IndexOf("<Description>", StringComparison.Ordinal);
        if (open < 0) return false;

        int close = text.IndexOf("</Description>", open, StringComparison.Ordinal);
        if (close < 0) return false;

        string value = text.Substring(open + "<Description>".Length, close - open - "<Description>".Length);
        return value.IndexOf(InheritedDescriptionMarker, StringComparison.Ordinal) < 0;
    }
}
