// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A text field the builder adds to an existing form whose default value needs more than one line
/// — the field is multiline, or the value holds a line break — leaves <c>/NeedAppearances true</c>
/// on the form, since the appearance the builder writes draws one line. The control, a one-line
/// value, leaves the form's flag as it was.
/// </summary>
public class FormFieldBuilderMultilineTests
{
    [Theory]
    [InlineData(true, "Line 1", true)]
    [InlineData(false, "Line 1\nLine 2", true)]
    [InlineData(false, "One line", false)]
    public void AMultilineDefaultValue_AsksTheViewerToDrawIt(bool multiline, string value, bool needAppearances)
    {
        using var output = new MemoryStream();
        PdfFormFieldBuilder.AddFields(new MemoryStream(FormPdfFixtures.TextField()), output, new FormFieldBase[]
        {
            new FormTextField
            {
                FieldName = "notes", PageIndex = 0, X = 100, Y = 600, Width = 120, Height = 40,
                Multiline = multiline, DefaultValue = value,
            },
        });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var acroForm = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]));
        bool flagged = doc.ResolveObject(acroForm[PdfName.NeedAppearances]) is PdfBoolean b && b.Value;
        Assert.Equal(needAppearances, flagged);
    }
}
