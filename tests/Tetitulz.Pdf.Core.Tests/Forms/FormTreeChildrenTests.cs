// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <c>FormTreeChildren.Resolve</c> — the one reading of a form-tree child array, for the form's
/// <c>/Fields</c> (Table 218) and a field's <c>/Kids</c> (Table 220).
/// </summary>
/// <remarks>
/// <para><b>Why these exist, and they are not a formality.</b> The typed getters do not follow an
/// indirect reference, so read through them <c>/Fields 11 0 R</c> would be a form with <b>no
/// fields</b> — hiding every signature from <c>GetSignatureFields</c> — and <c>/Kids 11 0 R</c>
/// would make a group look <b>terminal</b>, so a radio fill found no <c>/AP</c> on-states and a
/// signature group's <c>/FT</c> never resolved.</para>
///
/// <para><b>The rest of the Forms suite covers the <i>element</i>-level resolve</b>
/// (<c>/Fields [12 0 R]</c>, the ordinary spelling) and not the array itself, so a mutation of the
/// array-level resolve leaves it green. These are the fixture for the array, one per arrival.</para>
///
/// <para>The third pins the direct fallbacks, which only <see cref="PdfFormField"/> can reach:
/// its constructor is public and takes the document as a plain parameter, while
/// <see cref="PdfAcroForm"/>'s rejects a null one.</para>
/// </remarks>
public sealed class FormTreeChildrenTests
{
    [Fact]
    public void AnIndirectFieldsArray_StillYieldsTheFormsFields()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirectFields: true, indirectKids: false)));

        var fields = doc.AcroForm.Fields;

        Assert.Equal("Group", Assert.Single(fields).FieldName);
    }

    /// <summary>
    /// The acceptance control: the identical document with a direct <c>/Fields</c>. Without it the
    /// test above would pass on a fixture whose field list is empty for its own reasons.
    /// </summary>
    [Fact]
    public void ADirectFieldsArray_YieldsTheSameOneField()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirectFields: false, indirectKids: false)));

        Assert.Equal("Group", Assert.Single(doc.AcroForm.Fields).FieldName);
    }

    /// <summary>
    /// An indirect <c>/Kids</c> array: the group's two widgets must still be reachable, and
    /// <c>WidgetDictionaries</c> must hand back <b>those two</b> rather than the group's own
    /// dictionary.
    /// </summary>
    /// <remarks>
    /// <para><b>That substitution is the recorded symptom, and it is what makes this assertion the
    /// right one.</b> With the array unresolved the group has no kids, so
    /// <c>WidgetDictionaries</c> falls back to the group itself — a single dictionary with no
    /// <c>/AP</c> on-states, which is why a radio-button fill was silently refused.</para>
    /// <para><b>`IsTerminal` is deliberately not asserted here.</b> §12.7.3.1 makes a field whose
    /// kids are widget annotations a <i>terminal</i> field, so this group is terminal either way and
    /// the property cannot see the difference — which is exactly CLAUDE.md's warning that
    /// <c>IsTerminal</c> alone is not the question callers think it is.</para>
    /// </remarks>
    [Fact]
    public void AnIndirectKidsArray_StillYieldsTheGroupsTwoWidgets_NotTheGroupItself()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirectFields: false, indirectKids: true)));

        PdfFormField group = doc.AcroForm.Fields.Single();

        Assert.Equal(2, group.Kids.Count);
        Assert.Equal(2, group.WidgetDictionaries.Count);
        Assert.DoesNotContain(group.Dictionary, group.WidgetDictionaries);
    }

    /// <summary>The <c>/Kids</c> control, on the same document with the array written directly.</summary>
    [Fact]
    public void ADirectKidsArray_YieldsTheSameTwoWidgets()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirectFields: false, indirectKids: false)));

        PdfFormField group = doc.AcroForm.Fields.Single();

        Assert.Equal(2, group.Kids.Count);
        Assert.Equal(2, group.WidgetDictionaries.Count);
    }

    /// <summary>
    /// A <see cref="PdfFormField"/> constructed with <b>no document</b> over a hand-built graph:
    /// there is nothing to resolve through, so the directly-written array and its directly-written
    /// elements must still be read.
    /// </summary>
    /// <remarks>
    /// This is a shape callers can build — the constructor is public and the document is a plain
    /// parameter — and it is the only route to the two direct fallbacks in the shared helper. Before
    /// this the kid read dereferenced the document unguarded, so a field <i>with</i> kids threw
    /// <c>NullReferenceException</c> while a field without kids, and every other property, worked.
    /// </remarks>
    [Fact]
    public void AFieldWithNoDocument_StillReadsADirectKidsArray()
    {
        var kidA = new PdfDictionary();
        kidA.Set(PdfName.T, new PdfString("a"));
        var kidB = new PdfDictionary();
        kidB.Set(PdfName.T, new PdfString("b"));

        var kids = new PdfArray();
        kids.Add(kidA);
        kids.Add(kidB);

        var parent = new PdfDictionary();
        parent.Set(PdfName.T, new PdfString("Group"));
        parent.Set(PdfName.FT, new PdfName("Btn"));
        parent.Set(PdfName.Kids, kids);

        var field = new PdfFormField(parent, null, null);

        Assert.Equal(2, field.Kids.Count);
        Assert.Equal(new[] { "a", "b" }, field.Kids.Select(k => k.FieldName));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// One button group with two widget kids. <paramref name="indirectFields"/> puts the
    /// <c>/Fields</c> <b>array</b> in object 8; <paramref name="indirectKids"/> puts the
    /// <c>/Kids</c> <b>array</b> in object 9. The array, not its elements — the elements are always
    /// references here, which is the spelling the suite already covered.
    /// </summary>
    private static byte[] Pdf(bool indirectFields, bool indirectKids)
    {
        string fields = indirectFields ? "8 0 R" : "[5 0 R]";
        string kids   = indirectKids   ? "9 0 R" : "[6 0 R 7 0 R]";

        return Build(
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields {fields} >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
              + "/Annots [6 0 R 7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            $"<< /FT /Btn /T (Group) /Kids {kids} >>",
            "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [0 0 10 10] /AS /Off "
              + "/AP << /N << /On << >> /Off << >> >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [20 0 30 10] /AS /Off "
              + "/AP << /N << /Yes << >> /Off << >> >> >> >>",
            "[5 0 R]",
            "[6 0 R 7 0 R]");
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
