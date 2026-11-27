// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A footnote or endnote is tagged <c>/Note</c> with an <c>/ID</c> no other element carries, and the
/// structure tree root's <c>/IDTree</c> maps each identifier back to its note — ISO 14289-1 7.9 ("each
/// note tag shall have a unique entry in the ID key"), ISO 32000-1 Table 323 and §14.7.2.
/// </summary>
public sealed class NoteIdentifierTests
{
    /// <summary>Enough notes that their object numbers pass 99, where byte order and number order part.</summary>
    private const int Footnotes = 60;

    /// <summary>
    /// Sixty footnotes and an endnote: a Note each, every identifier distinct, and an <c>/IDTree</c>
    /// whose keys are those identifiers in byte order, each naming its own note.
    /// </summary>
    [Fact]
    public void EveryNote_CarriesAUniqueId_ThatTheIdTreeMapsBackToItsNote()
    {
        Document model = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p =>
            {
                p.AddText("Body");
                for (int i = 0; i < Footnotes; i++) p.AddFootnote("foot " + i).AddText(" more");
                p.AddEndnote("the end");
            }))
            .Build();
        model.Tagged = true;

        using var doc = PdfDocument.Open(new MemoryStream(model.ToPdf()));
        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot])!;
        List<PdfDictionary> notes = Elements(doc, root).Where(e => RoleOf(doc, e) == "Note").ToList();
        Assert.Equal(Footnotes + 1, notes.Count);

        Dictionary<string, int> byId = notes.ToDictionary(n => Ascii(doc.ResolveObject(n[PdfName.ID])), n => n.ObjectNumber);
        Assert.Equal(Footnotes + 1, byId.Count);                                  // every identifier distinct

        var idTree = (PdfDictionary)doc.ResolveObject(root[PdfName.IDTree])!;
        var names = (PdfArray)doc.ResolveObject(idTree[PdfName.Names])!;
        var keys = new List<string>();
        var mapped = new Dictionary<string, int>();
        for (int i = 0; i + 1 < names.Count; i += 2)
        {
            string key = Ascii(names[i]);
            keys.Add(key);
            mapped[key] = ((PdfReference)names[i + 1]).ReferenceObjectNumber;
        }
        Assert.Equal(byId.OrderBy(kv => kv.Key).ToList(), mapped.OrderBy(kv => kv.Key).ToList());

        // §7.9.6: the keys ascend as bytes — and in this fixture that order is not the notes' own.
        Assert.Equal(keys.OrderBy(k => k, System.StringComparer.Ordinal), keys);
        Assert.NotEqual(byId.OrderBy(kv => kv.Value).Select(kv => kv.Key), keys);
    }

    /// <summary>A tagged document with no note writes no <c>/IDTree</c>; its structure tree is there.</summary>
    [Fact]
    public void ADocumentWithoutNotes_WritesNoIdTree()
    {
        Document model = new DocumentBuilder().AddSection(s => s.AddParagraph(p => p.AddText("Body only"))).Build();
        model.Tagged = true;

        using var doc = PdfDocument.Open(new MemoryStream(model.ToPdf()));
        var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
        Assert.NotNull(root);
        Assert.Contains(Elements(doc, root!), e => RoleOf(doc, e) == "P");
        Assert.Null(root![PdfName.IDTree]);
    }

    private static string Ascii(PdfObject? o) => Encoding.ASCII.GetString(((PdfString)o!).RawBytes);

    private static string? RoleOf(PdfDocument doc, PdfDictionary element)
        => (doc.ResolveObject(element[PdfName.S]) as PdfName)?.Value;

    private static List<PdfDictionary> Elements(PdfDocument doc, PdfDictionary root)
    {
        var found = new List<PdfDictionary>();
        var queue = new Queue<PdfObject?>();
        queue.Enqueue(root[PdfName.K]);
        var seen = new HashSet<int>();
        while (queue.Count > 0)
        {
            PdfObject? resolved = doc.ResolveObject(queue.Dequeue());
            if (resolved is PdfArray array)
            {
                foreach (PdfObject item in array) queue.Enqueue(item);
                continue;
            }
            if (resolved is not PdfDictionary dict || dict[PdfName.S] == null) continue;
            if (dict.ObjectNumber > 0 && !seen.Add(dict.ObjectNumber)) continue;
            found.Add(dict);
            if (dict[PdfName.K] != null) queue.Enqueue(dict[PdfName.K]);
        }
        return found;
    }
}
