// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Xml;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// PDF/A's rule on the properties of an XMP packet (ISO 19005-1 6.7.9, ISO 19005-2/3 6.6.2.3.1):
    /// each is in a schema the XMP Specification or ISO 19005 predefines, or in an extension schema
    /// the packet describes (<c>pdfaExtension:schemas</c>), and its value has the type the schema
    /// declares — the rules 6.7.9-2/-3 and 6.6.2.3.1-1/-2, whose corpus files are the oracle.
    /// </summary>
    /// <remarks>
    /// A value's shape is read from the RDF (XMP Specification, "Serializing XMP"): a simple value is
    /// text or <c>rdf:resource</c>; a structure is <c>rdf:parseType="Resource"</c>, an
    /// <c>rdf:Description</c>, or property attributes; an array is <c>rdf:Bag</c>, <c>rdf:Seq</c> or
    /// <c>rdf:Alt</c> of <c>rdf:li</c> items; a qualified value is the structure's <c>rdf:value</c>.
    /// A predefined structure's fields are not checked — the specification's field tables are not in
    /// the generated table — so such a property only has to be a structure. A value type the packet
    /// describes says its fields itself: a value of it uses only those, under the type's namespace,
    /// each of its declared type (6.6.2.3.3-15's <c>pdfaType:field</c>; its corpus file uses a field
    /// a type with no <c>pdfaType:field</c> never declares).
    /// </remarks>
    internal static class XmpPropertyRules
    {
        internal const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
        private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

        /// <summary>How deep values may nest before the reading stops; the packet is file input.</summary>
        internal const int MaxValueDepth = 32;

        /// <summary>
        /// The longest <c>valueType</c> judged: <c>Bag Closed Choice of Text</c> is a few dozen
        /// characters, and each prefix of a longer one costs a call and a copy of the rest.
        /// </summary>
        internal const int MaxValueTypeLength = 256;

        /// <summary>The names a finding lists before it says how many more there are.</summary>
        internal const int MaxNamedProperties = 5;

        /// <summary>The value types the XMP Specification's schemas use that are structures.</summary>
        private static readonly HashSet<string> StructureTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Dimensions", "Thumbnail", "ResourceEvent", "ResourceRef", "Version", "Job", "Font", "Colorant",
            "Flash", "OECF/SFR", "CFAPattern", "DeviceSettings", "Marker", "Media", "ProjectLink", "Time",
            "Timecode", "beatSpliceStretch", "resampleStretch", "timeScaleStretch",
            // ISO 19005's extension schema description types
            "Schema", "Property", "ValueType", "Field",
        };

        /// <summary>The value types with a lexical form of their own (<see cref="XmpLexical"/>).</summary>
        private static readonly HashSet<string> LexicalTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Integer", "Real", "Rational", "Boolean", "Date",
        };

        /// <summary>The value types whose value is unconstrained text.</summary>
        private static readonly HashSet<string> TextTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Text", "ProperName", "AgentName", "URI", "URL", "RenditionClass", "MIMEType", "XPath", "Locale",
            "GPSCoordinate", "Choice", "Open Choice", "Closed Choice",
        };

        /// <summary>What one packet's properties come to.</summary>
        internal sealed class Findings
        {
            /// <summary>Properties no predefined or described schema defines, as <c>prefix:name</c>.</summary>
            internal DistinctEntries Undefined { get; } = new DistinctEntries();

            /// <summary>Properties whose value is not of the declared type: <c>prefix:name (type)</c>.</summary>
            internal DistinctEntries Mistyped { get; } = new DistinctEntries();

            /// <summary>
            /// What is wrong with the packet's extension schema descriptions (ISO 19005-1 6.7.8,
            /// ISO 19005-2/3 6.6.2.3.2 and 6.6.2.3.3): a field missing, not text, outside its value
            /// type, or written under a prefix other than the one the clause requires.
            /// </summary>
            internal DistinctEntries ExtensionProblems { get; } = new DistinctEntries();

            /// <summary>A value nested past <see cref="MaxValueDepth"/>; it was not judged.</summary>
            internal bool Truncated { get; set; }
        }

        /// <summary>A property's value as RDF shapes it.</summary>
        private sealed class Value
        {
            internal enum Shape { Simple, Structure, Array, TooDeep }

            internal Shape Kind;
            internal string Text;
            internal string Language;
            internal string ArrayKind;
            internal List<Value> Items;
            internal List<(string Namespace, string Name, string Prefix, Value Value)> Fields;
        }

        /// <summary>The properties a packet's extension schemas describe, and the value types they add.</summary>
        internal sealed class ExtensionSchemas
        {
            internal Dictionary<string, Dictionary<string, string>> Properties { get; } =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            internal HashSet<string> Types { get; } = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Each described value type's namespace and fields (name → value type), the first
            /// description of a type name deciding.
            /// </summary>
            internal Dictionary<string, (string Namespace, Dictionary<string, string> Fields)> TypeFields { get; } =
                new Dictionary<string, (string, Dictionary<string, string>)>(StringComparer.Ordinal);

            internal bool TryGetType(string uri, string name, out string type)
            {
                type = null;
                return Properties.TryGetValue(uri, out var props) && props.TryGetValue(name, out type);
            }
        }

        /// <summary>Checks every top-level property of <paramref name="packet"/>.</summary>
        internal static Findings Check(XmlDocument packet, XmpSchemaTable predefined)
        {
            var findings = new Findings();
            var extensions = ReadExtensionSchemas(packet, findings);
            foreach (var (element, attribute) in TopLevelProperties(packet))
            {
                string ns = element?.NamespaceURI ?? attribute.NamespaceURI;
                string name = element?.LocalName ?? attribute.LocalName;
                string shown = (element?.Prefix ?? attribute.Prefix) + ":" + name;
                if (!predefined.TryGetType(ns, name, out string type) && !extensions.TryGetType(ns, name, out type))
                {
                    Add(findings.Undefined, shown);
                    continue;
                }
                var value = element != null ? Read(element, 0) : new Value { Kind = Value.Shape.Simple, Text = attribute.Value };
                if (Contains(value, Value.Shape.TooDeep))
                {
                    findings.Truncated = true;
                    continue;
                }
                if (!Conforms(value, type, extensions))
                    Add(findings.Mistyped, shown + " (" + type + ")");
            }
            return findings;
        }

        private static void Add(DistinctEntries list, string entry) => list.Add(entry);

        /// <summary>
        /// Findings in the order found, each once. A set beside the list keeps them distinct: a search
        /// of the list before each was n² / 2 comparisons for n findings, and a packet chooses n.
        /// </summary>
        internal sealed class DistinctEntries
        {
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

            internal List<string> Items { get; } = new List<string>();

            internal int Count => Items.Count;

            /// <summary>How many entries the set holds — every one, while it is the set that decides.</summary>
            internal int Seen => _seen.Count;

            internal void Add(string entry)
            {
                if (_seen.Add(entry)) Items.Add(entry);
            }
        }

        /// <summary>The names in <paramref name="entries"/>: the first few, and how many more.</summary>
        internal static string Describe(DistinctEntries entries)
        {
            List<string> list = entries.Items;
            if (list.Count <= MaxNamedProperties) return string.Join(", ", list);
            return string.Join(", ", list.GetRange(0, MaxNamedProperties)) + " and "
                + (list.Count - MaxNamedProperties).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";
        }

        /// <summary>
        /// Each property of each <c>rdf:Description</c> directly under an <c>rdf:RDF</c>: an element
        /// child, or an attribute outside the RDF, XML and namespace-declaration namespaces.
        /// </summary>
        private static IEnumerable<(XmlElement Element, XmlAttribute Attribute)> TopLevelProperties(XmlDocument packet)
        {
            foreach (XmlElement rdf in packet.GetElementsByTagName("RDF", RdfNamespace))
            {
                foreach (XmlNode node in rdf.ChildNodes)
                {
                    if (!(node is XmlElement description) || !IsRdf(description, "Description")) continue;
                    foreach (XmlAttribute attribute in description.Attributes)
                        if (IsPropertyAttribute(attribute)) yield return (null, attribute);
                    foreach (XmlNode child in description.ChildNodes)
                        if (child is XmlElement property) yield return (property, null);
                }
            }
        }

        private static bool IsRdf(XmlElement e, string localName)
            => e.NamespaceURI == RdfNamespace && e.LocalName == localName;

        private static bool IsPropertyAttribute(XmlAttribute a)
            => a.NamespaceURI.Length != 0 && a.NamespaceURI != RdfNamespace && a.NamespaceURI != XmlNamespace
               && a.NamespaceURI != XmlnsNamespace;

        /// <summary>The value of property element <paramref name="e"/>.</summary>
        private static Value Read(XmlElement e, int depth)
        {
            if (depth > MaxValueDepth) return new Value { Kind = Value.Shape.TooDeep };
            string language = e.GetAttributeNode("lang", XmlNamespace)?.Value;

            var resource = e.GetAttributeNode("resource", RdfNamespace);
            if (resource != null)
                return new Value { Kind = Value.Shape.Simple, Text = resource.Value, Language = language };

            string parseType = e.GetAttributeNode("parseType", RdfNamespace)?.Value;
            if (parseType == "Literal")
                return new Value { Kind = Value.Shape.Simple, Text = e.InnerXml, Language = language };

            var children = ElementChildren(e);
            if (parseType == "Resource")
                return Structure(e, children, depth, language);

            if (children.Count == 0)
            {
                foreach (XmlAttribute attribute in e.Attributes)
                    if (IsPropertyAttribute(attribute))
                        return Structure(e, children, depth, language);
                return new Value { Kind = Value.Shape.Simple, Text = e.InnerText, Language = language };
            }

            if (children.Count == 1)
            {
                var only = children[0];
                if (only.NamespaceURI == RdfNamespace
                    && (only.LocalName == "Bag" || only.LocalName == "Seq" || only.LocalName == "Alt"))
                {
                    var items = new List<Value>();
                    foreach (XmlNode item in only.ChildNodes)
                        if (item is XmlElement li && IsRdf(li, "li")) items.Add(Read(li, depth + 1));
                    return new Value { Kind = Value.Shape.Array, ArrayKind = only.LocalName, Items = items, Language = language };
                }
                if (IsRdf(only, "Description"))
                    return Structure(only, ElementChildren(only), depth, language);
            }
            return Structure(e, children, depth, language);
        }

        private static List<XmlElement> ElementChildren(XmlElement e)
        {
            var children = new List<XmlElement>();
            foreach (XmlNode child in e.ChildNodes)
                if (child is XmlElement element) children.Add(element);
            return children;
        }

        /// <summary>
        /// A structure of <paramref name="fields"/> and <paramref name="owner"/>'s property
        /// attributes — or, holding <c>rdf:value</c>, the qualified value it is.
        /// </summary>
        private static Value Structure(XmlElement owner, List<XmlElement> fields, int depth, string language)
        {
            var value = new Value
            {
                Kind = Value.Shape.Structure,
                Language = language,
                Fields = new List<(string, string, string, Value)>(),
            };
            foreach (XmlAttribute attribute in owner.Attributes)
            {
                if (attribute.NamespaceURI == RdfNamespace && attribute.LocalName == "value")
                    return new Value { Kind = Value.Shape.Simple, Text = attribute.Value, Language = language };
                if (IsPropertyAttribute(attribute))
                    value.Fields.Add((attribute.NamespaceURI, attribute.LocalName, attribute.Prefix,
                        new Value { Kind = Value.Shape.Simple, Text = attribute.Value }));
            }
            foreach (var field in fields)
            {
                if (IsRdf(field, "value"))
                {
                    var qualified = Read(field, depth + 1);
                    if (qualified.Language == null) qualified.Language = language;
                    return qualified;
                }
                value.Fields.Add((field.NamespaceURI, field.LocalName, field.Prefix, Read(field, depth + 1)));
            }
            return value;
        }

        private static bool Contains(Value value, Value.Shape shape)
        {
            if (value.Kind == shape) return true;
            if (value.Items != null)
                foreach (var item in value.Items)
                    if (Contains(item, shape)) return true;
            if (value.Fields != null)
                foreach (var field in value.Fields)
                    if (Contains(field.Value, shape)) return true;
            return false;
        }

        /// <summary>Whether <paramref name="value"/> has value type <paramref name="type"/>.</summary>
        private static bool Conforms(Value value, string type, ExtensionSchemas extensions)
        {
            // A type past the length bound is no type the packet can define (IsDefinedType says so,
            // as an extension-schema problem), so this rule does not judge it.
            if (type.Length > MaxValueTypeLength) return true;
            type = type.Trim();
            if (string.Equals(type, "Lang Alt", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Kind != Value.Shape.Array || value.ArrayKind != "Alt") return false;
                foreach (var item in value.Items)
                    if (item.Kind != Value.Shape.Simple || string.IsNullOrEmpty(item.Language)) return false;
                return true;
            }

            int space = type.IndexOf(' ');
            if (space > 0)
            {
                string head = type.Substring(0, space);
                string rest = type.Substring(space + 1);
                foreach (var kind in new[] { "Bag", "Seq", "Alt" })
                {
                    if (!string.Equals(head, kind, StringComparison.OrdinalIgnoreCase)) continue;
                    if (value.Kind != Value.Shape.Array || value.ArrayKind != kind) return false;
                    foreach (var item in value.Items)
                        if (!Conforms(item, rest, extensions)) return false;
                    return true;
                }
                foreach (var choice in new[] { "Closed Choice of ", "Open Choice of ", "Choice of " })
                    if (type.StartsWith(choice, StringComparison.OrdinalIgnoreCase))
                        return Conforms(value, type.Substring(choice.Length), extensions);
            }

            if (TextTypes.Contains(type)) return value.Kind == Value.Shape.Simple;
            switch (type.ToLowerInvariant())
            {
                case "integer": return value.Kind == Value.Shape.Simple && XmpLexical.IsInteger(value.Text);
                case "real": return value.Kind == Value.Shape.Simple && XmpLexical.IsReal(value.Text);
                case "rational": return value.Kind == Value.Shape.Simple && XmpLexical.IsRational(value.Text);
                case "boolean": return value.Kind == Value.Shape.Simple && XmpLexical.IsBoolean(value.Text);
                case "date": return value.Kind == Value.Shape.Simple && XmpLexical.IsDate(value.Text);
            }
            if (StructureTypes.Contains(type))
                return value.Kind == Value.Shape.Structure;
            if (extensions.TypeFields.TryGetValue(type, out var described))
            {
                if (value.Kind != Value.Shape.Structure) return false;
                foreach (var field in value.Fields)
                    if (field.Namespace != described.Namespace
                        || !described.Fields.TryGetValue(field.Name, out string fieldType)
                        || !Conforms(field.Value, fieldType, extensions))
                        return false;
                return true;
            }
            // A type no table names is the extension schema rules' finding, not this one's.
            return true;
        }

        /// <summary>
        /// The properties and value types the packet's <c>pdfaExtension:schemas</c> describe. A
        /// description this cannot read adds nothing; the description's own rules judge it.
        /// </summary>
        internal static ExtensionSchemas ReadExtensionSchemas(XmlDocument packet, Findings findings)
        {
            var result = new ExtensionSchemas();
            foreach (var (element, _) in TopLevelProperties(packet))
            {
                if (element == null || element.NamespaceURI != XmpPredefinedSchemas.ExtensionNamespace
                    || element.LocalName != "schemas") continue;
                var schemas = Read(element, 0);
                if (Contains(schemas, Value.Shape.TooDeep))
                {
                    findings.Truncated = true;
                    continue;
                }
                if (element.Prefix != "pdfaExtension")
                    Add(findings.ExtensionProblems, element.Prefix + ":schemas is not written under the prefix pdfaExtension");
                if (schemas.Kind != Value.Shape.Array || schemas.ArrayKind != "Bag")
                {
                    Add(findings.ExtensionProblems, "pdfaExtension:schemas is not a Bag");
                    continue;
                }
                foreach (var schema in schemas.Items)
                    if (CheckDescription(schema, SchemaDescription, findings.ExtensionProblems))
                        foreach (var valueType in Items(Field(schema, XmpPredefinedSchemas.SchemaNamespace, "valueType")))
                            if (CheckDescription(valueType, ValueTypeDescription, findings.ExtensionProblems))
                            {
                                string type = Field(valueType, XmpPredefinedSchemas.TypeNamespace, "type")?.Text?.Trim();
                                if (string.IsNullOrEmpty(type)) continue;
                                result.Types.Add(type);
                                if (result.TypeFields.ContainsKey(type)) continue;
                                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                                foreach (var field in Items(Field(valueType, XmpPredefinedSchemas.TypeNamespace, "field")))
                                {
                                    string name = Field(field, XmpPredefinedSchemas.FieldNamespace, "name")?.Text?.Trim();
                                    string fieldType = Field(field, XmpPredefinedSchemas.FieldNamespace, "valueType")?.Text?.Trim();
                                    if (!string.IsNullOrEmpty(name) && fieldType != null && !fields.ContainsKey(name))
                                        fields.Add(name, fieldType);
                                }
                                string ns = Field(valueType, XmpPredefinedSchemas.TypeNamespace, "namespaceURI")?.Text?.Trim();
                                result.TypeFields.Add(type, (ns, fields));
                            }
                foreach (var schema in schemas.Items)
                {
                    string uri = Field(schema, XmpPredefinedSchemas.SchemaNamespace, "namespaceURI")?.Text?.Trim();
                    if (uri != null)
                    {
                        if (!result.Properties.TryGetValue(uri, out var props))
                            result.Properties.Add(uri, props = new Dictionary<string, string>(StringComparer.Ordinal));
                        foreach (var property in Items(Field(schema, XmpPredefinedSchemas.SchemaNamespace, "property")))
                        {
                            string name = Field(property, XmpPredefinedSchemas.PropertyNamespace, "name")?.Text?.Trim();
                            string type = Field(property, XmpPredefinedSchemas.PropertyNamespace, "valueType")?.Text?.Trim();
                            if (!string.IsNullOrEmpty(name) && type != null && !props.ContainsKey(name))
                                props.Add(name, type);
                        }
                    }
                }
            }
            // Second pass, once every described type is known: each property's and field's value type.
            foreach (var (element, _) in TopLevelProperties(packet))
            {
                if (element == null || element.NamespaceURI != XmpPredefinedSchemas.ExtensionNamespace
                    || element.LocalName != "schemas") continue;
                var schemas = Read(element, 0);
                if (schemas.Kind != Value.Shape.Array || Contains(schemas, Value.Shape.TooDeep)) continue;
                foreach (var schema in schemas.Items)
                {
                    foreach (var property in Items(Field(schema, XmpPredefinedSchemas.SchemaNamespace, "property")))
                        if (CheckDescription(property, PropertyDescription, findings.ExtensionProblems))
                        {
                            RequireDefinedType(property, XmpPredefinedSchemas.PropertyNamespace, "pdfaProperty", result, findings);
                            string category = Field(property, XmpPredefinedSchemas.PropertyNamespace, "category")?.Text?.Trim();
                            if (category != "external" && category != "internal")
                                Add(findings.ExtensionProblems, "pdfaProperty:category \"" + category + "\" is neither external nor internal");
                        }
                    foreach (var valueType in Items(Field(schema, XmpPredefinedSchemas.SchemaNamespace, "valueType")))
                        foreach (var field in Items(Field(valueType, XmpPredefinedSchemas.TypeNamespace, "field")))
                            if (CheckDescription(field, FieldDescription, findings.ExtensionProblems))
                                RequireDefinedType(field, XmpPredefinedSchemas.FieldNamespace, "pdfaField", result, findings);
                }
            }
            return result;
        }

        /// <summary>One of the extension schema description's value types (ISO 19005-1 6.7.8 Tables 3 to 6).</summary>
        private sealed class Description
        {
            internal string Name;
            internal string Namespace;
            internal string Prefix;
            internal string[] Required;
            internal string[] OptionalSeqs;
        }

        private static readonly Description SchemaDescription = new Description
        {
            Name = "Schema", Namespace = XmpPredefinedSchemas.SchemaNamespace, Prefix = "pdfaSchema",
            Required = new[] { "schema", "namespaceURI", "prefix" }, OptionalSeqs = new[] { "property", "valueType" },
        };

        private static readonly Description PropertyDescription = new Description
        {
            Name = "Property", Namespace = XmpPredefinedSchemas.PropertyNamespace, Prefix = "pdfaProperty",
            Required = new[] { "name", "valueType", "category", "description" }, OptionalSeqs = new string[0],
        };

        private static readonly Description ValueTypeDescription = new Description
        {
            Name = "ValueType", Namespace = XmpPredefinedSchemas.TypeNamespace, Prefix = "pdfaType",
            Required = new[] { "type", "namespaceURI", "prefix", "description" }, OptionalSeqs = new[] { "field" },
        };

        private static readonly Description FieldDescription = new Description
        {
            Name = "Field", Namespace = XmpPredefinedSchemas.FieldNamespace, Prefix = "pdfaField",
            Required = new[] { "name", "valueType", "description" }, OptionalSeqs = new string[0],
        };

        /// <summary>
        /// Whether <paramref name="value"/> is a well-formed <paramref name="d"/>: a structure whose
        /// fields are its own, each required one present as text, each sequence a Seq, every field
        /// under the prefix the clause requires. Each problem is added to <paramref name="problems"/>.
        /// </summary>
        private static bool CheckDescription(Value value, Description d, DistinctEntries problems)
        {
            if (value.Kind != Value.Shape.Structure)
            {
                Add(problems, "a " + d.Name + " entry is not a structure");
                return false;
            }
            bool good = true;
            foreach (var field in value.Fields)
            {
                bool known = field.Namespace == d.Namespace
                    && (Array.IndexOf(d.Required, field.Name) >= 0 || Array.IndexOf(d.OptionalSeqs, field.Name) >= 0);
                if (!known)
                {
                    Add(problems, field.Prefix + ":" + field.Name + " is not a field of the " + d.Name + " type");
                    good = false;
                }
                else if (field.Prefix != d.Prefix)
                {
                    Add(problems, field.Prefix + ":" + field.Name + " is not written under the prefix " + d.Prefix);
                    good = false;
                }
            }
            foreach (var name in d.Required)
            {
                var field = Field(value, d.Namespace, name);
                if (field == null || field.Kind != Value.Shape.Simple)
                {
                    Add(problems, d.Prefix + ":" + name + (field == null ? " is missing from a " : " is not text in a ") + d.Name);
                    good = false;
                }
            }
            foreach (var name in d.OptionalSeqs)
            {
                var field = Field(value, d.Namespace, name);
                if (field != null && (field.Kind != Value.Shape.Array || field.ArrayKind != "Seq"))
                {
                    Add(problems, d.Prefix + ":" + name + " is not a Seq");
                    good = false;
                }
            }
            return good;
        }

        /// <summary>A property's or field's <c>valueType</c> names a type the specification or the packet defines.</summary>
        private static void RequireDefinedType(Value description, string ns, string prefix, ExtensionSchemas defined, Findings findings)
        {
            string type = Field(description, ns, "valueType")?.Text?.Trim() ?? "";
            if (!IsDefinedType(type, defined))
                Add(findings.ExtensionProblems, prefix + ":valueType \"" + type + "\" is a type neither the XMP Specification nor the packet defines");
        }

        private static bool IsDefinedType(string type, ExtensionSchemas defined)
        {
            // Each prefix (Bag, Choice of, …) is a call and a copy of the rest: the bound on the
            // length bounds both.
            if (type.Length > MaxValueTypeLength) return false;
            if (string.Equals(type, "Lang Alt", StringComparison.OrdinalIgnoreCase)) return true;
            int space = type.IndexOf(' ');
            if (space > 0)
            {
                string head = type.Substring(0, space);
                if (string.Equals(head, "Bag", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(head, "Seq", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(head, "Alt", StringComparison.OrdinalIgnoreCase))
                    return IsDefinedType(type.Substring(space + 1), defined);
                foreach (var choice in new[] { "Closed Choice of ", "Open Choice of ", "Choice of " })
                    if (type.StartsWith(choice, StringComparison.OrdinalIgnoreCase))
                        return IsDefinedType(type.Substring(choice.Length), defined);
            }
            return TextTypes.Contains(type) || LexicalTypes.Contains(type) || StructureTypes.Contains(type)
                || defined.Types.Contains(type);
        }

        private static Value Field(Value structure, string ns, string name)
        {
            if (structure?.Fields == null) return null;
            foreach (var field in structure.Fields)
                if (field.Namespace == ns && field.Name == name) return field.Value;
            return null;
        }

        private static IEnumerable<Value> Items(Value array)
            => array?.Kind == Value.Shape.Array ? array.Items : (IEnumerable<Value>)Array.Empty<Value>();
    }
}
