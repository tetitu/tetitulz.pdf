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
/// What PDF/X reports where ISO 15930-7 and 15930-9 were not available (GAPS.md §12b): the findings it
/// names without judging (X26 a halftone's transfer function, X27 an optional content configuration),
/// ISO 32000's own optional content entries (X13), and PDF/X-6 checked in part — violations only where
/// a public source states the rule, X-4's other rules carried as informational.
/// </summary>
public class PdfXWithoutTheStandardTests
{
    private const string Screen = "/Frequency 60 /Angle 45 /SpotFunction /Round";

    // ================================================================== X26

    [Fact]
    public void X26_AHalftoneTransferFunction_IsNamedNotJudged_AtEveryLevel()
    {
        var f = new Fixture { PageResources = "/ExtGState << /G0 << /HT << /HalftoneType 1 " + Screen + " /TransferFunction /Identity >> >> >>" };
        foreach (var level in new[] { PdfXConformanceLevel.X1a, PdfXConformanceLevel.X3, PdfXConformanceLevel.X4, PdfXConformanceLevel.X6 })
        {
            var r = f.Validate(level);
            var x26 = Assert.Single(r.Informational, i => i.Code == "X26");
            Assert.Contains("/ExtGState /G0 /HT (one ISO 32000-1 does not require)", x26.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(r.Violations, v => v.Code == "X26");
        }

        // The control: the same halftone without one.
        var clean = new Fixture { PageResources = "/ExtGState << /G0 << /HT << /HalftoneType 1 " + Screen + " >> >> >>" };
        Assert.DoesNotContain(clean.Validate(PdfXConformanceLevel.X4).Informational, i => i.Code == "X26");
    }

    [Fact]
    public void X26_ATypeFiveComponentForASpotColorant_IsNamedAsOneIso32000Requires()
    {
        var f = new Fixture
        {
            PageResources = "/ExtGState << /G0 << /HT << /HalftoneType 5 /Default << /HalftoneType 1 " + Screen + " >> " +
                            "/Gold << /HalftoneType 1 " + Screen + " /TransferFunction /Identity >> >> >> >>",
        };
        var x26 = Assert.Single(f.Validate(PdfXConformanceLevel.X4).Informational, i => i.Code == "X26");
        Assert.Contains("/HT /Gold (one ISO 32000-1 Table 130 requires, for a non-primary colorant)", x26.Message, StringComparison.Ordinal);
    }

    // ================================================================== X27

    [Theory]
    [InlineData("/D << /Order [5 0 R] >>", "/D has no /Name")]
    [InlineData("/D << /Name () >>", "/D has an empty /Name")]
    [InlineData("/D << /Name (D) /AS [] >>", "/D has an /AS entry")]
    [InlineData("/D << /Name (S) >> /Configs [<< /Name (S) >>]", "the /Name (S) is not unique")]
    public void X27_AtX4_PdfA2sConditions_AreNamedNotJudged(string entries, string expected)
    {
        var r = new Fixture { OcProperties = "/OCGs [5 0 R 6 0 R] " + entries }.Validate(PdfXConformanceLevel.X4);
        var x27 = Assert.Single(r.Informational, i => i.Code == "X27");
        Assert.Contains(expected, x27.Message, StringComparison.Ordinal);
        Assert.Contains("ISO 15930-7 6.24", x27.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Violations, v => v.Code == "X27");
    }

    /// <summary>ISO 15930-7:2010's foreword says that edition removed the /Order restriction: X-4 is not
    /// told about an /Order that leaves a group out, while X-6 (PDF/A-4's reading) is.</summary>
    [Fact]
    public void X27_AnIncompleteOrder_IsNotX4sConcern_ButIsReportedAtX6()
    {
        var f = new Fixture { OcProperties = "/OCGs [5 0 R 6 0 R] /D << /Name (D) /Order [5 0 R] >>" };
        Assert.DoesNotContain(f.Validate(PdfXConformanceLevel.X4).Informational, i => i.Code == "X27");
        var x27 = Assert.Single(f.Validate(PdfXConformanceLevel.X6).Informational, i => i.Code == "X27");
        Assert.Contains("/D's /Order leaves out 1 of 2 groups", x27.Message, StringComparison.Ordinal);
    }

    /// <summary>X-6 reads the configurations as PDF/A-4 does: a /Name present is enough, and /AS is no concern.
    /// X-4 (PDF/A-2's reading) names both.</summary>
    [Fact]
    public void X27_AtX6_AnEmptyNameAndAnAsEntry_AreNotNamed()
    {
        var f = new Fixture { OcProperties = "/OCGs [5 0 R 6 0 R] /D << /Name () /AS [] /Order [5 0 R 6 0 R] >>" };
        Assert.DoesNotContain(f.Validate(PdfXConformanceLevel.X6).Informational, i => i.Code == "X27");
        var x4 = Assert.Single(f.Validate(PdfXConformanceLevel.X4).Informational, i => i.Code == "X27");
        Assert.Contains("/D has an empty /Name", x4.Message, StringComparison.Ordinal);
        Assert.Contains("/D has an /AS entry", x4.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void X26_ManyHalftones_AreCountedPastTheNamedOnes()
    {
        string states = string.Concat(Enumerable.Range(0, 10).Select(i =>
            $"/G{i} << /HT << /HalftoneType 1 {Screen} /TransferFunction /Identity >> >> "));
        var x26 = Assert.Single(new Fixture { PageResources = "/ExtGState << " + states + ">>" }
            .Validate(PdfXConformanceLevel.X4).Informational, i => i.Code == "X26");
        Assert.EndsWith(" and 2 more.", x26.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void X27_AConformingConfiguration_IsNotNamed()
    {
        var f = new Fixture { OcProperties = "/OCGs [5 0 R 6 0 R] /D << /Name (D) /Order [5 0 R 6 0 R] >>" };
        Assert.DoesNotContain(f.Validate(PdfXConformanceLevel.X4).Informational, i => i.Code == "X27");
        Assert.DoesNotContain(f.Validate(PdfXConformanceLevel.X6).Informational, i => i.Code == "X27");
    }

    // ================================================================== X13 — ISO 32000's entries

    [Theory]
    [InlineData("/OCGs [5 0 R 6 0 R]", "the /D default configuration")]
    [InlineData("/D << /Name (D) >>", "the /OCGs array")]
    public void X13_OCPropertiesWithoutARequiredEntry_IsAViolation(string ocProperties, string expected)
    {
        foreach (var level in new[] { PdfXConformanceLevel.X4, PdfXConformanceLevel.X6 })
        {
            var x13 = Assert.Single(new Fixture { OcProperties = ocProperties }.Validate(level).Violations, v => v.Code == "X13");
            Assert.Contains(expected, x13.Message, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(new Fixture { OcProperties = "/OCGs [5 0 R 6 0 R] /D << /Name (D) >>" }
            .Validate(PdfXConformanceLevel.X4).Violations, v => v.Code == "X13");
    }

    // ================================================================== X-6

    [Fact]
    public void X6_EveryResult_SaysWhatWasNotRead_AndIsPartial()
    {
        var r = new Fixture().Validate(PdfXConformanceLevel.X6);
        Assert.True(r.IsConforming);
        Assert.True(r.IsPartial);
        Assert.Contains(r.Informational, i => i.Code == "X29" && i.Message == PdfXValidator.X6Coverage);
        Assert.Equal(PdfXConformanceLevel.X6, r.Level);
    }

    [Theory]
    [InlineData("2.0", false)]
    [InlineData("1.7", true)]
    [InlineData("1.6", true)]
    public void X6_X12_IsPdf20(string version, bool reported)
    {
        var r = new Fixture { Header = version }.Validate(PdfXConformanceLevel.X6);
        Assert.Equal(reported, r.Violations.Any(v => v.Code == "X12" && v.Message.Contains("PDF 2.0", StringComparison.Ordinal)));
    }

    [Fact]
    public void X6_X02_AnIntentForTheDocumentOrForEveryPage()
    {
        // The document's: X-4's rule too.
        Assert.DoesNotContain(new Fixture { Pages = 2 }.Validate(PdfXConformanceLevel.X6).Violations, v => v.Code == "X02");

        // Every page's own, none in the catalog: X-6 accepts it, X-4 refuses it twice over (X02, X25).
        var everyPage = new Fixture { Pages = 2, CatalogIntent = false, PageIntents = true };
        Assert.DoesNotContain(everyPage.Validate(PdfXConformanceLevel.X6).Violations, v => v.Code == "X02" || v.Code == "X25");
        var x4 = everyPage.Validate(PdfXConformanceLevel.X4).Violations.Select(v => v.Code).ToList();
        Assert.Contains("X02", x4);
        Assert.Contains("X25", x4);

        // One page without: that page is named.
        var onePage = new Fixture { Pages = 2, CatalogIntent = false, PageIntents = true, IntentlessPage = 2 };
        var x02 = Assert.Single(onePage.Validate(PdfXConformanceLevel.X6).Violations, v => v.Code == "X02");
        Assert.Contains("page(s) 2 have none of their own", x02.Message, StringComparison.Ordinal);

        // Neither: every page.
        var none = new Fixture { Pages = 2, CatalogIntent = false };
        Assert.Contains("page(s) 1, 2 have none", Assert.Single(none.Validate(PdfXConformanceLevel.X6).Violations, v => v.Code == "X02").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void X6_X28_NoXfa()
    {
        var xfa = new Fixture { CatalogExtra = "/AcroForm << /Fields [] /XFA (<xdp:xdp/>) >>" };
        Assert.Contains("XFA", Assert.Single(xfa.Validate(PdfXConformanceLevel.X6).Violations, v => v.Code == "X28").Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(new Fixture { CatalogExtra = "/AcroForm << /Fields [] >>" }
            .Validate(PdfXConformanceLevel.X6).Violations, v => v.Code == "X28");
    }

    /// <summary>A rule the Application Notes do not list as changed is run, and its finding is informational,
    /// under its own code, never a violation. X-4 refuses the same file.</summary>
    [Fact]
    public void X6_AnX4RuleTheNotesDoNotChange_IsCarriedAsInformational()
    {
        var f = new Fixture { PageResources = "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>", Content = "BT /F1 12 Tf (a) Tj ET" };
        Assert.Contains(f.Validate(PdfXConformanceLevel.X4).Violations, v => v.Code == "X04");

        var x6 = f.Validate(PdfXConformanceLevel.X6);
        Assert.DoesNotContain(x6.Violations, v => v.Code == "X04");
        var carried = Assert.Single(x6.Informational, i => i.Code == "X04");
        Assert.StartsWith(PdfXValidator.CarriedFromX4, carried.Message, StringComparison.Ordinal);
    }

    /// <summary>Each X-4 rule X-6 carries: refused at X-4, informational at X-6 under its own code.</summary>
    [Theory]
    [InlineData("X05", "boxes", "")]
    [InlineData("X06", "resources", "/XObject << /Im0 << /Type /XObject /Subtype /Form /BBox [0 0 1 1] /OPI << >> >> >>")]
    [InlineData("X14", "resources", "/ExtGState << /G0 << /TR /Identity >> >>")]
    [InlineData("X16", "catalog", "/Names << /EmbeddedFiles << /Names [] >> >>")]
    [InlineData("X17", "boxes", "/TrimBox [0 0 200 200] /BleedBox [10 10 100 100]")]
    [InlineData("X18", "resources", "/ColorSpace << /S0 [/Separation /Gold /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 1 0] /N 1 >>] " +
                                    "/S1 [/Separation /Gold /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>] >>")]
    [InlineData("X19", "annots", "[<< /Type /Annot /Subtype /Sound /Rect [300 300 310 310] /F 4 >>]")]
    public void X6_EachCarriedX4Rule_IsInformational(string code, string where, string value)
    {
        var f = new Fixture();
        switch (where)
        {
            case "boxes": f.PageBoxes = value; break;
            case "resources": f.PageResources = value; break;
            case "catalog": f.CatalogExtra = value; break;
            case "annots": f.Annots = value; break;
        }
        Assert.Contains(f.Validate(PdfXConformanceLevel.X4).Violations, v => v.Code == code);
        var x6 = f.Validate(PdfXConformanceLevel.X6);
        Assert.DoesNotContain(x6.Violations, v => v.Code == code);
        Assert.Contains(x6.Informational, i => i.Code == code && i.Message.StartsWith(PdfXValidator.CarriedFromX4, StringComparison.Ordinal));
    }

    /// <summary>The rules the Notes say X-6 changed are not run at all: actions (X07), annotations in the
    /// print area (X24), page-level intents (X25); nor those whose X-4 reading cannot be X-6's (X03, X10).</summary>
    [Fact]
    public void X6_TheRulesTheNotesSayChanged_AreNotRun()
    {
        var f = new Fixture
        {
            CatalogExtra = "/AA << /WC << /S /JavaScript /JS (1) >> >>",
            Annots = "[<< /Type /Annot /Subtype /Square /Rect [10 10 50 50] /F 4 >>]",
            PageIntents = true,
        };
        var x4 = f.Validate(PdfXConformanceLevel.X4).Violations.Select(v => v.Code).ToList();
        foreach (string code in new[] { "X03", "X07", "X10", "X24", "X25" })
            Assert.Contains(code, x4);

        var x6 = f.Validate(PdfXConformanceLevel.X6);
        foreach (string code in new[] { "X03", "X07", "X10", "X24", "X25" })
        {
            Assert.DoesNotContain(x6.Violations, v => v.Code == code);
            Assert.DoesNotContain(x6.Informational, i => i.Code == code);
        }
    }

    [Fact]
    public void X6_X29_ADeprecatedFeature_IsARecommendationPerConstruct()
    {
        var f = new Fixture { PageResources = "/ProcSet [/PDF /Text]" };
        var x29 = Assert.Single(f.Validate(PdfXConformanceLevel.X6).Informational,
            i => i.Code == "X29" && i.Message.Contains("ProcSet", StringComparison.Ordinal));
        Assert.Contains("should not appear in a PDF/X-6 file", x29.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new Fixture().Validate(PdfXConformanceLevel.X6).Informational,
            i => i.Code == "X29" && i.Message.Contains("ProcSet", StringComparison.Ordinal));
    }

    [Fact]
    public void X6_AnEncryptedDocument_IsNotExamined_AndSaysSo()
    {
        var r = new Fixture { TrailerExtra = " /Encrypt 9 0 R", Objects = { (9, "<< /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >>") } }
            .Validate(PdfXConformanceLevel.X6);
        Assert.Empty(r.Violations);
        Assert.Contains(r.Informational, i => i.Code == "X29" && i.Message.StartsWith("The document is encrypted", StringComparison.Ordinal));
        Assert.StartsWith(PdfXValidator.CarriedFromX4, Assert.Single(r.Informational, i => i.Code == "X01").Message, StringComparison.Ordinal);

        // X-4 refuses it outright.
        Assert.Contains(new Fixture { TrailerExtra = " /Encrypt 9 0 R", Objects = { (9, "<< /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >>") } }
            .Validate(PdfXConformanceLevel.X4).Violations, v => v.Code == "X01");
    }

    [Fact]
    public void X6_TheConverter_RefusesIt_AndWritesNothing()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(new Fixture().Build()), output,
            new PdfXConversionOptions { Level = PdfXConformanceLevel.X6 });
        Assert.Contains(result.Blockers, b => b.Contains("PDF/X-6 is not offered", StringComparison.Ordinal));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void X6_ThePlugin_NamesTheLevel()
        => Assert.Equal("PDF/X-6", new PdfXValidationPlugin(PdfXConformanceLevel.X6).ConformanceLevel);

    // ================================================================== Fixture

    /// <summary>
    /// Objects 1–4 and 7: catalog, page tree, the first page, its content, the output intent's profile (CMYK);
    /// 5 and 6 are optional content groups; further pages from 20. Extra objects numbered by the test.
    /// </summary>
    private sealed class Fixture
    {
        public string Header = "2.0";
        public string PageBoxes = "/TrimBox [0 0 200 200]";
        public string Content = "q Q";
        public string PageResources = "";
        public string CatalogExtra = "";
        public string? OcProperties;
        public string? Annots;
        public string TrailerExtra = "";
        public bool CatalogIntent = true;
        public bool PageIntents;
        public int IntentlessPage;
        public int Pages = 1;
        public List<(int Num, string Body)> Objects { get; } = new();

        public PdfXValidationResult Validate(PdfXConformanceLevel level)
            => PdfXValidator.Validate(new MemoryStream(Build()), level);

        public byte[] Build()
        {
            const string intent = "<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (Custom) /DestOutputProfile 7 0 R >>";
            var objects = new SortedDictionary<int, string>();
            var pageNumbers = Enumerable.Range(0, Pages).Select(i => i == 0 ? 3 : 20 + i).ToList();
            string oc = OcProperties == null ? "" : "/OCProperties << " + OcProperties + " >>";
            objects[1] = $"<< /Type /Catalog /Pages 2 0 R {(CatalogIntent ? "/OutputIntents [" + intent + "]" : "")} {oc} {CatalogExtra} >>";
            objects[2] = $"<< /Type /Pages /Kids [{string.Join(" ", pageNumbers.Select(n => n + " 0 R"))}] /Count {Pages} >>";
            for (int i = 0; i < Pages; i++)
            {
                bool intentHere = PageIntents && IntentlessPage != i + 1;
                objects[pageNumbers[i]] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " + PageBoxes + " " +
                    (intentHere ? "/OutputIntents [" + intent + "] " : "") +
                    (Annots != null && i == 0 ? "/Annots " + Annots + " " : "") +
                    $"/Contents 4 0 R /Resources << {PageResources} >> >>";
            }
            objects[4] = $"<< /Length {Content.Length} >>\nstream\n{Content}\nendstream";
            objects[5] = "<< /Type /OCG /Name (One) >>";
            objects[6] = "<< /Type /OCG /Name (Two) >>";
            objects[7] = "<< /N 4 /Length 1 >>\nstream\n \nendstream";
            foreach (var (num, body) in Objects) objects[num] = body;

            var sb = new StringBuilder($"%PDF-{Header}\n");
            var offsets = new Dictionary<int, int>();
            foreach (var kvp in objects)
            {
                offsets[kvp.Key] = sb.Length;
                sb.Append(kvp.Key.ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(kvp.Value).Append("\nendobj\n");
            }
            int highest = objects.Keys.Max();
            int xref = sb.Length;
            sb.Append($"xref\n0 {highest + 1}\n0000000000 65535 f \n");
            for (int i = 1; i <= highest; i++)
                sb.Append(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
            sb.Append($"trailer\n<< /Size {highest + 1} /Root 1 0 R{TrailerExtra} /ID [<01> <01>] >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.Latin1.GetBytes(sb.ToString());
        }
    }
}
