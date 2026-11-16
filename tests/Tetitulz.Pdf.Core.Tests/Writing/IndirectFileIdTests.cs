// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// A document's <c>/ID</c> survives a rewrite even when the trailer writes it as an indirect
/// reference.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> §7.3.10 permits any value to be indirect, and
/// <see cref="PdfDictionary"/>'s indexer does not follow a reference — so
/// <c>PdfCompressedFileWriter</c>'s <c>trailer[PdfName.ID] is PdfArray</c> read <c>/ID 7 0 R</c> as
/// "not an array". That writer answers "no identifier" by <b>synthesising a fresh one</b>, so the
/// rewritten file silently got a new identity. `IncrementalUpdateWriter` resolved the same value
/// before testing it; the two writers disagreed about the same question.</para>
///
/// <para><b>Why that matters beyond tidiness.</b> §14.4 makes the first element permanent for the
/// life of the file: readers use the pair to tell one document's revisions from another's, and an
/// encrypted file's key derivation reads the first element. A rewrite that invents a new identifier
/// produces a file that no longer matches its own earlier revisions.</para>
///
/// <para><b>These assert on the bytes that come out</b>, not on the helper, because "the writer
/// carried the identity forward" is the claim — and the failure mode is a perfectly well-formed
/// <c>/ID</c> that simply is not the one the document had.</para>
/// </remarks>
public class IndirectFileIdTests
{
    private const int CatalogNumber = 1;
    private const int PagesNumber = 2;
    private const int IdNumber = 9;

    private static readonly byte[] FirstHalf =
        { 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
    private static readonly byte[] SecondHalf =
        { 0xFE, 0xED, 0xFA, 0xCE, 9, 9, 9, 9, 8, 8, 8, 8, 7, 7, 7, 7 };

    /// <summary>
    /// An indirect <c>/ID</c> is carried through a compressed rewrite unchanged.
    /// </summary>
    [Fact]
    public void AnIndirectFileIdSurvivesACompressedRewrite()
    {
        byte[] written = WriteCompressed(indirectId: true);

        (byte[] first, byte[] second) = ReadId(written);

        Assert.Equal(FirstHalf, first);
        Assert.Equal(SecondHalf, second);
    }

    /// <summary>
    /// The control: a direct <c>/ID</c> was always carried forward, so the theory above is a claim
    /// about <i>resolution</i> rather than about carrying an identifier at all.
    /// </summary>
    [Fact]
    public void ADirectFileIdSurvivesTheSameRewrite()
    {
        byte[] written = WriteCompressed(indirectId: false);

        (byte[] first, byte[] second) = ReadId(written);

        Assert.Equal(FirstHalf, first);
        Assert.Equal(SecondHalf, second);
    }

    /// <summary>
    /// A document with no <c>/ID</c> at all still gets one — and it is <b>deterministic</b>, so two
    /// runs over the same objects produce the same file. Without this the fix could have been "never
    /// synthesise", which would leave a rewritten file with no identifier.
    /// </summary>
    [Fact]
    public void ADocumentWithNoFileIdGetsADeterministicOne()
    {
        byte[] a = WriteCompressed(indirectId: false, omitId: true);
        byte[] b = WriteCompressed(indirectId: false, omitId: true);

        (byte[] firstA, byte[] secondA) = ReadId(a);
        Assert.Equal(16, firstA.Length);
        Assert.Equal(16, secondA.Length);
        Assert.NotEqual(firstA, secondA);          // a digest split in half, not one value twice

        Assert.Equal(a, b);                        // byte-for-byte reproducible
    }

    /// <summary>
    /// A dangling reference — an <c>/ID</c> naming an object the writer is not writing — is treated
    /// as absent rather than followed into nothing, so the output still carries a usable
    /// identifier instead of a reference to an object that is not in the file.
    /// </summary>
    [Fact]
    public void ADanglingFileIdReferenceFallsBackToASynthesisedOne()
    {
        byte[] written = WriteCompressed(indirectId: true, danglingId: true);

        (byte[] first, byte[] second) = ReadId(written);

        Assert.Equal(16, first.Length);
        Assert.NotEqual(FirstHalf, first);
    }

    /// <summary>
    /// The objects name the <c>/ID</c> object twice. The writer writes the last occurrence, and the
    /// identifier must come from the same one. The resolver took the first, so the file carried an
    /// <c>/ID</c> from an object that was never written.
    /// </summary>
    [Fact]
    public void ADuplicatedFileIdObject_ResolvesToTheOccurrenceWritten()
    {
        byte[] written = WriteCompressed(indirectId: true, duplicateIdDecoy: true);

        (byte[] first, byte[] second) = ReadId(written);

        Assert.Equal(FirstHalf, first);
        Assert.Equal(SecondHalf, second);
    }

    // ================================================================ helpers

    private static (byte[] first, byte[] second) ReadId(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var id = doc.ResolveObject(doc.Trailer[PdfName.ID]) as PdfArray;
        Assert.NotNull(id);
        Assert.True(id.Count >= 2, "/ID must be a two-element array");

        return (((PdfString)id[0]).RawBytes, ((PdfString)id[1]).RawBytes);
    }

    private static byte[] WriteCompressed(bool indirectId, bool omitId = false, bool danglingId = false,
                                          bool duplicateIdDecoy = false)
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(PagesNumber, 0));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));

        var objects = new List<(int, PdfObject)>
        {
            (CatalogNumber, catalog),
            (PagesNumber, pages),
        };

        var idArray = new PdfArray();
        idArray.Add(new PdfString(FirstHalf, isHex: true));
        idArray.Add(new PdfString(SecondHalf, isHex: true));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(CatalogNumber, 0));

        if (!omitId)
        {
            if (indirectId)
            {
                trailer.Set(PdfName.ID, new PdfReference(IdNumber, 0));
                // danglingId: the reference names an object the writer is not given.
                if (duplicateIdDecoy)
                {
                    // An earlier occurrence, which the last one replaces.
                    var decoy = new PdfArray();
                    decoy.Add(new PdfString(SecondHalf, isHex: true));
                    decoy.Add(new PdfString(FirstHalf, isHex: true));
                    objects.Add((IdNumber, decoy));
                }
                if (!danglingId) objects.Add((IdNumber, idArray));
            }
            else
            {
                trailer.Set(PdfName.ID, idArray);
            }
        }

        using var buffer = new MemoryStream();
        PdfCompressedFileWriter.Write(buffer, new PdfVersion(1, 7), objects, trailer);
        return buffer.ToArray();
    }
}
