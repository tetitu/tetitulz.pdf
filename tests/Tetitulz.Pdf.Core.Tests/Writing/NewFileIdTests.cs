// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfTrailerBuilder.NewFileId"/> — the §14.4 file identifier every from-scratch writer
/// in this tree puts in its trailer.
/// </summary>
/// <remarks>
/// <para><b>Written because the shape had no coverage.</b> Four writers built this themselves —
/// <c>PdfDocumentBuilder</c>, <c>PdfNup</c>, <c>PdfTaggedDocumentBuilder</c> and the
/// <c>PdfMerger</c> plugin — and reducing the array to a <i>single</i> element left Core, Base and
/// PageEdits entirely green (2 111, 3 040 and 151, full counts). §14.4 says <c>/ID</c> is "an array
/// of two byte strings"; a one-element array is malformed, and nothing noticed.</para>
///
/// <para>Merging the four copies is what made one test worth writing: before it, the same
/// assertions would have had to be made four times or would have covered one writer and left three.</para>
/// </remarks>
public class NewFileIdTests
{
    [Fact]
    public void TheIdentifierIsAnArrayOfTwoByteStrings()
    {
        PdfArray id = PdfTrailerBuilder.NewFileId();

        Assert.Equal(2, id.Count);
        Assert.All(Enumerable.Range(0, id.Count), i => Assert.IsType<PdfString>(id[i]));
    }

    /// <summary>
    /// Both elements are the same value, because a document written from scratch has no earlier
    /// version: §14.4's <c>/ID[0]</c> identifies the original and <c>/ID[1]</c> the current one, and
    /// for a new file those are the same document.
    /// </summary>
    [Fact]
    public void BothElementsAreTheSameValueForANewDocument()
    {
        PdfArray id = PdfTrailerBuilder.NewFileId();

        byte[] first = ((PdfString)id[0]).RawBytes;
        byte[] second = ((PdfString)id[1]).RawBytes;

        Assert.Equal(first, second);
    }

    [Fact]
    public void EachElementIsSixteenBytes()
    {
        PdfArray id = PdfTrailerBuilder.NewFileId();

        Assert.Equal(PdfTrailerBuilder.FileIdLengthBytes, ((PdfString)id[0]).RawBytes.Length);
        Assert.Equal(16, PdfTrailerBuilder.FileIdLengthBytes);
    }

    /// <summary>
    /// <b>Two documents must not share an identifier.</b> That is the entire purpose of §14.4 — a
    /// reader tells one file from another by it — so a generator returning a fixed value would
    /// satisfy every assertion above and defeat the feature.
    /// </summary>
    /// <remarks>
    /// Asserts distinctness, not randomness: a counter would pass this too, and would be a perfectly
    /// good identifier. What must not pass is a constant.
    /// </remarks>
    [Fact]
    public void TwoIdentifiersAreDifferent()
    {
        byte[] a = ((PdfString)PdfTrailerBuilder.NewFileId()[0]).RawBytes;
        byte[] b = ((PdfString)PdfTrailerBuilder.NewFileId()[0]).RawBytes;

        Assert.NotEqual(a, b);
    }
}
