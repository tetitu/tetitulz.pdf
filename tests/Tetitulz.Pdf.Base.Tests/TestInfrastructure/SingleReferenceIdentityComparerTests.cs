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
/// <c>Core.Objects.PdfReferenceEquality</c> is the only reference-identity comparer in the tree.
/// </summary>
/// <remarks>
/// <para>CLAUDE.md said so, "the only reference-identity comparer in the tree; do not add another",
/// and five others had been added: <c>PdfObjectIndex.ReferenceComparer</c>,
/// <c>PdfActionRunner.ActionIdentityComparer</c>, <c>ContentOperationCache.ReferenceComparer</c>,
/// <c>PdfRenderContext.StreamReferenceComparer</c> and Builder's
/// <c>FootnoteNumbering.PdfReferenceEqualityShim</c>, the last named after the one it copied. Each
/// was a class implementing <c>IEqualityComparer&lt;T&gt;</c> over <c>ReferenceEquals</c> and the
/// runtime's identity hash. <c>IEqualityComparer&lt;in T&gt;</c> is contravariant, so the one
/// <c>IEqualityComparer&lt;object&gt;</c> serves every key type they were written for.</para>
/// <para>A rule with nothing checking it is a comment. This scans every source file for a class that
/// implements <c>IEqualityComparer</c> and uses the identity hash, outside the one file allowed
/// to.</para>
/// </remarks>
public class SingleReferenceIdentityComparerTests
{
    private const string TheOneFile = "PdfReferenceEquality.cs";

    /// <summary>A class declaration that implements <c>IEqualityComparer&lt;…&gt;</c>.</summary>
    private static readonly Regex ComparerClass = new(
        @"\bclass\s+(?<name>\w+)\s*:[^{]*\bIEqualityComparer\s*<",
        RegexOptions.Compiled);

    [RepoSourcesFact]
    public void No_source_file_declares_its_own_reference_identity_comparer()
    {
        var srcRoot = RepoSources.SrcRoot!;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (RepoSources.IsGenerated(file)) continue;
            if (Path.GetFileName(file) == TheOneFile) continue;

            var text = RepoSources.StripComments(File.ReadAllText(file));
            foreach (Match m in ComparerClass.Matches(text))
            {
                if (!Body(text, m.Index).Contains("RuntimeHelpers.GetHashCode", StringComparison.Ordinal)) continue;

                int line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
                violations.Add($"{Path.GetRelativePath(srcRoot, file)}:{line} — class {m.Groups["name"].Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} reference-identity comparer(s) besides Core.Objects.PdfReferenceEquality; " +
            "use PdfReferenceEquality.Instance:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// The potency control: the scan finds the one comparer that is allowed, in its own file, so a
    /// scan that could never match would fail here rather than pass above.
    /// </summary>
    [RepoSourcesFact]
    public void The_scan_recognises_the_one_allowed_comparer()
    {
        string file = Directory.EnumerateFiles(RepoSources.SrcRoot!, TheOneFile, SearchOption.AllDirectories).Single();
        string text = RepoSources.StripComments(File.ReadAllText(file));

        Match m = ComparerClass.Match(text);
        Assert.True(m.Success, "the comparer-class pattern no longer matches PdfReferenceEquality");
        Assert.Equal("PdfReferenceEquality", m.Groups["name"].Value);
        Assert.Contains("RuntimeHelpers.GetHashCode", Body(text, m.Index), StringComparison.Ordinal);
    }

    /// <summary>The class's text from its declaration to its closing brace, by brace count.</summary>
    private static string Body(string text, int start)
    {
        int open = text.IndexOf('{', start);
        if (open < 0) return string.Empty;

        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(start, i - start + 1);
        }
        return text.Substring(start);
    }
}
