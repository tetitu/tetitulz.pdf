// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Docx;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// Both DOCX adapters accepted an options bag and never read a key of it. A caller who wrote
/// <c>Extra["PageWidth"] = 842</c> for A4 got US Letter, and a mistyped key and an honoured one
/// produced identical output — so nothing distinguished "your setting was applied" from "your
/// setting was silently discarded".
///
/// <para>The two adapters get <b>different</b> correct answers, which is why they are tested apart:
/// import has real options to map (<c>DocxToPdfConverter.Convert</c> takes a
/// <c>DocxToPdfOptions</c>), while export has none at all
/// (<c>PdfToDocxConverter.Convert(Stream)</c> takes no options object), so its only honest
/// behaviour is to refuse a bag it cannot honour.</para>
/// </summary>
public class AdapterOptionsAreHonouredTests
{
    private static byte[] MinimalDocx()
    {
        // A .docx is a zip; the converter needs word/document.xml with one paragraph.
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(content);
            }

            Add("[Content_Types].xml",
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
                + "</Types>");
            Add("_rels/.rels",
                "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>"
                + "</Relationships>");
            Add("word/document.xml",
                "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + "<w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body></w:document>");
        }
        return ms.ToArray();
    }

    private static double FirstPageWidth(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var box = doc.Pages[0].MediaBox;
        return box.GetDouble(2) - box.GetDouble(0);   // MediaBox is [x0 y0 x1 y1]
    }

    // ---- import: the options are real and must reach the converter --------------------

    /// <summary>
    /// The defect itself, asserted on a <b>measured page width</b> rather than on the call
    /// succeeding: A4 is 595 points wide, US Letter 612, and the old code produced 612 whatever
    /// the caller asked for.
    /// </summary>
    [Fact]
    public void ImportHonoursAPageWidthTheCallerAsksFor()
    {
        var plugin = new DocxImportPlugin();
        var options = new ImportOptions();
        options.Extra["PageWidth"] = 595.0;      // A4 width in points
        options.Extra["PageHeight"] = 842.0;

        using var outPdf = new MemoryStream();
        plugin.Import(new MemoryStream(MinimalDocx()), outPdf, options);

        Assert.Equal(595.0, FirstPageWidth(outPdf.ToArray()), 1);
    }

    /// <summary>
    /// The control that makes the test above mean something: with no options the page is still
    /// US Letter, so "it honours PageWidth" is not satisfied by a converter that happens to emit
    /// 595 regardless.
    /// </summary>
    [Fact]
    public void ImportWithNoOptionsStillProducesTheDefaultLetterWidth()
    {
        var plugin = new DocxImportPlugin();

        using var outPdf = new MemoryStream();
        plugin.Import(new MemoryStream(MinimalDocx()), outPdf, null);

        Assert.Equal(612.0, FirstPageWidth(outPdf.ToArray()), 1);
    }

    /// <summary>
    /// A key whose <b>casing</b> differs must behave the same way, because the unknown-key gate
    /// already accepts it: <c>RejectUnknownKeys</c> matches case-insensitively, so
    /// <c>Extra["pagewidth"]</c> is not refused as unrecognised.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the exact failure this class exists to end, reintroduced by a copy.</b> The
    /// reader's own summary says a caller who wrote <c>Extra["PageWidth"] = 842</c> "got US Letter
    /// and no indication the request had been dropped — a mistyped key and an honoured one produced
    /// identical output". With a lower-case key that was true again: the gate waved it through
    /// case-insensitively and the value read came back case-<i>sensitively</i>, so the option was
    /// accepted and then silently discarded.</para>
    ///
    /// <para>The divergence came from duplication. <c>Plugins.Html</c>'s equivalent reader looks the
    /// value up through a case-insensitive <c>TryGet</c>; the DOCX copy used
    /// <c>extra.TryGetValue</c> directly. Both files' remarks discuss why they are not shared —
    /// neither noticed they had stopped agreeing.</para>
    ///
    /// <para>Asserted on a <b>measured page width</b>, like its sibling above: 595 for A4 against the
    /// 612 the defect produced.</para>
    /// </remarks>
    [Fact]
    public void ImportHonoursAPageWidthKeyWhoseCasingDiffers()
    {
        var plugin = new DocxImportPlugin();
        var options = new ImportOptions();
        options.Extra["pagewidth"] = 595.0;
        options.Extra["pageheight"] = 842.0;

        using var outPdf = new MemoryStream();
        plugin.Import(new MemoryStream(MinimalDocx()), outPdf, options);

        Assert.Equal(595.0, FirstPageWidth(outPdf.ToArray()), 1);
    }

    /// <summary>
    /// An empty bag must take the same path as no bag at all — otherwise adding
    /// <c>new ImportOptions()</c> would silently change output.
    /// </summary>
    [Fact]
    public void ImportWithAnEmptyBagIsIdenticalToNoOptions()
    {
        var plugin = new DocxImportPlugin();

        using var withNull = new MemoryStream();
        using var withEmpty = new MemoryStream();
        plugin.Import(new MemoryStream(MinimalDocx()), withNull, null);
        plugin.Import(new MemoryStream(MinimalDocx()), withEmpty, new ImportOptions());

        Assert.Equal(FirstPageWidth(withNull.ToArray()), FirstPageWidth(withEmpty.ToArray()), 1);
    }

    [Fact]
    public void ImportRefusesAnUnrecognisedKeyRatherThanIgnoringIt()
    {
        var plugin = new DocxImportPlugin();
        var options = new ImportOptions();
        options.Extra["PgeWidth"] = 595.0;        // the typo this exists to surface

        var ex = Assert.Throws<ArgumentException>(() =>
            plugin.Import(new MemoryStream(MinimalDocx()), new MemoryStream(), options));

        Assert.Contains("PgeWidth", ex.Message);
    }

    [Theory]
    [InlineData("PageWidth", 0.0)]        // a zero-width page is not a smaller page
    [InlineData("PageWidth", -100.0)]
    [InlineData("DefaultFontSize", 0.0)]
    public void ImportRefusesANonPositiveDimensionRatherThanSubstitutingTheDefault(string key, double value)
    {
        var plugin = new DocxImportPlugin();
        var options = new ImportOptions();
        options.Extra[key] = value;

        var ex = Assert.Throws<ArgumentException>(() =>
            plugin.Import(new MemoryStream(MinimalDocx()), new MemoryStream(), options));

        Assert.Contains(key, ex.Message);
    }

    /// <summary>
    /// A margin of zero is legitimate where a page dimension of zero is not — which is why the
    /// reader has two methods rather than one with a flag.
    /// </summary>
    [Fact]
    public void ImportAcceptsAZeroMarginBecauseThatIsAMeaningfulValue()
    {
        var plugin = new DocxImportPlugin();
        var options = new ImportOptions();
        options.Extra["MarginLeft"] = 0.0;

        using var outPdf = new MemoryStream();
        plugin.Import(new MemoryStream(MinimalDocx()), outPdf, options);

        Assert.True(outPdf.Length > 0);
        Assert.Equal(612.0, FirstPageWidth(outPdf.ToArray()), 1);
    }

    [Fact]
    public void ImportRefusesAValueOfTheWrongShape()
    {
        var plugin = new DocxImportPlugin();
        var options = new ImportOptions();
        options.Extra["EmbedImages"] = "yes please";

        var ex = Assert.Throws<ArgumentException>(() =>
            plugin.Import(new MemoryStream(MinimalDocx()), new MemoryStream(), options));

        Assert.Contains("EmbedImages", ex.Message);
    }

    // ---- export: there are no options, so the bag must be refused ---------------------

    /// <summary>
    /// <c>PdfToDocxConverter.Convert(Stream)</c> takes no options object, so any key is a request
    /// this adapter cannot honour. Refusing is the only answer that does not lie.
    /// </summary>
    [Fact]
    public void ExportRefusesAnyOptionKeyBecauseItHasNoneToHonour()
    {
        var plugin = new DocxExportPlugin();
        var options = new ExportOptions();
        options.Extra["EmbedImages"] = false;

        using var pdf = new MemoryStream(SinglePagePdf());
        using var doc = PdfDocument.Open(pdf);

        var ex = Assert.Throws<ArgumentException>(() =>
            plugin.Export(doc, new MemoryStream(), options));

        Assert.Contains("EmbedImages", ex.Message);
        Assert.Contains("no configurable options", ex.Message);
    }

    /// <summary>
    /// The control: an absent or empty bag is not an error, so the refusal above cannot be
    /// satisfied by an adapter that simply always throws.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportWithNoOptionKeysProducesARealDocxPackage(bool passAnEmptyBag)
    {
        var plugin = new DocxExportPlugin();
        using var pdf = new MemoryStream(SinglePagePdf());
        using var doc = PdfDocument.Open(pdf);
        using var output = new MemoryStream();

        plugin.Export(doc, output, passAnEmptyBag ? new ExportOptions() : null);

        // Asserting the OUTPUT IS A DOCX, not that bytes were produced. The first version of this
        // test asserted `output.Length > 0`, which the repo's own NoUnfalsifiableAssertionsTests
        // guard correctly failed the build over: a .docx that is a valid empty zip, or any
        // non-empty garbage, clears that bar - and "still converts" is precisely the claim it
        // cannot make. This one fails if the export produces bytes that are not a package with a
        // main document part in it.
        output.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Read);

        var docPart = zip.GetEntry("word/document.xml");
        Assert.NotNull(docPart);

        using var reader = new StreamReader(docPart.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("<w:body", xml, StringComparison.Ordinal);
        Assert.Contains("Hello", xml, StringComparison.Ordinal);   // the text the fixture page draws
    }

    private static byte[] SinglePagePdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET";
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.7\n");
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
