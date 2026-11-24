// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// A document that already uses object number <c>int.MaxValue</c> leaves no number for a new
/// object (two public-corpus files define object 2147483647). "Highest plus one" overflows and
/// reaches <see cref="IncrementalUpdateWriter.WriteUpdate"/> as a negative <c>/Size</c>, or the
/// caller passes the ceiling itself; the writer refuses before writing, naming the cause and the
/// remedy, not only the overflowed value. An ordinary <c>/Size</c> mismatch keeps its own
/// refusal, without the cause.
/// </summary>
public class ObjectNumberCeilingTests
{
    private static readonly byte[] Original = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n");

    private static Exception Write(int objectNumber, int size)
    {
        using var output = new MemoryStream();
        return Record.Exception(() => IncrementalUpdateWriter.WriteUpdate(
            new MemoryStream(Original), output,
            new List<IndirectObject> { new IndirectObject(objectNumber, 0, new PdfDictionary()) },
            new PdfDictionary(), 0, size));
    }

    [Theory]
    [InlineData(int.MaxValue)]        // the ceiling passed as /Size: not strictly greater than the object
    [InlineData(int.MinValue)]        // the caller's "highest plus one", overflowed
    public void TheLargestObjectNumber_IsRefusedNamingTheCause(int size)
    {
        var ex = Assert.IsType<InvalidOperationException>(Write(int.MaxValue, size));

        Assert.Contains("2147483647", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no number is free for a new object", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rewrite the document", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape the overflow actually produces: the caller numbered its new object with "highest plus
    /// one" wrapped around, so neither the object nor the /Size is the ceiling — both are negative.
    /// The negative /Size alone names the cause.
    /// </summary>
    [Fact]
    public void AnOverflowedNumbering_IsRefusedNamingTheCause()
    {
        var ex = Assert.IsType<InvalidOperationException>(Write(int.MinValue, int.MinValue + 1));

        Assert.Contains("no number is free for a new object", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: an ordinary mismatch is refused as before, without the cause clause.</summary>
    [Fact]
    public void AnOrdinarySizeMismatch_IsRefusedWithoutTheCause()
    {
        var ex = Assert.IsType<InvalidOperationException>(Write(5, 5));

        Assert.Contains("Trailer /Size (5)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no number is free", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>And a /Size that covers the object writes: the guard is about the ceiling, not about large numbers.</summary>
    [Fact]
    public void ALargeNumberBelowTheCeiling_Writes()
    {
        Assert.Null(Write(int.MaxValue - 1, int.MaxValue));
    }
}
