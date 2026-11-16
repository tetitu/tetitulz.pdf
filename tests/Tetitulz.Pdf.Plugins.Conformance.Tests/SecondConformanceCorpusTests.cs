// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The PDF/A checker against the second committed PDF/A test suite (CC BY 3.0; <c>Fixtures/bfo/README.md</c>), a second set of
/// files this repository did not write, from another vendor than the published profiles (2026-10-20, GAPS.md §20a): each file named
/// for the part and clause it tests and whether a conforming validator passes it, read as
/// <see cref="ConformanceCorpusTests"/> reads the published profile's — every verdict its name's, but for the files
/// <c>known-disagreements.tsv</c> lists, a list that only shrinks.
/// </summary>
public class SecondConformanceCorpusTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "bfo");

    [Fact]
    public void EveryFileGetsTheVerdictItsNameStatesOrIsAListedDisagreement()
    {
        var known = ConformanceCorpusTests.KnownDisagreements(Root);
        var files = Directory.GetFiles(Root, "*.pdf");

        var unexpected = new List<string>();
        var resolved = new List<string>();
        int passes = 0, fails = 0;
        foreach (string path in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            // pdfa2-6-1-13-bfo-t04-fail.pdf: the part's digit after "pdfa", the verdict last.
            string folder = "PDF_A-" + name[4];
            bool shouldPass = name.EndsWith("-pass.pdf", StringComparison.Ordinal);
            if (shouldPass) passes++; else fails++;

            bool passed = ConformanceCorpusTests.Passes(folder, File.ReadAllBytes(path), out string codes);
            bool listed = known.ContainsKey(name);
            if (passed != shouldPass && !listed)
                unexpected.Add($"{name}: expected {(shouldPass ? "pass" : "fail")}, got {(passed ? "pass" : "fail")} {codes}");
            if (passed == shouldPass && listed)
                resolved.Add($"{name} now gets its expected verdict; remove it from known-disagreements.tsv ({known[name]})");
        }

        // Potency: a folder that lost its files, or one verdict, would pass vacuously.
        Assert.True(files.Length >= 30, $"{files.Length} fixture files under Fixtures/bfo");
        Assert.True(passes > 0 && fails > 0, $"{passes} passing and {fails} failing files");
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected));
        Assert.True(resolved.Count == 0, string.Join(Environment.NewLine, resolved));
    }

    /// <summary>Every listed disagreement names a committed file and says why.</summary>
    [Fact]
    public void EveryListedDisagreementNamesAFileAndAReason()
    {
        foreach (var entry in ConformanceCorpusTests.KnownDisagreements(Root))
        {
            Assert.True(File.Exists(Path.Combine(Root, entry.Key)), entry.Key + " is not a fixture file");
            Assert.True(entry.Value.StartsWith("miss ", StringComparison.Ordinal) || entry.Value.StartsWith("stricter ", StringComparison.Ordinal),
                entry.Key + ": a reason starts with 'miss' or 'stricter': " + entry.Value);
        }
    }
}
