// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Document;

public class PdfTransitionTests
{
    [Fact]
    public void AllKnownKeys_ParseCorrectly()
    {
        var dict = new PdfDictionary();
        dict[new PdfName("S")]  = new PdfName("Blinds");
        dict[new PdfName("D")]  = new PdfNumber(2.5);
        dict[new PdfName("Dm")] = new PdfName("V");
        dict[new PdfName("M")]  = new PdfName("O");
        dict[new PdfName("Di")] = new PdfNumber(270);
        dict[new PdfName("SS")] = new PdfNumber(1.3);
        dict[new PdfName("B")]  = PdfBoolean.True;

        var t = Invoke(dict);

        Assert.Equal("Blinds", t.Style);
        Assert.Equal(2.5,      t.Duration);
        Assert.Equal("V",      t.Dimension);
        Assert.Equal("O",      t.Motion);
        Assert.Equal(270,      t.Direction);
        Assert.Equal(1.3,      t.Scale);
        Assert.True(t.IsRectangular);
        Assert.Same(dict, t.Dictionary);
    }

    [Fact]
    public void EmptyDict_ReturnsSpecDefaults()
    {
        var t = Invoke(new PdfDictionary());

        Assert.Equal("R", t.Style);    // Replace is the spec default
        Assert.Equal(1.0, t.Duration); // 1 second default
        Assert.Equal("H", t.Dimension);
        Assert.Equal("I", t.Motion);
        Assert.Null(t.Direction);      // absent → null
        Assert.Equal(1.0, t.Scale);
        Assert.False(t.IsRectangular);
    }

    /// <summary>
    /// Wave 17 R1.8: a <c>/Di</c> past <c>int</c> saturates (<see cref="PdfNumber.IntValue"/>) where
    /// the <c>(int)LongValue</c> cast wrapped 2^32 + 270 to 270 — a legal-looking direction from a
    /// number that is none.
    /// </summary>
    [Fact]
    public void DiPastInt_SaturatesInsteadOfWrappingToADirection()
    {
        var dict = new PdfDictionary();
        dict[new PdfName("Di")] = new PdfNumber(4294967566.0);   // 2^32 + 270
        Assert.Equal(int.MaxValue, Invoke(dict).Direction);
    }

    [Fact]
    public void DiNameNone_ReturnsNullDirection()
    {
        var dict = new PdfDictionary();
        dict[new PdfName("Di")] = new PdfName("None");
        var t = Invoke(dict);
        Assert.Null(t.Direction);
    }

    // Helper: PdfTransition has an internal ctor.
    private static PdfTransition Invoke(PdfDictionary dict)
    {
        var ctor = typeof(PdfTransition).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(PdfDictionary) },
            modifiers: null);
        return (PdfTransition)ctor!.Invoke(new object[] { dict });
    }
}
