// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Import;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A template's cost is not its size in the file: a packet is decoded and built into a tree before
/// anyone reads it, and a prototype's weight multiplies by the references to it. The doors refuse
/// each on evidence, never on a clock.
/// </summary>
public sealed class XfaHostileSizeTests
{
    private const string TemplateNs = "http://www.xfa.org/schema/xfa-template/3.3/";

    // Core's ceilings, internal to it and pinned by its own tests.
    private const long PacketCeiling = 64L * 1024 * 1024;
    private const int NodeCeiling = 2_000_000;

    private static string Stream(byte[] data, string dict = "")
    {
        string hex = Convert.ToHexString(data) + ">";
        return $"<< {dict} /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream";
    }

    private static string FlateOfSpaces(long bytes)
    {
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
        {
            var buffer = new byte[65536];
            Array.Fill(buffer, (byte)' ');
            for (long left = bytes; left > 0; left -= buffer.Length) z.Write(buffer, 0, (int)Math.Min(left, buffer.Length));
        }

        string hex = Convert.ToHexString(deflated.ToArray()) + ">";
        return $"<< /Filter [/ASCIIHexDecode /FlateDecode] /Length {hex.Length} >>\nstream\n{hex}\nendstream";
    }

    private static byte[] Pdf(string template, string datasets)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA [(template) 4 0 R (datasets) 5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            template,
            datasets,
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string GoodTemplate() => Stream(Encoding.UTF8.GetBytes(
        $"<template xmlns=\"{TemplateNs}\"><subform name=\"f\" layout=\"tb\"><draw name=\"d\" w=\"1in\" h=\"1in\"><value><text>T</text></value></draw></subform></template>"));

    private static string GoodDatasets() => Stream(Encoding.UTF8.GetBytes(
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><f><v>1</v></f></xfa:data></xfa:datasets>"));

    /// <summary>The layout door names the packet it would not read; the control lays the same form out.</summary>
    [Fact]
    public void AdversarialTemplatePastTheCeiling_LayoutRefusesAndSaysWhich()
    {
        using var hostile = new MemoryStream(Pdf(FlateOfSpaces(PacketCeiling + 1), GoodDatasets()));
        Assert.Null(XfaLayout.Read(hostile));
        Assert.Contains("template packet could not be read", XfaLayout.LastError, StringComparison.Ordinal);

        using var control = new MemoryStream(Pdf(GoodTemplate(), GoodDatasets()));
        Assert.NotNull(XfaLayout.Read(control));
    }

    [Fact]
    public void AdversarialDatasetsPastTheCeiling_LayoutRefusesAndSaysWhich()
    {
        using var hostile = new MemoryStream(Pdf(GoodTemplate(), FlateOfSpaces(PacketCeiling + 1)));

        Assert.Null(XfaLayout.Read(hostile));
        Assert.Contains("datasets packet could not be read", XfaLayout.LastError, StringComparison.Ordinal);
    }

    /// <summary>The content door, which reads the datasets alone.</summary>
    [Fact]
    public void AdversarialDatasetsPastTheCeiling_ContentRefusesAndSaysWhich()
    {
        using var hostile = new MemoryStream(Pdf(GoodTemplate(), FlateOfSpaces(PacketCeiling + 1)));
        Assert.Null(XfaContent.Read(hostile));
        Assert.Contains("datasets packet could not be read", XfaContent.LastError, StringComparison.Ordinal);

        using var control = new MemoryStream(Pdf(GoodTemplate(), GoodDatasets()));
        Assert.NotNull(XfaContent.Read(control));
    }

    /// <summary>
    /// <b>The template reader asks the same bounds as the datasets reader</b>: elements enough that the
    /// count fits bare and does not with two attributes each.
    /// </summary>
    [Fact]
    public void AdversarialNodeFlood_TemplateIsNotBuilt()
    {
        int elements = NodeCeiling / 3 + 1;
        byte[] Template(string element)
        {
            var text = new StringBuilder($"<template xmlns=\"{TemplateNs}\"><subform name=\"f\">", elements * element.Length + 128);
            for (int i = 0; i < elements; i++) text.Append(element);
            return Encoding.ASCII.GetBytes(text.Append("</subform></template>").ToString());
        }

        Assert.NotNull(XfaTemplate.Parse(Template("<a/>")));
        Assert.Null(XfaTemplate.Parse(Template("<a b=\"\" c=\"\"/>")));
    }

    /// <summary>
    /// <b>One prototype of a megabyte, referenced many times.</b> Under the element ceiling by far, and
    /// each reference hands its reader the whole weight again: the characters added are what stop it.
    /// Text and attributes alike carry weight.
    /// </summary>
    [Theory]
    [InlineData("<draw id=\"P\" x=\"{0}\"/>", 10, false)]
    [InlineData("<draw id=\"P\" x=\"{0}\"/>", 70, true)]
    [InlineData("<draw id=\"P\"><value><image>{0}</image></value></draw>", 10, false)]
    [InlineData("<draw id=\"P\"><value><image>{0}</image></value></draw>", 70, true)]
    public void AdversarialHeavyPrototype_IsBoundedByTheCharactersItAdds(string prototype, int references, bool exhausted)
    {
        string weight = new string('A', 1024 * 1024);
        var xml = new StringBuilder("<template><subform name=\"form1\"><proto>")
            .Append(string.Format(CultureInfo.InvariantCulture, prototype, weight))
            .Append("</proto>");
        for (int i = 0; i < references; i++) xml.Append("<draw use=\"#P\"/>");
        var template = XElement.Parse(xml.Append("</subform></template>").ToString());

        var outcome = XfaPrototypes.Expand(template);

        Assert.Equal(exhausted, outcome.BudgetExhausted);
        Assert.Equal(exhausted, outcome.Unresolved > 0);
        Assert.InRange(outcome.Resolved, 1, references);
        if (!exhausted) Assert.Equal(references, outcome.Resolved);
        else Assert.InRange(outcome.Resolved, 50, 64);
    }

    /// <summary>The table's column list stops at its ceiling, and the form says so.</summary>
    [Fact]
    public void AdversarialColumnWidths_StopAtTheCeilingAndAreReported()
    {
        XfaTemplate? Parsed(int columns) => XfaTemplate.Parse(Encoding.ASCII.GetBytes(
            $"<template xmlns=\"{TemplateNs}\"><subform name=\"f\" layout=\"table\" columnWidths=\"{string.Join(' ', Enumerable.Repeat("1in", columns))}\"/></template>"));

        XfaTemplate hostile = Parsed(XfaTemplate.MaxColumnWidths + 1)!;
        Assert.Equal(XfaTemplate.MaxColumnWidths, Subform(hostile).ColumnWidths.Count);
        Assert.Contains(hostile.FormattingUnapplied, s => s.StartsWith("columnWidths past", StringComparison.Ordinal));

        XfaTemplate control = Parsed(XfaTemplate.MaxColumnWidths)!;
        Assert.Equal(XfaTemplate.MaxColumnWidths, Subform(control).ColumnWidths.Count);
        Assert.DoesNotContain(control.FormattingUnapplied, s => s.StartsWith("columnWidths past", StringComparison.Ordinal));
    }

    private static XfaSubform Subform(XfaTemplate template) =>
        template.Root.ColumnWidths.Count > 0 ? template.Root : template.Root.Children.OfType<XfaSubform>().Single();
}
