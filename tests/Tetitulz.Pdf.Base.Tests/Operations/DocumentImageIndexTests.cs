// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The form-cycle guard of <c>DocumentImageIndex</c>, the image walk <c>PdfImageReplacer</c> and
/// <c>PdfColorSpaceConverter</c> share.
/// </summary>
/// <remarks>
/// <b>The guard survived the sweep, and the reason is the lesson.</b> Without it a form that lists
/// itself is walked again at every level down to <c>MaxFormXObjectDepth</c> — and its image is still
/// found exactly once, because the index is keyed by object number. The answer does not change;
/// only the work does. So the test asserts the work (<c>FormsVisited</c>), the way a DoS ceiling is
/// tested here, and a second fixture shows what the work would have been: a form listing itself
/// twice doubles per level, 2^32 visits at the default cap.
/// </remarks>
public class DocumentImageIndexTests
{
    [Fact]
    public void ASelfReferencingFormIsDescendedIntoOnce()
    {
        using var doc = PdfDocument.Open(new MemoryStream(
            ImageFixtures.SelfReferencingFormPdf(ImageFixtures.Red)));

        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(1, index.FormsVisited);
        Assert.NotNull(index.Find(5));
    }

    /// <summary>
    /// Hostile input: a form whose resources name itself twice. Unguarded, each level doubles the
    /// walk, so this does not return; guarded, the form is entered once.
    /// </summary>
    [Fact]
    public void AFormListingItselfTwice_HostileBranchingCycle_IsEnteredOnce()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Fx 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "/Fx Do")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /XObject << /A 6 0 R /B 6 0 R /Logo 5 0 R >> >>",
                       "/Logo Do")
            .Finish();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(1, index.FormsVisited);
        Assert.NotNull(index.Find(5));
    }

    /// <summary>The control: two distinct nested forms are two visits, so the probe counts.</summary>
    [Fact]
    public void TwoDistinctNestedFormsAreTwoVisits()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Outer 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "/Outer Do")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /XObject << /Inner 7 0 R >> >>", "/Inner Do")
            .StreamObj(7, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /XObject << /Logo 5 0 R >> >>", "/Logo Do")
            .Finish();

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var index = DocumentImageIndex.Build(doc);

        Assert.Equal(2, index.FormsVisited);
        Assert.NotNull(index.Find(5));
    }
}
