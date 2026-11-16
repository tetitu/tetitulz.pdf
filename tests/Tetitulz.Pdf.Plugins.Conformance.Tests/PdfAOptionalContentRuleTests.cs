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
/// The optional content rules, PDF/A-2 onwards: A70 (a configuration's /Name), A71 (its /Order names
/// every group), A72 (no /AS before PDF/A-4) — ISO 19005-2/3 6.9, ISO 19005-4 6.10, the published profile's rules of
/// those numbers. Each refusal beside the configuration that passes.
/// </summary>
public class PdfAOptionalContentRuleTests
{
    private const string Groups = "/OCGs [5 0 R 6 0 R]";

    /// <summary>A catalog with <c>/OCProperties &lt;&lt; <paramref name="ocProperties"/> &gt;&gt;</c>; objects 5 and 6 are groups.</summary>
    internal static byte[] Document(string ocProperties, IEnumerable<string>? extraObjects = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << " + ocProperties + " >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            "<< /Type /OCG /Name (One) >>",
            "<< /Type /OCG /Name (Two) >>",
        };
        if (extraObjects != null) objects.AddRange(extraObjects);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static PdfAValidationResult Validate(string ocProperties, int part, IEnumerable<string>? extra = null)
    {
        using var ms = new MemoryStream(Document(ocProperties, extra));
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part);
    }

    private static List<PdfAViolation> Coded(string ocProperties, int part, string code)
        => Validate(ocProperties, part).Violations.Where(v => v.Code == code).ToList();

    private static string Config(string entries) => Groups + " /D << " + entries + " >>";

    // -------------------------------------------------------------- the control

    [Fact]
    public void AConformingConfiguration_PassesEveryRule()
    {
        string ok = Config("/Name (Default) /Order [5 0 R [6 0 R]]") + " /Configs [<< /Name (Other) >>]";
        foreach (int part in new[] { 2, 3, 4 })
            foreach (string code in new[] { "A70", "A71", "A72" })
                Assert.Empty(Coded(ok, part, code));
    }

    // -------------------------------------------------------------- A70

    [Theory]
    [InlineData("/Order [5 0 R 6 0 R]", 2, "/D has no /Name")]
    [InlineData("/Order [5 0 R 6 0 R]", 4, "/D has no /Name")]
    [InlineData("/Name ()", 2, "/D has an empty /Name")]
    [InlineData("/Name ()", 3, "/D has an empty /Name")]
    [InlineData("/Name ()", 4, null)]                               // PDF/A-4: present is enough (rule 6.10-1)
    [InlineData("/Name /NotAString", 2, "/D has no /Name")]
    public void A70_AConfigurationsName(string entries, int part, string? expected)
    {
        var a70 = Coded(Config(entries), part, "A70");
        if (expected == null)
            Assert.Empty(a70);
        else
            Assert.Contains(expected, Assert.Single(a70).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A70_TwoConfigurationsSharingAName_AreReported_AndOneListedTwiceIsNot()
    {
        string shared = Groups + " /D << /Name (Same) >> /Configs [<< /Name (Same) >>]";
        Assert.Contains("the /Name (Same) is not unique", Assert.Single(Coded(shared, 2, "A70")).Message, StringComparison.Ordinal);
        Assert.Single(Coded(shared, 4, "A70"));

        // The default configuration listed again in /Configs is one dictionary, not two sharing a name.
        string listedTwice = Groups + " /D 7 0 R /Configs [7 0 R]";
        using var ms = new MemoryStream(Document(listedTwice, new[] { "<< /Name (Same) >>" }));
        Assert.DoesNotContain(PdfAValidator.Validate(ms, PdfAConformancePart.Part2).Violations, v => v.Code == "A70");
    }

    [Fact]
    public void A70_AConfigurationInConfigs_IsReadToo()
    {
        string configs = Config("/Name (Default)") + " /Configs [<< /Order [] >>]";
        Assert.Contains("/Configs [0] has no /Name", Assert.Single(Coded(configs, 2, "A70")).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A71

    [Theory]
    [InlineData("/Name (D) /Order [5 0 R]", "leaves out 1 of 2 groups")]
    [InlineData("/Name (D) /Order []", "leaves out 2 of 2 groups")]
    [InlineData("/Name (D) /Order [(Label) [5 0 R [6 0 R]]]", null)]   // nested, with a label: every group named
    [InlineData("/Name (D)", null)]                                     // no /Order: nothing to judge
    public void A71_AnOrderNamesEveryGroup(string entries, string? expected)
    {
        foreach (int part in new[] { 2, 4 })
        {
            var a71 = Coded(Config(entries), part, "A71");
            if (expected == null)
                Assert.Empty(a71);
            else
                Assert.Contains(expected, Assert.Single(a71).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Hostile input: every configuration's /Order is one shared array of many entries, so the reads
    /// multiply. The walk shares one document budget and stops — reported as a partial A71 — instead of
    /// reading configurations × entries.
    /// </summary>
    [Fact]
    public void A71_HostileManyConfigurationsSharingAHugeOrder_StopsAtTheDocumentBudget()
    {
        int entries = 20_000;
        int configs = 200;   // 4 000 000 reads unbounded, against a budget of 1 000 000
        string order = "[" + string.Join(" ", Enumerable.Repeat("5 0 R", entries)) + "]";
        string configurations = string.Join(" ", Enumerable.Range(0, configs).Select(i => $"<< /Name (C{i}) /Order 7 0 R >>"));
        var result = Validate(Groups + " /D << /Name (D) /Order 7 0 R >> /Configs [" + configurations + "]", 2, new[] { order });

        var partial = Assert.Single(result.Informational, i => i.Message.Contains("A71", StringComparison.Ordinal));
        Assert.Contains("did not read every configuration's /Order in full", partial.Message, StringComparison.Ordinal);
        // The configurations read before the budget ran out were judged: each leaves out group 6.
        Assert.Contains("leaves out 1 of 2 groups", Assert.Single(result.Violations, v => v.Code == "A71").Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard's evidence, not its verdict: the verdict above is the same whether the walk stops at the
    /// budget or reads every configuration's /Order through to its end. The budget left says which — it
    /// runs out inside the fiftieth configuration's entries, at -1, and nothing after spends any more.
    /// </summary>
    [Fact]
    public void A71_HostileOrderWalk_SpendsNothingOnceTheBudgetIsGone()
    {
        string order = "[" + string.Join(" ", Enumerable.Repeat("5 0 R", 20_000)) + "]";
        string configurations = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"<< /Name (C{i}) /Order 7 0 R >>"));
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(
            new MemoryStream(Document(Groups + " /D << /Name (D) /Order 7 0 R >> /Configs [" + configurations + "]", new[] { order })));
        var ocProperties = OptionalContentConfigurations.Properties(doc);
        var groups = OptionalContentConfigurations.Groups(doc, ocProperties);
        var read = OptionalContentConfigurations.Read(doc, ocProperties);
        Assert.Equal(201, read.Count);

        int work = OptionalContentConfigurations.MaxOrderWork;
        int judged = 0;
        foreach (var c in read)
            if (OptionalContentConfigurations.GroupsMissingFromOrder(doc, c.Dictionary, groups, ref work, out bool complete) == 1 && complete)
                judged++;

        Assert.Equal(49, judged);   // 1 000 000 / (20 000 entries + 2 groups)
        Assert.Equal(-1, work);
    }

    /// <summary>A group /OCGs lists twice is one group.</summary>
    [Fact]
    public void A71_AGroupListedTwice_IsCountedOnce()
    {
        string twice = "/OCGs [5 0 R 5 0 R 6 0 R] /D << /Name (D) /Order [5 0 R] >>";
        Assert.Contains("leaves out 1 of 2 groups", Assert.Single(Coded(twice, 2, "A71")).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hostile input, the other factor: many groups, many configurations with an empty /Order. Comparing
    /// every configuration against every group is the product too, and spends the same budget.
    /// </summary>
    [Fact]
    public void A71_HostileManyGroupsTimesManyConfigurations_StopsAtTheDocumentBudget()
    {
        string groups = "/OCGs [" + string.Join(" ", Enumerable.Repeat("<< /Type /OCG /Name (g) >>", 10_000)) + "]";
        string configurations = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"<< /Name (C{i}) /Order [] >>"));
        var result = Validate(groups + " /D << /Name (D) /Order [] >> /Configs [" + configurations + "]", 2);

        Assert.Single(result.Informational, i => i.Message.Contains("did not read every configuration's /Order in full", StringComparison.Ordinal));
        Assert.Contains("leaves out 10000 of 10000 groups", Assert.Single(result.Violations, v => v.Code == "A71").Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A72

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]   // PDF/A-4 has no /AS rule
    public void A72_AnAsEntry_BeforePdfA4(int part, bool reported)
    {
        string withAs = Config("/Name (D) /AS [<< /Event /Print /OCGs [5 0 R] /Category [/Print] >>]");
        var a72 = Coded(withAs, part, "A72");
        if (reported)
            Assert.Contains("/D /AS", Assert.Single(a72).Message, StringComparison.Ordinal);
        else
            Assert.Empty(a72);
        Assert.Empty(Coded(Config("/Name (D)"), part, "A72"));
    }

    // -------------------------------------------------------------- PDF/A-1

    /// <summary>PDF/A-1 is PDF 1.4, which has no optional content: these rules are not its.</summary>
    [Fact]
    public void PdfA1_HasNoneOfTheseRules()
    {
        string broken = Groups + " /D << /Order [] /AS [] >> /Configs [<< >>]";
        var codes = Validate(broken, 1).Violations.Select(v => v.Code).ToList();
        Assert.DoesNotContain("A70", codes);
        Assert.DoesNotContain("A71", codes);
        Assert.DoesNotContain("A72", codes);
        // Potency: the same document at part 2 is refused by all three.
        var part2 = Validate(broken, 2).Violations.Select(v => v.Code).ToList();
        Assert.Contains("A70", part2);
        Assert.Contains("A71", part2);
        Assert.Contains("A72", part2);
    }
}
