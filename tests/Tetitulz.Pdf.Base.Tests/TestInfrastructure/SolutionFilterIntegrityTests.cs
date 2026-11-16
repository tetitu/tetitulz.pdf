// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every project path in <c>tetitulz.pdf.portable.slnf</c> resolves to a file and names a project
/// the solution actually contains.
/// </summary>
/// <remarks>
/// <para><b>This exists because a path in that file was wrong and every guard said it was fine.</b>
/// Two entries were written with doubled backslashes — <c>src\\\\plugins\\\\…</c>, which JSON
/// decodes to a literal <c>src\\plugins\\…</c> — so MSBuild refused the filter outright with
/// <c>MSB5028: … includes project … that is not in the solution file</c>. That is the Linux CI
/// job's <i>build</i> step, which means the portability leg was not being verified at all.</para>
///
/// <para><b>The CI step that reads this file could not have caught it.</b> It asks whether the
/// filter's raw text <c>Contains</c> each project's <i>file name</i>, which a path with any
/// separator at all satisfies. That is the right question for "did someone forget to add a new
/// project", and the wrong one for "is the path usable" — so the two checks are complementary
/// rather than duplicates, and this one is stated in terms of resolving the path.</para>
///
/// <para><b>The first version of this file did not catch it either, and the reason is worth
/// keeping.</b> It normalised every path before comparing — collapsing repeated separators — which
/// is exactly the defect, so the guard papered over the thing it was written for. Windows compounds
/// it: <c>File.Exists</c> collapses repeated separators too, so even resolving the path succeeded.
/// A guard that normalises its input cannot see a defect in the input's spelling; this one asserts
/// the spelling itself.</para>
///
/// <para><b>The rule is "no repeated separator", established by measurement rather than by
/// reading.</b> A path spelled with single backslashes — the solution file's own convention —
/// builds; the same path with doubled ones is refused. So the check is not "use forward slashes",
/// which would be a style preference, but the one property MSBuild actually rejects.</para>
///
/// <para>Kept here rather than in the workflow because a check that only runs in CI cannot fail on
/// the machine where the mistake is made, and this one costs a file read.</para>
/// </remarks>
public class SolutionFilterIntegrityTests
{
    private const string Filter = "tetitulz.pdf.portable.slnf";
    private const string Solution = "tetitulz.pdf.sln";

    private static string? RepoRoot =>
        TestCorpus.RepoRoot is { } repo && File.Exists(Path.Combine(repo, Filter))
            ? repo
            : null;

    /// <summary>
    /// No listed path contains a repeated separator, which is what MSBuild refuses the whole filter
    /// for.
    /// </summary>
    /// <remarks>
    /// Measured: <c>src\plugins\x.csproj</c> builds and <c>src\\plugins\\x.csproj</c> fails with
    /// <c>MSB5028</c>, so the property to assert is the repetition and not the separator character.
    /// Asserted on the raw string, before any normalisation, because normalising is what made the
    /// first version of this guard blind.
    /// </remarks>
    [RepoSourceFact]
    public void NoProjectPathInThePortableFilter_HasARepeatedSeparator()
    {
        var doubled = FilteredProjects()
            .Where(p => p.Contains("//", StringComparison.Ordinal) ||
                        p.Contains(@"\\", StringComparison.Ordinal))
            .ToList();

        Assert.True(doubled.Count == 0,
            $"{Filter} lists paths with a repeated separator, which MSBuild refuses with MSB5028 " +
            $"— the whole filter fails to build, not just the entry:{Environment.NewLine}" +
            string.Join(Environment.NewLine, doubled.Select(d => "  " + d)));
    }

    /// <summary>Every listed project file exists on disk.</summary>
    /// <remarks>
    /// Catches a stale path — a project renamed or moved without the filter following — which the
    /// separator check cannot see. <b>It does not catch a doubled separator</b>, because Windows
    /// collapses repeated separators when resolving a path; that is what the test above is for.
    /// </remarks>
    [RepoSourceFact]
    public void EveryProjectInThePortableFilter_ResolvesToAFile()
    {
        var missing = new List<string>();
        foreach (string relative in FilteredProjects())
        {
            string full = Path.Combine(RepoRoot!, relative.Replace('\\', Path.DirectorySeparatorChar)
                                                          .Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) missing.Add(relative);
        }

        Assert.True(missing.Count == 0,
            $"{Filter} lists paths that do not resolve to a project file:{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Select(m => "  " + m)));
    }

    /// <summary>
    /// Every listed project is one the solution contains, compared as a path rather than as a name.
    /// </summary>
    /// <remarks>
    /// A filter may name only projects in the solution it filters — MSBuild treats anything else as
    /// an error rather than as a no-op — and the comparison has to be on the path, because that is
    /// what MSBuild matches on and what was wrong.
    /// </remarks>
    [RepoSourceFact]
    public void EveryProjectInThePortableFilter_IsInTheSolution()
    {
        HashSet<string> inSolution = SolutionProjects();

        var strangers = FilteredProjects()
            .Where(p => !inSolution.Contains(Normalise(p)))
            .ToList();

        Assert.True(strangers.Count == 0,
            $"{Filter} lists projects the solution does not contain:{Environment.NewLine}" +
            string.Join(Environment.NewLine, strangers.Select(s => "  " + s)));
    }

    /// <summary>
    /// The filter's declared solution is the repository's, so it filters what it claims to.
    /// </summary>
    [RepoSourceFact]
    public void ThePortableFilterNamesTheRepositorysSolution()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot!, Filter)));
        string? path = doc.RootElement.GetProperty("solution").GetProperty("path").GetString();

        Assert.Equal(Solution, Normalise(path ?? string.Empty));
    }

    // ------------------------------------------------------------------

    private static List<string> FilteredProjects()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot!, Filter)));
        return doc.RootElement.GetProperty("solution").GetProperty("projects")
                  .EnumerateArray()
                  .Select(e => e.GetString() ?? string.Empty)
                  .ToList();
    }

    /// <summary>Project paths declared in the solution file, normalised.</summary>
    private static HashSet<string> SolutionProjects()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot!, Solution));
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Regex.Matches(text, @"^Project\(""\{[^}]+\}""\) = ""[^""]+"", ""([^""]+)""",
                                          RegexOptions.Multiline))
            projects.Add(Normalise(m.Groups[1].Value));

        Assert.True(projects.Count > 0, $"{Solution} yielded no project declarations to compare against.");
        return projects;
    }

    /// <summary>
    /// A path in the one spelling both files can be compared in: forward slashes, no repeats.
    /// </summary>
    /// <remarks>
    /// <b>This normalisation is why the membership test cannot see a doubled separator</b>, and that
    /// is deliberate now that it is stated: membership is a question about <i>which</i> project, and
    /// the answer should not change with the spelling. The spelling has its own test, which reads
    /// the raw string.
    /// </remarks>
    private static string Normalise(string path)
    {
        string p = path.Replace('\\', '/');
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        return p;
    }
}
