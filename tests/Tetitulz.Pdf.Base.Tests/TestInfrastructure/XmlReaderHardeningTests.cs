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
/// Every XML reader in <c>src/</c> is hardened against external-entity expansion (XXE).
/// </summary>
/// <remarks>
/// <para><b>The README's XXE claim was stronger than its coverage.</b> The code was in fact correct
/// at every site — that was checked before this was written — but nothing stopped the next
/// <c>XmlReader.Create</c> from arriving without settings, and a security claim with no guard
/// behind it is a claim about the past.</para>
///
/// <para><b>Three things have to hold, and each has its own failure.</b> A reader created from the
/// one-argument overload takes the framework defaults; a settings object without
/// <c>XmlResolver = null</c> can still fetch an external DTD even while refusing an inline one; and
/// an <c>XmlDocument</c> carries its own resolver independent of the reader that filled it.</para>
///
/// <para><b>Every count has a floor, because a regex that stops matching produces the same silence
/// as a clean tree.</b> This is the failure mode the whole area is about: a guard whose value is an
/// absence must prove it looked. If a refactor genuinely removes readers, lower the floor
/// deliberately in the same commit.</para>
/// </remarks>
public class XmlReaderHardeningTests
{
    /// <summary>Every non-generated C# file under <c>src/</c>.</summary>
    private static IEnumerable<(string path, string text)> SourceFiles()
    {
        string src = Path.Combine(TestCorpus.RepoRoot!, "src");

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceScanner.IsGenerated(file)) continue;
            yield return (Path.GetRelativePath(src, file).Replace('\\', '/'),
                          TestSourceScanner.StripComments(File.ReadAllText(file)));
        }
    }

    /// <summary>
    /// The floors, measured when this guard was written. Each is "at least", not "exactly", so
    /// adding a hardened reader does not redden the build.
    /// </summary>
    private const int MinReaderSettings = 8;
    private const int MinReaderCreates = 10;
    private const int MinXmlDocuments = 5;

    // ================================================================

    /// <summary>
    /// <b>Every <c>XmlReaderSettings</c> sets both <c>DtdProcessing</c> and a null
    /// <c>XmlResolver</c>.</b>
    /// </summary>
    /// <remarks>
    /// The pair matters. <c>DtdProcessing</c> alone decides what happens to an <i>inline</i>
    /// doctype; the resolver is what would go and fetch an external one.
    /// </remarks>
    [RepoSourceFact]
    public void EveryXmlReaderSettingsSetsDtdProcessingAndNullsTheResolver()
    {
        var offenders = new List<string>();
        int found = 0;

        foreach (var (path, text) in SourceFiles())
        {
            foreach (Match m in Regex.Matches(text, @"new\s+XmlReaderSettings\s*\{"))
            {
                found++;
                string block = BracedBlockAt(text, m.Index + m.Length - 1);

                if (!block.Contains("DtdProcessing"))
                    offenders.Add($"{path}:{TestSourceScanner.LineOf(text, m.Index)} sets no DtdProcessing");
                if (!Regex.IsMatch(block, @"XmlResolver\s*=\s*null"))
                    offenders.Add($"{path}:{TestSourceScanner.LineOf(text, m.Index)} does not null the XmlResolver");
            }
        }

        Assert.True(found >= MinReaderSettings,
            $"found only {found} XmlReaderSettings constructions, below the floor of " +
            $"{MinReaderSettings} — the scan is matching less than it did, so its silence means nothing");

        Assert.True(offenders.Count == 0,
            "These XmlReaderSettings are not hardened against XXE:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// <b>No <c>XmlReader.Create</c> uses the one-argument overload</b>, which takes the framework
    /// defaults rather than this repository's.
    /// </summary>
    [RepoSourceFact]
    public void NoXmlReaderIsCreatedWithoutSettings()
    {
        var offenders = new List<string>();
        int found = 0;

        foreach (var (path, text) in SourceFiles())
        {
            foreach (Match m in Regex.Matches(text, @"XmlReader\s*\.\s*Create\s*\("))
            {
                found++;
                string args = ParenthesisedArgumentsAt(text, m.Index + m.Length - 1);

                // A settings argument is the second one; the shape varies (a named local, an
                // inline object), so this asks only whether there IS a second argument.
                if (TopLevelCommaCount(args) < 1)
                    offenders.Add($"{path}:{TestSourceScanner.LineOf(text, m.Index)} XmlReader.Create with no settings");
            }
        }

        Assert.True(found >= MinReaderCreates,
            $"found only {found} XmlReader.Create calls, below the floor of {MinReaderCreates}");

        Assert.True(offenders.Count == 0,
            "These readers take the framework's default settings:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// <b>Every <c>XmlDocument</c> nulls its own resolver</b>, which is separate from the reader's.
    /// </summary>
    /// <remarks>
    /// <c>XmlDocument.Load(reader)</c> does not inherit the reader's resolver: the document resolves
    /// entities itself while building the DOM, so hardening only the reader leaves half the job.
    /// </remarks>
    [RepoSourceFact]
    public void EveryXmlDocumentNullsItsResolver()
    {
        var offenders = new List<string>();
        int found = 0;

        foreach (var (path, text) in SourceFiles())
        {
            foreach (Match m in Regex.Matches(text, @"new\s+XmlDocument\b"))
            {
                found++;

                // The initializer follows the type name across any amount of whitespace, and an
                // object initializer laid out over several lines is the normal case here — an
                // earlier version of this required the brace within two characters and reported a
                // correctly hardened site as an offender.
                int i = m.Index + m.Length;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

                string block = i < text.Length && text[i] == '{'
                    ? BracedBlockAt(text, i)
                    : string.Empty;

                if (!Regex.IsMatch(block, @"XmlResolver\s*=\s*null"))
                    offenders.Add($"{path}:{TestSourceScanner.LineOf(text, m.Index)} does not null the XmlResolver");
            }
        }

        Assert.True(found >= MinXmlDocuments,
            $"found only {found} XmlDocument constructions, below the floor of {MinXmlDocuments}");

        Assert.True(offenders.Count == 0,
            "These XmlDocuments carry the default resolver:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The scan reaches the files it is supposed to, named individually.
    /// </summary>
    /// <remarks>
    /// The floors above count matches; this checks the <i>files</i>, so a scan pointed at the wrong
    /// directory cannot meet a numeric floor from somewhere else in the tree.
    /// </remarks>
    [RepoSourceFact]
    public void TheScanReachesTheFilesThatParseXml()
    {
        var paths = SourceFiles().Select(f => f.path).ToHashSet(StringComparer.Ordinal);

        foreach (string expected in new[]
                 {
                     "tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/XfdfSerializer.cs",
                     "plugins/Tetitulz.Pdf.Plugins.Conformance/PdfUaChecker.cs",
                     "features/Tetitulz.Pdf.Accessibility/Metadata/PdfUaXmpWriter.cs",
                 })
        {
            Assert.Contains(expected, paths);
        }
    }

    // ================================================================ tiny source helpers

    /// <summary>The text between the brace at <paramref name="open"/> and its match.</summary>
    private static string BracedBlockAt(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text.Substring(open, i - open + 1);
        }
        return text.Substring(open);
    }

    /// <summary>The text between the parenthesis at <paramref name="open"/> and its match.</summary>
    private static string ParenthesisedArgumentsAt(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text.Substring(open + 1, i - open - 1);
        }
        return text.Substring(open);
    }

    /// <summary>Commas at nesting depth zero — the argument separators of one call.</summary>
    private static int TopLevelCommaCount(string args)
    {
        int depth = 0, commas = 0;
        foreach (char c in args)
        {
            if (c == '(' || c == '[' || c == '{' || c == '<') depth++;
            else if (c == ')' || c == ']' || c == '}' || c == '>') depth--;
            else if (c == ',' && depth == 0) commas++;
        }
        return commas;
    }
}
