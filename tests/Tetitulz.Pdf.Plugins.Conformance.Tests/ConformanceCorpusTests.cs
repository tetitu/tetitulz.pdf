// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The PDF/A and PDF/UA-1 checkers against files this repository did not write: a subset of
/// the published profile's test corpus (CC BY 4.0; <c>Fixtures/the published profiles/README.md</c>), each file named for the
/// clause it tests and whether a conforming validator passes it.
/// </summary>
/// <remarks>
/// <para>Every file's verdict must match its name, except the ones <c>known-disagreements.tsv</c>
/// lists, each with its reason: a rule the checker does not have yet (a <c>miss</c>, the file passing
/// though it should fail) or a check deliberately stricter than the published profile's (<c>stricter</c>). The list
/// only shrinks: an entry whose file now gets its expected verdict fails the test until it is
/// removed, so a newly closed rule is recorded where it was closed.</para>
/// <para>The first run (2026-10-18) found 14 conforming PDF/A files and 28 conforming PDF/UA-1 files
/// reported as violations, and every file-header test file passed; each was fixed against the published profile's
/// profile rule and the files that exercise it.</para>
/// </remarks>
public class ConformanceCorpusTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "veraPDF");

    public static TheoryData<string> Folders() => new()
    {
        "PDF_A-1a", "PDF_A-1b", "PDF_A-2a", "PDF_A-2b", "PDF_A-2u", "PDF_A-3b",
        "PDF_A-4", "PDF_A-4e", "PDF_A-4f", "PDF_UA-1",
    };

    [Theory]
    [MemberData(nameof(Folders))]
    public void EveryFileGetsTheVerdictItsNameStatesOrIsAListedDisagreement(string folder)
    {
        var known = KnownDisagreements().Where(k => k.Key.StartsWith(folder + "/", StringComparison.Ordinal))
            .ToDictionary(k => k.Key, k => k.Value, StringComparer.Ordinal);
        var files = Directory.GetFiles(Path.Combine(Root, folder), "*.pdf");

        var unexpected = new List<string>();
        var resolved = new List<string>();
        int passes = 0, fails = 0;
        foreach (string path in files.OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            string key = folder + "/" + name;
            bool shouldPass = name.Contains("-pass-", StringComparison.Ordinal);
            if (shouldPass) passes++; else fails++;

            bool passed = Passes(folder, File.ReadAllBytes(path), LevelOf(folder), out string codes);
            bool listed = known.ContainsKey(key);
            if (passed != shouldPass && !listed)
                unexpected.Add($"{key}: expected {(shouldPass ? "pass" : "fail")}, got {(passed ? "pass" : "fail")} {codes}");
            if (passed == shouldPass && listed)
                resolved.Add($"{key} now gets its expected verdict; remove it from known-disagreements.tsv ({known[key]})");
        }

        // Potency: a folder that lost its files, or one verdict, would pass vacuously.
        Assert.True(files.Length > 0, $"no fixture files under {folder}");
        Assert.True(passes > 0 && fails > 0, $"{folder}: {passes} passing and {fails} failing files");
        Assert.True(unexpected.Count == 0, string.Join(Environment.NewLine, unexpected));
        Assert.True(resolved.Count == 0, string.Join(Environment.NewLine, resolved));
    }

    /// <summary>Every listed disagreement names a committed file and says why.</summary>
    [Fact]
    public void EveryListedDisagreementNamesAFileAndAReason()
    {
        var known = KnownDisagreements();
        Assert.NotEmpty(known);
        foreach (var entry in known)
        {
            Assert.True(File.Exists(Path.Combine(Root, entry.Key)), entry.Key + " is not a fixture file");
            Assert.True(entry.Value.StartsWith("miss ", StringComparison.Ordinal) || entry.Value.StartsWith("stricter ", StringComparison.Ordinal)
                || entry.Value.StartsWith("deep ", StringComparison.Ordinal),
                entry.Key + ": a reason starts with 'miss', 'stricter' or 'deep': " + entry.Value);
            // A deep entry is a PDF/UA-1 rule the audit's deep checker owns, and names the code it fails with.
            if (entry.Value.StartsWith("deep ", StringComparison.Ordinal))
                Assert.True(entry.Key.StartsWith("PDF_UA-1/", StringComparison.Ordinal) && entry.Key.Contains("-fail-", StringComparison.Ordinal)
                    && Regex.IsMatch(entry.Value, @": UA[0-9]{2}$", RegexOptions.None, TimeSpan.FromSeconds(1)),
                    entry.Key + ": a deep entry is a failing PDF/UA-1 file whose reason ends ': UAnn': " + entry.Value);
        }
    }

    /// <summary>
    /// The level a folder of this corpus is validated at — its flavour's letter, as the corpus's own validator
    /// is run with that flavour's profile; <c>PDF_A-4</c> is PDF/A-4 itself, which declares none.
    /// </summary>
    internal static PdfAConformanceLevel LevelOf(string folder)
    {
        if (!folder.StartsWith("PDF_A-", StringComparison.Ordinal)) return PdfAConformanceLevel.Declared;
        if (folder.Length == "PDF_A-4".Length) return PdfAConformanceLevel.None;
        return folder[folder.Length - 1] switch
        {
            'a' => PdfAConformanceLevel.A,
            'b' => PdfAConformanceLevel.B,
            'u' => PdfAConformanceLevel.U,
            'e' => PdfAConformanceLevel.E,
            'f' => PdfAConformanceLevel.F,
            _ => throw new ArgumentException("no level in the folder name " + folder, nameof(folder)),
        };
    }

    /// <summary>
    /// Whether <paramref name="pdf"/> passes the checker for <paramref name="folder"/>'s standard — <c>PDF_UA-1</c>, or a
    /// <c>PDF_A-</c> folder by its part's digit — with the codes of what it failed. The one verdict every corpus here
    /// is read by (<see cref="SecondConformanceCorpusTests"/> too, at each file's declared level).
    /// </summary>
    internal static bool Passes(string folder, byte[] pdf, out string codes)
        => Passes(folder, pdf, PdfAConformanceLevel.Declared, out codes);

    /// <summary><see cref="Passes(string, byte[], out string)"/> at <paramref name="level"/>.</summary>
    internal static bool Passes(string folder, byte[] pdf, PdfAConformanceLevel level, out string codes)
    {
        if (folder == "PDF_UA-1")
        {
            // UA99 is "could not be verified", never a violation (PdfAccessibility.Audit maps it to Info).
            var violations = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code != "UA99").ToList();
            codes = string.Join(",", violations.Select(v => v.Code).Distinct());
            return violations.Count == 0;
        }

        var part = folder.StartsWith("PDF_A-1", StringComparison.Ordinal) ? PdfAConformancePart.Part1
            : folder.StartsWith("PDF_A-2", StringComparison.Ordinal) ? PdfAConformancePart.Part2
            : folder.StartsWith("PDF_A-3", StringComparison.Ordinal) ? PdfAConformancePart.Part3
            : PdfAConformancePart.Part4;
        var result = PdfAValidator.Validate(new MemoryStream(pdf), part, level);
        codes =string.Join(",", result.Violations.Select(v => v.Code).Distinct());
        return result.IsConformant;
    }

    /// <summary><c>folder/file</c> → reason, from <c>known-disagreements.tsv</c>.</summary>
    private static Dictionary<string, string> KnownDisagreements() => KnownDisagreements(Root);

    /// <summary>Path under <paramref name="root"/> → reason, from its <c>known-disagreements.tsv</c>: a corpus's files whose verdict the checkers do not yet match.</summary>
    internal static Dictionary<string, string> KnownDisagreements(string root)
        => File.ReadAllLines(Path.Combine(root, "known-disagreements.tsv"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t'))
            .ToDictionary(f => f[0], f => f.Length > 1 ? f[1] : "", StringComparer.Ordinal);
}
