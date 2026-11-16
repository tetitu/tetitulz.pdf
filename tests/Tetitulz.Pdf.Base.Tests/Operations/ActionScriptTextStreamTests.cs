// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A JavaScript action's <c>/JS</c> <b>text stream</b> is decoded as a text stream, by the
/// dispatcher and the runner alike.
/// </summary>
/// <remarks>
/// <para>§12.6.4.17 allows <c>/JS</c> to be "a text string or text stream", and §7.9.3 says a text
/// stream's bytes "shall meet the same requirements as a text string": PDFDocEncoding, or UTF-16BE
/// behind <c>FE FF</c>, or (PDF 2.0) UTF-8 behind <c>EF BB BF</c>. Both sides decoded the bytes as
/// UTF-8. A UTF-16BE script became NULs and replacement characters, a PDFDocEncoded <c>0x80</c>
/// (a bullet) became U+FFFD, and a UTF-8 script kept its byte-order mark as the first character of
/// the source.</para>
/// <para><b>In <c>PluginHostCollection</c></b> because the runner hands the script to a registered
/// <see cref="IJavaScriptPlugin"/>, and <c>PdfPluginHost.Instance</c> is process-wide.</para>
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class ActionScriptTextStreamTests
{
    private const string Script = "var s = 'caf\u00e9';";

    /// <summary>
    /// Eight bytes that are not a zlib stream, so <c>/FlateDecode</c> throws on them. Three such
    /// bytes did not make it throw, and a fixture that decodes cleanly tests nothing here.
    /// </summary>
    private static readonly byte[] NotDeflate = { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };

    public static IEnumerable<object[]> Encodings()
    {
        // UTF-16BE behind its byte-order mark (§7.9.2.2).
        yield return new object[] { "utf16be",
            new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(Script)).ToArray(), Script };

        // UTF-8 behind its byte-order mark (PDF 2.0): the mark is not part of the script.
        yield return new object[] { "utf8bom",
            new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Script)).ToArray(), Script };

        // PDFDocEncoding: 0x80 is a bullet (Annex D), not a UTF-8 continuation byte.
        yield return new object[] { "pdfdoc",
            Encoding.ASCII.GetBytes("// ").Concat(new byte[] { 0x80 }).ToArray(), "// \u2022" };
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void AScriptStreamIsDecodedAsATextStreamByBothSides(string label, byte[] body, string expected)
    {
        byte[] pdf = ActionTargetAgreementTests.OpenActionPdf(
            "<< /S /JavaScript /JS 5 0 R >>",
            ActionTargetAgreementTests.Stream(body));

        Assert.True(expected == ActionTargetAgreementTests.DispatcherTarget(pdf), label + ": dispatcher");
        Assert.True(expected == RunnerScript(pdf), label + ": runner");
    }

    /// <summary>The control: a plain ASCII script stream reads the same as it always did.</summary>
    [Fact]
    public void AnAsciiScriptStreamIsUnchanged()
    {
        byte[] pdf = ActionTargetAgreementTests.OpenActionPdf(
            "<< /S /JavaScript /JS 5 0 R >>",
            ActionTargetAgreementTests.Stream(Encoding.ASCII.GetBytes("app.alert(1);")));

        Assert.Equal("app.alert(1);", ActionTargetAgreementTests.DispatcherTarget(pdf));
        Assert.Equal("app.alert(1);", RunnerScript(pdf));
    }

    /// <summary>
    /// A script stream that will not decode costs its own target and says why. The rest of the
    /// document's actions are still listed.
    /// </summary>
    /// <remarks>
    /// The dispatcher decoded the stream outside any handler, so one corrupt script threw out of
    /// <see cref="PdfActionDispatcher.ExtractActions(PdfDocument)"/> and a caller got no actions at
    /// all. <c>Pdf20DeprecationChecker</c> catches that exception and reports nothing, so a document
    /// could hide its <c>/Launch</c> behind one bad stream.
    /// </remarks>
    [Fact]
    public void AnUndecodableScriptStreamLosesOnlyItsOwnTarget()
    {
        byte[] pdf = ActionTargetAgreementTests.OpenActionPdf(
            "<< /S /JavaScript /JS 5 0 R /Next << /S /Launch /F (after.exe) >> >>",
            ActionTargetAgreementTests.Stream(NotDeflate, " /Filter /FlateDecode"));

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        IReadOnlyList<PdfAction> actions = PdfActionDispatcher.ExtractActions(doc);

        Assert.Equal(2, actions.Count);
        Assert.Equal(PdfActionType.JavaScript, actions[0].ActionType);
        Assert.Null(actions[0].Target);
        Assert.Equal("after.exe", actions[1].Target);
        Assert.NotNull(PdfActionDispatcher.LastTargetError);

        var runner = new PdfActionRunner(doc);
        string launched = null;
        runner.LaunchRequested += (_, e) => launched = e.Target;
        runner.RunCatalogOpenAction(new PdfJsExecutionContext());
        Assert.NotNull(PdfActionRunner.LastStreamExtractError);
        Assert.Equal("after.exe", launched);
    }

    /// <summary>
    /// Both slots clear at the start of the next operation, so a clean document after a broken one
    /// does not inherit the broken one's error.
    /// </summary>
    [Fact]
    public void TheDecodeErrorSlotsClearOnTheNextOperation()
    {
        byte[] broken = ActionTargetAgreementTests.OpenActionPdf(
            "<< /S /JavaScript /JS 5 0 R >>",
            ActionTargetAgreementTests.Stream(NotDeflate, " /Filter /FlateDecode"));
        byte[] clean = ActionTargetAgreementTests.OpenActionPdf("<< /S /URI /URI (https://example.test) >>");

        ActionTargetAgreementTests.DispatcherTarget(broken);
        Assert.NotNull(PdfActionDispatcher.LastTargetError);
        ActionTargetAgreementTests.DispatcherTarget(clean);
        Assert.Null(PdfActionDispatcher.LastTargetError);

        RunnerScript(broken, expectScript: false);
        Assert.NotNull(PdfActionRunner.LastStreamExtractError);
        ActionTargetAgreementTests.RunnerTarget(clean);
        Assert.Null(PdfActionRunner.LastStreamExtractError);
    }

    // ====================================================================

    /// <summary>The script text the runner handed to the registered engine.</summary>
    private static string RunnerScript(byte[] pdf, bool expectScript = true)
    {
        var plugin = new RecordingJsPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            new PdfActionRunner(doc).RunCatalogOpenAction(new PdfJsExecutionContext());
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }

        if (!expectScript) return null;
        return Assert.Single(plugin.Ran);
    }

    internal sealed class RecordingJsPlugin : IJavaScriptPlugin
    {
        internal readonly List<string> Ran = new();

        public string Name => "recording-js-stub";
        public string Description => "records the scripts it was handed";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public bool TryExecute(string script, PdfJsExecutionContext context)
        {
            Ran.Add(script);
            return true;
        }
    }
}
