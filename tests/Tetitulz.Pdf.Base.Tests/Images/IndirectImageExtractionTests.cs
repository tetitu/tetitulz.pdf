// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// Image extraction must not skip an image whose <c>/Subtype</c> is an <b>indirect reference</b>,
/// nor report it with zero dimensions.
/// </summary>
/// <remarks>
/// <para><b>The <c>/Subtype</c> read is the one that costs the most, because its failure is
/// silence.</b> A null subtype hits <c>continue</c>, so the image is not extracted badly — it is
/// not extracted <i>at all</i>, and the caller receives a shorter list with nothing said. A caller
/// pulling images out of a document has no way to know one is missing.</para>
///
/// <para><b>The file already knew indirect values happen.</b> Forty lines below the defect, the
/// <c>/ColorSpace</c> read carries an explicit <c>PdfReference</c> branch. The resolve was added
/// where somebody noticed it missing and not to its neighbours — the same pattern as <c>/Widths</c>
/// resolved one line after <c>/FirstChar</c> was not, and <c>/W</c> two lines after <c>/DW</c>.
/// </para>
///
/// <para><c>/Width</c> and <c>/Height</c> fall to <b>0</b>, so the reported
/// <see cref="PdfImageInfo"/> describes a 0×0 image; <c>/Filter</c> falls to null, which selects
/// the wrong output format and extension for the file written to disk.</para>
///
/// <para>Asserted on the extracted list — its length and the dimensions it reports — not on the
/// call returning, which is true in every one of those failures.</para>
/// </remarks>
public class IndirectImageExtractionTests
{
    /// <summary>
    /// The image is found and correctly described whichever way <c>/Subtype</c>, <c>/Width</c> and
    /// <c>/Height</c> are spelled. The direct spelling is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Subtype 7 0 R  /Width 8 0 R  /Height 9 0 R
    [InlineData(false)]  // /Subtype /Image /Width 4      /Height 3
    public void ImageIsExtractedAndMeasured_WhicheverWayItIsSpelled(bool indirect)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-img-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            using var ms = new MemoryStream(ImagePdf(indirect));
            List<PdfImageInfo> images = PdfImageExtractor.ExtractImages(ms, dir);

            var image = Assert.Single(images);
            Assert.Equal(4, image.Width);
            Assert.Equal(3, image.Height);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A page drawing one 4×3 grey image. Objects 7, 8 and 9 carry the subtype and the dimensions
    /// when <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] ImagePdf(bool indirect)
    {
        string subtype = indirect ? "7 0 R" : "/Image";
        string w = indirect ? "8 0 R" : "4";
        string h = indirect ? "9 0 R" : "3";

        // 4 x 3 pixels, 8 bits per component, DeviceGray = 12 bytes.
        string pixels = new string('A', 12);

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObj("", "q 40 0 0 30 10 10 cm /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype " + subtype + " /Width " + w + " /Height " + h +
                      " /ColorSpace /DeviceGray /BitsPerComponent 8", pixels),
            "null",
            "/Image",
            "4",
            "3");
    }

    private static string StreamObj(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
