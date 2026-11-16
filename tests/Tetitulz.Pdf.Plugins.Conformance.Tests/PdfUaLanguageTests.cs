// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// UA02 as ISO 14289-1 §7.2 states it — "Natural language shall be declared as discussed in ISO
/// 32000-1:2008, 14.9.2 and/or as described in ISO 32000-1:2008, 7.9.2" — and as the published profile's rules
/// 7.2-2 and 7.2-21 to -34 read it: the catalog's <c>/Lang</c> is one way to declare a language, not
/// the only one. Each case is one of the conformance corpus files' shapes.
/// </summary>
public class PdfUaLanguageTests
{
    private const string Text = "BT /F1 12 Tf 72 700 Td (Hi) Tj ET";

    /// <summary>A language escape (§7.9.2.2) before "Hi": U+001B, "en", U+001B, in a UTF-16BE string.</summary>
    private const string EscapedHi = "<FEFF001B0065006E001B00480069>";

    // ---------------------------------------------------------------- page text

    /// <summary>Text inside tagged content takes its element's language, or an ancestor's (7.2-t34).</summary>
    [Theory]
    [InlineData("/Lang (en-US)", "", false)]
    [InlineData("", "/Lang (en-US)", false)]           // the Document element above declares it
    [InlineData("", "", true)]
    public void TaggedTextTakesItsElementsOrAnAncestorsLanguage(string paragraphEntries, string documentEntries, bool reportsUa02)
    {
        var result = Validate(Tagged("/P <</MCID 0>> BDC " + Text + " EMC", paragraphEntries: paragraphEntries, documentEntries: documentEntries));
        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("text-showing operator", StringComparison.Ordinal)));
    }

    /// <summary>A marked-content /Lang declares its text's language; an artifact's text needs none; untagged text has no other way.</summary>
    [Theory]
    [InlineData("/Span <</Lang (en-US)>> BDC " + Text + " EMC", false)]
    [InlineData("/Span <</Lang (en-US)>> BDC /Span <<>> BDC " + Text + " EMC EMC", false)]   // an enclosing sequence's
    [InlineData("/Span <</Lang (en-US)>> BDC EMC " + Text, true)]                              // closed before the text
    [InlineData("/Artifact BMC " + Text + " EMC", false)]
    [InlineData("/Artifact <</Type /Pagination>> BDC /Span <<>> BDC " + Text + " EMC EMC", false)]
    [InlineData(Text, true)]
    [InlineData("/Span <<>> BDC " + Text + " EMC", true)]
    public void MarkedContentDeclaresAndArtifactsAreExempt(string content, bool reportsUa02)
    {
        var result = Validate(Tagged(content));
        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("text-showing operator", StringComparison.Ordinal)));
    }

    /// <summary>With a catalog /Lang every language is determined — the control for every case here.</summary>
    [Fact]
    public void ACatalogLanguageDeterminesEveryText()
    {
        var withCatalog = Validate(Tagged(Text, catalogEntries: "/Lang (en-US)", paragraphEntries: "/Alt (x)"));
        var without = Validate(Tagged(Text, paragraphEntries: "/Alt (x)"));

        Assert.Empty(Ua02(withCatalog));
        Assert.Single(Ua02(without));
    }

    /// <summary>A document with no text owes no language — the minimal case §7.2 leaves alone.</summary>
    [Fact]
    public void ADocumentWithNoTextOwesNoLanguage()
    {
        Assert.Empty(Ua02(Validate(Tagged(""))));
    }

    // ---------------------------------------------------------------- alternates, annotations, fields, outlines, XMP

    /// <summary>An /Alt, /ActualText or /E needs its element's or an ancestor's /Lang (rules 7.2-21 to -23).</summary>
    [Theory]
    [InlineData("/Alt (A picture)")]
    [InlineData("/ActualText (x)")]
    [InlineData("/E (abbr.)")]
    public void AnAlternateTextNeedsALanguage(string entry)
    {
        Assert.Contains(Ua02(Validate(Tagged("", paragraphEntries: entry))), m => m.Contains("/Alt, /ActualText or /E", StringComparison.Ordinal));
        Assert.Empty(Ua02(Validate(Tagged("", paragraphEntries: entry + " /Lang (en)"))));
    }

    /// <summary>An annotation's /Contents: an escape, or its element's /Lang through the /OBJR (7.2-t24-pass-b).</summary>
    [Theory]
    [InlineData("(Click)", "", true)]
    [InlineData(EscapedHi, "", false)]
    [InlineData("(Click)", "/Lang (en-US)", false)]
    public void AnAnnotationsContentsNeedsALanguage(string contents, string paragraphEntries, bool reportsUa02)
    {
        var result = Validate(Tagged("", paragraphEntries: paragraphEntries, paragraphKids: "<< /Type /OBJR /Obj 7 0 R >>",
            pageEntries: "/Annots [7 0 R] /Tabs /S",
            extra: new[] { "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /F 4 /StructParent 1 /Contents " + contents + " >>" }));
        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("annotation /Contents", StringComparison.Ordinal)));
    }

    /// <summary>A field's /TU: an escape, or its widget's element's /Lang (7.2-t25-pass-b).</summary>
    [Theory]
    [InlineData("(Name)", "", true)]
    [InlineData(EscapedHi, "", false)]
    [InlineData("(Name)", "/Lang (en-US)", false)]
    public void AFieldsTooltipNeedsALanguage(string tooltip, string paragraphEntries, bool reportsUa02)
    {
        var result = Validate(Tagged("", catalogEntries: "/AcroForm << /Fields [7 0 R] >>", paragraphEntries: paragraphEntries,
            paragraphKids: "<< /Type /OBJR /Obj 7 0 R >>", pageEntries: "/Annots [7 0 R] /Tabs /S",
            extra: new[] { "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /TU " + tooltip + " /Rect [0 0 50 10] /F 4 /StructParent 1 >>" }));
        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("field /TU", StringComparison.Ordinal)));
    }

    /// <summary>An outline title is a text string whose language only an escape or the catalog gives (7.2-t02-fail-a).</summary>
    [Theory]
    [InlineData("(Intro)", true)]
    [InlineData(EscapedHi, false)]
    public void AnOutlineTitleNeedsALanguage(string title, bool reportsUa02)
    {
        var result = Validate(Tagged("", catalogEntries: "/Outlines 7 0 R",
            extra: new[] { "<< /Type /Outlines /First 8 0 R /Last 8 0 R /Count 1 >>", "<< /Title " + title + " /Parent 7 0 R >>" }));
        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("outline title", StringComparison.Ordinal)));
    }

    /// <summary>XMP text in x-default alone needs the catalog's language; beside a named language it does not (7.2-t33).</summary>
    [Theory]
    [InlineData("<rdf:li xml:lang=\"x-default\">T</rdf:li>", true)]
    [InlineData("<rdf:li xml:lang=\"x-default\">T</rdf:li><rdf:li xml:lang=\"en-US\">T</rdf:li>", false)]
    [InlineData("<rdf:li xml:lang=\"en-US\">T</rdf:li>", false)]
    public void XmpTextInXDefaultAloneNeedsTheCatalogLanguage(string items, bool reportsUa02)
    {
        string xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
            + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">"
            + "<pdfuaid:part>1</pdfuaid:part><dc:title><rdf:Alt>" + items + "</rdf:Alt></dc:title>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var result = Validate(Tagged("", catalogEntries: "/Metadata 7 0 R",
            extra: new[] { "<< /Type /Metadata /Subtype /XML /Length " + xmp.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + xmp + "\nendstream" }));

        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("x-default", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA03");   // the packet itself was read
    }

    // ---------------------------------------------------------------- the syntax of a /Lang

    /// <summary>Every /Lang is a language identifier (rule 7.2-29; 7.2-t29's "portugues"): the catalog's, an element's, a marked-content sequence's.</summary>
    [Theory]
    [InlineData("/Lang (portugues)", "", "", true)]
    [InlineData("/Lang (en-US)", "/Lang (en_US)", "", true)]
    [InlineData("/Lang (en-US)", "", "/Span <</Lang (e n)>> BDC EMC", true)]
    [InlineData("/Lang (en-US)", "/Lang ()", "/Span <</Lang (de-CH-1996)>> BDC EMC", false)]
    public void EveryLangValueIsALanguageIdentifier(string catalogEntries, string paragraphEntries, string content, bool reportsUa02)
    {
        var result = Validate(Tagged(content, catalogEntries: catalogEntries, paragraphEntries: paragraphEntries));
        Assert.Equal(reportsUa02, Ua02(result).Any(m => m.Contains("not language identifiers", StringComparison.Ordinal)));
    }

    /// <summary>§14.9.2.1's grammar, read by hand: letters 1–8, then any number of "-" and 1–8 letters or digits; or empty.</summary>
    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("de-CH-1996", true)]
    [InlineData("", true)]
    [InlineData("abcdefgh", true)]
    [InlineData("abcdefghi", false)]
    [InlineData("en-abcdefghi", false)]
    [InlineData("1en", false)]
    [InlineData("en-", false)]
    [InlineData("-en", false)]
    [InlineData("en--US", false)]
    [InlineData("en_US", false)]
    [InlineData("é", false)]
    public void IsLanguageIdentifier_ReadsTheGrammar(string tag, bool valid)
    {
        Assert.Equal(valid, PdfUaChecker.IsLanguageIdentifier(tag));
    }

    // ---------------------------------------------------------------- encrypted

    /// <summary>
    /// Through the ciphertext a structure element's /Lang is still present: with none and no catalog
    /// /Lang nothing declares a language (UA02); with one, the rest cannot be read (UA99).
    /// </summary>
    [Theory]
    [InlineData("", "UA02")]
    [InlineData("/Lang (en)", "UA99")]
    public void AnEncryptedDocumentIsJudgedByWhatItsStructureShows(string paragraphEntries, string code)
    {
        var result = Validate(Tagged("", paragraphEntries: paragraphEntries, encrypted: true));
        Assert.Contains(result.Violations, v => v.Code == code && v.Message.Contains("Lang", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Violations, v => v.Code == (code == "UA02" ? "UA99" : "UA02") && v.Message.Contains("/Lang", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- fixtures

    private static IEnumerable<string> Ua02(PdfUaValidationResult result)
        => result.Violations.Where(v => v.Code == "UA02").Select(v => v.Message).ToList();

    private static PdfUaValidationResult Validate(byte[] pdf) => PdfUaChecker.Validate(new MemoryStream(pdf));

    /// <summary>
    /// Catalog 1, pages 2, page 3, content 4, structure root 5, a Document element 6 holding a P
    /// element (the last object before <paramref name="extra"/>, which start at 7) whose kid is MCID 0.
    /// </summary>
    private static byte[] Tagged(string content, string catalogEntries = "", string paragraphEntries = "",
        string documentEntries = "", string paragraphKids = "", string pageEntries = "", string[] extra = null,
        bool encrypted = false)
    {
        extra ??= Array.Empty<string>();
        int paragraph = 7 + extra.Length;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 5 0 R " + catalogEntries + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> " + pageEntries + " >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 5 0 R /K [" + paragraph + " 0 R] " + documentEntries + " >>",
        };
        objects.AddRange(extra);
        objects.Add("<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0 " + paragraphKids + "] " + paragraphEntries + " >>");
        string trailer = "";
        if (encrypted)
        {
            objects.Add("<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
                + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
                + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>");
            trailer = " /Encrypt " + objects.Count.ToString(CultureInfo.InvariantCulture) + " 0 R";
        }

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R").Append(trailer).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
