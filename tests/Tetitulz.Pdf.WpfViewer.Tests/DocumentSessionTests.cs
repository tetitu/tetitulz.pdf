// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.WpfViewer.Models;
using Tetitulz.Pdf.WpfViewer.Services;

namespace Tetitulz.Pdf.WpfViewer.Tests;

/// <summary>
/// The viewer's edit session: every edit an undo step, edits that cannot overwrite each other, undo
/// bounded by bytes, and a save that marks only what it wrote.
/// </summary>
public sealed class DocumentSessionTests
{
    private static readonly PdfService Pdf = new();

    private static DocumentSession Open(int pages = 3, long budget = DocumentSession.DefaultUndoBudgetBytes)
    {
        var session = new DocumentSession(budget);
        session.Open(PdfService.OpenFromBytes(MinimalPdfBuilder.CreateMultiPagePdf(pages), "doc.pdf", "doc.pdf"), null);
        return session;
    }

    private static int[] Rotations(DocumentSession s) => PdfService.PageRotations(s.Current!);

    [Fact]
    public async Task Every_edit_is_a_named_step_that_undo_takes_back_and_redo_does_again()
    {
        using var s = Open();
        Assert.False(s.IsModified);
        Assert.True(await s.MutateAsync("Rotate page", d => Pdf.RotatePage(d, 0, 90)));
        Assert.True(await s.MutateAsync("Rotate page 2", d => Pdf.RotatePage(d, 1, 180)));
        Assert.True(s.IsModified);
        Assert.Equal(new[] { "Rotate page 2", "Rotate page" }, s.UndoLabels);
        Assert.Equal(new[] { 90, 180, 0 }, Rotations(s));

        Assert.Equal("Rotate page 2", await s.UndoAsync());
        Assert.Equal(new[] { 90, 0, 0 }, Rotations(s));
        Assert.Equal(new[] { "Rotate page 2" }, s.RedoLabels);

        Assert.Equal("Rotate page 2", await s.RedoAsync());
        Assert.Equal(new[] { 90, 180, 0 }, Rotations(s));
        Assert.Empty(s.RedoLabels);

        // A new edit after an undo forgets what could be redone.
        await s.UndoAsync();
        await s.MutateAsync("Delete page", d => Pdf.DeletePage(d, 2));
        Assert.Empty(s.RedoLabels);
        Assert.Equal(2, s.Current!.PageCount);
    }

    [Fact]
    public async Task Two_edits_started_together_run_one_after_the_other_and_both_count()
    {
        using var s = Open();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();

        Task<bool> first = s.MutateAsync("Turn page 1", d =>
        {
            firstStarted.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(30));
            return Pdf.RotatePage(d, 0, 90);
        });
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(30)));
        // The second edit starts while the first is running; it must read the first's result.
        Task<bool> second = s.MutateAsync("Turn page 2", d => Pdf.RotatePage(d, 1, 270));
        Assert.False(second.IsCompleted);
        releaseFirst.Set();
        Assert.True(await first);
        Assert.True(await second);

        Assert.Equal(new[] { 90, 270, 0 }, Rotations(s));
        Assert.Equal(new[] { "Turn page 2", "Turn page 1" }, s.UndoLabels);
    }

    [Fact]
    public async Task Undo_keeps_the_newest_steps_within_its_byte_budget()
    {
        byte[] one = MinimalPdfBuilder.CreateMultiPagePdf(3);
        // Room for about two copies of the document: the oldest steps go first.
        using var s = Open(budget: one.Length * 2 + one.Length / 2);
        for (int i = 0; i < 6; i++)
        {
            int page = i % 3;
            await s.MutateAsync($"Step {i}", d => Pdf.RotatePage(d, page, (i + 1) * 90 % 360));
        }
        Assert.InRange(s.UndoLabels.Count, 1, 3);
        Assert.Equal("Step 5", s.UndoLabels[0]);
        Assert.True(s.UndoBytes <= s.UndoBudgetBytes);
    }

    [Fact]
    public async Task The_newest_step_is_kept_whatever_it_costs()
    {
        using var s = Open(budget: 1);
        await s.MutateAsync("Rotate", d => Pdf.RotatePage(d, 0, 90));
        Assert.Equal(new[] { "Rotate" }, s.UndoLabels);
        Assert.Equal("Rotate", await s.UndoAsync());
    }

    [Fact]
    public async Task Values_typed_on_the_page_become_their_own_step_before_the_edit()
    {
        using var s = Open();
        byte[] typed = Pdf.RotatePage(s.Current!, 2, 180).Bytes;   // stands for what the page's form returned
        await s.MutateAsync("Turn page 1", d => Pdf.RotatePage(d, 0, 90), pendingEdits: () => typed);

        Assert.Equal(new[] { "Turn page 1", "Form values" }, s.UndoLabels);
        Assert.Equal(new[] { 90, 0, 180 }, Rotations(s));
        await s.UndoAsync();
        Assert.Equal(new[] { 0, 0, 180 }, Rotations(s));
    }

    [Fact]
    public async Task An_edit_that_returns_the_document_unchanged_is_no_step()
    {
        using var s = Open();
        var (done, result) = await s.MutateAsync<int>("Look", d => (d, 42));
        Assert.True(done);
        Assert.Equal(42, result);
        Assert.Empty(s.UndoLabels);
        Assert.False(s.IsModified);
    }

    [Fact]
    public async Task A_failed_edit_leaves_the_document_and_history_as_they_were()
    {
        using var s = Open();
        PdfDocumentModel before = s.Current!;
        await Assert.ThrowsAnyAsync<Exception>(() => s.MutateAsync("Bad", d => Pdf.RotatePage(d, 99, 90)));
        Assert.Same(before, s.Current);
        Assert.Empty(s.UndoLabels);
        // The gate was released: the next edit runs.
        Assert.True(await s.MutateAsync("Rotate", d => Pdf.RotatePage(d, 0, 90)));
    }

    [Fact]
    public async Task Removing_the_password_is_a_step_undo_takes_back()
    {
        var s = new DocumentSession();
        PdfDocumentModel doc = PdfService.OpenFromBytes(MinimalPdfBuilder.CreateMinimalPdf(), "p.pdf", "p.pdf");
        var protection = new OpenedProtection(new byte[] { 1 }, "pw", doc.Bytes);
        s.Open(doc, protection);

        Assert.True(await s.RemoveProtectionAsync("Remove password"));
        Assert.Null(s.Protection);
        Assert.True(s.IsModified);
        await s.UndoAsync();
        Assert.Same(protection, s.Protection);
    }

    [Fact]
    public async Task A_save_marks_only_the_document_it_wrote()
    {
        using var s = Open();
        await s.MutateAsync("Rotate", d => Pdf.RotatePage(d, 0, 90));
        PdfDocumentModel written = s.Current!;
        await s.MutateAsync("Rotate again", d => Pdf.RotatePage(d, 1, 90));

        await s.MarkSavedAsync(written);           // an edit ran after the write: still unsaved
        Assert.True(s.IsModified);

        await s.MarkSavedAsync(s.Current!);
        Assert.False(s.IsModified);
        Assert.Equal(2, s.UndoLabels.Count);       // saving keeps the history
    }
}
