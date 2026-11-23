// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.DocxToPdf.Engines;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// Cleaning a package for Word or LibreOffice finds each relationships part's owner through one index of the
/// package's part names, built once a clean, not by searching every entry for every <c>.rels</c>.
/// </summary>
public sealed class ExternalEngineInputCostTests
{
    /// <summary>
    /// Four hundred parts each with relationships of their own: searched, the owners are about 400 × 800 part names
    /// unescaped; indexed, a few per entry. A hyperlink-only id in a part is still cleaned the same way.
    /// </summary>
    [Fact]
    public void AdversarialPackageOfManyRelationshipParts_IsCleanedInLinearWork()
    {
        const int parts = 400;
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");
            for (int i = 0; i < parts; i++)
            {
                Add($"word/p{i}.xml", "<x/>");
                Add($"word/_rels/p{i}.xml.rels", rels);
            }
        }

        long before = ExternalEngineInput.PartNamesRead;
        using var output = new MemoryStream();
        ExternalEngineInput.Clean(new MemoryStream(ms.ToArray()), output);
        long read = ExternalEngineInput.PartNamesRead - before;

        using var cleaned = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(2 * parts + 3, cleaned.Entries.Count);
        Assert.InRange(read, 2 * parts, 10L * (2 * parts + 3));
    }
}
