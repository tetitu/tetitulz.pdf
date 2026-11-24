// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A character XML 1.0 forbids — the vertical tab U+000B that Word uses for a manual line break in copied
/// text, a stray control code, a lone surrogate — is replaced with U+FFFD before it reaches the DOCX or
/// ODT output. Written raw, it would make <c>content.xml</c> not well-formed (an office app refuses the
/// file) and make OpenXML throw <c>ArgumentException</c> out of the DOCX exporter, leaving the caller a
/// partial zip.
/// </summary>
public class XmlTextSanitizeTests
{
    private static Document Doc(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        var doc = new Document();
        var section = new Section();
        section.Children.Add(p);
        doc.Sections.Add(section);
        return doc;
    }

    private static string ZipEntryText(byte[] zipBytes, string entry)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        ZipArchiveEntry e = zip.GetEntry(entry) ?? throw new Xunit.Sdk.XunitException("no " + entry);
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void AForbiddenXmlCharacterInDocx_IsReplaced_NotThrown()
    {
        Document doc = Doc("before\u000Bafter");   // U+000B is forbidden by XML 1.0

        byte[] docx = doc.ToDocx();                // pre-fix: ArgumentException out of Export
        string xml = ZipEntryText(docx, "word/document.xml");

        XDocument.Parse(xml);                      // well-formed
        Assert.Contains("�", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("\u000B", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void AForbiddenXmlCharacterInOdt_IsReplaced_NotWrittenRaw()
    {
        Document doc = Doc("before\u000Bafter");

        byte[] odt = doc.ToOdt();
        string xml = ZipEntryText(odt, "content.xml");

        XDocument.Parse(xml);                      // well-formed (raw U+000B would not parse)
        Assert.Contains("�", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("\u000B", xml, StringComparison.Ordinal);
    }

    /// <summary>The control: ordinary text round-trips to both formats with no replacement character.</summary>
    [Fact]
    public void OrdinaryText_IsNotAltered()
    {
        Document doc = Doc("Ordinary text 123");

        string docx = ZipEntryText(doc.ToDocx(), "word/document.xml");
        string odt = ZipEntryText(doc.ToOdt(), "content.xml");

        Assert.Contains("Ordinary text 123", docx, StringComparison.Ordinal);
        Assert.Contains("Ordinary text 123", odt, StringComparison.Ordinal);
        Assert.DoesNotContain("�", docx, StringComparison.Ordinal);
        Assert.DoesNotContain("�", odt, StringComparison.Ordinal);
    }
}
