// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// An object an appended revision supersedes keeps its generation (§7.5.6), whatever generation
/// the caller handed <see cref="IncrementalUpdateWriter"/>: one rule in the one incremental writer,
/// rather than at the eighty-odd call sites that write 0.
/// </summary>
/// <remarks>
/// Asserted on the raw appended bytes, not by reopening: this library's parser resolves an object
/// by number alone, so a revision written at the wrong generation reopens fine here and is dropped
/// by a reader that matches <c>N G R</c> against the cross-reference entry (CLAUDE.md's first blind
/// spot — the parser normalises away what the writer got wrong).
/// </remarks>
public class SupersededGenerationTests
{
    /// <summary>Catalog 1 at generation 1; pages 2 and page 3 at 0.</summary>
    private static byte[] CatalogAtGenerationOne()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[4];
        offsets[1] = sb.Length; sb.Append("1 1 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.Append($"{offsets[1]:D10} 00001 n \n{offsets[2]:D10} 00000 n \n{offsets[3]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size 4 /Root 1 1 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfDictionary RevisedCatalog()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        catalog.Set(PdfName.Lang, new PdfString("en"));
        return catalog;
    }

    private static string Appended(byte[] original, byte[] output)
        => Encoding.ASCII.GetString(output, original.Length, output.Length - original.Length);

    [Fact]
    public void TheStreamOverload_WritesASupersededObjectAtItsOwnGeneration()
    {
        byte[] original = CatalogAtGenerationOne();
        using var doc = PdfDocument.Open(new MemoryStream(original));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            new MemoryStream(original), output,
            new List<IndirectObject> { new IndirectObject(1, 0, RevisedCatalog()), new IndirectObject(4, 0, new PdfDictionary()) },
            doc.Trailer, doc.Parser.StartXrefOffset, 5);

        string appended = Appended(original, output.ToArray());
        Assert.Matches(@"(^|\n)1 1 obj", appended);
        Assert.Matches(@"\d{10} 00001 n", appended);
        // Control: an object the original does not have is written at 0.
        Assert.Matches(@"(^|\n)4 0 obj", appended);
    }

    /// <summary>
    /// An original with no <c>%PDF-</c> header and no cross-reference table is still re-read — the
    /// parser takes a missing header as none and recovers the table from the object headers — so
    /// there is no <c>/Prev</c> to chain to and the
    /// report is the unchained recovery, naming the missing offset, not a parse failure. The control
    /// is the document above, whose table reads: there the report is the re-declaration, or nothing
    /// when it chains. The "could not be re-read" branch is reached only by an I/O failure.
    /// </summary>
    [Fact]
    public void AHeaderlessOriginal_IsReadByRecovery_AndTheUnchainedRevisionIsReported()
    {
        byte[] headerless = Encoding.ASCII.GetBytes("no header here\n1 0 obj\n<< >>\nendobj\n");

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            new MemoryStream(headerless), output,
            new List<IndirectObject> { new IndirectObject(1, 0, RevisedCatalog()) },
            new PdfDictionary(), 0, 2);

        string report = IncrementalUpdateWriter.LastUnchainedRecovery ?? "";
        Assert.Contains("no usable previous cross-reference offset", report, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be re-read", report, StringComparison.Ordinal);
        Assert.Contains("no %PDF- header", PdfParser.LastHeaderRecovery ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionsOverload_WritesASupersededObjectAtItsOwnGeneration_WithOrWithoutTheDocument()
    {
        byte[] original = CatalogAtGenerationOne();
        using var doc = PdfDocument.Open(new MemoryStream(original));
        var objects = new List<IndirectObject> { new IndirectObject(1, 0, RevisedCatalog()) };

        foreach (var options in new[] { new IncrementalUpdateOptions { Document = doc }, new IncrementalUpdateOptions() })
        {
            using var output = new MemoryStream();
            IncrementalUpdateWriter.WriteUpdate(original, output, objects, options);

            string appended = Appended(original, output.ToArray());
            Assert.Matches(@"(^|\n)1 1 obj", appended);
            Assert.DoesNotMatch(@"(^|\n)1 0 obj", appended);
        }
    }

    /// <summary>
    /// Only a 0 is corrected. A caller that computed a generation is taken at its word — reusing a
    /// freed number takes the next generation, and that is the caller's to know.
    /// </summary>
    [Fact]
    public void AGenerationTheCallerComputedIsKept()
    {
        byte[] original = CatalogAtGenerationOne();
        using var doc = PdfDocument.Open(new MemoryStream(original));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output,
            new List<IndirectObject> { new IndirectObject(1, 2, RevisedCatalog()) },
            new IncrementalUpdateOptions { Document = doc });

        Assert.Matches(@"(^|\n)1 2 obj", Appended(original, output.ToArray()));
    }

    /// <summary>
    /// Only an in-use entry answers. A freed number's generation is the one its reuse would take
    /// (§7.5.4), not an object's. Object 0 heads the free list even where a damaged table marks it
    /// in use.
    /// </summary>
    [Fact]
    public void OnlyAnInUseEntryAnswers()
    {
        var table = new CrossReferenceTable();
        table.SetEntry(new CrossReferenceEntry { ObjectNumber = 0, Type = CrossReferenceEntry.EntryType.Normal, GenerationNumber = 3, Offset = 9 });
        table.SetEntry(new CrossReferenceEntry { ObjectNumber = 4, Type = CrossReferenceEntry.EntryType.Free, GenerationNumber = 1 });
        table.SetEntry(new CrossReferenceEntry { ObjectNumber = 5, Type = CrossReferenceEntry.EntryType.Normal, GenerationNumber = 2, Offset = 9 });

        Assert.Equal(0, ExistingObjectGeneration.Of(table, 0));
        Assert.Equal(0, ExistingObjectGeneration.Of(table, 4));
        Assert.Equal(2, ExistingObjectGeneration.Of(table, 5));   // the control: an in-use entry answers
    }

    /// <summary>
    /// A trailer <c>/Root</c> recovered from a dictionary takes the table's generation, not the
    /// dictionary's stamp. A staged copy stamped with the number and nothing else — which is what
    /// <c>CloneShallow</c> plus an <c>ObjectNumber</c> assignment produces — must not demote a
    /// catalog at generation 1 to 0.
    /// </summary>
    [Fact]
    public void ARecoveredTrailerRootTakesTheTablesGenerationNotTheStamp()
    {
        byte[] original = CatalogAtGenerationOne();
        using var doc = PdfDocument.Open(new MemoryStream(original));
        PdfDictionary staged = RevisedCatalog();
        staged.ObjectNumber = 1;
        staged.GenerationNumber = 0;
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(4));
        trailer.Set(PdfName.Root, staged);

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output,
            new List<IndirectObject> { new IndirectObject(4, 0, new PdfDictionary()) },
            new IncrementalUpdateOptions { Document = doc, OriginalTrailer = trailer });

        string appended = Appended(original, output.ToArray());
        Assert.Contains("/Root 1 1 R", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Root 1 0 R", appended, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceToAnExistingObjectCarriesItsGeneration()
    {
        byte[] original = CatalogAtGenerationOne();
        using var doc = PdfDocument.Open(new MemoryStream(original));

        Assert.Equal(1, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, 1).ReferenceGenerationNumber);
        Assert.Equal(0, ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, 2).ReferenceGenerationNumber);
        // The table is the authority; a reference is only the fallback for a number it lacks.
        Assert.Equal(1, ExistingObjectGeneration.Of(doc.CrossReferenceTable, 1, new PdfReference(1, 7)));
        Assert.Equal(7, ExistingObjectGeneration.Of(doc.CrossReferenceTable, 9, new PdfReference(9, 7)));
    }
}
