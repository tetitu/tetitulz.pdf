// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Reads and writes FDF (Forms Data Format, §12.7.7) — the older sibling of XFDF, and still
    /// what a viewer's "export form data" produces by default in many workflows.
    /// </summary>
    /// <remarks>
    /// <para><b>An FDF is a PDF file with a different header and a different catalog</b>, so both
    /// halves here are the existing PDF machinery rather than a second syntax: writing goes through
    /// <see cref="PdfWriter"/>, which means <see cref="PdfString"/> decides the string encoding and
    /// a Hebrew value goes out as UTF-16BE with its BOM without this file knowing anything about
    /// it. Nothing here escapes or formats a PDF token by hand.</para>
    /// <para><b>Reading does not trust the cross-reference table.</b> Hand-built and
    /// server-generated FDF files routinely carry offsets that are wrong or absent, and the file is
    /// small, so the objects are found by the scan the repair tool uses,
    /// <see cref="RawObjectScanner"/>, and each one is parsed from its own bytes. That also means no
    /// <c>%PDF-</c> header check stands in the way: an FDF's header is <c>%FDF-</c>, which
    /// <see cref="PdfParser.ParseHeader"/> would reject.</para>
    /// </remarks>
    internal static class FdfSerializer
    {
        /// <summary>FDF 1.2 is the version viewers write and every reader accepts.</summary>
        private const string Header = "%FDF-1.2\n";

        // ----------------------------------------------------------------
        //  Write
        // ----------------------------------------------------------------

        internal static void Write(FormData data, Stream output)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var writer = new PdfWriter(output);
            writer.WriteRaw(Header);
            // The binary comment marks the file as containing 8-bit data, so a transport that
            // guesses at text-versus-binary does not mangle a UTF-16BE field value.
            writer.WriteRaw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            var fdf = new PdfDictionary();

            var fields = new PdfArray();
            foreach (var field in data.Fields)
            {
                if (field?.Name == null) continue;
                fields.Add(BuildFieldDictionary(field));
            }
            fdf.Set(PdfName.Fields, fields);

            // §12.7.7.2 Table 243: /Annots sits beside /Fields, and a file may carry either or
            // both. It is written only when there is something in it — an empty /Annots is a claim
            // that a review found nothing, which is not the same as a file that carries fields.
            if (data.Annotations.Count > 0)
            {
                var annots = new PdfArray();
                foreach (var annotation in data.Annotations)
                {
                    if (annotation == null) continue;
                    annots.Add(AnnotationDictionaryMap.ToDictionary(annotation, includePage: true));
                }
                if (annots.Count > 0) fdf.Set(PdfName.Annots, annots);
            }

            if (!string.IsNullOrEmpty(data.SourceFile))
                fdf.Set(PdfName.F, new PdfString(data.SourceFile));

            if (!string.IsNullOrEmpty(data.OriginalId) || !string.IsNullOrEmpty(data.ModifiedId))
            {
                var ids = new PdfArray();
                ids.Add(HexString(data.OriginalId));
                ids.Add(HexString(data.ModifiedId));
                fdf.Set(PdfName.ID, ids);
            }

            var catalog = new PdfDictionary();
            catalog.Set(PdfName.FDF, fdf);

            const int catalogObjNum = 1;
            long offset = writer.WriteIndirectObject(catalogObjNum, 0, catalog);

            long xrefOffset = writer.WriteXrefTable(
                new List<(int objNum, long offset)> { (catalogObjNum, offset) });

            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));
            trailer.Set(PdfName.Size, new PdfNumber(catalogObjNum + 1));
            writer.WriteTrailer(trailer, xrefOffset);
        }

        /// <summary>
        /// One <c>/Fields</c> entry. The fully-qualified name goes in a single <c>/T</c> rather than
        /// being split across <c>/Kids</c>: §12.7.7.3 allows either, a reader joins them the same
        /// way, and one dictionary per field keeps the file's structure the same as the model's.
        /// </summary>
        private static PdfDictionary BuildFieldDictionary(FormDataField field)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.T, new PdfString(field.Name));

            if (field.Values.Count > 1)
            {
                // §12.7.4.4 — a multi-select list box's value is an array of strings. Writing the
                // first one instead would silently drop the rest of the user's selection.
                var array = new PdfArray();
                foreach (string v in field.Values) array.Add(new PdfString(v ?? string.Empty));
                dict.Set(PdfName.V, array);
            }
            else
            {
                dict.Set(PdfName.V, new PdfString(field.Value));
            }

            return dict;
        }

        private static PdfObject HexString(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new PdfString(Array.Empty<byte>(), isHex: true);

            var bytes = new List<byte>(hex.Length / 2);
            for (int i = 0; i + 1 < hex.Length; i += 2)
            {
                if (!TryHex(hex[i], out int hi) || !TryHex(hex[i + 1], out int lo)) return new PdfString(Array.Empty<byte>(), isHex: true);
                bytes.Add((byte)((hi << 4) | lo));
            }
            return new PdfString(bytes.ToArray(), isHex: true);
        }

        /// <summary>A hex digit's value (Core's <c>PdfConstants.HexToNibble</c>); the XFDF reader asks it too.</summary>
        internal static bool TryHex(char c, out int value)
        {
            value = c < 0x80 ? Core.PdfConstants.HexToNibble((byte)c) : -1;
            if (value >= 0) return true;
            value = 0;
            return false;
        }

        // ----------------------------------------------------------------
        //  Read
        // ----------------------------------------------------------------

        internal static FormData Read(Stream input) => Read(input, PdfLimits.MaxFormDataBytes);

        /// <summary>
        /// As <see cref="Read(Stream)"/>, with the byte ceiling supplied.
        /// </summary>
        /// <remarks>
        /// <b>A ceiling only production scale can reach is tested by taking it as a parameter.</b>
        /// <see cref="PdfLimits.MaxFormDataBytes"/> is 64 MiB, so a fixture that reaches it costs
        /// 64 MiB of allocation per run and a test that cannot fail costs more; this overload lets a
        /// few hundred bytes stand where 64 MiB would. The overload above forwards the real constant
        /// and that forwarding is mutated too — without such a row every small-ceiling test passes
        /// over a production path the ceiling never reaches. Same shape as
        /// <c>Core.Fonts.Woff2Decoder.Decode(byte[], long)</c>.
        /// </remarks>
        internal static FormData Read(Stream input, long maxBytes)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            byte[] bytes = ReadAllBytes(input, maxBytes);
            Dictionary<int, PdfObject> objects = ScanObjects(bytes);

            PdfDictionary fdf = FindFdfDictionary(objects);
            if (fdf == null)
                throw new PdfException(
                    "The FDF file has no /FDF dictionary, so it carries no form data. " +
                    "(An XFDF file read as FDF looks exactly like this — check the format.)");

            var data = new FormData();

            if (Resolve(objects, fdf[PdfName.F]) is PdfString file)
                data.SourceFile = file.GetText();

            if (Resolve(objects, fdf[PdfName.ID]) is PdfArray ids)
            {
                if (ids.Count > 0) data.OriginalId = Hex(Resolve(objects, ids[0]) as PdfString);
                if (ids.Count > 1) data.ModifiedId = Hex(Resolve(objects, ids[1]) as PdfString);
            }

            if (Resolve(objects, fdf[PdfName.Fields]) is PdfArray fields)
                ReadFields(objects, fields, prefix: null, data,
                           new HashSet<PdfDictionary>(PdfReferenceEquality.Instance), depth: 0);

            if (Resolve(objects, fdf[PdfName.Annots]) is PdfArray annots)
                ReadAnnotations(objects, annots, data);

            ReportWhatWasNotRead(objects, fdf);
            return data;
        }

        /// <summary>
        /// Names the /FDF entries this reader saw and did not import.
        /// </summary>
        /// <remarks>
        /// <b>§12.7.7.2 Table 243 has thirteen entries and four are read here</b>, <c>/Annots</c>
        /// among them: an FDF exported from a comment review is mostly annotations and no fields at
        /// all. The rest stay reported rather than read, and reporting is still not
        /// importing.
        /// </remarks>
        private static void ReportWhatWasNotRead(Dictionary<int, PdfObject> objects, PdfDictionary fdf)
        {
            var ignored = new List<string>();

            foreach (string key in new[] { "JavaScript", "Pages", "Differences", "EmbeddedFDFs" })
                if (Resolve(objects, fdf[new PdfName(key)]) != null)
                    ignored.Add("/" + key);

            PdfFormDataExchange.ReportIgnored(ignored);
        }

        /// <summary>Reads the <c>/Annots</c> array into <see cref="FormData.Annotations"/>.</summary>
        /// <remarks>
        /// <para><b>An entry that yields no annotation is skipped, not refused.</b> The mapping
        /// answers null only when the dictionary has no usable <c>/Subtype</c>, and an exchange
        /// file with one malformed entry among twenty is a file whose other nineteen are worth
        /// having — the same leniency <see cref="ReadFields"/> applies one key over.</para>
        /// <para><b>The count is capped by the same budget as the field walk.</b> An
        /// attacker-supplied <c>/Annots</c> is as good a place to put a million entries as
        /// <c>/Fields</c> is.</para>
        /// </remarks>
        private static void ReadAnnotations(
            Dictionary<int, PdfObject> objects, PdfArray annots, FormData data)
        {
            int budget = PdfLimits.MaxFormDataFields;

            for (int i = 0; i < annots.Count; i++)
            {
                if (budget-- <= 0) break;

                if (!(Resolve(objects, annots[i]) is PdfDictionary dict)) continue;

                var annotation = AnnotationDictionaryMap.FromDictionary(
                    dict, o => Resolve(objects, o));

                if (annotation != null) data.Annotations.Add(annotation);
            }
        }

        private static byte[] ReadAllBytes(Stream input, long maxBytes)
        {
            if (input.CanSeek)
            {
                long remaining = input.Length - input.Position;
                if (remaining > maxBytes)
                    throw new PdfException(
                        $"The FDF file is larger than {maxBytes} bytes; refusing to read it.");
            }

            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            long total = 0;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new PdfException(
                        $"The FDF file is larger than {maxBytes} bytes; refusing to read it.");
                ms.Write(buffer, 0, read);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Every object in the file, each parsed from its own bytes so one damaged object cannot take
        /// the rest of the file with it.
        /// </summary>
        /// <remarks>
        /// The objects are found by <see cref="RawObjectScanner"/>, which the repair tool uses too:
        /// the parser's header rule, and an end read from each object's tokens. This method used to
        /// find both itself. Its header rule was a third spelling, which took <c>x12 0 obj</c> for
        /// object 12, and its end was the first <c>endobj</c> in the bytes: a field value holding the
        /// word cut the catalog short, and an object missing its <c>endobj</c> ran on over the next
        /// object, which was then never read.
        /// </remarks>
        private static Dictionary<int, PdfObject> ScanObjects(byte[] bytes)
        {
            var objects = new Dictionary<int, PdfObject>();

            // What the scan skips goes unreported here, as an unreadable object always has: the
            // fields that can be read are the answer this reader gives.
            foreach (RawObject located in RawObjectScanner.Scan(bytes, parser: null, new List<string>(), out _))
            {
                if (objects.Count >= PdfLimits.MaxFormDataFields)
                    throw new PdfException(
                        $"The FDF file declares more than {PdfLimits.MaxFormDataFields} objects; refusing to read it.");

                try
                {
                    using var slice = new MemoryStream(
                        bytes, (int)located.Start, (int)(located.End - located.Start), writable: false);
                    var parsed = new PdfParser(slice).ParseIndirectObject();
                    if (parsed != null) objects[located.ObjectNumber] = parsed;
                }
                catch (PdfException) { /* one unreadable object, not an unreadable file */ }
            }

            return objects;
        }
        private static PdfDictionary FindFdfDictionary(Dictionary<int, PdfObject> objects)
        {
            foreach (var obj in objects.Values)
                if (obj is PdfDictionary dict && Resolve(objects, dict[PdfName.FDF]) is PdfDictionary fdf)
                    return fdf;
            return null;
        }

        private static PdfObject Resolve(Dictionary<int, PdfObject> objects, PdfObject obj)
        {
            int hops = 0;
            while (obj is PdfReference reference && hops++ < PdfLimits.MaxIndirectReferenceChain)
                obj = objects.TryGetValue(reference.ReferenceObjectNumber, out PdfObject target) ? target : null;
            return obj;
        }

        /// <summary>
        /// Walks <c>/Fields</c>, joining each node's <c>/T</c> onto its ancestors'. A node may carry
        /// both a value and <c>/Kids</c>, so both are read at every level.
        /// </summary>
        /// <remarks>
        /// A node whose subtree has been read is not read again, as <c>PdfFormReader</c> reads a
        /// document's fields once each: a node is one field with one parent, and a child each level
        /// lists twice was walked once a path, which doubles at every level — forty levels, under the
        /// depth cap, and the call did not return. A node met again while the walk is still inside it
        /// is a cycle, and is walked on into the depth cap's refusal.
        /// </remarks>
        private static void ReadFields(
            Dictionary<int, PdfObject> objects, PdfArray fields, string prefix, FormData data,
            HashSet<PdfDictionary> read, int depth)
        {
            if (depth >= PdfLimits.MaxTreeDepth)
                throw new PdfException(
                    $"The FDF file nests /Kids more than {PdfLimits.MaxTreeDepth} deep; refusing to read it.");

            for (int i = 0; i < fields.Count; i++)
            {
                if (!(Resolve(objects, fields[i]) is PdfDictionary field) || read.Contains(field)) continue;

                string name = Core.Forms.FieldNames.Qualify(
                    prefix, (Resolve(objects, field[PdfName.T]) as PdfString)?.GetText());

                PdfObject value = Resolve(objects, field[PdfName.V]);
                if (value != null && !string.IsNullOrEmpty(name))
                {
                    if (data.Fields.Count >= PdfLimits.MaxFormDataFields)
                        throw new PdfException(
                            $"The FDF file declares more than {PdfLimits.MaxFormDataFields} fields; " +
                            "refusing to read it.");

                    data.Fields.Add(new FormDataField(name, ValuesOf(objects, value)));
                }

                if (Resolve(objects, field[PdfName.Kids]) is PdfArray kids)
                    ReadFields(objects, kids, name, data, read, depth + 1);
                read.Add(field);
            }
        }

        /// <summary>
        /// A <c>/V</c> is a string, a name (a check box's state), or an array of strings for a
        /// multi-select list box.
        /// </summary>
        private static IEnumerable<string> ValuesOf(Dictionary<int, PdfObject> objects, PdfObject value)
        {
            switch (value)
            {
                case PdfString s:
                    return new[] { s.GetText() };
                case PdfName n:
                    return new[] { n.Value };
                case PdfArray array:
                    var list = new List<string>(array.Count);
                    for (int i = 0; i < array.Count; i++)
                    {
                        PdfObject entry = Resolve(objects, array[i]);
                        if (entry is PdfString es) list.Add(es.GetText());
                        else if (entry is PdfName en) list.Add(en.Value);
                    }
                    return list;
                default:
                    return Array.Empty<string>();
            }
        }

        private static string Hex(PdfString s)
        {
            if (s == null || s.RawBytes == null || s.RawBytes.Length == 0) return null;
            var sb = new System.Text.StringBuilder(s.RawBytes.Length * 2);
            foreach (byte b in s.RawBytes) sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
