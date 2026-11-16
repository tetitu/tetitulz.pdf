// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// End-to-end cover for the plugin seam introduced when the Jint engine left
/// <c>Tetitulz.Pdf.Base</c> (GAPS.md §15b): with <see cref="JintJavaScriptPlugin"/> registered,
/// document JavaScript runs and mutates the context exactly as it did when the engine was
/// in-assembly.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart — the same <c>PdfActionRunner</c> path with <b>no</b> engine registered —
/// is <c>Tetitulz.Pdf.Base.Tests.DeferredBatchTests
/// .ActionRunner_JavaScriptAction_WithNoEnginePlugin_IsReportedNotSilentlyDropped</c>. The pair is
/// the point: each half passes only if the dispatch actually consults the plugin host, so neither
/// can be satisfied by a hard-coded answer.
/// </para>
/// <para>
/// <see cref="PdfPluginHost"/> is a process-wide singleton, so these tests register in the
/// constructor and unregister in <see cref="Dispose"/> rather than leaving global state behind for
/// whatever runs next.
/// </para>
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class JintJavaScriptPluginTests : IDisposable
{
    private readonly JintJavaScriptPlugin _plugin = new();

    public JintJavaScriptPluginTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    [Fact]
    public void Registered_PluginIsDiscoverableThroughTheRunner()
    {
        Assert.True(PluginJavaScriptRunner.IsAvailable);
    }

    [Fact]
    public void Registered_ExecuteJavaScript_RunsTheScriptAndMutatesTheContext()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["Name"] = "initial";

        bool executed = PdfOperations.ExecuteJavaScript(
            "this.getField('Name').value = 'modified';", ctx);

        Assert.True(executed);
        Assert.Null(PluginJavaScriptRunner.LastError);
        Assert.Equal("modified", ctx.ModifiedFieldValues["Name"]);
    }

    [Fact]
    public void Registered_ActionRunner_ExecutesCatalogOpenActionAndMutatesContext()
    {
        byte[] pdf = BuildPdfWithCatalogOpenJsAction(
            "this.getField('Name').value = 'modified';");

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);
        var jsCtx = new PdfJsExecutionContext();
        jsCtx.FieldValues["Name"] = "initial";

        var result = runner.RunCatalogOpenAction(jsCtx);

        Assert.NotNull(result);
        Assert.True(result.JavaScriptExecuted);
        Assert.Equal("modified", jsCtx.ModifiedFieldValues["Name"]);
    }

    /// <summary>
    /// A script that throws still counts as <i>executed</i>: the failure belongs on
    /// <see cref="PdfJsExecutionContext.Error"/>, not on
    /// <see cref="PluginJavaScriptRunner.LastError"/>, which means "no engine ran it". Collapsing
    /// the two would make a broken script indistinguishable from a missing package.
    /// </summary>
    [Fact]
    public void Registered_ScriptThatThrows_IsExecutedWithTheErrorOnTheContext()
    {
        var ctx = new PdfJsExecutionContext();

        bool executed = PdfOperations.ExecuteJavaScript("this is not valid javascript }{", ctx);

        Assert.True(executed);
        Assert.Null(PluginJavaScriptRunner.LastError);
        Assert.NotNull(ctx.Error);
    }

    /// <summary>
    /// A committed value through the real engine (<c>PdfFieldScripts</c>, which the viewer calls):
    /// the Acrobat helpers a form's calculation and validation use, end to end.
    /// </summary>
    [Fact]
    public void CommittingAValue_RunsTheFormsRealCalculation()
    {
        byte[] pdf = BuildCalculatingForm(validateA: null);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var r = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "3",
            new System.Collections.Generic.Dictionary<string, string> { ["a"] = "", ["b"] = "4", ["total"] = "" });

        Assert.True(r.ScriptsRan);
        Assert.True(r.Accepted);
        Assert.Equal("7", r.OtherChanges["total"]);
    }

    [Fact]
    public void CommittingAValue_ARangeValidationRefusesItWithItsMessage()
    {
        byte[] pdf = BuildCalculatingForm(validateA: "AFRange_Validate(true, 0, true, 10);");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var values = new System.Collections.Generic.Dictionary<string, string> { ["a"] = "", ["b"] = "4", ["total"] = "" };

        var refused = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "50", values);
        Assert.False(refused.Accepted);
        Assert.Equal("Invalid value: must be greater than or equal to 0 and less than or equal to 10.", refused.RejectionMessage);
        Assert.Empty(refused.OtherChanges);

        var accepted = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "5", values);
        Assert.True(accepted.Accepted);                                  // the control: inside the range
        Assert.Equal("9", accepted.OtherChanges["total"]);
    }

    /// <summary>
    /// A field's Validate script calls a function the document-level script defines: the commit runs
    /// that script first, in the same environment, so the function is there — refusing 50 and
    /// accepting 5. Without the document-level script the same Validate script fails, closed.
    /// </summary>
    [Fact]
    public void CommittingAValue_AFieldScriptCallsADocumentLevelFunction()
    {
        const string validate = "event.rc = inRange(Number(event.value));";
        byte[] pdf = BuildCalculatingForm(validateA: validate,
            documentScript: "function inRange(v) { return v >= 0 && v <= 10; }");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var values = new System.Collections.Generic.Dictionary<string, string> { ["a"] = "", ["b"] = "4", ["total"] = "" };

        Assert.False(Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "50", values).Accepted);
        var accepted = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "5", values);
        Assert.True(accepted.Accepted);
        Assert.Equal("9", accepted.OtherChanges["total"]);

        using var without = PdfDocument.Open(new MemoryStream(BuildCalculatingForm(validateA: validate)));
        var refused = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(without, "a", "5", values);
        Assert.False(refused.Accepted);
        Assert.Contains("inRange", refused.RejectionMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ExecuteDocumentScripts</c> opens the document the way a viewer does: the probe document's
    /// own answers show its document-level function visible to the open action, the open event's
    /// type and name, and its fields described.
    /// </summary>
    [Fact]
    public void ExecuteDocumentScripts_SharesOneEnvironment_NamesTheEvent_AndDescribesTheFields()
    {
        string pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Acrobat", "AcrobatProbe.pdf");
        PdfJsExecutionContext ctx = PdfOperations.ExecuteDocumentScripts(pdf);

        Assert.Null(ctx.Error);
        string results = ctx.ModifiedFieldValues["results"];
        Assert.Contains("G.probeDocFn()\tstring:doc-level visible", results, StringComparison.Ordinal);
        Assert.Contains("G.event.type/name\tstring:Doc/Open", results, StringComparison.Ordinal);
        Assert.Contains("G.results.type\tstring:text", results, StringComparison.Ordinal);
        Assert.Contains("G.numFields\tnumber:3", results, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the scripts read of the document comes from the document: the second probe's own answers
    /// show a push button's three captions (<c>/MK /CA</c>, <c>/AC</c>, <c>/RC</c>), a widget named by
    /// <c>name.N</c> with its own rect and page, a field group's kids, the <c>/Info</c> entries —
    /// a custom key kept and the dates as <c>Date</c> objects — and, when the caller asks, the
    /// attachment as a Data object with its contents. By default a script sees no attachment.
    /// </summary>
    [Fact]
    public void ExecuteDocumentScripts_HandsTheScriptsTheDocumentsCaptionsInfo_AndItsAttachmentsOnlyWhenAsked()
    {
        string pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Acrobat", "AcrobatProbe2.pdf");
        PdfJsExecutionContext ctx = PdfOperations.ExecuteDocumentScripts(pdf, new PdfJsExecutionContext { ExposeAttachments = true });

        Assert.Null(ctx.Error);
        string results = ctx.ModifiedFieldValues["results"];
        foreach (string answer in new[]
        {
            "F.pb.buttonGetCaption()\tstring:PB Caption", "F.pb.buttonGetCaption(1)\tstring:PB Down",
            "F.pb.buttonGetCaption(2)\tstring:PB Rollover",
            "F.rb.1.rect\tstring:300,520,320,500", "F.rb.1.page\tnumber:1", "F.rb.page\tstring:[0,1]",
            "F.grp.getArray\tstring:grp.a,grp.b", "S.SUM(grp)\tnumber:7",
            "I.author\tstring:Probe Author", "I.info.Custom\tstring:custom value", "I.info.author\tstring:Probe Author",
            "I.creationDate\tstring:Date:2026-3-4",
            "I.dataObjects\tnumber:1", "I.dataObjects[0].size\tnumber:11", "I.dataObjects[0].MIMEType\tstring:text/plain",
            "I.dataObjects[0].description\tstring:A test attachment", "I.getDataObjectContents\tstring:hello world",
        })
            Assert.Contains(answer, results, StringComparison.Ordinal);

        // The default: the same document, and its scripts see no attachment and read none.
        PdfJsExecutionContext closed = PdfOperations.ExecuteDocumentScripts(pdf);
        Assert.Null(closed.Error);
        Assert.Empty(closed.Attachments);
        Assert.Empty(closed.AttachmentContents);
        Assert.Contains("I.dataObjects\tstring:null", closed.ModifiedFieldValues["results"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Text fields <c>a</c>, <c>b</c> and <c>total</c>, <c>/CO [total]</c>; total's Calculate is
    /// <c>AFSimple_Calculate("SUM", ["a", "b"])</c>, and <c>a</c> carries <paramref name="validateA"/>.
    /// </summary>
    /// <summary>
    /// A script's writes to a field's <c>readonly</c>, <c>display</c> and <c>required</c> reach the
    /// commit's result as field states — for a viewer to apply — a property not written left null;
    /// a refused value carries none. The document itself is not changed.
    /// </summary>
    [Fact]
    public void FieldStateWrites_ReachTheCommitResult_AndNoneWhenRefused()
    {
        const string validate = "this.getField('b').readonly = true; this.getField('total').display = display.noView;" +
                                " this.getField('a').required = true; event.rc = Number(event.value) < 100;";
        byte[] pdf = BuildCalculatingForm(validateA: validate);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var values = new System.Collections.Generic.Dictionary<string, string> { ["a"] = "", ["b"] = "4", ["total"] = "" };

        var accepted = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "5", values);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.FieldStates["b"].ReadOnly);
        Assert.Null(accepted.FieldStates["b"].Hidden);
        Assert.True(accepted.FieldStates["total"].Hidden);
        Assert.True(accepted.FieldStates["a"].Required);
        Assert.Null(accepted.FieldStates["a"].ReadOnly);

        var refused = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "500", values);
        Assert.False(refused.Accepted);
        Assert.Empty(refused.FieldStates);
    }

    /// <summary>The total's Format script through the real engine: the value is the sum, the text its currency form.</summary>
    [Fact]
    public void CommittingAValue_FormatsTheCalculatedTotal()
    {
        byte[] pdf = BuildCalculatingForm(validateA: null, formatTotal: "AFNumber_Format(2, 0, 0, 0, \"$\", true);");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var r = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", "1234.5",
            new System.Collections.Generic.Dictionary<string, string> { ["a"] = "", ["b"] = "0", ["total"] = "" });

        Assert.Equal("1234.5", r.OtherChanges["total"]);
        Assert.Equal("$1,234.50", r.DisplayValues["total"]);
    }

    /// <summary>
    /// A keystroke through the real engine: <c>AFNumber_Keystroke</c> judges the value the keystroke
    /// would leave — the change in place of the selection — so a minus typed before "12" is a number
    /// and one typed after it is not; a letter is refused.
    /// </summary>
    [Theory]
    [InlineData("12", 0, 0, "-", true)]
    [InlineData("12", 2, 2, "-", false)]
    [InlineData("12", 2, 2, "5", true)]
    [InlineData("12", 1, 2, "x", false)]
    [InlineData("12", 0, 2, "7", true)]
    [InlineData("1.2", 3, 3, ".", false)]       // one decimal separator
    [InlineData("12", 2, 2, ".", true)]
    [InlineData("", 0, 0, "-", true)]           // a partial number while typing
    public void AKeystroke_IsJudgedWithItsSelection(string value, int start, int end, string change, bool accepted)
    {
        byte[] pdf = BuildCalculatingForm(validateA: null, keystrokeA: "AFNumber_Keystroke(2, 0, 0, 0, \"\", true);");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var r = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Keystroke(doc, "a", value, start, end, change,
            new System.Collections.Generic.Dictionary<string, string>(), TimeSpan.FromSeconds(5));

        Assert.True(r.ScriptRan);
        Assert.Equal(accepted, r.Accepted);
    }

    /// <summary>At commit a number must have a digit: "-" alone is refused, "" and "-12" are not.</summary>
    [Theory]
    [InlineData("-", false)]
    [InlineData("-12", true)]
    [InlineData("", true)]
    public void AtCommit_ANumberNeedsADigit(string value, bool accepted)
    {
        byte[] pdf = BuildCalculatingForm(validateA: null, keystrokeA: "AFNumber_Keystroke(2, 0, 0, 0, \"\", true);");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var r = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Commit(doc, "a", value,
            new System.Collections.Generic.Dictionary<string, string>());

        Assert.Equal(accepted, r.Accepted);
    }

    /// <summary>A Keystroke script may rewrite what was typed; the rewrite comes back.</summary>
    [Fact]
    public void AKeystrokeScript_RewritesTheChange()
    {
        byte[] pdf = BuildCalculatingForm(validateA: null, keystrokeA: "event.change = event.change.toUpperCase();");
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var r = Tetitulz.Pdf.Base.Operations.Forms.PdfFieldScripts.Keystroke(doc, "a", "ab", 2, 2, "c",
            new System.Collections.Generic.Dictionary<string, string>(), TimeSpan.FromSeconds(5));

        Assert.True(r.Accepted);
        Assert.Equal("C", r.Change);
    }

    private static byte[] BuildCalculatingForm(string? validateA, string? formatTotal = null, string? keystrokeA = null,
        string? documentScript = null)
    {
        static string Hex(string s)
        {
            var hex = new StringBuilder("<");
            foreach (byte b in Encoding.UTF8.GetBytes(s)) hex.AppendFormat("{0:X2}", b);
            return hex.Append('>').ToString();
        }
        static string Field(string name, string aa)
            => "<< /Type /Annot /Subtype /Widget /FT /Tx /T (" + name + ") /P 3 0 R /Rect [0 0 100 20]"
               + (aa.Length > 0 ? " /AA << " + aa + " >>" : "") + " >>";

        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] /CO [6 0 R] >>"
                + (documentScript == null ? "" : " /Names << /JavaScript << /Names [(lib) 7 0 R] >> >>") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R 6 0 R] /Resources << >> >>",
            Field("a", (validateA == null ? "" : "/V << /S /JavaScript /JS " + Hex(validateA) + " >>")
                + (keystrokeA == null ? "" : " /K << /S /JavaScript /JS " + Hex(keystrokeA) + " >>")),
            Field("b", ""),
            Field("total", "/C << /S /JavaScript /JS " + Hex("AFSimple_Calculate(\"SUM\", [\"a\", \"b\"]);") + " >>"
                + (formatTotal == null ? "" : " /F << /S /JavaScript /JS " + Hex(formatTotal) + " >>")),
        };
        if (documentScript != null) objects.Add("<< /S /JavaScript /JS " + Hex(documentScript) + " >>");
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n{1}\nendobj\n", i + 1, objects[i]);
        }
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", objects.Count + 1);
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", objects.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // Copied from Tetitulz.Pdf.Base.Tests.DeferredBatchTests rather than shared: CLAUDE.md bans
    // cross-project test dependencies, and tests must build their own in-memory PDF.
    private static byte[] BuildPdfWithCatalogOpenJsAction(string jsBody)
    {
        const string cs = "0 0 612 792 re";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /OpenAction 5 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        // The JS body goes in as a hex string so parentheses in the script need no escaping.
        var hex = new StringBuilder("<");
        foreach (byte b in Encoding.UTF8.GetBytes(jsBody)) hex.AppendFormat("{0:X2}", b);
        hex.Append(">");
        sb.AppendFormat("5 0 obj\n<< /S /JavaScript /JS {0} >>\nendobj\n", hex);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
