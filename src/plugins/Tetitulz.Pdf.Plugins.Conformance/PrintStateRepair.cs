// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Leaves out what PDF/X may forbid where the rule was not available (GAPS.md §12b), so the output
    /// is safe whichever way ISO 15930 reads: a graphics state's halftone and halftone phase, and an
    /// optional content configuration's missing or shared <c>/Name</c> and its <c>/AS</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The producer's answer to a rule nobody here has read.</b> The validator names these
    /// without judging them (X26, X27). A writer can do better than name them: if the output never holds
    /// the construct, no reading of the rule fails it. Common PDF/X producers do exactly this under
    /// PDF/X: no <c>/HT</c>, no transfer function and no halftone phase is written.</para>
    ///
    /// <para><b>Halftones.</b> <c>/HT</c> goes, and with it any halftone's <c>/TransferFunction</c>, the
    /// one X26 names; <c>/HTP</c> and PDF 2.0's <c>/HTO</c> go too. A halftone is a device instruction:
    /// a RIP screens with its own, and ISO 32000 lets a device ignore one, so the page's colours are
    /// what they were. What is lost is a screen the producer chose, and the result says how many.</para>
    ///
    /// <para><b>Optional content configurations.</b> Each gets a non-empty <c>/Name</c> no other
    /// carries — a name it has is kept, the first holder of a shared one keeps it — and loses its
    /// <c>/AS</c>, which changes a group's state by event (printing, viewing, exporting) and so lets
    /// what prints depend on the consumer. That is the strictest reading available, PDF/A-2's 6.9
    /// (X27's X-4 reading); <c>/Order</c> is left alone, its restriction being one ISO 15930-7:2010's
    /// foreword says that edition removed.</para>
    ///
    /// <para><b>How it writes.</b> Every object the cross-reference table lists is read once; a
    /// dictionary to change is changed in the nearest numbered object that holds it, a copy of the
    /// path to it written in one appended revision (<see cref="PdfPageContentAppender.AppendRevision"/>,
    /// as <see cref="PageBoxRepair"/> writes). Nothing to change is a byte-identical copy.</para>
    /// </remarks>
    internal static class PrintStateRepair
    {
        /// <summary>What <see cref="Repair"/> changed, and what it could not.</summary>
        internal sealed class Repaired
        {
            /// <summary>Graphics states that lost an <c>/HT</c>.</summary>
            internal int HalftonesRemoved;

            /// <summary>Graphics states that lost an <c>/HTP</c> or <c>/HTO</c>.</summary>
            internal int HalftonePhasesRemoved;

            /// <summary>Configurations given a <c>/Name</c> — none, an empty one, or one another held first.</summary>
            internal int ConfigurationsNamed;

            /// <summary>Configurations that lost an <c>/AS</c>.</summary>
            internal int AutoStatesRemoved;

            /// <summary>Graphics states changed — each once, whatever it lost.</summary>
            internal int StatesChanged;

            /// <summary>Configurations changed — each once, whatever it lost or gained.</summary>
            internal int ConfigurationsChanged;

            /// <summary>Objects the parser could not read, so nothing in them was changed.</summary>
            internal int Unreadable;

            /// <summary>Structures nested past <see cref="PdfLimits.MaxObjectGraphDepth"/>, not read further.</summary>
            internal int TooDeep;

            /// <summary>Names tried for the configurations renamed, each against every name taken: the
            /// naming's work, which one pass keeps near one a configuration.</summary>
            internal long NameCandidatesTried;
        }

        /// <param name="removeHalftones">Remove <c>/HT</c>, <c>/HTP</c> and <c>/HTO</c> from every graphics state.</param>
        /// <param name="repairConfigurations">Name every optional content configuration uniquely and remove its <c>/AS</c>.</param>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the original bytes plus the appended revision.</param>
        internal static Repaired Repair(Stream input, Stream output, bool removeHalftones, bool repairConfigurations)
        {
            var result = new Repaired();
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            var objects = new SortedDictionary<int, PdfObject>();
            if (doc.Parser != null)
                foreach (var kvp in doc.Parser.EnumerateAllObjects((number, ex) => result.Unreadable++))
                    if (kvp.Value != null && !(kvp.Value is PdfNull)) objects[kvp.Key] = kvp.Value;

            var walk = new Walk(doc, result, removeHalftones, repairConfigurations);
            foreach (var kvp in objects) walk.Classify(kvp.Key, kvp.Value);
            walk.ClassifyIndirectMaps(objects);
            if (repairConfigurations) walk.DecideNames();

            var revised = new List<IndirectObject>();
            foreach (var kvp in objects)
            {
                var rewritten = walk.Rewrite(kvp.Key, kvp.Value);
                if (ReferenceEquals(rewritten, kvp.Value)) continue;
                int generation = Generation(doc.CrossReferenceTable, kvp.Key);
                if (rewritten is PdfDictionary d)
                {
                    d.ObjectNumber = kvp.Key;
                    d.GenerationNumber = generation;
                }
                revised.Add(new IndirectObject(kvp.Key, generation, rewritten));
            }

            if (revised.Count == 0)
            {
                ms.Position = 0;
                ms.CopyTo(output);
                return result;
            }

            int newSize = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            PdfPageContentAppender.AppendRevision(ms, output, doc, revised, newSize);
            return result;
        }

        /// <summary>A superseded object keeps its generation (CLAUDE.md: the writer keeps it; only a 0 is corrected).</summary>
        private static int Generation(CrossReferenceTable table, int objectNumber)
        {
            var entry = table?.GetEntry(objectNumber);
            return entry != null && entry.Type == CrossReferenceEntry.EntryType.Normal ? entry.GenerationNumber : 0;
        }

        /// <summary>What a dictionary is, which decides what is changed in it.</summary>
        private enum Kind { Other, StateMap, State, OcProperties, Configs, Configuration }

        private sealed class Walk
        {
            private readonly PdfDocument _doc;
            private readonly Repaired _result;
            private readonly bool _halftones;
            private readonly bool _configurations;
            private readonly int _catalog;

            // Numbered objects by what they are, learned from the references that name them.
            private readonly HashSet<int> _stateMaps = new HashSet<int>();
            private readonly HashSet<int> _states = new HashSet<int>();
            private int _ocProperties = -1;
            private int _configsArray = -1;
            private readonly Dictionary<int, string> _configurationObjects = new Dictionary<int, string>();
            private readonly Dictionary<string, string> _newNames = new Dictionary<string, string>(System.StringComparer.Ordinal);

            internal Walk(PdfDocument doc, Repaired result, bool halftones, bool configurations)
            {
                _doc = doc;
                _result = result;
                _halftones = halftones;
                _configurations = configurations;
                _catalog = doc.Catalog?.Dictionary?.ObjectNumber ?? 0;
            }


            // ------------------------------------------------------------ the first pass

            /// <summary>Learns which numbered objects are graphics states and state maps, from every
            /// <c>/ExtGState</c> entry in the object's direct structure, and where the configurations are.</summary>
            internal void Classify(int number, PdfObject value)
            {
                if (_halftones) FindStateMaps(value, 0);
                if (_configurations && number == _catalog && value is PdfDictionary catalog)
                {
                    var ocp = catalog[PdfName.OCProperties];
                    if (ocp is PdfReference r) _ocProperties = r.ReferenceObjectNumber;
                    else if (ocp is PdfDictionary direct) ClassifyOcProperties(direct);
                }
            }

            private void FindStateMaps(PdfObject value, int depth)
            {
                if (depth > PdfLimits.MaxObjectGraphDepth) { _result.TooDeep++; return; }
                if (value is PdfDictionary d)
                {
                    foreach (var kvp in d)
                    {
                        if (kvp.Key.Value == PdfName.ExtGState.Value)
                        {
                            // A shading pattern's /ExtGState is one graphics state (ISO 32000-1 Table 76);
                            // a resource dictionary's is a map of them.
                            bool pattern = d[PdfName.PatternType] != null;
                            if (kvp.Value is PdfReference r) (pattern ? _states : _stateMaps).Add(r.ReferenceObjectNumber);
                            else if (!pattern && kvp.Value is PdfDictionary map) NoteStates(map);
                        }
                        else FindStateMaps(kvp.Value, depth + 1);
                    }
                }
                else if (value is PdfArray a)
                    for (int i = 0; i < a.Count; i++) FindStateMaps(a[i], depth + 1);
            }

            private void NoteStates(PdfDictionary map)
            {
                foreach (var kvp in map)
                    if (kvp.Value is PdfReference r) _states.Add(r.ReferenceObjectNumber);
            }

            /// <summary>An indirect map's own states, and an indirect /OCProperties' configurations, are
            /// known only once those objects are found.</summary>
            internal void ClassifyIndirectMaps(IDictionary<int, PdfObject> objects)
            {
                foreach (int n in _stateMaps)
                    if (objects.TryGetValue(n, out var map) && map is PdfDictionary d) NoteStates(d);
                if (_ocProperties > 0 && objects.TryGetValue(_ocProperties, out var ocp) && ocp is PdfDictionary od)
                    ClassifyOcProperties(od);
                if (_configsArray > 0 && objects.TryGetValue(_configsArray, out var arr) && arr is PdfArray configs)
                    NoteConfigs(configs);
            }

            private void ClassifyOcProperties(PdfDictionary ocp)
            {
                if (ocp[PdfName.D] is PdfReference d) _configurationObjects[d.ReferenceObjectNumber] = "/D";
                var configs = ocp[PdfName.Configs];
                if (configs is PdfReference r) _configsArray = r.ReferenceObjectNumber;
                else if (configs is PdfArray a) NoteConfigs(a);
            }

            private void NoteConfigs(PdfArray configs)
            {
                for (int i = 0; i < configs.Count; i++)
                    if (configs[i] is PdfReference r && !_configurationObjects.ContainsKey(r.ReferenceObjectNumber))
                        _configurationObjects[r.ReferenceObjectNumber] = ConfigsWhere(i);
            }

            private static string ConfigsWhere(int i) => "/Configs [" + i.ToString(CultureInfo.InvariantCulture) + "]";

            /// <summary>
            /// The name each configuration ends with, by its place (<c>/D</c>, <c>/Configs [i]</c>), from
            /// <see cref="OptionalContentConfigurations"/>' reading: a name held keeps it, the first holder of
            /// a shared one keeps it, and the rest get <c>Default</c> (for <c>/D</c>) or <c>Configuration
            /// n</c>, numbered past any name already taken.
            /// </summary>
            /// <remarks>
            /// Each stem's search resumes where its last one stopped: names are only ever added, so every
            /// candidate before it is still taken. Restarted at the bare stem, it was quadratic in the
            /// configurations — <c>/Configs</c> has no count bound, and a megabyte of <c>&lt;&lt;&gt;&gt;</c>
            /// is a quarter of a million (a hundred thousand took six minutes).
            /// </remarks>
            internal void DecideNames()
            {
                var read = OptionalContentConfigurations.Read(_doc, OptionalContentConfigurations.Properties(_doc));
                var taken = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var c in read)
                    if (!string.IsNullOrEmpty(c.Name)) taken.Add(c.Name);

                // The next candidate for each stem: 1 is the bare stem, n the stem and " n".
                var nextCandidate = new Dictionary<string, int>(System.StringComparer.Ordinal);
                var kept = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var c in read)
                {
                    if (!string.IsNullOrEmpty(c.Name) && kept.Add(c.Name)) continue;
                    string stem = c.Where == "/D" ? "Default" : "Configuration";
                    int n = nextCandidate.TryGetValue(stem, out int resume) ? resume : 1;
                    string name = Candidate(stem, n);
                    _result.NameCandidatesTried++;
                    while (taken.Contains(name)) { name = Candidate(stem, ++n); _result.NameCandidatesTried++; }
                    nextCandidate[stem] = n + 1;
                    taken.Add(name);
                    kept.Add(name);
                    _newNames[c.Where] = name;
                }
            }

            private static string Candidate(string stem, int n)
                => n == 1 ? stem : stem + " " + n.ToString(CultureInfo.InvariantCulture);

            // ------------------------------------------------------------ the second pass

            /// <summary>The object as it should be written: the same instance when nothing in it changes.</summary>
            internal PdfObject Rewrite(int number, PdfObject value)
            {
                Kind kind = _states.Contains(number) ? Kind.State
                          : _stateMaps.Contains(number) ? Kind.StateMap
                          : number == _ocProperties ? Kind.OcProperties
                          : number == _configsArray ? Kind.Configs
                          : _configurationObjects.ContainsKey(number) ? Kind.Configuration
                          : Kind.Other;
                string where = _configurationObjects.TryGetValue(number, out var w) ? w : null;
                return Rewrite(value, kind, where, number == _catalog, 0);
            }

            private PdfObject Rewrite(PdfObject value, Kind kind, string where, bool isCatalog, int depth)
            {
                if (depth > PdfLimits.MaxObjectGraphDepth) { _result.TooDeep++; return value; }
                if (value is PdfArray array)
                {
                    PdfArray copy = null;
                    for (int i = 0; i < array.Count; i++)
                    {
                        var item = array[i];
                        var next = kind == Kind.Configs && item is PdfDictionary
                            ? Rewrite(item, Kind.Configuration, ConfigsWhere(i), false, depth + 1)
                            : kind == Kind.Other ? Rewrite(item, Kind.Other, null, false, depth + 1) : item;
                        if (!ReferenceEquals(next, item))
                        {
                            if (copy == null) { copy = new PdfArray(); for (int j = 0; j < i; j++) copy.Add(array[j]); }
                        }
                        copy?.Add(next);
                    }
                    return copy ?? (PdfObject)array;
                }
                if (!(value is PdfDictionary dict)) return value;

                switch (kind)
                {
                    case Kind.State:
                        return _halftones ? WithoutHalftone(dict) : dict;
                    case Kind.Configuration:
                        return _configurations ? Repaired(dict, where) : dict;
                }

                PdfDictionary result = null;
                foreach (var kvp in dict)
                {
                    PdfObject next = kvp.Value;
                    if (kind == Kind.StateMap)
                    {
                        if (kvp.Value is PdfDictionary) next = Rewrite(kvp.Value, Kind.State, null, false, depth + 1);
                    }
                    else if (kind == Kind.OcProperties)
                    {
                        if (kvp.Key.Value == PdfName.D.Value && kvp.Value is PdfDictionary)
                            next = Rewrite(kvp.Value, Kind.Configuration, "/D", false, depth + 1);
                        else if (kvp.Key.Value == PdfName.Configs.Value && kvp.Value is PdfArray)
                            next = Rewrite(kvp.Value, Kind.Configs, null, false, depth + 1);
                    }
                    else if (kvp.Key.Value == PdfName.ExtGState.Value)
                    {
                        if (kvp.Value is PdfDictionary)
                            next = Rewrite(kvp.Value, dict[PdfName.PatternType] != null ? Kind.State : Kind.StateMap, null, false, depth + 1);
                    }
                    else if (isCatalog && kvp.Key.Value == PdfName.OCProperties.Value)
                    {
                        if (kvp.Value is PdfDictionary) next = Rewrite(kvp.Value, Kind.OcProperties, null, false, depth + 1);
                    }
                    else
                        next = Rewrite(kvp.Value, Kind.Other, null, false, depth + 1);

                    if (!ReferenceEquals(next, kvp.Value))
                    {
                        result ??= PdfDictionary.CloneShallow(dict);
                        result.Set(kvp.Key, next);
                    }
                }
                return result ?? dict;
            }

            private PdfDictionary WithoutHalftone(PdfDictionary state)
            {
                bool ht = state[PdfName.HT] != null;
                bool phase = state[PdfName.HTP] != null || state[PdfName.HTO] != null;
                if (!ht && !phase) return state;
                var copy = PdfDictionary.CloneShallow(state);
                copy.Remove(PdfName.HT);
                copy.Remove(PdfName.HTP);
                copy.Remove(PdfName.HTO);
                if (ht) _result.HalftonesRemoved++;
                if (phase) _result.HalftonePhasesRemoved++;
                _result.StatesChanged++;
                return copy;
            }

            private PdfDictionary Repaired(PdfDictionary config, string where)
            {
                bool rename = where != null && _newNames.ContainsKey(where);
                bool autoState = config[PdfName.AS] != null;
                if (!rename && !autoState) return config;
                var copy = PdfDictionary.CloneShallow(config);
                if (rename)
                {
                    copy.Set(PdfName.Name, new PdfString(_newNames[where]));
                    _result.ConfigurationsNamed++;
                }
                if (autoState)
                {
                    copy.Remove(PdfName.AS);
                    _result.AutoStatesRemoved++;
                }
                _result.ConfigurationsChanged++;
                return copy;
            }
        }
    }
}
