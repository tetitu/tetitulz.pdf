// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The optional content configuration dictionaries a document declares — <c>/OCProperties /D</c>
    /// and each element of <c>/Configs</c> (ISO 32000-1 §8.11.4.2, Tables 100 and 101) — with what the
    /// conformance rules ask of them: the <c>/Name</c>, whether two share one, which groups an
    /// <c>/Order</c> leaves out. PDF/A's A70–A72 judge them and PDF/X's X27 reports them; both read
    /// them here.
    /// </summary>
    internal static class OptionalContentConfigurations
    {
        /// <summary>The most <c>/Order</c> entries, nested arrays included, read for one document — each
        /// configuration's entries and its comparison against every group alike, since both counts are the
        /// file's and they multiply; past it the check reports itself partial rather than reading on.</summary>
        internal const int MaxOrderWork = 1_000_000;

        internal sealed class Configuration
        {
            internal PdfDictionary Dictionary;
            /// <summary><c>/D</c>, or <c>/Configs [n]</c>.</summary>
            internal string Where;
            /// <summary>The <c>/Name</c> text, or <c>null</c> when there is none that is a string.</summary>
            internal string Name;
        }

        /// <summary>The catalog's <c>/OCProperties</c>, or <c>null</c>.</summary>
        internal static PdfDictionary Properties(PdfDocument doc)
            => doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.OCProperties]) as PdfDictionary;

        /// <summary>
        /// The configurations <paramref name="ocProperties"/> declares, <c>/D</c> first, each dictionary
        /// once: a configuration listed as <c>/D</c> and again in <c>/Configs</c> is one configuration,
        /// and does not share its name with itself.
        /// </summary>
        internal static List<Configuration> Read(PdfDocument doc, PdfDictionary ocProperties)
        {
            var found = new List<Configuration>();
            var seen = new HashSet<PdfDictionary>();
            void Add(PdfObject value, string where)
            {
                if (!(doc.ResolveObject(value) is PdfDictionary config) || !seen.Add(config)) return;
                found.Add(new Configuration
                {
                    Dictionary = config,
                    Where = where,
                    Name = (doc.ResolveObject(config[PdfName.Name]) as PdfString)?.GetText(),
                });
            }

            if (ocProperties == null) return found;
            Add(ocProperties[PdfName.D], "/D");
            if (doc.ResolveObject(ocProperties[PdfName.Configs]) is PdfArray configs)
                for (int i = 0; i < configs.Count; i++)
                    Add(configs[i], "/Configs [" + i.ToString(CultureInfo.InvariantCulture) + "]");
            return found;
        }

        /// <summary>The names two or more of <paramref name="configurations"/> carry, each once, in order.</summary>
        internal static List<string> DuplicateNames(IEnumerable<Configuration> configurations)
        {
            var counts = new Dictionary<string, int>(System.StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var c in configurations)
            {
                if (c.Name == null) continue;
                counts.TryGetValue(c.Name, out int n);
                if (n == 1) order.Add(c.Name);
                counts[c.Name] = n + 1;
            }
            return order;
        }

        /// <summary>The groups <c>/OCProperties /OCGs</c> lists, resolved, each once, in order.</summary>
        internal static List<PdfDictionary> Groups(PdfDocument doc, PdfDictionary ocProperties)
        {
            var groups = new List<PdfDictionary>();
            var seen = new HashSet<PdfDictionary>();
            if (doc.ResolveObject(ocProperties?[PdfName.OCGs]) is PdfArray ocgs)
                for (int i = 0; i < ocgs.Count; i++)
                    if (doc.ResolveObject(ocgs[i]) is PdfDictionary g && seen.Add(g))
                        groups.Add(g);
            return groups;
        }

        /// <summary>
        /// How many of <paramref name="groups"/> the configuration's <c>/Order</c> does not name, nested
        /// arrays included (Table 101: an array names a group's children, a string labels it); <c>null</c>
        /// when it has no <c>/Order</c>. Each entry read and each group compared spends one unit of
        /// <paramref name="work"/>, which the caller starts at <see cref="MaxOrderWork"/> for the whole
        /// document; the walk is iterative, each array once, so the budget is its only bound.
        /// <paramref name="complete"/> is <c>false</c> when the work ran out, and the count then says nothing.
        /// </summary>
        internal static int? GroupsMissingFromOrder(PdfDocument doc, PdfDictionary config,
            IReadOnlyList<PdfDictionary> groups, ref int work, out bool complete)
        {
            complete = true;
            if (!(doc.ResolveObject(config[PdfName.Order]) is PdfArray order)) return null;
            // A budget already spent is not spent again: without this, every configuration after the one
            // that ran it out would read its first entry, and one sharing a long /Order the whole of it.
            if (work < 0) { complete = false; return null; }

            var named = new HashSet<PdfDictionary>();
            var seenArrays = new HashSet<PdfArray>();
            var stack = new Stack<PdfArray>();
            stack.Push(order);
            seenArrays.Add(order);
            while (stack.Count > 0)
            {
                var array = stack.Pop();
                for (int i = 0; i < array.Count; i++)
                {
                    if (--work < 0) { complete = false; return null; }
                    var item = doc.ResolveObject(array[i]);
                    if (item is PdfDictionary group) named.Add(group);
                    else if (item is PdfArray nested && seenArrays.Add(nested))
                        stack.Push(nested);
                }
            }

            work -= groups.Count;
            if (work < 0) { complete = false; return null; }
            int missing = 0;
            foreach (var g in groups)
                if (!named.Contains(g)) missing++;
            return missing;
        }
    }
}
