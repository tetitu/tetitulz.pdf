// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Import;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="XfaPrototypes"/>: <c>use</c>/<c>usehref</c> prototypes resolved before the template is
/// read, by XFA 3.3 chapter 6's rules — each case one of the specification's examples (6.4 to 6.12),
/// asserted on the expanded template or on where the laid-out form draws.
/// </summary>
public class XfaPrototypeTests
{
    private const string Ns = "http://www.xfa.org/schema/xfa-template/3.3/";

    private const string ProtoDraw =
        "<proto><draw id=\"P1\" name=\"proto\" x=\"1in\" y=\"2in\" w=\"2in\" h=\"0.25in\"><ui><textEdit/></ui><value><text>PROTO</text></value><font typeface=\"Myriad Pro\" size=\"12pt\"/></draw></proto>";

    /// <summary>A draw naming a prototype takes its place and text (Example 6.4) and nothing is reported.</summary>
    [Fact]
    public void ADrawTakesItsPrototypesPositionAndText()
    {
        var result = XfaLayout.Layout(Parse(Form(ProtoDraw, "<draw use=\"#P1\" name=\"d\"/>")), null);
        byte[] pdf = result.Document.ToPdf();

        var drawn = Single(pdf, "PROTO");
        Assert.Equal(90.0, drawn.X, 1);                     // 0.25in content area + 1in
        Assert.Empty(result.Approximations);
    }

    /// <summary>An attribute the referencing element sets overrides the prototype's (Example 6.9).</summary>
    [Fact]
    public void AnAttributeOnTheReferenceOverridesThePrototypes()
    {
        var result = XfaLayout.Layout(Parse(Form(ProtoDraw, "<draw use=\"#P1\" name=\"d\" x=\"2in\"/>")), null);
        Assert.Equal(162.0, Single(result.Document.ToPdf(), "PROTO").X, 1);
    }

    /// <summary>The reference's own data content overrides the prototype's, through matched children
    /// (Examples 6.10, 6.11): the value's text is the reference's, the font still the prototype's.</summary>
    [Fact]
    public void ChildrenMatchByTypeAndTheReferencesContentWins()
    {
        var result = XfaLayout.Layout(Parse(Form(ProtoDraw, "<draw use=\"#P1\" name=\"d\"><value><text>OWN</text></value></draw>")), null);
        byte[] pdf = result.Document.ToPdf();

        Assert.Equal(90.0, Single(pdf, "OWN").X, 1);
        Assert.DoesNotContain(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)), t => t.Text.Contains("PROTO", StringComparison.Ordinal));
    }

    /// <summary>
    /// A prototype may name a prototype (Example 6.5), and a SOM expression finds one (chapter 3): by its
    /// path, an index, a class among the true children, a descendant search, <c>xfa.template</c>, and
    /// unqualified from the referencing element outward — through a transparent unnamed subform.
    /// </summary>
    [Theory]
    [InlineData("<draw use=\"#P2\" name=\"d\"/>")]
    [InlineData("<draw usehref=\"#som($template.form1.second)\" name=\"d\"/>")]
    [InlineData("<draw use=\"#NOPE\" usehref=\"#P2\" name=\"d\"/>")]       // usehref wins when both are non-empty
    [InlineData("<draw use=\"$template.form1.second[0]\" name=\"d\"/>")]
    [InlineData("<draw use=\"$template.form1.#draw[1]\" name=\"d\"/>")]   // the second draw of form1's true children
    [InlineData("<draw use=\"$template..second\" name=\"d\"/>")]
    [InlineData("<draw use=\"xfa.template.form1.second\" name=\"d\"/>")]
    [InlineData("<draw use=\"second\" name=\"d\"/>")]                     // unqualified: found at form1's level
    [InlineData("<draw use=\"form1.second\" name=\"d\"/>")]               // unqualified, then qualified below its match
    [InlineData("<draw use=\"hidden\" name=\"d\"/>")]                     // inside an unnamed subform: transparent
    public void APrototypeMayReferenceAnother_AndSomFindsOneByItsPath(string reference)
    {
        string protos = ProtoDraw.Replace("</proto>", "<draw id=\"P2\" name=\"second\" use=\"#P1\"/><subform><draw name=\"hidden\" use=\"#P1\"/></subform></proto>");
        var result = XfaLayout.Layout(Parse(Form(protos, reference)), null);

        Assert.Equal(90.0, Single(result.Document.ToPdf(), "PROTO").X, 1);
        Assert.Empty(result.Approximations);
    }

    /// <summary>Repeated children match by ordinal (Example 6.12); id and name are not inherited.</summary>
    [Fact]
    public void RepeatedChildrenMatchByOrdinal_AndIdAndNameStayTheReferences()
    {
        var template = XElement.Parse(
            "<template><subform name=\"form1\"><proto><border id=\"B\" name=\"pb\" hand=\"right\"><edge thickness=\"2pt\"/><edge thickness=\"1pt\"/></border></proto>" +
            "<field name=\"f\"><border use=\"#B\"><edge thickness=\"3pt\"/></border></field></subform></template>");

        var outcome = XfaPrototypes.Expand(template);
        var border = template.Descendants("field").Single().Element("border")!;

        Assert.Equal(1, outcome.Resolved);
        Assert.Equal(new[] { "3pt", "1pt" }, border.Elements("edge").Select(e => (string)e.Attribute("thickness")!));
        Assert.Equal("right", (string?)border.Attribute("hand"));
        Assert.Null(border.Attribute("id"));
        Assert.Null(border.Attribute("name"));
        Assert.Null(border.Attribute("use"));
    }

    /// <summary>
    /// A child present in both takes the prototype's child as its own prototype (Example 6.11): the
    /// referencing field's font keeps its size and inherits the typeface and weight.
    /// </summary>
    [Fact]
    public void AMatchedChildInheritsFromThePrototypesChild()
    {
        var template = XElement.Parse(
            "<template><subform name=\"form1\"><proto><field id=\"F\"><font typeface=\"Helvetica\" size=\"10pt\" weight=\"bold\"/></field></proto>" +
            "<field use=\"#F\" name=\"num\"><font typeface=\"Times\" size=\"12pt\"/></field></subform></template>");

        XfaPrototypes.Expand(template);
        var font = template.Descendants("field").Single(f => (string?)f.Attribute("name") == "num").Element("font")!;

        Assert.Equal("Times", (string?)font.Attribute("typeface"));
        Assert.Equal("12pt", (string?)font.Attribute("size"));
        Assert.Equal("bold", (string?)font.Attribute("weight"));
    }

    /// <summary>A reference nested deeper than the template reader follows is counted, never skipped silently.</summary>
    [Fact]
    public void AReferenceBeyondTheDepthBoundIsCounted()
    {
        var root = new XElement("subform", new XAttribute("name", "form1"),
            new XElement("proto", new XElement("draw", new XAttribute("id", "P"), new XAttribute("x", "1in"))));
        XElement inner = root;
        for (int i = 0; i < XfaTemplate.MaxDepth + 5; i++)
        {
            var next = new XElement("subform");
            inner.Add(next);
            inner = next;
        }
        inner.Add(new XElement("draw", new XAttribute("use", "#P")));
        var template = new XElement("template", root);

        var outcome = XfaPrototypes.Expand(template);

        Assert.Equal(1, outcome.Unresolved);
        Assert.Equal(0, outcome.Resolved);
    }

    /// <summary>What cannot be resolved is counted and reported, and the element is read as it declares itself.</summary>
    [Theory]
    [InlineData("<draw usehref=\"other.xdp#P1\" name=\"d\"/>")]           // another document
    [InlineData("<draw use=\"#NOPE\" name=\"d\"/>")]                       // no such id
    [InlineData("<field use=\"#P1\" name=\"d\"/>")]                        // another type
    [InlineData("<draw use=\"$template.form1.*\" name=\"d\"/>")]           // an unsupported SOM step
    [InlineData("<draw use=\"$template.form1.proto[*]\" name=\"d\"/>")]    // several nodes, where a prototype is one
    [InlineData("<draw use=\"$template.form1.proto[1]\" name=\"d\"/>")]    // an index past the matches
    [InlineData("<draw use=\"$template.form1.#draw[9]\" name=\"d\"/>")]    // a class index past the matches
    [InlineData("<draw use=\"$data.form1.proto\" name=\"d\"/>")]           // another model
    [InlineData("<draw use=\"$template...proto\" name=\"d\"/>")]           // three dots
    [InlineData("<draw use=\"nowhere.proto\" name=\"d\"/>")]               // an unqualified first step found nowhere
    [InlineData("<draw use=\"$template.form1.proto[" + Zeros + "0]\" name=\"d\"/>")]   // past the length cap, though it names proto
    [InlineData("<draw use=\"#LOOP\" name=\"d\"/>")]                       // a loop
    public void AnUnresolvableReferenceIsReported(string reference)
    {
        string protos = ProtoDraw.Replace("</proto>", "<subform id=\"LOOP0\"><subform use=\"#LOOP0\"/></subform><draw id=\"LOOP\" use=\"#LOOP\"/></proto>");
        var template = Parse(Form(protos, reference));
        var result = XfaLayout.Layout(template, null);

        Assert.Equal(1, template.UnresolvedPrototypes);
        Assert.Contains(result.Approximations, a => a.StartsWith("a use/usehref prototype", StringComparison.Ordinal));
        Assert.DoesNotContain(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(result.Document.ToPdf())), t => t.Text.Contains("PROTO", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each prototype layer doubling the next — thirty levels is 2³⁰ elements — stops at
    /// <see cref="XfaPrototypes.MaxAddedElements"/> and says so; the control, five levels, resolves whole.
    /// </summary>
    [Theory]
    [InlineData(30, true)]
    [InlineData(5, false)]
    public void HostileTemplate_DoublingPrototypes_IsBoundedByTheExpansionBudget(int levels, bool exhausted)
    {
        var protos = new StringBuilder("<proto><subform id=\"L0\"><draw name=\"leaf\"/></subform>");
        for (int i = 1; i <= levels; i++)
            protos.Append($"<subform id=\"L{i}\"><subform use=\"#L{i - 1}\"/><subform use=\"#L{i - 1}\"/></subform>");
        protos.Append("</proto>");
        var template = XElement.Parse($"<template><subform name=\"form1\">{protos}<subform use=\"#L{levels}\"/></subform></template>");

        var outcome = XfaPrototypes.Expand(template);

        Assert.Equal(exhausted, outcome.BudgetExhausted);
        Assert.InRange(outcome.Added, 1, XfaPrototypes.MaxAddedElements);
        if (exhausted) Assert.True(outcome.Unresolved > 0);
        else
        {
            Assert.Equal(0, outcome.Unresolved);
            Assert.Equal(1 << levels, template.Elements("subform").Single().Elements("subform").Last().Descendants("draw").Count());
        }
    }

    /// <summary>
    /// <b>Adversarial: twenty thousand references to a sixty-thousand-element prototype.</b> The first
    /// resolves, the second spends the budget — and every reference after it measured the prototype again,
    /// 1.2 × 10⁹ elements counted for none added. A spent budget measures nothing more.
    /// </summary>
    [Fact]
    public void AdversarialReferencesPastTheBudget_AreNotMeasuredAgain()
    {
        var template = XElement.Parse("<template><subform name=\"form1\"><proto><subform id=\"P\">" +
            string.Concat(Enumerable.Repeat("<draw/>", 60_000)) + "</subform></proto>" +
            string.Concat(Enumerable.Repeat("<subform use=\"#P\"/>", 20_000)) + "</subform></template>");

        var outcome = XfaPrototypes.Expand(template);

        Assert.True(outcome.BudgetExhausted);
        Assert.Equal(1, outcome.Resolved);
        Assert.Equal(19_999, outcome.Unresolved);
        Assert.InRange(outcome.Examined, 60_001, 2L * XfaPrototypes.MaxAddedElements);
    }

    /// <summary>One prototype past the budget is measured only as far as the budget decides: three hundred thousand elements, a hundred thousand and one counted.</summary>
    [Fact]
    public void APrototypePastTheBudget_IsMeasuredOnlyToTheBudget()
    {
        var template = XElement.Parse("<template><subform name=\"form1\"><proto><subform id=\"P\">" +
            string.Concat(Enumerable.Repeat("<draw/>", 300_000)) + "</subform></proto><subform use=\"#P\"/></subform></template>");

        var outcome = XfaPrototypes.Expand(template);

        Assert.True(outcome.BudgetExhausted);
        Assert.Equal(XfaPrototypes.MaxAddedElements + 1, outcome.Examined);
    }

    /// <summary>
    /// <b>Adversarial: unqualified references that each search the form.</b> Every one of five thousand
    /// reads its form's five thousand children for a name none carries — twenty-five million candidates;
    /// the searches stop at <see cref="XfaPrototypes.MaxSomExamined"/> and say so. Five hundred stay under it.
    /// </summary>
    [Theory]
    [InlineData(5_000, true)]
    [InlineData(500, false)]
    public void AdversarialSomSearches_StopAtTheirBudget(int count, bool exhausted)
    {
        var template = XElement.Parse("<template><subform name=\"form1\">" +
            string.Concat(Enumerable.Repeat("<draw name=\"x\" use=\"nowhere\"/>", count)) + "</subform></template>");

        var outcome = XfaPrototypes.Expand(template);

        Assert.Equal(exhausted, outcome.BudgetExhausted);
        Assert.Equal(count, outcome.Unresolved);
        Assert.InRange(outcome.Examined, 1, XfaPrototypes.MaxSomExamined + 1);
    }

    /// <summary>The control for the length cap: a short index of leading zeros is index 0, and resolves.</summary>
    [Fact]
    public void AnIndexOfLeadingZeros_UnderTheCap_Resolves()
    {
        var result = XfaLayout.Layout(Parse(Form(ProtoDraw, "<draw use=\"$template.form1.proto[000]\" name=\"d\"/>")), null);
        Assert.Equal(90.0, Single(result.Document.ToPdf(), "PROTO").X, 1);
        Assert.Empty(result.Approximations);
    }

    /// <summary>Five hundred and twenty zeros: with the rest of its path, past <see cref="XfaPrototypes.MaxSomLength"/>.</summary>
    private const string Zeros =
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000";

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// <b>Adversarial: two empty unframed subforms nested, each occurring ten thousand times.</b> An
    /// unframed top-to-bottom subform emits no block, and with nothing below it to emit one the block
    /// ceiling never saw the 10⁸ instances; each instance is charged to it now, and the layout stops
    /// there and says so — at one level the hundred instances of the control are laid out and nothing is
    /// said. Nothing inside: a draw below would emit a block per instance, and its charge alone would stop
    /// the layout.
    /// </summary>
    [Theory]
    [InlineData(2, 10_000, true)]
    [InlineData(1, 100, false)]
    public void AdversarialNestedRepeatingSubforms_StopAtTheBlockCeiling(int levels, int occurs, bool stopped)
    {
        string nested = "";
        for (int k = 0; k < levels; k++)
            nested = $"<subform name=\"s{k}\" layout=\"tb\"><occur initial=\"{occurs}\" max=\"-1\"/>{nested}</subform>";
        string template = "<template xmlns=\"" + Ns + "\"><subform name=\"form1\" layout=\"tb\">" +
            "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"576pt\" h=\"756pt\"/><medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>" +
            nested + "</subform></template>";

        var result = XfaLayout.Layout(Parse(template), null);

        Assert.Equal(stopped, result.Approximations.Any(a => a.Contains("block ceiling", StringComparison.Ordinal)));
    }

    private static string Form(string protos, string body) =>
        "<template xmlns=\"" + Ns + "\"><subform name=\"form1\" layout=\"tb\">" + protos +
        "<pageSet><pageArea name=\"Page1\"><contentArea x=\"0.25in\" y=\"0.25in\" w=\"576pt\" h=\"756pt\"/><medium stock=\"letter\" short=\"612pt\" long=\"792pt\"/></pageArea></pageSet>" +
        "<subform w=\"576pt\" h=\"756pt\">" + body + "</subform></subform></template>";

    private static XfaTemplate Parse(string template)
    {
        XfaTemplate? t = XfaTemplate.Parse(Encoding.UTF8.GetBytes(template));
        Assert.NotNull(t);
        return t!;
    }

    private static PdfContentProbe.DrawnText Single(byte[] pdf, string text)
    {
        var hits = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Where(t => t.Text.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.True(hits.Count == 1, $"'{text}' drawn {hits.Count} times; drawn: " +
            string.Join(" | ", PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)).Select(t => t.Text)));
        return hits[0];
    }
}
