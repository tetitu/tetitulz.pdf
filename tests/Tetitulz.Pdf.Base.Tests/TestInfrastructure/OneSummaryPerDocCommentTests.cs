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
/// Every documentation comment in <c>src/</c> holds one <c>&lt;summary&gt;</c>.
/// </summary>
/// <remarks>
/// <para><b>Two summaries in one comment are a member written in under another's comment.</b> The
/// new member's own summary goes in below the old block, so it carries both, and the member the old
/// block described — usually private, where nothing asks for a comment — is left with none. The
/// compiler says nothing: duplicate tags are well-formed XML. The final review (2026-10-22) found
/// forty-four: a class's description on the class after it, a property's ceiling on the next
/// property, the reasoning behind a digest-name resolver on an Ed448 signer, and older summaries
/// of a member left above their rewrite.</para>
/// <para>A comment may put its <c>&lt;param&gt;</c> or <c>&lt;remarks&gt;</c> before its summary;
/// that is one member's comment, and is not counted.</para>
/// </remarks>
public class OneSummaryPerDocCommentTests
{
    /// <summary>The documentation comments in <paramref name="text"/> that hold more than one summary, by the line each starts on.</summary>
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
}
