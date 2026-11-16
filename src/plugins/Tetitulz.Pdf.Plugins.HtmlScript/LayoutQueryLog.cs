// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// The layout properties a page's scripts read — <c>offsetWidth</c>, <c>clientHeight</c>,
    /// <c>scrollTop</c> and the rest — each of which answers 0, because nothing is laid out while the
    /// scripts run. Registered on the page's browsing context and told by the vendored stubs
    /// (<c>ElementPseudoExtensions</c>), so the page says it once rather than a script silently sizing
    /// or placing something from 0.
    /// </summary>
    /// <remarks>
    /// One per page, and locked: a timer or an event handler runs on AngleSharp's event loop, not on
    /// the thread that opened the page.
    /// </remarks>
    internal sealed class LayoutQueryLog
    {
        private readonly object _gate = new object();
        private readonly List<string> _names = new List<string>();

        /// <summary>Records that a script read <paramref name="name"/>; a second read of it adds nothing.</summary>
        internal void Read(string name)
        {
            lock (_gate)
            {
                if (!_names.Contains(name)) _names.Add(name);
            }
        }

        /// <summary>The names read, in the order first read.</summary>
        internal IReadOnlyList<string> Names
        {
            get { lock (_gate) return _names.ToArray(); }
        }

        /// <summary>The page's report, or null when its scripts read no layout.</summary>
        internal string? Describe()
        {
            var names = Names;
            return names.Count == 0
                ? null
                : "The page's scripts read layout (" + string.Join(", ", names) + "), which answers 0 " +
                  "here — nothing is laid out while they run — so whatever they sized or placed from " +
                  "it was sized or placed from 0. The browser engine runs them against a real layout.";
        }
    }
}
