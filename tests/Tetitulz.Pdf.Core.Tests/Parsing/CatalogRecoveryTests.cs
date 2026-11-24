// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// A trailer whose <c>/Root</c> is missing or names an object that is not there, over a
/// cross-reference table that reads cleanly: the catalog is found by the
/// scan recovery a broken table takes — the file re-indexed from its objects' headers, the last
/// <c>/Type /Catalog</c> object taken — and <see cref="PdfParser.LastRootRecovery"/> says so, as
/// public-corpus files need. A file with no catalog
/// anywhere still refuses, and an ordinary file never takes the path.
/// </summary>
/// <remarks>
/// Every positive case reads the recovered catalog's page — a <c>/Type</c> alone would pass for a
/// recovery that found the right object number and read the wrong bytes — and the slot's text,
/// which names what the trailer said and which object stands in.
/// </remarks>
public class CatalogRecoveryTests
{
    private const string CatalogBody = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PagesBody   = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string PageBody    = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";

    [Fact]
    public void ARootNamingAMissingObject_IsRecoveredFromTheCatalogObject()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic("/Root 9 0 R")));

        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Single(doc.Pages);
        Assert.Equal(612d, ((PdfNumber)doc.Pages[0].MediaBox[2]).DoubleValue);
        Assert.NotNull(PdfParser.LastRootRecovery);
        Assert.Contains("names object 9", PdfParser.LastRootRecovery!, StringComparison.Ordinal);
        Assert.Contains("object, 1, is the catalog", PdfParser.LastRootRecovery!, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrailerWithNoRoot_IsRecoveredFromTheCatalogObject()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic(rootEntry: "")));

        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Single(doc.Pages);
        Assert.Contains("names no /Root", PdfParser.LastRootRecovery!, StringComparison.Ordinal);
    }

    /// <summary>The last catalog in the file wins (§7.5.6): a stale copy earlier in the file does not.</summary>
    [Fact]
    public void TheLastCatalogInTheFile_IsTheOneTaken()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic("/Root 9 0 R", staleCatalogFirst: true)));

        Assert.Single(doc.Pages);
        Assert.Contains("object, 1, is the catalog", PdfParser.LastRootRecovery!, StringComparison.Ordinal);
    }

    /// <summary>The refusal control: a file whose objects hold no catalog still refuses, and the slot says the scan found none.</summary>
    [Fact]
    public void NoCatalogAnywhere_StillRefuses_AndSaysTheScanFoundNone()
    {
        var ex = Assert.Throws<PdfParseException>(() => PdfDocument.Open(new MemoryStream(Classic("/Root 9 0 R", catalogBody: "<< /Pages 2 0 R >>"))));

        Assert.Contains("Cannot find document catalog", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no /Type /Catalog object was found", PdfParser.LastRootRecovery!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary file never takes the path: its <c>/Root</c> resolves, the slot stays null, and
    /// so does a catalog with no <c>/Type</c>, which many producers write — a dictionary is a catalog.
    /// </summary>
    [Theory]
    [InlineData(CatalogBody)]
    [InlineData("<< /Pages 2 0 R >>")]
    public void AResolvingRoot_IsNeverSecondGuessed(string catalog)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic("/Root 1 0 R", catalogBody: catalog)));

        Assert.Single(doc.Pages);
        Assert.Null(PdfParser.LastRootRecovery);
    }

    /// <summary>The slot is this open's: a recovered open, then an ordinary one, leaves it null.</summary>
    [Fact]
    public void TheSlot_IsClearedByTheNextOpen()
    {
        using (PdfDocument.Open(new MemoryStream(Classic("/Root 9 0 R")))) { }
        Assert.NotNull(PdfParser.LastRootRecovery);

        using (PdfDocument.Open(new MemoryStream(Classic("/Root 1 0 R")))) { }
        Assert.Null(PdfParser.LastRootRecovery);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A well-formed classic file — a cross-reference table that reads cleanly — whose trailer
    /// carries <paramref name="rootEntry"/>. With <paramref name="staleCatalogFirst"/> an older copy
    /// of object 1, a catalog whose page tree names a page that is not there, precedes the real one.
    /// </summary>
    private static byte[] Classic(string rootEntry, string catalogBody = CatalogBody, bool staleCatalogFirst = false)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        if (staleCatalogFirst) Write("1 0 obj\n<< /Type /Catalog /Pages 8 0 R >>\nendobj\n");
        long o1 = file.Position; Write("1 0 obj\n" + catalogBody + "\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n" + PagesBody + "\nendobj\n");
        long o3 = file.Position; Write("3 0 obj\n" + PageBody + "\nendobj\n");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 4\n0000000000 65535 f \n");
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 4 ").Append(rootEntry).Append(" >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
