// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A multiline field's value is wrapped measuring each word once: the line's width is carried from
/// word to word. Each word copied and measured the whole line, so a value whose line never filled —
/// a font measuring 0, a very wide box — cost its length squared over its word length.
/// </summary>
public sealed class FieldWrapWorkTests
{
    [Fact]
    public void AdversarialLongValueInAWideBox_IsMeasuredOnceAWord()
    {
        string value = string.Concat(Enumerable.Repeat("ab ", 40_000));
        byte[] pdf = FormPdfFixtures.TextField(rect: "[0 0 9000000 400]",
                                               fieldEntries: "/DA (/Helv 12 Tf 0 g) /Ff 4096");

        FormAppearance.WrapMeasuredChars = 0;
        using var output = new MemoryStream();
        var result = PdfFormFiller.Fill(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["name"] = value }, new FormFillOptions());

        Assert.Contains("name", result.FilledFields);
        // Each word measured once is the value's length; measured with the line before it, even
        // only up to MaxExactlyMeasuredLine, is more than twice it.
        Assert.InRange(FormAppearance.WrapMeasuredChars, value.Length / 2, 2L * value.Length);
    }

    /// <summary>The control: in a narrow box the value wraps, each line inside the box.</summary>
    [Fact]
    public void AValueInANarrowBox_StillWraps()
    {
        string value = string.Concat(Enumerable.Repeat("word ", 40));
        byte[] pdf = FormPdfFixtures.TextField(rect: "[0 0 100 400]",
                                               fieldEntries: "/DA (/Helv 12 Tf 0 g) /Ff 4096");

        using var output = new MemoryStream();
        PdfFormFiller.Fill(new MemoryStream(pdf), output,
            new Dictionary<string, string> { ["name"] = value }, new FormFillOptions());

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(output.ToArray()));
        var widget = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.GetObject(6);
        var ap = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(widget[Tetitulz.Pdf.Core.Objects.PdfName.AP]);
        var stream = (Tetitulz.Pdf.Core.Objects.PdfStream)doc.ResolveObject(ap[Tetitulz.Pdf.Core.Objects.PdfName.N]);
        var shown = Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(stream.GetDecodedData())
            .Count(op => op.Operator == "Tj");
        Assert.InRange(shown, 10, 40);
    }
}
