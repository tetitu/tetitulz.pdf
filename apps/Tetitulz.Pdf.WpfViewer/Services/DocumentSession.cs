// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.WpfViewer.Models;

namespace Tetitulz.Pdf.WpfViewer.Services;

/// <summary>
/// The open document and its history: the current version, the protection it was opened under,
/// every edit as a named undo step, and one gate so that no two edits run at once.
/// </summary>
/// <remarks>
/// <para><b>One edit at a time.</b> An edit takes the gate, reads the current document, runs off
/// the calling thread and replaces the document only if it is still the one it read. Two commands
/// started together therefore run one after the other, each on the other's result, instead of both
/// reading the same document and the later one discarding the earlier one's change.</para>
/// <para><b>Undo is bounded by the bytes it holds</b> (<see cref="UndoBudgetBytes"/>), the oldest
/// step dropped first; the most recent step is always kept, whatever its size.</para>
/// <para>A step holds bytes, not a parsed document: undoing parses the bytes again, so a history
/// costs its files' sizes and nothing more.</para>
/// </remarks>
public sealed class DocumentSession : IDisposable
{
    /// <summary>Undo's ceiling on the bytes its steps hold together: 256 MB.</summary>
    public const long DefaultUndoBudgetBytes = 256L * 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<Step> _undo = new();
    private readonly Stack<Step> _redo = new();

    /// <summary>One version of the document, and the name of the edit that left it.</summary>
    private sealed record Step(string Label, byte[] Bytes, byte[]? ShownBytes, OpenedProtection? Protection)
    {
        public long Size => Bytes.LongLength + (ShownBytes != null && !ReferenceEquals(ShownBytes, Bytes) ? ShownBytes.LongLength : 0);
    }

    public DocumentSession(long undoBudgetBytes = DefaultUndoBudgetBytes)
    {
        if (undoBudgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(undoBudgetBytes));
        UndoBudgetBytes = undoBudgetBytes;
    }

    /// <summary>The most bytes the undo steps hold together.</summary>
    public long UndoBudgetBytes { get; }

    /// <summary>The document as it is now; null when none is open.</summary>
    public PdfDocumentModel? Current { get; private set; }

    /// <summary>
    /// Set while the document was opened under a password and keeps it: a save puts the protection
    /// back (<see cref="ProtectedDocuments.ProtectForSave"/>), and signatures are checked against the file as
    /// it was opened.
    /// </summary>
    public OpenedProtection? Protection { get; private set; }

    /// <summary>True when the document has changed since it was opened or last saved.</summary>
    public bool IsModified { get; private set; }

    /// <summary>The undo steps' names, the most recent first.</summary>
    public IReadOnlyList<string> UndoLabels => _undo.Select(s => s.Label).ToList();

    /// <summary>The redo steps' names, the next first.</summary>
    public IReadOnlyList<string> RedoLabels => _redo.Select(s => s.Label).ToList();

    /// <summary>The bytes the undo steps hold together.</summary>
    public long UndoBytes => _undo.Sum(s => s.Size);

    /// <summary>Raised on the calling thread after the document changes: an edit, an undo, a redo, an open.</summary>
    public event Action? Changed;

    /// <summary>Makes <paramref name="document"/> the open one, with no history; unmodified unless <paramref name="modified"/>.</summary>
    public void Open(PdfDocumentModel document, OpenedProtection? protection, bool modified = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        Replace(document);
        Protection = protection;
        IsModified = modified;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Closes the document and forgets its history.</summary>
    public void Close()
    {
        Replace(null);
        Protection = null;
        IsModified = false;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Records that <paramref name="written"/> has been written: as <paramref name="saved"/> when the
    /// write moved it (a new path, or a pending encryption now the file's own), with the protection
    /// it now has. The history stays. Nothing changes when the document is no longer
    /// <paramref name="written"/> — an edit that ran meanwhile is not saved.
    /// </summary>
    public async Task MarkSavedAsync(PdfDocumentModel written, PdfDocumentModel? saved = null, OpenedProtection? protection = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (Current == null || !ReferenceEquals(Current, written)) return;
            if (saved != null)
            {
                Replace(saved);
                Protection = protection;
            }
            IsModified = false;
            Changed?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> on the document as it is now, inside the gate, so no edit
    /// replaces it meanwhile; the default when no document is open.
    /// </summary>
    public async Task<T?> ReadAsync<T>(Func<PdfDocumentModel, OpenedProtection?, Task<T>> read)
    {
        await _gate.WaitAsync();
        try
        {
            return Current == null ? default : await read(Current, Protection);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one edit. <paramref name="pendingEdits"/>, when given, is asked first, inside the gate
    /// and on the calling thread, for edits made outside the session (form values typed on the page):
    /// bytes it returns become a step of their own, named <paramref name="pendingLabel"/>, before the
    /// edit runs on them. False when no document is open.
    /// </summary>
    public async Task<bool> MutateAsync(string label, Func<PdfDocumentModel, PdfDocumentModel> edit,
        Func<byte[]?>? pendingEdits = null, string pendingLabel = "Form values")
    {
        var (done, _) = await MutateAsync<object?>(label, d => (edit(d), null), pendingEdits, pendingLabel);
        return done;
    }

    /// <summary>
    /// Runs one edit that also reports something (an operation's own result). The result is the
    /// default when no document is open.
    /// </summary>
    public async Task<(bool Done, T? Result)> MutateAsync<T>(string label,
        Func<PdfDocumentModel, (PdfDocumentModel Document, T Result)> edit,
        Func<byte[]?>? pendingEdits = null, string pendingLabel = "Form values")
    {
        ArgumentNullException.ThrowIfNull(edit);
        await _gate.WaitAsync();
        try
        {
            if (Current == null) return (false, default);
            if (pendingEdits?.Invoke() is byte[] typed) Commit(pendingLabel, PdfDocumentModel.FromBytes(typed, Current.FileName, Current.FilePath), Protection);

            PdfDocumentModel before = Current;
            var (after, result) = await Task.Run(() => edit(before));
            // Inside the gate nothing else edits, so this holds; it is checked rather than assumed,
            // because a document replaced under an edit would lose the replacement silently.
            if (!ReferenceEquals(Current, before))
                throw new InvalidOperationException("The document changed while an edit was running.");
            if (!ReferenceEquals(after, before)) Commit(label, after, Protection);
            return (true, result);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the password protection the document was opened under, as a step undo can take back:
    /// a save then writes the document in the clear.
    /// </summary>
    public async Task<bool> RemoveProtectionAsync(string label)
    {
        await _gate.WaitAsync();
        try
        {
            if (Current == null || Protection == null) return false;
            PushUndo(Snapshot(label));
            _redo.Clear();
            Protection = null;
            IsModified = true;
            Changed?.Invoke();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Folds edits made outside the session into it as a step, inside the gate; false when there were none.</summary>
    public async Task<bool> FoldAsync(Func<byte[]?> pendingEdits, string label = "Form values")
    {
        await _gate.WaitAsync();
        try
        {
            if (Current == null || pendingEdits() is not byte[] typed) return false;
            Commit(label, PdfDocumentModel.FromBytes(typed, Current.FileName, Current.FilePath), Protection);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Takes back the latest step; the name of what was undone, or null when there was none.</summary>
    public async Task<string?> UndoAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Current == null || _undo.First == null) return null;
            Step step = _undo.First.Value;
            _undo.RemoveFirst();
            _redo.Push(Snapshot(step.Label));
            Restore(step);
            return step.Label;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Does again the step undone last; the name of what was redone, or null when there was none.</summary>
    public async Task<string?> RedoAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Current == null || _redo.Count == 0) return null;
            Step step = _redo.Pop();
            PushUndo(Snapshot(step.Label));
            Restore(step);
            return step.Label;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Commit(string label, PdfDocumentModel next, OpenedProtection? protection)
    {
        PushUndo(Snapshot(label));
        _redo.Clear();
        Replace(next);
        Protection = protection;
        IsModified = true;
        Changed?.Invoke();
    }

    /// <summary>The document as it is now, to come back to under <paramref name="label"/>.</summary>
    private Step Snapshot(string label)
    {
        PdfDocumentModel doc = Current!;
        byte[]? shown = ReferenceEquals(doc.ShownBytes, doc.Bytes) ? null : doc.ShownBytes;
        return new Step(label, doc.Bytes, shown, Protection);
    }

    private void Restore(Step step)
    {
        PdfDocumentModel doc = Current!;
        PdfDocumentModel restored = step.ShownBytes == null
            ? PdfDocumentModel.FromBytes(step.Bytes, doc.FileName, doc.FilePath)
            : PdfDocumentModel.WithBytes(PdfDocumentModel.FromBytes(step.ShownBytes, doc.FileName, doc.FilePath), step.Bytes);
        Replace(restored);
        Protection = step.Protection;
        IsModified = true;
        Changed?.Invoke();
    }

    private void PushUndo(Step step)
    {
        _undo.AddFirst(step);
        long total = UndoBytes;
        // The newest step stays whatever it costs; older ones go until the rest fits.
        while (_undo.Count > 1 && total > UndoBudgetBytes)
        {
            total -= _undo.Last!.Value.Size;
            _undo.RemoveLast();
        }
    }

    /// <summary>Makes <paramref name="next"/> current and disposes the one it replaces, unless they share a parsed document.</summary>
    private void Replace(PdfDocumentModel? next)
    {
        PdfDocumentModel? previous = Current;
        Current = next;
        if (previous != null && !ReferenceEquals(previous, next) && !ReferenceEquals(previous.Doc, next?.Doc))
            previous.Dispose();
    }

    public void Dispose()
    {
        Replace(null);
        _gate.Dispose();
    }
}
