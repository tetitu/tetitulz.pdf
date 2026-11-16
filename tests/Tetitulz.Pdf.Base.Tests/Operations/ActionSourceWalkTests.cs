// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfActionDispatcher.ExtractActions(PdfDocument)"/> lists every place a document keeps
/// an action, and says which place each came from.
/// </summary>
/// <remarks>
/// <para><b>It walked the catalog, the pages' <c>/AA</c> and the form fields, and nothing
/// else.</b> Its own remarks say a scanner acts on the absence of a <c>/Launch</c> or a script, and
/// the places it skipped are where those usually are: a link annotation's <c>/A</c>, an
/// annotation's <c>/AA</c> (<c>/PO</c> runs a script when the page opens), the document-level
/// scripts in <c>/Names /JavaScript</c> (§12.6.4.17: "When the document is opened, all of the
/// actions in this name tree shall be executed"), and an outline item's <c>/A</c>. Its
/// <c>/AA</c> trigger list also lacked the catalog's <c>/WC</c> and the annotation triggers
/// <c>/PO</c>, <c>/PC</c>, <c>/PV</c> and <c>/PI</c> (Tables 197 and 200).</para>
/// </remarks>
public class ActionSourceWalkTests
{
    [Fact]
    public void ALinkAnnotationsActionIsListedWithItsPage()
    {
        byte[] pdf = Doc(
            catalog: "",
            pageExtra: "/Annots [4 0 R]",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Launch /F (payload.exe) >> >>");

        PdfAction action = Assert.Single(Extract(pdf));
        Assert.Equal(PdfActionType.Launch, action.ActionType);
        Assert.Equal("payload.exe", action.Target);
        Assert.Equal("MouseUp", action.Trigger);
        Assert.Equal(PdfActionSource.Annotation, action.Source);
        Assert.Equal(0, action.PageIndex);
        Assert.Null(action.FieldName);
    }

    [Fact]
    public void AnAnnotationsPageTriggersAreListed()
    {
        byte[] pdf = Doc(
            catalog: "",
            pageExtra: "/Annots [4 0 R]",
            "<< /Type /Annot /Subtype /Screen /Rect [0 0 10 10] /AA << "
            + "/PO << /S /JavaScript /JS (onOpen) >> /PC << /S /URI /URI (pc) >> "
            + "/PV << /S /URI /URI (pv) >> /PI << /S /URI /URI (pi) >> >> >>");

        var actions = Extract(pdf);
        Assert.Equal(new[] { "PageOpen", "PageClose", "PageVisible", "PageInvisible" },
                     actions.Select(a => a.Trigger));
        Assert.Equal("onOpen", actions[0].Target);
        Assert.All(actions, a => Assert.Equal(PdfActionSource.Annotation, a.Source));
    }

    /// <summary>
    /// Document-level scripts are listed, before the <c>/OpenAction</c>, on the trigger that runs
    /// them.
    /// </summary>
    /// <remarks>
    /// Before, because §12.6.4.17 says they define "functions for use by other scripts in the
    /// document", so they have to exist before any other script runs.
    /// </remarks>
    [Fact]
    public void DocumentLevelScriptsAreListedFirstOnOpen()
    {
        byte[] pdf = Doc(
            catalog: "/OpenAction << /S /URI /URI (open) >> "
                     + "/Names << /JavaScript << /Names [(a) 4 0 R (b) << /S /JavaScript /JS (second) >>] >> >>",
            pageExtra: "",
            "<< /S /JavaScript /JS (first) >>");

        var actions = Extract(pdf);
        Assert.Equal(new[] { "first", "second", "open" }, actions.Select(a => a.Target));
        Assert.All(actions, a => Assert.Equal("OpenDocument", a.Trigger));
        Assert.All(actions, a => Assert.Equal(PdfActionSource.Document, a.Source));
    }

    [Fact]
    public void OutlineItemActionsAreListedAtEveryLevel()
    {
        byte[] pdf = Doc(
            catalog: "/Outlines 4 0 R",
            pageExtra: "",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (top) /Parent 4 0 R /A << /S /URI /URI (https://outline.test) >> "
            + "/First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title (child) /Parent 5 0 R /A << /S /Launch /F (child.exe) >> >>");

        var actions = Extract(pdf);
        Assert.Equal(new[] { "https://outline.test", "child.exe" }, actions.Select(a => a.Target));
        Assert.All(actions, a => Assert.Equal(PdfActionSource.Outline, a.Source));
        Assert.All(actions, a => Assert.Equal(-1, a.PageIndex));
    }

    /// <summary>An outline whose <c>/Next</c> points back at itself is walked once.</summary>
    [Fact]
    public void ACyclicOutlineIsListedOnce()
    {
        byte[] pdf = Doc(
            catalog: "/Outlines 4 0 R",
            pageExtra: "",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R >>",
            "<< /Title (loop) /Parent 4 0 R /Next 5 0 R /First 5 0 R /A << /S /URI /URI (loop) >> >>");

        Assert.Equal("loop", Assert.Single(Extract(pdf)).Target);
    }

    /// <summary>
    /// A widget the field walk reaches is listed once, as the field's. The annotation walk does not
    /// list it again.
    /// </summary>
    [Fact]
    public void AFieldWidgetIsListedOnceAsTheFields()
    {
        byte[] pdf = Doc(
            catalog: "/AcroForm << /Fields [4 0 R] >>",
            pageExtra: "/Annots [4 0 R]",
            "<< /T (name) /FT /Btn /Type /Annot /Subtype /Widget /Rect [0 0 10 10] "
            + "/A << /S /URI /URI (w) >> >>");

        PdfAction action = Assert.Single(Extract(pdf));
        Assert.Equal("name", action.FieldName);
        Assert.Equal(PdfActionSource.Field, action.Source);
    }

    /// <summary>The control: a widget no field reaches is listed as an annotation.</summary>
    [Fact]
    public void AWidgetNoFieldReachesIsListedAsAnAnnotation()
    {
        byte[] pdf = Doc(
            catalog: "",
            pageExtra: "/Annots [4 0 R]",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /A << /S /URI /URI (w) >> >>");

        Assert.Equal(PdfActionSource.Annotation, Assert.Single(Extract(pdf)).Source);
    }

    [Fact]
    public void TheCatalogsWillCloseTriggerIsListedAndRuns()
    {
        byte[] pdf = Doc(
            catalog: "/AA << /WC << /S /URI /URI (bye) >> >>",
            pageExtra: "");

        PdfAction action = Assert.Single(Extract(pdf));
        Assert.Equal("WillClose", action.Trigger);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        string uri = null;
        runner.UriRequested += (_, e) => uri = e.Uri;
        Assert.NotNull(runner.RunCatalogAa("WillClose"));
        Assert.Equal("bye", uri);
    }

    /// <summary>
    /// A widget's page triggers (Table 197) are listed under the field, and the runner runs them by
    /// the same trigger name.
    /// </summary>
    [Fact]
    public void AFieldWidgetsPageOpenTriggerIsListedAndRuns()
    {
        byte[] pdf = Doc(
            catalog: "/AcroForm << /Fields [4 0 R] >>",
            pageExtra: "/Annots [4 0 R]",
            "<< /T (name) /FT /Tx /Type /Annot /Subtype /Widget /Rect [0 0 10 10] "
            + "/AA << /PO << /S /URI /URI (po) >> >> >>");

        PdfAction action = Assert.Single(Extract(pdf));
        Assert.Equal("PageOpen", action.Trigger);
        Assert.Equal("name", action.FieldName);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        string uri = null;
        runner.UriRequested += (_, e) => uri = e.Uri;
        Assert.NotNull(runner.RunFieldAction("name", "PageOpen"));
        Assert.Equal("po", uri);
    }

    /// <summary>
    /// An <c>/AA</c> entry under a key its dictionary does not define is still listed, under that
    /// key. A reader that runs it does something the list would otherwise hide.
    /// </summary>
    [Fact]
    public void AnAdditionalActionUnderAnUndefinedKeyIsStillListed()
    {
        byte[] pdf = Doc(
            catalog: "",
            pageExtra: "/AA << /O << /S /URI /URI (open) >> /Zz << /S /URI /URI (odd) >> >>");

        var actions = Extract(pdf);
        Assert.Equal(new[] { "PageOpen", "Zz" }, actions.Select(a => a.Trigger));
        Assert.All(actions, a => Assert.Equal(PdfActionSource.Page, a.Source));
        Assert.All(actions, a => Assert.Equal(0, a.PageIndex));
    }

    /// <summary>
    /// A field's name is its fully qualified name however each <c>/T</c> on its path is written.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFieldActionCarriesTheFullyQualifiedName(bool indirect)
    {
        byte[] pdf = Doc(
            catalog: "/AcroForm << /Fields [4 0 R] >>",
            pageExtra: "",
            "<< /T " + (indirect ? "5 0 R" : "(group)") + " /Kids [6 0 R] >>",
            "(group)",
            "<< /T " + (indirect ? "7 0 R" : "(kid)") + " /Parent 4 0 R /A << /S /URI /URI (k) >> >>",
            "(kid)");

        Assert.Equal("group.kid", Assert.Single(Extract(pdf)).FieldName);
    }

    /// <summary>
    /// A kid with an empty <c>/T</c> contributes no segment to the name its action is listed under.
    /// </summary>
    /// <remarks>
    /// <c>AcroFieldClassification</c> reads such a kid as the field's widget, not a field, and
    /// <c>PdfFormField.FullyQualifiedName</c> skips an empty name. So the form reader lists one field,
    /// <c>group</c>, and that is the name the widget's action belongs to. The dispatcher called it
    /// <c>group.</c>, a name nothing else in the library produces.
    /// </remarks>
    [Fact]
    public void AnEmptyPartialNameAddsNoSegment()
    {
        byte[] pdf = Doc(
            catalog: "/AcroForm << /Fields [4 0 R] >>",
            pageExtra: "",
            "<< /T (group) /Kids [5 0 R] >>",
            "<< /T () /Parent 4 0 R /FT /Tx /A << /S /URI /URI (k) >> >>");

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        string listed = Assert.Single(doc.AcroForm.Fields).FullyQualifiedName;

        Assert.Equal("group", listed);
        Assert.Equal(listed, Assert.Single(PdfActionDispatcher.ExtractActions(doc)).FieldName);
    }

    /// <summary>
    /// The calculation order names each field by its fully qualified name, which is the name
    /// <see cref="PdfActionRunner.RunFieldAction"/> finds it by.
    /// </summary>
    /// <remarks>
    /// It returned the raw leaf <c>/T</c>, so a calculated field in a group came back as
    /// <c>total</c>. <c>RunFieldAction("total", "Calculate")</c> then found nothing, and an indirect
    /// <c>/T</c> left the field out of the order altogether.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCalculationOrderNamesFieldsTheRunnerCanFind(bool indirect)
    {
        byte[] pdf = Doc(
            catalog: "/AcroForm << /Fields [4 0 R] /CO [5 0 R] >>",
            pageExtra: "",
            "<< /T (invoice) /Kids [5 0 R] >>",
            "<< /T " + (indirect ? "6 0 R" : "(total)") + " /Parent 4 0 R /FT /Tx "
            + "/AA << /C << /S /URI /URI (calc) >> >> >>",
            "(total)");

        string name;
        using (var ms = new MemoryStream(pdf))
            name = Assert.Single(PdfActionDispatcher.GetCalculationOrder(ms));
        Assert.Equal("invoice.total", name);

        using var ms2 = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms2);
        var runner = new PdfActionRunner(doc);
        string uri = null;
        runner.UriRequested += (_, e) => uri = e.Uri;
        Assert.NotNull(runner.RunFieldAction(name, "Calculate"));
        Assert.Equal("calc", uri);
    }

    /// <summary>
    /// A <c>/CO</c> entry that no field in the tree is still named, by its own resolved <c>/T</c>.
    /// </summary>
    [Fact]
    public void ACalculationOrderEntryNoFieldReachesKeepsItsOwnName()
    {
        byte[] pdf = Doc(
            catalog: "/AcroForm << /Fields [] /CO [4 0 R] >>",
            pageExtra: "",
            "<< /T 5 0 R /FT /Tx >>",
            "(orphan)");

        using var ms = new MemoryStream(pdf);
        Assert.Equal("orphan", Assert.Single(PdfActionDispatcher.GetCalculationOrder(ms)));
    }

    /// <summary>
    /// <b>A document-wide ceiling on the actions listed.</b> Every trigger's chain has its own
    /// budget, so a document can point many triggers at one wide chain and have it listed once per
    /// trigger.
    /// </summary>
    /// <remarks>
    /// The fixture is 257 links sharing one 256-action chain, which lists 65,792 actions without the
    /// ceiling. The same shape with a hundred thousand links is 25.6 million. The assertions are the
    /// ceiling's own evidence: the count it stops at and the reason it reports.
    /// </remarks>
    [Fact]
    public void ManyTriggersSharingOneWideChainStopAtTheDocumentCeiling()
    {
        const int chain = PdfActionDispatcher.MaxActionsPerChain;
        int links = PdfActionDispatcher.MaxActionsListed / chain + 1;

        var objects = new List<string>();
        int head = 4;
        int firstLink = head + chain;
        objects.Add("<< /S /URI /URI (h) /Next ["
                    + string.Join(" ", Enumerable.Range(head + 1, chain - 1).Select(n => n + " 0 R")) + "] >>");
        for (int i = 1; i < chain; i++) objects.Add("<< /S /URI /URI (a" + i + ") >>");
        for (int i = 0; i < links; i++) objects.Add("<< /Subtype /Link /Rect [0 0 1 1] /A " + head + " 0 R >>");

        byte[] pdf = Doc(
            catalog: "",
            pageExtra: "/Annots [" + string.Join(" ", Enumerable.Range(firstLink, links).Select(n => n + " 0 R")) + "]",
            objects.ToArray());

        var actions = Extract(pdf);
        Assert.Equal(PdfActionDispatcher.MaxActionsListed, actions.Count);
        Assert.Contains(PdfActionDispatcher.MaxActionsListed.ToString(), PdfActionDispatcher.LastChainDiagnostic,
                        StringComparison.Ordinal);
    }

    /// <summary>The control: under the ceiling, nothing is reported.</summary>
    [Fact]
    public void ADocumentUnderTheCeilingReportsNothing()
    {
        byte[] pdf = Doc(
            catalog: "",
            pageExtra: "/Annots [4 0 R 4 0 R]",
            "<< /Subtype /Link /Rect [0 0 1 1] /A << /S /URI /URI (x) >> >>");

        Assert.Single(Extract(pdf));
        Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
    }

    // ====================================================================

    private static IReadOnlyList<PdfAction> Extract(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return PdfActionDispatcher.ExtractActions(doc);
    }

    /// <summary>
    /// Catalog 1, pages 2, page 3, then <paramref name="objects"/> from 4. The catalog and page take
    /// extra entries.
    /// </summary>
    private static byte[] Doc(string catalog, string pageExtra, params string[] objects)
    {
        var all = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> " + pageExtra + " >>",
        };
        all.AddRange(objects);
        return ActionTargetAgreementTests.Build(all.ToArray());
    }
}
