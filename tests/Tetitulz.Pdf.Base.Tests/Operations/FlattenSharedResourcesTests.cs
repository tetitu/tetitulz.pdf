// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Both flatteners register a page's stamped forms where the page reads its XObjects
/// (<c>StampedXObjects</c>): pages inheriting one <c>/Resources</c> share one superseded copy of
/// it, where each was given a direct copy of the whole — 800 pages over 800 XObjects wrote 10 MB
/// from 371 KB. A page whose resources are written in its own dictionary still gets its own copy.
/// </summary>
public class FlattenSharedResourcesTests
{
    private const int Pages = 40, Entries = 40;

    public static IEnumerable<object[]> Shapes()
    {
        foreach (bool annotations in new[] { false, true })
            foreach (bool namedXObjects in new[] { false, true })
                foreach (bool resourcesOnPage in new[] { false, true })
                    yield return new object[] { annotations, namedXObjects, resourcesOnPage };
    }

    /// <summary>
    /// The shared dictionary is written once — one copy of its first image's name in the revision —
    /// and every page draws, through it, a form of its own; a page naming the dictionary still names
    /// it, and a page inheriting it still inherits it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void AdversarialPagesSharingResources_AreFlattenedIntoOneSharedDictionary(
        bool annotations, bool namedXObjects, bool resourcesOnPage)
    {
        byte[] input = Document(Pages, Entries, annotations, namedXObjects, directResources: false, resourcesOnPage);

        byte[] output = Flatten(input, annotations);

        string appended = Encoding.Latin1.GetString(output, input.Length, output.Length - input.Length);
        Assert.Single(Regex.Matches(appended, @"/Im0 "));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < Pages; p++)
        {
            var page = doc.Pages[p];
            if (resourcesOnPage) Assert.Equal(3, Assert.IsType<PdfReference>(page.Dictionary[PdfName.Resources]).ReferenceObjectNumber);
            else Assert.Null(page.Dictionary[PdfName.Resources]);
            string name = LastDrawnName(page);
            var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(page.Resources[PdfName.XObject]));
            Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(xobjects[new PdfName(name)]));
            Assert.NotNull(xobjects[new PdfName("Im" + (Entries - 1))]);
            drawn.Add(name);
        }
        Assert.Equal(Pages, drawn.Count);
    }

    /// <summary>A page whose <c>/Resources</c> is written in its own dictionary is given its own copy, its images kept.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APageWithItsOwnDirectResources_GetsItsOwnCopy(bool annotations)
    {
        byte[] input = Document(1, 3, annotations, namedXObjects: false, directResources: true);

        byte[] output = Flatten(input, annotations);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var page = doc.Pages[0];
        var own = Assert.IsType<PdfDictionary>(page.Dictionary[PdfName.Resources]);
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(own[PdfName.XObject]));
        Assert.NotNull(xobjects[new PdfName("Im2")]);
        Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(xobjects[new PdfName(LastDrawnName(page))]));
    }

    /// <summary>
    /// A stamped form is never registered under a name the page already uses: with every
    /// <c>FlatXO</c> name a flattener could mint already naming an image, the form the page draws is
    /// the widget's form and every one of those names still names the image.
    /// </summary>
    [Fact]
    public void AFormFlattenerName_TheResourcesAlreadyUse_IsNotReused()
    {
        byte[] input = Document(1, 1, annotations: false, namedXObjects: false, directResources: false,
            extraNames: Enumerable.Range(1, 400).Select(k => "FlatXO" + k));

        byte[] output = Flatten(input, annotations: false);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var page = doc.Pages[0];
        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(page.Resources[PdfName.XObject]));
        var drawn = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(xobjects[new PdfName(LastDrawnName(page))]));
        Assert.Equal(PdfName.Form, drawn[PdfName.Subtype] as PdfName);
        // Every name the resources held still names the image: a form registered over one of them
        // would also be drawn, so only the names themselves show the overwrite.
        for (int k = 1; k <= 400; k++)
            Assert.Equal(PdfName.Image, (doc.ResolveObject(xobjects[new PdfName("FlatXO" + k)]) as PdfStream)?[PdfName.Subtype] as PdfName);
    }

    // ------------------------------------------------------------------

    private static byte[] Flatten(byte[] input, bool annotations)
    {
        using var output = new MemoryStream();
        if (annotations) PdfAnnotationEditor.Flatten(new MemoryStream(input), output);
        else PdfFormFlattener.Flatten(new MemoryStream(input), output);
        return output.ToArray();
    }

    /// <summary>The name the page's last content stream draws with <c>Do</c> — the stamped form.</summary>
    private static string LastDrawnName(PdfPage page)
    {
        var ops = ContentStreamParser.Parse(page.GetContentStreamData());
        var draw = ops.Last(o => o.Operator == PdfOperators.InvokeXObject);
        return ((PdfName)draw.Operands[0]).Value;
    }

    /// <summary>
    /// <paramref name="pages"/> pages, each with one widget (or one free-text annotation) carrying an
    /// appearance, reading <paramref name="entries"/> image XObjects from one indirect
    /// <c>/Resources</c> their <c>/Pages</c> node names — its <c>/XObject</c> direct or an object of
    /// its own — or, with <paramref name="directResources"/>, from a dictionary in the page itself;
    /// with <paramref name="resourcesOnPage"/> each page names the indirect one itself.
    /// <paramref name="extraNames"/> are more names for the first image.
    /// </summary>
    private static byte[] Document(int pages, int entries, bool annotations, bool namedXObjects, bool directResources,
        bool resourcesOnPage = false, IEnumerable<string>? extraNames = null)
    {
        var bodies = new List<string>();
        // Objects 3 and 4 are the shared /Resources and the /XObject dictionary it may name.
        int images = 5, firstPage = images + entries, firstAnnot = firstPage + pages;
        int ap = firstAnnot + pages, content = ap + 1;
        var kids = new StringBuilder();
        var fields = new StringBuilder();
        var x = new StringBuilder();
        for (int p = 0; p < pages; p++) { kids.Append(firstPage + p).Append(" 0 R "); fields.Append(firstAnnot + p).Append(" 0 R "); }
        for (int i = 0; i < entries; i++) x.Append("/Im").Append(i).Append(' ').Append(images + i).Append(" 0 R ");
        foreach (string name in extraNames ?? Enumerable.Empty<string>()) x.Append('/').Append(name).Append(' ').Append(images).Append(" 0 R ");

        bodies.Add(annotations ? "<< /Type /Catalog /Pages 2 0 R >>"
                               : $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{fields}] >> >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792]"
                   + (directResources ? "" : " /Resources 3 0 R") + " >>");
        bodies.Add(namedXObjects ? "<< /XObject 4 0 R >>" : $"<< /XObject << {x}>> >>");
        bodies.Add($"<< {x}>>");
        for (int i = 0; i < entries; i++)
            bodies.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\nabc\nendstream");
        for (int p = 0; p < pages; p++)
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R /Annots [{firstAnnot + p} 0 R]"
                       + (directResources ? $" /Resources << /XObject << {x}>> >>" : resourcesOnPage ? " /Resources 3 0 R" : "") + " >>");
        for (int p = 0; p < pages; p++)
            bodies.Add(annotations
                ? $"<< /Type /Annot /Subtype /FreeText /Rect [10 10 40 30] /Contents (x) /DA (/Helv 9 Tf 0 g) /AP << /N {ap} 0 R >> /P {firstPage + p} 0 R >>"
                : $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (f{p}) /Rect [10 10 40 30] /AP << /N {ap} 0 R >> /P {firstPage + p} 0 R >>");
        bodies.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 30 20] /Length 11 >>\nstream\n0 0 m 1 1 l\nendstream");
        string text = "q 10 0 0 10 0 0 cm /Im0 Do Q";
        bodies.Add($"<< /Length {text.Length} >>\nstream\n{text}\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
