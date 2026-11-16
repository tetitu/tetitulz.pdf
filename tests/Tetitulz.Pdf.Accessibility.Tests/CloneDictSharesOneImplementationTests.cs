// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>PdfStructureTreeEditor.CloneDict</c> is a forwarder to
/// <c>Core.Objects.PdfDictionary.CloneShallow</c>, not a sixteenth copy of it.
/// </summary>
/// <remarks>
/// <para>The open-coded body built a bare <c>PdfDictionary</c> and copied entries, which
/// <b>flattens a stream</b>: <c>CloneShallow</c> goes through <c>NewLike</c>, which tests
/// <c>source is PdfStream</c> first and carries <c>RawData</c> and <c>StreamOffset</c> across,
/// while the copy produced a plain dictionary holding the stream's keys — a now-lying
/// <c>/Length</c> among them — that <c>PdfWriter</c> emits with no <c>stream</c>/<c>endstream</c>
/// at all. Every route into this method arrives through an <c>as PdfDictionary</c> cast, and a
/// <c>PdfStream</c> satisfies it.</para>
///
/// <para><b>The tell was one key cloned two ways in one assembly:</b> <c>PdfAccessibility</c>
/// clones <c>/MarkInfo</c> through <c>CloneShallow</c> and <c>PdfArtifactMarker</c> cloned the same
/// <c>/MarkInfo</c> through <c>CloneDict</c>.</para>
/// </remarks>
public sealed class CloneDictSharesOneImplementationTests
{
    /// <summary>
    /// A stream source clones as a stream, with its data intact. The open-coded body returned a
    /// plain dictionary and dropped the bytes.
    /// </summary>
    [Fact]
    public void AStreamSourceClonesAsAStreamWithItsDataIntact()
    {
        var source = new PdfStream();
        source.Set(PdfName.Type, PdfName.Metadata);
        source.RawData = new byte[] { 1, 2, 3, 4 };

        PdfDictionary clone = PdfStructureTreeEditor.CloneDict(source);

        var asStream = Assert.IsAssignableFrom<PdfStream>(clone);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, asStream.RawData);
        Assert.Equal(PdfName.Metadata, clone[PdfName.Type]);
    }

    /// <summary>
    /// The null tolerance is stated locally rather than inherited: <c>CloneShallow</c> throws on
    /// null by contract, and eighteen call sites here rely on getting an empty dictionary instead.
    /// </summary>
    [Fact]
    public void ANullSourceStillYieldsAnEmptyDictionary()
    {
        PdfDictionary clone = PdfStructureTreeEditor.CloneDict(null);

        Assert.NotNull(clone);
        Assert.IsNotType<PdfStream>(clone);
        Assert.Empty(clone);
    }

    /// <summary>
    /// The ordinary case is unchanged — entries are copied and the original is not aliased.
    /// </summary>
    [Fact]
    public void APlainDictionaryClonesItsEntriesWithoutAliasingTheSource()
    {
        var source = new PdfDictionary();
        source.Set(PdfName.Type, PdfName.Catalog);

        PdfDictionary clone = PdfStructureTreeEditor.CloneDict(source);
        clone.Set(PdfName.Lang, new PdfString("en"));

        Assert.Equal(PdfName.Catalog, clone[PdfName.Type]);
        Assert.Null(source[PdfName.Lang]);   // the clone is not the source
    }
}
