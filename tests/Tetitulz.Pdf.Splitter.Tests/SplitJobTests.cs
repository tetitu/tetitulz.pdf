// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Splitter;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Splitter.Tests;

/// <summary>The splitter's planning and writing, without its window.</summary>
public sealed class SplitJobTests
{
    static SplitJobTests() => PdfEncryptionOperations.Register();

    private static readonly byte[] TenPages = MinimalPdfBuilder.CreateMultiPagePdf(10);

    private static byte[] Encrypt(byte[] pdf, string userPassword)
    {
        using var input = new MemoryStream(pdf, false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = userPassword,
            OwnerPassword = "owner-secret",
            Algorithm = EncryptionAlgorithm.AES256,
        });
        return output.ToArray();
    }

    /// <summary>Each page's text, in page order — "Page 3" for the source's third page.</summary>
    private static string[] PageTexts(byte[] pdf, string password = null)
    {
        byte[] plain = pdf;
        if (password != null)
        {
            using var input = new MemoryStream(pdf, false);
            using var output = new MemoryStream();
            PdfDecryptor.Decrypt(input, output, password);
            plain = output.ToArray();
        }
        using var ms = new MemoryStream(plain, false);
        using var doc = PdfDocument.Open(ms);
        return doc.Pages.Select(p => PdfTextExtractor.ExtractText(p).Trim()).ToArray();
    }

    private static byte[] Write(byte[] source, string password, params int[] pages)
    {
        using var output = new MemoryStream();
        SplitOutcome outcome = SplitJob.Write(source, password, pages, output);
        Assert.Null(outcome.Error);
        return output.ToArray();
    }

    [Fact]
    public void Writes_the_pages_in_the_order_given()
    {
        byte[] extract = Write(TenPages, null, 7, 2, 9);
        Assert.Equal(new[] { "Page 7", "Page 2", "Page 9" }, PageTexts(extract));
    }

    [Fact]
    public void An_extract_of_an_encrypted_document_is_protected_by_its_password()
    {
        byte[] encrypted = Encrypt(TenPages, "open-me");
        using var output = new MemoryStream();
        SplitOutcome outcome = SplitJob.Write(encrypted, "open-me", new[] { 4, 5 }, output);
        Assert.Null(outcome.Error);
        Assert.True(outcome.Protected);

        byte[] extract = output.ToArray();
        using (var ms = new MemoryStream(extract, false))
        using (var doc = PdfDocument.Open(ms))
            Assert.True(doc.IsEncrypted);
        Assert.Equal(new[] { "Page 4", "Page 5" }, PageTexts(extract, "open-me"));
        // The password is what opens it: the empty one does not.
        Assert.ThrowsAny<Exception>(() => PageTexts(extract, ""));
    }

    [Fact]
    public void An_unencrypted_extract_is_not_reported_protected()
    {
        using var output = new MemoryStream();
        SplitOutcome outcome = SplitJob.Write(TenPages, null!, new[] { 1 }, output);
        Assert.Null(outcome.Error);
        Assert.False(outcome.Protected);
        using var ms = new MemoryStream(output.ToArray(), false);
        using var doc = PdfDocument.Open(ms);
        Assert.False(doc.IsEncrypted);
    }

    [Fact]
    public void A_refused_extract_writes_nothing_and_says_why()
    {
        using var output = new MemoryStream();
        SplitOutcome outcome = SplitJob.Write(TenPages, null!, new[] { 2, 2 }, output);   // a page twice
        Assert.NotNull(outcome.Error);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Every_N_pages_plans_the_whole_document_in_runs()
    {
        var plan = SplitJob.Plan(TenPages, null!, SplitMode.EveryNPages, Array.Empty<int>(),
            Array.Empty<IReadOnlyList<int>>(), 4, out _);
        Assert.Equal(new[] { new[] { 1, 2, 3, 4 }, new[] { 5, 6, 7, 8 }, new[] { 9, 10 } },
            plan.Select(c => c.ToArray()).ToArray());
    }

    [Fact]
    public void Every_N_pages_plans_an_encrypted_document_from_its_plaintext()
    {
        byte[] encrypted = Encrypt(TenPages, "open-me");
        var plan = SplitJob.Plan(encrypted, "open-me", SplitMode.EveryNPages, Array.Empty<int>(),
            Array.Empty<IReadOnlyList<int>>(), 5, out _);
        Assert.Equal(2, plan.Count);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, plan[1]);
    }

    [Fact]
    public void Under_a_megabyte_a_small_document_is_one_file()
    {
        var plan = SplitJob.Plan(TenPages, null!, SplitMode.UnderMegabytes, Array.Empty<int>(),
            Array.Empty<IReadOnlyList<int>>(), 1, out _);
        Assert.Single(plan);
        Assert.Equal(Enumerable.Range(1, 10), plan[0]);
    }

    [Fact]
    public void Per_group_and_selection_plans_are_what_the_range_box_said()
    {
        Assert.True(PageRangeText.TryParseGroups("1-2, 9", 10, out var groups, out _));
        var perGroup = SplitJob.Plan(TenPages, null!, SplitMode.PerGroup, Array.Empty<int>(), groups, 0, out _);
        Assert.Equal(2, perGroup.Count);
        Assert.Equal(new[] { 9 }, perGroup[1]);

        var selection = SplitJob.Plan(TenPages, null!, SplitMode.Selection, new[] { 3, 4 },
            Array.Empty<IReadOnlyList<int>>(), 0, out _);
        Assert.Equal(new[] { 3, 4 }, Assert.Single(selection));

        Assert.Empty(SplitJob.Plan(TenPages, null!, SplitMode.Selection, Array.Empty<int>(),
            Array.Empty<IReadOnlyList<int>>(), 0, out _));
    }

    [Fact]
    public void Part_names_number_each_file_and_name_its_pages()
    {
        string[] paths = SplitJob.PartPaths(Path.Combine("out", "report.pdf"),
            new IReadOnlyList<int>[] { new[] { 1, 2, 3 }, new[] { 7 } });
        Assert.Equal(new[]
        {
            Path.Combine("out", "report_part01_pages_1-3.pdf"),
            Path.Combine("out", "report_part02_pages_7.pdf"),
        }, paths);
    }

    [Fact]
    public void A_long_page_list_is_cut_in_the_file_name()
    {
        string name = SplitJob.FileNameFor("doc", Enumerable.Range(1, 200).Where(p => p % 2 == 1));
        Assert.EndsWith("-etc.pdf", name, StringComparison.Ordinal);
        Assert.True(name.Length < 80, name);
    }
}
