// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// The WordprocessingML elements that only mark the content they hold — content controls, custom XML, smart
    /// tags, tracked insertions and moves, bidirectional embeddings and overrides — opened, at the block, row,
    /// cell and run level, for the renderer and the Builder mapper alike.
    /// </summary>
    /// <remarks>
    /// <para>One list for both walks, so neither drops what the other keeps: text in a smart tag or in inline
    /// custom XML, a table row or cell inside a content control (a repeating section's rows) or inside custom
    /// XML, and a paragraph inside block custom XML. Word shows all of them; it removes custom XML markup on open but keeps what
    /// it holds.</para>
    /// <para>A tracked deletion and the source of a tracked move hold content that is not shown, and yield
    /// nothing. Hyperlinks and simple fields are not here: what they hold is content, but each carries meaning
    /// (a target, a computed result) the walk applies itself.</para>
    /// <para>The opening is iterative — one stack of enumerators — so nesting costs the walk nothing per level;
    /// how deep a part may nest at all is <c>PdfLimits.MaxDocxXmlDepth</c>'s, checked before the package is
    /// opened.</para>
    /// </remarks>
    internal static class MarkupWrappers
    {
        /// <summary>
        /// The run content an inline wrapper holds; empty for a deletion or a move's source; <c>null</c> for an
        /// element that is not an inline wrapper.
        /// </summary>
        internal static IEnumerable<OpenXmlElement> InlineContent(OpenXmlElement element)
        {
            switch (element)
            {
                case SdtRun sdt:
                    return (IEnumerable<OpenXmlElement>)sdt.GetFirstChild<SdtContentRun>()?.ChildElements
                           ?? new OpenXmlElement[0];
                case InsertedRun _:
                case CustomXmlRun _:
                case MoveToRun _:
                case BidirectionalEmbedding _:
                case BidirectionalOverride _:
                    return element.ChildElements;
                case DeletedRun _:
                case MoveFromRun _:
                    return new OpenXmlElement[0];
                default:
                    // w:smartTag has no type of its own in this SDK: it is read as an unknown element, by name,
                    // and so are the runs inside it — they are read again inside a paragraph, which types them.
                    return IsSmartTag(element) ? Typed(element) : null;
            }
        }

        /// <summary>What an unknown element holds, read again as a paragraph's content; its own children if that fails.</summary>
        /// <remarks>
        /// Every smart tag inside is opened in the same read (<see cref="WithoutSmartTags"/>): the SDK types only
        /// the top level of what it parses, so a nested smart tag would come back unknown and be serialised and
        /// parsed again on its own, a level at a time — the subtree's size times its depth, up to
        /// <c>PdfLimits.MaxDocxXmlDepth</c>.
        /// </remarks>
        private static IEnumerable<OpenXmlElement> Typed(OpenXmlElement unknown)
        {
            try
            {
                var holder = new Paragraph { InnerXml = WithoutSmartTags(unknown.InnerXml) };
                return new List<OpenXmlElement>(holder.ChildElements);
            }
            catch (System.Xml.XmlException)
            {
                return unknown.ChildElements;
            }
        }

        /// <summary>
        /// <paramref name="fragment"/> with every <c>w:smartTag</c>'s own start and end tags taken out and what
        /// it holds kept in place. The fragment is the SDK's own serialisation, each element declaring the
        /// namespaces it uses. Read with no DTD and no resolver, as every XML reader here is.
        /// </summary>
        internal static string WithoutSmartTags(string fragment)
        {
            if (fragment.IndexOf("smartTag", System.StringComparison.Ordinal) < 0) return fragment;

            var readerSettings = new System.Xml.XmlReaderSettings
            {
                ConformanceLevel = System.Xml.ConformanceLevel.Fragment,
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            var writerSettings = new System.Xml.XmlWriterSettings
            {
                ConformanceLevel = System.Xml.ConformanceLevel.Fragment,
                OmitXmlDeclaration = true,
            };
            var output = new System.Text.StringBuilder(fragment.Length);
            var opened = new Stack<bool>();   // per open element: was it a smart tag left out?

            using (var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(fragment), readerSettings))
            using (var writer = System.Xml.XmlWriter.Create(output, writerSettings))
            {
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case System.Xml.XmlNodeType.Element:
                            bool smartTag = reader.LocalName == "smartTag" && reader.NamespaceURI == WordprocessingNamespace;
                            bool empty = reader.IsEmptyElement;
                            if (!smartTag)
                            {
                                writer.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                                writer.WriteAttributes(reader, true);
                                if (empty) writer.WriteEndElement();
                            }
                            if (!empty) opened.Push(smartTag);
                            break;
                        case System.Xml.XmlNodeType.EndElement:
                            if (!opened.Pop()) writer.WriteFullEndElement();
                            break;
                        case System.Xml.XmlNodeType.Text:
                            writer.WriteString(reader.Value);
                            break;
                        case System.Xml.XmlNodeType.Whitespace:
                        case System.Xml.XmlNodeType.SignificantWhitespace:
                            writer.WriteWhitespace(reader.Value);
                            break;
                        case System.Xml.XmlNodeType.CDATA:
                            writer.WriteCData(reader.Value);
                            break;
                    }
                }
            }
            return output.ToString();
        }

        private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        private static bool IsSmartTag(OpenXmlElement element)
            => element.LocalName == "smartTag" && element.NamespaceUri == WordprocessingNamespace;

        /// <summary>
        /// The Unicode controls a bidirectional wrapper stands for, or <c>('\0', '\0')</c> for any other element:
        /// <c>w:dir</c> an LRE or RLE, <c>w:bdo</c> an LRO or RLO, each closed by a PDF — ECMA-376-1:2016
        /// §17.3.2.8 and §17.3.2.3 ("functionally equivalent to the presence of a LRE/RLE [LRO/RLO] character at the
        /// location of the start element, and a corresponding PDF character at the location of the end element").
        /// A <c>w:val</c> left out is <c>ltr</c>, both clauses say.
        /// </summary>
        internal static (char Open, char Close) DirectionControls(OpenXmlElement element)
        {
            switch (element)
            {
                case BidirectionalEmbedding embedding:
                    return (embedding.Val?.Value == DirectionValues.Rtl
                            ? UnicodeBidi.Controls.RightToLeftEmbedding : UnicodeBidi.Controls.LeftToRightEmbedding,
                        UnicodeBidi.Controls.PopDirectionalFormatting);
                case BidirectionalOverride @override:
                    return (@override.Val?.Value == DirectionValues.Rtl
                            ? UnicodeBidi.Controls.RightToLeftOverride : UnicodeBidi.Controls.LeftToRightOverride,
                        UnicodeBidi.Controls.PopDirectionalFormatting);
                default:
                    return ('\0', '\0');
            }
        }

        /// <summary>The content a block-level wrapper holds, or <c>null</c> for an element that is not one.</summary>
        internal static IEnumerable<OpenXmlElement> BlockContent(OpenXmlElement element)
        {
            switch (element)
            {
                case SdtBlock sdt:
                    return (IEnumerable<OpenXmlElement>)sdt.GetFirstChild<SdtContentBlock>()?.ChildElements
                           ?? new OpenXmlElement[0];
                case CustomXmlBlock _:
                    return element.ChildElements;
                default:
                    return null;
            }
        }

        /// <summary><paramref name="elements"/> with every block-level wrapper among them opened, in document order.</summary>
        internal static IEnumerable<OpenXmlElement> Blocks(IEnumerable<OpenXmlElement> elements)
            => Open(elements, BlockContent);

        /// <summary>The rows <paramref name="table"/> holds, through content controls and custom XML around them.</summary>
        internal static IEnumerable<TableRow> Rows(Table table)
        {
            foreach (var element in Open(table.ChildElements, RowContent))
                if (element is TableRow row) yield return row;
        }

        /// <summary>The cells <paramref name="row"/> holds, through content controls and custom XML around them.</summary>
        internal static IEnumerable<TableCell> Cells(TableRow row)
        {
            foreach (var element in Open(row.ChildElements, CellContent))
                if (element is TableCell cell) yield return cell;
        }

        private static IEnumerable<OpenXmlElement> RowContent(OpenXmlElement element)
        {
            switch (element)
            {
                case SdtRow sdt:
                    return (IEnumerable<OpenXmlElement>)sdt.GetFirstChild<SdtContentRow>()?.ChildElements
                           ?? new OpenXmlElement[0];
                case CustomXmlRow _:
                    return element.ChildElements;
                default:
                    return null;
            }
        }

        private static IEnumerable<OpenXmlElement> CellContent(OpenXmlElement element)
        {
            switch (element)
            {
                case SdtCell sdt:
                    return (IEnumerable<OpenXmlElement>)sdt.GetFirstChild<SdtContentCell>()?.ChildElements
                           ?? new OpenXmlElement[0];
                case CustomXmlCell _:
                    return element.ChildElements;
                default:
                    return null;
            }
        }

        /// <summary>The elements in order, each wrapper <paramref name="contentOf"/> recognises replaced by what it holds.</summary>
        private static IEnumerable<OpenXmlElement> Open(
            IEnumerable<OpenXmlElement> elements, System.Func<OpenXmlElement, IEnumerable<OpenXmlElement>> contentOf)
        {
            var stack = new Stack<IEnumerator<OpenXmlElement>>();
            stack.Push(elements.GetEnumerator());
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext())
                {
                    top.Dispose();
                    stack.Pop();
                    continue;
                }
                var content = contentOf(top.Current);
                if (content != null) stack.Push(content.GetEnumerator());
                else yield return top.Current;
            }
        }
    }
}
