// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// A trailer for an appended revision must not carry keys that described the cross-reference
/// <b>section it came from</b>.
/// </summary>
/// <remarks>
/// <para><b>For a PDF 1.5+ file the parser has no separate trailer to read</b>, so it merges the
/// xref <i>stream's own dictionary</i> into <c>PdfDocument.Trailer</c>. That dictionary carries
/// <c>/Type /XRef</c>, <c>/W</c>, <c>/Index</c>, <c>/Filter</c>, <c>/DecodeParms</c> and a stale
/// <c>/Length</c>. <see cref="PdfTrailerBuilder.ForUpdate"/> copied every key except <c>/Prev</c>
/// and <c>/XRefStm</c>, so appending a revision to any modern document emitted</para>
/// <code>trailer &lt;&lt; /Type /XRef /W [1 4 2] /Filter /FlateDecode /Length 342 … &gt;&gt;</code>
/// <para>in front of a <b>classic</b> <c>xref</c> table — which §7.5.5 does not permit. Every
/// operation that appends a revision was affected: bookmarks, page labels, form filling and
/// flattening, image replacement, annotations, font embedding, optional content, named
/// destinations, colour-space conversion, ICC embedding and metadata.</para>
///
/// <para><c>IncrementalUpdateWriter.CopyTrailerKeys</c> held the opposite rule in the same
/// assembly, with a doc comment stating exactly why the wholesale form is wrong.</para>
/// </remarks>
public class AppendedTrailerXrefKeyTests
{
    /// <summary>
    /// <b>The defect.</b> None of the cross-reference stream's own keys may survive into the new
    /// trailer.
    /// </summary>
    [Theory]
    [InlineData("Type")]
    [InlineData("W")]
    [InlineData("Index")]
    [InlineData("Filter")]
    [InlineData("DecodeParms")]
    [InlineData("Length")]
    [InlineData("Prev")]
    [InlineData("XRefStm")]
    [InlineData("AuthCode")]
    public void ACrossReferenceSectionKeyIsNotCarriedIntoTheNewTrailer(string key)
    {
        PdfDictionary trailer = PdfTrailerBuilder.ForUpdate(XrefStreamTrailer(), newSize: 42);

        Assert.False(trailer.ContainsKey(new PdfName(key)),
            $"/{key} describes the section it came from, not the document");
    }

    /// <summary>
    /// <b>The floor.</b> The keys that genuinely belong to the document must survive — otherwise
    /// the fix would be "emit an empty trailer", which loses the catalog.
    /// </summary>
    [Theory]
    [InlineData("Root")]
    [InlineData("Info")]
    [InlineData("ID")]
    [InlineData("Encrypt")]
    public void ADocumentLevelKeyIsCarriedThrough(string key)
    {
        PdfDictionary trailer = PdfTrailerBuilder.ForUpdate(XrefStreamTrailer(), newSize: 42);

        Assert.True(trailer.ContainsKey(new PdfName(key)),
            $"/{key} belongs to the document and must survive an appended revision");
    }

    /// <summary>
    /// A custom trailer key a producer put there deliberately also survives — ISO 32000-2 §7.5.6 has
    /// the added trailer contain every entry of the previous one but <c>/Prev</c>, which is why this is
    /// a deny list. <c>IncrementalUpdateWriter</c> once kept an allow list of four keys instead
    /// (<c>AppendedRevisionTrailerEntriesTests</c>).
    /// </summary>
    [Fact]
    public void ACustomTrailerKeySurvives()
    {
        PdfDictionary trailer = PdfTrailerBuilder.ForUpdate(XrefStreamTrailer(), newSize: 42);

        Assert.True(trailer.ContainsKey(new PdfName("MyProducerKey")));
    }

    /// <summary>And /Size is always restated, since it is the one key the new revision redefines.</summary>
    [Fact]
    public void SizeIsRestatedFromTheArgument()
    {
        PdfDictionary trailer = PdfTrailerBuilder.ForUpdate(XrefStreamTrailer(), newSize: 42);

        Assert.Equal(42, ((PdfNumber)trailer[PdfName.Size]).LongValue);
    }

    /// <summary>
    /// The trailer of a PDF 1.5+ document as the parser actually presents it: the xref stream's own
    /// dictionary, merged, with the document keys alongside.
    /// </summary>
    private static PdfDictionary XrefStreamTrailer()
    {
        var d = new PdfDictionary();

        // The cross-reference stream's own dictionary.
        d[PdfName.Type] = new PdfName("XRef");
        d[PdfName.W] = new PdfArray { new PdfNumber(1), new PdfNumber(4), new PdfNumber(2) };
        d[PdfName.Index] = new PdfArray { new PdfNumber(0), new PdfNumber(30) };
        d[PdfName.Filter] = new PdfName("FlateDecode");
        d[PdfName.DecodeParms] = new PdfDictionary();
        d[PdfName.Length] = new PdfNumber(342);
        d[PdfName.Prev] = new PdfNumber(1234);
        d[PdfName.XRefStm] = new PdfNumber(5678);
        d[PdfName.AuthCode] = new PdfDictionary();

        // The document's own keys.
        d[PdfName.Root] = new PdfReference(1, 0);
        d[PdfName.Info] = new PdfReference(2, 0);
        d[PdfName.ID] = new PdfArray { new PdfString("a"), new PdfString("b") };
        d[PdfName.Encrypt] = new PdfReference(3, 0);
        d[PdfName.Size] = new PdfNumber(30);
        d[new PdfName("MyProducerKey")] = new PdfString("kept");

        return d;
    }
}
