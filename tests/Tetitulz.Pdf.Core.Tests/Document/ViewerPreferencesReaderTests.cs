// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="PdfViewerPreferences.Read"/> — the one reader of a catalog's <c>/ViewerPreferences</c>
/// (§12.2), taking a resolve delegate rather than a document.
/// </summary>
/// <remarks>
/// <para><b>Why the delegate.</b> An encrypted document loads no catalog, so <c>doc.Catalog</c> is
/// null and <see cref="PdfCatalog.GetViewerPreferences"/> — the only public door — is unreachable
/// for a checker that has walked to the catalog <i>dictionary</i> by hand:
/// <c>Plugins.Conformance.PdfUaChecker</c> (UA04), <c>Accessibility</c>'s deep checker and its
/// PDF/UA-2 structure checker. Sharing the read follows the precedent CLAUDE.md records for
/// <c>/P</c>: where the two accessibility halves compute a check independently and merge the
/// results, the one step that could make them disagree is shared rather than mirrored.</para>
///
/// <para><b>The tri-state matters.</b> "No <c>/ViewerPreferences</c> dictionary at all" and "a
/// dictionary with no <c>/DisplayDocTitle</c>" are <i>different findings</i> with different
/// messages in the UA-2 checker, so an accessor that answered only <c>null</c> would collapse them.
/// <see cref="PdfViewerPreferences.Dictionary"/>
/// keeps them apart, and the pair of tests below is what stops that being quietly lost.</para>
/// </remarks>
public sealed class ViewerPreferencesReaderTests
{
    private static readonly Dictionary<int, PdfObject> Objects = new();

    private static PdfObject Resolve(PdfObject o)
        => o is PdfReference r && Objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject t) ? t : o;

    private static PdfReference Indirect(PdfObject value)
    {
        int number = Objects.Count + 500;
        Objects[number] = value;
        return new PdfReference(number, 0);
    }

    private static PdfDictionary Catalog(PdfObject viewerPreferences)
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        if (viewerPreferences != null) catalog.Set(PdfName.ViewerPreferences, viewerPreferences);
        return catalog;
    }

    private static PdfDictionary Preferences(PdfObject displayDocTitle)
    {
        var prefs = new PdfDictionary();
        if (displayDocTitle != null) prefs.Set(PdfName.DisplayDocTitle, displayDocTitle);
        return prefs;
    }

    // ── the tri-state ────────────────────────────────────────────────────────────────

    /// <summary>A catalog with no <c>/ViewerPreferences</c>: no dictionary, and no preference.</summary>
    [Fact]
    public void NoViewerPreferencesAtAll_HasANullDictionary()
    {
        var prefs = PdfViewerPreferences.Read(Catalog(null), Resolve);

        Assert.NotNull(prefs);                 // the wrapper is never null
        Assert.Null(prefs.Dictionary);         // "there is no preferences dictionary"
        Assert.Null(prefs.DisplayDocTitle);
    }

    /// <summary>
    /// A <c>/ViewerPreferences</c> that exists but says nothing about the title: the dictionary is
    /// there and the preference is absent. Collapsing this into the case above would merge two
    /// distinct conformance findings into one.
    /// </summary>
    [Fact]
    public void APreferencesDictionaryWithoutTheEntry_IsNotTheSameAsNoDictionary()
    {
        var prefs = PdfViewerPreferences.Read(Catalog(Preferences(null)), Resolve);

        Assert.NotNull(prefs.Dictionary);      // "there IS a preferences dictionary"
        Assert.Null(prefs.DisplayDocTitle);    // "…and it does not set this"
    }

    [Fact]
    public void ANullCatalog_HasANullDictionary_AndDoesNotThrow()
    {
        var prefs = PdfViewerPreferences.Read(null, Resolve);

        Assert.Null(prefs.Dictionary);
        Assert.Null(prefs.DisplayDocTitle);
    }

    // ── the value ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheDeclaredValueIsRead(bool declared)
    {
        var prefs = PdfViewerPreferences.Read(
            Catalog(Preferences(new PdfBoolean(declared))), Resolve);

        Assert.Equal(declared, prefs.DisplayDocTitle);
    }

    // ── §7.3.10: either level may be indirect ───────────────────────────────────────

    /// <summary>
    /// The <c>/ViewerPreferences</c> dictionary itself written as a reference, followed through the
    /// caller's delegate — the whole reason this overload exists.
    /// </summary>
    [Fact]
    public void AnIndirectPreferencesDictionary_IsFollowed()
    {
        var prefs = PdfViewerPreferences.Read(
            Catalog(Indirect(Preferences(PdfBoolean.True))), Resolve);

        Assert.NotNull(prefs.Dictionary);
        Assert.True(prefs.DisplayDocTitle);
    }

    /// <summary>And an indirect entry inside it.</summary>
    [Fact]
    public void AnIndirectEntry_IsFollowed()
    {
        var prefs = PdfViewerPreferences.Read(
            Catalog(Preferences(Indirect(PdfBoolean.True))), Resolve);

        Assert.True(prefs.DisplayDocTitle);
    }

    /// <summary>
    /// The control, and the honest negative: with <b>no</b> resolver the indirect forms cannot be
    /// followed, so they read as absent. That is what a caller holding only direct objects gets,
    /// and it is what shows the tests above are pinning the resolution rather than the fixture.
    /// </summary>
    [Fact]
    public void WithoutAResolver_AnIndirectPreferencesDictionary_ReadsAsAbsent()
    {
        var prefs = PdfViewerPreferences.Read(
            Catalog(Indirect(Preferences(PdfBoolean.True))), resolve: null);

        Assert.Null(prefs.Dictionary);
        Assert.Null(prefs.DisplayDocTitle);
    }

    /// <summary>A direct dictionary still reads with no resolver at all.</summary>
    [Fact]
    public void WithoutAResolver_ADirectPreferencesDictionary_StillReads()
    {
        var prefs = PdfViewerPreferences.Read(
            Catalog(Preferences(PdfBoolean.True)), resolve: null);

        Assert.NotNull(prefs.Dictionary);
        Assert.True(prefs.DisplayDocTitle);
    }

    /// <summary>
    /// <c>/NumCopies</c> is an integer (Table 150): a real reads as absent, and one past
    /// <see cref="int.MaxValue"/> saturates rather than keeping its low 32 bits.
    /// </summary>
    [Theory]
    [InlineData(3.0, true, 3)]
    [InlineData(4294967297.0, true, int.MaxValue)]
    [InlineData(2.5, false, 0)]
    public void NumCopies_IsAnIntegerOrAbsent(double written, bool present, int expected)
    {
        var prefs = new PdfDictionary();
        prefs.Set(PdfName.NumCopies, written % 1 == 0 ? new PdfNumber((long)written) : new PdfNumber(written));

        int? copies = PdfViewerPreferences.Read(Catalog(prefs), resolve: null).NumCopies;

        Assert.Equal(present ? expected : (int?)null, copies);
    }
}
