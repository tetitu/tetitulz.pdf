// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// <see cref="PdfDictionary.DeepClone"/> copies nested dictionaries and arrays rather than sharing
/// them — which is the whole difference between it and <see cref="PdfDictionary.CloneShallow"/>.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over CLAUDE.md's shared decision points
/// changed the depth gate from <c>maxDepth &lt;= 0</c> to <c>maxDepth &lt;= 64</c> — so every
/// default-depth <c>DeepClone</c> silently degraded to a shallow one — and <b>nothing reddened</b>,
/// in Core's suite or in Base's, where the production caller lives.</para>
///
/// <para><b>What a silent downgrade costs.</b> The caller asked for a copy precisely because it
/// intends to modify it: a page-content append that edits the cloned page's <c>/Resources</c> would
/// be editing the original document's, so the revision it writes and the document it claims to leave
/// alone change together. Nothing throws, and the output is a valid PDF that says something the
/// caller never asked it to.</para>
///
/// <para>The depth limit is the other half of the contract, and it is deliberately a <i>sharing</i>
/// fallback rather than a refusal: it bounds a self-referential graph instead of overflowing the
/// stack, so a cycle is clipped rather than fatal.</para>
/// </remarks>
public sealed class PdfDictionaryDeepCloneTests
{
    [Fact]
    public void ANestedDictionaryIsCopied_NotShared()
    {
        var nested = new PdfDictionary();
        nested.Set(PdfName.Type, PdfName.Page);

        var source = new PdfDictionary();
        source.Set(PdfName.Resources, nested);

        var clone = PdfDictionary.DeepClone(source);
        var clonedNested = Assert.IsType<PdfDictionary>(clone[PdfName.Resources]);

        Assert.NotSame(nested, clonedNested);

        // Editing the clone must not reach the original — the reason a caller asked for a copy.
        clonedNested.Set(PdfName.Type, PdfName.Pages);
        Assert.Equal("Page", ((PdfName)nested[PdfName.Type]).Value);
    }

    /// <summary>A nested array is copied too, and so are the dictionaries inside it.</summary>
    [Fact]
    public void ANestedArrayAndItsDictionariesAreCopied()
    {
        var inner = new PdfDictionary();
        inner.Set(PdfName.Type, PdfName.Page);

        var array = new PdfArray();
        array.Add(inner);

        var source = new PdfDictionary();
        source.Set(PdfName.Kids, array);

        var clone = PdfDictionary.DeepClone(source);
        var clonedArray = Assert.IsType<PdfArray>(clone[PdfName.Kids]);

        Assert.NotSame(array, clonedArray);
        Assert.NotSame(inner, clonedArray[0]);
    }

    /// <summary>
    /// The contrast that gives the tests above their meaning: <see cref="PdfDictionary.CloneShallow"/>
    /// shares the very same nested dictionary.
    /// </summary>
    [Fact]
    public void CloneShallow_SharesTheNestedDictionary()
    {
        var nested = new PdfDictionary();
        var source = new PdfDictionary();
        source.Set(PdfName.Resources, nested);

        var clone = PdfDictionary.CloneShallow(source);

        Assert.Same(nested, clone[PdfName.Resources]);
    }

    /// <summary>
    /// At <c>maxDepth 0</c> the caller has asked for no recursion, and gets the shallow behaviour —
    /// the boundary the depth gate actually governs.
    /// </summary>
    [Fact]
    public void AtZeroDepth_TheNestedDictionaryIsSharedAgain()
    {
        var nested = new PdfDictionary();
        var source = new PdfDictionary();
        source.Set(PdfName.Resources, nested);

        var clone = PdfDictionary.DeepClone(source, maxDepth: 0);

        Assert.Same(nested, clone[PdfName.Resources]);
    }

    /// <summary>
    /// A self-referential dictionary is clipped by the depth limit rather than overflowing the
    /// stack — the limit is a sharing fallback, not a refusal.
    /// </summary>
    [Fact]
    public void ASelfReferentialDictionary_IsBoundedByTheDepthLimit()
    {
        var cycle = new PdfDictionary();
        cycle.Set(PdfName.Parent, cycle);

        PdfDictionary clone = PdfDictionary.DeepClone(cycle);

        Assert.NotSame(cycle, clone);
        Assert.NotNull(clone[PdfName.Parent]);
    }

    [Fact]
    public void ANullSource_IsRefusedByName()
        => Assert.Throws<ArgumentNullException>(() => PdfDictionary.DeepClone(null));
}
