// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Tetitulz.Pdf.DocxToPdf.Engines
{
    /// <summary>
    /// What Word or LibreOffice is handed instead of the caller's file: a copy of the package whose
    /// references out of it lead nowhere.
    /// </summary>
    /// <remarks>
    /// <para>Opening a document fetches what its package names, whatever the application's link and
    /// macro settings say: Word asked a test server for an attached template, an external image and a
    /// subdocument, each with macros off and links not updated. On a server that is a request to any
    /// address the document's author chooses, and on Windows a UNC target hands over the account's
    /// credentials. So every relationship leading out of the package is pointed at
    /// <see cref="NowhereTarget"/>, its type kept so that what names it still reads. A hyperlink stays,
    /// since nothing follows one until a reader clicks it in the PDF — when only hyperlink elements name
    /// it: an engine looks a relationship up by its id whatever type it declares.</para>
    /// <para><b>Refused rather than rewritten:</b> a file that is not a package, since Word goes by the
    /// bytes and reads a <c>.docx</c> holding HTML or RTF as that, with every reference in it — and a ZIP
    /// that is not an Open Packaging Conventions package with its main document among its parts, such
    /// as an OpenDocument file, whose references are no relationships; and an
    /// imported chunk (<c>aFChunk</c>), a document of its own whose references Word follows too. Neither
    /// has a form this class could clean. The built-in engine fetches nothing and reads both ways.</para>
    /// <para>A field's code (<c>INCLUDEPICTURE</c>, <c>INCLUDETEXT</c>) is not rewritten. It fetches
    /// only when fields update, which <see cref="WordComConverter.HardenForUntrustedInput"/> and the
    /// LibreOffice profile leave off, and which a test server saw no request for.</para>
    /// </remarks>
    internal static class ExternalEngineInput
    {
        /// <summary>Where a rewritten relationship leads: no file and no host.</summary>
        internal const string NowhereTarget = "about:blank";

        private const string HyperlinkType = "/hyperlink";
        private const string ImportedChunkType = "/aFChunk";

        /// <summary>The whole package's inflated bytes this will copy; a part has its own cap, and a package can hold many.</summary>
        internal const long MaxPackageBytes = 1L << 31;

        /// <summary>Copies <paramref name="inputPath"/> as a cleaned package to a new file in <paramref name="directory"/>, and returns its path.</summary>
        internal static string CopyInto(string directory, string inputPath)
        {
            string copy = Path.Combine(directory, "input.docx");
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Clean(input, output);
            }
            return copy;
        }

        /// <summary>Writes the cleaned package.</summary>
        /// <param name="package">The file, seekable.</param>
        /// <param name="output">Where the copy goes.</param>
        /// <param name="maxPackageBytes">The whole package's inflated bytes, a parameter since only a test can reach it cheaply.</param>
        internal static void Clean(Stream package, Stream output, long maxPackageBytes = MaxPackageBytes)
        {
            try
            {
                DocxToPdfConverter.RefuseOversizedParts(package);
                package.Position = 0;

                using (var source = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
                {
                    RequireOfficePackage(source);
                    // Each relationships part's owner is found by name; searched for, it would be entries × entries.
                    var parts = PartIndex(source);

                    using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        long total = 0;
                        foreach (ZipArchiveEntry entry in source.Entries)
                        {
                            total += entry.Length;
                            if (total > maxPackageBytes)
                                throw new DocxToPdfException(
                                    $"The package inflates past {maxPackageBytes:N0} bytes; the document was not handed to the external engine.");

                            ZipArchiveEntry copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                            using (Stream from = entry.Open())
                            using (Stream to = copy.Open())
                            {
                                if (IsRelationshipsPart(entry.FullName))
                                    WriteCleanedRelationships(from, to, entry.FullName, IdsNamedOtherThanAsALink(parts, entry.FullName));
                                else from.CopyTo(to);
                            }
                        }
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                throw new DocxToPdfException(NotADocxPackage, ex);
            }
            catch (XmlException ex)
            {
                throw new DocxToPdfException("A relationships part of the package is not well formed; the document was not handed to the external engine.", ex);
            }
        }

        private const string NotADocxPackage =
            "The input is not a DOCX package, and an external engine reads whatever a file holds, by its bytes; it was not handed over.";

        /// <summary>
        /// Refuses a ZIP that is not an Open Packaging Conventions package whose main document is one of its
        /// own parts — an OpenDocument file named <c>.docx</c> is the case: an engine tells a format by what
        /// the archive holds, and such a file's references are no relationships this class could rewrite.
        /// </summary>
        private static void RequireOfficePackage(ZipArchive source)
        {
            var parts = new System.Collections.Generic.Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                string name = PartName(entry.FullName);
                if (name.Equals("mimetype", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("META-INF/manifest.xml", StringComparison.OrdinalIgnoreCase))
                    throw new DocxToPdfException(NotADocxPackage + $" It holds '{name}', which marks an OpenDocument file.");
                parts[name] = entry;
            }

            ZipArchiveEntry root;
            if (!parts.ContainsKey("[Content_Types].xml") || !parts.TryGetValue("_rels/.rels", out root))
                throw new DocxToPdfException(NotADocxPackage + " It has no [Content_Types].xml or no _rels/.rels.");

            XDocument relationships;
            using (Stream from = root.Open())
            using (XmlReader reader = XmlReader.Create(from, HardenedXml()))
                relationships = XDocument.Load(reader);

            foreach (XElement relationship in relationships.Descendants().Where(e => e.Name.LocalName == "Relationship"))
            {
                string type = (string)relationship.Attribute("Type") ?? "";
                string mode = (string)relationship.Attribute("TargetMode");
                string target = (string)relationship.Attribute("Target") ?? "";
                if (type.EndsWith(MainDocumentType, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase)
                    && !LeadsOutOfThePackage(target) && parts.ContainsKey(PartName(target)))
                    return;
            }

            throw new DocxToPdfException(NotADocxPackage + " Its relationships name no main document among its parts.");
        }

        /// <summary>The main document's relationship type, transitional and strict alike.</summary>
        private const string MainDocumentType = "/officeDocument";

        /// <summary>A ZIP entry's name, or a root relationship's target, as a part name: unescaped, forward slashes, no leading one.</summary>
        private static string PartName(string name)
        {
            PartNamesRead++;
            return Uri.UnescapeDataString(name).Replace('\\', '/').TrimStart('/');
        }

        /// <summary>Part names this thread has unescaped: the evidence that a clean reads each entry's name a bounded number of times.</summary>
        [ThreadStatic] internal static long PartNamesRead;

        /// <summary>The package's entries by part name, case folded — the first of a name, as a search in entry order finds it.</summary>
        private static System.Collections.Generic.Dictionary<string, ZipArchiveEntry> PartIndex(ZipArchive source)
        {
            var parts = new System.Collections.Generic.Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry e in source.Entries)
            {
                string name = PartName(e.FullName);
                if (!parts.ContainsKey(name)) parts.Add(name, e);
            }
            return parts;
        }

        private static XmlReaderSettings HardenedXml() => new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

        /// <summary>A part name as a reader of the package may take it: its case, and its percent escapes, are the file's to choose.</summary>
        private static bool IsRelationshipsPart(string name)
            => Uri.UnescapeDataString(name).EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The relationship ids the part <paramref name="relationshipsPart"/> belongs to names other than
        /// from a hyperlink element — an image's <c>r:embed</c> or <c>r:link</c>, an object's <c>r:id</c>.
        /// An engine looks a relationship up by its id whatever type it declares, so a "hyperlink" one of
        /// these is fetched as what names it. <c>null</c> when the part cannot be read: then no hyperlink
        /// of it is kept.
        /// </summary>
        private static System.Collections.Generic.HashSet<string> IdsNamedOtherThanAsALink(System.Collections.Generic.Dictionary<string, ZipArchiveEntry> parts, string relationshipsPart)
        {
            var ids = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            string name = PartName(relationshipsPart);
            int folder = name.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
            if (folder < 0 || !name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) return null;
            string owner = name.Substring(0, folder) + name.Substring(folder + "_rels/".Length, name.Length - folder - "_rels/".Length - ".rels".Length);
            if (owner.Length == 0) return ids;                     // the package's own: no part names its ids

            if (!parts.TryGetValue(owner, out ZipArchiveEntry part)) return ids;
            try
            {
                using (Stream s = part.Open())
                using (XmlReader reader = XmlReader.Create(s, HardenedXml()))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || !reader.HasAttributes) continue;
                        bool link = HyperlinkElements.Contains(reader.LocalName);
                        while (reader.MoveToNextAttribute())
                            if (!link && (reader.NamespaceURI == RelationshipsNamespace || reader.NamespaceURI == StrictRelationshipsNamespace))
                                ids.Add(reader.Value);
                        reader.MoveToElement();
                    }
                }
            }
            catch (XmlException) { return null; }
            return ids;
        }

        private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string StrictRelationshipsNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";

        /// <summary>The elements that follow a relationship only when a reader clicks: WordprocessingML's and DrawingML's hyperlinks.</summary>
        private static readonly System.Collections.Generic.HashSet<string> HyperlinkElements =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal) { "hyperlink", "hlinkClick", "hlinkHover", "hlinkMouseOver" };

        private static void WriteCleanedRelationships(Stream from, Stream to, string partName,
                                                      System.Collections.Generic.HashSet<string> namedOtherThanAsALink)
        {
            XDocument relationships;
            using (XmlReader reader = XmlReader.Create(from, HardenedXml()))
                relationships = XDocument.Load(reader);

            foreach (XElement relationship in relationships.Descendants().Where(e => e.Name.LocalName == "Relationship"))
            {
                string type = (string)relationship.Attribute("Type") ?? "";
                if (type.EndsWith(ImportedChunkType, StringComparison.OrdinalIgnoreCase))
                    throw new DocxToPdfException(
                        $"'{partName}' imports a document whole (aFChunk), whose own references an external engine follows; it was not handed over. The built-in engine fetches nothing.");

                if (type.EndsWith(HyperlinkType, StringComparison.OrdinalIgnoreCase)
                    && namedOtherThanAsALink != null && !namedOtherThanAsALink.Contains((string)relationship.Attribute("Id") ?? ""))
                    continue;

                string mode = (string)relationship.Attribute("TargetMode");
                string targetOf = (string)relationship.Attribute("Target") ?? "";
                if (string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase) || LeadsOutOfThePackage(targetOf))
                {
                    relationship.SetAttributeValue("Target", NowhereTarget);
                    relationship.SetAttributeValue("TargetMode", "External");
                }
            }

            using (XmlWriter writer = XmlWriter.Create(to, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false }))
                relationships.Save(writer);
        }

        /// <summary>Whether a target that claims no external mode names a scheme, a share or a host all the same.</summary>
        private static bool LeadsOutOfThePackage(string target)
            => target.IndexOf(':') >= 0 || target.StartsWith("\\\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal);
    }
}
