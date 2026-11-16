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
/// Fails the build when a markdown document cites a file that does not exist, or a
/// <c>GAPS.md §N</c> section that has no heading.
///
/// <para><b>Why this exists.</b> A documentation review found a type —
/// <c>AccessibilityIncrementalWriter</c> — cited fourteen times across five documents after it had
/// been deleted, and two of those citations were <i>instructions</i>: <c>CLAUDE.md</c> told a
/// reader not to consolidate two writers into one, preserving a structure that no longer existed.
/// Alongside it: three method names that had been renamed, two section cross-references pointing
/// at nothing, and half a dozen counts that no longer matched the tree.</para>
///
/// <para>This repository's own rule is that prose decays unless something checks it — the same
/// argument that produced the unfalsifiable-assertion guard and the redundant-<c>PdfName</c>
/// guard. <c>GAPS.md</c> even states a pruning rule for itself that nothing enforced, and 18% of
/// the file had become closed work.</para>
///
/// <para><b>Scope, stated honestly.</b> It checks cited <i>paths</i> and <i>section numbers</i>,
/// which are mechanical and have no false positives. It does <b>not</b> verify that every
/// backticked identifier exists: doing that needs a token index over the whole tree and produces a
/// residue of legitimate non-matches — foreign APIs (<c>SKShaper.Shape</c>), spec operators, and
/// names deliberately cited as removed. That check is worth adding, but only with a
/// self-cleaning allow-list for the last category rather than a permanent one.</para>
/// </summary>
/// <summary>
/// A <see cref="FactAttribute"/> for a guard over <c>GAPS.md</c>: it skips, visibly and with the
/// reason, where the repository sources or the file are not on disk. <c>GAPS.md</c> is the
/// developer's register and is not published, so a clone of the public repository has none.
/// </summary>
public sealed class GapsRegisterFactAttribute : FactAttribute
{
    public GapsRegisterFactAttribute()
    {
        if (RepoSources.SrcRoot is not { } src)
            Skip = "Repository sources are not available next to the test assembly; nothing to scan.";
        else if (!File.Exists(Path.Combine(Path.GetDirectoryName(src)!, "GAPS.md")))
            Skip = "GAPS.md is developer-local and not in this checkout; its citations are not checked.";
    }
}

public class DocumentationReferenceTests
{
    /// <summary>
    /// Every markdown file in the repository, excluding build output, agent scratch and vendored
    /// content.
    ///
    /// <para><b>Every exemption is scoped to a folder rather than to a list of files</b>, so none of
    /// them can quietly grow into a way of silencing a real finding. There is deliberately no
    /// exemption for repository documentation: every `.md` under version control that this project
    /// wrote is required to cite paths that exist.</para>
    /// </summary>
    private static IEnumerable<string> MarkdownFiles(string repo)
    {
        // `.claude/` is agent scratch — worktrees, transcripts, settings. A worktree there holds a
        // full COPY of the repo, CLAUDE.md included, so without this the guard walks another
        // agent's in-progress checkout and reports its stale paths as defects in this one.
        string agentScratch = Path.Combine(repo, ".claude") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(repo, "*.md", SearchOption.AllDirectories)
                        .Where(f => !RepoSources.IsGenerated(f))
                        .Where(f => !f.StartsWith(agentScratch, StringComparison.OrdinalIgnoreCase))
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                        // A restored NuGet folder is the same case as node_modules above, and this
                        // exemption was missing until tools/package-consumption/verify.ps1 restored
                        // packages INTO the tree for the first time. AngleSharp's own README cites
                        // paths from AngleSharp's repository -- docs/tutorials/05-Migration.md and
                        // three more -- and the guard dutifully reported four "defects" that are
                        // somebody else's links in somebody else's project.
                        //
                        // A scanner that walks vendored content reports other people's problems as
                        // yours. Excluded by the folder NuGet restores into, per convention, and by
                        // folder rather than by file list so it cannot quietly grow.
                        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}"));
    }

    private static string? RepoRoot =>
        RepoSources.SrcRoot is { } src ? Path.GetDirectoryName(src) : null;

    // A repo-relative path in prose or a link: src/…, tests/…, docs/…, benchmarks/…, .github/…
    //
    // The extension alternation is LONGEST-FIRST on purpose. Regex alternation is ordered, so
    // listing `cs` before `csproj` matches the `.cs` inside `.csproj` and then reports the
    // truncated path as missing — which this guard did on its first run, inventing two failures
    // in a plan file whose paths were perfectly correct.
    private static readonly Regex CitedPath = new(
        @"(?<![A-Za-z0-9._/-])(?<p>(?:src|tests|docs|benchmarks|apps|demo|build|\.github)/[A-Za-z0-9._/-]+\.(?:csproj|slnf|sln|props|yaml|yml|ps1|txt|md|cs))(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    /// <summary>Prose that declares the cited file gone, rather than claiming it is present.</summary>
    private static readonly Regex SaysGone = new(
        @"\b(deleted|removed|renamed|superseded|no longer exists|moved to)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [RepoSourcesFact]
    public void Every_repo_relative_path_cited_in_a_document_exists()
    {
        string repo = RepoRoot!;
        var missing = new List<string>();

        foreach (string file in MarkdownFiles(repo))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in CitedPath.Matches(lines[i]))
                {
                    string cited = m.Groups["p"].Value.TrimEnd('.', ',', ')');
                    string full = Path.Combine(repo, cited.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full) || Directory.Exists(full)) continue;

                    // A path with a brace-expansion or a glob is a shorthand for several real
                    // files ("…/{VerifiedWrite,PdfObjectIndex}.cs"), not a claim about one.
                    if (cited.Contains('{') || cited.Contains('*')) continue;

                    // A line that says the file was deleted, removed or renamed is not claiming
                    // it exists — it is recording that it does not. Keyed on the sentence rather
                    // than on an allow-list, so it cannot be used to silence a real breakage: the
                    // author has to state the disposal in the prose a reader will see.
                    if (SaysGone.IsMatch(lines[i])) continue;

                    missing.Add($"{Rel(repo, file)}:{i + 1} → {cited}");
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"{missing.Count} document(s) cite a file that does not exist. A path that has moved " +
            "is how a reader ends up believing a deleted design is still current:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    // WHAT THIS PATTERN DELIBERATELY CANNOT SEE, so a green run is not read as "no stale
    // citations". It requires the word GAPS before the section mark, because a BARE §12 is
    // ambiguous between a GAPS section and an ISO 32000-1 clause and the pattern cannot
    // discriminate without a decimal. Measured when tests/ was brought into scope: 45 bare §N
    // references (§18 and above, so certainly not ISO clauses) remain across the test tree and 11
    // under src/. Several are known-stale round numbers. Widening to bare §N is a separate
    // decision with its own false-positive question; the number is recorded here as scoped rather
    // than guessed at.
    // "GAPS.md §5c", "GAPS §12" — but never an ISO 32000 clause, which always carries a decimal
    // sub-clause (§8.9.6.4) or an explicit "ISO 32000-1" prefix.
    private static readonly Regex GapsSection = new(
        @"GAPS(?:\.md)?\s+§\s?(?<n>[0-9]+[a-z]?)(?![0-9.])",
        RegexOptions.Compiled);

    private static readonly Regex GapsHeading = new(
        @"^#{2,4}\s+(?<n>[0-9]+[a-z]?)[.\s]", RegexOptions.Compiled);

    [GapsRegisterFact]
    public void Every_GAPS_section_reference_resolves_to_a_heading()
    {
        string repo = RepoRoot!;
        string gaps = Path.Combine(repo, "GAPS.md");
        Assert.True(File.Exists(gaps), "GAPS.md is missing; this guard would check nothing.");

        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(gaps))
        {
            Match h = GapsHeading.Match(line);
            if (h.Success) sections.Add(h.Groups["n"].Value);
        }
        Assert.True(sections.Count > 10,
            $"Only {sections.Count} GAPS.md sections were recognised; the heading pattern is wrong " +
            "and this guard would pass by finding nothing to check.");

        var dangling = new List<string>();
        // Source files cite sections too, and that is where a broken reference is invisible.
        //
        // TESTS ARE SCANNED AS WELL AS src, and they were not: nine stale citations lived in
        // test files precisely because nothing looked there. src and tests are enumerated
        // EXPLICITLY rather than walking *.cs from the repo root -- c6bb810 already had to stop
        // this guard walking agent worktrees under .claude/, which hold a full copy of the
        // checkout and would pass locally while failing on a machine that has one.
        var scanned = MarkdownFiles(repo)
            .Concat(SourceFiles(repo, "src"))
            .Concat(SourceFiles(repo, "tests"));

        foreach (string file in scanned)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in GapsSection.Matches(lines[i]))
                {
                    string n = m.Groups["n"].Value;
                    if (sections.Contains(n)) continue;
                    dangling.Add($"{Rel(repo, file)}:{i + 1} → GAPS.md §{n}");
                }
            }
        }

        Assert.True(dangling.Count == 0,
            $"{dangling.Count} reference(s) point at a GAPS.md section that does not exist. " +
            "Most live in XML doc-comments, which is exactly where a stale cross-reference is " +
            "invisible until someone follows it:" +
            Environment.NewLine + string.Join(Environment.NewLine, dangling));
    }

    // A numbered GAPS.md heading: "## 5. Builder …" or "### 5c. Accessibility …". The number is
    // required to be followed by "." or whitespace so that "#### Group 1 — …" and the many
    // unnumbered prose headings ("### Still open — the real implementation") are not mistaken for
    // section numbers.
    private static readonly Regex GapsNumberedHeading = new(
        @"^(?<hashes>#{2,4})\s+(?<num>[0-9]+)(?<suffix>[a-z]?)[.\s]", RegexOptions.Compiled);

    /// <summary>
    /// <c>GAPS.md</c>'s numbered sections read in order: <c>##</c> numbers ascend, and each
    /// lettered <c>###</c> sub-section sits under the <c>##</c> it is numbered for, in letter
    /// order.
    ///
    /// <para><b>Why an order check and not just an existence check.</b> The sibling guard above
    /// resolves every <c>§N</c> reference to a heading, which is satisfied wherever that heading
    /// happens to sit. Commit <c>70b25eb</c> ("GAPS.md sections read in numeric order again") fixed
    /// the symptom by hand and left nothing to stop it recurring — and it had already recurred when
    /// this was written: <b>§4b preceded §4a</b>. In a register that a reader navigates by section
    /// number, a section filed out of order is a section not found, which is how an open item gets
    /// worked twice or not at all.</para>
    /// </summary>
    [GapsRegisterFact]
    public void GAPS_numbered_sections_are_in_order()
    {
        string repo = RepoRoot!;
        var problems = new List<string>();

        int topCount = 0, subCount = 0;
        int lastTop = 0;
        string lastTopLabel = "(none)";
        int currentParent = 0;
        string lastSuffix = "";

        string[] lines = File.ReadAllLines(Path.Combine(repo, "GAPS.md"));
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = GapsNumberedHeading.Match(lines[i]);
            if (!m.Success) continue;

            int num = int.Parse(m.Groups["num"].Value);
            string suffix = m.Groups["suffix"].Value;
            bool isTopLevel = m.Groups["hashes"].Value.Length == 2;

            if (isTopLevel && suffix.Length == 0)
            {
                topCount++;
                if (num <= lastTop)
                    problems.Add($"GAPS.md:{i + 1} → §{num} follows §{lastTopLabel}; " +
                                 "top-level sections must ascend.");
                lastTop = num;
                lastTopLabel = num.ToString();
                currentParent = num;
                lastSuffix = "";
                continue;
            }

            // A lettered sub-section. Its number names its parent, so a mismatch means it has been
            // filed under the wrong section — the failure that is invisible to a reference check,
            // because §6a resolves to a heading whether or not that heading sits inside §6.
            subCount++;
            if (num != currentParent)
                problems.Add($"GAPS.md:{i + 1} → §{num}{suffix} appears under §{currentParent}.");
            else if (string.CompareOrdinal(suffix, lastSuffix) <= 0)
                problems.Add($"GAPS.md:{i + 1} → §{num}{suffix} follows §{num}{lastSuffix}; " +
                             "sub-sections must ascend within their parent.");
            lastSuffix = suffix;
        }

        // A floor, for the same reason the sibling guards carry one: a pattern that stopped
        // matching would let this pass by finding nothing to order.
        Assert.True(topCount >= 10 && subCount >= 15,
            $"Only {topCount} top-level and {subCount} lettered GAPS.md sections were recognised; " +
            "the heading pattern is wrong and this guard would check nothing.");

        Assert.True(problems.Count == 0,
            $"{problems.Count} GAPS.md section(s) are out of order:" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// A floor for both guards: they must actually be reading something. A wrong root, an empty
    /// enumeration or a pattern that never matches would make each of them pass by checking
    /// nothing at all, which is the failure this repository's test rules are most alert to.
    /// </summary>
    [RepoSourcesFact]
    public void The_guards_are_reading_a_substantial_number_of_documents()
    {
        string repo = RepoRoot!;
        var files = MarkdownFiles(repo).ToList();

        Assert.True(files.Count >= 15, $"Only {files.Count} markdown files found under {repo}.");
        Assert.Contains(files, f => Path.GetFileName(f) == "CLAUDE.md");
        Assert.Contains(files, f => Path.GetFileName(f) == "README.md");

        int citations = files.Sum(f => CitedPath.Matches(File.ReadAllText(f)).Count);
        Assert.True(citations >= 100,
            $"Only {citations} repo-relative path citations found; the pattern is probably wrong.");

        // The .cs half needs its own floor: a mistyped directory name makes the concat
        // enumerate nothing and the section guard then passes by checking no source at all.
        int sources = SourceFiles(repo, "src").Count() + SourceFiles(repo, "tests").Count();
        Assert.True(sources >= 500,
            $"Only {sources} source files found under src/ and tests/; the scan is not reaching them.");
        Assert.NotEmpty(SourceFiles(repo, "tests"));
    }

    /// <summary>Non-generated C# sources under one top-level directory of the repository.</summary>
    private static IEnumerable<string> SourceFiles(string repo, string topLevel)
    {
        string root = Path.Combine(repo, topLevel);
        if (!Directory.Exists(root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !RepoSources.IsGenerated(f));
    }

    private static string Rel(string repo, string file) =>
        file.Substring(repo.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
}
