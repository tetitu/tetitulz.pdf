// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Proves that the object graph the accessibility pipeline <em>emits</em> is a real tagged PDF.
///
/// <para>
/// Every assertion here re-opens the produced bytes with <see cref="PdfDocument.Open(Stream)"/>
/// and walks the result: catalog → /StructTreeRoot → /ParentTree → /Nums → the per-page array →
/// the structure element → its MCIDs → the marked-content sequence in the page's decoded content
/// stream. Nothing is asserted about the <em>input</em>, and nothing is asserted from a byte
/// count: "the output got bigger" was exactly the kind of evidence that let a hollow tagging pass
/// look successful. The one length assertion in this file is an upper bound, and it exists to stop
/// a regression, not to prove work happened.
/// </para>
/// </summary>
public class TaggedOutputConformanceTests
{
    // =====================================================================
    //  Fixture: a multi-page PDF that actually draws text
    // =====================================================================

    private const int PageCount = 3;

    private static string HeaderOf(int page) => "Running Header " + page.ToString(CultureInfo.InvariantCulture);
    private static string HeadingOf(int page) => "Chapter " + page.ToString(CultureInfo.InvariantCulture);
    private static string BodyOf(int page, int line) =>
        "Body line " + line.ToString(CultureInfo.InvariantCulture) +
        " on page " + page.ToString(CultureInfo.InvariantCulture) + ".";
    private static string FooterOf(int page) =>
        "Page " + page.ToString(CultureInfo.InvariantCulture) + " of " +
        PageCount.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Untagged, uncompressed, three-page PDF (MediaBox 612x792). Each page draws a 9pt running
    /// header at y=760 (inside the default 50pt top band), a 24pt heading at y=700, five 10pt body
    /// lines from y=650 down in 12pt steps, and a 9pt footer at y=30 (inside the 50pt bottom band).
    /// The 24pt heading is 2.4x the 10pt median, comfortably above the 1.8x H1 ratio.
    /// </summary>
    private static byte[] MultiPageContentPdf()
    {
        string PageContent(int page)
        {
            var sb = new StringBuilder();
            sb.Append("BT /F1 9 Tf 72 760 Td (").Append(HeaderOf(page)).Append(") Tj ET\n");
            sb.Append("BT /F1 24 Tf 72 700 Td (").Append(HeadingOf(page)).Append(") Tj ET\n");
            for (int line = 1; line <= 5; line++)
            {
                int y = 650 - (line - 1) * 12;
                sb.Append("BT /F1 10 Tf 72 ").Append(y.ToString(CultureInfo.InvariantCulture))
                  .Append(" Td (").Append(BodyOf(page, line)).Append(") Tj ET\n");
            }
            sb.Append("BT /F1 9 Tf 72 30 Td (").Append(FooterOf(page)).Append(") Tj ET\n");
            return sb.ToString();
        }

        // Objects: 1 catalog, 2 page tree, 3..5 pages, 6..8 contents, 9 font.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        };

        for (int p = 0; p < PageCount; p++)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                        + "/Resources << /Font << /F1 9 0 R >> >> /Contents "
                        + (6 + p).ToString(CultureInfo.InvariantCulture) + " 0 R >>");
        }

        for (int p = 1; p <= PageCount; p++) objects.Add(StreamObject(PageContent(p)));

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        return BuildPdf(objects);
    }

    // =====================================================================
    //  PDF assembly helpers
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    /// <summary>
    /// Serialises <paramref name="objectBodies"/> as objects 1..n with a classic cross-reference
    /// table. Everything written is ASCII, so <c>StringBuilder.Length</c> is a valid byte offset.
    /// </summary>
    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(objectBodies.Count);
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objectBodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objectBodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // =====================================================================
    //  Reading helpers — everything below reads the OUTPUT document
    // =====================================================================

    private static PdfName Name(string value) => new PdfName(value);

    private static byte[] RunAutoTag(byte[] input, out AutoTagResult result)
    {
        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        result = PdfAccessibility.AutoTag(inStream, outStream, new AutoTagOptions
        {
            Language = "en-US",
            Title    = "Tagged Output Conformance Fixture",
        });
        return outStream.ToArray();
    }

    /// <summary>Decodes the (possibly array-valued) /Contents of one page as text.</summary>
    private static string PageContentText(PdfDocument doc, int pageIndex)
    {
        PdfObject contents = doc.ResolveObject(doc.Pages[pageIndex].Dictionary.Get(Name("Contents")));
        var buffer = new MemoryStream();

        if (contents is PdfArray parts)
        {
            foreach (PdfObject part in parts)
            {
                if (doc.ResolveObject(part) is not PdfStream ps) continue;
                byte[] decoded = ps.GetDecodedData();
                buffer.Write(decoded, 0, decoded.Length);
                buffer.WriteByte((byte)'\n');
            }
        }
        else if (contents is PdfStream stream)
        {
            byte[] decoded = stream.GetDecodedData();
            buffer.Write(decoded, 0, decoded.Length);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Every MCID opened by a <c>BDC</c> in the content, in the order the sequences appear.
    /// The tagger emits <c>/P &lt;&lt;/MCID n&gt;&gt; BDC</c>, so requiring the <c>BDC</c> operator
    /// to follow the property list is what makes this a proof that the identifier really opens a
    /// marked-content sequence rather than merely occurring somewhere in the stream.
    /// </summary>
    private static List<int> BdcMcids(string content)
    {
        var mcids = new List<int>();
        foreach (Match match in Regex.Matches(content, @"/MCID\s+(\d+)\s*>>\s*BDC"))
            mcids.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return mcids;
    }

    /// <summary>
    /// The operand text of the <c>BDC</c> that most recently opened before <paramref name="needle"/>
    /// — e.g. <c>/P &lt;&lt;/MCID 3&gt;&gt;</c> or
    /// <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Header&gt;&gt;</c>. The tagger writes each
    /// <c>BDC</c> on a line of its own, so the line is exactly the operand list.
    /// </summary>
    private static string EnclosingBdcOperands(string content, string needle)
    {
        int at = content.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(at >= 0, "the output must still contain the original text '" + needle + "'");

        int bdc = content.LastIndexOf("BDC", at, StringComparison.Ordinal);
        Assert.True(bdc >= 0, "'" + needle + "' must be inside a marked-content sequence");

        int lineStart = content.LastIndexOf('\n', bdc);
        return content.Substring(lineStart + 1, bdc - lineStart - 1);
    }

    private static PdfDictionary StructTreeRootOf(PdfDocument doc)
    {
        var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(Name("StructTreeRoot"))) as PdfDictionary;
        Assert.NotNull(root);
        return root;
    }

    /// <summary>Reads /ParentTree /Nums as an ordered list of (key, value-array) pairs.</summary>
    private static List<KeyValuePair<int, PdfArray>> ParentTreeEntries(PdfDocument doc, PdfDictionary structTreeRoot)
    {
        var parentTree = doc.ResolveObject(structTreeRoot.Get(Name("ParentTree"))) as PdfDictionary;
        Assert.NotNull(parentTree);

        var nums = doc.ResolveObject(parentTree.Get(Name("Nums"))) as PdfArray;
        Assert.NotNull(nums);
        Assert.True(nums.Count > 0, "/Nums must not be empty in a document that marked content");
        Assert.True(nums.Count % 2 == 0, "/Nums is a flat array of key/value pairs");

        var entries = new List<KeyValuePair<int, PdfArray>>(nums.Count / 2);
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            var key = doc.ResolveObject(nums[i]) as PdfNumber;
            Assert.NotNull(key);
            var value = doc.ResolveObject(nums[i + 1]) as PdfArray;
            Assert.NotNull(value);
            entries.Add(new KeyValuePair<int, PdfArray>(key.IntValue, value));
        }
        return entries;
    }

    /// <summary>Reference-identity set membership; the object model gives PdfName/PdfReference value semantics.</summary>
    private sealed class ReferenceIdentity : IEqualityComparer<PdfDictionary>
    {
        internal static readonly ReferenceIdentity Instance = new ReferenceIdentity();
        public bool Equals(PdfDictionary x, PdfDictionary y) => ReferenceEquals(x, y);
        public int GetHashCode(PdfDictionary obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>The raw (unresolved) /K children of a structure element, in every shape /K may take.</summary>
    private static IReadOnlyList<PdfObject> RawKids(PdfDocument doc, PdfDictionary element)
    {
        PdfObject k = element.Get(Name("K"));
        if (k == null) return Array.Empty<PdfObject>();

        if (doc.ResolveObject(k) is PdfArray array)
        {
            var items = new PdfObject[array.Count];
            for (int i = 0; i < array.Count; i++) items[i] = array[i];
            return items;
        }
        return new[] { k };
    }

    /// <summary>Every structure element reachable from the root, cycle-guarded and depth-capped.</summary>
    private static List<PdfDictionary> CollectStructElements(PdfDocument doc, PdfDictionary structTreeRoot)
    {
        var found = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceIdentity.Instance);

        void Visit(PdfDictionary node, int depth)
        {
            if (node == null || depth > 64 || !seen.Add(node)) return;
            if (node.Get(Name("S")) is PdfName) found.Add(node);

            foreach (PdfObject kid in RawKids(doc, node))
            {
                if (doc.ResolveObject(kid) is not PdfDictionary child) continue;
                if (child.Get(Name("S")) == null) continue;   // /MCR and /OBJR are leaves
                Visit(child, depth + 1);
            }
        }

        Visit(structTreeRoot, 0);
        return found;
    }

    private static int PageObjectNumberOf(PdfDocument doc, PdfDictionary element)
        => element.Get(Name("Pg")) is PdfReference reference ? reference.ReferenceObjectNumber : -1;

    // =====================================================================
    //  Catalog wiring
    // =====================================================================

    [Fact]
    public void AutoTag_Output_CatalogDeclaresATaggedDocument()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out AutoTagResult result);
        Assert.True(result.ContentMarked,
            "fixture must be taggable: " + string.Join(" | ", result.SkippedPageReasons));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary catalog = doc.Catalog.Dictionary;

        Assert.NotNull(doc.ResolveObject(catalog.Get(Name("StructTreeRoot"))) as PdfDictionary);

        var markInfo = doc.ResolveObject(catalog.Get(Name("MarkInfo"))) as PdfDictionary;
        Assert.NotNull(markInfo);
        Assert.True((doc.ResolveObject(markInfo.Get(Name("Marked"))) as PdfBoolean)?.Value,
            "/MarkInfo << /Marked true >> is what declares the document tagged");

        string lang = (doc.ResolveObject(catalog.Get(Name("Lang"))) as PdfString)?.GetText();
        Assert.False(string.IsNullOrWhiteSpace(lang), "the catalog must carry a natural-language tag");

        var viewerPrefs = doc.ResolveObject(catalog.Get(Name("ViewerPreferences"))) as PdfDictionary;
        Assert.NotNull(viewerPrefs);
        Assert.True((doc.ResolveObject(viewerPrefs.Get(Name("DisplayDocTitle"))) as PdfBoolean)?.Value,
            "/ViewerPreferences << /DisplayDocTitle true >> is required by PDF/UA-1");
    }

    // =====================================================================
    //  Number tree
    // =====================================================================

    [Fact]
    public void AutoTag_Output_ParentTreeNextKeyIsExactlyOneAboveTheLargestKey()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfDictionary structTreeRoot = StructTreeRootOf(doc);

        var nextKey = doc.ResolveObject(structTreeRoot.Get(Name("ParentTreeNextKey"))) as PdfNumber;
        Assert.NotNull(nextKey);

        List<KeyValuePair<int, PdfArray>> entries = ParentTreeEntries(doc, structTreeRoot);
        int largest = entries.Max(e => e.Key);

        Assert.Equal(largest + 1, nextKey.IntValue);
    }

    [Fact]
    public void AutoTag_Output_ParentTreeKeysAscendAndCoverEveryPage()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        List<KeyValuePair<int, PdfArray>> entries = ParentTreeEntries(doc, StructTreeRootOf(doc));

        // A number tree is defined to be searchable by key, and readers binary-search it.
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i - 1].Key < entries[i].Key,
                "/Nums keys must strictly ascend, found " + entries[i - 1].Key + " before " + entries[i].Key);
        }

        var keys = new HashSet<int>(entries.Select(e => e.Key));
        Assert.Equal(PageCount, doc.Pages.Count);

        for (int p = 0; p < doc.Pages.Count; p++)
        {
            var structParents = doc.ResolveObject(
                doc.Pages[p].Dictionary.Get(Name("StructParents"))) as PdfNumber;
            Assert.True(structParents != null, "page " + (p + 1) + " must declare /StructParents");
            Assert.True(keys.Contains(structParents.IntValue),
                "page " + (p + 1) + " declares /StructParents " + structParents.IntValue +
                " which the /ParentTree does not define");
        }

        // Distinct keys per page: sharing one would resolve page 2's MCIDs to page 1's elements.
        Assert.Equal(doc.Pages.Count, keys.Count);
    }

    [Fact]
    public void AutoTag_Output_EveryParentTreeValueIsAnMcidIndexedArrayOfElementsOnThatPage()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        List<KeyValuePair<int, PdfArray>> entries = ParentTreeEntries(doc, StructTreeRootOf(doc));
        var byKey = entries.ToDictionary(e => e.Key, e => e.Value);

        for (int p = 0; p < doc.Pages.Count; p++)
        {
            var structParents = doc.ResolveObject(
                doc.Pages[p].Dictionary.Get(Name("StructParents"))) as PdfNumber;
            Assert.True(structParents != null, "page " + (p + 1) + " must declare /StructParents");

            Assert.True(byKey.TryGetValue(structParents.IntValue, out PdfArray owners),
                "the /ParentTree has no entry for page " + (p + 1) + "'s key " + structParents.IntValue);

            int pageObjectNumber = index.GetPageObjectNumber(p);
            Assert.True(pageObjectNumber > 0, "the page's object number must be resolvable");

            List<int> contentMcids = BdcMcids(PageContentText(doc, p));
            Assert.Equal(contentMcids.Count, owners.Count);

            for (int mcid = 0; mcid < owners.Count; mcid++)
            {
                var owner = doc.ResolveObject(owners[mcid]) as PdfDictionary;
                Assert.True(owner != null,
                    "page " + (p + 1) + " MCID " + mcid + " resolves to no structure element");

                Assert.Equal("StructElem", (owner.Get(Name("Type")) as PdfName)?.Value);
                Assert.NotNull(owner.Get(Name("S")) as PdfName);

                Assert.Equal(pageObjectNumber, PageObjectNumberOf(doc, owner));
            }
        }
    }

    // =====================================================================
    //  Structure ↔ content linkage
    // =====================================================================

    [Fact]
    public void AutoTag_Output_EveryStructElemMcidOpensABdcInItsOwnPageContentStream()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        // Page object number → the MCIDs its content stream really opens.
        var contentMcidsByPageObject = new Dictionary<int, HashSet<int>>();
        for (int p = 0; p < doc.Pages.Count; p++)
            contentMcidsByPageObject[index.GetPageObjectNumber(p)] = new HashSet<int>(BdcMcids(PageContentText(doc, p)));

        List<PdfDictionary> elements = CollectStructElements(doc, StructTreeRootOf(doc));
        Assert.NotEmpty(elements);

        int checkedMcids = 0;
        foreach (PdfDictionary element in elements)
        {
            int elementPage = PageObjectNumberOf(doc, element);

            foreach (PdfObject kid in RawKids(doc, element))
            {
                int pageObjectNumber;
                int mcid;

                if (kid is PdfNumber bare)
                {
                    // A bare integer names an MCID on the element's own /Pg.
                    pageObjectNumber = elementPage;
                    mcid = bare.IntValue;
                }
                else if (doc.ResolveObject(kid) is PdfDictionary mcr
                         && (mcr.Get(Name("Type")) as PdfName)?.Value == "MCR")
                {
                    pageObjectNumber = mcr.Get(Name("Pg")) is PdfReference pgRef
                        ? pgRef.ReferenceObjectNumber
                        : elementPage;

                    var mcrMcid = doc.ResolveObject(mcr.Get(Name("MCID"))) as PdfNumber;
                    Assert.True(mcrMcid != null, "an /MCR must carry an /MCID");
                    mcid = mcrMcid.IntValue;
                }
                else
                {
                    continue;   // a child structure element, handled on its own turn
                }

                Assert.True(pageObjectNumber > 0,
                    "an element that owns MCID " + mcid + " must name the page it lives on");
                Assert.True(contentMcidsByPageObject.TryGetValue(pageObjectNumber, out HashSet<int> present),
                    "/Pg " + pageObjectNumber + " is not a page of this document");
                Assert.True(present.Contains(mcid),
                    "MCID " + mcid + " is claimed by a structure element but no BDC in object " +
                    pageObjectNumber + "'s content stream opens it");
                checkedMcids++;
            }
        }

        Assert.True(checkedMcids > 0,
            "a tagged document must reference at least one MCID from its structure tree");
    }

    [Fact]
    public void AutoTag_Output_McidsOnEachPageAreContiguousFromZero()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        for (int p = 0; p < doc.Pages.Count; p++)
        {
            List<int> mcids = BdcMcids(PageContentText(doc, p));
            Assert.True(mcids.Count > 0, "page " + (p + 1) + " draws text and must carry MCIDs");

            // The ParentTree value is an array INDEXED by MCID, so a gap or a repeat silently
            // shifts every later element by one.
            List<int> sorted = mcids.OrderBy(m => m).ToList();
            for (int i = 0; i < sorted.Count; i++)
                Assert.Equal(i, sorted[i]);

            Assert.Equal(mcids.Count, new HashSet<int>(mcids).Count);
        }
    }

    // =====================================================================
    //  Artifacts
    // =====================================================================

    [Fact]
    public void AutoTag_Output_HeadersAndFootersAreContentArtifactsOutsideTheStructureTree()
    {
        byte[] output = RunAutoTag(MultiPageContentPdf(), out _);

        using var doc = PdfDocument.Open(new MemoryStream(output));

        for (int p = 0; p < doc.Pages.Count; p++)
        {
            string content = PageContentText(doc, p);

            Assert.Contains("/Subtype /Header", content, StringComparison.Ordinal);
            Assert.Contains("/Subtype /Footer", content, StringComparison.Ordinal);

            foreach (string running in new[] { HeaderOf(p + 1), FooterOf(p + 1) })
            {
                string operands = EnclosingBdcOperands(content, running);
                Assert.Contains("/Artifact", operands, StringComparison.Ordinal);
                Assert.DoesNotContain("/MCID", operands, StringComparison.Ordinal);
            }

            // The control: real content is inside an /MCID sequence, not an artifact one.
            string headingOperands = EnclosingBdcOperands(content, HeadingOf(p + 1));
            Assert.Contains("/MCID", headingOperands, StringComparison.Ordinal);
            Assert.DoesNotContain("/Artifact", headingOperands, StringComparison.Ordinal);
        }

        // Artifacts are content-level. An /S /Artifact structure element is the old,
        // non-conformant shape and must not exist anywhere in the tree.
        foreach (PdfDictionary element in CollectStructElements(doc, StructTreeRootOf(doc)))
            Assert.NotEqual("Artifact", (element.Get(Name("S")) as PdfName)?.Value);
    }

    // =====================================================================
    //  Round trip: the audit must agree the output is really tagged
    // =====================================================================

    [Fact]
    public void AutoTag_Output_AuditReportsNoHollowTaggingErrors()
    {
        byte[] input = MultiPageContentPdf();

        // Positive control: the audit really is looking. The untagged input fails UA01.
        AccessibilityAuditReport before = PdfAccessibility.Audit(new MemoryStream(input));
        Assert.Contains(before.Issues, i => i.Code == "UA01" && i.Severity == IssueSeverity.Error);

        byte[] output = RunAutoTag(input, out _);
        AccessibilityAuditReport after = PdfAccessibility.Audit(new MemoryStream(output));

        Assert.DoesNotContain(after.Issues, i => i.Code == "UA01" && i.Severity == IssueSeverity.Error);

        foreach (string hollowCode in new[] { "UA16", "UA17", "UA18" })
        {
            Assert.DoesNotContain(after.Issues,
                i => i.Code == hollowCode && i.Severity == IssueSeverity.Error);
        }
    }

    // =====================================================================
    //  Remediation pipeline
    // =====================================================================

    private static RemediationReport Remediate(byte[] input, out byte[] output)
    {
        using var inStream = new MemoryStream(input);
        using var outStream = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inStream, outStream,
            new RemediationOptions { Language = "en-US", Title = "Tagged Output Conformance Fixture" });
        output = outStream.ToArray();
        return report;
    }

    [Fact]
    public void RemediateWithReport_ReducesErrorsAndWritesThePdfUaIdentifier()
    {
        byte[] input = MultiPageContentPdf();
        RemediationReport report = Remediate(input, out byte[] output);

        Assert.True(report.AfterAudit.ErrorCount < report.BeforeAudit.ErrorCount,
            "remediation must strictly reduce errors: " + report);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var metadata = doc.ResolveObject(doc.Catalog.Dictionary.Get(Name("Metadata"))) as PdfStream;
        Assert.True(metadata != null, "the catalog must carry an XMP /Metadata stream after remediation");

        // UA03 is unreachable by any structural fix; only this declaration closes it.
        string xmp = Encoding.UTF8.GetString(metadata.GetDecodedData());
        Assert.Contains("pdfuaid:part", xmp, StringComparison.Ordinal);
    }

    [Fact]
    public void RemediateWithReport_LogsTheStepsThatRanAndWhyTheOthersDidNot()
    {
        RemediationReport report = Remediate(MultiPageContentPdf(), out _);

        Assert.NotEmpty(report.Steps);
        Assert.NotEmpty(report.AppliedSteps);
        Assert.Empty(report.FailedSteps);

        Assert.Contains(report.AppliedSteps, s => s.Name == "AutoTag");
        Assert.Contains(report.AppliedSteps, s => s.Name == "SetPdfUaIdentifier");

        // AutoTag subsumes artifact marking and reading-order repair, so those must be reported
        // as skipped-with-a-reason rather than silently doing nothing.
        Assert.NotEmpty(report.SkippedSteps);
        foreach (RemediationStepResult step in report.SkippedSteps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.SkipReason),
                "step '" + step.Name + "' was skipped without saying why");
        }
    }

    [Fact]
    public void RemediateWithReport_DoesNotBloatTheDocument()
    {
        byte[] input = MultiPageContentPdf();
        RemediationReport report = Remediate(input, out byte[] output);

        Assert.Equal(input.Length, report.InputLength);
        Assert.Equal(output.Length, report.OutputLength);
        Assert.True(output.Length > input.Length, "remediation appends at least one revision");

        // Guard against the old unconditional seven-pass pipeline, which re-serialised the whole
        // document once per step. The pipeline is now two revisions for an untagged file (the
        // layout pass plus the XMP pass); the XMP packet alone is a fixed ~3 KB regardless of
        // document size, which is why the bound is a multiple with generous headroom rather than
        // a tight one. Twelve times the input would still mean something went badly wrong.
        Assert.True(output.Length <= input.Length * 12L,
            "remediation output grew to " + output.Length + " bytes from " + input.Length +
            " — more than 12x suggests the passes are no longer collapsed");
    }

    // =====================================================================
    //  Artifact marker used on its own
    // =====================================================================

    [Fact]
    public void MarkHeadersAndFooters_MarksContentArtifactsAndInventsNoStructureElement()
    {
        using var input = new MemoryStream(MultiPageContentPdf());
        using var output = new MemoryStream();
        ArtifactMarkResult result = PdfAccessibility.MarkHeadersAndFooters(input, output);

        Assert.True(result.ArtifactsMarked > 0,
            "each page's header at y=760 and footer at y=30 sit inside the default 50pt bands");

        byte[] bytes = output.ToArray();
        using var doc = PdfDocument.Open(new MemoryStream(bytes));

        for (int p = 0; p < doc.Pages.Count; p++)
        {
            string content = PageContentText(doc, p);

            string headerOperands = EnclosingBdcOperands(content, HeaderOf(p + 1));
            Assert.Contains("/Artifact", headerOperands, StringComparison.Ordinal);

            string footerOperands = EnclosingBdcOperands(content, FooterOf(p + 1));
            Assert.Contains("/Artifact", footerOperands, StringComparison.Ordinal);
        }

        // The old, non-conformant behaviour invented /S /Artifact structure elements. Artifacts
        // are content-level constructs and must never appear in the structure tree.
        string raw = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("/S /Artifact", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/S/Artifact", raw, StringComparison.Ordinal);

        var structTreeRoot = doc.ResolveObject(
            doc.Catalog.Dictionary.Get(Name("StructTreeRoot"))) as PdfDictionary;
        if (structTreeRoot != null)
        {
            foreach (PdfDictionary element in CollectStructElements(doc, structTreeRoot))
                Assert.NotEqual("Artifact", (element.Get(Name("S")) as PdfName)?.Value);
        }
    }
}
