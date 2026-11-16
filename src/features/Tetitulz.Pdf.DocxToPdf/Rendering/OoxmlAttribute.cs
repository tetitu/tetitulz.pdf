// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using DocumentFormat.OpenXml;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// An OOXML element's attribute by its local name, whatever its namespace prefix —
    /// <c>w:val</c>, <c>r:id</c>, <c>a:x</c>. The renderer reads attributes this way rather than
    /// through the typed SDK properties because it walks elements the SDK knows only as unknown
    /// (DrawingML extensions, SmartArt, charts) as well as the ones it knows.
    /// </summary>
    /// <remarks>
    /// Twenty-two sites wrote this lookup inline or as a private <c>Attr</c>: eleven helpers, each
    /// a copy, and eleven inline expressions. One implementation, and without the LINQ
    /// <c>FirstOrDefault</c> each site carried — the attribute list is short and read per element.
    /// </remarks>
    internal static class OoxmlAttribute
    {
        /// <summary>
        /// The value of <paramref name="element"/>'s attribute named <paramref name="localName"/>,
        /// or <c>null</c> when the element is <c>null</c> or has no such attribute.
        /// </summary>
        internal static string Value(OpenXmlElement element, string localName)
        {
            if (element == null) return null;
            foreach (OpenXmlAttribute attribute in element.GetAttributes())
                if (attribute.LocalName == localName) return attribute.Value;
            return null;
        }
    }

    /// <summary>
    /// An OOXML boolean read from its text: <c>ST_OnOff</c> (ECMA-376 §22.9.2.7: <c>true</c>, <c>on</c>,
    /// <c>1</c> and their opposites) and <c>xsd:boolean</c> alike, the one a superset of the other.
    /// </summary>
    /// <remarks>
    /// Five sites read this themselves, each its own way: the shape-text reader took a child's
    /// <c>w:val="on"</c> as off, so <c>&lt;w:b w:val="on"/&gt;</c> in a text box drew its run plain, and the
    /// contextual-alternates reader took any value but three as on.
    /// </remarks>
    internal static class OoxmlOnOff
    {
        /// <summary>Whether <paramref name="value"/> says on: <c>1</c>, <c>true</c> or <c>on</c>, in any case; anything else is off.</summary>
        internal static bool IsTrue(string value)
            => value == "1"
               || string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "on", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether an on/off element is on: present with no <c>val</c> (or an empty one) it is, absent it is not,
        /// otherwise its <c>val</c> says (<see cref="IsTrue"/>).
        /// </summary>
        internal static bool ElementIsOn(OpenXmlElement element)
        {
            if (element == null) return false;
            string value = OoxmlAttribute.Value(element, "val");
            return string.IsNullOrEmpty(value) || IsTrue(value);
        }
    }
}
