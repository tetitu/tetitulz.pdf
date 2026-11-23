// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.DocxToPdf;
using Tetitulz.Pdf.DocxToPdf.Engines;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// What an external engine is handed: Word fetched a document's attached template, external image and
/// subdocument with macros off and links not updated, so what leads out of the package goes first.
/// Word itself is no part of these tests; what it asked for was read off a listening socket by hand.
/// </summary>
public sealed class ExternalEngineInputTests
{
    private const string Ns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Attacker = "http://attacker.example/x";

    private static string Rels(string inner) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + inner + "</Relationships>";

    private static string Rel(string id, string type, string target, string mode = "External") =>
        $"<Relationship Id=\"{id}\" Type=\"{Ns}{type}\" Target=\"{target}\"" + (mode == null ? "" : $" TargetMode=\"{mode}\"") + "/>";

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";

    private const string MainDocument = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>";

    /// <summary>The parts given, in a package around them: its content types, its root relationships and a main document, each unless given.</summary>
    private static byte[] Package(params (string Name, string Text)[] parts)
    {
        var all = parts.ToList();
        foreach (var (name, text) in new[]
                 {
                     ("[Content_Types].xml", ContentTypes),
                     ("_rels/.rels", Rels(Rel("rId1", "officeDocument", "word/document.xml", null))),
                     ("word/document.xml", MainDocument),
                 })
            if (!all.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) all.Add((name, text));
        return Zip(all.ToArray());
    }

    /// <summary>The parts given, and nothing else.</summary>
    private static byte[] Zip(params (string Name, string Text)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static long InflatedBytes(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.Entries.Sum(e => e.Length);
    }

    private static byte[] Clean(byte[] package)
    {
        using var output = new MemoryStream();
        ExternalEngineInput.Clean(new MemoryStream(package), output);
        return output.ToArray();
    }

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name).Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string TargetOf(byte[] package, string part, string id) =>
        XDocument.Parse(Part(package, part)).Descendants().Single(e => (string)e.Attribute("Id") == id).Attribute("Target").Value;

    [Theory]
    [InlineData("attachedTemplate", "word/_rels/settings.xml.rels")]
    [InlineData("image", "word/_rels/document.xml.rels")]
    [InlineData("subDocument", "word/_rels/document.xml.rels")]
    [InlineData("frame", "word/_rels/webSettings.xml.rels")]
    [InlineData("oleObject", "word/_rels/header1.xml.rels")]
    public void AdversarialExternalRelationship_LeadsNowhere(string type, string part)
    {
        byte[] cleaned = Clean(Package((part, Rels(Rel("r1", type, Attacker)))));

        Assert.Equal(ExternalEngineInput.NowhereTarget, TargetOf(cleaned, part, "r1"));
        Assert.DoesNotContain("attacker", Part(cleaned, part), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WORD/_RELS/DOCUMENT.XML.RELS")]
    [InlineData("word/_rels/document.xml%2Erels")]
    public void AdversarialPartNaming_IsSeenThrough(string part)
    {
        byte[] cleaned = Clean(Package((part, Rels(Rel("r1", "image", Attacker)))));

        Assert.DoesNotContain("attacker", Part(cleaned, part), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("external")]
    [InlineData(null)]
    public void AdversarialTargetMode_OddOrAbsent_DoesNotHideATarget(string mode)
    {
        const string part = "word/_rels/document.xml.rels";
        byte[] cleaned = Clean(Package((part, Rels(Rel("r1", "image", Attacker, mode)))));

        Assert.Equal(ExternalEngineInput.NowhereTarget, TargetOf(cleaned, part, "r1"));
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\x.png")]
    [InlineData("//attacker.example/x.png")]
    [InlineData("file:///C:/secret.png")]
    public void AdversarialTarget_AShareOrAScheme_WithNoMode_LeadsNowhere(string target)
    {
        const string part = "word/_rels/document.xml.rels";
        byte[] cleaned = Clean(Package((part, Rels(Rel("r1", "image", target, null)))));

        Assert.Equal(ExternalEngineInput.NowhereTarget, TargetOf(cleaned, part, "r1"));
    }

    [Fact]
    public void A_packages_own_parts_and_a_hyperlink_stay_as_they_are()
    {
        const string part = "word/_rels/document.xml.rels";
        const string body = "<w:document xmlns:w=\"x\">body</w:document>";
        byte[] cleaned = Clean(Package(
            ("word/document.xml", body),
            ("word/media/image1.png", "not xml at all"),
            (part, Rels(Rel("r1", "image", "media/image1.png", null) + Rel("r2", "hyperlink", "https://example.org/")))));

        Assert.Equal(body, Part(cleaned, "word/document.xml"));
        Assert.Equal("not xml at all", Part(cleaned, "word/media/image1.png"));
        Assert.Equal("media/image1.png", TargetOf(cleaned, part, "r1"));
        Assert.Equal("https://example.org/", TargetOf(cleaned, part, "r2"));
    }

    /// <summary>
    /// A relationship declared a hyperlink but named by an image's <c>r:link</c>: an engine looks it up by
    /// its id, whatever its type, and fetches it as the picture. It leads nowhere; a hyperlink named only by
    /// a hyperlink element keeps its target. A part that is not XML keeps none of its hyperlinks.
    /// </summary>
    [Fact]
    public void AdversarialHyperlinkNamedByAnImage_LeadsNowhere()
    {
        const string part = "word/_rels/document.xml.rels";
        const string body = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><w:body>" +
                            "<w:hyperlink r:id=\"r2\"/><a:blip r:link=\"r1\"/></w:body></w:document>";
        byte[] cleaned = Clean(Package(("word/document.xml", body),
            (part, Rels(Rel("r1", "hyperlink", Attacker) + Rel("r2", "hyperlink", "https://example.org/")))));

        Assert.Equal(ExternalEngineInput.NowhereTarget, TargetOf(cleaned, part, "r1"));
        Assert.Equal("https://example.org/", TargetOf(cleaned, part, "r2"));

        byte[] unreadable = Clean(Package(("word/document.xml", "<<not xml"), (part, Rels(Rel("r2", "hyperlink", "https://example.org/")))));
        Assert.Equal(ExternalEngineInput.NowhereTarget, TargetOf(unreadable, part, "r2"));
    }

    [Fact]
    public void AdversarialImportedChunk_IsRefused()
    {
        byte[] package = Package(("word/_rels/document.xml.rels", Rels(Rel("r1", "aFChunk", "chunk.html", null))),
                                 ("word/chunk.html", "<img src=\"http://attacker.example/x\">"));

        var refused = Assert.Throws<DocxToPdfException>(() => Clean(package));
        Assert.Contains("aFChunk", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html><img src=\"http://attacker.example/x\"></html>")]
    [InlineData("{\\rtf1 {\\field{\\*\\fldinst INCLUDEPICTURE \"http://attacker.example/x\"}}}")]
    public void AdversarialFile_NotAPackage_IsRefused(string text)
    {
        var refused = Assert.Throws<DocxToPdfException>(() => Clean(Encoding.ASCII.GetBytes(text)));
        Assert.Contains("not a DOCX package", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialRelationshipsPart_WithADtd_IsRefused()
    {
        byte[] package = Package(("word/_rels/document.xml.rels",
            "<!DOCTYPE r [<!ENTITY x SYSTEM \"http://attacker.example/x\">]><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>"));

        var refused = Assert.Throws<DocxToPdfException>(() => Clean(package));
        Assert.Contains("not well formed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialPackage_InflatingPastTheWholeCeiling_IsRefused_Hostile()
    {
        byte[] package = Package(("word/a.bin", new string('x', 600)), ("word/b.bin", new string('x', 600)));
        long inflated = InflatedBytes(package);

        using var output = new MemoryStream();
        var refused = Assert.Throws<DocxToPdfException>(() => ExternalEngineInput.Clean(new MemoryStream(package), output, maxPackageBytes: inflated - 1));
        Assert.Contains("inflates past", refused.Message, StringComparison.Ordinal);

        // Control: the same package under a ceiling that holds it.
        using var enough = new MemoryStream();
        ExternalEngineInput.Clean(new MemoryStream(package), enough, maxPackageBytes: inflated);
        Assert.Equal(new string('x', 600), Part(enough.ToArray(), "word/b.bin"));
    }

    /// <summary>
    /// An OpenDocument file named <c>.docx</c> is a ZIP, and its references are attributes of its own
    /// content, no relationships: an engine telling the format by what the archive holds would read them.
    /// So a ZIP is handed over only as a package whose main document is one of its own parts.
    /// </summary>
    [Fact]
    public void AdversarialZip_AnOpenDocumentFile_IsRefused()
    {
        const string content = "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
                               "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\">" +
                               "<draw:image xlink:href=\"http://attacker.example/x.png\"/></office:document-content>";
        byte[] odf = Zip(("mimetype", "application/vnd.oasis.opendocument.text"),
                         ("META-INF/manifest.xml", "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"/>"),
                         ("content.xml", content));

        var refused = Assert.Throws<DocxToPdfException>(() => Clean(odf));
        Assert.Contains("not a DOCX package", refused.Message, StringComparison.Ordinal);
        Assert.Contains("OpenDocument", refused.Message, StringComparison.Ordinal);

        // Dressed as a package around the same content, it is refused for what it holds all the same —
        // by its mimetype, or by its manifest alone, since an OpenDocument reader does without the former.
        var dressed = Assert.Throws<DocxToPdfException>(() => Clean(Package(("mimetype", "application/vnd.oasis.opendocument.text"), ("content.xml", content))));
        Assert.Contains("OpenDocument", dressed.Message, StringComparison.Ordinal);
        var manifestOnly = Assert.Throws<DocxToPdfException>(() => Clean(Package(
            ("META-INF/manifest.xml", "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"/>"), ("content.xml", content))));
        Assert.Contains("OpenDocument", manifestOnly.Message, StringComparison.Ordinal);
    }

    /// <summary>Each part of a package's frame is required: content types, root relationships, and a main document they name among the parts.</summary>
    [Theory]
    [InlineData("no content types")]
    [InlineData("no root relationships")]
    [InlineData("main document missing")]
    [InlineData("main document external")]
    [InlineData("no main document relationship")]
    public void AdversarialZip_WithoutAPackagesFrame_IsRefused(string missing)
    {
        string rels = Rels(Rel("rId1", "officeDocument", "word/document.xml", null));
        var parts = new System.Collections.Generic.List<(string, string)>
        {
            ("[Content_Types].xml", ContentTypes), ("_rels/.rels", rels), ("word/document.xml", MainDocument),
        };
        switch (missing)
        {
            case "no content types": parts.RemoveAt(0); break;
            case "no root relationships": parts.RemoveAt(1); break;
            case "main document missing": parts.RemoveAt(2); break;
            case "main document external": parts[1] = ("_rels/.rels", Rels(Rel("rId1", "officeDocument", "word/document.xml"))); break;
            case "no main document relationship": parts[1] = ("_rels/.rels", Rels(Rel("rId1", "image", "word/document.xml", null))); break;
        }

        var refused = Assert.Throws<DocxToPdfException>(() => Clean(Zip(parts.ToArray())));
        Assert.Contains("not a DOCX package", refused.Message, StringComparison.Ordinal);

        // Control: the whole frame is handed over, its main document as it was.
        Assert.Equal(MainDocument, Part(Clean(Package()), "word/document.xml"));
    }

    [Fact]
    public void AdversarialPart_PastWhatTheConverterReads_IsRefused_Hostile()
    {
        int depth = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDocxXmlDepth + 5;
        string deep = string.Concat(Enumerable.Repeat("<a>", depth)) + string.Concat(Enumerable.Repeat("</a>", depth));

        Assert.Throws<DocxToPdfException>(() => Clean(Package(("word/document.xml", deep))));
    }

    /// <summary>
    /// Which file each engine is given is not visible without the engine, so what it is given first
    /// shows: a hostile file is refused for itself, before the program is looked for.
    /// </summary>
    [Theory]
    [InlineData(ConversionEngine.MicrosoftWord)]
    [InlineData(ConversionEngine.LibreOffice)]
    public void AdversarialFile_NotAPackage_NeverReachesAnEngine(ConversionEngine engine)
    {
        string dir = Directory.CreateTempSubdirectory("tetitulz-engine-input-").FullName;
        try
        {
            string input = Path.Combine(dir, "hostile.docx");
            File.WriteAllText(input, "<html><img src=\"http://attacker.example/x\"></html>");

            var refused = Assert.Throws<DocxToPdfException>(() => DocxToPdfConverter.Convert(input, Path.Combine(dir, "out.pdf"),
                new DocxToPdfOptions { Engine = engine, LibreOfficePath = Path.Combine(dir, "no-such-soffice") }));

            // Word is Windows' alone, and says so first anywhere else.
            string expected = engine == ConversionEngine.MicrosoftWord && !OperatingSystem.IsWindows()
                ? "only supported on Windows" : "not a DOCX package";
            Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A stand-in for <c>soffice</c>: writes a PDF named for the input into the directory after <c>--outdir</c>.</summary>
    private static string StandInSoffice(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(dir, "soffice.cmd");
            File.WriteAllText(batch,
                "@echo off\r\n:loop\r\nif \"%~1\"==\"\" exit /b 1\r\nif \"%~1\"==\"--outdir\" goto found\r\nshift\r\ngoto loop\r\n" +
                ":found\r\necho %%PDF-1.4 stand-in> \"%~2\\input.pdf\"\r\nexit /b 0\r\n");
            return batch;
        }

        string script = Path.Combine(dir, "soffice");
        File.WriteAllText(script,
            "#!/bin/sh\nwhile [ $# -gt 0 ]; do if [ \"$1\" = \"--outdir\" ]; then printf '%%PDF-1.4 stand-in\\n' > \"$2/input.pdf\"; exit 0; fi; shift; done\nexit 1\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>
    /// A file converted to bytes by an external engine leaves no PDF loose in the temporary directory: the
    /// engine's output is copied into a directory of the conversion's own, never to a name beside every
    /// other account's files, where a copy takes its source's mode. The watcher's potency is a sentinel
    /// written after the call, whose event also marks every earlier event as delivered.
    /// </summary>
    /// <remarks>
    /// The temporary directory is shared with every test assembly running beside this one, and a watcher
    /// whose buffer overflows drops events — the sentinel's among them — reporting it only through its
    /// <c>Error</c> event. An observation that overflowed, or did not see its sentinel, establishes nothing,
    /// so it is taken again, up to <see cref="WatchAttempts"/> times; a loose PDF seen in any attempt fails.
    /// </remarks>
    [Fact]
    public void ConvertToBytes_WithAnExternalEngine_LeavesNoPdfInTheTemporaryDirectory()
    {
        string dir = Directory.CreateTempSubdirectory("tetitulz-engine-bytes-").FullName;
        try
        {
            string input = Path.Combine(dir, "source.docx");
            File.WriteAllBytes(input, Package());
            var options = new DocxToPdfOptions { Engine = ConversionEngine.LibreOffice, LibreOfficePath = StandInSoffice(dir) };

            var loose = new System.Collections.Concurrent.ConcurrentBag<string>();
            byte[]? pdf = null;
            var failures = new List<string>();
            bool observed = false;
            for (int attempt = 1; attempt <= WatchAttempts && !observed; attempt++)
            {
                observed = WatchTemporaryDirectory(() => pdf = DocxToPdfConverter.ConvertToBytes(input, options), loose, out string? failure);
                if (!observed) failures.Add($"attempt {attempt}: {failure}");
            }

            Assert.True(observed, "The watcher saw the sentinel without losing events: " + string.Join("; ", failures));
            Assert.StartsWith("%PDF-1.4 stand-in", Encoding.ASCII.GetString(pdf!), StringComparison.Ordinal);
            Assert.Empty(loose);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>How many times the temporary directory is watched before the observation is called failed.</summary>
    private const int WatchAttempts = 3;

    /// <summary>
    /// Runs <paramref name="act"/> under a watcher of the temporary directory, adding every engine-named PDF
    /// (32 hexadecimal digits) created there to <paramref name="loose"/>; true when a sentinel written after
    /// the act was seen and the watcher reported no lost events, else false with <paramref name="failure"/>.
    /// </summary>
    private static bool WatchTemporaryDirectory(Action act, System.Collections.Concurrent.ConcurrentBag<string> loose, out string? failure)
    {
        string temp = Path.GetTempPath();
        string sentinel = Path.Combine(temp, "tetitulz-sentinel-" + Guid.NewGuid().ToString("N") + ".pdf");
        using var sentinelSeen = new System.Threading.ManualResetEventSlim();
        Exception? lost = null;
        using var watcher = new FileSystemWatcher(temp, "*.pdf")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, e) =>
        {
            if (string.Equals(e.FullPath, sentinel, StringComparison.OrdinalIgnoreCase)) sentinelSeen.Set();
            else if (System.Text.RegularExpressions.Regex.IsMatch(e.Name ?? "", "^[0-9a-f]{32}\\.pdf$")) loose.Add(e.Name!);
        };
        watcher.Error += (_, e) => lost ??= e.GetException();
        watcher.EnableRaisingEvents = true;

        act();

        File.WriteAllBytes(sentinel, Array.Empty<byte>());
        try
        {
            bool seen = sentinelSeen.Wait(TimeSpan.FromSeconds(30));
            Exception? error = System.Threading.Volatile.Read(ref lost);
            failure = error != null ? "the watcher lost events (" + error.GetType().Name + ": " + error.Message + ")"
                : !seen ? "the sentinel was not seen within 30 s"
                : null;
            return failure == null;
        }
        finally
        {
            File.Delete(sentinel);
        }
    }

    [Fact]
    public void A_cleaned_package_is_written_beside_a_path_given()
    {
        string dir = Directory.CreateTempSubdirectory("tetitulz-engine-input-").FullName;
        try
        {
            string input = Path.Combine(dir, "source.docx");
            File.WriteAllBytes(input, Package(("word/_rels/document.xml.rels", Rels(Rel("r1", "image", Attacker)))));

            string copy = ExternalEngineInput.CopyInto(dir, input);

            Assert.NotEqual(input, copy);
            Assert.DoesNotContain("attacker", Part(File.ReadAllBytes(copy), "word/_rels/document.xml.rels"), StringComparison.Ordinal);
            Assert.Contains("attacker", Part(File.ReadAllBytes(input), "word/_rels/document.xml.rels"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
