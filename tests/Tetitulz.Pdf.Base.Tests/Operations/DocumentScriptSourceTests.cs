// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Which scripts <see cref="PdfOperations.ExecuteDocumentScripts(Stream, PdfJsExecutionContext)"/>
/// runs, now that the action list it reads names annotation and outline actions too.
/// </summary>
/// <remarks>
/// <para>It runs every JavaScript action <c>PdfActionDispatcher</c> lists. It never ran the
/// document-level scripts in <c>/Names /JavaScript</c>, the one set §12.6.4.17 says is executed
/// "when the document is opened", because the dispatcher never listed them.</para>
/// <para>Once the dispatcher lists a link's or an outline item's script as well, running
/// "everything listed" would run a script the user has to click. It runs what it ran before, plus
/// the document-level scripts, and no annotation or outline script.</para>
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class DocumentScriptSourceTests
{
    [Fact]
    public void DocumentScriptsRunTheNameTreeAndNotAnAnnotationOrOutlineScript()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /JavaScript /JS (open) >> "
            + "/Names << /JavaScript << /Names [(lib) << /S /JavaScript /JS (docjs) >>] >> >> "
            + "/AcroForm << /Fields [5 0 R] >> /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] "
            + "/AA << /O << /S /JavaScript /JS (page) >> >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /JavaScript /JS (link) >> >>",
            "<< /T (total) /FT /Tx /AA << /C << /S /JavaScript /JS (calc) >> >> >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R >>",
            "<< /Title (item) /Parent 6 0 R /A << /S /JavaScript /JS (outline) >> >>");

        var plugin = new ActionScriptTextStreamTests.RecordingJsPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            PdfOperations.ExecuteDocumentScripts(new MemoryStream(pdf), new PdfJsExecutionContext());
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }

        Assert.Equal(new[] { "docjs", "open", "page", "calc" }, plugin.Ran);
    }

    /// <summary>
    /// <see cref="PdfOperations.ExecuteOpenScripts(Stream, PdfJsExecutionContext)"/> runs what a
    /// viewer runs at open — the document-level script, the open action, the first page's open
    /// action — in that order, and no field, link or outline script.
    /// </summary>
    [Fact]
    public void OpenScriptsRunTheNameTreeTheOpenActionAndTheFirstPage_AndNoFieldScript()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /JavaScript /JS (open) >> "
            + "/Names << /JavaScript << /Names [(lib) << /S /JavaScript /JS (docjs) >>] >> >> "
            + "/AcroForm << /Fields [5 0 R] >> /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [4 0 R] "
            + "/AA << /O << /S /JavaScript /JS (page) >> /C << /S /JavaScript /JS (pageclose) >> >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /JavaScript /JS (link) >> >>",
            "<< /T (total) /FT /Tx /AA << /C << /S /JavaScript /JS (calc) >> >> >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R >>",
            "<< /Title (item) /Parent 6 0 R /A << /S /JavaScript /JS (outline) >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
            + "/AA << /O << /S /JavaScript /JS (second) >> >> >>");

        var plugin = new ActionScriptTextStreamTests.RecordingJsPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            PdfOperations.ExecuteOpenScripts(new MemoryStream(pdf), new PdfJsExecutionContext());
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }

        Assert.Equal(new[] { "docjs", "open", "page" }, plugin.Ran);
    }
}
