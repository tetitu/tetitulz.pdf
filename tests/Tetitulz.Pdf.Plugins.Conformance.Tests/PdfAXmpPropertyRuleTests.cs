// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// PDF/A's XMP property rules: A34 (a property no predefined or described schema defines), A35 (a
/// value not of its declared type), A36 (a malformed extension schema description) and A07's
/// required <c>pdfaid</c> prefix — ISO 19005-1 6.7.8, 6.7.9 and 6.7.11; ISO 19005-2/3 6.6.2.3 and
/// 6.6.4; the conformance corpus is the wider oracle (<see cref="ConformanceCorpusTests"/>).
/// </summary>
public class PdfAXmpPropertyRuleTests
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    private const string Namespaces =
        " xmlns:dc=\"http://purl.org/dc/elements/1.1/\"" +
        " xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"" +
        " xmlns:xmpMM=\"http://ns.adobe.com/xap/1.0/mm/\"" +
        " xmlns:tiff=\"http://ns.adobe.com/tiff/1.0/\"" +
        " xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"" +
        " xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\"" +
        " xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"" +
        " xmlns:pdfaType=\"http://www.aiim.org/pdfa/ns/type#\"" +
        " xmlns:pdfaField=\"http://www.aiim.org/pdfa/ns/field#\"" +
        " xmlns:cs=\"http://example.com/custom/\"" +
        " xmlns:ct=\"http://example.com/customtype/\"";

    /// <summary>A packet declaring <paramref name="part"/> and holding <paramref name="body"/>'s properties.</summary>
    private static string Packet(int part, string body, string? identification = null)
    {
        string conformance = part == 4 ? "" : " pdfaid:conformance=\"B\"";
        identification ??= "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"" +
                           " pdfaid:part=\"" + part.ToString(CultureInfo.InvariantCulture) + "\"" +
                           (part == 4 ? " pdfaid:rev=\"2020\"" : conformance) + "/>";
        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
               "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\">" +
               identification +
               "<rdf:Description rdf:about=\"\"" + Namespaces + ">" + body + "</rdf:Description>" +
               "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    /// <summary>
    /// <b>Adversarial: twenty thousand distinct undefined properties.</b> Each finding was kept
    /// distinct by a search of the list before it, so n findings cost n² / 2 comparisons — 5 × 10⁹
    /// from a packet of a megabyte or two at a hundred thousand; a set beside the list keeps them
    /// distinct now, and is live: it holds every finding, a repeated property counted once.
    /// </summary>
    [Fact]
    public void AdversarialManyUndefinedProperties_AreKeptDistinctBySet()
    {
        var body = new StringBuilder();
        for (int i = 0; i < 20_000; i++) body.Append("<cs:p").Append(i.ToString(CultureInfo.InvariantCulture)).Append(">x</cs:p").Append(i.ToString(CultureInfo.InvariantCulture)).Append('>');
        body.Append("<cs:p0>again</cs:p0>");
        var xml = new System.Xml.XmlDocument { XmlResolver = null };
        xml.LoadXml(Packet(2, body.ToString()).Replace("<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>", "").Replace("<?xpacket end=\"w\"?>", ""));

        var findings = XmpPropertyRules.Check(xml, XmpPredefinedSchemas.ForPart(PdfAConformancePart.Part2));

        Assert.Equal(20_000, findings.Undefined.Count);
        Assert.Equal(20_000, findings.Undefined.Seen);
        Assert.Equal("cs:p0", findings.Undefined.Items[0]);
        Assert.StartsWith("cs:p0, cs:p1, cs:p2, cs:p3, cs:p4 and 19995 more", XmpPropertyRules.Describe(findings.Undefined), StringComparison.Ordinal);
    }

    private static byte[] Document(string packet)
    {
        byte[] xmp = Encoding.UTF8.GetBytes(packet);
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"),
            Concat(Encoding.ASCII.GetBytes("<< /Type /Metadata /Subtype /XML /Length " +
                                           xmp.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"),
                   xmp, Encoding.ASCII.GetBytes("\nendstream")),
        };
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n%");
        ms.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 5);
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            Write((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            Write("\nendobj\n");
        }
        long xref = ms.Position;
        Write("xref\n0 " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (long o in offsets) Write(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) +
              " /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\nstartxref\n" +
              xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static PdfAValidationResult Validate(int part, string body, string? identification = null)
    {
        using var ms = new MemoryStream(Document(Packet(part, body, identification)));
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part);
    }

    private static List<PdfAViolation> Coded(PdfAValidationResult r, string code)
        => r.Violations.Where(v => v.Code == code).ToList();

    // -------------------------------------------------------------- A34: defined at all

    [Fact]
    public void A34_XmpRating_IsUndefinedForPdfA1_ButDefinedForPdfA2()
    {
        // xmp:Rating first appears in the June 2005 edition, which PDF/A-2 reads and PDF/A-1 does not.
        const string body = "<xmp:Rating>3</xmp:Rating>";
        var part1 = Coded(Validate(1, body), "A34");
        Assert.Single(part1);
        Assert.Contains("xmp:Rating", part1[0].Message, StringComparison.Ordinal);
        Assert.Empty(Coded(Validate(2, body), "A34"));
    }

    [Fact]
    public void A34_AnAttributeProperty_IsCheckedLikeAnElement()
    {
        var r = Validate(1, "", "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"" +
                                " xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" pdfaid:part=\"1\" pdfaid:conformance=\"B\"" +
                                " xmp:Author=\"Someone\" xmp:CreatorTool=\"Tool\"/>");
        var a34 = Coded(r, "A34");
        Assert.Single(a34);
        Assert.Contains("xmp:Author", a34[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatorTool", a34[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A34_XmpMMInstanceID_IsAcceptedInBothParts_AsTheProfilesDo()
    {
        const string body = "<xmpMM:InstanceID>uuid:1e9aa632-1509-4d61-ac69-72ba38a567d3</xmpMM:InstanceID>";
        Assert.Empty(Coded(Validate(1, body), "A34"));
        Assert.Empty(Coded(Validate(2, body), "A34"));
        // Potency: a sibling xmpMM name neither edition defines is still refused.
        Assert.Single(Coded(Validate(2, "<xmpMM:Pantry>x</xmpMM:Pantry>"), "A34"));
    }

    [Fact]
    public void A34_ManyUndefinedProperties_NameTheFirstFiveAndCountTheRest()
    {
        string body = string.Concat(Enumerable.Range(1, 7).Select(i => $"<cs:P{i}>x</cs:P{i}>"));
        var a34 = Coded(Validate(2, body), "A34");
        Assert.Single(a34);
        Assert.Contains("cs:P1, cs:P2, cs:P3, cs:P4, cs:P5 and 2 more", a34[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cs:P6", a34[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A34_IsNotAPdfA4Rule()
    {
        Assert.Empty(Coded(Validate(4, "<cs:Undescribed>x</cs:Undescribed>"), "A34"));
        Assert.Single(Coded(Validate(2, "<cs:Undescribed>x</cs:Undescribed>"), "A34"));
    }

    // -------------------------------------------------------------- A35: value types

    [Theory]
    [InlineData("<dc:creator>Someone</dc:creator>", "dc:creator")]                                   // seq ProperName as text
    [InlineData("<dc:contributor><rdf:Seq><rdf:li>a</rdf:li></rdf:Seq></dc:contributor>", "dc:contributor")] // bag, not seq
    [InlineData("<dc:title><rdf:Alt><rdf:li>t</rdf:li></rdf:Alt></dc:title>", "dc:title")]              // Lang Alt without xml:lang
    [InlineData("<dc:title>t</dc:title>", "dc:title")]
    [InlineData("<tiff:ImageWidth>256 mm</tiff:ImageWidth>", "tiff:ImageWidth")]                      // Integer
    [InlineData("<tiff:ImageWidth>8.0</tiff:ImageWidth>", "tiff:ImageWidth")]
    [InlineData("<tiff:XResolution>3.1</tiff:XResolution>", "tiff:XResolution")]                      // Rational
    [InlineData("<tiff:XResolution>8/1.5</tiff:XResolution>", "tiff:XResolution")]
    [InlineData("<xmp:CreateDate>2016-02-01TIME13:19:21+01:00</xmp:CreateDate>", "xmp:CreateDate")]   // Date
    [InlineData("<xmp:CreateDate>2016-13-01</xmp:CreateDate>", "xmp:CreateDate")]
    [InlineData("<xmp:CreateDate>2016-02-01T13:19:21.+01:00</xmp:CreateDate>", "xmp:CreateDate")]
    [InlineData("<xmp:CreateDate>2016-02-01T13:19:21+0100</xmp:CreateDate>", "xmp:CreateDate")]
    [InlineData("<xmpRights:Marked xmlns:xmpRights=\"http://ns.adobe.com/xap/1.0/rights/\">FALSE</xmpRights:Marked>", "xmpRights:Marked")] // Boolean
    [InlineData("<xmp:CreateDate>2016-02-01T13:19:21Z+01:00</xmp:CreateDate>", "xmp:CreateDate")]
    [InlineData("<xmpMM:DerivedFrom>a reference</xmpMM:DerivedFrom>", "xmpMM:DerivedFrom")]           // structure as text
    [InlineData("<tiff:YCbCrSubSampling>2,1</tiff:YCbCrSubSampling>", "tiff:YCbCrSubSampling")]        // closed choice of seq Integer
    public void A35_AValueOfTheWrongShapeOrForm_IsReported(string body, string property)
    {
        var a35 = Coded(Validate(1, body), "A35");
        Assert.Single(a35);
        Assert.Contains(property + " (", a35[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<dc:creator><rdf:Seq><rdf:li>Someone</rdf:li></rdf:Seq></dc:creator>")]
    [InlineData("<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">t</rdf:li></rdf:Alt></dc:title>")]
    [InlineData("<tiff:ImageWidth> +256 </tiff:ImageWidth>")]
    [InlineData("<tiff:XResolution>-18/5</tiff:XResolution>")]
    [InlineData("<xmp:CreateDate>1997</xmp:CreateDate>")]
    [InlineData("<xmp:CreateDate>1997-07-16T19:20+01:00</xmp:CreateDate>")]
    [InlineData("<xmp:CreateDate>1997-07-16T19:20:15.45Z</xmp:CreateDate>")]
    [InlineData("<xmpMM:DerivedFrom rdf:parseType=\"Resource\"><stRef:instanceID xmlns:stRef=\"http://ns.adobe.com/xap/1.0/sType/ResourceRef#\">uuid:1</stRef:instanceID></xmpMM:DerivedFrom>")]
    [InlineData("<xmpMM:DerivedFrom><rdf:Description xmlns:stRef=\"http://ns.adobe.com/xap/1.0/sType/ResourceRef#\" stRef:instanceID=\"uuid:1\"/></xmpMM:DerivedFrom>")]
    [InlineData("<tiff:YCbCrSubSampling><rdf:Seq><rdf:li>2</rdf:li><rdf:li>1</rdf:li></rdf:Seq></tiff:YCbCrSubSampling>")]
    [InlineData("<xmp:CreatorTool><rdf:Description><rdf:value>Tool</rdf:value><xmpidq:Scheme xmlns:xmpidq=\"http://ns.adobe.com/xmp/Identifier/qual/1.0/\">s</xmpidq:Scheme></rdf:Description></xmp:CreatorTool>")]
    [InlineData("<xmp:CreatorTool xmlns:xmpidq=\"http://ns.adobe.com/xmp/Identifier/qual/1.0/\" rdf:value=\"Tool\" xmpidq:Scheme=\"s\"/>")]
    public void A35_AValueOfTheDeclaredType_IsAccepted(string body)
    {
        var r = Validate(1, body);
        Assert.Empty(Coded(r, "A35"));
        Assert.Empty(Coded(r, "A34"));
        // Potency: the identical document with an undefined property beside it is judged.
        Assert.Single(Coded(Validate(1, body + "<cs:Extra>x</cs:Extra>"), "A34"));
    }

    [Fact]
    public void A35_XmpRatingAsAReal_IsAcceptedForPdfA2_AsTheProfilesDo()
    {
        Assert.Empty(Coded(Validate(2, "<xmp:Rating>1.0</xmp:Rating>"), "A35"));
        Assert.Single(Coded(Validate(2, "<xmp:Rating>one</xmp:Rating>"), "A35"));
    }

    [Fact]
    public void A35_ValueNestedPastTheDepthCap_IsReportedAsPartialNotJudged_HostileNesting()
    {
        int depth = 200;
        var sb = new StringBuilder("<xmpMM:DerivedFrom>");
        for (int i = 0; i < depth; i++) sb.Append("<rdf:Description><cs:n>");
        sb.Append("x");
        for (int i = 0; i < depth; i++) sb.Append("</cs:n></rdf:Description>");
        sb.Append("</xmpMM:DerivedFrom>");
        var r = Validate(1, sb.ToString());
        Assert.Empty(Coded(r, "A35"));
        Assert.Contains(r.Informational, v => v.Code == "A99" && v.Message.Contains("nests deeper", StringComparison.Ordinal));
        // Control: within the cap the same shape is judged, and there is no partial entry.
        var shallow = Validate(1, "<xmpMM:DerivedFrom><rdf:Description><cs:n>x</cs:n></rdf:Description></xmpMM:DerivedFrom>");
        Assert.DoesNotContain(shallow.Informational, v => v.Message.Contains("nests deeper", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- extension schemas

    private static string Extension(string schemaFields, string? properties = null, string? valueTypes = null)
        => "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">" + schemaFields +
           (properties == null ? "" : "<pdfaSchema:property><rdf:Seq>" + properties + "</rdf:Seq></pdfaSchema:property>") +
           (valueTypes == null ? "" : "<pdfaSchema:valueType><rdf:Seq>" + valueTypes + "</rdf:Seq></pdfaSchema:valueType>") +
           "</rdf:li></rdf:Bag></pdfaExtension:schemas>";

    private const string SchemaFields =
        "<pdfaSchema:schema>Custom</pdfaSchema:schema>" +
        "<pdfaSchema:namespaceURI>http://example.com/custom/</pdfaSchema:namespaceURI>" +
        "<pdfaSchema:prefix>cs</pdfaSchema:prefix>";

    private static string Property(string name, string type, string category = "external")
        => "<rdf:li rdf:parseType=\"Resource\"><pdfaProperty:name>" + name + "</pdfaProperty:name>" +
           "<pdfaProperty:valueType>" + type + "</pdfaProperty:valueType>" +
           "<pdfaProperty:category>" + category + "</pdfaProperty:category>" +
           "<pdfaProperty:description>d</pdfaProperty:description></rdf:li>";

    private const string ArticleType =
        "<rdf:li rdf:parseType=\"Resource\"><pdfaType:type>Article</pdfaType:type>" +
        "<pdfaType:namespaceURI>http://example.com/customtype/</pdfaType:namespaceURI>" +
        "<pdfaType:prefix>ct</pdfaType:prefix><pdfaType:description>d</pdfaType:description>" +
        "<pdfaType:field><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><pdfaField:name>number</pdfaField:name>" +
        "<pdfaField:valueType>Text</pdfaField:valueType><pdfaField:description>d</pdfaField:description>" +
        "</rdf:li></rdf:Seq></pdfaType:field></rdf:li>";

    [Fact]
    public void ExtensionSchema_DescribesAProperty_AndItsType_ThenTheValueIsChecked()
    {
        string ext = Extension(SchemaFields, Property("Count", "Integer") + Property("Item", "Article"), ArticleType);
        var good = Validate(2, ext + "<cs:Count>3</cs:Count><cs:Item rdf:parseType=\"Resource\"><ct:number>M17</ct:number></cs:Item>");
        Assert.Empty(Coded(good, "A34"));
        Assert.Empty(Coded(good, "A35"));
        Assert.Empty(Coded(good, "A36"));

        var badValue = Validate(2, ext + "<cs:Count>three</cs:Count><cs:Item>M17</cs:Item>");
        var a35 = Coded(badValue, "A35");
        Assert.Single(a35);
        Assert.Contains("cs:Count (Integer)", a35[0].Message, StringComparison.Ordinal);
        Assert.Contains("cs:Item (Article)", a35[0].Message, StringComparison.Ordinal);

        // Without the description the same property is undefined.
        Assert.Single(Coded(Validate(2, "<cs:Count>3</cs:Count>"), "A34"));
    }

    private const string IssueType =
        "<rdf:li rdf:parseType=\"Resource\"><pdfaType:type>Issue</pdfaType:type>" +
        "<pdfaType:namespaceURI>http://example.com/customtype/</pdfaType:namespaceURI>" +
        "<pdfaType:prefix>ct</pdfaType:prefix><pdfaType:description>d</pdfaType:description>" +
        "<pdfaType:field><rdf:Seq><rdf:li rdf:parseType=\"Resource\"><pdfaField:name>volume</pdfaField:name>" +
        "<pdfaField:valueType>Integer</pdfaField:valueType><pdfaField:description>d</pdfaField:description>" +
        "</rdf:li></rdf:Seq></pdfaType:field></rdf:li>";

    private const string FieldlessType =
        "<rdf:li rdf:parseType=\"Resource\"><pdfaType:type>Stamp</pdfaType:type>" +
        "<pdfaType:namespaceURI>http://example.com/customtype/</pdfaType:namespaceURI>" +
        "<pdfaType:prefix>ct</pdfaType:prefix><pdfaType:description>d</pdfaType:description></rdf:li>";

    /// <summary>
    /// A value of a value type the packet describes uses only the fields the type declares, under its
    /// namespace, each of its declared type (ISO 19005-2/3 6.6.2.3.3; the corpus's
    /// 6-6-2-3-3-t03-fail-b uses a field of a type that declares none).
    /// </summary>
    [Theory]
    [InlineData("a declared field", "Article", "<ct:number>M17</ct:number>", false)]
    [InlineData("a declared Integer field", "Issue", "<ct:volume>12</ct:volume>", false)]
    [InlineData("no field of a fieldless type", "Stamp", "", false)]
    [InlineData("an undeclared field", "Article", "<ct:number>M17</ct:number><ct:pages>9</ct:pages>", true)]
    [InlineData("a declared name under another namespace", "Article", "<cs:number>M17</cs:number>", true)]
    [InlineData("a field of the declared name but not its type", "Issue", "<ct:volume>twelve</ct:volume>", true)]
    [InlineData("a field of a type that declares none", "Stamp", "<ct:number>M17</ct:number>", true)]
    public void A35_AStructureOfADescribedType_UsesOnlyItsDeclaredFields(string why, string type, string fields, bool reported)
    {
        string ext = Extension(SchemaFields, Property("Item", type), ArticleType + IssueType + FieldlessType);
        var r = Validate(2, ext + "<cs:Item rdf:parseType=\"Resource\">" + fields + "</cs:Item>");

        Assert.True(Coded(r, "A36").Count == 0, why + ": the description itself is well formed");
        var a35 = Coded(r, "A35");
        Assert.True(a35.Count == (reported ? 1 : 0), why + ": " + string.Join(" | ", a35.Select(v => v.Message)));
        if (reported) Assert.Contains("cs:Item (" + type + ")", a35[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing prefix", "<pdfaSchema:schema>Custom</pdfaSchema:schema><pdfaSchema:namespaceURI>http://example.com/custom/</pdfaSchema:namespaceURI>", "pdfaSchema:prefix is missing")]
    [InlineData("wrong prefix", "<x:schema xmlns:x=\"http://www.aiim.org/pdfa/ns/schema#\">Custom</x:schema><pdfaSchema:namespaceURI>http://example.com/custom/</pdfaSchema:namespaceURI><pdfaSchema:prefix>cs</pdfaSchema:prefix>", "x:schema is not written under the prefix pdfaSchema")]
    [InlineData("foreign field", SchemaFields + "<cs:note>n</cs:note>", "cs:note is not a field of the Schema type")]
    public void A36_AMalformedSchemaDescription_IsReported(string why, string schemaFields, string expected)
    {
        var a36 = Coded(Validate(2, Extension(schemaFields, Property("Count", "Integer"))), "A36");
        Assert.True(a36.Count == 1, why);
        Assert.Contains(expected, a36[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Integer", "external", null)]
    [InlineData("seq Article", "internal", null)]
    [InlineData("Closed Choice of Integer", "internal", null)]
    [InlineData("seq Widget", "external", "pdfaProperty:valueType \"seq Widget\" is a type neither")]
    [InlineData("Widget", "external", "pdfaProperty:valueType \"Widget\" is a type neither")]
    [InlineData("Integer", "External", "pdfaProperty:category \"External\" is neither")]
    public void A36_APropertyDescriptionsTypeAndCategory_AreChecked(string type, string category, string? expected)
    {
        var a36 = Coded(Validate(2, Extension(SchemaFields, Property("Count", type, category), ArticleType)), "A36");
        if (expected == null)
            Assert.Empty(a36);
        else
            Assert.Contains(expected, Assert.Single(a36).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A valueType past <see cref="XmpPropertyRules.MaxValueTypeLength"/> is no type the packet can
    /// define: each of its prefixes cost a call and a copy of the rest, so a packet of 100 000
    /// <c>Bag </c> prefixes was some 10^10 characters copied. It is reported as undefined, and the
    /// property's value is not judged against it; a type of the same shape under the bound is
    /// accepted (the control).
    /// </summary>
    [Fact]
    public void AdversarialValueTypeOfManyPrefixes_IsReportedUndefined_NotWalked()
    {
        string hostile = string.Concat(Enumerable.Repeat("Bag ", 100_000)) + "Text";
        var a36 = Coded(Validate(2, Extension(SchemaFields, Property("Count", hostile, "internal"))), "A36");
        Assert.Contains("is a type neither", Assert.Single(a36).Message, StringComparison.Ordinal);

        string deepButShort = string.Concat(Enumerable.Repeat("Bag ", 3)) + "Text";
        Assert.Empty(Coded(Validate(2, Extension(SchemaFields, Property("Count", deepButShort, "internal"))), "A36"));
    }

    [Fact]
    public void A36_AFieldDescriptionMissingItsDescription_IsReported_AndAWellFormedOneIsNot()
    {
        string broken = ArticleType.Replace("<pdfaField:description>d</pdfaField:description>", "", StringComparison.Ordinal);
        var a36 = Coded(Validate(1, Extension(SchemaFields, null, broken)), "A36");
        Assert.Contains("pdfaField:description is missing from a Field", Assert.Single(a36).Message, StringComparison.Ordinal);
        Assert.Contains("ISO 19005-1 6.7.8", a36[0].Message, StringComparison.Ordinal);
        Assert.Empty(Coded(Validate(1, Extension(SchemaFields, null, ArticleType)), "A36"));
    }

    [Fact]
    public void A36_ADescriptionsPropertyListThatIsNotASeq_IsReported()
    {
        string bag = Extension(SchemaFields, Property("Count", "Integer"))
            .Replace("<pdfaSchema:property><rdf:Seq>", "<pdfaSchema:property><rdf:Bag>", StringComparison.Ordinal)
            .Replace("</rdf:Seq></pdfaSchema:property>", "</rdf:Bag></pdfaSchema:property>", StringComparison.Ordinal);
        Assert.Contains("pdfaSchema:property is not a Seq", Assert.Single(Coded(Validate(2, bag), "A36")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A36_AContainerUnderAnotherPrefix_IsReported()
    {
        string other = Extension(SchemaFields)
            .Replace("<pdfaExtension:schemas>", "<ext:schemas xmlns:ext=\"http://www.aiim.org/pdfa/ns/extension/\">", StringComparison.Ordinal)
            .Replace("</pdfaExtension:schemas>", "</ext:schemas>", StringComparison.Ordinal);
        Assert.Contains("ext:schemas is not written under the prefix pdfaExtension",
            Assert.Single(Coded(Validate(2, other), "A36")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A36_AContainerThatIsNotABag_IsReported()
    {
        string seq = Extension(SchemaFields).Replace("rdf:Bag", "rdf:Seq", StringComparison.Ordinal);
        Assert.Contains("pdfaExtension:schemas is not a Bag", Assert.Single(Coded(Validate(2, seq), "A36")).Message,
            StringComparison.Ordinal);
        Assert.Empty(Coded(Validate(2, Extension(SchemaFields)), "A36"));
    }

    // -------------------------------------------------------------- A07: the pdfaid prefix

    [Fact]
    public void A07_IdentificationUnderAnotherPrefix_IsReported_ThoughItsNamespaceIsRight()
    {
        string other = "<rdf:Description rdf:about=\"\" xmlns:id=\"http://www.aiim.org/pdfa/ns/id/\" id:part=\"2\" id:conformance=\"B\"/>";
        var a07 = Coded(Validate(2, "", other), "A07");
        Assert.Equal(2, a07.Count);
        Assert.Contains(a07, v => v.Message.Contains("id:part is not written under the prefix pdfaid", StringComparison.Ordinal));
        Assert.Contains(a07, v => v.Message.Contains("id:conformance", StringComparison.Ordinal));
        Assert.Empty(Coded(Validate(2, ""), "A07"));
    }
}
