// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Rendering.Halftones;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

public partial class HalftoneTests
{
    [Fact]
    public void Tiff_Group4Strip_DecodesToTheBitsInAnIndependentRenderer()
    {
        var thresholds = new byte[] { 8, 136, 40, 168, 200, 72, 232, 104, 56, 184, 24, 152, 248, 120, 216, 88 };
        byte[] pdf = PageWithHalftone(64, GreyBands(64, 4, Enumerable.Range(0, 16).Select(i => i * 16 / 255.0)),
            Stream("/Type /Halftone /HalftoneType 6 /Width 4 /Height 4", thresholds));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        byte[] tiff = page.ToTiff();
        var tags = TiffPages(tiff).Single();
        byte[] strip = tiff.Skip((int)tags[273]).Take((int)tags[279]).ToArray();

        // The strip as a PDF image: CCITT Group 4 (K −1). The codes carry black and white runs themselves; the
        // filter's default output gives black 0, which DeviceGray draws black.
        var grey = IndependentImageRender.RenderGray(strip,
            "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns 64 /Rows 64 >>", 64, 64);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                Assert.True(page.IsInk(x, y) == (grey[y, x] < 128), $"pixel ({x}, {y})");
    }
}
