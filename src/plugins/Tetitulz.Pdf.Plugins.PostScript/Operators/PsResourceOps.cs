// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Level 2 resources (PLRM §3.9): <c>defineresource</c>, <c>undefineresource</c>,
    /// <c>findresource</c>, <c>resourcestatus</c> and <c>resourceforall</c>, over one dictionary of
    /// instances a category, kept with the program's dictionaries (<see cref="PsDictStack.Resources"/>)
    /// so a child interpreter — a pattern's, a glyph's — sees what its program defined. <c>Font</c> is
    /// <c>definefont</c> and <c>findfont</c> spelled through the machinery; <c>Encoding</c> answers a
    /// defined encoding before a built-in one; the implicit categories this interpreter has
    /// (<c>ImageType</c>, <c>FontType</c>, <c>ShadingType</c>, …) answer what it carries. All five were
    /// arity-table entries answering null, so Illustrator's
    /// <c>/AdobeProc … /ProcSet defineresource</c> lost its procedure set and the
    /// <c>/AdobeProc /ProcSet findresource begin</c> after it failed.
    /// </summary>
    internal static class PsResourceOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("defineresource", DefineResource);
            op("undefineresource", UndefineResource);
            op("findresource", FindResource);
            op("resourcestatus", ResourceStatus);
            op("resourceforall", ResourceForAll);
        }

        /// <summary>
        /// The instances each implicit category has (PLRM Table 3.9): what this interpreter carries,
        /// each its own value. <c>Filter</c> lists the filters a data source may be read through.
        /// </summary>
        private static readonly Dictionary<string, object[]> Implicit = new(StringComparer.Ordinal)
        {
            ["ColorSpaceFamily"] = new object[] { "DeviceGray", "DeviceRGB", "DeviceCMYK", "Indexed", "Separation", "DeviceN",
                                                  "Pattern", "CIEBasedA", "CIEBasedABC", "CIEBasedDEF", "CIEBasedDEFG", "ICCBased" },
            ["Filter"] = new object[] { "ASCIIHexDecode", "ASCII85Decode", "RunLengthDecode", "LZWDecode", "FlateDecode", "DCTDecode",
                                        "CCITTFaxDecode", "SubFileDecode", "ReusableStreamDecode" },
            ["FMapType"] = new object[] { 2, 3, 4, 5, 6, 7, 8, 9 },
            ["FontType"] = new object[] { 0, 1, 3, 42 },
            ["FormType"] = new object[] { 1 },
            ["FunctionType"] = new object[] { 0, 2, 3 },
            ["ImageType"] = new object[] { 1, 3, 4 },
            ["PatternType"] = new object[] { 1, 2 },
            ["ShadingType"] = new object[] { 1, 2, 3, 4, 5, 6, 7 },
        };

        /// <summary><c>key instance category defineresource instance</c>.</summary>
        private static void DefineResource(PsInterpreter i)
        {
            string category = CategoryName(i.Operands.Pop(), "defineresource");
            PsObject instance = i.Operands.Pop();
            PsObject key = i.Operands.Pop();
            CheckKey(key, "defineresource");

            if (category == "Font")
            {
                i.Operands.Push(key);
                i.Operands.Push(instance);
                PsTextOps.DefineFontFromResource(i);
                return;
            }
            if (Implicit.ContainsKey(category))
                throw new PsErrorException(PsErrorKind.InvalidAccess, "defineresource", $"/{category} is an implicit category");

            // A CIDFont's program is read where it is defined, as a font's is at definefont.
            if (category == "CIDFont" && instance.Type == PsType.Dictionary && KeyText(key) is string cidFontName)
                PsTextOps.DefineCidFont(i, cidFontName, instance.AsDict());
            // A CMap the CIDInit operators collected is made one when it is defined.
            if (category == "CMap" && instance.Type == PsType.Dictionary)
                PsCMapOps.Define(i, key, instance.AsDict());

            i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "defineresource");
            Instances(i, category, create: true).Put(key, instance);
            i.Operands.Push(instance);
        }

        /// <summary><c>key category undefineresource</c>: the instance removed; one never defined is no error.</summary>
        private static void UndefineResource(PsInterpreter i)
        {
            string category = CategoryName(i.Operands.Pop(), "undefineresource");
            PsObject key = i.Operands.Pop();
            CheckKey(key, "undefineresource");
            if (category == "Font")
            {
                i.Operands.Push(key);
                PsTextOps.UndefineFontFromResource(i);
                return;
            }
            Instances(i, category, create: false)?.Remove(key);
        }

        /// <summary>
        /// <c>key category findresource instance</c>, or <c>undefinedresource</c> where there is none
        /// — what a program's <c>{ … findresource } stopped</c> and <c>resourcestatus</c> probes are
        /// written to see.
        /// </summary>
        private static void FindResource(PsInterpreter i)
        {
            string category = CategoryName(i.Operands.Pop(), "findresource");
            PsObject key = i.Operands.Pop();
            if (category == "Font" && (key.Type == PsType.Name || key.Type == PsType.String))
            {
                i.Operands.Push(key);
                PsTextOps.FindFont(i);
                return;
            }
            if (TryFind(i, category, key, out PsObject instance))
            {
                i.Operands.Push(instance);
                return;
            }
            throw new PsErrorException(PsErrorKind.UndefinedResource, "findresource", $"{Describe(key)} in /{category}");
        }

        /// <summary><c>key category resourcestatus status size true</c> (defined, in VM: <c>0 0</c>), or <c>false</c>.</summary>
        private static void ResourceStatus(PsInterpreter i)
        {
            string category = CategoryName(i.Operands.Pop(), "resourcestatus");
            PsObject key = i.Operands.Pop();
            bool found = category == "Font"
                ? key.Type == PsType.Name && PsTextOps.FontExists(i, key.AsName())
                : TryFind(i, category, key, out _);
            if (!found) { i.Operands.Push(PsObject.False); return; }
            i.Operands.Push(PsObject.Int(0));
            i.Operands.Push(PsObject.Int(0));
            i.Operands.Push(PsObject.True);
        }

        /// <summary>
        /// <c>template proc scratch category resourceforall</c>: <paramref name="i"/>'s proc run once a
        /// key matching the template (<c>*</c> any run, <c>?</c> one character, <c>\</c> the next taken
        /// as it is), the key copied into the scratch string and its substring pushed — through a step
        /// loop frame, a loop context for <c>exit</c>, the keys listed when it starts.
        /// </summary>
        private static void ResourceForAll(PsInterpreter i)
        {
            string category = CategoryName(i.Operands.Pop(), "resourceforall");
            PsObject scratch = i.Operands.Pop();
            PsObject proc = i.Operands.Pop();
            PsObject template = i.Operands.Pop();
            if (scratch.Type != PsType.String || template.Type != PsType.String || proc.Type != PsType.Array || !proc.IsExecutable)
                throw new PsErrorException(PsErrorKind.TypeCheck, "resourceforall");

            List<int> tokens = Tokens(i.LatinFromOperand(template.AsString(), "resourceforall"));
            var keys = new List<string>();
            foreach (string key in Keys(i, category))
            {
                // Each key read and matched is charged as it is done: the match is template × key in
                // the worst case, and both are the program's.
                bool matched = Matches(tokens, key, out long steps);
                i.Tick(1 + (key.Length + steps) / 64);
                if (matched) keys.Add(key);
            }
            i.Tick(1 + keys.Count);

            PsString buffer = scratch.AsString();
            int next = 0;
            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Callback,
                Advance = interpreter =>
                {
                    if (next >= keys.Count) return null;
                    string key = keys[next++];
                    if (key.Length > buffer.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "resourceforall", "the scratch string is shorter than a key");
                    for (int k = 0; k < key.Length; k++) buffer[k] = (byte)key[k];
                    interpreter.Operands.Push(PsObject.Str(buffer.Interval(0, key.Length)));
                    return proc.AsArray();
                },
            });
        }

        private static IEnumerable<string> Keys(PsInterpreter i, string category)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (category == "Font")
            {
                foreach (var entry in i.FontDirectory.Entries)
                    if (KeyText(entry.Key) is string font && seen.Add(font)) yield return font;
                yield break;
            }
            if (Implicit.TryGetValue(category, out object[] values))
            {
                foreach (object v in values) yield return v.ToString();
                yield break;
            }
            if (category == "Encoding")
                foreach (string name in new[] { "StandardEncoding", "ISOLatin1Encoding" })
                    if (seen.Add(name)) yield return name;
            PsDict instances = Instances(i, category, create: false);
            if (instances == null) yield break;
            foreach (var entry in instances.Entries)
                if (KeyText(entry.Key) is string text && seen.Add(text)) yield return text;
        }

        /// <summary>The instance <paramref name="key"/> names in <paramref name="category"/> — <c>composefont</c>'s CIDFont lookup.</summary>
        internal static bool TryFindInstance(PsInterpreter i, string category, PsObject key, out PsObject instance)
            => TryFind(i, category, key, out instance);

        /// <summary><c>key instance category defineresource</c> for an operator that defines one itself — <c>StartData</c>'s FontSet.</summary>
        internal static void DefineInstance(PsInterpreter i, string category, PsObject key, PsObject instance)
        {
            i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "defineresource");
            Instances(i, category, create: true).Put(key, instance);
        }

        private static bool TryFind(PsInterpreter i, string category, PsObject key, out PsObject instance)
        {
            instance = PsObject.Null;
            if (Instances(i, category, create: false) is PsDict instances && instances.TryGet(key, out instance)) return true;

            // The FontSetInit and CIDInit procedure sets (PLRM §5.8.1, §5.11.3) are built in: made at
            // their first lookup and kept as instances, so the program's `begin` and StartData's end
            // find one dictionary.
            if (category == "ProcSet" && KeyText(key) is "FontSetInit" or "CIDInit")
            {
                instance = PsObject.Dict(KeyText(key) == "CIDInit" ? PsFontSetOps.CidInit() : PsFontSetOps.FontSetInit());
                DefineInstance(i, category, key, instance);
                return true;
            }
            if (category == "Encoding" && key.Type == PsType.Name && PsEncodings.Build(key.AsName()) is PsArray encoding)
            {
                instance = PsObject.Arr(encoding);
                return true;
            }
            if (Implicit.TryGetValue(category, out object[] values))
            {
                // The key's text once, charged: a string key is decoded, and it was decoded afresh,
                // uncharged, for every value the category has.
                if (key.Type == PsType.String) i.Tick(key.AsString().Length / 64);
                string text = KeyText(key);
                foreach (object v in values)
                    if (text == (v is int n0 ? n0.ToString(System.Globalization.CultureInfo.InvariantCulture) : (string)v))
                    {
                        instance = v is int n ? PsObject.Int(n) : PsObject.LitName((string)v);
                        return true;
                    }
            }
            return false;
        }

        private static PsDict Instances(PsInterpreter i, string category, bool create)
        {
            if (i.Dicts.Resources.TryGetValue(category, out PsDict instances)) return instances;
            if (!create) return null;
            instances = new PsDict();
            i.Dicts.Resources[category] = instances;
            return instances;
        }

        private static string CategoryName(PsObject category, string op) =>
            category.Type == PsType.Name ? category.AsName()
            : throw new PsErrorException(PsErrorKind.TypeCheck, op, "the category is not a name");

        private static void CheckKey(PsObject key, string op)
        {
            if (key.Type == PsType.Null) throw new PsErrorException(PsErrorKind.TypeCheck, op, "a resource key is null");
        }

        private static string KeyText(PsObject key) =>
            key.Type == PsType.Name ? key.AsName()
            : key.Type == PsType.String ? key.AsString().AsLatin1()
            : key.IsNumber ? (key.Type == PsType.Integer ? key.AsLong().ToString(System.Globalization.CultureInfo.InvariantCulture) : null)
            : null;

        /// <summary>
        /// A key for a message: its first 64 characters. A string key is decoded no further — the
        /// message reaches every <c>OnEvent</c> subscriber and <c>LastError</c>, and quoted a
        /// megabyte key whole.
        /// </summary>
        private static string Describe(PsObject key)
        {
            if (key.Type == PsType.String && key.AsString().Length > 65)
                key = PsObject.Str(key.AsString().Interval(0, 65));
            return KeyText(key) is string text ? "/" + PsTypeOps.Ellipsis(text) : key.TypeName;
        }

        /// <summary>A template as tokens: -1 a star, -2 any one character, else the character itself (<c>\</c> takes the next as it is).</summary>
        private static List<int> Tokens(string template)
        {
            var tokens = new List<int>(template.Length);
            for (int k = 0; k < template.Length; k++)
            {
                char c = template[k];
                if (c == '\\' && k + 1 < template.Length) tokens.Add(template[++k]);
                else tokens.Add(c == '*' ? -1 : c == '?' ? -2 : c);
            }
            return tokens;
        }

        /// <summary>
        /// PLRM's template match: <c>*</c> any run, <c>?</c> any one character, <c>\</c> the next
        /// character as it is — the greedy match that backtracks only to the last star, never
        /// exponential, but template × text in the worst case (<c>*a*a…b</c> against a run of
        /// <c>a</c>): each backtrack restarts the template after the star. <paramref name="steps"/>
        /// counts the comparisons, which the caller charges.
        /// </summary>
        private static bool Matches(List<int> tokens, string text, out long steps)
        {
            steps = 0;
            int p = 0, t = 0, star = -1, resume = 0;
            while (t < text.Length)
            {
                steps++;
                if (p < tokens.Count && (tokens[p] == -2 || tokens[p] == text[t])) { p++; t++; continue; }
                if (p < tokens.Count && tokens[p] == -1) { star = p++; resume = t; continue; }
                if (star < 0) return false;
                p = star + 1;
                t = ++resume;
            }
            while (p < tokens.Count && tokens[p] == -1) p++;
            return p == tokens.Count;
        }
    }
}
