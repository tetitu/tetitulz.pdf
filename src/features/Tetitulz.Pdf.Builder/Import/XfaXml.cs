// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>The XML reading the XFA readers share: the template, its prototypes and rich text.</summary>
    internal static class XfaXml
    {
        /// <summary>
        /// The value of <paramref name="e"/>'s attribute named <paramref name="localName"/> in any
        /// namespace, compared ordinally; <c>null</c> for none. A namespace declaration is not an
        /// attribute here (<c>xmlns:x</c> has the local name <c>x</c>).
        /// </summary>
        internal static string? Attr(XElement e, string localName)
        {
            foreach (XAttribute a in e.Attributes())
                if (!a.IsNamespaceDeclaration && string.Equals(a.Name.LocalName, localName, System.StringComparison.Ordinal))
                    return a.Value;
            return null;
        }
    }
}
