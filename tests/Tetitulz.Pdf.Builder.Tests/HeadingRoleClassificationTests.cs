// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Which <see cref="Paragraph.Role"/> values are headings, and at what level — the two structural
/// guards in <c>TocEntries.LevelOf</c> that decide what a table of contents will even look at.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep deleted both guards and <b>all 473 Builder
/// tests stayed green</b>. The suite covers the levels that <i>are</i> headings thoroughly —
/// <c>H1</c>–<c>H6</c>, the bare <c>H</c> at its section depth, the depth limit — and covered
/// nothing about the values that are not.</para>
///
/// <para><b>The shape guard is load-bearing, not defensive.</b> <c>Role</c> is a public settable
/// string, and <c>"P"</c>, <c>"Caption"</c> and <c>"Figure"</c> are the ordinary values a caller
/// writing a tagged document sets. Without the length test, <c>LevelOf</c> indexes
/// <c>role[1]</c> on a one-character role — so building a table of contents over a correctly
/// tagged document would throw, from inside an exporter, on input that is entirely valid.</para>
///
/// <para><b>The range guard needs a raised <see cref="TocBlock.MaxLevel"/> to be observable</b>,
/// which is why it went untested: at the default of 3, an <c>H7</c> admitted at level 7 is
/// filtered out again by the level limit, and the two implementations agree by accident.
/// <c>MaxLevel</c> is caller-settable and unclamped, so the disagreement is reachable — and a role
/// like <c>"HX"</c> resolves to level 40, which no limit a caller would write excludes.</para>
/// </remarks>
public class HeadingRoleClassificationTests
{
    private static Document DocWithRoles(params string?[] roles)
    {
        var doc = new Document();
        var section = new Section();
        foreach (string? role in roles)
        {
            var p = new Paragraph { Role = role };
            p.Runs.Add(new TextRun { Text = role ?? "(none)" });
            section.Children.Add(p);
        }
        doc.Sections.Add(section);
        return doc;
    }

    private static string[] TextsOf(Document doc, int maxLevel) =>
        TocEntries.Collect(doc, maxLevel).Select(e => e.Text).ToArray();

    // ── the shape guard ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-character structural role is not a heading — and, crucially, does not make the
    /// collector index past the end of the string.
    /// </summary>
    [Theory]
    [InlineData("P")]
    [InlineData("L")]
    [InlineData("Caption")]
    [InlineData("Figure")]
    [InlineData("TOCI")]
    public void ANonHeadingRole_IsNeitherAnEntryNorACrash(string role)
    {
        string[] texts = TextsOf(DocWithRoles(role), maxLevel: 6);

        Assert.Empty(texts);
    }

    /// <summary>
    /// The same roles mixed in with real headings: the headings are collected and the rest are
    /// passed over. A document is not made of headings alone, so this is the realistic input.
    /// </summary>
    [Fact]
    public void NonHeadingRoles_AreSkippedWithoutDisturbingTheHeadings()
    {
        string[] texts = TextsOf(
            DocWithRoles("H1", "P", "H2", "Caption", null, "H3"), maxLevel: 6);

        Assert.Equal(new[] { "H1", "H2", "H3" }, texts);
    }

    // ── the range guard ───────────────────────────────────────────────────────────────

    /// <summary>
    /// ISO 32000-1 defines <c>H1</c>–<c>H6</c>. A role naming a deeper level is not a heading, even
    /// when the table of contents would have had room for it.
    /// </summary>
    [Theory]
    [InlineData("H7")]
    [InlineData("H8")]
    [InlineData("H9")]
    [InlineData("H0")]
    public void ARoleOutsideH1ToH6_IsNotAnEntry(string role)
    {
        // The raised limit is the point: at the default of 3 this cannot discriminate, because a
        // level-7 entry is filtered out again by the limit rather than by the range guard.
        string[] texts = TextsOf(DocWithRoles(role), maxLevel: 9);

        Assert.Empty(texts);
    }

    /// <summary>
    /// A second character that is not a digit at all resolves to a nonsense level — <c>'X'</c> is
    /// 40 — which no limit a caller would write excludes.
    /// </summary>
    [Theory]
    [InlineData("HX")]
    [InlineData("H:")]
    [InlineData("H-")]
    public void ARoleWhoseSecondCharacterIsNotADigit_IsNotAnEntry(string role)
    {
        string[] texts = TextsOf(DocWithRoles(role), maxLevel: 99);

        Assert.Empty(texts);
    }

    // ── the acceptance control ────────────────────────────────────────────────────────

    /// <summary>
    /// All six real levels are collected, at the level their role names. Without this the refusals
    /// above all pass on a collector that returns nothing at all.
    /// </summary>
    [Fact]
    public void EveryRealHeadingLevel_IsCollectedAtItsOwnLevel()
    {
        var doc = DocWithRoles("H1", "H2", "H3", "H4", "H5", "H6");

        var entries = TocEntries.Collect(doc, maxLevel: 6);

        Assert.Equal(6, entries.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, entries.Select(e => e.Level).ToArray());
    }
}
