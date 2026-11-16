// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// JSON round-trip for PDF outlines (bookmarks): each item's <c>title</c>, <c>page</c>,
    /// <c>top</c> (the destination's top in user space), <c>open</c>, <c>color</c> (red, green and
    /// blue from 0 to 1), <c>bold</c>, <c>italic</c> and <c>children</c> — a member that is at its
    /// default is left out. Hand-written encoder/decoder avoids pulling System.Text.Json into
    /// netstandard2.0.
    /// </summary>
    public static class PdfOutlineJson
    {
        /// <summary>Reads outline from PDF as an indented JSON string.</summary>
        public static string Export(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var bookmarks = PdfBookmarksManager.ReadBookmarks(input);
            var sb = new StringBuilder();
            WriteArray(sb, bookmarks, indent: 0);
            return sb.ToString();
        }

        /// <summary>Writes the PDF with its outline replaced by the supplied JSON tree.</summary>
        public static void Import(Stream input, Stream output, string json)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (json == null)   throw new ArgumentNullException(nameof(json));

            var p = new JsonParser(json);
            var nodes = p.ReadArray();
            var bookmarks = new List<PdfBookmark>();
            BuildBookmarks(nodes, bookmarks, 0);
            PdfBookmarksManager.WriteBookmarks(input, output, bookmarks);
        }

        // ---- encoder ----

        private static void WriteArray(StringBuilder sb, IReadOnlyList<PdfBookmark> list, int indent)
        {
            if (list.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('\n');
                Indent(sb, indent + 1);
                WriteObject(sb, list[i], indent + 1);
            }
            sb.Append('\n');
            Indent(sb, indent);
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, PdfBookmark b, int indent)
        {
            sb.Append("{\n");
            Indent(sb, indent + 1);
            sb.Append("\"title\": ");
            WriteString(sb, b.Title ?? string.Empty);
            if (b.PageNumber.HasValue)
                Member(sb, indent, "page", b.PageNumber.Value.ToString(CultureInfo.InvariantCulture));
            if (b.Top.HasValue)
                Member(sb, indent, "top", b.Top.Value.ToString("R", CultureInfo.InvariantCulture));
            if (b.IsOpen)
                Member(sb, indent, "open", "true");
            if (b.Color != null && b.Color.Count == 3)
                Member(sb, indent, "color", "[" + b.Color[0].ToString("R", CultureInfo.InvariantCulture) + ", " +
                                            b.Color[1].ToString("R", CultureInfo.InvariantCulture) + ", " +
                                            b.Color[2].ToString("R", CultureInfo.InvariantCulture) + "]");
            if (b.IsBold)
                Member(sb, indent, "bold", "true");
            if (b.IsItalic)
                Member(sb, indent, "italic", "true");
            if (b.Children != null && b.Children.Count > 0)
            {
                sb.Append(",\n");
                Indent(sb, indent + 1);
                sb.Append("\"children\": ");
                WriteArray(sb, b.Children, indent + 1);
            }
            sb.Append('\n');
            Indent(sb, indent);
            sb.Append('}');
        }

        /// <summary>One more member of an object, after the title: its name and its JSON <paramref name="value"/> as written.</summary>
        private static void Member(StringBuilder sb, int indent, string name, string value)
        {
            sb.Append(",\n");
            Indent(sb, indent + 1);
            sb.Append('"').Append(name).Append("\": ").Append(value);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("  ");
        }

        // ---- decoder ----

        private sealed class JsonParser
        {
            private readonly string _s;
            private int _i;
            private int _depth;
            // Bounds object/array nesting; deeply nested JSON ([[[[...]]]])
            // would otherwise stack-overflow (uncatchable in .NET).
            private const int MaxJsonDepth = 200;
            public JsonParser(string s) { _s = s; _i = 0; }

            public List<object> ReadArray()
            {
                Skip();
                Expect('[');
                var list = new List<object>();
                Skip();
                if (Peek() == ']') { _i++; return list; }
                while (true)
                {
                    Skip();
                    list.Add(ReadValue());
                    Skip();
                    char c = Read();
                    if (c == ',') continue;
                    if (c == ']') return list;
                    throw new FormatException($"JSON: expected ',' or ']' at {_i}");
                }
            }

            private object ReadValue()
            {
                Skip();
                char c = Peek();
                if (c == '{' || c == '[')
                {
                    if (++_depth > MaxJsonDepth)
                        throw new FormatException($"JSON: nesting exceeds {MaxJsonDepth} levels");
                    object nested = c == '{' ? ReadObject() : (object)ReadArray();
                    _depth--;
                    return nested;
                }
                if (c == '"') return ReadString();
                if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                if (TryLiteral("true"))  return true;
                if (TryLiteral("false")) return false;
                if (TryLiteral("null"))  return null;
                throw new FormatException($"JSON: unexpected char '{c}' at {_i}");
            }

            /// <summary>
            /// Consumes <paramref name="word"/> when the input continues with it, comparing in place.
            /// </summary>
            /// <remarks>
            /// This was <c>_s.Substring(_i).StartsWith(word)</c>, which <b>copies the rest of the
            /// input</b> to look at four characters — and a <c>null</c> took three such copies, one
            /// per literal tried. An array of N literals therefore cost O(N²): about half a
            /// megabyte of <c>null,</c> copied hundreds of gigabytes before the import could write
            /// anything, which is a hang for an input small enough to arrive as a request body.
            /// </remarks>
            private bool TryLiteral(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
                _i += word.Length;
                return true;
            }

            private Dictionary<string, object> ReadObject()
            {
                Expect('{');
                var d = new Dictionary<string, object>();
                Skip();
                if (Peek() == '}') { _i++; return d; }
                while (true)
                {
                    Skip();
                    string key = ReadString();
                    Skip(); Expect(':');
                    Skip();
                    d[key] = ReadValue();
                    Skip();
                    char c = Read();
                    if (c == ',') continue;
                    if (c == '}') return d;
                    throw new FormatException($"JSON: expected ',' or '}}' at {_i}");
                }
            }

            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\' && _i < _s.Length)
                    {
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"':  sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/':  sb.Append('/');  break;
                            case 'n':  sb.Append('\n'); break;
                            case 'r':  sb.Append('\r'); break;
                            case 't':  sb.Append('\t'); break;
                            case 'b':  sb.Append('\b'); break;
                            case 'f':  sb.Append('\f'); break;
                            case 'u':
                                if (_i + 4 <= _s.Length)
                                {
                                    string hex = _s.Substring(_i, 4);
                                    _i += 4;
                                    sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                }
                                break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(c);
                }
                throw new FormatException("JSON: unterminated string");
            }

            private double ReadNumber()
            {
                int start = _i;
                if (Peek() == '-') _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }
                return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private void Skip()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private char Peek() => _i < _s.Length ? _s[_i] : '\0';
            private char Read() => _i < _s.Length ? _s[_i++] : throw new FormatException("JSON: unexpected EOF");
            private void Expect(char c)
            {
                if (Peek() != c) throw new FormatException($"JSON: expected '{c}' at {_i}");
                _i++;
            }
        }

        private static void BuildBookmarks(List<object> nodes, List<PdfBookmark> output, int depth)
        {
            // Defensive: the JSON parser already caps nesting, but bound this
            // independent recursion too.
            if (depth >= 256) return;
            foreach (var raw in nodes)
            {
                if (!(raw is Dictionary<string, object> d)) continue;
                string title = d.TryGetValue("title", out var t) ? t as string ?? string.Empty : string.Empty;
                // A number past an int saturates (a cast is the type's minimum on .NET Framework).
                int? page = d.TryGetValue("page", out var p) && p is double pd ? (int?)SaturatingCast.ToInt(pd) : null;
                var children = new List<PdfBookmark>();
                if (d.TryGetValue("children", out var ch) && ch is List<object> chList)
                    BuildBookmarks(chList, children, depth + 1);
                output.Add(new PdfBookmark(title, page, depth, children,
                    FiniteFloat(d, "top"), Flag(d, "open"), Color(d), Flag(d, "bold"), Flag(d, "italic")));
            }
        }

        private static bool Flag(Dictionary<string, object> d, string name)
            => d.TryGetValue(name, out var v) && v is bool b && b;

        /// <summary>A number member within a <c>float</c>'s range, else none: the bookmark refuses NaN and infinity.</summary>
        private static float? FiniteFloat(Dictionary<string, object> d, string name)
            => d.TryGetValue(name, out var v) && v is double x && !double.IsNaN(x) && !double.IsInfinity(x)
               && x >= float.MinValue && x <= float.MaxValue ? (float?)(float)x : null;

        /// <summary>The <c>color</c> member as three numbers clamped to 0–1, or null for anything else.</summary>
        private static float[] Color(Dictionary<string, object> d)
        {
            if (!d.TryGetValue("color", out var v) || !(v is List<object> list) || list.Count != 3) return null;
            var rgb = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!(list[i] is double x) || double.IsNaN(x) || double.IsInfinity(x)) return null;
                rgb[i] = (float)Math.Max(0, Math.Min(1, x));
            }
            return rgb;
        }
    }
}
