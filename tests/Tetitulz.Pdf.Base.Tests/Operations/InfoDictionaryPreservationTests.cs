// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §14.3.3 — <c>WriteMetadata</c> preserves every <c>/Info</c> entry it was not asked to change, and
/// <see cref="PdfMetadataUpdate.Custom"/> reaches the ones it has no property for.
/// </summary>
/// <remarks>
/// <para><b>This was silent data loss, not a missing feature.</b> <c>WriteInfo</c> started from
/// <c>new PdfDictionary()</c> and copied exactly the eight keys <c>PdfMetadata</c> exposes, then
/// wrote the result at the <i>same object number</i> — so every other entry the document carried was
/// superseded out of existence. Table 317 names <c>/Trapped</c> and permits any custom key besides,
/// and this library's own <c>PdfXConverter</c> writes <c>/Trapped</c>: calling <c>WriteMetadata</c>
/// after it destroyed the conformance the converter had just established.</para>
///
/// <para><b>Nothing caught it because a dropped key is an absence.</b> The one existing test wrote a
/// title and read the title back — true both before and after, and true of an implementation that
/// deletes everything else. That is the shape this file exists to close: every assertion here reads
/// a key the writer was <i>not</i> asked about.</para>
/// </remarks>
public class InfoDictionaryPreservationTests
{
    /// <summary>
    /// The defect, in one line: a key the API has no property for survives an unrelated update.
    /// </summary>
    [Fact]
    public void AnEntryTheApiCannotName_SurvivesAnUnrelatedUpdate()
    {
        byte[] result = Write(WithInfo("/Trapped (True) /Title (Before)"),
                              new PdfMetadataUpdate { Title = "After" });

        Assert.Equal("True", InfoString(result, "Trapped"));
        Assert.Equal("After", InfoString(result, "Title"));
    }

    /// <summary>
    /// …and so does more than one, including a key with no meaning to this library at all. A fixture
    /// with a single custom key cannot tell "preserved the entries" from "special-cased /Trapped".
    /// </summary>
    [Fact]
    public void EveryUnnamedEntrySurvives_NotJustTheOnesTheSpecNames()
    {
        byte[] result = Write(
            WithInfo("/Trapped (Unknown) /Company (Acme) /SAPDocumentId (4711)"),
            new PdfMetadataUpdate { Author = "someone" });

        Assert.Equal("Unknown", InfoString(result, "Trapped"));
        Assert.Equal("Acme", InfoString(result, "Company"));
        Assert.Equal("4711", InfoString(result, "SAPDocumentId"));
    }

    /// <summary>
    /// The named fields still behave as documented — null leaves the existing value, a value
    /// replaces it. Cloning must not turn "leave unchanged" into "leave unchangeable".
    /// </summary>
    [Fact]
    public void ANamedFieldIsStillOverwritten_AndANullOneIsStillLeftAlone()
    {
        byte[] result = Write(WithInfo("/Title (Old) /Author (Keep me)"),
                              new PdfMetadataUpdate { Title = "New" });

        Assert.Equal("New", InfoString(result, "Title"));
        Assert.Equal("Keep me", InfoString(result, "Author"));
    }

    // ------------------------------------------------------------------
    //  Custom
    // ------------------------------------------------------------------

    /// <summary>A caller can now write an entry this API has no property for.</summary>
    [Fact]
    public void Custom_WritesAnEntryTheApiDoesNotName()
    {
        byte[] result = Write(WithInfo("/Title (T)"), new PdfMetadataUpdate
        {
            Custom = new Dictionary<string, string> { { "Department", "Prepress" } },
        });

        Assert.Equal("Prepress", InfoString(result, "Department"));
    }

    /// <summary>A null value removes the key — the only way to delete one.</summary>
    [Fact]
    public void Custom_ANullValueRemovesTheEntry()
    {
        byte[] result = Write(WithInfo("/Trapped (True) /Title (T)"), new PdfMetadataUpdate
        {
            Custom = new Dictionary<string, string> { { "Trapped", null } },
        });

        Assert.Null(InfoString(result, "Trapped"));
        Assert.Equal("T", InfoString(result, "Title"));
    }

    /// <summary>
    /// <b>The precedence rule.</b> A typed property beats the same key spelled into the map, because
    /// two ways to say one thing is this repository's recurring defect and the predictable rule is
    /// that the typed one wins. Asserted rather than left to the reading order of the writer.
    /// </summary>
    [Fact]
    public void ATypedPropertyBeatsTheSameKeyInCustom()
    {
        byte[] result = Write(WithInfo("/Title (Old)"), new PdfMetadataUpdate
        {
            Title = "FromProperty",
            Custom = new Dictionary<string, string> { { "Title", "FromCustom" } },
        });

        Assert.Equal("FromProperty", InfoString(result, "Title"));
    }

    /// <summary>
    /// A document with no <c>/Info</c> at all still gets one — the clone path must not turn the
    /// create path into a no-op.
    /// </summary>
    [Fact]
    public void ADocumentWithNoInfoStillGetsOne()
    {
        byte[] result = Write(NoInfo(), new PdfMetadataUpdate
        {
            Title = "Fresh",
            Custom = new Dictionary<string, string> { { "Department", "Prepress" } },
        });

        Assert.Equal("Fresh", InfoString(result, "Title"));
        Assert.Equal("Prepress", InfoString(result, "Department"));
    }

    /// <summary>
    /// §7.3.10 lets the trailer's <c>/Info</c> be written as an indirect reference to a dictionary
    /// that is itself reached through one more hop. <c>PdfDictionary</c>'s indexer does not follow a
    /// reference, so reading it raw yields <c>null</c> — indistinguishable from "no <c>/Info</c>",
    /// which would re-create the very data loss this fixes at the one spelling a producer is free to
    /// choose.
    /// </summary>
    [Fact]
    public void TheTrailersInfoIsResolved_SoAnIndirectOneIsNotTreatedAsAbsent()
    {
        byte[] result = Write(WithInfo("/Trapped (True)"),
                              new PdfMetadataUpdate { Title = "T" });

        Assert.Equal("True", InfoString(result, "Trapped"));
    }

    // ------------------------------------------------------------------

    private static byte[] Write(byte[] pdf, PdfMetadataUpdate update)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.WriteMetadata(input, output, update);
        return output.ToArray();
    }

    /// <summary>
    /// The value of one <c>/Info</c> entry in the produced file, read back through the parser, or
    /// null when the key is absent. Resolved, because the writer emits <c>/Info</c> as a reference.
    /// </summary>
    private static string InfoString(byte[] pdf, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.NotNull(info);
        return (doc.ResolveObject(info![new PdfName(key)]) as PdfString)?.GetText();
    }

    /// <summary>A one-page file whose trailer points at an indirect <c>/Info</c>.</summary>
    private static byte[] WithInfo(string infoEntries) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""),
        "<< " + infoEntries + " >>");

    /// <summary>The same file with no <c>/Info</c> in the trailer at all.</summary>
    private static byte[] NoInfo() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""));

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R");
        // Five bodies means the fixture carries an /Info object; four means it deliberately does not.
        if (bodies.Length >= 5) sb.Append(" /Info 5 0 R");
        sb.Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
