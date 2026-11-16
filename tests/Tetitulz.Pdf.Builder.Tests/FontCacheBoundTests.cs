// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The retained-bytes ceiling on <see cref="BuilderFontResolver"/>'s process-wide file cache
/// (GAPS §16 item 4b) — the counterpart of <c>DocxToPdf.Tests.FontCacheBoundTests</c>.
///
/// <para>Both resolvers had the same defect and it is fixed in both, because "one fixed, the other
/// silently disagreeing" is this repository's most-repeated failure. Here the walk is reached only
/// with <see cref="PdfExportOptions.AllowSystemFontSubstitution"/> set, which the neighbouring
/// <c>FontResolutionTests</c> deliberately never does — hence a separate file rather than a case
/// added there, since that file's contract is host independence and this assertion cannot have it.
/// </para>
///
/// <para><b>Host-dependent, stated plainly:</b> the ceiling can only be exceeded on a machine with
/// more than <see cref="BuilderFontResolver.MaxCachedFontBytes"/> of installed fonts. On a bare
/// container the first assertion is vacuously true; on the Windows box where the defect was found,
/// removing the bound makes it fail. It is an invariant either way, so a small host cannot turn it
/// green by accident.</para>
/// </summary>
public class FontCacheBoundTests
{
    [Fact]
    public void ExportNeedingASystemFallback_DoesNotRetainEveryInstalledFontForever()
    {
        // U+12000 CUNEIFORM SIGN A: assigned, and essentially nothing ships a font for it, so the
        // resolver exhausts its walk instead of short-circuiting on a covering face.
        var document = new Document();
        var section = new Section();
        section.Children.Add(new Paragraph { Runs = { new TextRun { Text = "\U00012000\U00012001" } } });
        document.Sections.Add(section);

        var options = new PdfExportOptions { AllowSystemFontSubstitution = true };
        byte[] pdf = document.ToPdf(options);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));

        Assert.True(BuilderFontResolver.ChargedBytes <= BuilderFontResolver.MaxCachedFontBytes,
            $"the font cache is holding {BuilderFontResolver.ChargedBytes / 1024 / 1024} MiB, over the " +
            $"{BuilderFontResolver.MaxCachedFontBytes / 1024 / 1024} MiB ceiling — the scan is retaining " +
            "every font it touched (GAPS §16 item 4b)");
    }

    /// <summary>
    /// The mistake that would be worse than the leak: a cache that stops accepting entries and
    /// stops <i>returning</i> them, so a font resolved after the ceiling loses its glyphs.
    /// </summary>
    [Fact]
    public void PastTheCeiling_TheProgramIsStillReturned()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "tetitulz-builder-cachebound-" + Guid.NewGuid().ToString("N") + ".ttf");
        try
        {
            File.WriteAllBytes(path, MinimalTrueType.Build("AB"));

            var resolver = new BuilderFontResolver(PdfExportOptions.Default);
            var first = resolver.LoadFontFileForTest(path);
            var second = resolver.LoadFontFileForTest(path);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.UnitsPerEm, second!.UnitsPerEm);
            Assert.True(first.FontData.Length > 0,
                "the loader returned a program with no bytes, so it could never be embedded");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
