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
/// Every shipping package must have a checked-in public-API baseline, so a breaking API change
/// cannot reach a consumer unnoticed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as a separate guard from the per-package
/// <see cref="Tetitulz.Pdf.Base.Tests.PublicApiSurfaceTests"/>.</b> Those compare one assembly each
/// against its baseline, through a hand-written <c>[Fact]</c> naming the assembly by a type.
/// Nothing enumerated the packages and demanded
/// a baseline, so <b>20 of the 25 shipping packages had none</b> — every plugin, all three
/// converters and the WinForms viewer could change or remove a public member and fail nothing. A
/// guard that has to be remembered for each new project is a guard that will be forgotten, and this
/// one was: <c>PublicApiSnapshot.Compare</c> even reports a missing baseline, but only if someone
/// calls it for that assembly.
/// </para>
/// <para>
/// <b>It checks for the baseline FILE, not the rendered surface.</b> Comparing content needs the
/// assembly loaded, which needs a project reference — and making <c>Base.Tests</c> reference sixteen
/// plugins to satisfy a guard would invert the dependency direction the whole ring model exists to
/// protect. The content comparison belongs in each package's own test project, beside the code it
/// covers. This guard's job is the one thing no per-project test can do: notice a package that has
/// no such test at all.
/// </para>
/// <para>
/// <b>The unguarded list is a burn-down, and it is self-cleaning.</b> An entry that has since gained
/// a baseline fails <see cref="The_unguarded_list_has_no_stale_entries"/>, so the list can only
/// shrink — the same shape as the CI step that checks every shipping library has a test project, and
/// as <c>NoUnfalsifiableAssertionsTests</c>' <c>KnownOffenders</c> before it reached zero and was
/// deleted. <b>Do not add to it.</b> A new package ships with a baseline or it does not ship.
/// </para>
/// </remarks>
public class ApiBaselineCoverageTests
{
    /// <summary>
    /// Packages that ship without an API baseline today. **This list may only shrink.**
    /// </summary>
    /// <remarks>
    /// Captured 2026-08-13, when the coverage gap was found. Each entry is a package whose public
    /// surface can currently change without any test noticing.
    /// </remarks>
    private static readonly string[] KnownUnguarded =
    {
    };

    [RepoSourcesFact]
    public void No_new_package_ships_without_an_api_baseline()
    {
        var unguarded = PackagesWithoutBaseline().ToList();
        var unexpected = unguarded.Where(p => !KnownUnguarded.Contains(p, StringComparer.Ordinal))
                                  .OrderBy(p => p, StringComparer.Ordinal)
                                  .ToList();

        Assert.True(unexpected.Count == 0,
            "These shipping packages have no tests/ApiSnapshots/<name>.txt, so a breaking change to " +
            "their public API would fail nothing:\n  " + string.Join("\n  ", unexpected) +
            "\n\nAdd a baseline (render it with PublicApiSnapshot.Render from that package's own test " +
            "project) rather than adding the name to KnownUnguarded — that list may only shrink.");
    }

    /// <summary>
    /// A name on the burn-down list that has since gained a baseline must be removed from it, or the
    /// list stops describing the tree and starts excusing it.
    /// </summary>
    [RepoSourcesFact]
    public void The_unguarded_list_has_no_stale_entries()
    {
        var stillUnguarded = PackagesWithoutBaseline().ToHashSet(StringComparer.Ordinal);
        var stale = KnownUnguarded.Where(p => !stillUnguarded.Contains(p))
                                  .OrderBy(p => p, StringComparer.Ordinal)
                                  .ToList();

        Assert.True(stale.Count == 0,
            "These now have an API baseline and must be deleted from KnownUnguarded:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>
    /// A baseline <b>file</b> is not a guard. Every shipping package must also have a test that
    /// <i>compares</i> its assembly against that file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-08-22, reporting five packages as unguarded</b> — <c>Core</c>, <c>Base</c>,
    /// <c>Signing</c>, <c>Encryption</c> and <c>Plugins.HtmlBrowser</c>, described at the time as
    /// "a checked-in baseline that nothing read".
    /// </para>
    /// <para>
    /// <b>That description was wrong for four of the five, and the correction is the more useful
    /// lesson (checked 2026-08-28).</b> A grouped <c>PublicApiSurfaceTests</c> had sat in
    /// <c>Base.Tests/TestInfrastructure/</c> since 2026-08-10 comparing all four core-ring
    /// assemblies against these very files. So <c>Base</c> was never unguarded at all — this
    /// scan searches a package's own test directory recursively, and that copy lived inside it, so
    /// the probe returned true for Base before the "fix" was written. <c>Core</c>, <c>Signing</c>
    /// and <c>Encryption</c> were genuinely flagged by <i>this</i> guard's rule, which is narrower
    /// than the prose was: not "nothing compares them" but "nothing in their own test project
    /// does". That rule is still worth enforcing — a comparison owned by a different project
    /// vanishes silently if that project is trimmed — but only <c>Plugins.HtmlBrowser</c> matched
    /// the claim as written.
    /// </para>
    /// <para>
    /// <b>The evidence was read backwards, which is the part worth remembering.</b> The original
    /// note offered "three of the five matched their assemblies exactly on the first run" as proof
    /// that a baseline can be accurate <i>and</i> unguarded. They matched because they had been
    /// guarded for twelve days. A guard doing its job looks identical to luck if you do not check
    /// whether something upstream was already holding the line — and the check was one
    /// <c>git log</c> on a file the new test sat beside.
    /// </para>
    /// <para>
    /// The real finding survives all of that: a baseline nothing compares against is byte-identical
    /// to one that passes, so the gap leaves no artefact and no stale marker. That is why the
    /// comparison is asserted per package here rather than trusted to a grouped test somebody may
    /// move. The grouped copy was deleted on 2026-08-28, once every one of its four assemblies had
    /// its own pin, since by then it only made a single-member API change fail twice.
    /// </para>
    /// <para>
    /// It is a <b>source scan</b>, for the reason the class remarks give: comparing content needs
    /// the assembly loaded, and making <c>Base.Tests</c> reference sixteen plugins would invert the
    /// ring model. Looking for the call in the package's own test sources needs neither.
    /// </para>
    /// <para>
    /// <b>There is no burn-down list</b>, deliberately. The gap was closed in the commit that added
    /// this test, so there is nothing to exempt and nowhere to add an exemption — the shape
    /// <c>NoUnfalsifiableAssertionsTests</c> reached when its <c>KnownOffenders</c> was deleted
    /// rather than emptied.
    /// </para>
    /// </remarks>
    [RepoSourcesFact]
    public void Every_package_baseline_is_actually_compared_against()
    {
        var unguarded = PackagesWithoutComparison()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(unguarded.Count == 0,
            "These shipping packages have a tests/ApiSnapshots baseline that NOTHING compares " +
            "against, so a breaking change to their public API would fail nothing:\n  " +
            string.Join("\n  ", unguarded) +
            "\n\nAdd a PublicApiSurfaceTests to that package's own test project calling " +
            "PublicApiSnapshot.Compare. There is no exemption list here on purpose.");
    }

    /// <summary>
    /// A floor for the guard itself: if the project scan found nothing, both tests above would pass
    /// while checking nothing at all — banned pattern 3 wearing a directory walk.
    /// </summary>
    [RepoSourcesFact]
    public void The_guard_is_reading_a_real_package_set()
    {
        var packages = PackageNames().ToList();

        Assert.True(packages.Count >= 20,
            $"only {packages.Count} packable projects found under src/; the scan is not working");
        Assert.Contains("Tetitulz.Pdf.Core", packages);
        Assert.Contains("Tetitulz.Pdf.Base", packages);

        // The packages that DO have a baseline must be seen to have one, or the file probe is broken.
        var withBaseline = packages.Except(PackagesWithoutBaseline(), StringComparer.Ordinal).ToList();
        Assert.Contains("Tetitulz.Pdf.Core", withBaseline);
        Assert.Contains("Tetitulz.Pdf.Accessibility", withBaseline);

        // …and the comparison probe must be able to see a call it is looking for, or
        // Every_package_baseline_is_actually_compared_against passes by finding nothing to check.
        Assert.True(ComparesItsBaseline("Tetitulz.Pdf.Accessibility"),
            "the source probe cannot find PublicApiSnapshot.Compare in a project known to call it");
        Assert.False(ComparesItsBaseline("Tetitulz.Pdf.NoSuchPackage"),
            "the source probe reports a comparison for a project that does not exist");
    }

    private static IEnumerable<string> PackagesWithoutBaseline()
    {
        string snapshots = Path.Combine(TestCorpus.RepoRoot!, "tests", "ApiSnapshots");
        foreach (string name in PackageNames())
            if (!File.Exists(Path.Combine(snapshots, name + ".txt")))
                yield return name;
    }

    private static IEnumerable<string> PackagesWithoutComparison()
    {
        foreach (string name in PackageNames())
            if (!ComparesItsBaseline(name))
                yield return name;
    }

    /// <summary>
    /// True when <paramref name="packageName"/>'s own test project calls
    /// <c>PublicApiSnapshot.Compare</c>. A missing test project counts as "does not compare",
    /// which is the honest answer — a separate CI step already requires the project to exist.
    /// </summary>
    private static bool ComparesItsBaseline(string packageName)
    {
        string testDir = Path.Combine(TestCorpus.RepoRoot!, "tests", packageName + ".Tests");
        if (!Directory.Exists(testDir)) return false;

        foreach (string file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories))
        {
            // bin/ and obj/ hold generated and copied sources; a hit there says nothing about
            // what this project's checked-in tests do.
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                continue;

            if (File.ReadAllText(file).Contains("PublicApiSnapshot.Compare", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every project under <c>src/</c>. <c>src/Directory.Build.props</c> sets
    /// <c>IsPackable=true</c> for the whole tree, so this is the shipping set; a project that opts
    /// out with its own <c>&lt;IsPackable&gt;false&lt;/IsPackable&gt;</c> is excluded.
    /// </summary>
    private static IEnumerable<string> PackageNames()
    {
        string src = Path.Combine(TestCorpus.RepoRoot!, "src");
        if (!Directory.Exists(src)) yield break;

        foreach (string csproj in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(csproj);
            if (text.IndexOf("<IsPackable>false</IsPackable>", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            yield return Path.GetFileNameWithoutExtension(csproj);
        }
    }
}
