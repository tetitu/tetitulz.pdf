// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Import;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="XfaXml.Attr"/>, the one attribute reading the template, its prototypes and rich text
/// share (2026-10-20; there were three): by local name in any namespace, a namespace declaration not
/// an attribute.
/// </summary>
public class XfaXmlTests
{
    [Theory]
    [InlineData("<draw name='a'/>", "a")]
    [InlineData("<draw xmlns:x='urn:x' x:name='b'/>", "b")]
    [InlineData("<draw xmlns:name='urn:x'/>", null)]
    [InlineData("<draw xmlns:name='urn:x' name='c'/>", "c")]
    [InlineData("<draw Name='d'/>", null)]
    public void AnAttributeIsReadByItsLocalName_NotFromANamespaceDeclaration(string xml, string? expected)
        => Assert.Equal(expected, XfaXml.Attr(XElement.Parse(xml), "name"));
}
