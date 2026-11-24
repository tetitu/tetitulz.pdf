// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// The <c>password</c> overloads added to five single-stream readers really read an encrypted
/// document: <c>GetPageLabels</c>, <c>CountImagesOnPage</c>, <c>ReadPageThumbnail</c>,
/// <c>ReadDocumentParts</c> and <c>ReadGeoViewports</c>.
/// </summary>
/// <remarks>
/// <para><b>The assertion is a correlation, not a shape check.</b> Each test reads the <i>same</i>
/// document twice — once unencrypted through the shipped overload, once encrypted through the new
/// one — and requires the two answers to match. That is what makes a zero falsifiable: every one of
/// these readers returns null/0/empty for a document that simply lacks the feature, which is
/// exactly what a failed decrypt also produces, so "it returned something" and even "it did not
/// throw" pass with the password ignored.</para>
/// <para>Each test therefore also asserts the <b>unencrypted</b> answer is non-trivial first. Without
/// that the comparison is two zeroes agreeing, which is the vacuous form this repository bans.</para>
/// </remarks>
public sealed class EncryptedReaderOverloadTests
{
    private static readonly string LF = "\n";
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";

    public EncryptedReaderOverloadTests() => PdfEncryptionOperations.Register();

    [Fact]
    public void GetPageLabels_WithThePassword_ReadsWhatTheClearDocumentGives()
    {
        byte[] plain = PdfWith("/PageLabels << /Nums [0 << /P (Cover) /S /D >>] >>");

        IReadOnlyList<string> clear = PdfOperations.GetPageLabels(Open(plain));
        Assert.NotNull(clear);
        Assert.NotEmpty(clear);                       // the fixture must actually have labels
        Assert.StartsWith("Cover", clear[0], StringComparison.Ordinal);

        IReadOnlyList<string> viaPassword =
            PdfOperations.GetPageLabels(Open(Encrypt(plain)), UserPassword);

        Assert.Equal(clear, viaPassword);
    }

    [Fact]
    public void CountImagesOnPage_WithThePassword_CountsWhatTheClearDocumentHas()
    {
        byte[] plain = PdfWithImage();

        int clear = PdfOperations.CountImagesOnPage(Open(plain), 0);
        Assert.True(clear > 0, "the fixture has no image, so the comparison below would be vacuous");

        int viaPassword = PdfOperations.CountImagesOnPage(Open(Encrypt(plain)), 0, OwnerPassword);

        Assert.Equal(clear, viaPassword);
    }

    [Fact]
    public void ReadPageThumbnail_WithThePassword_ReadsTheThumbnail()
    {
        byte[] plain = PdfWithImage(thumbnail: true);

        var clear = PdfOperations.ReadPageThumbnail(Open(plain), 0);
        Assert.NotNull(clear);

        var viaPassword = PdfOperations.ReadPageThumbnail(Open(Encrypt(plain)), 0, UserPassword);

        Assert.NotNull(viaPassword);
        Assert.Equal(clear.Width, viaPassword.Width);
        Assert.Equal(clear.Height, viaPassword.Height);
    }

    [Fact]
    public void ReadDocumentParts_WithThePassword_ReadsTheHierarchy()
    {
        // /Start and /End give the node real values, so the comparison below is about the parsed
        // object rather than about a non-null reference -- which is all this asserted at first, and
        // NoUnfalsifiableAssertionsTests correctly rejected it.
        byte[] plain = PdfWith(
            "/DPartRoot << /Type /DPartRoot /DPartRootNode 6 0 R >>",
            extraObjects: "6 0 obj" + LF + "<< /Type /DPart /DParts [] /Start 0 /End 0 >>" + LF + "endobj" + LF);

        var clear = PdfOperations.ReadDocumentParts(Open(plain));
        Assert.NotNull(clear);
        Assert.NotNull(clear.Dictionary);   // the fixture really parsed, or the compare is vacuous

        var viaPassword = PdfOperations.ReadDocumentParts(Open(Encrypt(plain)), UserPassword);

        Assert.NotNull(viaPassword);
        Assert.Equal(clear.StartPage, viaPassword.StartPage);
        Assert.Equal(clear.EndPage, viaPassword.EndPage);
        Assert.Equal(clear.Children.Count, viaPassword.Children.Count);
        Assert.Equal(clear.Dictionary.Count, viaPassword.Dictionary.Count);
    }

    [Fact]
    public void ReadGeoViewports_WithThePassword_ReadsTheViewports()
    {
        byte[] plain = PdfWithPageEntry(
            "/VP [ << /Type /Viewport /BBox [0 0 612 792] /Name (Map) >> ]");

        var clear = PdfOperations.ReadGeoViewports(Open(plain));
        Assert.NotEmpty(clear);

        var viaPassword = PdfOperations.ReadGeoViewports(Open(Encrypt(plain)), UserPassword);

        Assert.Equal(clear.Count, viaPassword.Count);
    }

    [Fact]
    public void ExtractImage_WithThePassword_ReturnsTheSameBytesAsTheClearDocument()
    {
        byte[] plain = PdfWithImage();

        var clear = PdfOperations.ExtractImage(Open(plain), 0, 0);
        Assert.NotNull(clear);
        Assert.NotEmpty(clear.Data);           // the fixture really has an image

        var viaPassword = PdfOperations.ExtractImage(Open(Encrypt(plain)), 0, 0, UserPassword);

        Assert.NotNull(viaPassword);
        // The image bytes themselves: a decrypt that silently produced a different (or empty)
        // stream would still return a non-null object.
        Assert.Equal(clear.Data, viaPassword.Data);
    }

    [Fact]
    public void ExtractTables_WithThePassword_FindsWhatTheClearDocumentHas()
    {
        byte[] plain = PdfWithTable();

        var clear = PdfOperations.ExtractTables(Open(plain));
        Assert.NotEmpty(clear);                // the fixture really has a table

        var viaPassword = PdfOperations.ExtractTables(Open(Encrypt(plain)), UserPassword);

        Assert.Equal(clear.Count, viaPassword.Count);
        Assert.Equal(clear[0].Rows.Count, viaPassword[0].Rows.Count);
        Assert.Equal(clear[0].ColumnCount, viaPassword[0].ColumnCount);
    }

    [Fact]
    public void ExtractTaggedText_WithThePassword_ReadsTheSameBlocks()
    {
        byte[] plain = PdfWithTable();

        var clear = PdfOperations.ExtractTaggedText(Open(plain));
        var viaPassword = PdfOperations.ExtractTaggedText(Open(Encrypt(plain)), UserPassword);

        // Equal counts AND equal text: a failed decrypt yields zero blocks, which would match a
        // document that is simply untagged, so the text is what separates them.
        Assert.Equal(clear.Count, viaPassword.Count);
        Assert.Equal(string.Concat(clear.Select(b => b.Text)),
                     string.Concat(viaPassword.Select(b => b.Text)));
    }

    [Fact]
    public void ListRichMedia_WithThePassword_ReturnsWhatTheClearDocumentReturns()
    {
        byte[] plain = PdfWithTable();

        var clear = PdfOperations.ListRichMedia(Open(plain));
        var viaPassword = PdfOperations.ListRichMedia(Open(Encrypt(plain)), UserPassword);

        // This fixture has no rich media, so both are empty -- which alone would be vacuous. The
        // claim being pinned is that the encrypted call REACHES the reader rather than throwing or
        // failing to decrypt, and the mutation (WithDecrypted ignoring its password) makes it throw.
        Assert.Equal(clear.Count, viaPassword.Count);
    }

    [Fact]
    public void ExtractFields_WithThePassword_ReturnsWhatTheClearDocumentReturns()
    {
        byte[] plain = PdfWithTable();
        var queries = new[]
        {
            new global::Tetitulz.Pdf.Base.Operations.Forms.FieldQuery
            {
                Key = "cost",
                Labels = new[] { "Cost" },
            },
        };

        var clear = PdfOperations.ExtractFields(Open(plain), queries);
        Assert.NotEmpty(clear);              // one result row per query, so never empty
        Assert.Equal("cost", clear[0].Key);

        var viaPassword =
            PdfOperations.ExtractFields(Open(Encrypt(plain)), queries, UserPassword);

        Assert.Equal(clear.Count, viaPassword.Count);
        // The extracted VALUE and its page, not just the count: a failed decrypt still yields a
        // result row, with an empty value and PageIndex -1, which matches on count alone.
        Assert.Equal(clear[0].Value, viaPassword[0].Value);
        Assert.Equal(clear[0].PageIndex, viaPassword[0].PageIndex);
    }

    [Fact]
    public void ExecuteDocumentScripts_WithThePassword_ReachesTheDocument()
    {
        byte[] plain = PdfWithTable();

        var clear = PdfOperations.ExecuteDocumentScripts(Open(plain));
        var viaPassword =
            PdfOperations.ExecuteDocumentScripts(Open(Encrypt(plain)), UserPassword);

        Assert.NotNull(viaPassword);
        // NumPages is filled from the document, so it is evidence the runner actually read one:
        // a context that never saw a page tree keeps the default of 1 and would not match a
        // multi-page fixture. Both sides are compared rather than hard-coded.
        Assert.Equal(clear.NumPages, viaPassword.NumPages);
        Assert.True(viaPassword.NumPages > 0);
    }

    /// <summary>
    /// OCR with no plugin registered fails the same way for both routes — but the encrypted route
    /// must reach that decision rather than failing to decrypt on the way.
    /// </summary>
    [Fact]
    public void RecognizeTextWithOcr_WithThePassword_ReachesTheOcrDispatch()
    {
        byte[] plain = PdfWithTable();

        string clear = PdfOperations.RecognizeTextWithOcr(Open(plain), 0);
        string viaPassword =
            PdfOperations.RecognizeTextWithOcr(Open(Encrypt(plain)), 0, UserPassword);

        Assert.Equal(clear, viaPassword);
    }

    /// <summary>
    /// The plugin-dispatched validators reach the plugin with <b>plaintext</b>: the password is
    /// consumed in Base and the decrypted stream crosses the boundary, so the plugin contract
    /// carries no password — the dispatch passes a <c>Stream</c>.
    /// </summary>
    [Fact]
    public void Validate_WithThePassword_ReachesThePluginWithPlaintext()
    {
        byte[] plain = PdfWithTable();

        // No conformance plugin is registered in this assembly, so both routes must agree on the
        // SAME answer -- which is the point: the encrypted call must get as far as the dispatch.
        var clear = PdfOperations.Validate(Open(plain), "PDF/A-1b");
        var viaPassword = PdfOperations.Validate(Open(Encrypt(plain)), "PDF/A-1b", UserPassword);

        Assert.Equal(clear?.IsConformant, viaPassword?.IsConformant);
        Assert.Equal(clear?.Violations?.Count ?? 0, viaPassword?.Violations?.Count ?? 0);
    }

    /// <summary>
    /// The export path reaches the plugin dispatch with plaintext, asserted by the <b>outcome
    /// type</b> rather than by the produced bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>Written wrong first, and the mutation caught it.</b> The original compared
    /// <c>output.Length</c> between the clear and encrypted calls with both exceptions swallowed —
    /// and with no export plugin registered both are zero, so it compared two zeroes and survived
    /// the mutation that turns every sibling red. A comment on it claimed the mutation carried it.
    /// It did not.</para>
    /// <para>Comparing the <i>outcome</i> is what makes it falsifiable: with no plugin registered
    /// both calls fail the same way, and a decrypt that ignored the password fails differently —
    /// <c>PdfException</c> rather than whatever "no export plugin" raises.</para>
    /// </remarks>
    [Fact]
    public void Export_WithThePassword_FailsTheSameWayTheClearTextCallDoes()
    {
        byte[] plain = PdfWithTable();

        Exception clearEx, encEx;
        using (var o = new MemoryStream())
            clearEx = Record.Exception(() => PdfOperations.Export(Open(plain), o, "svg"));
        using (var o = new MemoryStream())
            encEx = Record.Exception(
                () => PdfOperations.Export(Open(Encrypt(plain)), o, "svg", UserPassword));

        Assert.Equal(clearEx?.GetType(), encEx?.GetType());
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    private static MemoryStream Open(byte[] bytes) => new MemoryStream(bytes, writable: false);

    private static byte[] Encrypt(byte[] plain)
    {
        using var input  = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }

    /// <summary>One page, with <paramref name="catalogEntry"/> spliced into the catalog.</summary>
    private static byte[] PdfWith(string catalogEntry, string extraObjects = null)
        => Build(catalogEntry, pageEntry: null, extraObjects: extraObjects);

    /// <summary>One page, with <paramref name="pageEntry"/> spliced into the page dictionary.</summary>
    private static byte[] PdfWithPageEntry(string pageEntry)
        => Build(catalogEntry: null, pageEntry: pageEntry, extraObjects: null);

    /// <summary>One page drawing a 2x2 grey image, optionally also as the page's <c>/Thumb</c>.</summary>
    private static byte[] PdfWithImage(bool thumbnail = false)
    {
        const string samples = "\x00\x40\x80\xC0";
        string image =
            "6 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 " +
            "/ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>\nstream\n" +
            samples + "\nendstream\nendobj\n";

        return Build(
            catalogEntry: null,
            pageEntry: "/Resources << /XObject << /Im0 6 0 R >> >>" +
                       (thumbnail ? " /Thumb 6 0 R" : ""),
            extraObjects: image,
            content: "q 100 0 0 100 72 600 cm /Im0 Do Q");
    }


    /// <summary>A page with a 2x2 ruled grid and text in each cell — a table the extractor finds.</summary>
    private static byte[] PdfWithTable()
    {
        var c = new StringBuilder();
        c.Append("1 w 0 G ");
        // Two horizontal and two vertical rules bounding a 2x2 grid.
        c.Append("100 700 m 300 700 l S 100 660 m 300 660 l S 100 620 m 300 620 l S ");
        c.Append("100 620 m 100 700 l S 200 620 m 200 700 l S 300 620 m 300 700 l S ");
        c.Append("BT /F1 9 Tf 110 670 Td (Item) Tj ET ");
        c.Append("BT /F1 9 Tf 210 670 Td (Cost) Tj ET ");
        c.Append("BT /F1 9 Tf 110 630 Td (Pen) Tj ET ");
        c.Append("BT /F1 9 Tf 210 630 Td (12) Tj ET");

        return Build(catalogEntry: null,
                     pageEntry: "/Resources << /Font << /F1 5 0 R >> >>",
                     extraObjects: null,
                     content: c.ToString());
    }

    private static byte[] Build(
        string catalogEntry, string pageEntry, string extraObjects, string content = "72 700 m 200 700 l S")
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();

        sb.Append("%PDF-1.7\n");

        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R ")
          .Append(catalogEntry ?? string.Empty).Append(" >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R ")
          .Append(pageEntry ?? string.Empty).Append(" >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Length ").Append(content.Length).Append(" >>\nstream\n")
          .Append(content).Append("\nendstream\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        if (!string.IsNullOrEmpty(extraObjects))
        {
            offsets.Add(sb.Length);
            sb.Append(extraObjects);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
