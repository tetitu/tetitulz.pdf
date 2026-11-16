// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// A tree the page's scripts build is measured before it is serialised: one <c>innerHTML</c> of
/// <c>'&lt;span&gt;'.repeat(100000)</c> nests a hundred thousand elements, and AngleSharp's serialiser
/// recurses — a stack overflow ends the process. Past <see cref="HtmlScriptLimits.MaxDocumentDepth"/>
/// the page is laid out as written, and said; a shallow change still lands (the control).
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class PostScriptDepthTests
{
    [Fact]
    public void AdversarialScriptBuildingADeepTree_IsReported_AndThePageIsLaidOutAsWritten()
    {
        var (text, reports) = PageScriptTests.Convert(
            "<html><body><p>kept</p><script>document.body.innerHTML = '<span>'.repeat(100000) + 'deep';</script></body></html>");

        Assert.Contains(reports, r => r.Contains("MaxDocumentDepth", StringComparison.Ordinal));
        Assert.Contains("kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deep", text, StringComparison.Ordinal);

        var (shallow, _) = PageScriptTests.Convert(
            "<html><body><p>kept</p><script>document.body.innerHTML = '<span>'.repeat(10) + 'shallow';</script></body></html>");
        Assert.Contains("shallow", shallow, StringComparison.Ordinal);
    }
}
