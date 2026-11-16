// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Plugins.Html;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// Every CSS length the PDF→HTML export writes is formatted culture-invariantly.
///
/// <para><b>Why this exists.</b> The text-overlay spans were written with an interpolated string,
/// which formats through <see cref="CultureInfo.CurrentCulture"/>. On a comma-decimal machine
/// (de-DE, fr-FR, ru-RU, …) that produced <c>left:12,3px</c>; a browser's CSS parser discards a
/// declaration it cannot parse, so every span lost its absolute position and the entire selectable
/// text layer collapsed into the page's top-left corner. The output is still valid HTML and the
/// page image is still correct, so nothing about the failure is visible on an en-US developer
/// machine — and the sibling <c>ConvertToLayoutHtml</c> path already formatted invariantly, so the
/// codebase disagreed with itself.</para>
/// </summary>
public class HtmlCssCultureTests
{
    /// <summary>Cultures whose number format uses ',' as the decimal separator.</summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ConvertToHtml_UnderACommaDecimalCulture_WritesInvariantCssLengths(string cultureName)
    {
        List<string> styles = StyleAttributesUnder(cultureName);

        // The fixture has to actually reach the formatting code, or the assertion below is vacuous:
        // "F1" always emits one fractional digit, so an invariant run must show a '.' somewhere.
        Assert.NotEmpty(styles);
        Assert.Contains(styles, s => s.Contains('.'));

        foreach (string style in styles)
            Assert.DoesNotContain(',', style);
    }

    /// <summary>
    /// The control: the invariant culture produces the same lengths, so the assertion above cannot
    /// pass by the exporter having stopped writing lengths at all.
    /// </summary>
    [Fact]
    public void ConvertToHtml_UnderTheInvariantCulture_WritesTheSameLengths()
    {
        Assert.Equal(StyleAttributesUnder(CultureInfo.InvariantCulture.Name),
                     StyleAttributesUnder("de-DE"));
    }

    private static List<string> StyleAttributesUnder(string cultureName)
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            string html = PdfHtmlConverter.ConvertToHtml(
                new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("CultureMarker")),
                new HtmlConversionOptions { IncludeText = true, EmbedImages = false, ImageDpi = 96 });

            var styles = new List<string>();
            foreach (Match m in Regex.Matches(html, "<span style=\"([^\"]*)\""))
                styles.Add(m.Groups[1].Value);
            return styles;
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
