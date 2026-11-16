// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Forms;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// The XML half of static XFA — reading leaf values out of a <c>datasets</c> packet and writing new
/// ones back into it.
/// </summary>
/// <remarks>
/// <para><b>Tested here rather than through a filler.</b> The reader moved to Core when Builder's
/// dynamic-XFA content importer needed it, so it now has two consumers — <c>PdfXfaFormFiller</c> and
/// <c>Builder.Import.XfaContent</c> — and every decision below is one both of them inherit. A
/// mutation sweep found nothing reached most of them: the suites on either side assert the values
/// that came out, and a value survives a round trip whether or not the packet was re-indented, its
/// declaration invented, or its DTD refused.</para>
/// <para><b>This is attacker-controlled XML</b>: it arrives inside a PDF, from whoever produced the
/// PDF.</para>
/// </remarks>
public class XfaDatasetsTests
{
    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    private const string Simple =
        "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">"
        + "<xfa:data><form1><total>7</total></form1></xfa:data></xfa:datasets>";

    private static IReadOnlyDictionary<string, string>? Read(string xml) =>
        XfaDatasets.ReadValues(Bytes(xml));

    // ================================================================
    //  Hardening
    // ================================================================

    /// <summary>
    /// A packet carrying a DTD is refused even when the DTD declares nothing hostile.
    /// </summary>
    /// <remarks>
    /// <b>This is the input that tells <c>Prohibit</c> from <c>Ignore</c>, and the existing XXE
    /// fixture cannot.</b> That one declares an entity and references it, so under <c>Ignore</c> the
    /// reference is simply undeclared and the parse fails anyway — the same refusal for a different
    /// reason. A DTD with no reference to it parses cleanly under <c>Ignore</c>, so only this
    /// separates the two readings. The choice is deliberate and is about whether the caller
    /// <i>learns</i>: silently dropping part of a form's data produces a fill that looks like it
    /// worked.
    /// </remarks>
    [Fact]
    public void AdversarialDoctypeIsRefusedEvenWhenItDeclaresNothingHostile()
    {
        Assert.Null(Read("<!DOCTYPE xfa:datasets><xfa:datasets xmlns:xfa=\"x\"><xfa:data>"
                         + "<form1><total>7</total></form1></xfa:data></xfa:datasets>"));
    }

    // A packet whose innermost value sits `levels` elements below <xfa:data>: the text node is
    // levels + 2 deep, counting the datasets root as 0.
    private static string Nested(int levels)
    {
        var xml = new StringBuilder("<xfa:datasets xmlns:xfa=\"x\"><xfa:data>");
        for (int i = 0; i < levels; i++) xml.Append("<e").Append(i).Append('>');
        xml.Append('v');
        for (int i = levels - 1; i >= 0; i--) xml.Append("</e").Append(i).Append('>');
        xml.Append("</xfa:data></xfa:datasets>");
        return xml.ToString();
    }

    /// <summary>
    /// A packet nested 200 000 elements deep is refused by every door instead of overflowing the
    /// stack in the walk after the load — which nothing catches.
    /// </summary>
    [Fact]
    public void AdversarialDeepPacket_IsRefusedByEveryDoor()
    {
        byte[] deep = Bytes(Nested(200_000));

        Assert.Null(XfaDatasets.ReadValues(deep));
        Assert.Null(XfaDatasets.ReadDataRoot(deep));
        Assert.Null(XfaDatasets.WriteValues(deep, new Dictionary<string, string> { ["e0"] = "x" }, out _));
    }

    [Theory]
    [InlineData(XfaDatasets.MaxXmlDepth - 2, true)]
    [InlineData(XfaDatasets.MaxXmlDepth - 1, false)]
    public void APacketIsReadDownToTheDepthBound(int levels, bool read)
    {
        var values = Read(Nested(levels));

        Assert.Equal(read, values != null);
        if (read) Assert.Equal("v", Assert.Single(values!).Value);
    }

    /// <summary>The acceptance control: the same packet without the DTD reads.</summary>
    [Fact]
    public void ThePacketWithoutADoctypeReads()
    {
        Assert.Equal("7", Read(Simple)!["form1.total"]);
    }

    // ================================================================
    //  Which element is the data root
    // ================================================================

    /// <summary>
    /// A <c>datasets</c> root with no <c>data</c> child yields nothing. Guessing past it would file
    /// values into a <c>Data description</c> or a <c>connectionData</c> sibling — a form that says
    /// something the caller never asked it to say.
    /// </summary>
    [Fact]
    public void ADatasetsRootWithNoDataChildYieldsNothing()
    {
        Assert.Null(Read("<xfa:datasets xmlns:xfa=\"x\"><xfa:Data description=\"1\">"
                         + "<form1><total>7</total></form1></xfa:Data></xfa:datasets>"));
    }

    /// <summary>
    /// Anything that is not a <c>datasets</c> root <i>is</i> the data root — a bare
    /// <c>&lt;data&gt;</c>, or a producer that wrapped the values in nothing at all.
    /// </summary>
    [Fact]
    public void ARootThatIsNotDatasetsIsItselfTheDataRoot()
    {
        Assert.Equal("7", Read("<data><form1><total>7</total></form1></data>")!["form1.total"]);
    }

    // ================================================================
    //  Which leaves are collected
    // ================================================================

    /// <summary>
    /// An empty element is a leaf with the empty string, not a missing field: a cleared field and a
    /// field the form never had are different facts.
    /// </summary>
    [Fact]
    public void AnEmptyElementIsALeafWithAnEmptyValue()
    {
        var values = Read("<data><form1><total></total></form1></data>")!;

        Assert.True(values.ContainsKey("form1.total"));
        Assert.Equal(string.Empty, values["form1.total"]);
    }

    /// <summary>
    /// Repeated siblings collide and the <b>first</b> wins — XFA addresses them by occurrence index
    /// and this model has none.
    /// </summary>
    /// <remarks>
    /// What matters is not which one wins but that both sides agree: <c>WriteValues</c> must find
    /// the element <c>ReadValues</c> reported, and both take the first. The second half of this
    /// test is that agreement, which is the property a last-wins read would break.
    /// </remarks>
    [Fact]
    public void ARepeatedSiblingKeepsTheFirstOnBothSides()
    {
        const string packet = "<data><item><v>one</v></item><item><v>two</v></item></data>";

        Assert.Equal("one", Read(packet)!["item.v"]);

        byte[] written = XfaDatasets.WriteValues(
            Bytes(packet), new Dictionary<string, string> { ["item.v"] = "CHANGED" }, out var paths);

        Assert.Equal(new[] { "item.v" }, paths);
        string result = Encoding.UTF8.GetString(written);
        Assert.Contains("<v>CHANGED</v><", result, StringComparison.Ordinal);
        Assert.Contains("<v>two</v>", result, StringComparison.Ordinal);
    }

    // ================================================================
    //  Writing
    // ================================================================

    /// <summary>
    /// A path naming the data root itself addresses no leaf. Without that guard an empty or
    /// root-shaped path would rewrite the whole tree's text content, destroying every value in it.
    /// </summary>
    [Fact]
    public void APathNamingTheDataRootItselfWritesNothing()
    {
        byte[] written = XfaDatasets.WriteValues(
            Bytes("<data><form1><total>7</total></form1></data>"),
            new Dictionary<string, string> { ["data"] = "WIPED" }, out var paths);

        Assert.Empty(paths);
        Assert.Contains("<total>7</total>", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
    }

    /// <summary>A path with no element is reported by its absence and changes nothing.</summary>
    [Fact]
    public void APathWithNoElementIsNotCreated()
    {
        byte[] written = XfaDatasets.WriteValues(
            Bytes(Simple), new Dictionary<string, string> { ["form1.missing"] = "x" }, out var paths);

        Assert.Empty(paths);
        Assert.DoesNotContain("missing", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
    }

    /// <summary>A null value clears the element rather than throwing.</summary>
    [Fact]
    public void ANullValueClearsTheElement()
    {
        byte[] written = XfaDatasets.WriteValues(
            Bytes(Simple), new Dictionary<string, string> { ["form1.total"] = null! }, out var paths);

        // Read back rather than matched as a substring: whether the empty element comes out as
        // <total></total> or <total /> is the writer's business, and the claim is about the value.
        Assert.Equal(new[] { "form1.total" }, paths);
        Assert.Equal(string.Empty, XfaDatasets.ReadValues(written)!["form1.total"]);
    }

    /// <summary>
    /// The producer's own layout survives the round trip: a value reported unchanged comes back
    /// byte-identical, and nothing is re-indented.
    /// </summary>
    /// <remarks>
    /// Reformatting a packet the caller did not ask to reformat makes every diff of the output
    /// useless — and an XFA packet sits inside a PDF, where a diff is often all anyone has.
    /// </remarks>
    [Fact]
    public void ThePacketsOwnWhitespaceSurvivesAWriteBack()
    {
        const string spaced = "<data>\n  <form1>\n    <total>7</total>\n  </form1>\n</data>";

        byte[] written = XfaDatasets.WriteValues(
            Bytes(spaced), new Dictionary<string, string> { ["form1.total"] = "9" }, out _);

        Assert.Equal("<data>\n  <form1>\n    <total>9</total>\n  </form1>\n</data>",
                     Encoding.UTF8.GetString(written));
    }

    /// <summary>
    /// The XML declaration is written back only if the packet arrived with one. A packet inside a
    /// PDF is a fragment, and inventing a prologue changes the bytes an XFA processor concatenates.
    /// </summary>
    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>", true)]
    [InlineData("", false)]
    public void TheDeclarationIsWrittenBackOnlyWhenItWasThere(string prologue, bool expected)
    {
        byte[] written = XfaDatasets.WriteValues(
            Bytes(prologue + "<data><form1><total>7</total></form1></data>"),
            new Dictionary<string, string> { ["form1.total"] = "9" }, out _);

        string result = Encoding.UTF8.GetString(written);
        Assert.Equal(expected, result.StartsWith("<?xml", StringComparison.Ordinal));
    }

    /// <summary>Malformed XML is null on both halves, not an exception and not an empty form.</summary>
    [Fact]
    public void MalformedXmlIsNullRatherThanEmpty()
    {
        Assert.Null(Read("<data><unclosed>"));
        Assert.Null(XfaDatasets.WriteValues(Bytes("<data><unclosed>"),
                                            new Dictionary<string, string>(), out _));
    }
}
