// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.WpfViewer.Models;

/// <summary>Immutable handle for an opened PDF.</summary>
public sealed class PdfDocumentModel : IDisposable
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int PageCount { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    internal Tetitulz.Pdf.Core.Document.PdfDocument Doc { get; init; } = null!;
    internal byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>The bytes shown when they differ from <see cref="Bytes"/> (see <see cref="WithBytes"/>).</summary>
    private byte[]? ViewBytes { get; init; }

    /// <summary>The bytes the view shows: the readable document, which after Encrypt is not what Save writes.</summary>
    internal byte[] ShownBytes => ViewBytes ?? Bytes;

    /// <summary>
    /// True while what Save writes is not what is shown — the ciphertext of a pending Encrypt. Values
    /// filled into the view then have no bytes to go into, so form filling is off.
    /// </summary>
    internal bool SavesOtherBytes => ViewBytes != null;

    public void Dispose() => Doc?.Dispose();

    /// <summary>The document <paramref name="bytes"/> are, parsed, under <paramref name="name"/> and <paramref name="path"/>.</summary>
    public static PdfDocumentModel FromBytes(byte[] bytes, string name, string path = "")
    {
        var d = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(bytes));
        return new PdfDocumentModel
        {
            FileName = name, FilePath = path, PageCount = d.Pages.Count,
            Title = d.Metadata?.Title ?? "", Author = d.Metadata?.Author ?? "", Doc = d, Bytes = bytes,
        };
    }

    /// <summary>
    /// A view whose displayed document is <paramref name="source"/>'s (same page tree) but whose
    /// persisted bytes are <paramref name="bytes"/>. Used by Encrypt: the ciphertext has no readable
    /// catalog, so it must not drive the view, but it is what Save writes. The returned model shares
    /// <paramref name="source"/>'s live <see cref="Doc"/>, so dispose only one of the two.
    /// </summary>
    /// <summary>The same document under another name and path — after a Save As. Shares <see cref="Doc"/>.</summary>
    internal PdfDocumentModel WithLocation(string fileName, string filePath) => new()
    {
        FileName = fileName, FilePath = filePath, PageCount = PageCount, Title = Title, Author = Author,
        Doc = Doc, Bytes = Bytes, ViewBytes = ViewBytes,
    };

    /// <summary>
    /// The readable document alone — what a pending Encrypt showed — once the ciphertext has been
    /// written, under the path it was written to. Shares <see cref="Doc"/>; its bytes are
    /// <see cref="ShownBytes"/>, the very array.
    /// </summary>
    internal PdfDocumentModel AsShown(string fileName, string filePath) => new()
    {
        FileName = fileName, FilePath = filePath, PageCount = PageCount, Title = Title, Author = Author,
        Doc = Doc, Bytes = ShownBytes,
    };

    internal static PdfDocumentModel WithBytes(PdfDocumentModel source, byte[] bytes) => new()
    {
        FileName = source.FileName, FilePath = source.FilePath, PageCount = source.PageCount,
        Title = source.Title, Author = source.Author, Doc = source.Doc, Bytes = bytes,
        ViewBytes = source.ShownBytes,
    };
}

/// <summary>
/// The protection a document was opened under: the file as it was, the password that opened it,
/// and the plaintext it decrypted to. The commands work on plaintext, so this is what lets a save
/// write the document back protected instead of quietly writing the decrypted copy.
/// </summary>
/// <param name="EncryptedOriginal">The file's bytes as opened.</param>
/// <param name="Password">The password that opened it; empty for a document that restricts but does not lock.</param>
/// <param name="DecryptedOriginal">
/// The plaintext the file decrypted to — the very array the opened model holds, so an untouched
/// document is recognised by identity and saved as the file it was.
/// </param>
public sealed record OpenedProtection(byte[] EncryptedOriginal, string Password, byte[] DecryptedOriginal);

/// <summary>
/// What a protected document's save writes, or why it writes nothing yet: <see cref="Bytes"/>,
/// or <see cref="NeedsSignatureConsent"/> (a signature would be invalidated and nobody agreed),
/// or <see cref="Refusal"/> (the protection could not be written back).
/// </summary>
public sealed record ProtectedSave(byte[]? Bytes, string? Refusal, bool NeedsSignatureConsent);

public record PageInfo(double WidthPt, double HeightPt, int? Rotation);
public record SearchHit(int Page, string Match, string Context, int Offset);
