// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Xunit;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="SystemFontResolver"/> — the one font search that checks <b>cmap coverage</b> rather
/// than trusting a filename, and the only one a plugin can reach (Core grants
/// <c>InternalsVisibleTo</c> to no plugin, so <c>FontFileLocator</c> is out of reach).
///
/// <para><b>What these pin.</b> That the opt-in gate really gates; that a font matching by name but
/// lacking the glyphs is rejected rather than returned; and that a failure names the code point.
/// The last one matters because "no font found" sends a caller looking for a missing font, while
/// "nothing covers U+05D0" tells them they need a Hebrew face specifically.</para>
/// </summary>
[Collection(SystemFontCacheCollection.Name)]
public class SystemFontResolverTests
{
    private static IReadOnlyCollection<int> CodePoints(string s) => s.Select(c => (int)c).ToList();

    // ── the opt-in gate ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With no provider and no opt-in there is nothing the resolver is permitted to use, so it must
    /// fail rather than quietly redistribute a system font. Embedding a font redistributes it, and
    /// that is the caller's licensing decision to make.
    /// </summary>
    [Fact]
    public void WithoutOptIn_AndWithoutAProvider_ResolutionFails()
    {
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                FamilyHint = "Arial",
                RequiredCodePoints = CodePoints("abc"),
                AllowSystemFontSubstitution = false,
            },
            fontProvider: null);

        Assert.False(result.Success);
        Assert.Null(result.Program);
        Assert.Contains("AllowSystemFontSubstitution", result.FailureReason);
        Assert.Equal(result.FailureReason, SystemFontResolver.LastResolutionError);
    }

    /// <summary>
    /// The control for the test above: the same request with the opt-in set does find something.
    /// Without this pair, a resolver that always failed would pass the gate test.
    /// </summary>
    [HebrewFontFact]
    public void WithOptIn_ResolutionSucceedsForTheSameRequest()
    {
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = CodePoints(HebrewFontProbe.Shalom),
                AllowSystemFontSubstitution = true,
            },
            fontProvider: null);

        Assert.True(result.Success, result.FailureReason);
        Assert.True(result.FromSystem);
        Assert.NotNull(result.SourcePath);
        Assert.Null(SystemFontResolver.LastResolutionError);
    }

    // ── coverage is what decides, not the filename ──────────────────────────────────────────

    /// <summary>
    /// A caller-supplied font that parses but cannot draw the text is <b>not</b> a match. Accepting
    /// it defers the failure to drawing time, where the historical behaviour was a silently
    /// substituted space — a Hebrew name losing a letter with no trace.
    /// </summary>
    [HebrewFontFact]
    public void ProviderFontLackingTheGlyphs_IsRejectedRatherThanReturned()
    {
        byte[] hebrewFont = HebrewFontProbe.Bytes;

        // Ask that same font for a code point no real font carries.
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = new[] { 0x10FFFF },
                AllowSystemFontSubstitution = false,
            },
            fontProvider: _ => hebrewFont);

        Assert.False(result.Success);
        Assert.Contains(0x10FFFF, result.UncoveredCodePoints);
        Assert.Contains("10FFFF", result.FailureReason);
    }

    /// <summary>
    /// The control: the same provider font <i>is</i> accepted for text it covers — so the rejection
    /// above is about coverage, not about providers being ignored.
    /// </summary>
    [HebrewFontFact]
    public void ProviderFontCoveringTheGlyphs_IsUsedWithoutTouchingTheSystem()
    {
        byte[] hebrewFont = HebrewFontProbe.Bytes;

        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = CodePoints(HebrewFontProbe.Shalom),
                AllowSystemFontSubstitution = false,   // deliberately off: the provider must suffice
            },
            fontProvider: _ => hebrewFont);

        Assert.True(result.Success, result.FailureReason);
        Assert.False(result.FromSystem);
        Assert.Null(result.SourcePath);
        Assert.Same(hebrewFont, result.FontData);
    }

    /// <summary>
    /// A resolved font must actually be able to draw every requested code point. This is the
    /// property the whole type exists for, asserted against the returned program rather than
    /// against the resolver's own opinion of itself.
    /// </summary>
    [HebrewFontFact]
    public void AResolvedFontCoversEveryRequestedCodePoint()
    {
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = CodePoints(HebrewFontProbe.Shalom),
                AllowSystemFontSubstitution = true,
            },
            fontProvider: null);

        Assert.True(result.Success, result.FailureReason);
        foreach (char c in HebrewFontProbe.Shalom)
            Assert.True(result.Program.Covers(c), $"resolved font cannot draw U+{(int)c:X4}");
    }

    // ── failure reporting ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// An unsatisfiable request names the code points. "No font found" sends the caller looking for
    /// a missing file; naming U+10FFFF tells them the request itself was impossible.
    /// </summary>
    [Fact]
    public void AnUnsatisfiableRequest_NamesTheCodePointsRatherThanFailingGenerically()
    {
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = new[] { 0x10FFFF },
                AllowSystemFontSubstitution = true,
                MaxScannedFiles = 8,     // keep the test quick; the scan cannot succeed anyway
            },
            fontProvider: null);

        Assert.False(result.Success);
        Assert.Contains("10FFFF", result.FailureReason);
        Assert.Contains(0x10FFFF, result.UncoveredCodePoints);
    }

    /// <summary>
    /// A provider that throws is the caller's bug, not a reason to take the operation down — the
    /// resolver records it and carries on to whatever fallbacks the caller allowed.
    /// </summary>
    [HebrewFontFact]
    public void AProviderThatThrows_DoesNotPropagateAndFallsThroughToTheSystem()
    {
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = CodePoints(HebrewFontProbe.Shalom),
                AllowSystemFontSubstitution = true,
            },
            fontProvider: _ => throw new InvalidOperationException("provider is broken"));

        Assert.True(result.Success, result.FailureReason);
        Assert.True(result.FromSystem);
    }

    /// <summary>
    /// Whitespace is not evidence of coverage — nearly every font has a space — so requiring it
    /// must not change which font is chosen, and a whitespace-only request must not succeed by
    /// accident on a font that covers nothing else.
    /// </summary>
    [HebrewFontFact]
    public void WhitespaceIsNotCountedAsARequiredCodePoint()
    {
        byte[] hebrewFont = HebrewFontProbe.Bytes;

        var withSpaces = SystemFontResolver.Resolve(
            new FontResolutionRequest { RequiredCodePoints = CodePoints($" {HebrewFontProbe.Shalom}\t") },
            fontProvider: _ => hebrewFont);
        var without = SystemFontResolver.Resolve(
            new FontResolutionRequest { RequiredCodePoints = CodePoints(HebrewFontProbe.Shalom) },
            fontProvider: _ => hebrewFont);

        Assert.True(withSpaces.Success);
        Assert.True(without.Success);
        Assert.Same(without.FontData, withSpaces.FontData);
    }

    // ── FindFamilyFiles ranking ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The ranked list's first element must equal what the single-answer lookup returns, or the two
    /// searches have drifted and a caller walking the list would get a different font from a caller
    /// asking for one.
    /// </summary>
    /// <summary>
    /// A resolution that <b>succeeds</b> must leave no diagnostic behind, even when something went
    /// wrong earlier in the same call.
    ///
    /// <para>The case that catches it: a provider throws (recorded mid-call), and the search then
    /// succeeds from a system font. Clearing the slot only at entry is not enough — a caller reading
    /// it after a successful resolution would see a failure that did not happen. This is the shape
    /// of the <c>LastPermsValidation</c> defect CLAUDE.md records: per-thread storage is not
    /// per-outcome scope.</para>
    /// </summary>
    [HebrewFontFact]
    public void AThrowingProviderLeavesNoDiagnosticBehindWhenTheResolutionThenSucceeds()
    {
        var result = SystemFontResolver.Resolve(
            new FontResolutionRequest
            {
                RequiredCodePoints = CodePoints(HebrewFontProbe.Shalom),
                AllowSystemFontSubstitution = true,
            },
            fontProvider: _ => throw new InvalidOperationException("provider is broken"));

        Assert.True(result.Success, result.FailureReason);
        Assert.Null(result.FailureReason);
        Assert.Null(SystemFontResolver.LastResolutionError);
    }

    [Fact]
    public void RankedFamilyLookup_AgreesWithTheSingleAnswerLookupOnItsFirstElement()
    {
        // Uses the internals visible to this test assembly; the production callers of the ranked
        // form reach it the same way from inside Core.
        string single = FontFileLocator.FindFamilyFile("Arial", bold: false, italic: false);
        var ranked = FontFileLocator.FindFamilyFiles("Arial", bold: false, italic: false, max: 4);

        if (single == null)
        {
            Assert.Empty(ranked);
        }
        else
        {
            Assert.NotEmpty(ranked);
            Assert.Equal(single, ranked[0]);
        }
    }

    [Fact]
    public void RankedFamilyLookup_HonoursItsCapAndReturnsNoDuplicates()
    {
        var ranked = FontFileLocator.FindFamilyFiles("Hebrew", bold: false, italic: false, max: 3);

        Assert.True(ranked.Count <= 3, $"cap ignored: {ranked.Count} results");
        Assert.Equal(ranked.Count, ranked.Distinct().Count());
    }
}
