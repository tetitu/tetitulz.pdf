// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tests for annotation inclusion in the structure tree (ISO 32000-1 §14.7.4.3 / §14.8.4.4).
///
/// <para>
/// <b>The defect these pin.</b> <c>AutoTag</c> emitted no <c>/OBJR</c> at all, so no annotation
/// had an entry in the generated parent tree; <c>PurgeStaleStructureLinks</c> then stripped the
/// annotation's now-dangling <c>/StructParent</c>, and the audit reported <b>UA23</b> — a
/// violation that was <em>absent</em> from the before-audit and <em>present</em> in the
/// after-audit of the same document. Remediation was manufacturing the finding it exists to close.
/// </para>
///
/// <para>
/// Every fixture is assembled in memory as ASCII bytes, and every assertion re-opens the produced
/// document and walks the parsed object graph — a byte scan cannot tell the current revision of an
/// object from a superseded one, which is exactly what an incremental update leaves behind.
/// </para>
/// </summary>
public class AnnotationStructureTests
{
    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    /// <summary>
    /// A heading at y=700, a body line at y=400 and a footer at y=30 (which the tagger classifies
    /// as a pagination artifact). The two blocks give the enclosing-block tests something real to
    /// land inside.
    /// </summary>
    private const string PageText =
        "BT /F1 18 Tf 72 700 Td (Heading) Tj ET\n"
        + "BT /F1 10 Tf 72 400 Td (Body text on the page.) Tj ET\n"
        + "BT /F1 8 Tf 72 30 Td (Page 1) Tj ET\n";

    /// <summary>
    /// One untagged page carrying <paramref name="annotations"/>, which become objects 6, 7, …
    /// in order, so a test can name the annotation it cares about by object number.
    /// </summary>
    private static byte[] UntaggedPdf(params string[] annotations)
    {
        var refs = new List<string>();
        for (int i = 0; i < annotations.Length; i++)
            refs.Add((6 + i).ToString(CultureInfo.InvariantCulture) + " 0 R");

        string annots = annotations.Length == 0
            ? string.Empty
            : "/Annots [" + string.Join(" ", refs) + "] ";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + annots + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(string.Empty, PageText),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        bodies.AddRange(annotations);
        return BuildPdf(bodies);
    }

    /// <summary>
    /// Two pages: the first carries text, the second has an empty content stream (so the tagger
    /// refuses it) and the annotation. Object 8 is the annotation.
    /// </summary>
    private static byte[] TwoPageWithAnnotationOnUnmarkablePage()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(string.Empty, PageText),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R "
                + "/Annots [8 0 R] /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(string.Empty, string.Empty),
            "<< /Type /Annot /Subtype /Widget /Rect [72 395 220 415] /F 4 >>",
        });

    // Annotation bodies. /F 4 is the Print flag — present so the flag path is exercised with a
    // value that must NOT exempt the annotation.
    private const string WidgetOverBody   = "<< /Type /Annot /Subtype /Widget /Rect [72 395 220 415] /F 4 >>";
    private const string WidgetInBlank    = "<< /Type /Annot /Subtype /Widget /Rect [400 195 500 215] /F 4 >>";
    private const string LinkDescribed    = "<< /Type /Annot /Subtype /Link /Rect [72 395 220 415] "
                                          + "/Contents (Opens the annual report) >>";
    private const string LinkUndescribed  = "<< /Type /Annot /Subtype /Link /Rect [72 395 220 415] >>";
    private const string PopupAnnotation  = "<< /Type /Annot /Subtype /Popup /Rect [300 300 400 400] >>";
    private const string HiddenWidget     = "<< /Type /Annot /Subtype /Widget /Rect [72 395 220 415] /F 2 >>";
    private const string NoViewWidget     = "<< /Type /Annot /Subtype /Widget /Rect [72 395 220 415] /F 32 >>";

    // =====================================================================
    //  Operations
    // =====================================================================

    private static byte[] AutoTag(byte[] input, out AutoTagResult result)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        result = PdfAccessibility.AutoTag(inMs, outMs, new AutoTagOptions
        {
            CompressRewrittenStreams = false,
        });
        return outMs.ToArray();
    }

    private static AccessibilityAuditReport Audit(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfAccessibility.Audit(ms);
    }

    // =====================================================================
    //  Object-graph helpers — every assertion goes through these
    // =====================================================================

    private static PdfName Name(string value) => new PdfName(value);

    private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
        => obj is PdfReference ? doc.ResolveObject(obj) : obj;

    /// <summary>One structure element, with the object number the reference to it carried.</summary>
    private sealed class StructElement
    {
        internal int ObjectNumber;
        internal PdfDictionary Dict;
        internal string Role;

        public override string ToString()
            => "<" + Role + "> obj " + ObjectNumber.ToString(CultureInfo.InvariantCulture);
    }

    private static PdfDictionary StructTreeRoot(PdfDocument doc)
    {
        PdfObject raw = doc.Catalog.Dictionary[Name("StructTreeRoot")];
        return raw as PdfDictionary ?? Resolve(doc, raw) as PdfDictionary;
    }

    /// <summary>
    /// The raw <c>/K</c> children of a node, in every shape /K may take. References are returned
    /// unresolved so the caller can still see the object number they name.
    /// </summary>
    private static List<PdfObject> Kids(PdfDocument doc, PdfDictionary node)
    {
        var kids = new List<PdfObject>();
        if (node == null) return kids;

        PdfObject k = node[Name("K")];
        if (k == null) return kids;

        PdfObject resolved = Resolve(doc, k);
        if (resolved is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++) kids.Add(array[i]);
            return kids;
        }

        kids.Add(k);
        return kids;
    }

    /// <summary>Every structure element reachable from the tree root, breadth-first.</summary>
    private static List<StructElement> CollectElements(PdfDocument doc)
    {
        var result = new List<StructElement>();
        PdfDictionary root = StructTreeRoot(doc);
        Assert.NotNull(root);

        var queue = new Queue<KeyValuePair<int, PdfDictionary>>();
        var seen  = new HashSet<int>();

        void Enqueue(PdfObject kid)
        {
            if (kid == null || kid is PdfNumber) return;

            int number = kid is PdfReference reference ? reference.ReferenceObjectNumber : -1;
            PdfDictionary dict = kid as PdfDictionary ?? Resolve(doc, kid) as PdfDictionary;
            if (dict == null) return;

            queue.Enqueue(new KeyValuePair<int, PdfDictionary>(number, dict));
        }

        foreach (PdfObject kid in Kids(doc, root)) Enqueue(kid);

        while (queue.Count > 0)
        {
            KeyValuePair<int, PdfDictionary> entry = queue.Dequeue();
            if (entry.Key > 0 && !seen.Add(entry.Key)) continue;

            string role = (Resolve(doc, entry.Value[Name("S")]) as PdfName)?.Value;
            if (role == null) continue;   // an /OBJR or /MCR leaf, not an element

            result.Add(new StructElement { ObjectNumber = entry.Key, Dict = entry.Value, Role = role });

            foreach (PdfObject kid in Kids(doc, entry.Value)) Enqueue(kid);
        }

        return result;
    }

    /// <summary>Object numbers named by an <c>/OBJR</c> directly under this element.</summary>
    private static List<int> ObjrTargets(PdfDocument doc, PdfDictionary element)
    {
        var targets = new List<int>();
        foreach (PdfObject kid in Kids(doc, element))
        {
            PdfDictionary dict = kid as PdfDictionary ?? Resolve(doc, kid) as PdfDictionary;
            if (dict == null) continue;
            if ((Resolve(doc, dict[Name("Type")]) as PdfName)?.Value != "OBJR") continue;

            if (dict[Name("Obj")] is PdfReference objRef) targets.Add(objRef.ReferenceObjectNumber);
        }
        return targets;
    }

    /// <summary>The one element whose /OBJR names <paramref name="annotObjectNumber"/>.</summary>
    private static StructElement OwnerOf(PdfDocument doc, int annotObjectNumber)
    {
        List<StructElement> owners = CollectElements(doc)
            .Where(e => ObjrTargets(doc, e.Dict).Contains(annotObjectNumber))
            .ToList();

        Assert.True(owners.Count == 1,
            "expected exactly one structure element to reference annotation " +
            annotObjectNumber.ToString(CultureInfo.InvariantCulture) + " through an /OBJR, found " +
            owners.Count.ToString(CultureInfo.InvariantCulture));

        return owners[0];
    }

    /// <summary>Every /OBJR target anywhere in the tree.</summary>
    private static List<int> AllObjrTargets(PdfDocument doc)
        => CollectElements(doc).SelectMany(e => ObjrTargets(doc, e.Dict)).ToList();

    /// <summary>The /ParentTree /Nums keys, in the order the array declares them.</summary>
    private static List<int> ParentTreeKeysInOrder(PdfDocument doc)
    {
        var keys = new List<int>();
        PdfArray nums = ParentTreeNums(doc);
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            var key = Resolve(doc, nums[i]) as PdfNumber;
            Assert.NotNull(key);
            keys.Add(key.IntValue);
        }
        return keys;
    }

    private static PdfArray ParentTreeNums(PdfDocument doc)
    {
        PdfDictionary root = StructTreeRoot(doc);
        Assert.NotNull(root);

        var tree = Resolve(doc, root[Name("ParentTree")]) as PdfDictionary;
        Assert.NotNull(tree);

        var nums = Resolve(doc, tree[Name("Nums")]) as PdfArray;
        Assert.NotNull(nums);
        return nums;
    }

    private static Dictionary<int, PdfObject> ParentTreeEntries(PdfDocument doc)
    {
        var map = new Dictionary<int, PdfObject>();
        PdfArray nums = ParentTreeNums(doc);
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            var key = Resolve(doc, nums[i]) as PdfNumber;
            Assert.NotNull(key);
            map[key.IntValue] = nums[i + 1];
        }
        return map;
    }

    private static int ParentTreeNextKey(PdfDocument doc)
    {
        var next = Resolve(doc, StructTreeRoot(doc)[Name("ParentTreeNextKey")]) as PdfNumber;
        Assert.NotNull(next);
        return next.IntValue;
    }

    private static PdfDictionary Annotation(PdfDocument doc, int pageIndex, int annotIndex, out int objectNumber)
    {
        var annots = Resolve(doc, doc.Pages[pageIndex].Dictionary[Name("Annots")]) as PdfArray;
        Assert.NotNull(annots);

        PdfObject raw = annots[annotIndex];
        objectNumber = raw is PdfReference reference ? reference.ReferenceObjectNumber : -1;
        Assert.True(objectNumber > 0, "the fixture's annotation must be an indirect object");

        var dict = Resolve(doc, raw) as PdfDictionary;
        Assert.NotNull(dict);
        return dict;
    }

    private static int? StructParentOf(PdfDocument doc, PdfDictionary annot)
        => (Resolve(doc, annot[Name("StructParent")]) as PdfNumber)?.IntValue;

    /// <summary>
    /// Walks annotation → /StructParent → /ParentTree → owning element and asserts the round trip
    /// closes on the element whose /OBJR named the annotation.
    /// </summary>
    private static StructElement AssertReachableThroughParentTree(PdfDocument doc, int annotObjectNumber)
    {
        PdfDictionary annot = null;
        for (int p = 0; p < doc.Pages.Count && annot == null; p++)
        {
            var annots = Resolve(doc, doc.Pages[p].Dictionary[Name("Annots")]) as PdfArray;
            if (annots == null) continue;
            for (int i = 0; i < annots.Count; i++)
            {
                if (annots[i] is PdfReference r && r.ReferenceObjectNumber == annotObjectNumber)
                {
                    annot = Resolve(doc, annots[i]) as PdfDictionary;
                    break;
                }
            }
        }
        Assert.NotNull(annot);

        int? key = StructParentOf(doc, annot);
        Assert.True(key.HasValue, "the annotation must carry /StructParent");

        StructElement owner = OwnerOf(doc, annotObjectNumber);

        Assert.True(ParentTreeEntries(doc).TryGetValue(key.Value, out PdfObject value),
            "/ParentTree has no entry for the annotation's /StructParent key " +
            key.Value.ToString(CultureInfo.InvariantCulture));

        // ISO 32000-1 §14.7.4.4: an object's key maps to the owning element ITSELF. A page's key
        // maps to an array indexed by MCID — wrapping this one in an array is the classic bug.
        var reference = value as PdfReference;
        Assert.True(reference != null,
            "an annotation's /ParentTree value must be a direct reference to its owning element, " +
            "not " + (value?.GetType().Name ?? "null"));
        Assert.Equal(owner.ObjectNumber, reference.ReferenceObjectNumber);

        return owner;
    }

    private static string RoleOfParent(PdfDocument doc, StructElement element)
    {
        var parent = Resolve(doc, element.Dict[Name("P")]) as PdfDictionary;
        Assert.NotNull(parent);
        return (Resolve(doc, parent[Name("S")]) as PdfName)?.Value;
    }

    // =====================================================================
    //  A widget becomes a <Form> with an /OBJR
    // =====================================================================

    [Fact]
    public void AutoTag_WidgetAnnotation_GainsAFormElementWhoseObjrNamesIt()
    {
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody), out AutoTagResult result);

        Assert.Equal(1, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Annotation(doc, 0, 0, out int annotNumber);

        StructElement owner = AssertReachableThroughParentTree(doc, annotNumber);
        Assert.Equal("Form", owner.Role);

        // The wrapper must name the page it is rendered on, or a reader cannot place it.
        var pg = owner.Dict[Name("Pg")] as PdfReference;
        Assert.NotNull(pg);
    }

    /// <summary>
    /// ISO 14289-1 §7.18.1 does not apply to an annotation whose rectangle is outside the CropBox —
    /// the shared scope says so for the tagger and the deep checker alike: neither tags nor asks for
    /// one off the page, and both still handle one on it.
    /// </summary>
    [Theory]
    [InlineData("[100 100 200 120]", true)]
    [InlineData("[700 100 800 120]", false)]
    public void AnAnnotationOutsideTheCropBoxIsNeitherTaggedNorAskedFor(string rect, bool onPage)
    {
        string link = "<< /Type /Annot /Subtype /Link /Rect " + rect + " /F 4 /Contents (Example) "
            + "/A << /S /URI /URI (https://example.com) >> >>";

        byte[] output = AutoTag(UntaggedPdf(link), out AutoTagResult result);
        // The deep checker's annotation walk asks a link in scope for its /Contents (UA22).
        var audit = Audit(UntaggedPdf(link.Replace("/Contents (Example) ", "")));

        Assert.Equal(onPage ? 1 : 0, result.AnnotationsTagged);
        using (var doc = PdfDocument.Open(new MemoryStream(output)))
        {
            Annotation(doc, 0, 0, out int annotNumber);
            Assert.Equal(onPage, AllObjrTargets(doc).Contains(annotNumber));
        }
        Assert.Equal(onPage, audit.Issues.Any(i => i.Code == "UA22"));
    }

    [Fact]
    public void AutoTag_WidgetAnnotation_GetsStructParentNotStructParents()
    {
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary annot = Annotation(doc, 0, 0, out _);

        Assert.NotNull(StructParentOf(doc, annot));

        // /StructParents (plural) is the page-level key whose value is an array indexed by MCID.
        // An annotation carrying it would resolve to the wrong shape entirely.
        Assert.Null(annot[Name("StructParents")]);
    }

    // =====================================================================
    //  A link becomes a <Link>
    // =====================================================================

    [Fact]
    public void AutoTag_LinkAnnotation_GainsALinkElementWhoseObjrNamesIt()
    {
        byte[] output = AutoTag(UntaggedPdf(LinkDescribed), out AutoTagResult result);

        Assert.Equal(1, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Annotation(doc, 0, 0, out int annotNumber);

        StructElement owner = AssertReachableThroughParentTree(doc, annotNumber);
        Assert.Equal("Link", owner.Role);
    }

    [Fact]
    public void AutoTag_LinkWithoutContents_IsTaggedButWarnsTheDescriptionIsCallerSupplied()
    {
        byte[] output = AutoTag(UntaggedPdf(LinkUndescribed), out AutoTagResult result);

        // Tagged — reachability and description are different requirements…
        Assert.Equal(1, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary annot = Annotation(doc, 0, 0, out int annotNumber);
        AssertReachableThroughParentTree(doc, annotNumber);

        // …and no description was invented: a fabricated /Contents would make UA22 pass on a
        // document that still announces the wrong thing to a human.
        Assert.Null(annot[Name("Contents")]);

        Assert.Contains(result.Warnings,
            w => w.Contains("/Contents", StringComparison.Ordinal)
              && w.Contains("UA22", StringComparison.Ordinal));

        // The warning is true: the audit really does still report UA22.
        Assert.Contains(Audit(output).Issues, i => i.Code == "UA22");
    }

    // =====================================================================
    //  Exempt annotations are left out — and stay unreported
    // =====================================================================

    [Fact]
    public void AutoTag_PopupAnnotation_IsNotAddedAndProducesNoUa23()
    {
        byte[] output = AutoTag(UntaggedPdf(PopupAnnotation), out AutoTagResult result);

        Assert.Equal(0, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Empty(AllObjrTargets(doc));

        // A pop-up is never independently navigable, so the checker exempts it too — producer and
        // checker must agree, or one of them manufactures a finding.
        Assert.DoesNotContain(Audit(output).Issues, i => i.Code == "UA23");
    }

    [Theory]
    [InlineData(HiddenWidget)]
    [InlineData(NoViewWidget)]
    public void AutoTag_HiddenOrNoViewAnnotation_IsNotAddedAndProducesNoUa23(string annotation)
    {
        byte[] output = AutoTag(UntaggedPdf(annotation), out AutoTagResult result);

        Assert.Equal(0, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Empty(AllObjrTargets(doc));

        PdfDictionary annot = Annotation(doc, 0, 0, out _);
        Assert.Null(annot[Name("StructParent")]);

        Assert.DoesNotContain(Audit(output).Issues, i => i.Code == "UA23");
    }

    // =====================================================================
    //  Parent-tree bookkeeping
    // =====================================================================

    [Fact]
    public void AutoTag_PageKeysAndAnnotationKeys_CoexistWithoutCollision()
    {
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody, LinkDescribed), out AutoTagResult result);

        Assert.Equal(2, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        var pageKey = Resolve(doc, doc.Pages[0].Dictionary[Name("StructParents")]) as PdfNumber;
        Assert.NotNull(pageKey);

        PdfDictionary first  = Annotation(doc, 0, 0, out _);
        PdfDictionary second = Annotation(doc, 0, 1, out _);

        int? firstKey  = StructParentOf(doc, first);
        int? secondKey = StructParentOf(doc, second);
        Assert.True(firstKey.HasValue && secondKey.HasValue);

        var used = new[] { pageKey.IntValue, firstKey.Value, secondKey.Value };
        Assert.Equal(used.Length, used.Distinct().Count());

        // The page key maps to an ARRAY (indexed by MCID); each annotation key to a REFERENCE.
        Dictionary<int, PdfObject> entries = ParentTreeEntries(doc);
        Assert.IsType<PdfArray>(Resolve(doc, entries[pageKey.IntValue]));
        Assert.IsType<PdfReference>(entries[firstKey.Value]);
        Assert.IsType<PdfReference>(entries[secondKey.Value]);
    }

    [Fact]
    public void AutoTag_ParentTreeNextKey_ExceedsEveryPageAndAnnotationKey()
    {
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody, LinkDescribed), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        List<int> keys = ParentTreeKeysInOrder(doc);
        Assert.NotEmpty(keys);

        int nextKey = ParentTreeNextKey(doc);
        foreach (int key in keys)
            Assert.True(key < nextKey,
                "/ParentTreeNextKey (" + nextKey.ToString(CultureInfo.InvariantCulture) +
                ") must exceed every key in use, including annotation keys; found " +
                key.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AutoTag_ParentTreeNums_KeysRemainInAscendingOrder()
    {
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody, LinkDescribed), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        List<int> keys = ParentTreeKeysInOrder(doc);
        Assert.Equal(3, keys.Count);   // one page + two annotations

        for (int i = 1; i < keys.Count; i++)
            Assert.True(keys[i - 1] < keys[i],
                "a number tree's /Nums must be sorted by key — a reader is entitled to binary-search it");
    }

    [Fact]
    public void AutoTag_DocumentWithoutAnnotations_IsUnaffected()
    {
        byte[] output = AutoTag(UntaggedPdf(), out AutoTagResult result);

        Assert.Equal(0, result.AnnotationsTagged);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Single(ParentTreeKeysInOrder(doc));   // the page key alone
        Assert.Empty(AllObjrTargets(doc));
    }

    // =====================================================================
    //  Placement: enclosing block, else /Document
    // =====================================================================

    [Fact]
    public void AutoTag_AnnotationOverABlock_BecomesAChildOfThatBlock()
    {
        // /Rect [72 395 220 415] straddles the body line drawn at y=400.
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Annotation(doc, 0, 0, out int annotNumber);

        StructElement owner = OwnerOf(doc, annotNumber);
        Assert.Equal("P", RoleOfParent(doc, owner));
    }

    [Fact]
    public void AutoTag_AnnotationInsideNoBlock_BecomesAChildOfTheDocumentElement()
    {
        // /Rect [400 195 500 215] sits in blank space: no block's band contains it.
        byte[] output = AutoTag(UntaggedPdf(WidgetInBlank), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Annotation(doc, 0, 0, out int annotNumber);

        StructElement owner = OwnerOf(doc, annotNumber);
        Assert.Equal("Document", RoleOfParent(doc, owner));
    }

    [Fact]
    public void AutoTag_AnnotationOnAPageThatCouldNotBeMarked_IsStillReachable()
    {
        // An /OBJR names the annotation, not an MCID, so it does not depend on its page carrying
        // marked content. Skipping such pages would leave exactly the UA23 findings this work
        // exists to prevent.
        byte[] output = AutoTag(TwoPageWithAnnotationOnUnmarkablePage(), out AutoTagResult result);

        Assert.Equal(1, result.AnnotationsTagged);
        Assert.Equal(1, result.PagesSkipped);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        StructElement owner = AssertReachableThroughParentTree(doc, 8);
        Assert.Equal("Form", owner.Role);
        Assert.Equal("Document", RoleOfParent(doc, owner));
    }

    // =====================================================================
    //  The audit agrees — nothing is manufactured
    // =====================================================================

    [Fact]
    public void AutoTag_AnnotatedDocument_ProducesNoUa23AndNoUa14()
    {
        byte[] output = AutoTag(UntaggedPdf(WidgetOverBody, LinkDescribed), out _);

        AccessibilityAuditReport report = Audit(output);

        Assert.DoesNotContain(report.Issues, i => i.Code == "UA23");

        // UA14 is the other half of the same contract: every /StructParent(s) must be a key in the
        // ParentTree AND resolve to the right kind of object.
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA14");
    }

    /// <summary>
    /// <b>The regression this work exists for.</b> Before the fix, remediating a document with an
    /// annotation produced a UA23 that the input did not have: the auto-tagger emitted no /OBJR,
    /// the stale-link purge then stripped the annotation's /StructParent, and the after-audit —
    /// now looking at a document that claims to be tagged — reported the annotation as unreachable.
    /// A remediation pass must never introduce a violation.
    /// </summary>
    [Fact]
    public void RemediateWithReport_DocumentWithAnnotation_DoesNotIntroduceUa23()
    {
        byte[] input = UntaggedPdf(WidgetOverBody);

        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs,
            new RemediationOptions { Title = "Annotation Fixture" });

        Assert.DoesNotContain(report.BeforeAudit.Issues, i => i.Code == "UA23");
        Assert.DoesNotContain(report.AfterAudit.Issues,  i => i.Code == "UA23");

        // …and the assertion is not vacuous: the document really was tagged, and the annotation
        // really is reachable from the tree that was written.
        byte[] output = outMs.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(output));

        Assert.NotNull(StructTreeRoot(doc));
        Annotation(doc, 0, 0, out int annotNumber);
        AssertReachableThroughParentTree(doc, annotNumber);
    }

    /// <summary>
    /// The same rule stated over the whole annotation family rather than one code, and over every
    /// annotation shape the fixtures cover: whatever the pipeline does, it may close an
    /// annotation finding but never open one. Scoped to UA22/UA23 deliberately — a blanket
    /// "no new code at all" assertion would fail for reasons that have nothing to do with
    /// annotations the moment an unrelated check is added.
    /// </summary>
    [Theory]
    [InlineData(WidgetOverBody)]
    [InlineData(WidgetInBlank)]
    [InlineData(LinkDescribed)]
    [InlineData(LinkUndescribed)]
    [InlineData(PopupAnnotation)]
    [InlineData(HiddenWidget)]
    public void RemediateWithReport_OpensNoAnnotationFinding(string annotation)
    {
        byte[] input = UntaggedPdf(annotation);

        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs,
            new RemediationOptions { Title = "Annotation Fixture" });

        var before = new HashSet<string>(
            report.BeforeAudit.Issues
                .Where(i => i.Severity == IssueSeverity.Error)
                .Select(i => i.Code),
            StringComparer.Ordinal);

        List<string> introduced = report.AfterAudit.Issues
            .Where(i => i.Severity == IssueSeverity.Error)
            .Select(i => i.Code)
            .Where(c => (c == "UA22" || c == "UA23") && !before.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(introduced.Count == 0,
            "remediation introduced annotation finding(s) the input did not have: " +
            string.Join(", ", introduced));
    }
}
