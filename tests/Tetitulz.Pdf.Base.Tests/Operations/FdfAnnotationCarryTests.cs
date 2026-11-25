// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An FDF annotation imported into a document keeps the plain entries the model does not name —
/// rich text, line endings — and loses what runs. Read from the document's
/// own annotation dictionary, as another reader would see it.
/// </summary>
public class FdfAnnotationCarryTests
{
    private const string Fdf =
        "%FDF-1.2\n" +
        "1 0 obj\n<< /FDF << /Annots [2 0 R] >> >>\nendobj\n" +
        "2 0 obj\n<< /Type /Annot /Subtype /Line /Page 0 /Rect [10 10 100 100] /L [10 10 100 100] " +
        "/RC (<p>rich</p>) /LE [/OpenArrow /None] /A << /S /JavaScript /JS (app.alert\\(1\\)) >> >>\nendobj\n" +
        "trailer\n<< /Root 1 0 R >>\n%%EOF\n";

    [Fact]
    public void AnImportedAnnotation_KeepsItsRichTextAndLineEndings_AndNotItsAction()
    {
        FormData data = PdfFormDataExchange.ReadFdf(new MemoryStream(Encoding.ASCII.GetBytes(Fdf)));
        Assert.Single(data.Annotations);

        using var output = new MemoryStream();
        int written = PdfFormDataExchange.ImportAnnotations(
            new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()), data, output);
        Assert.Equal(1, written);

        output.Position = 0;
        using var doc = PdfDocument.Open(output);
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        var annot = annots.Cast<PdfObject>().Select(o => (PdfDictionary)doc.ResolveObject(o))
                          .Single(d => ((PdfName)d[PdfName.Subtype]).Value == PdfAnnotationSubtypes.Line);

        Assert.Equal("<p>rich</p>", ((PdfString)doc.ResolveObject(annot[new PdfName("RC")])).GetText());
        var endings = (PdfArray)doc.ResolveObject(annot[PdfName.LE]);
        Assert.Equal("OpenArrow", ((PdfName)endings[0]).Value);
        Assert.Null(annot[PdfName.A]);
        // Line endings are the viewer's to draw: no generated appearance stands in for them.
        Assert.Null(annot[PdfName.AP]);
        Assert.NotNull(PdfAnnotationWriter.LastAppearanceSkipped);
    }
}
