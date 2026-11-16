// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Document;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The document writer's promotion cap follows the collector's own ceilings
/// (<see cref="PsLimits.MaxImageXObjects"/>, <see cref="PsLimits.MaxFontResources"/>), not
/// <see cref="PdfLimits.MaxPluginResourceEntries"/>, which bounds the direct graph a plugin hands a
/// host for one fragment: under that cap a job that drew more images than it had lost every image
/// past it as a null in <c>/XObject</c>, and said so.
/// </summary>
[Collection(PsLimitsCollection.Name)]
public sealed class DocumentPromotionCapTests
{
    [Fact]
    public void ImagesPastTheFragmentCap_AreWritten_AsTheCollectorAdmittedThem()
    {
        const int Images = 8;
        var ps = new StringBuilder();
        for (int i = 0; i < Images; i++)
            ps.Append("10 0 translate 1 1 8 [1 0 0 1 0 0] <").Append((0x80 + i).ToString("X2")).Append("> image\n");
        ps.Append("showpage\n");

        try
        {
            PdfLimits.MaxPluginResourceEntries = 4;
            PsLimits.MaxImageXObjects = Images;

            using var source = new MemoryStream(Encoding.ASCII.GetBytes(ps.ToString()));
            using var output = new MemoryStream();
            var result = PostScriptConversion.ConvertDocument(source, output);
            Assert.DoesNotContain("promotion cap", result.LastError ?? "");

            using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
            var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]));
            Assert.Equal(Images, xobjects.Count);
            foreach (var entry in xobjects)
                Assert.IsType<PdfStream>(doc.ResolveObject(entry.Value));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
            PsLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// The cap admits every stream the collector can: each image, and each font's program with its
    /// up to 256 Type 3 glyph procedures — under the print host's sizing as under the default.
    /// </summary>
    [Theory]
    [InlineData(PsLimits.DefaultMaxImageXObjects, PsLimits.DefaultMaxFontResources)]
    [InlineData(65_536, 4096)]
    public void TheDocumentCap_AdmitsWhatTheCollectorAdmits(int images, int fonts)
    {
        try
        {
            PsLimits.MaxImageXObjects = images;
            PsLimits.MaxFontResources = fonts;

            Assert.True(ResourcePromoter.DocumentCap() >= (long)images + 257L * fonts);
        }
        finally { PsLimits.ResetToDefaults(); }
    }
}
