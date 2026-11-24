// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>Describes a field in a PDF Portfolio schema.</summary>
    public sealed class PortfolioField
    {
        /// <summary>Internal field name.</summary>
        public string Name { get; init; }

        /// <summary>
        /// The field's <c>/Subtype</c> (ISO 32000-2 Table 155): <c>S</c> (text), <c>D</c> (date) or
        /// <c>N</c> (number) for data the collection items carry, or a file-related field —
        /// <c>F</c>, <c>Desc</c>, <c>ModDate</c>, <c>CreationDate</c>, <c>Size</c>,
        /// <c>CompressedSize</c>. <c>S</c> when the dictionary names none.
        /// </summary>
        public string FieldType { get; init; }

        /// <summary>The field name shown to the user (<c>/N</c>).</summary>
        public string Description { get; init; }

        /// <summary>Whether this field is shown in the navigator.</summary>
        public bool Visible { get; init; }

        /// <summary>Display order.</summary>
        public int Order { get; init; }
    }

    /// <summary>Describes a file entry in a PDF Portfolio.</summary>
    public sealed class PortfolioItem
    {
        /// <summary>Display file name.</summary>
        public string FileName { get; init; }

        /// <summary>Description from the file spec.</summary>
        public string Description { get; init; }

        /// <summary>Creation date, if available.</summary>
        public DateTime? CreationDate { get; init; }

        /// <summary>Byte size of the embedded file.</summary>
        public long Size { get; init; }

        /// <summary>Collection item (/CI) field values keyed by schema field name.</summary>
        public Dictionary<string, string> Fields { get; init; }
    }

    /// <summary>
    /// Reads PDF Portfolio (/Collection) metadata and embedded file listings.
    /// Also supports turning a regular PDF into a portfolio by attaching a /Collection
    /// dictionary via <see cref="ConvertToPortfolio"/>.
    /// </summary>
    public static class PdfPortfolioManager
    {
        /// <summary>
        /// Converts an input PDF into a PDF Portfolio by writing an incremental update that
        /// attaches a minimal <c>/Collection</c> dictionary to the catalog. Existing embedded
        /// files remain intact and will appear in the portfolio UI.
        /// </summary>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the original bytes plus the appended revision.</param>
        /// <param name="viewMode">"D" (details), "T" (tile) or "H" (hidden).</param>
        public static void ConvertToPortfolio(Stream input, Stream output, string viewMode = "D")
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            // Read twice — parse, then serve as the incremental writer's base — so this one needs
            // a seekable view. `ownsBuffer` distinguishes a buffer to release from the caller's own
            // stream.
            Stream ms = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsBuffer);
            try
            {
                WritePortfolioCollection(ms, output, viewMode);
            }
            finally
            {
                if (ownsBuffer) ms.Dispose();
            }
        }

        private static void WritePortfolioCollection(Stream ms, Stream output, string viewMode)
        {
            using var doc = PdfDocument.Open(ms);

            // Fail closed, as every writer here that appends clear text does: the revision carries a
            // copy of the catalog, whose strings (a /Lang, say) a reader would "decrypt" into noise.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Encrypted PDFs are not supported by PdfPortfolioManager.ConvertToPortfolio: the " +
                    "revision would be appended in clear text. Decrypt the document first " +
                    "(Tetitulz.Pdf.Encryption.PdfDecryptor), convert it, then re-encrypt if required.");

            int catalogObjNum = Tetitulz.Pdf.Core.Writing.CatalogObject.NumberOrRefuse(doc.Trailer, "the /Collection");
            var catalog = doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary
                ?? throw new Core.PdfException("Cannot resolve catalog.");

            int maxObj = 0;
            foreach (var kvp in doc.Parser.EnumerateAllObjects(null))
                if (kvp.Key > maxObj) maxObj = kvp.Key;

            var collection = new PdfDictionary();
            collection[PdfName.Type] = PdfName.Collection;
            string mode = (viewMode == "T" || viewMode == "H") ? viewMode : "D";
            collection[new PdfName("View")] = new PdfName(mode);

            int collectionObj = maxObj + 1;

            var updatedCatalog = new PdfDictionary();
            foreach (var kvp in catalog) updatedCatalog[kvp.Key] = kvp.Value;
            updatedCatalog[PdfName.Collection] = new PdfReference(collectionObj, 0);

            var modified = new System.Collections.Generic.List<Tetitulz.Pdf.Core.Writing.IndirectObject>
            {
                new Tetitulz.Pdf.Core.Writing.IndirectObject(collectionObj, 0, collection),
                new Tetitulz.Pdf.Core.Writing.IndirectObject(catalogObjNum, 0, updatedCatalog),
            };

            ms.Position = 0;
            Tetitulz.Pdf.Core.Writing.IncrementalUpdateWriter.WriteUpdate(
                ms, output, modified, doc.Trailer, doc.Parser.StartXrefOffset, collectionObj + 1);
        }
        [ThreadStatic] private static string _lastReadWarning;

        /// <summary>
        /// What the last <see cref="ListItems(Stream)"/>, <see cref="GetSchema(Stream)"/> or <see cref="IsPortfolio(Stream)"/>
        /// on this thread left out — the embedded-files name tree read short, an item whose size could not be
        /// measured — or <see langword="null"/> when it read everything. Reset at the start of every call.
        /// </summary>
        public static string LastReadWarning => _lastReadWarning;

        /// <summary>
        /// The most one item's embedded file is decoded to measure it when its <c>/Params</c> gives no <c>/Size</c>;
        /// past it the size reads 0 and <see cref="LastReadWarning"/> says why.
        /// </summary>
        public const long MaxMeasuredItemBytes = 64L * 1024 * 1024;

        /// <summary>Returns true if the PDF is a Portfolio (has /Collection dict in catalog).</summary>
        public static bool IsPortfolio(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return IsPortfolio(fs);
        }

        /// <summary>Returns true if the PDF stream is a Portfolio (has /Collection dict).</summary>
        public static bool IsPortfolio(Stream input)
        {
            _lastReadWarning = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            // An encrypted document loads no catalog, and "not a portfolio" would be an answer about a
            // document this door never read.
            EncryptedRead.Refuse(doc, "the portfolio collection", null);
            return HasCollection(doc);
        }

        /// <summary>Returns the portfolio schema field names and types.</summary>
        public static IReadOnlyList<PortfolioField> GetSchema(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return GetSchema(fs);
        }

        /// <summary>Returns the portfolio schema field names and types.</summary>
        public static IReadOnlyList<PortfolioField> GetSchema(Stream input)
        {
            _lastReadWarning = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the portfolio schema", null);
            return ReadSchema(doc);
        }

        /// <summary>Lists portfolio items with their metadata.</summary>
        public static IReadOnlyList<PortfolioItem> ListItems(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ListItems(fs);
        }

        /// <summary>Lists portfolio items with their metadata.</summary>
        public static IReadOnlyList<PortfolioItem> ListItems(Stream input)
        {
            _lastReadWarning = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the portfolio items", null);
            return ReadItems(doc);
        }

        // -------------------------------------------------------------------
        //  Implementation
        // -------------------------------------------------------------------

        private static bool HasCollection(PdfDocument doc)
        {
            if (doc.Catalog == null) return false;
            var collectionObj = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Collection]);
            return collectionObj is PdfDictionary;
        }

        private static IReadOnlyList<PortfolioField> ReadSchema(PdfDocument doc)
        {
            var result = new List<PortfolioField>();
            if (doc.Catalog == null) return result;

            var collectionObj = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Collection]);
            if (!(collectionObj is PdfDictionary collectionDict)) return result;

            var schemaObj = doc.ResolveObject(collectionDict[new PdfName("Schema")]);
            if (!(schemaObj is PdfDictionary schemaDict)) return result;

            foreach (var kvp in schemaDict)
            {
                if (kvp.Key.Value == "Type") continue;

                var fieldObj = doc.ResolveObject(kvp.Value);
                if (!(fieldObj is PdfDictionary fieldDict)) continue;

                // ISO 32000-2 Table 155: the name shown to the user is /N, and the field's type is
                // /Subtype. This read /E for the one — the boolean "editable" flag, never a string,
                // so every description came back null — and /ST for the other, a key the table does
                // not define, so every field reported "S" whatever it held.
                string description = null;
                if (doc.ResolveObject(fieldDict[PdfName.N]) is PdfString nStr) description = nStr.GetText();

                string fieldType = "S";
                if (doc.ResolveObject(fieldDict[PdfName.Subtype]) is PdfName subtype) fieldType = subtype.Value;

                bool visible = true;
                var vObj = fieldDict[PdfName.V];
                if (vObj is PdfBoolean vBool) visible = vBool.Value;
                // /Visible is not a key in Table 108 — /V is. This lookup is a tolerant
                // fallback for producers that spell the entry out, and deliberately uses
                // its own name rather than PdfName.V (which is "V", already read above).
                var vObj2 = fieldDict[new PdfName("Visible")];
                if (vObj2 is PdfBoolean vb2) visible = vb2.Value;

                int order = 0;
                var oObj = fieldDict[PdfName.O];
                if (oObj is PdfNumber oNum) order = oNum.IntValue;

                result.Add(new PortfolioField
                {
                    Name        = kvp.Key.Value,
                    FieldType   = fieldType,
                    Description = description,
                    Visible     = visible,
                    Order       = order
                });
            }

            return result.AsReadOnly();
        }

        private static IReadOnlyList<PortfolioItem> ReadItems(PdfDocument doc)
        {
            var result = new List<PortfolioItem>();
            if (doc.Catalog == null) return result;

            // Read the EmbeddedFiles name tree from /Names
            var namesObj = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]);
            if (!(namesObj is PdfDictionary namesDict)) return result;

            var efObj = doc.ResolveObject(namesDict[PdfName.EmbeddedFiles]);
            if (!(efObj is PdfDictionary efDict)) return result;

            // Collect all name-filespec pairs from the name tree
            var pairs = new List<(string name, PdfDictionary fileSpec)>();
            CollectNameTreePairs(efDict, pairs, doc);
            var unmeasured = new Core.Util.DistinctStrings();
            var lengths = new Extraction.DecodedLengths(MaxMeasuredItemBytes);

            foreach (var (name, fileSpec) in pairs)
            {
                string description = null;
                var descObj = fileSpec[PdfName.Desc];
                if (descObj is PdfString descStr) description = descStr.GetText();

                DateTime? creationDate = null;
                long size = 0;

                // Look in /EF for the embedded file stream
                var efStreamDictObj = doc.ResolveObject(fileSpec[PdfName.EF]);
                if (efStreamDictObj is PdfDictionary efStreamDict)
                {
                    var streamObj = doc.ResolveObject(efStreamDict[PdfName.F]);
                    if (streamObj is PdfStream embStream)
                    {
                        // Try /Params for size and creation date
                        // Resolved: /Params may be written as its own object. And /Size is a size
                        // only when it is an integer — a (long) of an out-of-range real is
                        // long.MinValue.
                        var paramsObj = doc.ResolveObject(embStream[PdfName.Params]);
                        if (paramsObj is PdfDictionary paramsDict)
                        {
                            var sizeObj = doc.ResolveObject(paramsDict[PdfName.Size]);
                            if (sizeObj is PdfNumber sizeNum && sizeNum.IsInteger && sizeNum.LongValue >= 0)
                                size = sizeNum.LongValue;

                            var cdObj = paramsDict[PdfName.CreationDate];
                            if (cdObj is PdfString cdStr) creationDate = ParsePdfDate(cdStr.GetText());
                        }
                        if (size == 0)
                        {
                            // Measured once a stream, under a ceiling: a stream refused at the ceiling is not
                            // cached by the stream, and items naming one would each be a decode to it.
                            size = lengths.Of(embStream, "it does not decode", out string why);
                            if (why != null) unmeasured.Add("'" + name + "' (" + why + ")");
                        }
                    }
                }

                // Read /CI (collection item) for schema field values
                var ciObj = doc.ResolveObject(fileSpec[new PdfName("CI")]);
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (ciObj is PdfDictionary ciDict)
                {
                    foreach (var kvp in ciDict)
                    {
                        if (kvp.Value is PdfString sv)
                            fields[kvp.Key.Value] = sv.GetText();
                        else if (kvp.Value is PdfNumber nv)
                            fields[kvp.Key.Value] = nv.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                result.Add(new PortfolioItem
                {
                    FileName     = name,
                    Description  = description,
                    CreationDate = creationDate,
                    Size         = size,
                    Fields       = fields
                });
            }
            if (unmeasured.Count > 0)
                _lastReadWarning ??= "The size of " + unmeasured.Joined(", ") + " could not be measured and reads 0.";

            return result.AsReadOnly();
        }

        // Traversal is Core.Document.PdfNameTree (GAPS §15c): one implementation across the three
        // rings that walk name trees, and it adds the /Kids cycle guard and entry budget this copy
        // lacked. The value is resolved here rather than in the walker because only this caller
        // wants the file-spec dictionary; two others need the reference identity preserved.
        private static void CollectNameTreePairs(PdfDictionary node,
            List<(string, PdfDictionary)> pairs, PdfDocument doc)
        {
            var flat = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            if (!PdfNameTree.TryFlatten(node, doc, flat, ref budget))
                _lastReadWarning = "The embedded-files name tree could not be read whole (an unreadable or overdeep node, a cycle, or past " +
                                   "PdfLimits.MaxNameTreeEntries); the items past where it stopped are not listed.";

            foreach (var entry in flat)
            {
                // ToString() fallback: a portfolio pane shows the key, so a malformed non-string key
                // is better shown than dropped.
                string key = PdfNameTree.KeyText(entry.Key) ?? entry.Key?.ToString();

                if (doc.ResolveObject(entry.Value) is PdfDictionary fileSpec)
                    pairs.Add((key, fileSpec));
            }
        }

        /// <summary>
        /// An embedded file's <c>/CreationDate</c> or <c>/ModDate</c> (§7.9.4), as UTC.
        ///
        /// <para><b>The whole date, through <see cref="PdfDate.TryParse(string)"/></b>, which
        /// <c>PdfMetadata</c> calls too: eight characters alone would drop the time of day and the
        /// time zone, and list every file in a portfolio as created at 00:00.</para>
        ///
        /// <para><see cref="DateTimeOffset.UtcDateTime"/> rather than <c>.DateTime</c>: the
        /// property this feeds is a bare <see cref="DateTime"/>, which cannot carry the file's
        /// offset, and <c>.DateTime</c> would hand back the local wall-clock reading labelled
        /// <see cref="DateTimeKind.Unspecified"/> — a number that means nothing without the offset
        /// that was just discarded. UTC is a instant the caller can compare.</para>
        /// </summary>
        private static DateTime? ParsePdfDate(string pdfDate)
            => PdfDate.TryParse(pdfDate)?.UtcDateTime;
    }
}
