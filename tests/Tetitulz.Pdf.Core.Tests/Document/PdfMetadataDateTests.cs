// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <c>/Info</c>'s two dates (§14.3.3) in both the raw and the parsed form —
/// <see cref="PdfMetadata.CreationDate"/> / <see cref="PdfMetadata.ModDate"/> and their
/// <c>…Parsed</c> counterparts.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A census of <c>Core/Document/</c> found
/// <see cref="PdfMetadata.ModDate"/>, <see cref="PdfMetadata.CreationDateParsed"/> and
/// <see cref="PdfMetadata.ModDateParsed"/> with no caller anywhere in the repository and no test
/// of any kind. They were candidates for deletion alongside <c>PdfResources</c>, and they are
/// kept for a reason worth writing down: <b>for a library, "no caller under <c>src/</c>" is weak
/// evidence of deadness.</b> These members exist to be called by consumers, and the neighbouring
/// ones are — <c>Title</c>, <c>Author</c> and <c>CreationDate</c> are read by <c>demo/</c> and
/// <c>apps/</c>. <c>ModDate</c> is <c>CreationDate</c>'s symmetric partner and the two
/// <c>…Parsed</c> properties are the typed forms of that documented pair, so removing three of the
/// four would leave an API that is smaller but arbitrary. <c>PdfResources</c> — <b>since removed</b>,
/// and named here only as the contrast — was the opposite case: an entire type, no member used
/// anywhere, and every consumer performing the same reads directly off a raw dictionary.</para>
///
/// <para>What was genuinely missing was the coverage, which is what this closes — they are now
/// pinned rather than merely present.</para>
/// </remarks>
public sealed class PdfMetadataDateTests
{
    // D:YYYYMMDDHHmmSSOHH'mm' — §7.9.4. Deliberately not UTC, so a parser that drops the offset
    // is visible in the result rather than only in the hour.
    private const string Created = "D:20240115103000+02'00'";
    private const string Modified = "D:20250620141500-05'00'";

    // ── the raw strings ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheRawDateStringsAreReturnedVerbatim()
    {
        using PdfDocument doc = OpenWithInfo($"/CreationDate ({Created}) /ModDate ({Modified})");

        Assert.Equal(Created, doc.Metadata.CreationDate);
        Assert.Equal(Modified, doc.Metadata.ModDate);
    }

    /// <summary>
    /// The two are read independently. A document carrying only one must not report the other —
    /// which a single shared read would do, and is the defect this pairing exists to exclude.
    /// </summary>
    [Fact]
    public void OnlyOneDatePresent_LeavesTheOtherNull()
    {
        using PdfDocument doc = OpenWithInfo($"/CreationDate ({Created})");

        Assert.Equal(Created, doc.Metadata.CreationDate);
        Assert.Null(doc.Metadata.ModDate);
    }

    // ── the parsed forms ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The offset is part of the value.</b> A parser that took the first eight characters —
    /// which one of the two copies merged into <c>PdfDate</c> did — yields the right day and the
    /// wrong instant, and an ordering built on it is wrong by up to a day.
    /// </summary>
    [Fact]
    public void CreationDateParsed_KeepsTheTimeOfDayAndTheOffset()
    {
        using PdfDocument doc = OpenWithInfo($"/CreationDate ({Created}) /ModDate ({Modified})");

        DateTimeOffset? parsed = doc.Metadata.CreationDateParsed;

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2)), parsed!.Value);
    }

    /// <summary>A negative offset, so the sign is read rather than assumed.</summary>
    [Fact]
    public void ModDateParsed_ReadsANegativeOffset()
    {
        using PdfDocument doc = OpenWithInfo($"/CreationDate ({Created}) /ModDate ({Modified})");

        DateTimeOffset? parsed = doc.Metadata.ModDateParsed;

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(2025, 6, 20, 14, 15, 0, TimeSpan.FromHours(-5)), parsed!.Value);
    }

    /// <summary>
    /// The two parsed properties read their own entry. Asserting only one would pass on an
    /// implementation that returned the creation date for both.
    /// </summary>
    [Fact]
    public void TheTwoParsedDatesAreNotTheSameValue()
    {
        using PdfDocument doc = OpenWithInfo($"/CreationDate ({Created}) /ModDate ({Modified})");

        Assert.NotEqual(doc.Metadata.CreationDateParsed, doc.Metadata.ModDateParsed);
    }

    // ── absence and damage ───────────────────────────────────────────────────────────

    [Fact]
    public void AnAbsentDate_ParsesToNull()
    {
        using PdfDocument doc = OpenWithInfo("/Title (No dates here)");

        Assert.Null(doc.Metadata.CreationDate);
        Assert.Null(doc.Metadata.CreationDateParsed);
        Assert.Null(doc.Metadata.ModDate);
        Assert.Null(doc.Metadata.ModDateParsed);
    }

    /// <summary>
    /// An unparseable date is <b>null as a value and intact as a string</b>. Dropping the raw form
    /// too would lose what the file actually said, which is the only thing left to report.
    /// </summary>
    [Fact]
    public void AnUnparseableDate_IsNullParsedButStillReadableRaw()
    {
        using PdfDocument doc = OpenWithInfo("/CreationDate (last Tuesday)");

        Assert.Equal("last Tuesday", doc.Metadata.CreationDate);
        Assert.Null(doc.Metadata.CreationDateParsed);
    }

    // ── §7.3.10: an /Info entry may be indirect ─────────────────────────────────────

    /// <summary>
    /// A date written as an indirect reference is followed. Producers share one string object
    /// between <c>/Info</c> and the XMP packet, and without the document the entry reads as absent —
    /// indistinguishable from a document that carries no date at all.
    /// </summary>
    [Fact]
    public void AnIndirectDate_IsResolved()
    {
        using PdfDocument doc = OpenWithInfo("/CreationDate 6 0 R", extraObject: $"({Created})");

        Assert.Equal(Created, doc.Metadata.CreationDate);
        Assert.NotNull(doc.Metadata.CreationDateParsed);
    }

    // ---------------------------------------------------------------- fixture

    /// <summary>
    /// A one-page file whose trailer names an <c>/Info</c> carrying <paramref name="infoEntries"/>.
    /// Object 6, when <paramref name="extraObject"/> is supplied, is what an indirect entry points at.
    /// </summary>
    private static PdfDocument OpenWithInfo(string infoEntries, string? extraObject = null)
    {
        var offsets = new List<long>();
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string body)
        {
            offsets.Add(file.Position);
            Write((offsets.Count).ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + body + "\nendobj\n");
        }

        Write("%PDF-1.7\n");
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        Obj("<< " + infoEntries + " >>");
        Obj("null");
        Obj(extraObject ?? "null");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (long o in offsets)
            xs.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R /Info 4 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());

        return PdfDocument.Open(new MemoryStream(file.ToArray()));
    }
}
