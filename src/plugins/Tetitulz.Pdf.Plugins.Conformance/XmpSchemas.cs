// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>One XMP schema: its namespace URI, preferred prefix, and each property's value type.</summary>
    internal sealed class XmpSchema
    {
        internal XmpSchema(string uri, string prefix, (string Name, string Type)[] properties)
        {
            Uri = uri;
            Prefix = prefix;
            foreach (var (name, type) in properties)
                if (!Properties.ContainsKey(name)) Properties.Add(name, type);
        }

        internal string Uri { get; }

        internal string Prefix { get; }

        /// <summary>Property name → value type as the specification writes it ("bag ProperName", "Lang Alt").</summary>
        internal Dictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>A set of schemas looked up by namespace URI; two entries for one URI are merged.</summary>
    internal sealed class XmpSchemaTable
    {
        private readonly Dictionary<string, Dictionary<string, string>> _byUri =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        internal XmpSchemaTable(IEnumerable<XmpSchema> schemas)
        {
            foreach (var schema in schemas)
            {
                if (!_byUri.TryGetValue(schema.Uri, out var properties))
                    _byUri.Add(schema.Uri, properties = new Dictionary<string, string>(StringComparer.Ordinal));
                foreach (var entry in schema.Properties)
                    if (!properties.ContainsKey(entry.Key)) properties.Add(entry.Key, entry.Value);
            }
        }

        /// <summary>
        /// A table holding this one's schemas and <paramref name="more"/>; a property both name takes
        /// <paramref name="more"/>'s type.
        /// </summary>
        internal XmpSchemaTable With(IEnumerable<XmpSchema> more)
        {
            var table = new XmpSchemaTable(Array.Empty<XmpSchema>());
            foreach (var entry in _byUri)
                table._byUri.Add(entry.Key, new Dictionary<string, string>(entry.Value, StringComparer.Ordinal));
            foreach (var schema in more)
            {
                if (!table._byUri.TryGetValue(schema.Uri, out var properties))
                    table._byUri.Add(schema.Uri, properties = new Dictionary<string, string>(StringComparer.Ordinal));
                foreach (var entry in schema.Properties) properties[entry.Key] = entry.Value;
            }
            return table;
        }

        /// <summary>The value type of property <paramref name="name"/> in namespace <paramref name="uri"/>.</summary>
        internal bool TryGetType(string uri, string name, out string type)
        {
            type = null;
            return _byUri.TryGetValue(uri, out var properties) && properties.TryGetValue(name, out type);
        }
    }

    internal static partial class XmpPredefinedSchemas
    {
        /// <summary>ISO 19005's PDF/A identification schema.</summary>
        internal const string PdfAIdNamespace = "http://www.aiim.org/pdfa/ns/id/";

        /// <summary>The PDF/A extension schema container schema (ISO 19005-1 6.7.8, 19005-2 6.6.2.3.3).</summary>
        internal const string ExtensionNamespace = "http://www.aiim.org/pdfa/ns/extension/";

        /// <summary>The field namespaces of the extension schema description's four value types.</summary>
        internal const string SchemaNamespace = "http://www.aiim.org/pdfa/ns/schema#";
        internal const string PropertyNamespace = "http://www.aiim.org/pdfa/ns/property#";
        internal const string TypeNamespace = "http://www.aiim.org/pdfa/ns/type#";
        internal const string FieldNamespace = "http://www.aiim.org/pdfa/ns/field#";

        // ISO 19005-1 6.7.11 Table 2: part, amd, conformance; ISO 19005-2 6.6.4 Table 9 adds corr.
        private static readonly XmpSchema PdfAId1 = new XmpSchema(PdfAIdNamespace, "pdfaid", new[]
        {
            ("part", "Integer"), ("amd", "Text"), ("conformance", "Text"),
        });

        private static readonly XmpSchema PdfAId2 = new XmpSchema(PdfAIdNamespace, "pdfaid", new[]
        {
            ("part", "Integer"), ("amd", "Text"), ("corr", "Text"), ("conformance", "Text"),
        });

        private static readonly XmpSchema Extension = new XmpSchema(ExtensionNamespace, "pdfaExtension", new[]
        {
            ("schemas", "bag Schema"),
        });

        // Later editions' definitions the profiles accept in both parts, the corpus files the evidence
        // (6-7-2-t09-pass-q, 6-6-2-3-1-t09-pass-q, 6-8-t02-pass-g): xmpMM:InstanceID, which neither
        // edition lists and the XMP Specification of 2010 does (Part 2, as a GUID), and which nearly
        // every authoring tool's packet carries.
        private static readonly XmpSchema LaterMediaManagement = new XmpSchema("http://ns.adobe.com/xap/1.0/mm/", "xmpMM", new[]
        {
            ("InstanceID", "URI"),
        });

        // And for PDF/A-2 and -3, xmp:Rating as a Real (6-6-2-3-1-t07-pass-m: "1.0"), as later
        // editions define it; June 2005's table says closed choice of Integer.
        private static readonly XmpSchema LaterBasic = new XmpSchema("http://ns.adobe.com/xap/1.0/", "xmp", new[]
        {
            ("Rating", "Real"),
        });

        /// <summary>
        /// The schemas a PDF/A file's XMP may use without describing them: the XMP Specification's —
        /// the January 2004 edition for PDF/A-1 (ISO 19005-1 6.7.9), the June 2005 edition for PDF/A-2
        /// and -3 (ISO 19005-2 6.6.2.3.1) — and ISO 19005's own.
        /// </summary>
        internal static XmpSchemaTable ForPart(PdfAConformancePart part)
            => part == PdfAConformancePart.Part1 ? Tables.Part1 : Tables.Part2;

        // A nested holder: its initialisers run after the outer class's, whatever file each
        // partial's static fields are declared in.
        private static class Tables
        {
            internal static readonly XmpSchemaTable Part1 = Xmp2004.With(new[] { PdfAId1, Extension, LaterMediaManagement });
            internal static readonly XmpSchemaTable Part2 =
                Xmp2005.With(new[] { PdfAId2, Extension, LaterMediaManagement, LaterBasic });
        }
    }
}
