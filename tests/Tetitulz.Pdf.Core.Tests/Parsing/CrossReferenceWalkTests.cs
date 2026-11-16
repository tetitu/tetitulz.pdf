// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Reading an object of a damaged file can repair the cross-reference table, and every walk of the
/// table reads what it visits.
/// </summary>
public sealed class CrossReferenceWalkTests
{
    /// <summary>A generated file the sanitizer's fuzz target threw "Collection was modified" on.</summary>
    private const string TableRepairedWhileWalked =
        "JVBERi0xLjcKJeLjz9MKMSAwIG9iago8PCAvVHlwZSBubm90cyBbOSAwIFIgMTAgMCBSXSAvUmVzb3VyY2VzIDw8IC9Gb250IDw8" +
        "IC9GMSA1IDAgUiAvRjIgNiAwIFIgPj4gL1hPYmplY3QgPDwgL1gwIDggMCBSID4+ID4+ID4+CmVuZG9iago0IDAgb2JqCjw8IC9M" +
        "ZW5ndGggMTQwID4+CnN0cmVhbQovU3BhbiA8PCAvQWN0dWFsVGV4dCAoeCkgL01DSUQgMCA+PiBCREMgQlQgL0YxIDEyIFRmIDYw" +
        "IDYwIFRkIChBQikgVGogL0YyIDEyIFRmIChhYikgJyBFVCBFTUMgcSA1MCAwIDAgNTAgODAgODAgY20gL1gwIERvIFEgMCAwIDMw" +
        "MCAzMDAgcmUgZgplbmRzdHJlYW0KZW5kb2JqCjUgMCBvYmoKPDwgL1R5cGUgL0ZvbnQgL1N1YnR5cGUgL1R5cGUxIC9CYXNlRm9u" +
        "dCAvSGVsdmV0aWNhID4+CmVuZG9iago2IDAgb2JqCjw8IC9UeXBlIC9Gb250IC9TdWJ0eXBlIC9UeXBlMyAvRm9udEJCb3ggWzAg" +
        "MCAxMDAwIDEwMDBdIC9Gb250TWF0cml4IFswLjAwMSAwIDAgMC4wMDEgMCAwXSAvQ2hhclByb2NzIDw8IC9hIDcgMCBSID4+IC9F" +
        "bmNvZGluZyA8PCAvRGlmZmVyZW5jZXMgWzk3IC9hXSA+PiAvRmlyc3RDaGFyIDk3IC9MYXN0Q2hhciA5NyAvV2lkdGhzIFshMDAw" +
        "XSA+PgplbmRvYmoKNyAwIG9iago8PCAvTGVuZ3RoIDQ1ID4+CnN0cmVhbQoxMDAwIDAgZDAgcSAyIDAgMCAyIDAgMCBjbSAwIDAg" +
        "MTAwIDEwMCByZSBmIFEKZW5kc3RyZWFtCmVuZG9iago4IDAgb2JqCjw8IC9UeXBlIC9YT2JqZWN0IC9TdWJ0eXBlIC9Gb3JtIC9C" +
        "Qm94IFswIDAgMTAwIDEwMF0gL01hdHJpeCBbMSAwIDAgMSAwIDBdIC9SZXNvdXJjZXMgPDwgL0ZvbnQgPDwgL0YxIDUgMCBSID4+" +
        "ID4+IC9MZW5ndGggMzggPj4Kc3RyZWFtCkJUIC9GMSA4IFRmIDEwIDEwIFRkIChpbiBhIGZvcm0pIFRqIEVUCmVuZHN0cmVhbQpl" +
        "bmRvYmoKOSAwIG9iago8PCAvVHlwZSAvQW5ub3QgL1N1YnR5cGUgL0ZyZWVUZXh0IC9SZWN0IFs2MCA2MCAxMDAgMTAwXSAvQ29u" +
        "dGVudHMgKG5vdGUpIC9Qb3B1cCA5IDAgUiAvQSA8PCAvUyAvVVJJIC9VUkkgKGh0dHA6Ly9hKSA+PiA+PgplbmRvYmoKMTUgMCBv" +
        "YmoKPDwgL1R5cGUgL0Fubm90IC9TdWJ0eXBlIC9XaWRnIDBdIC9SZXNvdXJjZXMgPDwgL0ZvbnQgPDwgL0YxIDUgMCBSID4+ID4+" +
        "IC9MZW5ndGggMzggPj4Kc3RyZWFtCkJUIC9GMSA4IFRmIDEwIDEwIFRkIChpbiBhIGZvcm0pIFRqIEVUCmVuZHN0cmVhbQplbmRv" +
        "YmoKOSAwIG9iago8PCAvVHlwZSAvQW5ub3QgL1N1YnR5cGUgL0ZyZWVUZXh0IC9SZWN0IFs2MCA2MCAxMDAgMTAwXSAvQ29udGVu" +
        "dHMgKG5vdGUpIC9Qb3B1cCA5IDAgUiAvQSA8PCAvUyAvVVJJIC9VUkkgKGh0dHA6Ly9hKSA+PiA+PgplbmRvYmoKMTAgMCBvYmoK" +
        "PDwgL1R5cGUgL0Fubm90IC9TdWJ0eXBlIC9XaWRnZXQgL0ZUIC9UeCAvVCAoZikgL1YgKHYpIC9SZWN0IFs2MCAxMjAgMTAwIDE0" +
        "MF0gL0RBICgvRjEgMTAgVGYgMCBnKSA+PgplbmRvYmoKMTEgMCBvYmoKPDwgL1R5cGUgL1N0cnVjdFRyZWVSb290IC9LIDw8IC9T" +
        "IC9QIC9BbHQgKGEpIC9QZyAzIDAgUiAvSyAwID4+ID4+CmVuZG9iagp4cmVmCjAgMTIKMDAwMDAwMDAwMCA2NTUzNSBmIAowMDAw" +
        "MDAwMDE1IDAwMDAwIG4gCjAwMDAwMDAxOTMgMDAwMDAgbiAKMDAwMDAwMDI1MCAwMDAwMCBuIAowMDAwMDAwNDM0IDAwMDAwIG4g" +
        "CjAwMDAwMDA2MjUgMDAwMDAgbiAKMDAwMDAwMDY5NSAwMDAwMCBuIAowMDAwMDAwOTA5IDAwMDAwIG4gCjAwMDAwMDEwMDQgMDAw" +
        "MDAgbiAKMDAwMDAwMTIwMyAwMDAwMCBuIAowMDAwMDAxMzQxIDAwMDAwIG4gCjAwMDAwMDE0NTggMDAwMDAgbiAKdHJhaWxlcgo8" +
        "PCAvU2l6ZSAxMiAvUm9vdCAxIDAgUiA+PgpzdGFydHhyZWYKMTU0MQolJUVPRgo=";

    [Fact]
    public void AdversarialFile_RepairedWhileItsTableIsWalked_IsNotAnUntypedFailure()
    {
        byte[] pdf = Convert.FromBase64String(TableRepairedWhileWalked);
        using var output = new MemoryStream();

        try
        {
            PdfSanitizer.Sanitize(new MemoryStream(pdf), output, new SanitizeOptions { AllowSignatureInvalidation = true });
        }
        catch (PdfException)
        {
            // A refusal is an answer; "Collection was modified" was not.
        }
    }

    [Fact]
    public void Entries_added_during_a_walk_do_not_end_it()
    {
        var table = new CrossReferenceTable();
        table.AddEntry(new CrossReferenceEntry { ObjectNumber = 1, Type = CrossReferenceEntry.EntryType.Normal, Offset = 10 });
        table.AddEntry(new CrossReferenceEntry { ObjectNumber = 2, Type = CrossReferenceEntry.EntryType.Normal, Offset = 20 });

        int visited = 0;
        foreach (CrossReferenceEntry entry in table.AllEntries)
        {
            visited++;
            table.AddEntry(new CrossReferenceEntry { ObjectNumber = entry.ObjectNumber + 100, Type = CrossReferenceEntry.EntryType.Normal, Offset = 30 });
        }

        Assert.Equal(2, visited);
        Assert.Equal(4, table.Count);
        Assert.Equal(4, table.AllEntries.Count());
    }
}