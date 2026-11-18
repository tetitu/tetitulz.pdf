// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The PDF version each feature a writer can put in a file needs, as ISO 32000-2 marks it
    /// ("(PDF 1.x)" beside the key, the filter or the operator), and the scan that reads the floor
    /// off the objects a writer is about to write.
    /// </summary>
    /// <remarks>
    /// <para><b>One table for "what version does this need".</b> A writer asks it rather than
    /// writing its own literal: the compressed writer and the optimizer for object streams, the
    /// encryptor for its algorithms, <see cref="PdfDocumentBuilder"/> for everything it builds.</para>
    /// <para><b>Public because a plugin and Encryption need it</b>: Core's <c>InternalsVisibleTo</c>
    /// names neither. The object scan stays internal.</para>
    /// </remarks>
    public static class PdfFeatureVersions
    {
        // --- File structure and security -----------------------------------------------------

        /// <summary>The trailer's <c>/ID</c> (PDF 1.1).</summary>
        public static readonly PdfVersion FileIdentifier = PdfVersion.Pdf11;

        /// <summary>Object streams and cross-reference streams (PDF 1.5, §7.5.7, §7.5.8).</summary>
        public static readonly PdfVersion ObjectStreams = PdfVersion.Pdf15;

        /// <summary>The standard security handler with 40-bit RC4, revision 2 (PDF 1.1).</summary>
        public static readonly PdfVersion Rc4Revision2 = PdfVersion.Pdf11;

        /// <summary>The standard security handler's revision 3, and RC4 keys above 40 bits (PDF 1.4).</summary>
        public static readonly PdfVersion Rc4Revision3 = PdfVersion.Pdf14;

        /// <summary>AES-128 through the <c>AESV2</c> crypt filter (PDF 1.6).</summary>
        public static readonly PdfVersion Aes128 = PdfVersion.Pdf16;

        /// <summary>AES-256, revision 6 (PDF 2.0).</summary>
        public static readonly PdfVersion Aes256 = PdfVersion.Pdf20;

        /// <summary>A PDF MAC token (ISO/TS 32004: <c>/KDFSalt</c> and <c>/AuthCode</c> are PDF 2.0 entries).</summary>
        public static readonly PdfVersion PdfMac = PdfVersion.Pdf20;

        // --- Content-stream features, which no object shows -----------------------------------

        /// <summary>Marked-content operators <c>BMC</c>, <c>BDC</c>, <c>EMC</c> (PDF 1.2, §14.6).</summary>
        public static readonly PdfVersion MarkedContent = PdfVersion.Pdf12;

        /// <summary>Optional content (PDF 1.5, §8.11).</summary>
        public static readonly PdfVersion OptionalContent = PdfVersion.Pdf15;

        /// <summary>An artifact's <c>/Subtype</c> (PDF 1.7, §14.8.2.2).</summary>
        public static readonly PdfVersion ArtifactSubtype = PdfVersion.Pdf17;

        /// <summary>
        /// The highest version a writer has been asked for so far, and the feature that asked —
        /// the one a raised header is reported by.
        /// </summary>
        internal sealed class Floor
        {
            /// <summary>The version the features seen so far need; PDF 1.0 when none needs more.</summary>
            public PdfVersion Version { get; private set; } = PdfVersion.Pdf10;

            /// <summary>The feature <see cref="Version"/> is for; null while it is PDF 1.0.</summary>
            public string Feature { get; private set; }

            /// <summary>Raises the floor to <paramref name="version"/> for <paramref name="feature"/>; a lower one changes nothing.</summary>
            public void Require(PdfVersion version, string feature)
            {
                if (version <= Version) return;
                Version = version;
                Feature = feature;
            }

            /// <summary>
            /// The version a header written for <paramref name="requested"/> carries: the request
            /// raised to this floor, never lowered — every writer that takes a version from its
            /// caller settles its header here.
            /// </summary>
            public PdfVersion HeaderFor(PdfVersion requested) => PdfVersion.Max(requested, Version);

            /// <summary>The feature that raised a header written for <paramref name="requested"/>; null when the request stood.</summary>
            public string RaisedBecause(PdfVersion requested) => Version > requested ? Feature : null;
        }

        /// <summary>
        /// What a producer reports when its caller asked for <paramref name="asked"/> and the header
        /// names <paramref name="written"/> because the content uses <paramref name="raisedBecause"/>;
        /// null when nothing was asked (the header is then whatever the content needs, and nothing
        /// deviates) or the request stood. The Builder's exporter and the DOCX converter's own
        /// assembler alike.
        /// </summary>
        internal static string RaiseReport(PdfVersion? asked, PdfVersion written, string raisedBecause)
        {
            if (!asked.HasValue || raisedBecause == null) return null;
            return $"PDF {asked.Value} was asked for and the file was written as PDF {written}: the content uses {raisedBecause}.";
        }

        /// <summary>
        /// Raises <paramref name="floor"/> to what <paramref name="obj"/> and the direct objects
        /// inside it need. References are not followed: a writer scans every object it writes, each
        /// once, so a referenced object is scanned where it is written. Iterative, so the depth of a
        /// caller-built dictionary costs no stack.
        /// </summary>
        internal static void Scan(PdfObject obj, Floor floor)
        {
            var pending = new Stack<PdfObject>();
            pending.Push(obj);
            while (pending.Count > 0)
            {
                PdfObject current = pending.Pop();
                if (current is PdfDictionary dict)
                {
                    ScanDictionary(dict, floor);
                    foreach (KeyValuePair<PdfName, PdfObject> entry in dict)
                        if (entry.Value is PdfDictionary || entry.Value is PdfArray)
                            pending.Push(entry.Value);
                }
                else if (current is PdfArray array)
                {
                    foreach (PdfObject item in array)
                        if (item is PdfDictionary || item is PdfArray)
                            pending.Push(item);
                }
            }
        }

        /// <summary>The rules for one dictionary, each the version ISO 32000-2 marks beside the entry.</summary>
        private static void ScanDictionary(PdfDictionary dict, Floor floor)
        {
            PdfName type = dict.GetName(PdfName.Type);
            PdfName subtype = dict.GetName(PdfName.Subtype);

            // §7.4: a stream's filters. FlateDecode is PDF 1.2, JPXDecode PDF 1.5.
            ScanFilters(dict.Get(PdfName.Filter), floor);

            // §8.9.5: an image's soft mask (PDF 1.4) and a JPEG 2000 image's own opacity (PDF 1.5).
            if (PdfName.Image.Equals(subtype))
            {
                if (dict.ContainsKey(PdfName.SMask)) floor.Require(PdfVersion.Pdf14, "an image soft mask (/SMask)");
                if (dict.ContainsKey(PdfName.SMaskInData)) floor.Require(PdfVersion.Pdf15, "/SMaskInData");
            }

            // §8.4.5: the transparency entries of a graphics state (PDF 1.4).
            if (PdfName.ExtGState.Equals(type))
            {
                if (dict.ContainsKey(PdfName.ca) || dict.ContainsKey(PdfName.CA))
                    floor.Require(PdfVersion.Pdf14, "constant alpha (/ca, /CA)");
                if (dict.ContainsKey(PdfName.BM)) floor.Require(PdfVersion.Pdf14, "a blend mode (/BM)");
                if (dict.ContainsKey(PdfName.SMask)) floor.Require(PdfVersion.Pdf14, "a soft mask (/SMask)");
                if (dict.ContainsKey(PdfName.AIS)) floor.Require(PdfVersion.Pdf14, "/AIS");
            }

            // §8.10: a form XObject's group attributes (PDF 1.4).
            if (PdfName.Form.Equals(subtype) && dict.ContainsKey(PdfName.Group))
                floor.Require(PdfVersion.Pdf14, "a transparency group (/Group)");

            // §7.8.3 resources: /Shading (PDF 1.3), /Properties (PDF 1.2); a shading itself (§8.7.4.3).
            if (dict.Get(PdfName.Shading) is PdfDictionary || dict.ContainsKey(PdfName.ShadingType))
                floor.Require(PdfVersion.Pdf13, "a shading");
            if (dict.Get(PdfName.Properties) is PdfDictionary)
                floor.Require(PdfVersion.Pdf12, "marked-content properties (/Properties)");

            // §9.7, §9.10.3: composite fonts and /ToUnicode (PDF 1.2).
            if (PdfName.Type0.Equals(subtype) || PdfName.CIDFontType0.Equals(subtype) || PdfName.CIDFontType2.Equals(subtype))
                floor.Require(PdfVersion.Pdf12, "a composite font");
            if (dict.ContainsKey(PdfName.ToUnicode)) floor.Require(PdfVersion.Pdf12, "/ToUnicode");

            // §9.9: embedded programs — /FontFile2 (PDF 1.1), /FontFile3 (PDF 1.2), and a /FontFile3
            // stream whose /Subtype is /OpenType (PDF 1.6; the subtype has no other meaning).
            if (dict.ContainsKey(PdfName.FontFile2)) floor.Require(PdfVersion.Pdf11, "/FontFile2");
            if (dict.ContainsKey(PdfName.FontFile3)) floor.Require(PdfVersion.Pdf12, "/FontFile3");
            if (PdfName.OpenType.Equals(subtype)) floor.Require(PdfVersion.Pdf16, "an embedded OpenType font program");

            // §7.7.2: the catalog's entries.
            if (PdfName.Catalog.Equals(type))
            {
                if (dict.ContainsKey(PdfName.Names)) floor.Require(PdfVersion.Pdf12, "the name dictionary (/Names)");
                if (dict.ContainsKey(PdfName.StructTreeRoot)) floor.Require(PdfVersion.Pdf13, "a structure tree (/StructTreeRoot)");
                if (dict.ContainsKey(PdfName.MarkInfo)) floor.Require(PdfVersion.Pdf14, "/MarkInfo");
                if (dict.ContainsKey(PdfName.Lang)) floor.Require(PdfVersion.Pdf14, "the document language (/Lang)");
                if (dict.ContainsKey(PdfName.OCProperties)) floor.Require(PdfVersion.Pdf15, "optional content (/OCProperties)");
            }

            // §14.7.2: a structure element's /Lang and /ActualText (PDF 1.4), /E (PDF 1.5).
            if (PdfName.StructElem.Equals(type))
            {
                if (dict.ContainsKey(PdfName.Lang)) floor.Require(PdfVersion.Pdf14, "a structure element's /Lang");
                if (dict.ContainsKey(PdfName.ActualText)) floor.Require(PdfVersion.Pdf14, "/ActualText");
                if (dict.ContainsKey(PdfName.E)) floor.Require(PdfVersion.Pdf15, "an expansion (/E)");
            }

            // §7.5.5: the trailer's /ID (PDF 1.1). A trailer is the dictionary naming /Root and /Size.
            if (dict.ContainsKey(PdfName.Root) && dict.ContainsKey(PdfName.Size) && dict.ContainsKey(PdfName.ID))
                floor.Require(FileIdentifier, "the file identifier (/ID)");
        }

        private static void ScanFilters(PdfObject filter, Floor floor)
        {
            if (filter is PdfName name) ScanFilter(name, floor);
            else if (filter is PdfArray chain)
                foreach (PdfObject item in chain)
                    if (item is PdfName n) ScanFilter(n, floor);
        }

        private static void ScanFilter(PdfName filter, Floor floor)
        {
            if (PdfName.FlateDecode.Equals(filter)) floor.Require(PdfVersion.Pdf12, "FlateDecode");
            else if (PdfName.JPXDecode.Equals(filter)) floor.Require(PdfVersion.Pdf15, "a JPEG 2000 image (JPXDecode)");
        }
    }
}
