// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The coordinates of an <c>/XYZ</c> destination reach the file as numbers, and an unset one as
/// <c>null</c> — "keep the viewer's current value", which is not 0 (§12.3.2.2). Read raw: every reader
/// here turns both into the same "no value".
/// </summary>
public class DestinationCoordinatesTests
{
    [Fact]
    public void ANamedDestinationCarriesItsLeftTopAndZoom()
    {
        PdfArray dest = NamedDest(Set(left: 36, top: 700, zoom: 1.5));
        Assert.Equal(new double?[] { 36, 700, 1.5 }, Coordinates(dest));

        PdfArray unset = NamedDest(Set(left: null, top: null, zoom: null));
        Assert.Equal(new double?[] { null, null, null }, Coordinates(unset));
        Assert.All(new[] { unset[2], unset[3], unset[4] }, o => Assert.IsType<PdfNull>(o));
    }

    [Fact]
    public void TheOpeningViewCarriesItsLeftTopAndZoom()
    {
        using var output = new MemoryStream();
        PdfInitialView.SetInitialView(new MemoryStream(OnePage()), output, new InitialViewSettings
        {
            OpenAtPageIndex = 0, OpenAtLeft = 36, OpenAtTop = 700, OpenAtZoom = 1.5,
        });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var dest = (PdfArray)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OpenAction])!;
        Assert.Equal(new double?[] { 36, 700, 1.5 }, Coordinates(dest));
    }

    // ------------------------------------------------------------------ helpers

    private static double?[] Coordinates(PdfArray dest)
        => new[] { (dest[2] as PdfNumber)?.DoubleValue, (dest[3] as PdfNumber)?.DoubleValue, (dest[4] as PdfNumber)?.DoubleValue };

    private static byte[] Set(double? left, double? top, double? zoom)
    {
        using var output = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(OnePage()), output, "here", 0, left, top, zoom);
        return output.ToArray();
    }

    private static PdfArray NamedDest(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names])!;
        var leaf = (PdfDictionary)doc.ResolveObject(names[PdfName.Dests])!;
        var entries = (PdfArray)doc.ResolveObject(leaf[PdfName.Names])!;
        return (PdfArray)doc.ResolveObject(entries[1])!;
    }

    private static byte[] OnePage() => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
}
