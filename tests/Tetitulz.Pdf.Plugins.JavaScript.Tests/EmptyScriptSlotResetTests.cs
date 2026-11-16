// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// <see cref="PdfJsEngine.LastUnsupportedRequest"/> is reset at the start of every
/// <see cref="PdfJsEngine.Execute"/>, the empty script's early return included: a host reading it after
/// an empty <c>/JS</c> action saw the previous script's report.
/// </summary>
public sealed class EmptyScriptSlotResetTests
{
    [Fact]
    public void AnEmptyScript_ClearsThePreviousScriptsUnsupportedRequest()
    {
        PdfJsEngine.Execute("this.submitForm()", new PdfJsExecutionContext());
        Assert.NotNull(PdfJsEngine.LastUnsupportedRequest);   // potency: the first script did report

        PdfJsEngine.Execute("", new PdfJsExecutionContext());
        Assert.Null(PdfJsEngine.LastUnsupportedRequest);
    }
}
