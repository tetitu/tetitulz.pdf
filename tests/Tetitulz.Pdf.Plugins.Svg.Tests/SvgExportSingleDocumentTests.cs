// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Svg;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Svg.Tests;

/// <summary>
/// <see cref="SvgExportPlugin"/> must produce <b>one</b> SVG document, and must refuse a document
/// it cannot read rather than writing an empty file.
///
/// <para><b>The two defects these pin.</b> (1) The plugin concatenated <i>N</i> complete SVG files —
/// each with its own <c>&lt;?xml …?&gt;</c> declaration — and advertised the result as
/// <c>image/svg+xml</c>. For any multi-page PDF that is not well-formed XML: a parser stops at
/// "XML declaration allowed only at the start of the document". The suite did not notice because
/// its only structural test used a <b>one</b>-page fixture, where concatenation and a single
/// document are indistinguishable. (2) <see cref="PdfDocument"/> skips the catalog of an encrypted
/// file, so <c>Pages</c> was empty, the loop wrote nothing, and the caller got a 0-byte .svg and
/// no exception — a batch archiver could not tell that from a genuinely empty PDF.</para>
///
/// <para>Every assertion here parses the output as an <see cref="XDocument"/>. CLAUDE.md records
/// that this suite substring-matched SVG for its entire history, which is exactly how an export
/// that was not even well-formed XML went unnoticed.</para>
/// </summary>
public class SvgExportSingleDocumentTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static string Export(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        using var output = new MemoryStream();
        new SvgExportPlugin().Export(doc, output, null);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    // ────────────────────────────────────────────────────────────────────
    //  One document
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Three pages, one parseable document. Under the concatenation this call threw
    /// <see cref="XmlException"/> before reaching any assertion.
    /// </summary>
    [Fact]
    public void MultiPageExport_IsOneWellFormedSvgDocument()
    {
        string svg = Export(MinimalPdfBuilder.CreateMultiPagePdf(3));

        var doc = XDocument.Parse(svg);

        Assert.Equal(Svg + "svg", doc.Root!.Name);
        // Exactly one root, and the pages hang underneath it rather than beside it.
        Assert.Equal(3, doc.Root.Elements(Svg + "svg").Count());
        Assert.Single(doc.Descendants().Where(e => e.Parent == null));
    }

    /// <summary>
    /// A single-page export is the same shape — one root with one page inside — so a caller does
    /// not have to special-case page counts.
    /// </summary>
    [Fact]
    public void SinglePageExport_IsOneRootWithOnePageInside()
    {
        var doc = XDocument.Parse(Export(MinimalPdfBuilder.CreateMinimalPdf()));

        Assert.Equal(Svg + "svg", doc.Root!.Name);
        Assert.Single(doc.Root.Elements(Svg + "svg"));
    }

    /// <summary>
    /// The pages are stacked, not overlaid: page <i>n+1</i> starts where page <i>n</i> ends, and
    /// the root's declared height is the sum. Placing them all at y=0 would still parse — which is
    /// why this asserts the geometry rather than just the element count.
    /// </summary>
    [Fact]
    public void Pages_AreStackedVerticallyAndTheRootSpansThemAll()
    {
        var doc = XDocument.Parse(Export(MinimalPdfBuilder.CreateMultiPagePdf(3)));
        var pages = doc.Root!.Elements(Svg + "svg").ToList();

        Assert.Equal(3, pages.Count);

        double expectedY = 0;
        foreach (var page in pages)
        {
            Assert.Equal(expectedY, Num(page, "y"), 3);
            Assert.Equal(0d, Num(page, "x"), 3);
            double h = Num(page, "height");
            Assert.True(h > 0, "each page must keep a positive height");
            expectedY += h;
        }

        Assert.Equal(expectedY, Num(doc.Root, "height"), 3);
        Assert.True(Num(doc.Root, "width") > 0);
    }

    /// <summary>
    /// Each page keeps its own <c>viewBox</c>, so nesting does not rescale its content.
    /// </summary>
    [Fact]
    public void EachPage_KeepsItsOwnViewBox()
    {
        var doc = XDocument.Parse(Export(MinimalPdfBuilder.CreateMultiPagePdf(2)));

        foreach (var page in doc.Root!.Elements(Svg + "svg"))
        {
            string? viewBox = (string?)page.Attribute("viewBox");
            Assert.False(string.IsNullOrWhiteSpace(viewBox),
                "a nested page viewport without its own viewBox would inherit the root's scale");
        }
    }

    /// <summary>
    /// The pages' drawing content survives the wrap — an empty root would satisfy every structural
    /// assertion above.
    /// </summary>
    [Fact]
    public void CombinedDocument_StillCarriesTheDrawingContent()
    {
        var doc = XDocument.Parse(Export(MinimalPdfBuilder.CreatePdfWithRedRectangle()));

        var shapes = doc.Descendants()
            .Where(e => e.Name == Svg + "path" || e.Name == Svg + "rect" || e.Name == Svg + "polygon")
            .ToList();

        // A PAINTED shape, not merely a shape element. The fixture draws a filled rectangle, so an
        // export that emitted the geometry with fill="none" — or that kept the element and lost the
        // graphics state wrapping it — carries no ink, and a bare NotEmpty cannot tell those from a
        // faithful export.
        Assert.Contains(shapes, s =>
        {
            string? fill = (string?)s.Attribute("fill");
            return !string.IsNullOrWhiteSpace(fill) && fill != "none";
        });
    }

    // ────────────────────────────────────────────────────────────────────
    //  Element ids
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every page renders independently and mints ids from a counter that restarts at 0, so
    /// combining pages would otherwise put two <c>id="clip1"</c> in one document and make every
    /// <c>url(#clip1)</c> resolve to whichever came last. The pages are namespaced instead.
    /// </summary>
    [Fact]
    public void ElementIds_AreUniqueAcrossThePagesOfTheCombinedDocument()
    {
        var doc = XDocument.Parse(Export(ThreeClippedPages()));

        var ids = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name == "id")
            .Select(a => a.Value)
            .ToList();

        Assert.True(ids.Count >= 3,
            $"the fixture must actually mint ids for this to mean anything; got {ids.Count}");
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Namespacing an id is only half the job: every reference to it has to move with it, or the
    /// clip/mask/gradient silently resolves to nothing.
    /// </summary>
    [Fact]
    public void EveryLocalReference_ResolvesToAnIdThatExists()
    {
        var doc = XDocument.Parse(Export(ThreeClippedPages()));

        var defined = new HashSet<string>(
            doc.Descendants().SelectMany(e => e.Attributes())
               .Where(a => a.Name == "id").Select(a => a.Value),
            StringComparer.Ordinal);

        var referenced = new List<string>();
        foreach (var a in doc.Descendants().SelectMany(e => e.Attributes()))
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(a.Value, @"url\(#([^)]+)\)"))
                referenced.Add(m.Groups[1].Value);

            if (a.Name.LocalName == "href" && a.Value.StartsWith("#", StringComparison.Ordinal))
                referenced.Add(a.Value.Substring(1));
        }

        Assert.NotEmpty(referenced);
        foreach (string r in referenced)
            Assert.Contains(r, defined);
    }

    /// <summary>
    /// A colour literal is not an id reference. The rewrite keys on the page's own id set for
    /// exactly this reason; a blind <c>#…</c> substitution would corrupt <c>fill="#ff0000"</c>.
    /// </summary>
    [Fact]
    public void ColourLiterals_AreNotRewrittenAsIdReferences()
    {
        const string page =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\" viewBox=\"0 0 10 10\">" +
            "<defs><clipPath id=\"clip1\"><rect width=\"5\" height=\"5\"/></clipPath></defs>" +
            "<g clip-path=\"url(#clip1)\"><rect width=\"9\" height=\"9\" fill=\"#ff0000\"/></g>" +
            "</svg>";

        var doc = XDocument.Parse(SvgExportPlugin.Combine(new[] { page }));

        Assert.Equal("#ff0000",
            (string?)doc.Descendants(Svg + "rect").Last().Attribute("fill"));
        Assert.Equal("p0-clip1",
            (string?)doc.Descendants(Svg + "clipPath").Single().Attribute("id"));
        Assert.Equal("url(#p0-clip1)",
            (string?)doc.Descendants(Svg + "g").Single().Attribute("clip-path"));
    }

    /// <summary>
    /// The scan that prefixes id references answers what the pattern it replaced answered — a name
    /// taken whole and greedily, matches that do not overlap, a <c>#</c> before no name passed over.
    /// </summary>
    [Theory]
    [InlineData("url(#clip1)")]
    [InlineData("#clip1")]
    [InlineData("#ff0000")]
    [InlineData("##clip1")]
    [InlineData("#clip1#clip1 url(#m_2)")]
    [InlineData("#clip10")]
    [InlineData("#clip1.5 #clip1-x")]
    [InlineData("# clip1 #")]
    [InlineData("#")]
    [InlineData("http://example.com/a#clip1")]
    [InlineData("#éclip1 #_a.b-c")]
    [InlineData("#Clip1 #9clip1")]
    public void IdReferencePrefixing_AnswersAsTheReplacedPatternDid(string value)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "clip1", "m_2", "_a.b-c", "clip1.5" };
        string expected = System.Text.RegularExpressions.Regex.Replace(value, @"#([A-Za-z_][A-Za-z0-9_.\-]*)",
            m => ids.Contains(m.Groups[1].Value) ? "#p0-" + m.Groups[1].Value : m.Value);

        Assert.Equal(expected, SvgExportPlugin.PrefixIdReferences(value, ids, "p0-"));
    }

    /// <summary>
    /// Pages stack at their exact offsets however many there are: six significant digits put page
    /// 1 300 of a Letter document at 1 028 810 instead of 1 028 808, overlapping the page before.
    /// </summary>
    [Fact]
    public void ThousandsOfStackedPages_AreEachPlacedAtTheirExactOffset()
    {
        const int Pages = 1300;
        const string page =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"612\" height=\"792\" viewBox=\"0 0 612 792\"/>";

        var doc = XDocument.Parse(SvgExportPlugin.Combine(Enumerable.Repeat(page, Pages).ToList()));

        var placed = doc.Root!.Elements(Svg + "svg").ToList();
        Assert.Equal(Pages, placed.Count);
        Assert.Equal("1028808", (string?)placed[Pages - 1].Attribute("y"));
        Assert.Equal("1029600", (string?)doc.Root.Attribute("height"));
    }

    /// <summary>
    /// The page is read with no DTD (CLAUDE.md's rule for every XML reader): one declaring an entity is
    /// refused rather than expanded, as a page that is not well-formed is. The control is the same page
    /// without the declaration, combined.
    /// </summary>
    [Fact]
    public void APageDeclaringADtd_IsRefused_NotExpanded()
    {
        const string body = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><title>{0}</title></svg>";
        string withDtd = "<!DOCTYPE svg [<!ENTITY a \"expanded\">]>" + string.Format(body, "&a;");

        var refused = Assert.Throws<InvalidOperationException>(() => SvgExportPlugin.Combine(new[] { withDtd }));
        Assert.Contains("page 1", refused.Message, StringComparison.Ordinal);

        var doc = XDocument.Parse(SvgExportPlugin.Combine(new[] { string.Format(body, "plain") }));
        Assert.Equal("plain", doc.Descendants(Svg + "title").Single().Value);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Encrypted input
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An encrypted document has no readable page tree here, and this assembly cannot decrypt.
    /// It must say so — a 0-byte output that a batch job files as "converted" is the failure this
    /// replaces.
    ///
    /// <para>The type is <see cref="PdfEncryptedDocumentException"/>, which is what every other
    /// encrypted-input refusal in the tree throws (Base's sanitizer, flattener and ICC embedder;
    /// the Html, Text, Maintenance and PageEdits plugins), so one catch covers them all. The
    /// message must still name the decryption route — a refusal that does not say how to proceed
    /// is only half of failing closed.</para>
    /// </summary>
    [Fact]
    public void EncryptedDocument_IsRefusedWithAReasonAndWritesNothing()
    {
        using var doc = PdfDocument.Open(new MemoryStream(EncryptedPdf()));
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => new SvgExportPlugin().Export(doc, output, null));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tetitulz.Pdf.Encryption", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The control: the guard keys on encryption, not on emptiness. The same fixture without
    /// <c>/Encrypt</c> exports normally, so the refusal above cannot be a blanket failure.
    /// </summary>
    [Fact]
    public void TheSameDocumentWithoutEncryption_ExportsNormally()
    {
        var doc = XDocument.Parse(Export(UnencryptedPdf()));

        Assert.Equal(Svg + "svg", doc.Root!.Name);
        Assert.Single(doc.Root.Elements(Svg + "svg"));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ────────────────────────────────────────────────────────────────────

    private static double Num(XElement e, string name)
    {
        string? v = (string?)e.Attribute(name);
        Assert.False(string.IsNullOrEmpty(v), $"<{e.Name.LocalName}> is missing its {name}");
        return double.Parse(v!, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Three pages that each clip — <c>W n</c> makes the renderer emit a <c>&lt;clipPath&gt;</c>,
    /// so all three mint ids from their own counters and would collide if they were not
    /// namespaced.
    /// </summary>
    private static byte[] ThreeClippedPages() =>
        BuildPdf(pageCount: 3, encrypted: false,
            content: "0 0 100 100 re W n 1 0 0 rg 10 10 50 50 re f");

    private static byte[] EncryptedPdf()   => BuildPdf(1, encrypted: true,  content: "1 0 0 rg 0 0 50 50 re f");
    private static byte[] UnencryptedPdf() => BuildPdf(1, encrypted: false, content: "1 0 0 rg 0 0 50 50 re f");

    /// <summary>
    /// A minimal PDF with <paramref name="pageCount"/> identical pages, optionally carrying a
    /// trailer <c>/Encrypt</c>. <see cref="PdfDocument.IsEncrypted"/> is driven purely by that
    /// trailer key, so no real crypto handler — and no Tetitulz.Pdf.Encryption reference — is
    /// needed to exercise the guard.
    /// </summary>
    private static byte[] BuildPdf(int pageCount, bool encrypted, string content)
    {
        var bytes = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => bytes.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(bytes.Count); Emit(body); }

        Emit("%PDF-1.7\n");

        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++) kids.Append($"{3 + i * 2} 0 R ");

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj($"2 0 obj\n<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {pageCount} >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            Obj($"{3 + i * 2} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
                $"/Contents {4 + i * 2} 0 R /Resources << >> >>\nendobj\n");
            Obj($"{4 + i * 2} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        }

        int encryptObj = 3 + pageCount * 2;
        if (encrypted)
            Obj($"{encryptObj} 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 " +
                "/O (0123456789ABCDEF0123456789ABCDEF) " +
                "/U (FEDCBA9876543210FEDCBA9876543210) >>\nendobj\n");

        int xref = bytes.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");

        Emit(encrypted
            ? $"trailer\n<< /Size {size} /Root 1 0 R /Encrypt {encryptObj} 0 R " +
              "/ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n"
            : $"trailer\n<< /Size {size} /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return bytes.ToArray();
    }
}
