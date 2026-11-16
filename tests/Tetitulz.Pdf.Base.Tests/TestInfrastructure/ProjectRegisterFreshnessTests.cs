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
/// Keeps the <b>enumerable</b> claims in <c>CLAUDE.md</c> honest: the list of plugins and the
/// number of packable projects.
///
/// <para><b>Why this exists.</b> CLAUDE.md states, twice and emphatically, that "a register row is
/// a hypothesis; the code is the authority", and that a hand-maintained number "drifts the moment
/// anyone adds a test". Both rows guarded here had in fact drifted: a 17th plugin
/// (<c>Plugins.HtmlBrowser</c>) shipped while the file still said "the 16 plugins under
/// <c>src/plugins/</c>" and listed sixteen by name, and the package count still said 25. Nothing
/// noticed, because <b>nothing enforces prose</b> — which is the repo's own phrasing, and the
/// reason the fix is a guard rather than another correction.</para>
///
/// <para><b>Scope, deliberately narrow.</b> This checks only claims that are a list of directory
/// names or a count of projects — facts with exactly one right answer that can be read off the
/// tree. It does not attempt to validate prose, architecture rationale, or anything requiring
/// judgement; those stay review-enforced, as CLAUDE.md says they must.</para>
///
/// <para>The line-number references elsewhere in CLAUDE.md are deliberately <i>not</i> guarded:
/// two of them were stale as well, and the fix there was to tell the reader to find CI steps by
/// name instead. Pinning line numbers with a test would preserve the wrong habit.</para>
/// </summary>
public class ProjectRegisterFreshnessTests
{
    private static string? RepoRoot =>
        TestCorpus.RepoRoot is { } repo && Directory.Exists(Path.Combine(repo, "src"))
            ? repo
            : null;

    private static string GuideText() => File.ReadAllText(Path.Combine(RepoRoot!, "CLAUDE.md"));

    /// <summary>Directory names under <c>src/plugins/</c>, as plugin short names ("CMap", …).</summary>
    private static List<string> PluginShortNames()
    {
        const string Prefix = "Tetitulz.Pdf.Plugins.";
        return Directory.EnumerateDirectories(Path.Combine(RepoRoot!, "src", "plugins"))
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n => n!.Substring(Prefix.Length))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> PackableProjects()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot!, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/obj/") &&
                        !p.Replace('\\', '/').Contains("/bin/"))
            // src/Directory.Build.props sets IsPackable=true for the whole tree; a project opts out
            // explicitly. Reading the opt-out is what makes this a claim about packages rather than
            // about project files.
            .Where(p => !File.ReadAllText(p).Contains("<IsPackable>false</IsPackable>"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Every plugin directory must be named in CLAUDE.md's plugin list. A plugin that ships without
    /// being listed is invisible to the next reader deciding where code belongs.
    /// </summary>
    [RepoSourceFact]
    public void EveryPluginDirectory_IsNamedInTheGuide()
    {
        string guide = GuideText();

        // The sentence that enumerates them, so a passing mention elsewhere cannot satisfy this.
        var listed = Regex.Match(guide, @"plugins under `src/plugins/` \(([^)]*)\)", RegexOptions.Singleline);
        Assert.True(listed.Success,
            "CLAUDE.md no longer contains the 'plugins under `src/plugins/` (…)' enumeration this " +
            "guard reads. Restore it or update this test to match its new shape.");

        string names = listed.Groups[1].Value;
        var missing = PluginShortNames()
            .Where(p => !Regex.IsMatch(names, @"\b" + Regex.Escape(p) + @"\b"))
            .ToList();

        Assert.True(missing.Count == 0,
            "CLAUDE.md's plugin list does not name: " + string.Join(", ", missing) +
            ". Add them to the enumeration under 'Project Layout'.");
    }

    /// <summary>
    /// And the count in front of that list must match how many there are — the half that actually
    /// went stale, since a reader skimming for "how many plugins" reads the number, not the list.
    /// </summary>
    [RepoSourceFact]
    public void ThePluginCountInTheGuide_MatchesTheTree()
    {
        var stated = Regex.Match(GuideText(), @"the (\d+) plugins under `src/plugins/`");
        Assert.True(stated.Success, "CLAUDE.md no longer states a plugin count in the expected form.");

        Assert.Equal(PluginShortNames().Count, int.Parse(stated.Groups[1].Value));
    }

    /// <summary>
    /// The package count in the metrics table must match the number of projects that actually pack.
    /// </summary>
    [RepoSourceFact]
    public void ThePackageCountInTheGuide_MatchesTheTree()
    {
        var stated = Regex.Match(GuideText(), @"\*\*(\d+)\*\* library `\.nupkg`s");
        Assert.True(stated.Success, "CLAUDE.md no longer states a package count in the expected form.");

        Assert.Equal(PackableProjects().Count, int.Parse(stated.Groups[1].Value));
    }

    /// <summary>
    /// The guard's own fixture check: the tree really does have plugins and packable projects to
    /// count. Without this, a broken <see cref="RepoRoot"/> would make every assertion above
    /// compare zero against zero and pass while checking nothing.
    /// </summary>
    [RepoSourceFact]
    public void TheGuardCanSeeTheTree()
    {
        Assert.True(PluginShortNames().Count > 10,
            "found almost no plugin directories — this guard is not reading the repository");
        Assert.True(PackableProjects().Count > 10,
            "found almost no packable projects — this guard is not reading the repository");
    }
}
