// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// <b>Annotation scrubbing is not a widget-only rule.</b> <c>FilterAnnotations</c> drops every
/// annotation whose <c>/Rect</c> meets a redaction region — Stamp, FreeText and Ink included — and
/// the appearance keys are cleared on all of them. A Stamp annotation's <c>/AP /N</c> is drawn
/// content sitting inside the region the caller asked to have removed, so it is exactly what
/// redaction exists to destroy; there is nothing widget-specific about the leak.
///
/// <para>These tests use the <b>tagged-document</b> shape on purpose. Without a second route to the
/// annotation the reachable-objects-only rewrite drops the whole dictionary anyway, so the
/// assertion would pass with the key-clearing deleted outright. <c>/StructTreeRoot … /OBJR /Obj</c>
/// keeps the dictionary reachable, which is what makes "cleared" and "merely orphaned"
/// distinguishable in the output bytes.</para>
///
/// <para>The companion widget cases live in <c>RedactedWidgetFieldTests</c>; only the
/// <c>/AcroForm /Fields</c> detach there is genuinely widget-specific.</para>
/// </summary>
public class RedactedNonWidgetAnnotationTests
{
    private const string PublicWord = "PUBLICHEADER";
    private const string StampTitle = "STAMPTITLEMARKER";
    private const string SecretStampText = "SECRETSTAMPDRAWING";
    private const string KeptStampText = "KEEPTHISSTAMPDRAWING";

    [Fact]
    public void RedactRegions_ClearsStampAppearanceEvenWhenTheStructureTreeStillReachesIt()
    {
        byte[] pdf = BuildStampPdf();
        Assert.True(ContainsAscii(pdf, SecretStampText), "fixture must contain the secret to begin with");

        byte[] outBytes = Redact(pdf);

        // Both of these must hold for the third assertion to mean anything: the annotation
        // dictionary is still in the output, reached through the /OBJR, so a missing secret can
        // only be the cleared /AP and not a dropped object.
        Assert.True(ContainsAscii(outBytes, "/OBJR"),
            "fixture must keep the structure-tree route to the stamp in the output");
        Assert.True(ContainsAscii(outBytes, "100 652 300 674"),
            "the stamp annotation dictionary itself is expected to survive via /OBJR");

        Assert.False(ContainsAscii(outBytes, SecretStampText),
            "A dropped Stamp annotation's /AP draws content inside the redacted region; it must be " +
            "cleared, not merely orphaned.");
        Assert.False(ContainsAscii(outBytes, StampTitle),
            "A dropped markup annotation's /T names who wrote it, and goes with its words.");
    }

    [Fact]
    public void RedactRegions_KeepsTheAppearanceOfAStampNoRegionTouches()
    {
        // Scope guard: clearing must follow the /Rect intersection, not "every annotation".
        byte[] pdf = BuildStampPdf();

        byte[] outBytes = Redact(pdf);

        Assert.True(ContainsAscii(outBytes, KeptStampText),
            "a stamp outside every redaction region must keep its appearance stream");
        Assert.True(ContainsAscii(outBytes, PublicWord),
            "unrelated page text must survive (guards against an empty-output false pass)");
    }

    // ---- helpers ----

    private static byte[] Redact(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            // Covers the first stamp's /Rect [100 652 300 674] and nothing else.
            new RedactionRegion { PageIndex = 0, X = 96, Y = 650, Width = 210, Height = 26 },
        });
        return output.ToArray();
    }

    private static bool ContainsAscii(byte[] haystack, string ascii)
        => ContainsSequence(haystack, Encoding.ASCII.GetBytes(ascii));

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    /// <summary>
    /// A one-page tagged PDF carrying two <b>Stamp</b> annotations — no AcroForm anywhere, so
    /// nothing here can be reached by the widget/field path. The first sits at [100 652 300 674]
    /// (inside the redaction region) and its <c>/AP /N</c> draws <see cref="SecretStampText"/>; the
    /// second sits at [100 300 300 322] (outside every region) and draws
    /// <see cref="KeptStampText"/>. <c>/StructTreeRoot … /OBJR /Obj</c> points at the first.
    /// </summary>
    private static byte[] BuildStampPdf()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string pageContent = $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj ET";
        string secretAp = $"q BT /F1 12 Tf 2 6 Td ({SecretStampText}) Tj ET Q";
        string keptAp = $"q BT /F1 12 Tf 2 6 Td ({KeptStampText}) Tj ET Q";

        //  1 catalog, 2 pages, 3 page, 4 contents, 5 font, 6 redacted stamp, 7 its /AP /N,
        //  8 untouched stamp, 9 its /AP /N, 10 struct tree root, 11 struct element (/OBJR -> 6).
        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 10 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Annots [6 0 R 8 0 R] /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        Obj($"6 0 obj\n<< /Type /Annot /Subtype /Stamp /T ({StampTitle}) /Rect [100 652 300 674] " +
            "/P 3 0 R /F 4 /AP << /N 7 0 R >> >>\nendobj\n");
        Obj($"7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 22] " +
            $"/Resources << /Font << /F1 5 0 R >> >> /Length {secretAp.Length} >>\n" +
            $"stream\n{secretAp}\nendstream\nendobj\n");

        Obj("8 0 obj\n<< /Type /Annot /Subtype /Stamp /T (KeptStamp) /Rect [100 300 300 322] " +
            "/P 3 0 R /F 4 /AP << /N 9 0 R >> >>\nendobj\n");
        Obj($"9 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 22] " +
            $"/Resources << /Font << /F1 5 0 R >> >> /Length {keptAp.Length} >>\n" +
            $"stream\n{keptAp}\nendstream\nendobj\n");

        Obj("10 0 obj\n<< /Type /StructTreeRoot /K 11 0 R >>\nendobj\n");
        Obj("11 0 obj\n<< /Type /StructElem /S /Figure /P 10 0 R " +
            "/K [ << /Type /OBJR /Obj 6 0 R >> ] >>\nendobj\n");

        int total = offsets.Count + 1; // + free object 0
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", total);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", total);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
