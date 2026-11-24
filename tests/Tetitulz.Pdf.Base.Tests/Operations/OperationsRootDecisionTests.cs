// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions in the <c>Operations</c> root's shared types that the sweep found nothing in Base
/// asserting — most of them reached mainly from the Accessibility and Conformance assemblies.
/// </summary>
public class OperationsRootDecisionTests
{
    private static byte[] OnePage() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
        .Finish();

    /// <summary>A stream that accepts <paramref name="limit"/> bytes in all and refuses any write that would pass it.</summary>
    private sealed class CappedStream : MemoryStream
    {
        private readonly long _limit;
        internal CappedStream(long limit) => _limit = limit;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > _limit) throw new IOException("the destination is full");
            base.Write(buffer, offset, count);
        }
    }

    private static (byte[] Original, PdfDocument Doc, PdfObjectIndex Index) Open(byte[] pdf = null)
    {
        pdf ??= OnePage();
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        return (pdf, doc, PdfObjectIndex.Build(doc));
    }

    private static Dictionary<int, PdfObject> Edit(PdfDocument doc)
        => new Dictionary<int, PdfObject> { [1] = PdfDictionary.CloneShallow(doc.Catalog.Dictionary) };

    // ---------------------------------------------------------------- VerifiedWrite

    /// <summary>A revision that reaches the output is a success, and the bytes counted are the stream's.</summary>
    [Fact]
    public void VerifiedWrite_ARevisionThatLands_IsASuccess()
    {
        var (original, doc, index) = Open();
        using (doc)
        {
            var output = new MemoryStream();
            Assert.True(VerifiedWrite.Write(output, original, doc, index, Edit(doc), null, -1, out _, out string cause));
            Assert.Null(cause);
            Assert.True(output.Length > original.Length);
        }
    }

    /// <summary>
    /// A planned edit — revised objects, or new objects alone — whose revision the stream refused is a
    /// failure though the original bytes all arrived: one byte more than the original is the proof, and
    /// the output is the whole original, not part of it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifiedWrite_ARevisionThatDidNotLand_IsAFailure_WithTheOriginalWhole(bool modified)
    {
        var (original, doc, index) = Open();
        using (doc)
        {
            var mods = modified ? Edit(doc) : null;
            var news = modified ? null : new Dictionary<int, PdfObject> { [index.AllocateObjectNumber()] = new PdfDictionary() };

            var output = new CappedStream(original.Length);
            Assert.False(VerifiedWrite.Write(output, original, doc, index, mods, news, -1, out bool truncated, out string cause));
            Assert.False(truncated);
            Assert.False(string.IsNullOrEmpty(cause));
        }
    }

    /// <summary>
    /// When even the original could not be written whole the output is truncated, and the explanation
    /// says it holds only part of the document — and reports it.
    /// </summary>
    [Fact]
    public void VerifiedWrite_ATruncatedOutput_IsExplainedAsPartOfTheOriginal_AndReported()
    {
        var (original, doc, index) = Open();
        using (doc)
        {
            DocumentEditDiagnostics.Clear();
            Assert.False(VerifiedWrite.WriteOrExplain(new CappedStream(10), original, doc, index, Edit(doc), null, -1,
                "Test.Op", "the planned edit", out string detail));
            Assert.Contains("only part of the original document", detail);
            Assert.Contains(detail, DocumentEditDiagnostics.LastError);

            Assert.False(VerifiedWrite.WriteOrExplain(new CappedStream(original.Length), original, doc, index, Edit(doc), null, -1,
                "Test.Op", "the planned edit", out detail));
            Assert.Contains("the original, unedited document", detail);
        }
    }

    // ---------------------------------------------------------------- DocumentRevisionWriter

    /// <summary>
    /// The entries no revision can carry are skipped and named — object 0, a null — and an object in
    /// both maps is written once, the revised one, with a note.
    /// </summary>
    [Fact]
    public void DocumentRevisionWriter_SkipsWhatCannotBeWritten_AndSaysSo()
    {
        var (original, doc, index) = Open();
        using (doc)
        {
            var notes = new List<string>();
            void Note(string m) => notes.Add(m);
            DocumentEditDiagnostics.OnEvent += Note;
            try
            {
                var revised = PdfDictionary.CloneShallow(doc.Catalog.Dictionary);
                revised.Set(new PdfName("Marker"), new PdfName("Revised"));
                int fresh = index.AllocateObjectNumber();
                var output = new MemoryStream();
                DocumentRevisionWriter.Write(output, original, doc, index,
                    new Dictionary<int, PdfObject> { [1] = revised },
                    new Dictionary<int, PdfObject> { [0] = new PdfDictionary(), [fresh] = null, [1] = new PdfDictionary() });

                Assert.Contains(notes, n => n.Contains("object number 0") && n.Contains("not a valid indirect object number"));
                Assert.Contains(notes, n => n.Contains($"object {fresh}") && n.Contains("is null"));
                Assert.Contains(notes, n => n.Contains("object 1") && n.Contains("appears in both object maps"));

                using var written = PdfDocument.Open(new MemoryStream(output.ToArray()));
                Assert.Equal("Revised", ((PdfName)written.Catalog.Dictionary[new PdfName("Marker")]).Value);
            }
            finally { DocumentEditDiagnostics.OnEvent -= Note; }
        }
    }

    /// <summary>
    /// What the Core writer reports reaches this assembly's sink: a document whose <c>startxref</c>
    /// points past its end has no <c>/Prev</c> the writer can verify, which it says — and the revision
    /// is still written.
    /// </summary>
    [Fact]
    public void DocumentRevisionWriter_PassesTheCoreWritersReportsOn()
    {
        string text = System.Text.Encoding.Latin1.GetString(OnePage());
        int at = text.LastIndexOf("startxref\n", StringComparison.Ordinal) + "startxref\n".Length;
        int end = text.IndexOf('\n', at);
        byte[] broken = System.Text.Encoding.Latin1.GetBytes(text.Substring(0, at) + "999999" + text.Substring(end));
        var (original, doc, index) = Open(broken);
        using (doc)
        {
            var notes = new List<string>();
            void Note(string m) => notes.Add(m);
            DocumentEditDiagnostics.OnEvent += Note;
            try
            {
                var output = new MemoryStream();
                DocumentRevisionWriter.Write(output, original, doc, index, Edit(doc), null);
                Assert.True(output.Length > original.Length);
                Assert.Contains(notes, n => n.Contains("IncrementalUpdateWriter"));
            }
            finally { DocumentEditDiagnostics.OnEvent -= Note; }
        }
    }

    /// <summary>
    /// A new object the caller numbered itself, past everything the index knows, does not share its
    /// number with the cross-reference stream the revision needs: the document reopens with both.
    /// </summary>
    [Fact]
    public void DocumentRevisionWriter_TheXrefStreamNeverTakesACallersNumber()
    {
        var (original, doc, index) = Open(Metadata.XmpMetadataRevisionTests.XrefStreamPdf());
        using (doc)
        {
            int own = index.MaxObjectNumber + 1;   // numbered here, not allocated from the index
            var marker = new PdfDictionary();
            marker.Set(new PdfName("Marker"), new PdfName("Mine"));
            var revised = PdfDictionary.CloneShallow(doc.Catalog.Dictionary);
            revised.Set(new PdfName("Extra"), new PdfReference(own, 0));

            // The Core writer would recover from a colliding number on its own, and say so — so the
            // evidence is that it never had to.
            var notes = new List<string>();
            void Note(string m) => notes.Add(m);
            DocumentEditDiagnostics.OnEvent += Note;
            var output = new MemoryStream();
            try
            {
                DocumentRevisionWriter.Write(output, original, doc, index,
                    new Dictionary<int, PdfObject> { [1] = revised }, new Dictionary<int, PdfObject> { [own] = marker });
            }
            finally { DocumentEditDiagnostics.OnEvent -= Note; }
            Assert.DoesNotContain(notes, n => n.Contains("collides"));
            Assert.Contains("/Type /XRef", System.Text.Encoding.Latin1.GetString(output.ToArray(), original.Length, (int)output.Length - original.Length));

            using var written = PdfDocument.Open(new MemoryStream(output.ToArray()));
            var extra = written.ResolveObject(written.Catalog.Dictionary[new PdfName("Extra")]) as PdfDictionary;
            Assert.NotNull(extra);
            Assert.Equal("Mine", ((PdfName)extra[new PdfName("Marker")]).Value);
        }
    }

    // ---------------------------------------------------------------- WalkBudget

    /// <summary>
    /// A charge of nothing still costs one step; a charge of exactly what is left is granted; a refused
    /// charge spends the rest and says the budget is exhausted.
    /// </summary>
    [Fact]
    public void WalkBudget_UnitCharges()
    {
        var zero = new WalkBudget(1);
        Assert.True(zero.Take(0));
        Assert.False(zero.Take());

        var exact = new WalkBudget(3);
        Assert.True(exact.Take(3));
        Assert.False(exact.Exhausted);

        var refused = new WalkBudget(3);
        Assert.False(refused.Take(4));
        Assert.True(refused.Exhausted);
        Assert.False(refused.Take());
    }

    /// <summary>What is left follows each charge, and a refused charge leaves nothing.</summary>
    [Fact]
    public void WalkBudget_Remaining_FollowsTheCharges()
    {
        var budget = new WalkBudget(10);
        Assert.Equal(10, budget.Remaining);
        budget.Take();
        Assert.Equal(9, budget.Remaining);
        budget.Take(4);
        Assert.Equal(5, budget.Remaining);
        budget.Take(6);
        Assert.Equal(0, budget.Remaining);
    }

    // ---------------------------------------------------------------- StructKidClassifier

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (k, v) in entries) d.Set(new PdfName(k), v);
        return d;
    }

    /// <summary>
    /// A leaf's <c>/Type</c> names it outright, even with nothing else in it; a <c>/Type</c> written as
    /// its own object is resolved; a bare <c>/Type /StructElem</c> is an element; and nothing at all is
    /// not a kid.
    /// </summary>
    [Fact]
    public void StructKidClassifier_ReadsTheTypeFirst_ResolvedAndBare()
    {
        Assert.Equal(StructKidKind.MarkedContentReference, StructKidClassifier.Classify(Dict(("Type", new PdfName("MCR"))), null));
        Assert.Equal(StructKidKind.ObjectReference, StructKidClassifier.Classify(Dict(("Type", new PdfName("OBJR"))), null));
        Assert.Equal(StructKidKind.Element, StructKidClassifier.Classify(Dict(("Type", new PdfName("StructElem"))), null));
        Assert.Equal(StructKidKind.Unknown, StructKidClassifier.Classify(Dict(), null));

        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Obj(3, "/MCR")
            .Obj(4, "<< /Type 3 0 R >>")
            .Finish();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var kid = (PdfDictionary)doc.GetObject(4);
        Assert.Equal(StructKidKind.MarkedContentReference, StructKidClassifier.Classify(kid, doc));
    }

    // ---------------------------------------------------------------- TextDirection

    private static PdfTextChar C(string ch, float x, float w = 10, float h = 10) => new PdfTextChar(ch, x, 0, w, h);

    /// <summary>A group is right to left when more than half its letters are — spaces not counted, and exactly half is not more.</summary>
    [Fact]
    public void TextDirection_RightToLeftNeedsAMajority_OfLetters()
    {
        Assert.False(TextDirection.IsRtlGroup(new[] { C("א", 0), C("b", 10) }));
        Assert.True(TextDirection.IsRtlGroup(new[] { C("א", 0), C("ב", 10), C(" ", 20), C(" ", 30), C("c", 40) }));
    }

    /// <summary>
    /// A right-to-left group reads from the right, a word break wherever the gap passes 0.8 of a glyph's
    /// width — measured the right way round for the descending order — or, for glyphs with no width,
    /// a quarter of their height.
    /// </summary>
    [Fact]
    public void TextDirection_ReadsRightToLeft_WithWordBreaksAtTheGap()
    {
        // Visually, left to right: "דג" then a 9-unit gap then "בא" — read from the right: "אב גד".
        var chars = new[] { C("ד", 0), C("ג", 10), C("ב", 29), C("א", 39) };
        Assert.Equal("אב גד", TextDirection.ToReadingOrder(chars, applyBidi: false));

        // A gap of 7 units under glyphs 10 wide is no break.
        Assert.Equal("אבגד", TextDirection.ToReadingOrder(new[] { C("ד", 0), C("ג", 10), C("ב", 27), C("א", 37) }, applyBidi: false));

        // Zero-width glyphs: a 5-unit gap passes a quarter of their 10-unit height.
        Assert.Equal("ab cd", TextDirection.ToReadingOrder(
            new[] { C("a", 0, 0), C("b", 1, 0), C("c", 6, 0), C("d", 7, 0) }, applyBidi: false));
    }

    /// <summary>
    /// Under bidi, a line drawn "XY" then gimel, bet, alef — Latin at its left end, Hebrew the more —
    /// reads right to left, the Hebrew first (<c>UnicodeBidi.VisualLineDirection</c>); asked of its
    /// leftmost strong character alone, it would read the code first. The control: with Latin the
    /// more, the same ends read left to right.
    /// </summary>
    [Fact]
    public void TextDirection_UnderBidi_ALineWithLatinAtItsLeftEnd_ReadsByItsLetters()
    {
        Assert.Equal("אבג XY", TextDirection.ToReadingOrder(
            new[] { C("X", 0), C("Y", 10), C("ג", 40), C("ב", 50), C("א", 60) }, applyBidi: true));
        Assert.Equal("WXYZ אבג", TextDirection.ToReadingOrder(
            new[] { C("W", 0), C("X", 10), C("Y", 20), C("Z", 30), C("ג", 60), C("ב", 70), C("א", 80) }, applyBidi: true));
    }

    // ---------------------------------------------------------------- AnnotationAppearanceStamp

    /// <summary>
    /// With no usable <c>/AS</c>, the one appearance state that is not <c>/Off</c> is chosen; two of
    /// them are ambiguous, and nothing is guessed.
    /// </summary>
    [Fact]
    public void AnnotationAppearanceStamp_SingleOnState()
    {
        var states = Dict(("Off", new PdfNumber(1)), ("Yes", new PdfNumber(2)));
        Assert.Equal(2, ((PdfNumber)AnnotationAppearanceStamp.SingleOnState(states)).IntValue);

        states.Set(new PdfName("Maybe"), new PdfNumber(3));
        Assert.Null(AnnotationAppearanceStamp.SingleOnState(states));
    }

    /// <summary>A direct appearance stream is written out as an object of its own, under the next free number.</summary>
    [Fact]
    public void AnnotationAppearanceStamp_ADirectAppearanceIsWrittenOut()
    {
        var (_, doc, _) = Open();
        using (doc)
        {
            var stream = new PdfStream { RawData = new byte[] { 0x71, 0x20, 0x51 } };
            var ap = Dict(("N", stream));
            int next = 40;
            var objects = new List<IndirectObject>();

            var resolved = AnnotationAppearanceStamp.ResolveNormalAppearance(new PdfDictionary(), ap, doc, ref next, objects, out PdfReference normal);

            Assert.NotNull(resolved);
            Assert.Equal(40, normal.ReferenceObjectNumber);
            Assert.Equal(41, next);
            Assert.Equal(40, Assert.Single(objects).ObjectNumber);
        }
    }

    /// <summary>A flat appearance box maps 1:1 rather than dividing by its zero extent.</summary>
    [Fact]
    public void AnnotationAppearanceStamp_AFlatBoxMapsOneToOne()
    {
        var (_, doc, _) = Open();
        using (doc)
        {
            var stream = new PdfStream();
            stream.Set(PdfName.BBox, new PdfArray { new PdfNumber(0), new PdfNumber(0), new PdfNumber(0), new PdfNumber(0) });
            string placement = AnnotationAppearanceStamp.BuildPlacement(doc, stream, "Ap0", 10, 20, 30, 40);
            // The number writer turns a non-finite value into a finite one, so the evidence is the
            // scale itself: 1, not whatever an infinity became.
            var cm = Core.Text.ContentStreamParser.Parse(System.Text.Encoding.ASCII.GetBytes(placement))
                .Single(op => op.Operator == "cm").Operands.Select(o => ((PdfNumber)o).DoubleValue).ToArray();
            Assert.Equal(new[] { 1.0, 0, 0, 1, 10, 20 }, cm);
        }
    }

    /// <summary>A <c>/Rect</c> written with its corners the other way round is normalised.</summary>
    [Fact]
    public void AnnotationAppearanceStamp_AReversedRectIsNormalised()
    {
        var annot = Dict(("Rect", new PdfArray { new PdfNumber(100), new PdfNumber(80), new PdfNumber(10), new PdfNumber(20) }));
        Assert.True(AnnotationAppearanceStamp.TryGetRect(null, annot, out var rect));
        Assert.Equal((10.0, 20.0, 90.0, 60.0), rect);
    }
}
