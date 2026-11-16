// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Two pages with <c>/Resources</c> objects of their own that name one <c>/XObject</c> object: a
/// form flattened onto the second page takes a name the first page's form has not taken in the one
/// superseded copy both read, so each page's content invokes its own appearance. The names a
/// revision has registered are read through the <c>/XObject</c> object the resources name, not
/// through the <c>/Resources</c> number alone.
/// </summary>
public class StampedXObjectsSharedDictionaryTests
{
    [Fact]
    public void FlatteningOntoTwoPagesSharingAnXObjectDictionary_RegistersTwoNames()
    {
        using var output = new MemoryStream();
        var result = PdfAnnotationEditor.Flatten(new MemoryStream(Pdf()), output);
        Assert.Equal(2, result.Affected);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var first = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]);
        var second = (PdfDictionary)doc.ResolveObject(doc.Pages[1].Resources[PdfName.XObject]);

        // One dictionary, read by both pages, holding both appearances under two names.
        Assert.Same(first, second);
        var targets = first.Select(kv => ((PdfReference)kv.Value).ReferenceObjectNumber).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { 5, 7 }, targets);
    }

    /// <summary>
    /// Page 3 reads resources 10, page 4 resources 11; both name /XObject 20. Each page carries one
    /// Square annotation whose appearance (5, red; 7, blue) is flattened.
    /// </summary>
    private static byte[] Pdf() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R /Resources 10 0 R /Annots [12 0 R] >>")
        .Obj(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 9 0 R /Resources 11 0 R /Annots [13 0 R] >>")
        .StreamObj(5, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 rg 0 0 20 20 re f")
        .StreamObj(6, "<<", "")
        .StreamObj(7, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 0 1 rg 0 0 20 20 re f")
        .StreamObj(8, "<<", "")
        .StreamObj(9, "<<", "")
        .Obj(10, "<< /XObject 20 0 R >>")
        .Obj(11, "<< /XObject 20 0 R /ProcSet [/PDF] >>")
        .Obj(12, "<< /Type /Annot /Subtype /Square /Rect [0 0 20 20] /AP << /N 5 0 R >> >>")
        .Obj(13, "<< /Type /Annot /Subtype /Square /Rect [100 0 120 20] /AP << /N 7 0 R >> >>")
        .Obj(14, "null").Obj(15, "null").Obj(16, "null").Obj(17, "null").Obj(18, "null").Obj(19, "null")
        .Obj(20, "<< >>")
        .Finish();
}
