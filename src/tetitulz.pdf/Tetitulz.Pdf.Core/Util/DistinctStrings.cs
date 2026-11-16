// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// Strings in the order first added, each once — the notes and warnings an operation collects
    /// about the objects of a file, one per object it could not handle.
    /// </summary>
    /// <remarks>
    /// <para><b>A set beside the list decides.</b> These were kept distinct by a search of the list
    /// before each addition, and a note names its object, so a file of n such objects cost
    /// n² / 2 comparisons.</para>
    /// <para><b>Joined under a cap.</b> The text a <c>Last*</c> slot shows names the first
    /// <see cref="MaxJoined"/> and counts the rest: joined whole, a file chose its length.</para>
    /// </remarks>
    internal sealed class DistinctStrings
    {
        /// <summary>How many entries <see cref="Joined"/> names before it counts the rest.</summary>
        internal const int MaxJoined = 20;

        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The entries, in the order first added.</summary>
        internal List<string> Items { get; } = new List<string>();

        internal int Count => Items.Count;

        /// <summary>How many entries the set holds — every one, while it is the set that decides.</summary>
        internal int Seen => _seen.Count;

        /// <summary>Adds <paramref name="text"/> unless it is already here; whether it was added.</summary>
        internal bool Add(string text)
        {
            if (text == null || !_seen.Add(text)) return false;
            Items.Add(text);
            return true;
        }

        /// <summary>The first <see cref="MaxJoined"/> entries joined by <paramref name="separator"/>, and how many more there are.</summary>
        internal string Joined(string separator)
        {
            if (Items.Count <= MaxJoined) return string.Join(separator, Items);
            var sb = new StringBuilder();
            for (int i = 0; i < MaxJoined; i++) { if (i > 0) sb.Append(separator); sb.Append(Items[i]); }
            return sb.Append(separator).Append("and ")
                     .Append((Items.Count - MaxJoined).ToString(CultureInfo.InvariantCulture)).Append(" more").ToString();
        }
    }
}
