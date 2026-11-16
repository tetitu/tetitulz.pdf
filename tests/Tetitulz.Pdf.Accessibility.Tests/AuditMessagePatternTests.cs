// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The audit reads numbers out of the checkers' messages, which embed text the file chose; each pattern
/// reads at most <see cref="PdfAccessibility.MaxMessageCharsMatched"/> characters and carries a match
/// timeout (CLAUDE.md, regular expressions over a file's text; 07's DoS hunt, F4).
/// </summary>
public sealed class AuditMessagePatternTests
{
    /// <summary>A page number inside the cap is read; the same one just past it is not.</summary>
    [Fact]
    public void APageNumber_IsReadOnlyWithinTheCap()
    {
        string name = new string('n', PdfAccessibility.MaxMessageCharsMatched - 20);

        Assert.Equal(3, PdfAccessibility.ExtractPageNumber("Figure '" + name + "' on page 3 has no /Alt"));
        Assert.Null(PdfAccessibility.ExtractPageNumber("Figure '" + name + "xxxxxxxxxxxxxxxxxxxxxxxxx' on page 3 has no /Alt"));
        Assert.Null(PdfAccessibility.ExtractPageNumber(null));
    }

    /// <summary>Every pattern the audit holds has a finite match timeout.</summary>
    [Fact]
    public void EveryAuditPattern_CarriesAMatchTimeout()
    {
        var patterns = typeof(PdfAccessibility)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(Regex))
            .Select(f => (f.Name, Regex: (Regex)f.GetValue(null)))
            .ToList();

        Assert.Equal(4, patterns.Count);
        Assert.All(patterns, p => Assert.NotEqual(Regex.InfiniteMatchTimeout, p.Regex.MatchTimeout));
    }
}
