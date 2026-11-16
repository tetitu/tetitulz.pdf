// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// The XML half of static XFA: reads leaf values out of a <c>datasets</c> packet and writes new
    /// ones back into it.
    /// </summary>
    /// <remarks>
    /// <para><b>Only <c>datasets</c>, and only its values.</b> An XFA form is several XML packets —
    /// <c>template</c> (layout and field definitions), <c>config</c>, <c>localeSet</c>,
    /// <c>connectionSet</c> and others. <c>datasets</c> is the one holding what the form is
    /// <i>filled with</i>, and it is the only one a filler has to understand: everything else
    /// describes how a viewer should present those values, which is a layout engine's problem and
    /// deliberately out of charter (GAPS.md §19d).</para>
    ///
    /// <para><b>The reader is hardened, and this is attacker-controlled XML.</b> It arrives inside a
    /// PDF, from whoever produced the PDF. <see cref="DtdProcessing.Prohibit"/> plus a null resolver
    /// closes XXE and entity expansion together — a DTD is the only way to declare an entity, so
    /// refusing the DTD refuses the billion-laughs shape with it. <b>Prohibit rather than
    /// Ignore</b>: silently dropping part of a form's data would produce a fill that looks like it
    /// worked.</para>
    ///
    /// <para><b>Whitespace is preserved on the round trip.</b> Reformatting a packet the caller did
    /// not ask to reformat makes every diff of the output useless, and a value that
    /// <see cref="ReadValues"/> reported unchanged must come back byte-identical.</para>
    ///
    /// <para><b>Namespaces are matched by local name.</b> The <c>xfa:</c> prefix binds
    /// <c>http://www.xfa.org/schema/xfa-data/1.0/</c> in a conformant packet, but producers write
    /// the elements unprefixed too, and a filler that only recognised the namespaced form would
    /// report those documents as having no data at all — the failure mode that is
    /// indistinguishable from an empty form.</para>
    /// </remarks>
    internal static class XfaDatasets
    {
        /// <summary>The XFA data namespace, when the packet declares one.</summary>
        internal const string DataNamespace = "http://www.xfa.org/schema/xfa-data/1.0/";

        /// <summary>
        /// Every leaf value in the packet, keyed by its dotted path below the data root.
        /// <see langword="null"/> when the bytes are not well-formed XML, or nest deeper than
        /// <see cref="MaxXmlDepth"/>.
        /// </summary>
        /// <param name="packet">The decoded <c>datasets</c> packet.</param>
        /// <remarks>
        /// <para><b>A leaf is an element with no element children</b>, so an empty element counts and
        /// maps to the empty string. That distinction matters: an XFA field that was cleared is an
        /// empty element, not a missing one, and a reader that skipped it would report the field as
        /// absent from the form.</para>
        /// <para><b>Paths are relative to the data root</b> — the <c>&lt;xfa:data&gt;</c> element —
        /// so the first segment is the top-level subform's name, which is what an AcroForm
        /// fully-qualified name in a static XFA form starts with too.</para>
        /// </remarks>
        internal static IReadOnlyDictionary<string, string> ReadValues(byte[] packet)
        {
            XElement data = DataRoot(Parse(packet));
            if (data == null) return null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(data, string.Empty, values);
            return values;
        }

        /// <summary>
        /// The deepest a packet's XML may nest before it is refused, as the Builder's template reader
        /// refuses one (<c>XfaTemplate.MaxXmlDepth</c>).
        /// </summary>
        /// <remarks>
        /// The packet is the file's, and <c>XDocument.Load</c> builds a tree of any depth without
        /// recursing — so the walks after it were the first to meet one: <see cref="Collect"/>, and
        /// every reader of <see cref="ReadDataRoot"/>'s tree, recurse once a level, and a chain of a
        /// few hundred thousand elements (a few kilobytes deflated) overflowed the stack, which nothing
        /// catches. 512 is far above any form's data and far below the stack's limit.
        /// </remarks>
        internal const int MaxXmlDepth = 512;

        /// <summary>
        /// The most nodes and attributes a packet may hold before it is refused: a tree costs about a
        /// hundred bytes a node however few bytes the node took in the file, four for <c>&lt;a/&gt;</c>.
        /// </summary>
        internal const int MaxXmlNodes = 2_000_000;

        /// <summary>
        /// Whether <paramref name="packet"/> may be built into a tree: under <see cref="XfaPacket.MaxPacketBytes"/>,
        /// <see cref="MaxXmlDepth"/> and <see cref="MaxXmlNodes"/>, found by a streaming pass that builds nothing —
        /// the one question both the datasets reader and the Builder's template reader ask. An
        /// <see cref="XmlException"/> from the pass is the caller's, as the load's is.
        /// </summary>
        internal static bool IsWithinBounds(byte[] packet, XmlReaderSettings settings)
        {
            if (packet.LongLength > XfaPacket.MaxPacketBytes) return false;
            using var probe = new MemoryStream(packet, writable: false);
            using var reader = XmlReader.Create(probe, settings);
            long nodes = 0;
            while (reader.Read())
            {
                if (reader.Depth > MaxXmlDepth) return false;
                nodes += 1 + reader.AttributeCount;
                if (nodes > MaxXmlNodes) return false;
            }
            return true;
        }

        /// <summary>
        /// The data root itself — the <c>&lt;xfa:data&gt;</c> element with its whole tree — or
        /// <see langword="null"/> when the bytes are not well-formed XML, nest deeper than
        /// <see cref="MaxXmlDepth"/>, or carry no data root.
        /// </summary>
        /// <remarks>
        /// For a reader that needs the <b>tree</b>: a dynamic form's layout binds a repeating
        /// subform to every data element of its name, in order, which the dotted-path dictionary
        /// above cannot express (its remark: "repeated siblings collide, and the first wins"). Same
        /// hardened parse, same root rule.
        /// </remarks>
        internal static XElement ReadDataRoot(byte[] packet) => DataRoot(Parse(packet));

        /// <summary>
        /// Writes <paramref name="values"/> into the packet by dotted path, returning the new bytes.
        /// </summary>
        /// <param name="packet">The decoded <c>datasets</c> packet.</param>
        /// <param name="values">Dotted path → new value.</param>
        /// <param name="written">The paths that were found and replaced.</param>
        /// <returns>
        /// The rewritten packet, or <see langword="null"/> when the bytes were not well-formed XML
        /// or carried no data root.
        /// </returns>
        /// <remarks>
        /// <para><b>Nothing is created.</b> A path with no element is reported by its absence from
        /// <paramref name="written"/> and left alone. Minting the element would mean inventing where
        /// in the subform hierarchy it belongs, and a value in the wrong subform is a filled form
        /// that says something the caller did not ask it to say — worse than an unfilled one, which
        /// is at least visible.</para>
        /// <para><b>The declaration is written back only if it was there.</b> An XFA packet inside a
        /// PDF is a fragment, and adding an <c>&lt;?xml … ?&gt;</c> prologue a producer had left out
        /// changes the bytes an XFA processor concatenates.</para>
        /// </remarks>
        internal static byte[] WriteValues(
            byte[] packet,
            IDictionary<string, string> values,
            out List<string> written)
        {
            written = new List<string>();
            if (values == null) throw new ArgumentNullException(nameof(values));

            XDocument doc = Parse(packet);
            XElement data = DataRoot(doc);
            if (data == null) return null;

            foreach (var pair in values)
            {
                XElement leaf = Locate(data, pair.Key);
                if (leaf == null) continue;

                leaf.Value = pair.Value ?? string.Empty;
                written.Add(pair.Key);
            }

            return Serialize(doc);
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Parses the packet with an XXE-hardened reader, or <see langword="null"/> when it is not
        /// well-formed.
        /// </summary>
        private static XDocument Parse(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return null;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = false,
                // THIS is what preserves the packet's own layout, and it has to live here.
                // `LoadOptions.PreserveWhitespace` is inert for the XmlReader overload of
                // `XDocument.Load` — whitespace comes from the reader's settings.
                IgnoreWhitespace = false,
            };

            try
            {
                // A streaming pass before the tree is built: XmlReader has no depth limit, the walks
                // over the tree recurse once a level, and the tree is what a small packet is costly as.
                if (!IsWithinBounds(packet, settings)) return null;

                using var ms = new MemoryStream(packet, writable: false);
                using var reader = XmlReader.Create(ms, settings);
                return XDocument.Load(reader);
            }
            catch (XmlException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// The <c>&lt;xfa:data&gt;</c> element, whatever the packet's root turned out to be.
        /// </summary>
        /// <remarks>
        /// Two shapes. A <c>datasets</c> root whose child is <c>data</c> is the conformant one, and
        /// a <c>datasets</c> root with no such child yields nothing — the packet says it is a
        /// datasets packet and does not contain the element that holds the data, and guessing past
        /// that would mean filling values into <c>Data description</c> or a
        /// <c>connectionData</c> sibling. Anything else is taken as the data root itself, which
        /// covers both a bare <c>&lt;data&gt;</c> and a producer that wrapped the values in nothing
        /// at all.
        /// </remarks>
        private static XElement DataRoot(XDocument doc)
        {
            XElement root = doc?.Root;
            if (root == null) return null;

            if (IsLocal(root, "datasets"))
            {
                foreach (XElement child in root.Elements())
                    if (IsLocal(child, "data"))
                        return child;
                return null;
            }

            return root;
        }

        private static bool IsLocal(XElement element, string localName)
            => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal);

        /// <summary>Every leaf below <paramref name="element"/>, by dotted path.</summary>
        /// <remarks>
        /// <b>Repeated siblings collide, and the first wins.</b> XFA addresses them by occurrence
        /// index (<c>item[1]</c>); this model has no index, so a repeating subform's second instance
        /// is not reachable. That is the "best effort" boundary, and losing the first row instead of
        /// the second would be no better — what matters is that the write side finds the same
        /// element the read side reported, which it does, because both take the first.
        /// </remarks>
        private static void Collect(XElement element, string prefix, Dictionary<string, string> into)
        {
            foreach (XElement child in element.Elements())
            {
                string path = prefix.Length == 0
                    ? child.Name.LocalName
                    : prefix + "." + child.Name.LocalName;

                if (child.HasElements) Collect(child, path, into);
                else if (!into.ContainsKey(path)) into[path] = child.Value;
            }
        }

        /// <summary>The element at a dotted path below the data root, or <see langword="null"/>.</summary>
        private static XElement Locate(XElement data, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            XElement current = data;
            foreach (string segment in path.Split('.'))
            {
                XElement next = null;
                foreach (XElement child in current.Elements())
                    if (string.Equals(child.Name.LocalName, segment, StringComparison.Ordinal))
                    {
                        next = child;
                        break;
                    }

                if (next == null) return null;
                current = next;
            }

            // No guard against returning the data root itself: an empty path returned above, and
            // any non-empty one descends at least one level, so `current` is never `data` here.
            return current;
        }

        /// <summary>
        /// Renders the document back to bytes, keeping the prologue it arrived with and no other.
        /// </summary>
        private static byte[] Serialize(XDocument doc)
        {
            var settings = new XmlWriterSettings
            {
                // The packet is a fragment inside a PDF, so it carries no BOM and no indentation of
                // this writer's choosing. PreserveWhitespace on the read side means the document
                // already holds the producer's own layout.
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                // XmlWriter's default NewLineHandling is Replace, which rewrites every LF in the
                // packet as CRLF — so a packet a producer wrote with LF came back with every line
                // ending changed, and "a value ReadValues reported unchanged comes back
                // byte-identical" was not true of any such packet. A mutation sweep is what asked
                // the question; the fixture that answered it is XfaDatasetsTests.
                //
                // The trade, stated because it is real: with None a newline inside an ATTRIBUTE
                // value is written raw instead of as &#xA;, and XML attribute-value normalisation
                // turns it into a space on the next read. Datasets carry their values in elements,
                // where None is exactly right, and losing a packet's line endings on every
                // write-back is the larger harm.
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = doc.Declaration == null,
            };

            using var ms = new MemoryStream();
            using (var writer = XmlWriter.Create(ms, settings))
                doc.Save(writer);

            return ms.ToArray();
        }
    }
}
