// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// ISO 32000-2:2020 Table 354 — a namespace a structure element names must be <b>declared</b> in the
/// structure tree root's <c>/Namespaces</c> array.
/// </summary>
/// <remarks>
/// <para><b>This check exists because authoring found its absence.</b> Every other namespace rule in
/// the PDF/UA-2 structure checker reads an element's own <c>/NS</c> and stops there, so a document
/// whose elements all point at a namespace dictionary the root never declares passed the whole
/// audit. It was noticed by mutating the auto-tagger's <c>/Namespaces</c> write away and watching
/// the audit stay green — the authoring side finding a hole in the checking side, which is the only
/// direction that discovery can run when the two share no code.</para>
///
/// <para><b>Why it is a real failure and not a formality.</b> Table 354 makes that array the
/// document's list of namespace dictionaries. A consumer resolving roles through it — rather than
/// by chasing each element's <c>/NS</c> — finds nothing, so every role in the tree resolves against
/// the default PDF 1.7 namespace instead of the one the elements name.</para>
/// </remarks>
public class Ua2NamespaceDeclarationTests
{
    private const string Pdf2Namespace = "http://iso.org/pdf2/ssn";

    private static AccessibilityAuditReport AuditAsUa2(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfAccessibility.Audit(input, new AuditOptions { Target = PdfUaVersion.Ua2 });
    }

    private static string[] NamespaceFindings(AccessibilityAuditReport report) =>
        report.Issues
              .Where(i => i.Message != null
                          && i.Message.IndexOf("/Namespaces", System.StringComparison.Ordinal) >= 0)
              .Select(i => i.Code + ": " + i.Message)
              .ToArray();

    /// <summary>
    /// An element naming a namespace the root does not declare is reported.
    /// </summary>
    [Fact]
    public void AnUndeclaredNamespaceIsReported()
    {
        string[] found = NamespaceFindings(AuditAsUa2(TaggedPdf(declareNamespace: false)));

        Assert.NotEmpty(found);
        Assert.Contains(found, f => f.IndexOf(Pdf2Namespace, System.StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The control: the same document with the declaration present is not reported — so the check
    /// is about the declaration and not about namespaces existing at all.
    /// </summary>
    [Fact]
    public void ADeclaredNamespaceIsNotReported()
    {
        Assert.Empty(NamespaceFindings(AuditAsUa2(TaggedPdf(declareNamespace: true))));
    }

    /// <summary>
    /// A tree using no namespaces at all is not reported by this check. Its PDF/UA-2 problem is the
    /// <i>absent</i> <c>/NS</c>, which §8.2.5.2's own check reports — two different defects that
    /// must not be conflated into one message.
    /// </summary>
    [Fact]
    public void ATreeWithNoNamespacesAtAllIsNotReportedByThisCheck()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2), writable: false);
        using var tagged = new MemoryStream();
        PdfAccessibility.AutoTag(input, tagged, new AutoTagOptions { Target = PdfUaVersion.Ua1 });

        Assert.Empty(NamespaceFindings(AuditAsUa2(tagged.ToArray())));
    }

    /// <summary>
    /// And the auto-tagger's own PDF/UA-2 output passes it, which is the case the check was written
    /// from.
    /// </summary>
    [Fact]
    public void TheAutoTaggersUa2OutputDeclaresWhatItUses()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(2), writable: false);
        using var tagged = new MemoryStream();
        PdfAccessibility.AutoTag(input, tagged, new AutoTagOptions { Target = PdfUaVersion.Ua2 });

        Assert.Empty(NamespaceFindings(AuditAsUa2(tagged.ToArray())));
    }

    /// <summary>
    /// <b>A child element with no <c>/NS</c> at all is not reported</b> — ISO 14289-2 §8.2.4 permits
    /// it, and a checker that objected would fail conforming documents.
    /// </summary>
    /// <remarks>
    /// <para>§8.2.4: "A structure element with no explicit namespace may be present. Such a
    /// structure element shall have, after any role mapping, a structure type matching one of the
    /// unique PDF 1.7 element types and as restricted by this document." <c>P</c> is such a type,
    /// so this tree conforms. Only the <c>Document</c> element is required to name the PDF 2.0
    /// namespace, by §8.2.5.2, and that is checked separately.</para>
    ///
    /// <para><b>This is an acceptance control, and it exists because a mutation asked for it.</b>
    /// Stripping the namespace from every child the auto-tagger emits leaves the whole PDF/UA-2
    /// audit green — which reads as a checker gap until the clause is actually looked up, and then
    /// turns out to be the correct answer: the tagger is stricter than the standard requires, and
    /// nothing is wrong with output that is not. Without this test, someone reading that survivor
    /// would "fix" the checker into rejecting conforming files.</para>
    /// </remarks>
    [Fact]
    public void AChildElementWithNoNamespaceIsNotReported()
    {
        string[] found = NamespaceFindings(AuditAsUa2(TaggedPdf(declareNamespace: true, namespacelessChild: true)));

        Assert.Empty(found);

        // And not by §8.2.5.2 either: that clause is about the Document element, which still names
        // the PDF 2.0 namespace here.
        AccessibilityAuditReport report =
            AuditAsUa2(TaggedPdf(declareNamespace: true, namespacelessChild: true));
        Assert.DoesNotContain(report.Issues,
            i => i.Code != null && i.Code.StartsWith("UA2-8.2.5.2", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Merging a PDF/UA-2 tag pass into a tree keeps the namespaces the root already declares, whether
    /// its <c>/Namespaces</c> is written direct or as an indirect object: read raw, an indirect array
    /// was no array and was replaced by one holding the tagger's namespace alone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergingKeepsTheDeclaredNamespaces_DirectOrIndirect(bool indirect)
    {
        using var input = new MemoryStream(HalfTaggedPdfDeclaringANamespace(indirect), writable: false);
        using var tagged = new MemoryStream();
        var result = PdfAccessibility.AutoTag(input, tagged,
            new AutoTagOptions { Target = PdfUaVersion.Ua2, MergeIntoExistingTree = true });
        Assert.True(result.MergedIntoExistingTree);
        Assert.True(result.ContentMarked);

        using var doc = Core.Document.PdfDocument.Open(new MemoryStream(tagged.ToArray(), writable: false));
        var root = doc.ResolveObject(doc.Catalog!.Dictionary[Core.Objects.PdfName.StructTreeRoot]) as Core.Objects.PdfDictionary;
        var namespaces = doc.ResolveObject(root![Core.Objects.PdfName.Namespaces]) as Core.Objects.PdfArray;
        Assert.NotNull(namespaces);

        var declared = new List<string>();
        foreach (var entry in namespaces!)
            if (doc.ResolveObject(entry) is Core.Objects.PdfDictionary ns
                && doc.ResolveObject(ns[Core.Objects.PdfName.NS]) is Core.Objects.PdfString uri)
                declared.Add(uri.GetText());
        Assert.Contains("http://example.com/own-namespace", declared);   // the document's own, kept
        Assert.Contains(Pdf2Namespace, declared);                           // the tagger's, added
        Assert.Equal(2, namespaces.Count);
    }

    /// <summary>
    /// Page 1 tagged (a paragraph under a Document element naming the document's own namespace,
    /// declared by the root directly or through object 9), page 2 untagged text for the merge to tag.
    /// </summary>
    private static byte[] HalfTaggedPdfDeclaringANamespace(bool indirectNamespaces)
    {
        const string content1 = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (One) Tj ET EMC";
        const string content2 = "BT /F1 12 Tf 72 700 Td (Two) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 /Tabs /S /Resources << /Font << /F1 12 0 R >> >> /Contents 4 0 R >>",
            "<< /Length " + content1.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content1 + "\nendstream",
            "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 13 0 R /ParentTreeNextKey 1 /Namespaces "
                + (indirectNamespaces ? "9 0 R" : "[7 0 R]") + " >>",
            "<< /Type /StructElem /S /Document /P 5 0 R /NS 7 0 R /K [8 0 R] >>",
            "<< /Type /Namespace /NS (http://example.com/own-namespace) >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "[7 0 R]",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Tabs /S /Resources << /Font << /F1 12 0 R >> >> /Contents 11 0 R >>",
            "<< /Length " + content2.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content2 + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Nums [0 [8 0 R]] >>",
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-2.0\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ================================================================

    /// <summary>
    /// A one-page tagged document whose <c>Document</c> element names the PDF 2.0 namespace, with
    /// the root's <c>/Namespaces</c> declaration present or absent, and optionally with a child
    /// paragraph carrying no <c>/NS</c> of its own.
    /// </summary>
    private static byte[] TaggedPdf(bool declareNamespace, bool namespacelessChild = false)
    {
        // 1 catalog, 2 pages, 3 page, 4 contents, 5 struct tree root, 6 Document, 7 namespace,
        // 8 the namespace-less paragraph when one is asked for.
        const string Content = "BT ET";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + Content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
                + Content + "\nendstream",
            "<< /Type /StructTreeRoot /K [6 0 R]"
                + (declareNamespace ? " /Namespaces [7 0 R]" : string.Empty) + " >>",
            "<< /Type /StructElem /S /Document /P 5 0 R /NS 7 0 R"
                + (namespacelessChild ? " /K [8 0 R]" : string.Empty) + " >>",
            "<< /Type /Namespace /NS (" + Pdf2Namespace + ") >>",
        };

        if (namespacelessChild)
            bodies.Add("<< /Type /StructElem /S /P /P 6 0 R >>");

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-2.0\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
