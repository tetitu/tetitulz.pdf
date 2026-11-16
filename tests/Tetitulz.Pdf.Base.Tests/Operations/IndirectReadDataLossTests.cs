// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Three <c>Base.Operations</c> writers that read a legally-indirect value without resolving, where
/// the value read is then <b>replaced</b> — so "absent" meant "deleted".
/// </summary>
/// <remarks>
/// <para>Each is the shape this repository keeps finding: the array or dictionary that was read is
/// rebuilt from what the read returned and written into a superseding revision, so a
/// <see langword="null"/> is not an omission but a deletion. Raised by the plan-15 sweep, verified
/// against the code before being touched.</para>
///
/// <para><b><c>PdfOcgManager</c>'s was the most visible tell:</b> <c>BuildOcgList</c> already
/// <i>took</i> the <c>PdfDocument</c> and never used it. Someone intended the resolve and left it
/// out, and the unused parameter sat there as evidence.</para>
///
/// <para>Every case pairs the indirect spelling with the direct one. The direct spelling is what
/// every other fixture in the suite uses and always worked, so a test built on it could never have
/// gone red.</para>
/// </remarks>
public class IndirectReadDataLossTests
{
    // ==================================================================
    //  PdfOcgManager — setting one layer must not reset the others
    // ==================================================================

    /// <summary>
    /// Hiding one layer leaves the other layer's recorded state alone. With <c>/OFF</c> read as
    /// absent the rebuilt list contained only the newly hidden layer, so every layer the document
    /// had already turned off came back on.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /OFF 8 0 R
    [InlineData(false)]  // /OFF [7 0 R]
    public void HidingALayer_KeepsTheOtherLayersAlreadyHidden(bool indirect)
    {
        using var input = new MemoryStream(TwoLayerPdf(indirect));
        using var output = new MemoryStream();

        int changed = PdfOcgManager.SetLayerVisibility(input, output, "Second", visible: false);
        Assert.Equal(1, changed);

        // Read the written /OFF list directly rather than through ListLayers, which derives its
        // answer from /ON and so cannot see this at all. Object 6 is "First", 7 is "Second".
        HashSet<int> off = OffList(output.ToArray());

        Assert.Contains(7, off);                                    // just hidden
        Assert.Contains(6, off);                                    // and the one already hidden
    }

    // ==================================================================
    //  PdfFormFieldBuilder — adding a field must not drop the others
    // ==================================================================

    /// <summary>
    /// Adding a text field keeps the field the form already had — the same shape as the four
    /// Signing sites closed in <c>aa17171</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /Fields 7 0 R
    [InlineData(false)]  // /Fields [6 0 R]
    public void AddingAField_KeepsTheExistingOne(bool indirect)
    {
        using var input = new MemoryStream(FormPdf(indirect));
        using var output = new MemoryStream();

        PdfFormFieldBuilder.AddTextField(input, output, new FormTextField
        {
            FieldName = "Added",
            PageIndex = 0,
            X = 10, Y = 10, Width = 50, Height = 20,
        });

        List<string> names = FieldNames(output.ToArray());
        Assert.Contains("Existing", names);
        Assert.Contains("Added", names);
    }

    // ==================================================================
    //  FormInvocationCounter — a shared form must not look uniquely drawn
    // ==================================================================

    /// <summary>
    /// A form XObject drawn by exactly one page is uniquely drawn, whichever way its
    /// <c>/Subtype</c> is written.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the assertion that discriminates, and the shared case is not.</b>
    /// <c>BuildUniquelyDrawnTest</c> seeds its memo with <c>false</c>, so an object with no recorded
    /// invocation sites answers "not uniquely drawn" — the same answer a genuinely shared form
    /// gives. An unresolved <c>/Subtype</c> skips the invocation entirely, so the shared case looked
    /// correct for the wrong reason and a test asserting it passed with the defect intact. The
    /// mutation is what showed that; the first version of this test asserted the shared case and
    /// stayed green.</para>
    ///
    /// <para><b>And the severity in the register is wrong for this row.</b> It is recorded as
    /// data-loss, but the failure direction is <i>conservative</i>: a form the walk never saw is
    /// reported non-unique, so redaction substitutes a clone rather than editing in place. That is
    /// wasteful, not damaging — the damaging direction would be a shared form reported unique, which
    /// this defect could not produce. Corrected in plan 15 rather than left to mislead.</para>
    /// </remarks>
    [Theory]
    [InlineData(true)]   // /Subtype 9 0 R
    [InlineData(false)]  // /Subtype /Form
    public void AFormDrawnByOnePage_IsUniquelyDrawn(bool indirect)
    {
        using var ms = new MemoryStream(SharedFormPdf(indirect, secondPageDrawsIt: false));
        using var doc = PdfDocument.Open(ms);

        Assert.True(FormInvocationCounter.BuildUniquelyDrawnTest(doc)(6),
            "a form drawn by exactly one page was not recognised as uniquely drawn");
    }

    /// <summary>
    /// The floor: a form drawn by <i>two</i> pages is not uniquely drawn, so the assertion above
    /// cannot be met by a predicate that always answers true.
    /// </summary>
    [Fact]
    public void AFormDrawnByTwoPages_IsNotUniquelyDrawn()
    {
        using var ms = new MemoryStream(SharedFormPdf(indirect: false));
        using var doc = PdfDocument.Open(ms);

        Assert.False(FormInvocationCounter.BuildUniquelyDrawnTest(doc)(6));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The object numbers in the written <c>/OCProperties /D /OFF</c> list.
    /// </summary>
    /// <remarks>
    /// Read straight from the dictionary rather than through <c>ListLayers</c>, whose
    /// <c>DefaultVisible</c> is derived from <c>/ON</c> and is therefore blind to a document that
    /// records its hidden layers in <c>/OFF</c> alone. Using it here would have made this test fail
    /// for a reason that has nothing to do with the defect — which is exactly what the first
    /// version did, in <b>both</b> the indirect case and the control.
    /// </remarks>
    private static HashSet<int> OffList(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var ocProps = doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("OCProperties")]) as PdfDictionary;
        Assert.NotNull(ocProps);
        var config = doc.ResolveObject(ocProps[PdfName.D]) as PdfDictionary;
        Assert.NotNull(config);
        var offArr = doc.ResolveObject(config[PdfName.OFF]) as PdfArray;
        Assert.NotNull(offArr);

        var set = new HashSet<int>();
        for (int i = 0; i < offArr.Count; i++)
            if (offArr[i] is PdfReference r) set.Add(r.ReferenceObjectNumber);
        return set;
    }

    private static List<string> FieldNames(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var names = new List<string>();
        var acro = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        Assert.NotNull(acro);
        var fields = doc.ResolveObject(acro[PdfName.Fields]) as PdfArray;
        Assert.NotNull(fields);
        for (int i = 0; i < fields.Count; i++)
            if (doc.ResolveObject(fields[i]) is PdfDictionary f &&
                doc.ResolveObject(f[PdfName.T]) is PdfString t)
                names.Add(t.GetText());
        return names;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Two layers, the first already in <c>/OFF</c>. Object 8 is that list.</summary>
    private static byte[] TwoLayerPdf(bool indirect) => Build(
        "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R 7 0 R]" +
        " /D << /OFF " + (indirect ? "8 0 R" : "[6 0 R]") + " >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
        StreamObj("q Q\n"),
        "null",
        "<< /Type /OCG /Name (First) >>",
        "<< /Type /OCG /Name (Second) >>",
        "[6 0 R]");

    /// <summary>A form holding one text field; object 7 is the <c>/Fields</c> array.</summary>
    private static byte[] FormPdf(bool indirect) => Build(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields " +
        (indirect ? "7 0 R" : "[6 0 R]") + " >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
        StreamObj("q Q\n"),
        "null",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Existing) /Rect [0 0 10 10] >>",
        "[6 0 R]");

    /// <summary>
    /// Two pages, both drawing form XObject 6 — unless <paramref name="secondPageDrawsIt"/> is
    /// false. Object 9 is the <c>/Subtype</c> name, referenced when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] SharedFormPdf(bool indirect, bool secondPageDrawsIt = true)
    {
        string subtype = indirect ? "9 0 R" : "/Form";
        string page2Content = secondPageDrawsIt ? "/Fm0 Do\n" : "q Q\n";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 7 0 R" +
            " /Resources << /XObject << /Fm0 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 8 0 R" +
            " /Resources << /XObject << /Fm0 6 0 R >> >> >>",
            "null",
            StreamObj2("/Type /XObject /Subtype " + subtype + " /BBox [0 0 10 10]", "q Q\n"),
            StreamObj("/Fm0 Do\n"),
            StreamObj(page2Content),
            "/Form");
    }

    private static string StreamObj(string data) => StreamObj2("", data);

    private static string StreamObj2(string dictEntries, string data) =>
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
