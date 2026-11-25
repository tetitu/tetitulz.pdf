// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// The <c>/AcroForm /XFA</c> entry (ISO 32000-1 §12.7.8) resolved into named XML packets.
    /// </summary>
    /// <remarks>
    /// <para><b>Two shapes, both resolved.</b> §12.7.8 says the value is <i>either</i> a stream
    /// holding the whole XDP <i>or</i> an array of alternating <c>(name) stream</c> pairs. Whether
    /// the key is present is all a stripper needs, and not enough for anything that has to read a
    /// packet. The array form is what authoring tools write, so it
    /// is the common case, not the exotic one.</para>
    ///
    /// <para><b>A malformed array is read as far as it parses, not refused.</b> An odd trailing
    /// element, a name that is not a string, a value that does not resolve to a stream: each such
    /// pair is skipped and the rest are kept. The alternative — refusing the whole packet — turns a
    /// producer's sloppiness into "this document has no XFA", and the one thing every caller here
    /// does with that answer is stop looking.</para>
    ///
    /// <para><b>Both a reader and a handle.</b> <see cref="Streams"/> exposes the stream objects
    /// themselves, because the sanitizer has to blank the payload rather than read it and the
    /// filler has to write a packet back. They are the parsed document's own instances, so a change
    /// through this type is a change to the document.</para>
    ///
    /// <para><b>Public, and a leaf by construction.</b> Core's <c>InternalsVisibleTo</c> names no
    /// plugin, and <c>Plugins.PageEdits</c> is a caller. No static fields, so no type
    /// initializer.</para>
    /// </remarks>
    public sealed class XfaPacket
    {
        /// <summary>The <c>datasets</c> packet name — where a static XFA form keeps its values.</summary>
        public const string DatasetsPacket = "datasets";

        /// <summary>The <c>template</c> packet's name — the form's design, which a dynamic form's layout is made from.</summary>
        public const string TemplatePacket = "template";

        private readonly List<string> _names;
        private readonly List<PdfStream> _streams;

        private XfaPacket(List<string> names, List<PdfStream> streams, bool packaged)
        {
            _names = names;
            _streams = streams;
            IsPackaged = packaged;
        }

        /// <summary>
        /// <c>true</c> when the entry was the array-of-pairs form, so <see cref="PacketNames"/> is
        /// meaningful; <c>false</c> when it was a single stream holding a whole XDP document.
        /// </summary>
        /// <remarks>
        /// The distinction survives into <see cref="GetPacket"/>: an unpackaged entry has exactly
        /// one stream and no names, so asking it for <c>datasets</c> answers <see langword="null"/>
        /// even though the bytes are in there somewhere. Splitting an XDP into its packets is an
        /// XML problem, not a PDF one, and belongs to the caller that has an XML reader.
        /// </remarks>
        public bool IsPackaged { get; }

        /// <summary>The packet names, in file order. Empty when <see cref="IsPackaged"/> is false.</summary>
        public IReadOnlyList<string> PacketNames => _names;

        /// <summary>
        /// The streams carrying the packets, in file order — the document's own instances.
        /// </summary>
        /// <remarks>
        /// One entry per name when packaged; exactly one entry otherwise. Mutating a stream here
        /// mutates the document, which is the point: it is how the sanitizer removes the payload
        /// and how the filler writes new values back.
        /// </remarks>
        public IReadOnlyList<PdfStream> Streams => _streams;

        // ------------------------------------------------------------------

        /// <summary>
        /// <c>true</c> when <paramref name="acroForm"/> carries an <c>/XFA</c> entry at all.
        /// </summary>
        /// <remarks>
        /// Deliberately <b>does not resolve</b> and does not decode: a caller that only reports
        /// presence — the splitter's warning, the merger's, a facade's <c>HasXfa</c> — must not pay
        /// for an inflate, and must answer <c>true</c> for a packet it could not parse. "Present
        /// but unreadable" is still present, and reporting it as absent is the failure this
        /// separation exists to avoid.
        /// </remarks>
        public static bool IsPresent(PdfDictionary acroForm)
        {
            if (acroForm == null) return false;
            var xfa = acroForm[PdfName.XFA];
            return xfa != null && !(xfa is PdfNull);
        }

        /// <summary>
        /// Resolves an <c>/AcroForm</c> dictionary's <c>/XFA</c> entry, or <see langword="null"/>
        /// when there is none or nothing in it resolved to a stream.
        /// </summary>
        /// <param name="acroForm">The catalog's <c>/AcroForm</c> dictionary.</param>
        /// <param name="document">The owning document, used to follow indirect references.</param>
        /// <remarks>
        /// <c>/XFA</c> is routinely an indirect reference and its array elements always are, so
        /// every read here goes through <paramref name="document"/>. A direct dictionary read would
        /// yield <see langword="null"/> — indistinguishable from an absent key, which is why
        /// <see cref="IsPresent"/> is the separate question.
        /// </remarks>
        public static XfaPacket Read(PdfDictionary acroForm, PdfDocument document)
        {
            if (acroForm == null || document == null) return null;

            var entry = document.ResolveObject(acroForm[PdfName.XFA]);
            if (entry == null || entry is PdfNull) return null;

            if (entry is PdfStream single)
                return new XfaPacket(new List<string>(), new List<PdfStream> { single }, packaged: false);

            if (!(entry is PdfArray array)) return null;

            var names = new List<string>();
            var streams = new List<PdfStream>();

            // Pairs: (name) stream. A trailing odd element cannot name anything, so the loop stops
            // one short of the end rather than reading past it.
            for (int i = 0; i + 1 < array.Count; i += 2)
            {
                string name = NameOf(document.ResolveObject(array[i]));
                if (name == null) continue;
                if (!(document.ResolveObject(array[i + 1]) is PdfStream stream)) continue;

                names.Add(name);
                streams.Add(stream);
            }

            return streams.Count == 0 ? null : new XfaPacket(names, streams, packaged: true);
        }

        /// <summary>
        /// The most a packet may decode to before it is refused. A packet is XML the readers build a
        /// tree of, at some twenty-five times its bytes, and a double-deflated stream of a kilobyte reaches
        /// <see cref="Constants.PdfLimits.MaxDecompressedBytes"/>; a form's template and data are megabytes.
        /// </summary>
        internal const long MaxPacketBytes = 64L * 1024 * 1024;

        /// <summary>
        /// <paramref name="packet"/>'s decoded bytes, no more than <see cref="MaxPacketBytes"/>: every
        /// reader's way to a packet, the decode stopped at the ceiling. Null, with
        /// <paramref name="refusal"/>, for one past it or that will not decode.
        /// </summary>
        internal static byte[] BoundedBytes(PdfStream packet, out string refusal)
            => packet.TryGetDecodedData(MaxPacketBytes, out byte[] bytes, out refusal) ? bytes : null;

        /// <summary>
        /// The decoded bytes of the named packet, or <see langword="null"/> when this entry has no
        /// such packet, or it decodes past <see cref="MaxPacketBytes"/>.
        /// </summary>
        /// <param name="name">The packet name, compared ordinally and case-sensitively.</param>
        /// <remarks>
        /// <b>Case-sensitive on purpose.</b> The names are XML element names from the XFA grammar,
        /// not PDF names, and XML is case-sensitive. A case-insensitive match here would accept
        /// <c>Datasets</c> and hand the caller a packet no XFA processor would have found.
        /// </remarks>
        public byte[] GetPacket(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : BoundedBytes(_streams[i], out _);
        }

        /// <summary>
        /// The stream carrying the named packet, or <see langword="null"/> when there is none.
        /// </summary>
        /// <param name="name">The packet name, compared ordinally and case-sensitively.</param>
        /// <remarks>
        /// The document's own instance, so its <c>ObjectNumber</c> is the number an incremental
        /// update has to supersede. §7.3.8 requires every stream to be an indirect object, so a
        /// packet stream always has one — but a caller that is about to write must check anyway,
        /// because a malformed file is exactly the case where it would not.
        /// </remarks>
        public PdfStream GetPacketStream(string name)
        {
            int i = IndexOf(name);
            return i < 0 ? null : _streams[i];
        }

        /// <summary>The <c>config</c> packet's name — the form's processing options, <c>dynamicRender</c> among them.</summary>
        public const string ConfigPacket = "config";

        /// <summary>
        /// Whether the form is a dynamic XFA form: its <c>dynamicRender</c> element — the child of
        /// <c>acrobat7</c>, of <c>acrobat</c>, of <c>config</c>, of the <c>xdp</c> root (ISO 14289-1
        /// 7.15 NOTE) — has the value <c>required</c>. <see langword="false"/> when there is no such
        /// element, it says anything else, or a packaged entry has no <c>config</c> packet;
        /// <see langword="null"/> when the bytes it would be in could not be read, and
        /// <paramref name="unreadable"/> then says why.
        /// </summary>
        /// <remarks>
        /// <para>A packaged entry's <c>config</c> packet is read, its root <c>config</c>; an unpackaged
        /// entry's one stream is the whole XDP, its root <c>xdp</c>. Elements are matched by local name,
        /// since each XFA release gives the config grammar a namespace of its own version, and the
        /// value is compared ordinally once the whitespace around it is set aside: XFA keywords are
        /// case-sensitive. The first such element decides.</para>
        /// <para>The bytes come through <see cref="BoundedBytes"/>, and the reader prohibits a DTD and
        /// resolves nothing. It builds no tree and keeps the names of the first five levels only, so
        /// its memory does not grow with the packet's depth.</para>
        /// </remarks>
        public bool? RequiresDynamicRender(out string unreadable)
        {
            unreadable = null;
            PdfStream stream;
            if (IsPackaged)
            {
                int i = IndexOf(ConfigPacket);
                if (i < 0) return false;
                stream = _streams[i];
            }
            else
            {
                stream = _streams[0];
            }

            byte[] bytes = BoundedBytes(stream, out string refusal);
            if (bytes == null)
            {
                unreadable = refusal ?? "the packet does not decode";
                return null;
            }

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            var path = new string[5];
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var reader = XmlReader.Create(input, settings);
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    int depth = reader.Depth;
                    if (depth >= path.Length) continue;
                    path[depth] = reader.LocalName;
                    if (!IsDynamicRenderPath(path, depth)) continue;

                    string value = reader.IsEmptyElement ? string.Empty : reader.ReadElementContentAsString();
                    return string.Equals(value.Trim(), "required", StringComparison.Ordinal);
                }
                return false;
            }
            catch (XmlException ex)
            {
                unreadable = "the packet could not be read as XML (" + ex.Message + ")";
                return null;
            }
        }

        /// <summary>
        /// Whether the element at <paramref name="depth"/> is <c>dynamicRender</c> under
        /// <c>config/acrobat/acrobat7</c>, the <c>config</c> element the root (depth 3) or the root's
        /// child (depth 4, the deepest <paramref name="path"/> holds).
        /// </summary>
        private static bool IsDynamicRenderPath(string[] path, int depth)
        {
            int config = depth - 3;
            if (config < 0 || path[depth] != "dynamicRender") return false;
            if (config == 1 && path[0] != "xdp") return false;
            return path[config] == "config" && path[config + 1] == "acrobat" && path[config + 2] == "acrobat7";
        }

        private int IndexOf(string name)
        {
            for (int i = 0; i < _names.Count; i++)
                if (string.Equals(_names[i], name, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        /// <summary>
        /// The packet name carried by an array element — a <c>PdfString</c> per §12.7.8, but a
        /// <c>PdfName</c> is accepted too because producers write both.
        /// </summary>
        private static string NameOf(PdfObject obj)
        {
            if (obj is PdfString s) return s.GetText();
            if (obj is PdfName n) return n.Value;
            return null;
        }
    }
}
