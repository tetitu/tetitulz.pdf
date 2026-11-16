// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The repaired trailer takes <c>/Root</c>, <c>/Info</c>, <c>/Encrypt</c> and <c>/ID</c> from the
/// newest of the file's trailers that has each.
/// </summary>
/// <remarks>
/// Only the last <c>trailer</c> keyword used to be read, and the parser's recovery reads only that
/// one too, so a file whose last trailer names nothing but <c>/Size</c> came out of repair without
/// its document information or identifier. None of these fixtures has a <c>startxref</c>, so the
/// parser recovers by scanning as well.
/// </remarks>
public sealed class RepairTrailerTests
{
    private const string IdHex = "00112233445566778899AABBCCDDEEFF";

    private const string Objects =
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
        "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
        "6 0 obj\n<< /Producer (Probe) >>\nendobj\n";

    private static PdfDocument RepairAndOpen(string pdf)
    {
        using var output = new MemoryStream();
        PdfRepair.Repair(new MemoryStream(Encoding.Latin1.GetBytes(pdf), writable: false), output);
        return PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
    }

    [Fact]
    public void AnEarlierTrailerSuppliesWhatTheLastOneLacks()
    {
        string pdf = "%PDF-1.4\n" + Objects +
                     $"trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R /ID [<{IdHex}> <{IdHex}>] >>\n" +
                     "7 0 obj\n<< /Updated true >>\nendobj\n" +
                     "trailer\n<< /Size 8 >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Equal("Probe", doc.Metadata.Producer);
        var id = Assert.IsType<PdfArray>(doc.Trailer[PdfName.ID]);
        Assert.Equal(IdHex, Convert.ToHexString(Assert.IsType<PdfString>(id[0]).RawBytes));
    }

    [Fact]
    public void ANewerTrailersEntryWinsOverAnOlderOnes()
    {
        string pdf = "%PDF-1.4\n" + Objects +
                     "trailer\n<< /Size 7 /Root 1 0 R /Info 3 0 R >>\n" +
                     "trailer\n<< /Size 7 /Info 6 0 R >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Equal("Probe", doc.Metadata.Producer);
    }

    [Fact]
    public void AnAppendedTrailerWinsOverTheOneStartxrefStillNames()
    {
        // An update appended with its own trailer, but with a startxref that still names the original
        // table — so the parser reads the original trailer, and only the bytes say what is newer.
        var sb = new StringBuilder("%PDF-1.4\n").Append(Objects);
        string text = sb.ToString();
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (string header in new[] { "1 0 obj", "2 0 obj", "3 0 obj" })
            sb.AppendFormat("{0:D10} 00000 n \n", text.IndexOf(header, StringComparison.Ordinal));
        sb.Append("6 1\n").AppendFormat("{0:D10} 00000 n \n", text.IndexOf("6 0 obj", StringComparison.Ordinal));
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R /Info 3 0 R >>\n").AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        sb.Append("7 0 obj\n<< /Updated true >>\nendobj\n");
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R /Info 6 0 R >>\n").AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        using var doc = RepairAndOpen(sb.ToString());

        Assert.Equal("Probe", doc.Metadata.Producer);
    }

    [Fact]
    public void ATrailerKeywordInsideAnObject_IsNotTheFilesTrailer()
    {
        // An uncompressed embedded file holding a trailer that names object 3 — a page — as /Info.
        const string embedded = "trailer\n<< /Info 3 0 R >>\n";
        string pdf = "%PDF-1.4\n" + Objects +
                     $"8 0 obj\n<< /Type /EmbeddedFile /Length {embedded.Length} >>\nstream\n{embedded}\nendstream\nendobj\n" +
                     "trailer\n<< /Size 9 /Root 1 0 R >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Null(doc.Trailer[PdfName.Info]);
    }

    [Fact]
    public void AWordEndingInTrailer_IsNotATrailerKeyword()
    {
        string pdf = "%PDF-1.4\n" + Objects +
                     "Xtrailer\n<< /Info 3 0 R >>\n" +
                     "trailer\n<< /Size 7 /Root 1 0 R >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Null(doc.Trailer[PdfName.Info]);
    }

    // ---------------------------------------------------------------- the root

    [Fact]
    public void TheTrailersRootWinsOverALaterCatalog()
    {
        string pdf = "%PDF-1.4\n" + Objects +
                     "9 0 obj\n<< /Type /Catalog >>\nendobj\n" +
                     "trailer\n<< /Size 10 /Root 1 0 R >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Single(doc.Pages);
    }

    [Fact]
    public void ATrailerRootNamingAMissingObject_FallsBackToTheLatestCatalog()
    {
        // Catalog 1 has one page; catalog 9, written later, has two.
        string pdf = "%PDF-1.4\n" + Objects +
                     "9 0 obj\n<< /Type /Catalog /Pages 10 0 R >>\nendobj\n" +
                     "10 0 obj\n<< /Type /Pages /Kids [3 0 R 11 0 R] /Count 2 >>\nendobj\n" +
                     "11 0 obj\n<< /Type /Page /Parent 10 0 R /MediaBox [0 0 612 792] >>\nendobj\n" +
                     "trailer\n<< /Size 12 /Root 99 0 R >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Equal(2, doc.Pages.Count);
    }

    [Fact]
    public void WithNoCatalogAnywhere_TheRootNamesAnObjectTheFileHas()
    {
        // Nothing can be the catalog, but a /Root naming an object that is not there makes the
        // repaired file unopenable rather than empty.
        string pdf = "%PDF-1.4\n" +
                     "5 0 obj\n<< /A 1 >>\nendobj\n" +
                     "6 0 obj\n<< /Producer (Probe) >>\nendobj\n" +
                     "trailer\n<< /Size 7 >>\n%%EOF\n";

        using var doc = RepairAndOpen(pdf);

        Assert.Equal(5, Assert.IsType<PdfReference>(doc.Trailer[PdfName.Root]).ReferenceObjectNumber);
        Assert.Empty(doc.Pages);
    }

    // ---------------------------------------------------------------- adversarial

    [Fact]
    public void ManyUnclosedTrailerDictionaries_OnlyTheNewestAreRead()
    {
        // Sixty thousand "trailer <<" that never close. Each is read to the end of the file, so
        // reading them all costs ~1.8 billion tokens; the newest sixteen, a few megabytes.
        var sb = new StringBuilder("%PDF-1.4\n").Append(Objects);
        for (int i = 0; i < 60_000; i++) sb.Append("trailer <<\n");
        string pdf = sb.ToString();

        int pages = OnItsOwnThread(() =>
        {
            using var doc = RepairAndOpen(pdf);
            return doc.Pages.Count;
        });

        Assert.Equal(1, pages);
    }

    private static T OnItsOwnThread<T>(Func<T> work)
    {
        T value = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { value = work(); }
            catch (Exception ex) { failure = ex; }
        }, 16 * 1024 * 1024)
        { IsBackground = true };

        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            Assert.Fail("the repair did not finish: every trailer keyword in the file was parsed to its end.");
        if (failure != null)
            Assert.Fail("the repair threw: " + failure);
        return value;
    }
}
