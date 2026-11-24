// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfAccessibility.Audit(string)"/> — both PDF/UA-1 checkers, the plugin's and the deep
/// one — against the published profile's PDF/UA-1 test files (CC BY 4.0), the subset committed under the
/// conformance plugin's tests (<c>Fixtures/the published profiles/PDF_UA-1</c>), each named for its clause and verdict.
/// </summary>
/// <remarks>
/// <para>The plugin's own corpus test runs <c>PdfUaChecker</c> alone; the structure rules — 7.2's
/// parent and child elements, 7.4's headings, 7.5's tables — are the deep checker's, so only the audit
/// can be held to those files: an LBody is not required in a list item (UA11), a form's own content
/// items are not counted against its page (UA18), Identity-H over Adobe-Japan1 is accepted (UA20),
/// and UA14 is the deliberate strictness the plugin's UA50 shares.</para>
/// <para>Every file's verdict — no error-severity issue, or some — must match its name unless
/// <c>verapdf-audit-disagreements.tsv</c> beside this file lists it, with its reason; the list only
/// shrinks, as the plugin's does.</para>
/// </remarks>
public class UaConformanceCorpusTests
{
    private static string Folder()
    {
        string? root = TestCorpus.RepoRoot;
        Assert.NotNull(root);
        return Path.Combine(root!, "tests", "Tetitulz.Pdf.Plugins.Conformance.Tests", "Fixtures", "veraPDF", "PDF_UA-1");
    }

    private static Dictionary<string, string> KnownDisagreements()
    {
        string path = Path.Combine(TestCorpus.RepoRoot!, "tests", "Tetitulz.Pdf.Accessibility.Tests", "verapdf-audit-disagreements.tsv");
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int tab = line.IndexOf('\t');
            known[tab < 0 ? line : line.Substring(0, tab)] = tab < 0 ? "" : line.Substring(tab + 1);
        }
        return known;
    }

    [Fact]
    public void EveryPdfUa1FileGetsTheVerdictItsNameStatesOrIsAListedDisagreement()
    {
        var known = KnownDisagreements();
        var unexpected = new List<string>();
        var resolved = new List<string>();
        int files = 0;
        foreach (string path in Directory.GetFiles(Folder(), "*.pdf").OrderBy(p => p, StringComparer.Ordinal))
        {
            files++;
            string name = Path.GetFileName(path);
            bool shouldPass = name.Contains("-pass-", StringComparison.Ordinal);
            var errors = PdfAccessibility.Audit(path).Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).Distinct().ToList();
            bool passed = errors.Count == 0;
            bool listed = known.ContainsKey(name);
            if (passed != shouldPass && !listed)
                unexpected.Add($"{name}: expected {(shouldPass ? "pass" : "fail")}, got {(passed ? "pass" : "fail " + string.Join(",", errors))}");
            if (passed == shouldPass && listed)
                resolved.Add($"{name} now gets its expected verdict; remove it from verapdf-audit-disagreements.tsv ({known[name]})");
        }

        Assert.True(files > 50, $"only {files} PDF/UA-1 files were found");   // the folder, not an empty glob
        Assert.True(unexpected.Count == 0 && resolved.Count == 0, string.Join(Environment.NewLine, unexpected.Concat(resolved)));
    }

    /// <summary>
    /// The plugin's corpus list marks a file <c>deep</c> when its rule is the deep checker's, so
    /// <c>PdfUaChecker</c> alone passes it; each such file fails the audit with the code the entry names.
    /// </summary>
    [Fact]
    public void EveryDeepEntryOfThePluginsListFailsTheAuditWithTheCodeItNames()
    {
        string list = Path.Combine(TestCorpus.RepoRoot!, "tests", "Tetitulz.Pdf.Plugins.Conformance.Tests", "Fixtures", "veraPDF", "known-disagreements.tsv");
        var deep = File.ReadAllLines(list)
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t'))
            .Where(f => f.Length > 1 && f[1].StartsWith("deep ", StringComparison.Ordinal))
            .ToList();
        var auditList = KnownDisagreements();

        var wrong = new List<string>();
        foreach (var entry in deep)
        {
            string name = entry[0].Substring("PDF_UA-1/".Length);
            string code = entry[1].Substring(entry[1].LastIndexOf(": ", StringComparison.Ordinal) + 2);
            var errors = PdfAccessibility.Audit(Path.Combine(Folder(), name)).Issues
                .Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).Distinct().ToList();
            if (!errors.Contains(code, StringComparer.Ordinal) || auditList.ContainsKey(name))
                wrong.Add($"{name}: the entry names {code}; the audit reports [{string.Join(",", errors)}]");
        }

        Assert.True(deep.Count > 20, $"only {deep.Count} deep entries were read");   // the list, not an empty filter
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }
}
