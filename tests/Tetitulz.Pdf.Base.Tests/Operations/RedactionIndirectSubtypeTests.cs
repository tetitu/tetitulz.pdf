// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Redaction must not preserve an image whose <c>/Subtype</c> is an <b>indirect reference</b>.
/// </summary>
/// <remarks>
/// <para><b>The engine is deliberately fail-closed and this read defeated that.</b> A few lines
/// above the defect it <i>throws</i> — "Cannot resolve XObject overlapping a redaction region" —
/// rather than guess. But <c>/Subtype</c> was read with <c>xobj[PdfName.Subtype] as PdfName</c>, so
/// a legal <c>/Subtype 12 0 R</c> (§7.3.10, and the document's author chooses the spelling) came
/// back <see langword="null"/>. Null matches neither the <c>"Image"</c> branch nor the
/// <c>"Form"</c> branch, and the fall-through is <c>Verbatim(w, op)</c> — <b>the image is written
/// straight through, unredacted</b>.</para>
///
/// <para>Two neighbours had the same shape and the same direction: an unresolved <c>/Matrix</c>
/// mis-transforms a form's <c>/BBox</c> so the form may not be inspected at all, and an unresolved
/// annotation <c>/Rect</c> makes <c>intersects</c> false — which <i>keeps</i> the annotation. Every
/// one of them fails open, on the operation whose entire purpose is destroying content.</para>
///
/// <para><b>Asserted on the image stream's surviving bytes.</b> Redaction blanks the stream in
/// place (<c>StripStream</c>), so the check is that the pixel payload is gone from the output —
/// not that redaction returned, and not that the <c>Do</c> disappeared, since dropping the operator
/// while leaving the bytes reachable is the exact failure the engine's own comment says it is
/// avoiding.</para>
/// </remarks>
public class RedactionIndirectSubtypeTests
{
    /// <summary>A recognisable pixel payload; four bytes that will not occur by chance.</summary>
    private const string Payload = "SECRETPIXELS";

    /// <summary>
    /// The image's bytes are gone whichever way <c>/Subtype</c> is spelled. The direct spelling is
    /// the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Subtype 7 0 R
    [InlineData(false)]  // /Subtype /Image
    public void ImageOverlappingARedaction_IsBlanked_WhicheverWaySubtypeIsSpelled(bool indirect)
    {
        byte[] output = Redact(ImagePdf(indirect));

        Assert.DoesNotContain(Payload, Encoding.Latin1.GetString(output));
    }

    /// <summary>
    /// The floor: an image that does <b>not</b> overlap the redaction region keeps its bytes.
    /// Without this, the assertion above would also hold for an engine that blanked every image in
    /// the document.
    /// </summary>
    [Fact]
    public void ImageOutsideTheRedaction_KeepsItsBytes()
    {
        byte[] output = Redact(ImagePdf(indirect: false, overlapping: false));

        Assert.Contains(Payload, Encoding.Latin1.GetString(output));
    }

    // ------------------------------------------------------------------

    private static byte[] Redact(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactionEngine.Redact(input, output, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 0f, Y = 0f, Width = 100f, Height = 100f },
        });

        return output.ToArray();
    }

    /// <summary>
    /// A page drawing one image XObject. When <paramref name="overlapping"/> it sits inside the
    /// redaction region; otherwise it is placed well clear of it. Object 7 carries the subtype name
    /// when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] ImagePdf(bool indirect, bool overlapping = true)
    {
        string subtype = indirect ? "7 0 R" : "/Image";
        string placement = overlapping ? "20 0 0 20 10 10 cm" : "20 0 0 20 500 500 cm";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 4 0 R" +
            " /Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObj("", "q " + placement + " /Im0 Do Q\n"),
            StreamObj("/Type /XObject /Subtype " + subtype +
                      " /Width 4 /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8", Payload),
            "null",
            "/Image");
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
