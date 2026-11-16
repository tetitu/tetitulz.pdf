// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The one reader for a page's <c>/Contents</c> (§7.7.3.3), and the separator that makes an array
/// of fragments into one content stream.
/// </summary>
/// <remarks>
/// <para><b>The separator is not cosmetic and the fixtures here are built so a test can prove it.</b>
/// §7.7.3.3 permits the division between streams only at a token boundary and requires a conforming
/// reader to insert white space. Every existing fixture in the tree built fragments that already
/// ended in <c>\n</c>, so no separator defect could ever have been caught by them — the parts below
/// end in a <b>digit</b> instead, and the two readings differ by a whole operand.</para>
///
/// <para>Both readings parse cleanly, which is why the defect would be silent: <c>10 20 3</c> +
/// <c>0 40 re</c> leaves the parser five operands and <c>10 20 30 40 re</c> leaves it four.
/// Nothing throws; the page simply draws in the wrong place.</para>
/// </remarks>
public class PdfPageContentTests
{
    [Fact]
    public void FragmentsAreJoinedSoATokenCannotSpanTheDivision()
    {
        // Neither fragment ends in white space. Concatenated without a separator the digits "3"
        // and "0" become the single token "30".
        byte[] content = Concatenate("10 20 3", "0 40 re f");

        ContentStreamOperation re = Parse(content).Single(o => o.Operator == "re");

        Assert.Equal(new double[] { 10, 20, 3, 0, 40 }, Numbers(re));
    }

    /// <summary>
    /// The control that names what the assertion above is not: the joined-without-separator reading
    /// is itself a valid rectangle, so "it parsed" proves nothing.
    /// </summary>
    [Fact]
    public void TheUnseparatedJoinWouldAlsoParse()
    {
        ContentStreamOperation re =
            Parse(Encoding.ASCII.GetBytes("10 20 30 40 re f")).Single(o => o.Operator == "re");

        Assert.Equal(new double[] { 10, 20, 30, 40 }, Numbers(re));
    }

    /// <summary>A single-stream <c>/Contents</c> takes no separator: there is no join.</summary>
    [Fact]
    public void ASingleStreamIsReturnedWithNoSeparatorAppended()
    {
        var page = new PdfDictionary();
        page.Set(PdfName.Contents, Stream("10 20 30 40 re f"));

        byte[] content = PdfPageContent.Concatenate(page, o => o, s => s.GetDecodedData());

        Assert.Equal("10 20 30 40 re f", Encoding.ASCII.GetString(content));
    }

    /// <summary>
    /// An array <c>/Contents</c> ends with a separator after the <b>last</b> part as well as
    /// between parts — the exact byte count, not merely "the parts are all there".
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberate, and asserted because only an exact assertion can hold it.</b> All four
    /// copies this reader replaced emitted a trailing separator, so it is behaviour rather than
    /// tidiness; a single-stream <c>/Contents</c> gets none, because there is no join. Content
    /// streams tolerate trailing white space, so every test that parses the result — which is all
    /// the others in this file — passes with the trailing byte removed. A mutation sweep confirmed
    /// it: dropping it left Core's suite and Base's both green, and the byte counts this repository
    /// asserts elsewhere are what it would silently shift.</para>
    /// </remarks>
    [Fact]
    public void AnArrayEndsWithASeparatorAfterTheLastPart()
    {
        byte[] content = Concatenate("10 20 30 40 re", "f");

        Assert.Equal("10 20 30 40 re\nf\n", Encoding.ASCII.GetString(content));
    }

    /// <summary>
    /// No <c>/Contents</c> is <c>null</c> — a legal blank page — and an <b>empty array</b> is an
    /// empty result. Collapsing the two would make a producer defect indistinguishable from a page
    /// that simply draws nothing.
    /// </summary>
    [Fact]
    public void AbsentContentsAndAnEmptyArrayAreDifferentAnswers()
    {
        Assert.Null(PdfPageContent.Concatenate(new PdfDictionary(), o => o, s => s.GetDecodedData()));

        var withEmptyArray = new PdfDictionary();
        withEmptyArray.Set(PdfName.Contents, new PdfArray());
        Assert.Empty(PdfPageContent.Concatenate(withEmptyArray, o => o, s => s.GetDecodedData()));
    }

    /// <summary>
    /// <c>/Contents</c> and its elements are almost always indirect, and <c>PdfDictionary</c>'s
    /// indexer does not follow a reference — unresolved, a page reads as having no content at all.
    /// </summary>
    [Fact]
    public void AnIndirectContentsArrayAndIndirectPartsAreBothFollowed()
    {
        var array = new PdfArray();
        array.Add(new PdfReference(11, 0));
        array.Add(new PdfReference(12, 0));

        var objects = new Dictionary<int, PdfObject>
        {
            [10] = array,
            [11] = Stream("10 20 3"),
            [12] = Stream("0 40 re f"),
        };

        var page = new PdfDictionary();
        page.Set(PdfName.Contents, new PdfReference(10, 0));

        byte[] content = PdfPageContent.Concatenate(
            page,
            o => o is PdfReference r && objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject t) ? t : o,
            s => s.GetDecodedData());

        ContentStreamOperation re = Parse(content).Single(o => o.Operator == "re");
        Assert.Equal(new double[] { 10, 20, 3, 0, 40 }, Numbers(re));
    }

    /// <summary>
    /// A part the caller's decoder declines is skipped, and the rest is kept — the lenient contract
    /// the four merged readers share. The parts that survive still join with a separator.
    /// </summary>
    [Fact]
    public void APartTheDecoderDeclinesIsSkippedAndTheRestStillJoins()
    {
        var array = new PdfArray();
        array.Add(Stream("10 20 3"));
        array.Add(Stream("SKIP"));
        array.Add(Stream("0 40 re f"));

        var page = new PdfDictionary();
        page.Set(PdfName.Contents, array);

        byte[] content = PdfPageContent.Concatenate(page, o => o, s =>
        {
            byte[] data = s.GetDecodedData();
            return Encoding.ASCII.GetString(data) == "SKIP" ? null : data;
        });

        ContentStreamOperation re = Parse(content).Single(o => o.Operator == "re");
        Assert.Equal(new double[] { 10, 20, 3, 0, 40 }, Numbers(re));
    }

    // ------------------------------------------------------------------

    private static byte[] Concatenate(params string[] fragments)
    {
        var array = new PdfArray();
        foreach (string f in fragments) array.Add(Stream(f));

        var page = new PdfDictionary();
        page.Set(PdfName.Contents, array);
        return PdfPageContent.Concatenate(page, o => o, s => s.GetDecodedData());
    }

    private static PdfStream Stream(string text)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        var stream = new PdfStream();
        stream.RawData = data;
        stream.Set(PdfName.Length, new PdfNumber(data.Length));
        return stream;
    }

    private static List<ContentStreamOperation> Parse(byte[] content)
        => ContentStreamParser.Parse(content);

    private static double[] Numbers(ContentStreamOperation op)
        => op.Operands.OfType<PdfNumber>().Select(n => n.DoubleValue).ToArray();
}
