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
/// <see cref="PdfAValidator.Validate(Stream, PdfAConformancePart, PdfAConformanceLevel)"/>: a level the caller
/// requires decides which <c>pdfaid:conformance</c> letters a file may declare — the levels nesting, as the
/// published profiles' rules 6.7.11-3, 6.6.4-3 and 6.7.3-3 read them — and which level's rules run.
/// </summary>
public class PdfAConformanceLevelTests
{
    private static PdfAValidationResult Validate(byte[] pdf, int part, PdfAConformanceLevel level)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part, level);
    }

    private static List<PdfAViolation> LevelFindings(PdfAValidationResult r)
        => r.Violations.Where(v => v.Code == "A07" && v.Message.Contains("validated as", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// The letter a file declares (for PDF/A-4 the fixture's <c>"B"</c> writes none, <c>""</c> an empty property)
    /// against the level required: a higher level of the part is admitted, a lower one or another kind is not.
    /// </summary>
    [Theory]
    [InlineData(1, "B", PdfAConformanceLevel.B, false)]
    [InlineData(1, "A", PdfAConformanceLevel.B, false)]
    [InlineData(1, "B", PdfAConformanceLevel.A, true)]
    [InlineData(2, "U", PdfAConformanceLevel.B, false)]
    [InlineData(2, "A", PdfAConformanceLevel.U, false)]
    [InlineData(2, "B", PdfAConformanceLevel.U, true)]
    [InlineData(3, "U", PdfAConformanceLevel.A, true)]
    [InlineData(4, "B", PdfAConformanceLevel.None, false)]
    [InlineData(4, "", PdfAConformanceLevel.None, true)]
    [InlineData(4, "E", PdfAConformanceLevel.None, true)]
    [InlineData(4, "B", PdfAConformanceLevel.E, true)]
    [InlineData(4, "E", PdfAConformanceLevel.E, false)]
    [InlineData(4, "E", PdfAConformanceLevel.F, true)]
    [InlineData(4, "F", PdfAConformanceLevel.F, false)]
    public void ARequiredLevel_AdmitsItsOwnLetterAndTheHigherOnes(int part, string declared, PdfAConformanceLevel level, bool reported)
    {
        var findings = LevelFindings(Validate(PdfAEmbeddedFileRuleTests.PdfA(part, level: declared), part, level));

        Assert.True(findings.Count == (reported ? 1 : 0), string.Join(" | ", findings.Select(f => f.Message)));
        if (reported)
            Assert.Contains("validated as PDF/A-" + part + (level == PdfAConformanceLevel.None ? "" : level.ToString().ToLowerInvariant()),
                findings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>The declared level is today's reading: the same file, its letter its own, no level finding.</summary>
    [Fact]
    public void TheDeclaredLevel_ReadsTheFilesOwnLetter()
    {
        byte[] pdf = PdfAEmbeddedFileRuleTests.PdfA(4, level: "E");
        var declared = Validate(pdf, 4, PdfAConformanceLevel.Declared);
        Assert.Empty(LevelFindings(declared));
        Assert.True(declared.IsConformant, string.Join(" | ", declared.Violations.Select(v => v.Code + " " + v.Message)));
        Assert.Single(LevelFindings(Validate(pdf, 4, PdfAConformanceLevel.None)));
    }

    /// <summary>
    /// The level-specific rules run at the level required, not the one declared: level A's mark information
    /// (A73) and structure tree (A74) are asked of a file declaring B only when A is required, and a file
    /// declaring A judged at B is not asked them.
    /// </summary>
    [Fact]
    public void TheLevelsOwnRules_FollowTheRequiredLevel()
    {
        static bool LevelARules(PdfAValidationResult r) => r.Violations.Any(v => v.Code == "A73" || v.Code == "A74");

        byte[] declaresB = PdfAEmbeddedFileRuleTests.PdfA(2, level: "B");
        byte[] declaresA = PdfAEmbeddedFileRuleTests.PdfA(2, level: "A");

        Assert.False(LevelARules(Validate(declaresB, 2, PdfAConformanceLevel.Declared)));
        Assert.True(LevelARules(Validate(declaresB, 2, PdfAConformanceLevel.A)));
        Assert.True(LevelARules(Validate(declaresA, 2, PdfAConformanceLevel.Declared)));
        var atB = Validate(declaresA, 2, PdfAConformanceLevel.B);
        Assert.False(LevelARules(atB));
        Assert.True(atB.IsConformant, string.Join(" | ", atB.Violations.Select(v => v.Code + " " + v.Message)));
    }

    /// <summary>A level the part does not have is the caller's mistake, refused by both doors before the file is read.</summary>
    [Theory]
    [InlineData(1, PdfAConformanceLevel.U)]
    [InlineData(1, PdfAConformanceLevel.E)]
    [InlineData(2, PdfAConformanceLevel.None)]
    [InlineData(3, PdfAConformanceLevel.F)]
    [InlineData(4, PdfAConformanceLevel.A)]
    [InlineData(4, PdfAConformanceLevel.B)]
    [InlineData(2, (PdfAConformanceLevel)99)]
    public void ALevelThePartDoesNotHave_IsRefused(int part, PdfAConformanceLevel level)
    {
        // Not a PDF at all: the refusal comes before the file is opened.
        var stream = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfAValidator.Validate(new MemoryStream(new byte[] { 1, 2, 3 }), (PdfAConformancePart)part, level));
        Assert.Equal("level", stream.ParamName);
        var path = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfAValidator.Validate(Path.Combine(AppContext.BaseDirectory, "no-such-file.pdf"), (PdfAConformancePart)part, level));
        Assert.Equal("level", path.ParamName);
    }

    /// <summary>
    /// The plugin built with a level judges at it, names the flavour, and registers beside the declared-level
    /// plugin of the same part; one built with a level the part lacks is refused.
    /// </summary>
    [Fact]
    public void ThePluginJudgesAtItsLevel_UnderANameOfItsOwn()
    {
        byte[] noLetter = PdfAEmbeddedFileRuleTests.PdfA(4);   // PDF/A-4 itself: no pdfaid:conformance
        var declared = new PdfAValidationPlugin(PdfAConformancePart.Part4);
        var at4e = new PdfAValidationPlugin(PdfAConformancePart.Part4, PdfAConformanceLevel.E);

        Assert.True(declared.Validate(new MemoryStream(noLetter)).IsConformant);
        var result = at4e.Validate(new MemoryStream(noLetter));
        Assert.False(result.IsConformant);
        Assert.Contains(result.Violations, v => v.Code == "A07" && v.Message.Contains("validated as PDF/A-4e", StringComparison.Ordinal));
        Assert.True(at4e.Validate(new MemoryStream(PdfAEmbeddedFileRuleTests.PdfA(4, level: "E"))).IsConformant);

        Assert.Equal("PDF/A-4e", at4e.ConformanceLevel);
        Assert.Equal("PDF/A-2u", new PdfAValidationPlugin(PdfAConformancePart.Part2, PdfAConformanceLevel.U).ConformanceLevel);
        Assert.Equal("PDF/A-4", new PdfAValidationPlugin(PdfAConformancePart.Part4, PdfAConformanceLevel.None).ConformanceLevel);
        // The host refuses a second plugin of one name; a level's plugin has its own, the declared one keeps today's.
        Assert.Equal("Tetitulz.Plugins.PdfAValidation.Part4", declared.Name);
        Assert.Equal("Tetitulz.Plugins.PdfAValidation.Part4.E", at4e.Name);
        Assert.Equal(declared.Name, new PdfAValidationPlugin(PdfAConformancePart.Part4, PdfAConformanceLevel.Declared).Name);

        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => new PdfAValidationPlugin(PdfAConformancePart.Part1, PdfAConformanceLevel.U));
        Assert.Equal("level", refused.ParamName);
    }

    /// <summary>The acceptance control for the refusals: each part's own levels are taken, through the path door too.</summary>
    [Theory]
    [InlineData(1, PdfAConformanceLevel.A, "A")]
    [InlineData(2, PdfAConformanceLevel.U, "U")]
    [InlineData(3, PdfAConformanceLevel.B, "B")]
    [InlineData(4, PdfAConformanceLevel.F, "F")]
    public void EachPartsOwnLevels_AreTaken(int part, PdfAConformanceLevel level, string declared)
    {
        string file = Path.Combine(AppContext.BaseDirectory, $"level-{part}-{level}.pdf");
        File.WriteAllBytes(file, PdfAEmbeddedFileRuleTests.PdfA(part, level: declared));
        try
        {
            Assert.Empty(LevelFindings(PdfAValidator.Validate(file, (PdfAConformancePart)part, level)));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
