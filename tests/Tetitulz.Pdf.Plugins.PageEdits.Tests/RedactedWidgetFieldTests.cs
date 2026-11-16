// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A redacted <b>widget annotation</b> is reachable from two places, not one: the page's
/// <c>/Annots</c> and the catalog's <c>/AcroForm /Fields</c> (directly for a merged
/// widget/field, or through a parent field's <c>/Kids</c>). Dropping it from <c>/Annots</c>
/// alone leaves the field dictionary — its <c>/V</c> value and its <c>/AP /N</c> appearance
/// stream — fully reachable from the catalog, so the "reachable objects only" rewrite emits
/// it and the supposedly-destroyed value is still in the output bytes.
///
/// <para>These tests assert on the OUTPUT BYTES and on <see cref="PdfFormReader.GetFields"/>,
/// because the value never was extractable as page text: it lives in a dictionary, so a
/// text-extraction assertion passes while the leak is wide open.</para>
/// </summary>
public class RedactedWidgetFieldTests
{
    private const string PublicWord = "PUBLICHEADER";
    private const string SecretValue = "SECRETSSN12345";

    /// <summary>
    /// §12.7.4.3's rich-text value — <b>the same content in a second spelling</b>. A form filler
    /// writes it beside <c>/V</c>, so a redaction that clears one and not the other leaves the
    /// secret in the file in full. Deliberately free of parentheses and backslashes: this is a PDF
    /// literal string, and an escaped payload could not be found in the output whether it was
    /// removed or not.
    /// </summary>
    private const string SecretRichText = "SECRETRICHTEXT54321";
    private const string KeptValue = "KEEPTHISVALUE99";

    [Fact]
    public void RedactRegions_RemovesMergedWidgetFieldValueAndAppearanceFromOutputBytes()
    {
        byte[] pdf = BuildFormPdf(valueOnParentField: false, withSecondField: false);
        Assert.True(ContainsAscii(pdf, SecretValue), "fixture must contain the secret to begin with");

        byte[] outBytes = Redact(pdf);

        Assert.False(ContainsAscii(outBytes, SecretValue),
            "The redacted widget's /V and /AP content must not survive anywhere in the output bytes.");
        Assert.True(ContainsAscii(outBytes, PublicWord),
            "unrelated page text must survive (guards against an empty-output false pass)");

        using var read = new MemoryStream(outBytes);
        var fields = PdfFormReader.GetFields(read);
        Assert.DoesNotContain(fields, f => f.Value == SecretValue);
    }

    [Fact]
    public void RedactRegions_RemovesParentFieldValueWhenItsOnlyWidgetIsRedacted()
    {
        // /V lives on the parent field node; the widget is its only kid. Detaching the widget
        // alone would leave the parent - value included - reachable from /AcroForm /Fields.
        byte[] pdf = BuildFormPdf(valueOnParentField: true, withSecondField: false);
        Assert.True(ContainsAscii(pdf, SecretValue), "fixture must contain the secret to begin with");

        byte[] outBytes = Redact(pdf);

        Assert.False(ContainsAscii(outBytes, SecretValue),
            "A parent field whose every widget was redacted must not keep its inherited /V.");
        Assert.True(ContainsAscii(outBytes, PublicWord));
    }

    [Fact]
    public void RedactRegions_KeepsFormFieldsThatNoRegionTouches()
    {
        // Scope check: the prune must remove the redacted field only, not the whole AcroForm.
        byte[] pdf = BuildFormPdf(valueOnParentField: false, withSecondField: true);

        byte[] outBytes = Redact(pdf);

        Assert.False(ContainsAscii(outBytes, SecretValue));
        Assert.True(ContainsAscii(outBytes, KeptValue),
            "a field outside every redaction region must survive intact");

        using var read = new MemoryStream(outBytes);
        var fields = PdfFormReader.GetFields(read);
        Assert.Contains(fields, f => f.Value == KeptValue);
        Assert.DoesNotContain(fields, f => f.Value == SecretValue);
    }

    /// <summary>
    /// In a tagged document the structure tree reaches the widget a third way
    /// (<c>/StructTreeRoot … /OBJR /Obj</c>), so pruning the AcroForm field tree does NOT make the
    /// dictionary unreachable and the rewrite still emits it. Clearing <c>/V</c> and <c>/AP</c> on
    /// the dropped annotation is what removes the data in that case.
    /// </summary>
    [Fact]
    public void RedactRegions_ClearsWidgetValueEvenWhenTheStructureTreeStillReachesIt()
    {
        byte[] pdf = BuildFormPdf(valueOnParentField: false, withSecondField: false,
                                  withStructTree: true);

        byte[] outBytes = Redact(pdf);

        // The widget dictionary itself is still in the output - reached through the /OBJR - so this
        // asserts the keys were cleared, not that the object went away.
        Assert.True(ContainsAscii(outBytes, "/OBJR"),
            "fixture must keep the structure-tree route to the widget in the output");
        Assert.True(ContainsAscii(outBytes, "(SSN)"),
            "the widget dictionary itself is expected to survive via /OBJR");
        Assert.False(ContainsAscii(outBytes, SecretValue),
            "The redacted widget's /V and /AP must be cleared, not merely orphaned.");
    }

    /// <summary>
    /// <b>The rich-text value goes too.</b> §12.7.4.3's <c>/RV</c> holds what <c>/V</c> holds, as
    /// XHTML — so clearing <c>/V</c> and <c>/AP</c> while leaving <c>/RV</c> leaves the secret in
    /// the output in full, in a key a form filler writes routinely.
    /// </summary>
    /// <remarks>
    /// Its two siblings were already covered and this one was not: mutating each of the three
    /// removals out in turn reddened tests for <c>/V</c> and for <c>/AP</c>, and nothing at all for
    /// <c>/RV</c> — in either <c>Base.Tests</c> or this suite.
    /// </remarks>
    [Fact]
    public void RedactRegions_ClearsTheWidgetsRichTextValueAsWellAsItsValue()
    {
        byte[] pdf = BuildFormPdf(valueOnParentField: false, withSecondField: false,
                                  withStructTree: true);
        Assert.True(ContainsAscii(pdf, SecretRichText), "fixture must carry the rich-text value");

        byte[] outBytes = Redact(pdf);

        Assert.False(ContainsAscii(outBytes, SecretRichText),
            "The redacted widget's /RV holds the same secret as its /V and must be cleared too.");
        // The control: the widget dictionary survives via /OBJR, so this is the key being cleared
        // rather than the whole object going away.
        Assert.True(ContainsAscii(outBytes, "(SSN)"),
            "the widget dictionary itself is expected to survive via /OBJR");
    }

    /// <summary>
    /// Adversarial input: the field node lists <i>itself</i> in <c>/Kids</c> alongside the widget.
    /// The walk must terminate and must still conclude that every real child was redacted — a
    /// cycle guard that merely kept the back edge would leave the node looking as though it still
    /// had a child, and its <c>/V</c> would outlive the widget that displayed it.
    /// </summary>
    [Fact]
    public void RedactRegions_FieldKidsCycle_TerminatesAndStillRemovesTheValue()
    {
        byte[] pdf = BuildFormPdf(valueOnParentField: true, withSecondField: false,
                                  cyclicKids: true);
        Assert.True(ContainsAscii(pdf, SecretValue), "fixture must contain the secret to begin with");

        byte[] outBytes = Redact(pdf);

        Assert.True(ContainsAscii(outBytes, PublicWord),
            "the walk must have completed and produced the document");
        Assert.False(ContainsAscii(outBytes, SecretValue),
            "A self-referential /Kids entry must not preserve the redacted field's value.");
    }

    // ---- helpers ----

    private static byte[] Redact(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            // Covers the SSN widget's /Rect [100 652 300 674] and nothing else.
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
    /// A one-page PDF whose page text is PUBLICHEADER (y=700) and which carries a filled text
    /// field at [100 652 300 674] holding <see cref="SecretValue"/>, with an /AP /N appearance
    /// stream that draws the same string.
    /// </summary>
    /// <param name="valueOnParentField">
    /// False: merged widget/field (one dictionary, in /Fields directly).
    /// True: a parent field node holding /T and /V with the widget as its single /Kids entry.
    /// </param>
    /// <param name="withSecondField">
    /// Adds a second, merged field at y=300 holding <see cref="KeptValue"/>, outside every
    /// redaction region.
    /// </param>
    /// <param name="withStructTree">
    /// Points a tagged document's <c>/StructTreeRoot … /OBJR /Obj</c> at the redacted widget, so
    /// the widget dictionary stays reachable from the catalog after the field tree is pruned.
    /// </param>
    /// <param name="cyclicKids">
    /// Adds the parent field to its own <c>/Kids</c> — a back edge no valid field tree has.
    /// </param>
    private static byte[] BuildFormPdf(bool valueOnParentField, bool withSecondField,
                                       bool withStructTree = false, bool cyclicKids = false)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string pageContent = $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj ET";
        string apContent = $"/Tx BMC q BT /F1 12 Tf 2 6 Td ({SecretValue}) Tj ET Q EMC";
        string keptAp = $"/Tx BMC q BT /F1 12 Tf 2 6 Td ({KeptValue}) Tj ET Q EMC";

        // Object numbering is the same in every variant — objects 1..12 are always written, and
        // the /Fields, /Annots and /StructTreeRoot entries decide which of them are referenced.
        // (An unreferenced object is dropped by the reachable-only rewrite, so an unused one
        // cannot make an assertion pass by accident.)
        //  1 catalog, 2 pages, 3 page, 4 contents, 5 font, 6 widget, 7 widget /AP /N,
        //  8 parent field, 9/10 second widget + its /AP /N, 11/12 struct tree root + element.
        string topLevelField = valueOnParentField ? "8 0 R" : "6 0 R";
        string fields = withSecondField ? $"{topLevelField} 9 0 R" : topLevelField;
        string annots = withSecondField ? "6 0 R 9 0 R" : "6 0 R";
        string structRoot = withStructTree ? " /StructTreeRoot 11 0 R" : "";

        sb.Append("%PDF-1.4\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{fields}] >>{structRoot} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            $"/Annots [{annots}] /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // 6: the widget. Merged with the field unless the value sits on the parent.
        Obj(valueOnParentField
            ? "6 0 obj\n<< /Type /Annot /Subtype /Widget /Parent 8 0 R /Rect [100 652 300 674] " +
              "/P 3 0 R /F 4 /AP << /N 7 0 R >> >>\nendobj\n"
            : $"6 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (SSN) /V ({SecretValue}) " +
              $"/RV ({SecretRichText}) " +
              "/Rect [100 652 300 674] /P 3 0 R /F 4 /AP << /N 7 0 R >> >>\nendobj\n");
        Obj($"7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 22] " +
            $"/Resources << /Font << /F1 5 0 R >> >> /Length {apContent.Length} >>\n" +
            $"stream\n{apContent}\nendstream\nendobj\n");

        // Object 8 is always emitted so the numbering below is the same in every variant.
        string parentKids = cyclicKids ? "6 0 R 8 0 R" : "6 0 R";   // 8 0 R: the malformed back edge
        Obj(valueOnParentField
            ? $"8 0 obj\n<< /FT /Tx /T (SSN) /V ({SecretValue}) /Kids [{parentKids}] >>\nendobj\n"
            : "8 0 obj\n<< /Unused true >>\nendobj\n");

        Obj($"9 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (Keep) /V ({KeptValue}) " +
            "/Rect [100 300 300 322] /P 3 0 R /F 4 /AP << /N 10 0 R >> >>\nendobj\n");
        Obj($"10 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 22] " +
            $"/Resources << /Font << /F1 5 0 R >> >> /Length {keptAp.Length} >>\n" +
            $"stream\n{keptAp}\nendstream\nendobj\n");

        // The tagged-document path to the same widget: /StructTreeRoot -> /StructElem -> /OBJR.
        Obj("11 0 obj\n<< /Type /StructTreeRoot /K 12 0 R >>\nendobj\n");
        Obj("12 0 obj\n<< /Type /StructElem /S /Form /P 11 0 R " +
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
