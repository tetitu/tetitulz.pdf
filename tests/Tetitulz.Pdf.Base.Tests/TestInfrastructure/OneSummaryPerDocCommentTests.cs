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
/// Every documentation comment in <c>src/</c> holds one <c>&lt;summary&gt;</c>, and every partial
/// type carries its documentation comment on one part.
/// </summary>
/// <remarks>
/// <para><b>Two summaries in one comment are a member written in under another's comment.</b> The
/// new member's own summary goes in below the old block, so it carries both, and the member the old
/// block described — usually private, where nothing asks for a comment — is left with none. The
/// compiler says nothing: duplicate tags are well-formed XML. The shapes it takes: a class's
/// description on the class after it, a property's ceiling on the next property, one member's
/// reasoning on an unrelated member, and older summaries of a member left above their rewrite.</para>
/// <para>A comment may put its <c>&lt;param&gt;</c> or <c>&lt;remarks&gt;</c> before its summary;
/// that is one member's comment, and is not counted.</para>
/// <para><b>A partial type documented on several parts is the same defect at a distance.</b> The
/// compiler merges every part's comment into the type's one XML doc entry, so the type publishes
/// each summary, and copies on the parts go stale one at a time with nothing pointing at the
/// contradiction. The type's summary stands on one part; a part's note about its own file is an
/// ordinary comment.</para>
/// </remarks>
public class OneSummaryPerDocCommentTests
{
    /// <summary>The documentation comments in <paramref name="lines"/> that hold more than one summary, by the line each starts on.</summary>
    internal static List<int> CommentsWithSeveralSummaries(string[] lines, out int comments)
    {
        var offenders = new List<int>();
        comments = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal)) continue;
            int start = i, summaries = 0;
            for (; i < lines.Length && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal); i++)
                if (lines[i].Contains("<summary>", StringComparison.Ordinal)) summaries++;
            comments++;
            if (summaries > 1) offenders.Add(start + 1);
        }
        return offenders;
    }

    /// <summary>The detector itself: two summaries in one run are found; a summary after a member's parameters and remarks is not.</summary>
    [Fact]
    public void TheDetector_FindsTwoSummaries_AndNotASummaryAfterItsParameters()
    {
        string[] twice =
        {
            "    /// <summary>The member that lost its comment.</summary>",
            "    /// <remarks>Why it does what it does.</remarks>",
            "    /// <summary>The member written in below it.</summary>",
            "    private static void Written() { }",
        };
        Assert.Equal(new[] { 1 }, CommentsWithSeveralSummaries(twice, out int one));
        Assert.Equal(1, one);

        string[] paramsFirst =
        {
            "    /// <param name=\"x\">An argument.</param>",
            "    /// <remarks>Why.</remarks>",
            "    /// <summary>One member, its summary last.</summary>",
            "    private static void Member(int x) { }",
            "",
            "    /// <summary>The next member.</summary>",
            "    private static void Next() { }",
        };
        Assert.Empty(CommentsWithSeveralSummaries(paramsFirst, out int two));
        Assert.Equal(2, two);
    }

    [RepoSourceFact]
    public void EveryDocumentationCommentHoldsOneSummary()
    {
        string src = Path.Combine(TestCorpus.RepoRoot!, "src");
        var offenders = new List<string>();
        int comments = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceScanner.IsGenerated(file)) continue;
            List<int> found = CommentsWithSeveralSummaries(File.ReadAllLines(file), out int inFile);
            comments += inFile;
            string relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            offenders.AddRange(found.Select(line => relative + ":" + line));
        }

        Assert.True(comments > 15_000,"only " + comments + " documentation comments were read; the scan is not reaching the tree");
        Assert.True(offenders.Count == 0,
            "A documentation comment holds more than one <summary> — a member written in under another member's comment, " +
            "or an older summary left above its rewrite. Give the first block back to the member it describes, or delete " +
            "the stale one:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The partial types (namespace-qualified) that carry a documentation comment on more than one
    /// part, each with the files of its documented parts; <paramref name="types"/> counts the
    /// partial types read.
    /// </summary>
    /// <remarks>
    /// A part is documented when the line above its declaration, attributes passed over, is a
    /// <c>///</c> line. The namespace is the file's <c>namespace</c> declaration, block or
    /// file-scoped.
    /// </remarks>
    internal static Dictionary<string, List<string>> PartialTypesDocumentedTwice(
        IEnumerable<(string File, string[] Lines)> files, out int types)
    {
        var documented = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, lines) in files)
        {
            string ns = "";
            string previous = "";
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("namespace ", StringComparison.Ordinal))
                    ns = trimmed.Substring("namespace ".Length).TrimEnd(';', '{', ' ');
                var match = System.Text.RegularExpressions.Regex.Match(
                    line, @"\bpartial\s+(?:class|struct|record|interface)\s+(\w+)",
                    System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                if (match.Success && !trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    string key = ns + "." + match.Groups[1].Value;
                    seen.Add(key);
                    if (previous.StartsWith("///", StringComparison.Ordinal))
                    {
                        if (!documented.TryGetValue(key, out var parts)) documented[key] = parts = new List<string>();
                        parts.Add(file);
                    }
                }
                if (!trimmed.StartsWith("[", StringComparison.Ordinal)) previous = trimmed;
            }
        }
        types = seen.Count;
        return documented.Where(kv => kv.Value.Count > 1)
                         .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>The detector itself: a type documented on two parts is found; one documented part, its other part carrying an ordinary comment, is not.</summary>
    [Fact]
    public void TheDetector_FindsAPartialTypeDocumentedOnTwoParts_AndNotOneOnOnePart()
    {
        string[] main =
        {
            "namespace A.B",
            "{",
            "    /// <summary>The type.</summary>",
            "    [Obsolete]",
            "    internal sealed partial class Mapper",
            "    {",
            "    }",
            "}",
        };
        string[] documentedPart =
        {
            "namespace A.B;",
            "/// <summary>What this file does.</summary>",
            "internal sealed partial class Mapper { }",
        };
        string[] commentedPart =
        {
            "namespace A.B;",
            "// What this file does.",
            "internal sealed partial class Mapper { }",
        };

        var twice = PartialTypesDocumentedTwice(new[] { ("M.cs", main), ("M.Part.cs", documentedPart) }, out int one);
        Assert.Equal(new[] { "A.B.Mapper" }, twice.Keys);
        Assert.Equal(new[] { "M.cs", "M.Part.cs" }, twice["A.B.Mapper"]);
        Assert.Equal(1, one);

        Assert.Empty(PartialTypesDocumentedTwice(new[] { ("M.cs", main), ("M.Part.cs", commentedPart) }, out int two));
        Assert.Equal(1, two);
    }

    [RepoSourceFact]
    public void EveryPartialTypeIsDocumentedOnOnePart()
    {
        string src = Path.Combine(TestCorpus.RepoRoot!, "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !TestSourceScanner.IsGenerated(f))
            .Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllLines(f)));

        var offenders = PartialTypesDocumentedTwice(files, out int types);

        Assert.True(types > 15, "only " + types + " partial types were read; the scan is not reaching the tree");
        Assert.True(offenders.Count == 0,
            "A partial type carries a documentation comment on more than one part, and the compiler publishes every one. " +
            "Keep the type's summary on its main part and make each other part's note an ordinary comment:\n  " +
            string.Join("\n  ", offenders.Select(kv => kv.Key + ": " + string.Join(", ", kv.Value))));
    }
}
