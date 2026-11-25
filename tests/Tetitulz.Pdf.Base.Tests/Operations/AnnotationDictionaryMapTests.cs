// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="AnnotationDictionaryMap"/>'s own decisions — the one mapping the FDF writer, the FDF
/// reader and <c>PdfAnnotationWriter</c> all share.
/// </summary>
/// <remarks>
/// <para><b>Why a round trip could not reach any of this.</b> The existing suites write an exchange
/// file and read it back, and both halves of that trip go through <i>this</i> table: a key spelled
/// one way going out and the same way coming back round-trips perfectly while being wrong for every
/// other reader. CLAUDE.md names the shape — a relative assertion cannot see a change applied to
/// both of the things it relates — and a mutation sweep found it here in the sharpest possible
/// form: <b>all nine decisions survived</b> against suites that exercise the mapping constantly.</para>
/// <para>So every test below asserts the <b>dictionary</b>, which is what another implementation
/// sees, rather than the value that came back through the same code.</para>
/// </remarks>
public class AnnotationDictionaryMapTests
{
    private static FormDataAnnotation Annotation(string subtype = PdfAnnotationSubtypes.Highlight)
        => new FormDataAnnotation(subtype);

    private static PdfObject Entry(PdfDictionary d, PdfName key) => d[key];

    private static double[] Numbers(PdfDictionary d, PdfName key) =>
        ((PdfArray)d[key]).Cast<PdfObject>().Select(o => ((PdfNumber)o).DoubleValue).ToArray();

    // ================================================================
    //  Writing
    // ================================================================

    /// <summary>
    /// <c>/Page</c> belongs to an exchange file and not to a document: there the page carries the
    /// annotation and <c>/P</c> points the other way.
    /// </summary>
    /// <remarks>
    /// Both directions matter and the caller says which — writing <c>/Page</c> into a document's
    /// annotation is wrong, and omitting it from an FDF loses the placement entirely.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePageEntryIsWrittenOnlyForAnExchangeFile(bool includePage)
    {
        var a = Annotation();
        a.PageIndex = 3;

        PdfDictionary d = AnnotationDictionaryMap.ToDictionary(a, includePage);

        Assert.Equal(includePage, d[PdfName.Page] != null);
        if (includePage) Assert.Equal(3, ((PdfNumber)d[PdfName.Page]).IntValue);
    }

    /// <summary>
    /// §12.5.2: a rectangle is written with its corners ordered, whatever order the model holds
    /// them in — the model keeps what the file said, and what leaves here is what a reader should
    /// be given.
    /// </summary>
    [Fact]
    public void TheRectangleIsNormalisedOnTheWayOut()
    {
        var a = Annotation();
        a.Rect = new double[] { 300, 400, 100, 200 };   // corners backwards

        Assert.Equal(new double[] { 100, 200, 300, 400 },
                     Numbers(AnnotationDictionaryMap.ToDictionary(a, includePage: false), PdfName.Rect));
    }

    /// <summary>
    /// <c>/L</c> is two points, so a line that is not four numbers has no spelling — and writing it
    /// anyway hands the next reader a malformed entry.
    /// </summary>
    [Fact]
    public void AShortLineIsNotWritten()
    {
        var a = new FormDataAnnotation(PdfAnnotationSubtypes.Line) { Line = new double[] { 1, 2 } };

        Assert.Null(Entry(AnnotationDictionaryMap.ToDictionary(a, includePage: false), PdfName.L));
    }

    /// <summary>A null stroke is skipped rather than written as a null array entry.</summary>
    [Fact]
    public void ANullInkStrokeIsSkipped()
    {
        var a = new FormDataAnnotation(PdfAnnotationSubtypes.Ink);
        a.InkList.Add(null);
        a.InkList.Add(new double[] { 1, 2, 3, 4 });

        var ink = (PdfArray)AnnotationDictionaryMap.ToDictionary(a, includePage: false)[PdfName.InkList];

        Assert.Single(ink);
        Assert.Equal(new double[] { 1, 2, 3, 4 },
                     ((PdfArray)ink[0]).Cast<PdfObject>().Select(o => ((PdfNumber)o).DoubleValue));
    }

    /// <summary>
    /// An integer is serialised <b>as an integer</b>, which is not the same as serialising its
    /// value: past 2^53 a <c>double</c> cannot hold one, so rendering it as a real silently changes
    /// the number an <c>Extras</c> entry carries back into the next file.
    /// </summary>
    /// <remarks>
    /// <para>A small integer cannot show this — <c>90</c> renders as "90" whichever way it is
    /// written, which is why the mutation of this line first survived a test that used one.</para>
    /// <para><b>A large one shows it twice over.</b> The round-trip format a real is written with
    /// switches to scientific notation past a point, so <c>1000000000000000</c> comes back as
    /// <c>1E+15</c> — and §7.3.3 has no exponent form, so that is not merely a different spelling
    /// of the number but a token no PDF reader accepts.</para>
    /// </remarks>
    [Theory]
    [InlineData(90L, "90")]
    [InlineData(10000000000000000L, "10000000000000000")]   // 10^16, exactly representable
    public void AnIntegerExtraKeepsItsExactValue(long value, string expected)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Highlight));
        dict.Set(new PdfName("Rotate"), new PdfNumber(value));

        FormDataAnnotation a = AnnotationDictionaryMap.FromDictionary(dict, o => o);

        Assert.Equal(expected, a.Extras["Rotate"]);
    }

    // ================================================================
    //  Reading
    // ================================================================

    /// <summary>
    /// A dictionary whose <c>/Subtype</c> is an empty name yields no annotation: the subtype is the
    /// one thing the model cannot invent, and <c>/Subtype /</c> would become an annotation no
    /// reader matches.
    /// </summary>
    [Fact]
    public void AnEmptySubtypeYieldsNoAnnotation()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(string.Empty));

        Assert.Null(AnnotationDictionaryMap.FromDictionary(dict, o => o));
    }

    /// <summary>
    /// <c>/BS</c> and its <c>/W</c> are followed through indirect references, which FDF files write
    /// as often as not. Read raw, the width comes back absent — indistinguishable from a border
    /// the file never set.
    /// </summary>
    [Fact]
    public void TheBorderWidthIsFollowedThroughAnIndirectReference()
    {
        var bs = new PdfDictionary();
        bs.Set(PdfName.W, new PdfReference(9, 0));

        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
        dict.Set(PdfName.BS, new PdfReference(8, 0));

        FormDataAnnotation a = AnnotationDictionaryMap.FromDictionary(dict, o =>
            o is PdfReference r
                ? (r.ReferenceObjectNumber == 8 ? bs : (PdfObject)new PdfNumber(2.5))
                : o);

        Assert.Equal(2.5, a.BorderWidth);
    }

    /// <summary>
    /// The <b>entries inside</b> a geometry array are resolved too, not just the array. A producer
    /// that writes one coordinate indirectly would otherwise lose the whole rectangle.
    /// </summary>
    [Fact]
    public void ANumberInsideAGeometryArrayIsResolved()
    {
        var rect = new PdfArray();
        rect.Add(new PdfNumber(1));
        rect.Add(new PdfNumber(2));
        rect.Add(new PdfReference(7, 0));
        rect.Add(new PdfNumber(4));

        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
        dict.Set(PdfName.Rect, rect);

        FormDataAnnotation a = AnnotationDictionaryMap.FromDictionary(dict, o =>
            o is PdfReference ? new PdfNumber(3) : o);

        Assert.Equal(new double[] { 1, 2, 3, 4 }, a.Rect);
    }

    /// <summary>
    /// A key this model understands does not also land in <see cref="FormDataAnnotation.Extras"/>.
    /// Extras exists so an unrecognised key survives a read-modify-write; duplicating a known one
    /// there means the next write emits it twice, and §7.3.7 leaves a repeated key undefined.
    /// </summary>
    [Fact]
    public void AKnownKeyDoesNotAlsoAppearInExtras()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Highlight));
        dict.Set(PdfName.Contents, new PdfString("note"));
        dict.Set(new PdfName("RC"), new PdfString("<p>rich</p>"));

        FormDataAnnotation a = AnnotationDictionaryMap.FromDictionary(dict, o => o);

        Assert.Equal("note", a.Contents);
        Assert.True(a.Extras.ContainsKey("RC"), "an unrecognised key must survive");
        Assert.False(a.Extras.ContainsKey("Contents"), "a known key must not be duplicated into Extras");
    }

    // ================================================================
    //  Unrecognised entries written back
    // ================================================================

    private static PdfDictionary Written(PdfDictionary read, Func<PdfObject, PdfObject>? resolve = null)
        => AnnotationDictionaryMap.ToDictionary(AnnotationDictionaryMap.FromDictionary(read, resolve ?? (o => o)), includePage: true);

    /// <summary>
    /// An entry made only of plain values is written back as it was read — rich text with the
    /// characters a literal string must escape, a border effect, line endings, an intent, an integer
    /// kept an integer, and a difference rectangle the file wrote indirectly — each a fresh copy.
    /// </summary>
    [Fact]
    public void APlainUnrecognisedEntry_IsWrittenBackAsItWasRead()
    {
        byte[] rich = System.Text.Encoding.ASCII.GetBytes("<p>a (b) \\c</p>");
        var be = new PdfDictionary();
        be.Set(PdfName.S, new PdfName("C"));
        be.Set(new PdfName("I"), new PdfNumber(2));
        var le = new PdfArray { new PdfName("OpenArrow"), new PdfName("None") };
        var rd = new PdfArray { new PdfNumber(1.5), new PdfNumber(1.5), new PdfNumber(1.5), new PdfNumber(1.5) };

        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Line));
        dict.Set(new PdfName("RC"), new PdfString(rich));
        dict.Set(PdfName.BE, be);
        dict.Set(PdfName.LE, le);
        dict.Set(new PdfName("IT"), new PdfName("LineArrow"));
        dict.Set(new PdfName("Rotate"), new PdfNumber(90));
        dict.Set(new PdfName("RD"), new PdfReference(12, 0));

        PdfDictionary written = Written(dict, o => o is PdfReference r && r.ReferenceObjectNumber == 12 ? rd : o);

        Assert.Equal(rich, ((PdfString)written[new PdfName("RC")]).RawBytes);
        Assert.Equal("C", ((PdfName)((PdfDictionary)written[PdfName.BE])[PdfName.S]).Value);
        Assert.Equal(new[] { "OpenArrow", "None" }, ((PdfArray)written[PdfName.LE]).Cast<PdfName>().Select(n => n.Value));
        Assert.Equal("LineArrow", ((PdfName)written[new PdfName("IT")]).Value);
        Assert.True(((PdfNumber)written[new PdfName("Rotate")]).IsInteger);
        Assert.Equal(new[] { 1.5, 1.5, 1.5, 1.5 }, Numbers(written, new PdfName("RD")));
        Assert.NotSame(be, written[PdfName.BE]);   // a copy, never the file's own object
    }

    /// <summary>
    /// Nothing that runs, draws from a stream, or points into the document the annotation came from
    /// is written back — though each is still readable in <see cref="FormDataAnnotation.Extras"/>. The
    /// control is a plain entry beside them, which is.
    /// </summary>
    [Fact]
    public void ActionsAppearancesAndLinksIntoTheSourceDocument_AreNotWrittenBack()
    {
        var js = new PdfDictionary();
        js.Set(PdfName.S, new PdfName("JavaScript"));
        js.Set(new PdfName("JS"), new PdfString("app.alert(1)"));

        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
        dict.Set(PdfName.A, js);
        dict.Set(new PdfName("AA"), new PdfDictionary());
        dict.Set(new PdfName("PA"), js);
        dict.Set(PdfName.AP, new PdfDictionary());
        dict.Set(new PdfName("AS"), new PdfName("On"));
        dict.Set(new PdfName("Popup"), new PdfName("X"));
        dict.Set(new PdfName("IRT"), new PdfName("X"));
        dict.Set(new PdfName("StructParent"), new PdfNumber(3));
        dict.Set(new PdfName("Dest"), new PdfName("Chapter1"));
        dict.Set(new PdfName("IT"), new PdfName("SquareCloud"));

        FormDataAnnotation a = AnnotationDictionaryMap.FromDictionary(dict, o => o);
        PdfDictionary written = AnnotationDictionaryMap.ToDictionary(a, includePage: true);

        foreach (string key in new[] { "A", "AA", "PA", "AP", "AS", "Popup", "IRT", "StructParent", "Dest" })
        {
            Assert.Null(written[new PdfName(key)]);
            Assert.True(a.Extras.ContainsKey(key), $"/{key} is still readable");
        }
        Assert.Equal("SquareCloud", ((PdfName)written[new PdfName("IT")]).Value);
    }

    /// <summary>
    /// A stream, a reference that resolves to nothing, or a stream anywhere inside an array makes the
    /// whole entry not written — never half of it — while a plain sibling entry is.
    /// </summary>
    [Fact]
    public void AnEntryHoldingAStreamOrAnUnresolvedReference_IsNotWrittenBackAtAll()
    {
        var stream = new PdfStream { RawData = System.Text.Encoding.ASCII.GetBytes("<p>x</p>") };
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
        dict.Set(new PdfName("RC"), new PdfReference(5, 0));
        dict.Set(new PdfName("Gone"), new PdfReference(6, 0));
        dict.Set(new PdfName("Mixed"), new PdfArray { new PdfNumber(1), new PdfReference(5, 0) });
        dict.Set(new PdfName("Kept"), new PdfArray { new PdfNumber(1), new PdfNumber(2) });

        PdfDictionary written = Written(dict, o => o is PdfReference r ? (r.ReferenceObjectNumber == 5 ? stream : null) : o);

        Assert.Null(written[new PdfName("RC")]);
        Assert.Null(written[new PdfName("Gone")]);
        Assert.Null(written[new PdfName("Mixed")]);
        Assert.Equal(new double[] { 1, 2 }, Numbers(written, new PdfName("Kept")));
    }

    /// <summary>
    /// A hostile cycle — a dictionary whose entry refers back to itself — stops at
    /// <see cref="AnnotationDictionaryMap.MaxCarriedDepth"/> and is not written, where following it
    /// would never end; the same chain kept shorter than the bound is written.
    /// </summary>
    [Fact]
    public void AHostileCycleOfReferences_StopsAtTheDepthBoundAndIsNotWritten()
    {
        var loop = new PdfDictionary();
        loop.Set(new PdfName("Next"), new PdfReference(7, 0));
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
        dict.Set(new PdfName("Loop"), new PdfReference(7, 0));

        PdfDictionary written = Written(dict, o => o is PdfReference ? loop : o);
        Assert.Null(written[new PdfName("Loop")]);

        // The control: a chain as deep as the bound allows, ending in a number, is carried whole.
        PdfObject chain = new PdfNumber(1);
        for (int i = 0; i < AnnotationDictionaryMap.MaxCarriedDepth; i++)
        {
            var link = new PdfDictionary();
            link.Set(new PdfName("Next"), chain);
            chain = link;
        }
        var shallow = new PdfDictionary();
        shallow.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
        shallow.Set(new PdfName("Chain"), chain);
        Assert.NotNull(Written(shallow)[new PdfName("Chain")]);
    }

    /// <summary>
    /// Each bound on its own: a chain of plain dictionaries one level past
    /// <see cref="AnnotationDictionaryMap.MaxCarriedDepth"/> is not written, and an array one object past
    /// <see cref="AnnotationDictionaryMap.MaxCarriedObjects"/> is not; one just inside each is (the
    /// controls). A cycle cannot tell the two apart, since either stops it.
    /// </summary>
    [Fact]
    public void AValuePastEitherBound_IsNotWritten_AndOneJustInsideIs()
    {
        PdfObject Chain(int links)
        {
            PdfObject value = new PdfNumber(1);
            for (int i = 0; i < links; i++)
            {
                var link = new PdfDictionary();
                link.Set(new PdfName("Next"), value);
                value = link;
            }
            return value;
        }
        PdfArray Wide(int count)
        {
            var array = new PdfArray();
            for (int i = 0; i < count; i++) array.Add(new PdfNumber(i));
            return array;
        }
        PdfObject? WrittenValue(PdfObject value)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Subtype, new PdfName(PdfAnnotationSubtypes.Square));
            dict.Set(new PdfName("V"), value);
            return Written(dict)[new PdfName("V")];
        }

        Assert.Null(WrittenValue(Chain(AnnotationDictionaryMap.MaxCarriedDepth + 1)));
        Assert.NotNull(WrittenValue(Chain(AnnotationDictionaryMap.MaxCarriedDepth)));
        // The array itself is one object; its items are the rest.
        Assert.Null(WrittenValue(Wide(AnnotationDictionaryMap.MaxCarriedObjects)));
        Assert.NotNull(WrittenValue(Wide(AnnotationDictionaryMap.MaxCarriedObjects - 1)));
    }
}
