// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="LinkTargets"/>, the one answer to "may this target from a converted file be a link" for the
/// Builder's HTML and PDF exporters, the XFA importer and the DOCX converter. The scheme is read as a
/// browser's URL parser reads it, so a leading space, a control character or a tab inside <c>javascript:</c> does
/// not make it a relative target.
/// </summary>
public class LinkTargetsTests
{
    [Theory]
    [InlineData("https://x.example/a", true)]
    [InlineData("mailto:a@x.example", true)]
    [InlineData("tel:+972-3-0000000", true)]
    [InlineData("#section", true)]
    [InlineData("page.html", true)]
    [InlineData("a/b:c", true)]                       // the colon is inside a path
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]       // a leading space, which the URL parser strips
    [InlineData("java\tscript:alert(1)", false)]      // a tab, which the URL parser removes
    [InlineData("java\nscript:alert(1)", false)]
    [InlineData("\u0001javascript:alert(1)", false)]  // a leading C0 control
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("data:text/html,<b>x</b>", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void ATargetIsALinkOnlyForADocumentLinksScheme(string? uri, bool safe)
        => Assert.Equal(safe, LinkTargets.IsSafe(uri));

    [Theory]
    [InlineData("HTTPS", true)]
    [InlineData("ftps", true)]
    [InlineData("javascript", false)]
    [InlineData("file", false)]
    public void ASchemeIsADocumentLinksOrNot(string scheme, bool safe)
        => Assert.Equal(safe, LinkTargets.IsSafeScheme(scheme));
}
