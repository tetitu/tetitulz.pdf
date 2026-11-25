// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Tests.Helpers;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Exercises <see cref="DocMdpAnalyzer"/> directly across the P=2/P=3 permission
/// levels and the structural-change classifier — the part the high-level
/// <see cref="PdfSignatureVerifier"/> wiring depends on. Each post-certification
/// change is produced as a real incremental update so the analyzer diffs two
/// genuine revisions.
/// </summary>
public class DocMdpAnalyzerTests
{
    // ── P=2: form fill / additional signatures are permitted ────────────────

    [Fact]
    public void P2_AdditionalApprovalSignature_IsWithinPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 sign"),
            "Cert", DocMdpPermission.FormsAndSignatures);
        byte[] modified = Sign(certified, "Approval");

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.Equal(2, mdp.PermissionLevel);
        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
    }

    [Fact]
    public void P2_AddingCommentAnnotation_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 annot"),
            "Cert", DocMdpPermission.FormsAndSignatures);
        byte[] modified = AppendTextAnnotation(certified);

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("annotation"));
    }

    // ── P=3: annotations are permitted, content/structure is not ────────────

    [Fact]
    public void P3_AddingCommentAnnotation_IsWithinPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P3 annot"),
            "Cert", DocMdpPermission.CommentAnnotations);
        byte[] modified = AppendTextAnnotation(certified);

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.Equal(3, mdp.PermissionLevel);
        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
    }

    [Fact]
    public void P3_RotatingPage_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P3 rotate"),
            "Cert", DocMdpPermission.CommentAnnotations);
        byte[] modified = AddPageRotation(certified);

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("page modified"));
    }

    // ── The catalog: an anchoring proof is evidence, in its own shape only ───

    /// <summary>
    /// A <c>/BlockchainProof</c> the catalog gains is evidence about the signature, as <c>/DSS</c> is:
    /// permitted when it carries a transaction hash and the document hash it anchors, a violation in
    /// any other shape — and any other new catalog entry stays one.
    /// </summary>
    [Theory]
    [InlineData("BlockchainProof", true, true)]
    [InlineData("BlockchainProof", false, false)]
    [InlineData("ReviewProbe", true, false)]
    public void ACatalogGainingAnEntry_IsPermittedOnlyForAProofInItsShape(string key, bool shaped, bool permitted)
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 proof"),
            "Cert", DocMdpPermission.FormsAndSignatures);
        var value = new PdfDictionary();
        value.Set(PdfName.DocumentHash, new PdfString("00"));
        if (shaped) value.Set(PdfName.TxHash, new PdfString("0x1"));
        byte[] modified = AppendCatalogEntry(certified, new PdfName(key), value);

        var mdp = Analyze(modified);

        Assert.Equal(permitted, mdp.IsCompliant);
        if (permitted)
            Assert.Contains(mdp.PermittedChanges, c => c.Contains("/BlockchainProof added"));
        else
            Assert.Contains(mdp.Violations, v => v.Contains("/" + key));
    }

    /// <summary>The catalog superseded with one more entry, as an incremental update.</summary>
    private static byte[] AppendCatalogEntry(byte[] pdf, PdfName key, PdfObject value)
    {
        using var src = new MemoryStream(pdf);
        var parser = new PdfParser(src);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var rootRef = (PdfReference)parser.Trailer[PdfName.Root];
        var root = (PdfDictionary)parser.ResolveObject(rootRef);
        var catalog = Clone(root);
        catalog.Set(key, value);
        return AppendObjects(pdf, (rootRef.ReferenceObjectNumber, catalog));
    }

    // ── Adversarial: malformed certification /ByteRange must not throw/hang ──

    [Fact]
    public void MalformedCertByteRange_ReportsErrorWithoutThrowing()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("DoS"),
            "Cert", DocMdpPermission.NoChanges);
        byte[] corrupt = CorruptCertByteRange(certified);

        var mdp = Analyze(corrupt); // must return, never throw or loop

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.NotEmpty(mdp.Violations);
    }

    [Fact]
    public void NonDictionaryObjectRewrittenAfterCertification_ReportsViolationWithoutThrowing()
    {
        // Regression: an object that is a NON-dictionary in *both* revisions reached the
        // form-field classifier with oldNull == newNull == false but a null `dict` — IsNull()
        // only treats a missing object or /Null as null, so a rewritten array does not set
        // either flag. Dereferencing `dict` there threw NullReferenceException straight out of
        // Analyze, which is documented to fail closed rather than throw.
        byte[] baseline = MinimalPdfBuilder.CreateMinimalPdf("MDP non-dict");
        int arrayObj = MaxObjectNumber(baseline) + 1;

        // The object must already exist in the certified revision; if it were introduced by the
        // update it would be classified as "added" and never reach the failing branch.
        var before = new PdfArray(); before.Add(new PdfNumber(1));
        var after = new PdfArray(); after.Add(new PdfNumber(2));

        byte[] certified = Certify(AppendIndirectObject(baseline, arrayObj, before),
            "Cert", DocMdpPermission.NoChanges);
        byte[] modified = AppendIndirectObject(certified, arrayObj, after);

        var mdp = Analyze(modified); // must return, never throw

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.NotEmpty(mdp.Violations);
    }

    // ── Indirect names (§7.3.10) are classified as what they name ───────────
    //
    // Each referenced name object is written before certification, so the only change after it is
    // the object under test. The first three were false violations; the last was a false pass.

    [Fact]
    public void P2_AppearanceStreamWithAnIndirectSubtype_IsWithinPermissions()
    {
        byte[] baseline = MinimalPdfBuilder.CreateMinimalPdf("MDP indirect subtype");
        int nameObj = MaxObjectNumber(baseline) + 1;
        byte[] certified = Certify(AppendIndirectObject(baseline, nameObj, new PdfName("Form")),
            "Cert", DocMdpPermission.FormsAndSignatures);

        var appearance = new PdfStream { RawData = Encoding.ASCII.GetBytes("0 0 5 5 re f") };
        appearance.Set(PdfName.Subtype, new PdfReference(nameObj, 0));
        appearance.Set(PdfName.BBox, new PdfArray(new PdfObject[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(5), new PdfNumber(5) }));
        var mdp = Analyze(AppendIndirectObject(certified, MaxObjectNumber(certified) + 1, appearance));

        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
    }

    [Fact]
    public void P2_SignatureFieldWithAnIndirectFieldType_IsWithinPermissions()
    {
        byte[] baseline = MinimalPdfBuilder.CreateMinimalPdf("MDP indirect FT");
        int nameObj = MaxObjectNumber(baseline) + 1;
        byte[] certified = Certify(AppendIndirectObject(baseline, nameObj, new PdfName("Sig")),
            "Cert", DocMdpPermission.FormsAndSignatures);

        var field = new PdfDictionary();
        field.Set(PdfName.FT, new PdfReference(nameObj, 0));
        field.Set(PdfName.T, new PdfString("Later"));
        var mdp = Analyze(AppendIndirectObject(certified, MaxObjectNumber(certified) + 1, field));

        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
    }

    [Fact]
    public void P1_DocumentTimestampWithAnIndirectType_IsWithinPermissions()
    {
        byte[] baseline = MinimalPdfBuilder.CreateMinimalPdf("MDP indirect DocTimeStamp");
        int nameObj = MaxObjectNumber(baseline) + 1;
        byte[] certified = Certify(AppendIndirectObject(baseline, nameObj, new PdfName("DocTimeStamp")),
            "Cert", DocMdpPermission.NoChanges);

        var timestamp = new PdfDictionary();
        timestamp.Set(PdfName.Type, new PdfReference(nameObj, 0));
        timestamp.Set(PdfName.ByteRange, new PdfArray(new PdfObject[] { new PdfNumber(0), new PdfNumber(10), new PdfNumber(20), new PdfNumber(10) }));
        timestamp.Set(PdfName.Contents, new PdfString(new byte[] { 0x30, 0x00 }));
        var mdp = Analyze(AppendIndirectObject(certified, MaxObjectNumber(certified) + 1, timestamp));

        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
    }

    [Fact]
    public void P3_AddedWidgetWithAnIndirectSubtype_IsNotReadAsACommentAnnotation()
    {
        byte[] baseline = MinimalPdfBuilder.CreateMinimalPdf("MDP indirect widget");
        int nameObj = MaxObjectNumber(baseline) + 1;
        byte[] certified = Certify(AppendIndirectObject(baseline, nameObj, new PdfName("Widget")),
            "Cert", DocMdpPermission.CommentAnnotations);

        byte[] modified = AppendPageEdit(certified, (pageDict, annotObjNum) =>
        {
            var annots = new PdfArray();
            var existing = pageDict.GetArray(PdfName.Annots);
            if (existing != null) foreach (var a in existing) annots.Add(a);
            annots.Add(new PdfReference(annotObjNum, 0));
            var newPage = Clone(pageDict);
            newPage.Set(PdfName.Annots, annots);

            var widget = new PdfDictionary();
            widget.Set(PdfName.Subtype, new PdfReference(nameObj, 0));
            widget.Set(PdfName.Rect, new PdfArray(new PdfObject[] { new PdfNumber(10), new PdfNumber(10), new PdfNumber(30), new PdfNumber(30) }));
            return (newPage, widget);
        });

        var mdp = Analyze(modified);

        Assert.False(mdp.IsCompliant, "an added widget passed as a comment annotation at P=3");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // ── Deletions: the change that left no offset to diff ───────────────────
    //
    // A deleted object is marked free in the later revision's cross-reference section. Free
    // entries carry no file offset, so the "which objects were written after the certified byte
    // range?" scan could not see one — and a revision containing nothing but deletions was
    // classified as compliant at every permission level.

    /// <summary>
    /// P=2 permits form fill-in and signing. Deleting the page object is neither, and it is the
    /// case that shows what the gap cost: the document no longer renders, and the certification
    /// reported itself intact.
    /// </summary>
    [Fact]
    public void P2_DeletingAPageObject_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 delete"),
            "Cert", DocMdpPermission.FormsAndSignatures);
        byte[] modified = AppendObjectDeletion(certified, objNum: 3);   // object 3 is the page

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("deleted after certification"));
    }

    /// <summary>
    /// P=3 additionally permits annotations to be added, modified <i>and deleted</i>
    /// (§12.8.2.2 Table 254), so removing one must stay compliant — the control that keeps the
    /// test above from meaning "any deletion is a violation".
    /// </summary>
    [Fact]
    public void P3_DeletingAnAnnotation_IsWithinPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P3 delete annot"),
            "Cert", DocMdpPermission.CommentAnnotations);
        byte[] annotated = AppendTextAnnotation(certified);

        // AppendPageEdit allocates the annotation as the first free object number, so it is the
        // document's highest — read it back rather than hard-coding, since the certification
        // signature's own objects sit in between.
        int annotObjNum = HighestObjectNumber(annotated);
        byte[] modified = AppendObjectDeletion(annotated, annotObjNum);

        var mdp = Analyze(modified);

        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
    }

    /// <summary>
    /// …and P=3's licence is for annotations only: deleting the page is still a violation there.
    /// Without this, "P=3 permits annotations" would be indistinguishable from a blanket
    /// exemption at that level.
    /// </summary>
    [Fact]
    public void P3_DeletingAPageObject_StillExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P3 delete page"),
            "Cert", DocMdpPermission.CommentAnnotations);
        byte[] modified = AppendObjectDeletion(certified, objNum: 3);

        var mdp = Analyze(modified);

        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("deleted after certification"));
    }

    // ── LTV (/DSS) blesses additions of its own shape, never a rewrite ──────
    //
    // A /DSS allow-list admitting every object reachable from the catalog's /DSS, through any key,
    // before any other classification would report a revision that rewrote the page's content
    // stream and listed it under /Certs compliant at P=1 — the verdict PdfSignatureVerifier
    // reports. LTV data is only ever added, and only in its own shapes.

    [Fact]
    public void P1_ContentStreamRewrittenAndListedUnderDssCerts_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P1 dss"), "Cert", DocMdpPermission.NoChanges);
        const int contentObj = 4; // MinimalPdfBuilder's page content stream
        int dssObj = MaxObjectNumber(certified) + 1;

        var dss = new PdfDictionary();
        dss.Set(new PdfName("Certs"), new PdfArray(new PdfObject[] { new PdfReference(contentObj, 0) }));

        byte[] modified = AppendObjects(certified,
            (contentObj, StreamOf(Encoding.ASCII.GetBytes("BT /F1 12 Tf 100 700 Td (Rewritten) Tj ET"))),
            (dssObj, dss),
            (CatalogObjectNumber(certified), CatalogWith(certified, PdfName.DSS, new PdfReference(dssObj, 0))));

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.Equal(1, mdp.PermissionLevel);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.StartsWith($"obj {contentObj}:", StringComparison.Ordinal));
    }

    [Fact]
    public void P2_AnnotationListedUnderDssVri_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 vri"), "Cert", DocMdpPermission.FormsAndSignatures);
        int annotObj = MaxObjectNumber(certified) + 1;
        int dssObj = annotObj + 1;

        var vri = new PdfDictionary();
        vri.Set(new PdfName(new string('A', 40)), new PdfReference(annotObj, 0));
        var dss = new PdfDictionary();
        dss.Set(new PdfName("VRI"), vri);

        byte[] modified = AppendObjects(certified,
            (annotObj, TextAnnotation()),
            (PageObjectNumber(certified), PageWithAnnotation(certified, annotObj)),
            (dssObj, dss),
            (CatalogObjectNumber(certified), CatalogWith(certified, PdfName.DSS, new PdfReference(dssObj, 0))));

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.Equal(2, mdp.PermissionLevel);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("annotation"));
    }

    /// <summary>The acceptance control: a DSS of ISO 32000-2's shape — evidence streams, a /VRI entry with a timestamp — is LTV at P=1.</summary>
    [Fact]
    public void P1_DssWithNewEvidenceStreamsAndAVriEntry_IsWithinPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P1 ltv"), "Cert", DocMdpPermission.NoChanges);
        int certStream = MaxObjectNumber(certified) + 1;
        int tsStream = certStream + 1;
        int vriEntryObj = tsStream + 1;
        int dssObj = vriEntryObj + 1;

        var vriEntry = new PdfDictionary();
        vriEntry.Set(PdfName.Type, new PdfName("VRI"));
        vriEntry.Set(PdfName.Cert, new PdfArray(new PdfObject[] { new PdfReference(certStream, 0) }));
        vriEntry.Set(new PdfName("TS"), new PdfReference(tsStream, 0));
        vriEntry.Set(new PdfName("TU"), new PdfString("D:20261016000000Z"));
        var vri = new PdfDictionary();
        vri.Set(new PdfName(new string('B', 40)), new PdfReference(vriEntryObj, 0));
        var dss = new PdfDictionary();
        dss.Set(PdfName.Type, PdfName.DSS);
        dss.Set(new PdfName("Certs"), new PdfArray(new PdfObject[] { new PdfReference(certStream, 0) }));
        dss.Set(new PdfName("VRI"), vri);

        byte[] modified = AppendObjects(certified,
            (certStream, StreamOf(new byte[] { 0x30, 0x82, 0x01, 0x00 })),
            (tsStream, StreamOf(new byte[] { 0x30, 0x80 })),
            (vriEntryObj, vriEntry),
            (dssObj, dss),
            (CatalogObjectNumber(certified), CatalogWith(certified, PdfName.DSS, new PdfReference(dssObj, 0))));

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.Equal(1, mdp.PermissionLevel);
        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));
        Assert.Contains("DSS", mdp.ModifiedObjectTypes); // the update was classified, not overlooked
    }

    /// <summary>
    /// A refresh that rewrites the certified revision's own /DSS object with more evidence — a common viewer's
    /// shape — is LTV: a dictionary at its position may change shape-for-shape, where a stream never may.
    /// </summary>
    [Fact]
    public void P1_CertifiedDssDictionaryRefreshedInPlace_IsWithinPermissions()
    {
        byte[] baseline = MinimalPdfBuilder.CreateMinimalPdf("P1 refresh");
        int certStream = MaxObjectNumber(baseline) + 1;
        int dssObj = certStream + 1;

        var before = new PdfDictionary();
        before.Set(new PdfName("Certs"), new PdfArray(new PdfObject[] { new PdfReference(certStream, 0) }));
        byte[] withDss = AppendObjects(baseline,
            (certStream, StreamOf(new byte[] { 0x30, 0x82 })),
            (dssObj, before),
            (CatalogObjectNumber(baseline), CatalogWith(baseline, PdfName.DSS, new PdfReference(dssObj, 0))));
        byte[] certified = Certify(withDss, "Cert", DocMdpPermission.NoChanges);
        int crlStream = MaxObjectNumber(certified) + 1;

        var after = new PdfDictionary();
        after.Set(new PdfName("Certs"), new PdfArray(new PdfObject[] { new PdfReference(certStream, 0) }));
        after.Set(new PdfName("CRLs"), new PdfArray(new PdfObject[] { new PdfReference(crlStream, 0) }));
        byte[] refreshed = AppendObjects(certified,
            (crlStream, StreamOf(new byte[] { 0x30, 0x83 })),
            (dssObj, after));

        var mdp = Analyze(refreshed);

        Assert.True(mdp.HasCertification);
        Assert.True(mdp.IsCompliant, "Errors: " + string.Join(", ", mdp.Violations));

        // The refresh took: the document's /DSS is the rewritten object, with the CRL the certified one lacked.
        using var s = new MemoryStream(refreshed);
        using var doc = PdfDocument.Open(s);
        var dssNow = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.DSS]));
        Assert.True(dssNow.ContainsKey(new PdfName("CRLs")));
    }

    // Each shape check at an LTV position has an attack that only it refuses.

    /// <summary>An evidence position admits a stream with a stream's keys alone — not one whose dictionary is an annotation's.</summary>
    [Fact]
    public void P2_AnnotationWrittenAsAStreamAndListedUnderDssCerts_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 stream annot"), "Cert", DocMdpPermission.FormsAndSignatures);
        int annotObj = MaxObjectNumber(certified) + 1;
        int dssObj = annotObj + 1;

        var annot = StreamOf(new byte[] { 0x30 });
        foreach (var kv in TextAnnotation()) annot.Set(kv.Key, kv.Value);
        var dss = new PdfDictionary();
        dss.Set(new PdfName("Certs"), new PdfArray(new PdfObject[] { new PdfReference(annotObj, 0) }));

        byte[] modified = AppendObjects(certified,
            (annotObj, annot),
            (PageObjectNumber(certified), PageWithAnnotation(certified, annotObj)),
            (dssObj, dss),
            (CatalogObjectNumber(certified), CatalogWith(certified, PdfName.DSS, new PdfReference(dssObj, 0))));

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("annotation"));
    }

    /// <summary>The <c>/VRI</c> position admits a dictionary keyed by digests alone — not a page tree, rewritten and pointed at.</summary>
    [Fact]
    public void P1_PageTreeRewrittenAndListedAsTheDssVri_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P1 vri pages"), "Cert", DocMdpPermission.NoChanges);
        const int pagesObj = 2; // MinimalPdfBuilder's page tree: a dictionary, not a stream, with a page tree's keys
        int dssObj = MaxObjectNumber(certified) + 1;

        var dss = new PdfDictionary();
        dss.Set(new PdfName("VRI"), new PdfReference(pagesObj, 0));

        byte[] modified = AppendObjects(certified,
            (pagesObj, ObjectWith(certified, pagesObj, PdfName.Rotate, new PdfNumber(90))),
            (dssObj, dss),
            (CatalogObjectNumber(certified), CatalogWith(certified, PdfName.DSS, new PdfReference(dssObj, 0))));

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.StartsWith($"obj {pagesObj}:", StringComparison.Ordinal));
    }

    /// <summary>The <c>/DSS</c> position admits a dictionary of Table 261's keys alone — not an annotation named as the DSS.</summary>
    [Fact]
    public void P2_AnnotationListedAsTheDssItself_ExceedsPermissions()
    {
        byte[] certified = Certify(MinimalPdfBuilder.CreateMinimalPdf("P2 dss annot"), "Cert", DocMdpPermission.FormsAndSignatures);
        int annotObj = MaxObjectNumber(certified) + 1;

        byte[] modified = AppendObjects(certified,
            (annotObj, TextAnnotation()),
            (PageObjectNumber(certified), PageWithAnnotation(certified, annotObj)),
            (CatalogObjectNumber(certified), CatalogWith(certified, PdfName.DSS, new PdfReference(annotObj, 0))));

        var mdp = Analyze(modified);

        Assert.True(mdp.HasCertification);
        Assert.False(mdp.IsCompliant);
        Assert.Contains(mdp.Violations, v => v.Contains("annotation"));
    }

    private static int HighestObjectNumber(byte[] pdf)
    {
        using var s = new MemoryStream(pdf);
        var parser = new PdfParser(s);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser.CrossReferenceTable.GetMaxObjectNumber();
    }

    private static DocMdpAnalysis Analyze(byte[] pdf)
    {
        using var s = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(s);
        return DocMdpAnalyzer.Analyze(doc, s.Length);
    }

    private static byte[] Certify(byte[] pdf, string field, DocMdpPermission perm)
        => Sign(pdf, field, perm);

    private static byte[] Sign(byte[] pdf, string field, DocMdpPermission perm = DocMdpPermission.None)
    {
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = pwd,
            SignatureFieldName = field,
            DocMdpPermission = perm,
        });
        return output.ToArray();
    }

    /// <summary>Appends a /Text comment annotation to the first page via a genuine incremental update.</summary>
    private static byte[] AppendTextAnnotation(byte[] pdf)
        => AppendPageEdit(pdf, (pageDict, annotObjNum) =>
        {
            var annots = new PdfArray();
            var existing = pageDict.GetArray(PdfName.Annots);
            if (existing != null) foreach (var a in existing) annots.Add(a);
            annots.Add(new PdfReference(annotObjNum, 0));

            var newPage = Clone(pageDict);
            newPage.Set(PdfName.Annots, annots);

            var annot = new PdfDictionary();
            annot.Set(PdfName.Type, PdfName.Annot);
            annot.Set(PdfName.Subtype, new PdfName("Text"));
            annot.Set(PdfName.Rect, new PdfArray(new PdfObject[]
                { new PdfNumber(10), new PdfNumber(10), new PdfNumber(30), new PdfNumber(30) }));
            annot.Set(PdfName.Contents, new PdfString("A comment"));
            return (newPage, annot);
        });

    /// <summary>Rotates the first page (a content/structure change) via an incremental update.</summary>
    private static byte[] AddPageRotation(byte[] pdf)
        => AppendPageEdit(pdf, (pageDict, _) =>
        {
            var newPage = Clone(pageDict);
            newPage.Set(PdfName.Rotate, new PdfNumber(90));
            return (newPage, null); // no extra object
        });

    private static PdfDictionary Clone(PdfDictionary src)
    {
        var d = new PdfDictionary();
        foreach (var kv in src) d.Set(kv.Key, kv.Value);
        return d;
    }

    /// <summary>
    /// Rewrites the first page object (and optionally adds one new object) as an
    /// incremental update appended to <paramref name="pdf"/>.
    /// </summary>
    private static byte[] AppendPageEdit(byte[] pdf,
        Func<PdfDictionary, int, (PdfDictionary newPage, PdfDictionary extraObj)> edit)
    {
        using var src = new MemoryStream(pdf);
        var parser = new PdfParser(src);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var trailer = parser.Trailer;
        int maxObj = parser.CrossReferenceTable.GetMaxObjectNumber();
        long prevXref = parser.StartXrefOffset;

        var root = parser.ResolveObject(trailer[PdfName.Root]) as PdfDictionary;
        var pages = parser.ResolveObject(root[PdfName.Pages]) as PdfDictionary;
        var pageRef = (PdfReference)pages.GetArray(PdfName.Kids)[0];
        int pageObjNum = pageRef.ReferenceObjectNumber;
        var pageDict = parser.ResolveObject(pageRef) as PdfDictionary;

        int extraObjNum = maxObj + 1;
        var (newPage, extraObj) = edit(pageDict, extraObjNum);

        using var outMs = new MemoryStream();
        outMs.Write(pdf, 0, pdf.Length);
        var writer = new PdfWriter(outMs);
        var xrefEntries = new List<(int, long)>();

        if (extraObj != null)
        {
            long extraOffset = outMs.Position;
            writer.WriteIndirectObject(extraObjNum, 0, extraObj);
            xrefEntries.Add((extraObjNum, extraOffset));
        }

        long pageOffset = outMs.Position;
        writer.WriteIndirectObject(pageObjNum, 0, newPage);
        xrefEntries.Add((pageObjNum, pageOffset));

        long xrefOffset = writer.WriteXrefTable(xrefEntries);

        var newTrailer = new PdfDictionary();
        foreach (var kv in trailer)
        {
            if (kv.Key == PdfName.Prev) continue;
            newTrailer.Set(kv.Key, kv.Value);
        }
        newTrailer.Set(PdfName.Size, new PdfNumber(extraObjNum + 1));
        newTrailer.Set(PdfName.Prev, new PdfNumber(prevXref));
        writer.WriteTrailer(newTrailer, xrefOffset);

        return outMs.ToArray();
    }

    /// <summary>Writes <paramref name="objects"/> as one incremental update appended to <paramref name="pdf"/>.</summary>
    private static byte[] AppendObjects(byte[] pdf, params (int objNum, PdfObject value)[] objects)
    {
        using var src = new MemoryStream(pdf);
        var parser = Parsed(src);
        var trailer = parser.Trailer;
        long prevXref = parser.StartXrefOffset;
        int maxObj = parser.CrossReferenceTable.GetMaxObjectNumber();

        using var outMs = new MemoryStream();
        outMs.Write(pdf, 0, pdf.Length);
        var writer = new PdfWriter(outMs);
        var xrefEntries = new List<(int, long)>();
        foreach (var (objNum, value) in objects)
        {
            xrefEntries.Add((objNum, writer.WriteIndirectObject(objNum, 0, value)));
            maxObj = Math.Max(maxObj, objNum);
        }
        long xrefOffset = writer.WriteXrefTable(xrefEntries);

        var newTrailer = new PdfDictionary();
        foreach (var kv in trailer)
        {
            if (kv.Key == PdfName.Prev) continue;
            newTrailer.Set(kv.Key, kv.Value);
        }
        newTrailer.Set(PdfName.Size, new PdfNumber(maxObj + 1));
        newTrailer.Set(PdfName.Prev, new PdfNumber(prevXref));
        writer.WriteTrailer(newTrailer, xrefOffset);

        return outMs.ToArray();
    }

    private static PdfStream StreamOf(byte[] bytes)
    {
        var stream = new PdfStream();
        stream.RawData = bytes;
        return stream;
    }

    private static PdfDictionary TextAnnotation()
    {
        var annot = new PdfDictionary();
        annot.Set(PdfName.Type, PdfName.Annot);
        annot.Set(PdfName.Subtype, new PdfName("Text"));
        annot.Set(PdfName.Rect, new PdfArray(new PdfObject[]
            { new PdfNumber(10), new PdfNumber(10), new PdfNumber(30), new PdfNumber(30) }));
        annot.Set(PdfName.Contents, new PdfString("A comment"));
        return annot;
    }

    private static int CatalogObjectNumber(byte[] pdf)
    {
        using var src = new MemoryStream(pdf);
        return ((PdfReference)Parsed(src).Trailer[PdfName.Root]).ReferenceObjectNumber;
    }

    /// <summary>The document's catalog as it stands, with <paramref name="key"/> set.</summary>
    private static PdfDictionary CatalogWith(byte[] pdf, PdfName key, PdfObject value)
    {
        using var src = new MemoryStream(pdf);
        var parser = Parsed(src);
        var catalog = Clone((PdfDictionary)parser.ResolveObject(parser.Trailer[PdfName.Root]));
        catalog.Set(key, value);
        return catalog;
    }

    /// <summary>Object <paramref name="objNum"/> as it stands — a dictionary — with <paramref name="key"/> set.</summary>
    private static PdfDictionary ObjectWith(byte[] pdf, int objNum, PdfName key, PdfObject value)
    {
        using var src = new MemoryStream(pdf);
        var dict = Clone((PdfDictionary)Parsed(src).GetObject(objNum));
        dict.Set(key, value);
        return dict;
    }

    private static int PageObjectNumber(byte[] pdf)
    {
        using var src = new MemoryStream(pdf);
        return FirstPageReference(Parsed(src)).ReferenceObjectNumber;
    }

    /// <summary>The first page as it stands, with <paramref name="annotObjNum"/> added to its <c>/Annots</c>.</summary>
    private static PdfDictionary PageWithAnnotation(byte[] pdf, int annotObjNum)
    {
        using var src = new MemoryStream(pdf);
        var parser = Parsed(src);
        var page = Clone((PdfDictionary)parser.ResolveObject(FirstPageReference(parser)));
        var annots = new PdfArray();
        var existing = parser.ResolveObject(page[PdfName.Annots]) as PdfArray;
        if (existing != null) foreach (var a in existing) annots.Add(a);
        annots.Add(new PdfReference(annotObjNum, 0));
        page.Set(PdfName.Annots, annots);
        return page;
    }

    private static PdfReference FirstPageReference(PdfParser parser)
    {
        var root = (PdfDictionary)parser.ResolveObject(parser.Trailer[PdfName.Root]);
        var pages = (PdfDictionary)parser.ResolveObject(root[PdfName.Pages]);
        return (PdfReference)((PdfArray)parser.ResolveObject(pages[PdfName.Kids]))[0];
    }

    private static PdfParser Parsed(MemoryStream src)
    {
        var parser = new PdfParser(src);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    private static int MaxObjectNumber(byte[] pdf)
    {
        using var src = new MemoryStream(pdf);
        var parser = new PdfParser(src);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser.CrossReferenceTable.GetMaxObjectNumber();
    }

    /// <summary>
    /// Appends an incremental update whose only content is a cross-reference section marking
    /// <paramref name="objNum"/> <b>free</b> — i.e. a deletion, written the way a real editor
    /// writes one. Nothing else changes, so the analyzer sees a revision with no rewritten object
    /// at all: a shape an analyzer reading objects alone would classify as compliant.
    /// </summary>
    private static byte[] AppendObjectDeletion(byte[] pdf, int objNum)
    {
        using var src = new MemoryStream(pdf);
        var parser = new PdfParser(src);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var trailer = parser.Trailer;
        long prevXref = parser.StartXrefOffset;
        int size = parser.CrossReferenceTable.GetMaxObjectNumber() + 1;

        using var outMs = new MemoryStream();
        outMs.Write(pdf, 0, pdf.Length);

        // A free entry cannot be expressed through PdfWriter.WriteXrefTable, which writes 'n'
        // rows from (objNum, offset) pairs — so the section is written by hand. Two subsections:
        // object 0 (the free-list head, mandatory in a classic table) and the freed object.
        long xrefOffset = outMs.Position;
        byte[] section = Encoding.ASCII.GetBytes(
            "xref\n0 1\n0000000000 65535 f \n" +
            $"{objNum} 1\n0000000000 00001 f \n");
        outMs.Write(section, 0, section.Length);

        var writer = new PdfWriter(outMs);
        var newTrailer = new PdfDictionary();
        foreach (var kv in trailer)
        {
            if (kv.Key == PdfName.Prev) continue;
            newTrailer.Set(kv.Key, kv.Value);
        }
        newTrailer.Set(PdfName.Size, new PdfNumber(size));
        newTrailer.Set(PdfName.Prev, new PdfNumber(prevXref));
        writer.WriteTrailer(newTrailer, xrefOffset);

        return outMs.ToArray();
    }

    /// <summary>
    /// Writes <paramref name="value"/> as object <paramref name="objNum"/> in a genuine
    /// incremental update appended to <paramref name="pdf"/>. Unlike <see cref="AppendPageEdit"/>
    /// this takes any <see cref="PdfObject"/>, so a revision whose changed object is *not* a
    /// dictionary can be produced.
    /// </summary>
    private static byte[] AppendIndirectObject(byte[] pdf, int objNum, PdfObject value)
    {
        using var src = new MemoryStream(pdf);
        var parser = new PdfParser(src);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var trailer = parser.Trailer;
        long prevXref = parser.StartXrefOffset;
        int maxObj = parser.CrossReferenceTable.GetMaxObjectNumber();

        using var outMs = new MemoryStream();
        outMs.Write(pdf, 0, pdf.Length);
        var writer = new PdfWriter(outMs);

        long offset = outMs.Position;
        writer.WriteIndirectObject(objNum, 0, value);
        long xrefOffset = writer.WriteXrefTable(new List<(int, long)> { (objNum, offset) });

        var newTrailer = new PdfDictionary();
        foreach (var kv in trailer)
        {
            if (kv.Key == PdfName.Prev) continue;
            newTrailer.Set(kv.Key, kv.Value);
        }
        newTrailer.Set(PdfName.Size, new PdfNumber(Math.Max(maxObj, objNum) + 1));
        newTrailer.Set(PdfName.Prev, new PdfNumber(prevXref));
        writer.WriteTrailer(newTrailer, xrefOffset);

        return outMs.ToArray();
    }

    /// <summary>Overwrites the certification signature's /ByteRange so it points out of bounds,
    /// keeping byte length identical so all other offsets stay valid.</summary>
    private static byte[] CorruptCertByteRange(byte[] pdf)
    {
        byte[] needle = Encoding.ASCII.GetBytes("/ByteRange [");
        int start = IndexOf(pdf, needle, 0);
        Assert.True(start >= 0, "ByteRange not found");
        int open = start + needle.Length - 1;            // position of '['
        int close = IndexOf(pdf, new byte[] { (byte)']' }, open);
        Assert.True(close > open, "ByteRange close bracket not found");

        int innerLen = close - open - 1;                  // bytes between [ and ]
        // off1=0, len1=0, off2=0, len2 = a run of 9s → off2+len2 far past EOF, so the analyzer
        // must take the bounds-error path. Sized to occupy the slot exactly (offsets unchanged).
        const string head = "0 0 0 ";
        int digits = innerLen - head.Length;
        Assert.True(digits >= 4, $"ByteRange slot too small ({innerLen})");
        byte[] repl = Encoding.ASCII.GetBytes(head + new string('9', digits));
        Assert.True(repl.Length == innerLen, "replacement does not fit");

        byte[] copy = (byte[])pdf.Clone();
        Array.Copy(repl, 0, copy, open + 1, innerLen);
        return copy;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (int i = from; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
