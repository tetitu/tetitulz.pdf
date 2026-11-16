// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// FDF and XFDF are how form data leaves a PDF for a system that is not a PDF, and how a filled
/// form comes back. Both directions of both formats are asserted here on the <b>values that come
/// out the far end</b>, never on a call having returned something: an exporter that writes an
/// empty field list produces a perfectly valid file, and an importer that writes nothing leaves a
/// perfectly valid PDF.
/// </summary>
public sealed class FormDataExchangeTests
{
    private const string Hebrew = "שלום עולם";

    private static MemoryStream Stream(byte[] bytes) => new MemoryStream(bytes, writable: false);

    private static byte[] Written(Action<Stream> write)
    {
        using var ms = new MemoryStream();
        write(ms);
        return ms.ToArray();
    }

    private static FormData ExportFrom(byte[] pdf, FormDataExportOptions options = null)
    {
        using var input = Stream(pdf);
        return PdfFormDataExchange.Export(input, options);
    }

    private static string ValueOf(FormData data, string name) =>
        data.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    // ================================================================ export

    /// <summary>Filled fields are exported under their fully-qualified names.</summary>
    [Fact]
    public void Export_CarriesEveryFilledFieldUnderItsFullyQualifiedName()
    {
        FormData data = ExportFrom(FormPdfFixtures.ExchangeForm());

        Assert.Equal("Ada", ValueOf(data, "name"));
        Assert.Equal("London", ValueOf(data, "address.city"));
    }

    /// <summary>
    /// A push button has no value, a signature field's value is a dictionary rather than text, and
    /// an empty field is not data. Exporting any of them puts a name in the file that importing
    /// could never act on.
    /// </summary>
    [Fact]
    public void Export_OmitsPushButtonsSignatureFieldsAndEmptyFields()
    {
        FormData data = ExportFrom(FormPdfFixtures.ExchangeForm());
        var names = data.Fields.Select(f => f.Name).ToList();

        Assert.DoesNotContain("print", names);
        Assert.DoesNotContain("sig", names);
        Assert.DoesNotContain("empty", names);
    }

    /// <summary>...but an empty field IS exported on request, which is how a template of names is made.</summary>
    [Fact]
    public void Export_IncludesEmptyFieldsWhenAsked()
    {
        FormData data = ExportFrom(FormPdfFixtures.ExchangeForm(),
            new FormDataExportOptions { IncludeEmptyFields = true });

        var empty = data.Fields.FirstOrDefault(f => f.Name == "empty");
        Assert.NotNull(empty);
        Assert.Empty(empty.Values);
    }

    /// <summary>
    /// A push button and a signature field stay out <b>even when empty fields are asked for</b> —
    /// they are excluded by what they are, not by having no value.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that makes the type filter falsifiable, and the reason is worth
    /// keeping.</b> Deleting the <c>Signature || IsPushButton</c> clause left every other test in
    /// this file green, because both fields are also valueless and the empty-field rule was
    /// excluding them anyway — the clause was doing nothing that could be observed. Only turning
    /// <see cref="FormDataExportOptions.IncludeEmptyFields"/> on separates the two rules, and a
    /// caller who wants a template of names is precisely the caller who must not be handed a
    /// signature field to fill.
    /// </remarks>
    [Fact]
    public void Export_IncludingEmptyFields_StillOmitsPushButtonsAndSignatureFields()
    {
        FormData data = ExportFrom(FormPdfFixtures.ExchangeForm(),
            new FormDataExportOptions { IncludeEmptyFields = true });
        var names = data.Fields.Select(f => f.Name).ToList();

        Assert.Contains("empty", names);          // the control: empty fields really are included
        Assert.DoesNotContain("print", names);
        Assert.DoesNotContain("sig", names);
    }

    /// <summary>A multi-select list box's whole selection is exported, not just its first entry.</summary>
    [Fact]
    public void Export_KeepsEverySelectionOfAMultiSelectListBox()
    {
        var langs = ExportFrom(FormPdfFixtures.ExchangeForm()).Fields.First(f => f.Name == "langs");

        Assert.True(langs.IsMultiValued);
        Assert.Equal(new[] { "en", "he" }, langs.Values);
    }

    // ================================================================ XFDF

    /// <summary>
    /// A fully-qualified name is written as <b>nested</b> field elements, because §12.7.3.2 forbids
    /// a period in a partial name and a viewer builds the name from the nesting. Writing
    /// <c>name="address.city"</c> flat would read back as a single partial name in a strict reader.
    /// </summary>
    [Fact]
    public void WriteXfdf_SplitsAQualifiedNameIntoNestedFieldElements()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("address.city", "London"));

        XDocument doc = XDocument.Parse(Encoding.UTF8.GetString(
            Written(s => PdfFormDataExchange.WriteXfdf(data, s))));

        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var address = doc.Root.Element(ns + "fields").Elements(ns + "field").Single();
        Assert.Equal("address", (string)address.Attribute("name"));

        var city = address.Elements(ns + "field").Single();
        Assert.Equal("city", (string)city.Attribute("name"));
        Assert.Equal("London", city.Element(ns + "value").Value);
    }

    /// <summary>A multi-select list box writes one value element per selection.</summary>
    [Fact]
    public void WriteXfdf_WritesOneValueElementPerSelection()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("langs", "en", "he"));

        XDocument doc = XDocument.Parse(Encoding.UTF8.GetString(
            Written(s => PdfFormDataExchange.WriteXfdf(data, s))));

        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var values = doc.Root.Element(ns + "fields").Elements(ns + "field").Single()
                        .Elements(ns + "value").Select(v => v.Value).ToList();

        Assert.Equal(new[] { "en", "he" }, values);
    }

    /// <summary>
    /// A value's leading and trailing spaces are the user's data. <c>xml:space="preserve"</c> is
    /// what keeps them; without it a conforming reader is free to collapse them away.
    /// </summary>
    [Fact]
    public void WriteXfdf_PreservesSignificantWhitespaceInAValue()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("code", "  A  B  "));

        byte[] xfdf = Written(s => PdfFormDataExchange.WriteXfdf(data, s));

        XNamespace xml = "http://www.w3.org/XML/1998/namespace";
        Assert.Equal("preserve", (string)XDocument.Parse(Encoding.UTF8.GetString(xfdf))
                                                  .Root.Attribute(xml + "space"));

        using var read = new MemoryStream(xfdf);
        Assert.Equal("  A  B  ", PdfFormDataExchange.ReadXfdf(read).Fields.Single().Value);
    }

    /// <summary>Nested elements read back as the qualified name they encode.</summary>
    [Fact]
    public void ReadXfdf_JoinsNestedFieldNamesWithAPeriod()
    {
        const string xfdf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <fields>
                <field name="address"><field name="city"><value>London</value></field></field>
              </fields>
            </xfdf>
            """;

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
        FormData data = PdfFormDataExchange.ReadXfdf(input);

        Assert.Equal("address.city", data.Fields.Single().Name);
        Assert.Equal("London", data.Fields.Single().Value);
    }

    /// <summary>
    /// A flat file whose name already carries the periods reads the same way. Real producers write
    /// both shapes, and a period cannot be part of a legal partial name, so this is unambiguous.
    /// </summary>
    [Fact]
    public void ReadXfdf_AcceptsAFlatQualifiedName()
    {
        const string xfdf = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <fields><field name="address.city"><value>London</value></field></fields>
            </xfdf>
            """;

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
        Assert.Equal("address.city", PdfFormDataExchange.ReadXfdf(input).Fields.Single().Name);
    }

    /// <summary>A file without the XFDF namespace still reads; producers vary and the data is the same.</summary>
    [Fact]
    public void ReadXfdf_AcceptsAFileWithNoNamespace()
    {
        const string xfdf = "<xfdf><fields><field name=\"name\"><value>Ada</value></field></fields></xfdf>";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
        Assert.Equal("Ada", PdfFormDataExchange.ReadXfdf(input).Fields.Single().Value);
    }

    /// <summary>The source document and its identifier survive a write and a read.</summary>
    [Fact]
    public void Xfdf_RoundTripsTheSourceFileAndDocumentId()
    {
        var data = new FormData
        {
            SourceFile = "form.pdf",
            OriginalId = "0123ABCD",
            ModifiedId = "4567EF89",
        };
        data.Fields.Add(new FormDataField("name", "Ada"));

        using var round = new MemoryStream(Written(s => PdfFormDataExchange.WriteXfdf(data, s)));
        FormData back = PdfFormDataExchange.ReadXfdf(round);

        Assert.Equal("form.pdf", back.SourceFile);
        Assert.Equal("0123ABCD", back.OriginalId);
        Assert.Equal("4567EF89", back.ModifiedId);
    }

    // ================================================================ FDF

    /// <summary>
    /// An FDF is a PDF-syntax file, so this reads the <b>raw bytes</b>: a reader would hide a
    /// missing header, a wrong catalog key, or a second <c>/V</c>, and the writer emits all three
    /// itself.
    /// </summary>
    [Fact]
    public void WriteFdf_EmitsTheFdfHeaderAndCatalog()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", "Ada"));

        string fdf = Encoding.GetEncoding("iso-8859-1")
                             .GetString(Written(s => PdfFormDataExchange.WriteFdf(data, s)));

        Assert.StartsWith("%FDF-1.2", fdf, StringComparison.Ordinal);
        Assert.Contains("/FDF", fdf);
        Assert.Contains("/T (name)", fdf);
        Assert.Contains("/V (Ada)", fdf);
        Assert.Contains("trailer", fdf);
        Assert.EndsWith("%%EOF\n", fdf, StringComparison.Ordinal);
    }

    [Fact]
    public void Fdf_RoundTripsFieldsIncludingAQualifiedNameAndAMultiSelect()
    {
        var data = new FormData { SourceFile = "form.pdf" };
        data.Fields.Add(new FormDataField("name", "Ada"));
        data.Fields.Add(new FormDataField("address.city", "London"));
        data.Fields.Add(new FormDataField("langs", "en", "he"));

        using var round = new MemoryStream(Written(s => PdfFormDataExchange.WriteFdf(data, s)));
        FormData back = PdfFormDataExchange.ReadFdf(round);

        Assert.Equal("form.pdf", back.SourceFile);
        Assert.Equal("Ada", ValueOf(back, "name"));
        Assert.Equal("London", ValueOf(back, "address.city"));
        Assert.Equal(new[] { "en", "he" }, back.Fields.Single(f => f.Name == "langs").Values);
    }

    /// <summary>
    /// A <c>/Kids</c> tree — the other shape §12.7.7.3 allows — reads as the same qualified names.
    /// </summary>
    [Fact]
    public void ReadFdf_JoinsAKidsTreeIntoQualifiedNames()
    {
        string fdf = "%FDF-1.2\n" +
                     "1 0 obj\n<< /FDF << /Fields [<< /T (address) /Kids [<< /T (city) /V (London) >>] >>] >> >>\nendobj\n" +
                     "trailer\n<< /Root 1 0 R >>\n%%EOF\n";

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(fdf));
        FormData data = PdfFormDataExchange.ReadFdf(input);

        Assert.Equal("address.city", data.Fields.Single().Name);
        Assert.Equal("London", data.Fields.Single().Value);
    }

    /// <summary>
    /// The objects are found by scanning, not through the cross-reference table — which
    /// server-generated FDF routinely gets wrong. This file's <c>startxref</c> points at nothing.
    /// </summary>
    [Fact]
    public void ReadFdf_ReadsAFileWhoseCrossReferenceOffsetsAreWrong()
    {
        string fdf = "%FDF-1.2\n" +
                     "1 0 obj\n<< /FDF << /Fields [<< /T (name) /V (Ada) >>] >> >>\nendobj\n" +
                     "xref\n0 2\n0000000000 65535 f \n0000999999 00000 n \n" +
                     "trailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n999999\n%%EOF\n";

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(fdf));
        Assert.Equal("Ada", PdfFormDataExchange.ReadFdf(input).Fields.Single().Value);
    }

    /// <summary>A check box's value is a name, not a string, and reads as its state.</summary>
    [Fact]
    public void ReadFdf_ReadsANameValueAsTheStateName()
    {
        string fdf = "%FDF-1.2\n" +
                     "1 0 obj\n<< /FDF << /Fields [<< /T (agree) /V /Yes >>] >> >>\nendobj\n" +
                     "trailer\n<< /Root 1 0 R >>\n%%EOF\n";

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(fdf));
        Assert.Equal("Yes", PdfFormDataExchange.ReadFdf(input).Fields.Single().Value);
    }

    /// <summary>
    /// Handing XFDF to the FDF reader names the confusion instead of returning an empty result —
    /// "no fields" and "wrong format" are the two things a caller must be able to tell apart.
    /// </summary>
    [Fact]
    public void ReadFdf_GivenXfdf_SaysSoRatherThanReturningNothing()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            "<xfdf><fields><field name=\"name\"><value>Ada</value></field></fields></xfdf>"));

        var ex = Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadFdf(input));
        Assert.Contains("XFDF", ex.Message);
    }

    // ================================================================ Unicode

    /// <summary>
    /// A Hebrew value survives both formats. FDF carries it as a UTF-16BE PDF string with its BOM
    /// and XFDF as UTF-8 — two entirely different encodings, which is why each is asserted rather
    /// than one standing in for the other.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HebrewValue_SurvivesBothFormats(bool xfdf)
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", Hebrew));

        byte[] file = Written(s =>
        {
            if (xfdf) PdfFormDataExchange.WriteXfdf(data, s);
            else PdfFormDataExchange.WriteFdf(data, s);
        });

        using var round = new MemoryStream(file);
        FormData back = xfdf ? PdfFormDataExchange.ReadXfdf(round) : PdfFormDataExchange.ReadFdf(round);

        Assert.Equal(Hebrew, back.Fields.Single().Value);
    }

    /// <summary>
    /// ...and in FDF it really is UTF-16BE, not Latin-1 with the characters destroyed. Reading the
    /// bytes is the only way to see that: a round trip through a reader that made the same mistake
    /// in both directions would agree with itself.
    /// </summary>
    [Fact]
    public void HebrewValue_IsWrittenToFdfAsUtf16BigEndianWithItsByteOrderMark()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", Hebrew));

        byte[] fdf = Written(s => PdfFormDataExchange.WriteFdf(data, s));

        // FE FF, then the first Hebrew letter as a big-endian code unit.
        byte[] expected = { 0xFE, 0xFF, 0x05, 0xE9 };
        Assert.True(IndexOf(fdf, expected) >= 0,
            "the FDF does not contain a UTF-16BE BOM followed by U+05E9");
        Assert.DoesNotContain((byte)'?', fdf.Skip(IndexOf(fdf, Encoding.ASCII.GetBytes("/V"))).Take(32));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // ================================================================ end to end

    /// <summary>
    /// The whole point of the feature: values leave one document and arrive in another, through a
    /// file that is not a PDF.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExportThenImport_MovesValuesBetweenDocuments(bool xfdf)
    {
        byte[] filled = FormPdfFixtures.ExchangeForm(cityValue: "Haifa");

        byte[] file = Written(s =>
        {
            using var input = Stream(filled);
            if (xfdf) PdfOperations.ExportFormDataAsXfdf(input, s);
            else PdfOperations.ExportFormDataAsFdf(input, s);
        });

        // A blank copy of the same form is the target, so a value that arrives came from the file.
        byte[] blank = FormPdfFixtures.ExchangeForm(cityValue: "");

        byte[] result = Written(s =>
        {
            using var input = Stream(blank);
            using var data = new MemoryStream(file);
            if (xfdf) PdfOperations.ImportFormDataFromXfdf(input, data, s);
            else PdfOperations.ImportFormDataFromFdf(input, data, s);
        });

        using var check = new MemoryStream(result);
        var fields = PdfOperations.GetFormFields(check);

        Assert.Equal("Haifa", fields.Single(f => f.FullyQualifiedName == "address.city").Value);
        Assert.Equal("Ada", fields.Single(f => f.FullyQualifiedName == "name").Value);
    }

    /// <summary>
    /// A multi-select list box keeps every selection through an import — the fill writes the
    /// array <c>/V</c> (§12.7.4.4) — and round-trips through an export, so the exchange loses nothing.
    /// </summary>
    [Fact]
    public void Import_KeepsEverySelectionOfAMultiSelect_AndExportReadsThemBack()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("langs", "he", "fr"));

        using var input = Stream(FormPdfFixtures.ExchangeForm());
        using var output = new MemoryStream();
        FormFillResult result = PdfFormDataExchange.Import(input, data, output);

        Assert.Contains("langs", result.FilledFields);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("only the first", StringComparison.Ordinal));
        FormData back = PdfFormDataExchange.Export(new MemoryStream(output.ToArray()));
        Assert.Equal(new[] { "he", "fr" }, back.Fields.Single(f => f.Name == "langs").Values);
    }

    // ================================================================ hostile input

    /// <summary>
    /// An XFDF naming an external entity must not fetch it. The DTD is prohibited outright, so the
    /// read is refused — the caller learns, rather than receiving a file quietly stripped of part
    /// of its content.
    /// </summary>
    [Fact]
    public void ReadXfdf_MalformedWithAnExternalEntity_IsRefusedAndFetchesNothing()
    {
        string secret = Path.Combine(Path.GetTempPath(), "tetitulz-xxe-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOP-SECRET-MARKER");
        try
        {
            string xfdf =
                "<?xml version=\"1.0\"?>" +
                "<!DOCTYPE xfdf [<!ENTITY xxe SYSTEM \"file:///" + secret.Replace('\\', '/') + "\">]>" +
                "<xfdf><fields><field name=\"name\"><value>&xxe;</value></field></fields></xfdf>";

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf));
            var ex = Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadXfdf(input));

            // The refusal is the point, but so is what did NOT happen: the file's contents must
            // appear nowhere, including in the message a caller might log.
            Assert.DoesNotContain("TOP-SECRET-MARKER", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    /// <summary>
    /// The billion-laughs shape needs a DTD to declare its entities, so prohibiting the DTD closes
    /// it too — refused before any expansion is attempted.
    /// </summary>
    [Fact]
    public void ReadXfdf_AdversarialEntityExpansion_IsRefused()
    {
        var xfdf = new StringBuilder();
        xfdf.Append("<?xml version=\"1.0\"?><!DOCTYPE xfdf [");
        xfdf.Append("<!ENTITY a0 \"aaaaaaaaaa\">");
        for (int i = 1; i <= 9; i++)
            xfdf.Append($"<!ENTITY a{i} \"&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};\">");
        xfdf.Append("]><xfdf><fields><field name=\"n\"><value>&a9;</value></field></fields></xfdf>");

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf.ToString()));
        Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadXfdf(input));
    }

    /// <summary>An XFDF nested past the field-tree depth cap is refused rather than recursed into.</summary>
    [Fact]
    public void ReadXfdf_AdversarialNestingDepth_IsRefused()
    {
        var xfdf = new StringBuilder("<xfdf><fields>");
        const int depth = 400;
        for (int i = 0; i < depth; i++) xfdf.Append("<field name=\"f\">");
        xfdf.Append("<value>x</value>");
        for (int i = 0; i < depth; i++) xfdf.Append("</field>");
        xfdf.Append("</fields></xfdf>");

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xfdf.ToString()));
        var ex = Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadXfdf(input));
        Assert.Contains("deep", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An FDF whose /Kids point at each other terminates instead of recursing forever.</summary>
    [Fact]
    public void ReadFdf_AdversarialKidsCycle_DoesNotHang()
    {
        string fdf = "%FDF-1.2\n" +
                     "1 0 obj\n<< /FDF << /Fields [2 0 R] >> >>\nendobj\n" +
                     "2 0 obj\n<< /T (a) /Kids [3 0 R] >>\nendobj\n" +
                     "3 0 obj\n<< /T (b) /Kids [2 0 R] >>\nendobj\n" +
                     "trailer\n<< /Root 1 0 R >>\n%%EOF\n";

        using var input = new MemoryStream(Encoding.ASCII.GetBytes(fdf));

        // The guard's own evidence: a typed refusal naming the depth, not a wall-clock ceiling.
        var ex = Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadFdf(input));
        Assert.Contains("deep", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Malformed XML is reported as such, not as an empty field list.</summary>
    [Fact]
    public void ReadXfdf_MalformedXml_IsReportedRatherThanReadAsEmpty()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("<xfdf><fields><field name="));
        Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadXfdf(input));
    }

    // ================================================================ conversion

    /// <summary>
    /// The two formats meet in <see cref="FormData"/>, so converting between them is a read and a
    /// write — which is the property that makes one model rather than two the right design.
    /// </summary>
    [Fact]
    public void FdfAndXfdf_ConvertIntoEachOtherThroughTheSharedModel()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", Hebrew));
        data.Fields.Add(new FormDataField("address.city", "London"));

        using var asFdf = new MemoryStream(Written(s => PdfFormDataExchange.WriteFdf(data, s)));
        FormData viaFdf = PdfFormDataExchange.ReadFdf(asFdf);

        using var asXfdf = new MemoryStream(Written(s => PdfFormDataExchange.WriteXfdf(viaFdf, s)));
        FormData viaXfdf = PdfFormDataExchange.ReadXfdf(asXfdf);

        Assert.Equal(Hebrew, ValueOf(viaXfdf, "name"));
        Assert.Equal("London", ValueOf(viaXfdf, "address.city"));
    }
}
