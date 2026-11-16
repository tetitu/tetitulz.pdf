// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Reads the AcroForm of every PDF in the developer-local corpus and asserts the forms counterpart
/// of the render and extraction smoke tests: a document whose catalog declares
/// <c>/AcroForm /Fields</c> must yield fields from <see cref="PdfFormReader.GetFields(Stream)"/>.
/// </summary>
/// <remarks>
/// <para><b>The correlation is the assertion.</b> An empty field list is a valid answer for the
/// overwhelming majority of PDFs, so "GetFields returned a list" passes with the reader deleted.
/// Reading <c>/AcroForm /Fields</c> straight out of the catalog gives an independent second opinion
/// on whether there was anything to find, which is what makes a zero result falsifiable.</para>
/// <para><b>The two counts are deliberately compared with <c>&gt;= 1</c>, not for equality.</b>
/// <c>/Fields</c> holds the roots of a field <i>tree</i>: one root with five <c>/Kids</c> is one
/// entry and five terminal fields, and a field split across pages is several widgets under one
/// name. Asserting equality would fail on correct documents and teach the next reader to relax the
/// test rather than trust it. The claim being made is only "the reader found the form" — which is
/// exactly the regression that matters, because a reader that silently finds nothing is
/// indistinguishable from a document with no form.</para>
/// <para><b>What this cannot say:</b> nothing about whether a field's <i>value</i> is right, whether
/// its type was classified correctly, or whether an RTL value round-trips. Those need reference
/// data this repository does not have.</para>
/// </remarks>
public sealed class CorpusFormSmokeTests
{
    private readonly ITestOutputHelper _out;

    public CorpusFormSmokeTests(ITestOutputHelper output) => _out = output;

    [CorpusFact]
    public void EveryCorpusDocumentWithAnAcroForm_YieldsFieldsInsteadOfSilentlyFindingNone()
    {
        IReadOnlyList<string> pdfs = TestCorpus.Pdfs();
        Assert.NotEmpty(pdfs);

        var missed = new List<string>();
        int withAcroForm = 0, fieldsFound = 0;

        foreach (string path in pdfs)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { continue; }

            int declaredRoots;
            try { declaredRoots = CountDeclaredFieldRoots(bytes); }
            catch (Exception ex)
            {
                _out.WriteLine($"skipped (cannot open): {Path.GetFileName(path)} — {ex.Message}");
                continue;
            }

            if (declaredRoots <= 0) continue;
            withAcroForm++;

            IReadOnlyList<PdfFormFieldInfo> fields;
            using (var ms = new MemoryStream(bytes, writable: false))
            {
                // Reading a form must never escape: a malformed AcroForm degrades, it does not throw.
                fields = PdfFormReader.GetFields(ms);
            }

            Assert.NotNull(fields);
            fieldsFound += fields.Count;

            if (fields.Count == 0)
                missed.Add($"{Path.GetFileName(path)} (/AcroForm declares {declaredRoots} field root(s))");
        }

        _out.WriteLine(
            $"{pdfs.Count} documents, {withAcroForm} with an AcroForm, {fieldsFound} fields read.");

        Assert.True(missed.Count == 0,
            "These documents declare AcroForm fields but the reader returned none, so their form " +
            "was silently missed:\n  " + string.Join("\n  ", missed));

        // A corpus with no form at all satisfies the assertion above vacuously. That is a fact
        // about the corpus rather than a defect, so it must not fail -- and this xunit has no
        // runtime skip. Non-vacuity is therefore guaranteed by
        // TheReader_FindsAFormItJustWrote_SoAZeroResultAboveIsMeaningful, which always runs, and
        // the count is written to the output so a vacuous run is visible rather than silent.
        if (withAcroForm == 0)
            _out.WriteLine(
                "NOTE: no document in this corpus declares an AcroForm, so the assertion above " +
                "proved nothing about real files. Add a form-bearing PDF to give it something " +
                "to check.");
    }

    /// <summary>
    /// Every field the reader returns must carry a name, and its type must be a defined enum value.
    /// A nameless or <c>(PdfFieldType)47</c> field is unusable by a caller and points at a parse
    /// that half-succeeded.
    /// </summary>
    [CorpusFact]
    public void EveryFieldReadFromTheCorpus_HasANameAndADefinedType()
    {
        var bad = new List<string>();
        int checkedFields = 0;

        foreach (string path in TestCorpus.Pdfs())
        {
            IReadOnlyList<PdfFormFieldInfo> fields;
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path), writable: false);
                fields = PdfFormReader.GetFields(ms);
            }
            catch (Exception) { continue; }

            foreach (PdfFormFieldInfo f in fields ?? Array.Empty<PdfFormFieldInfo>())
            {
                checkedFields++;

                if (string.IsNullOrEmpty(f.FullyQualifiedName))
                    bad.Add($"{Path.GetFileName(path)}: a field has no fully-qualified name");

                if (!Enum.IsDefined(typeof(PdfFieldType), f.FieldType))
                    bad.Add($"{Path.GetFileName(path)}: field '{f.FullyQualifiedName}' has " +
                            $"undefined type {(int)f.FieldType}");
            }
        }

        _out.WriteLine($"{checkedFields} fields checked.");

        Assert.True(bad.Count == 0, string.Join("\n  ", bad));
        if (checkedFields == 0)
            _out.WriteLine("NOTE: no field was read from the corpus, so this proved nothing " +
                           "about real files.");
    }

    /// <summary>
    /// Guards the guard: the reader really does find a form when one is present, so a zero result
    /// over the corpus means "no form there" rather than "the reader is broken".
    /// </summary>
    /// <remarks>
    /// <para>This runs <b>without</b> a corpus, on purpose. The corpus tests above cannot protect
    /// their own instrument: on a clean checkout they skip, and on a corpus that happens to contain
    /// no AcroForm they pass while proving nothing. Something that always runs has to carry the
    /// claim that a zero is informative.</para>
    /// <para>The fixture is a hand-built PDF rather than one produced by this library's own form
    /// writer: writing and reading with the same code could agree on a shared misunderstanding.
    /// It is a narrower claim than reading a third-party form — the corpus test is what widens
    /// it — but it is enough to falsify "the reader returns nothing for everything", which is the
    /// failure mode that would make the corpus assertion vacuous.</para>
    /// </remarks>
    [Fact]
    public void TheReader_FindsAFormItJustWrote_SoAZeroResultAboveIsMeaningful()
    {
        byte[] withForm = BuildSingleTextFieldPdf();

        // Control: the catalog really declares the form, so the two opinions are independent.
        Assert.True(CountDeclaredFieldRoots(withForm) > 0);

        using var ms = new MemoryStream(withForm, writable: false);
        IReadOnlyList<PdfFormFieldInfo> fields = PdfFormReader.GetFields(ms);

        PdfFormFieldInfo only = Assert.Single(fields);
        Assert.Equal("Amount", only.FullyQualifiedName);
        Assert.Equal(PdfFieldType.Text, only.FieldType);
    }

    /// <summary>A document with no AcroForm yields no fields — the other half of the claim.</summary>
    [Fact]
    public void TheReader_ReturnsNoFieldsForADocumentWithNoForm()
    {
        byte[] noForm = BuildSingleTextFieldPdf(includeAcroForm: false);

        Assert.Equal(0, CountDeclaredFieldRoots(noForm));

        using var ms = new MemoryStream(noForm, writable: false);
        Assert.Empty(PdfFormReader.GetFields(ms));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The number of entries in <c>/AcroForm /Fields</c>, read directly from the catalog — the
    /// independent second opinion the assertion above rests on.
    /// </summary>
    /// <remarks>
    /// Encrypted documents return 0: <c>PdfDocument.Open</c> loads no catalog for one, so "no
    /// AcroForm" would be a statement about the encryption rather than the document. Excluding them
    /// keeps the count honest.
    /// </remarks>
    private static int CountDeclaredFieldRoots(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);

        if (doc.IsEncrypted) return 0;

        if (doc.Catalog?.Dictionary is not PdfDictionary catalog) return 0;
        if (doc.ResolveObject(catalog[PdfName.AcroForm]) is not PdfDictionary acro) return 0;
        if (doc.ResolveObject(acro[PdfName.Fields]) is not PdfArray fields) return 0;

        return fields.Count;
    }

    /// <summary>
    /// A minimal but real PDF carrying one text field named <c>Amount</c>: catalog →
    /// <c>/AcroForm</c> → one field root, which is also the page's widget annotation — the merged
    /// field/widget shape §12.7.3.1 permits and most producers emit.
    /// </summary>
    private static byte[] BuildSingleTextFieldPdf(bool includeAcroForm = true)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();

        sb.Append("%PDF-1.7\n");

        offsets.Add(sb.Length);
        sb.Append(includeAcroForm
            ? "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n"
            : "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Annots [4 0 R] >>\nendobj\n");

        // Merged field + widget: /FT /Tx makes it a terminal text field and /T names it.
        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (Amount) /V (0) " +
                  "/Rect [72 700 272 724] /P 3 0 R >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
