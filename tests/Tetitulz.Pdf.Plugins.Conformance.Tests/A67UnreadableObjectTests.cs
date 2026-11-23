// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The A67 association check reads every object for the <c>/AF</c> arrays that name a file specification: an object
/// it cannot read is not an absent one, so the check says it was partial, as its siblings do.
/// </summary>
public class A67UnreadableObjectTests
{
    private const string Tree = " /Names << /EmbeddedFiles << /Names [(a.pdf) 5 0 R] >> >> /AF [5 0 R]";
    private const string Spec = "<< /Type /Filespec /F (a.pdf) /UF (a.pdf) /AFRelationship /Data /EF << /F 6 0 R >> >>";
    private const string File = "<< /Type /EmbeddedFile /Subtype /text#2Fplain /Length 5 >>\nstream\nhello\nendstream";

    /// <summary>An unreadable object (a stray <c>]</c>) makes the check say it was partial; the control, readable, does not.</summary>
    [Theory]
    [InlineData("]", true)]
    [InlineData("<< /Note (x) >>", false)]
    public void AnUnreadableObject_MakesTheAssociationCheckSayItIsPartial(string seventh, bool partial)
    {
        byte[] pdf = PdfAEmbeddedFileRuleTests.PdfA(3, Tree, "B", Spec, File, seventh);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part3);

        Assert.Equal(partial, result.Informational.Any(v => v.Message.Contains("A67 association check could not read", StringComparison.Ordinal)));
    }
}
