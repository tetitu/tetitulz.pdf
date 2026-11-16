// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Font directories a host or a package adds to the search path — searched before the
    /// operating system's directories, and before <c>TETITULZ_PDF_FONTDIR</c>'s, so a bundled font
    /// is the one found when the host also has one of the same family.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> No fonts ship in the core packages, and the standard 14 are
    /// mapped to the host's Arial, Times New Roman and Courier New, then to Liberation
    /// (<c>FontFileLocator</c>): on a host with no fonts installed — a
    /// Linux container, a locked-down service — nothing renders predictably. <c>Tetitulz.Pdf.Plugins.Fonts</c>
    /// carries Liberation and Noto Sans Hebrew and Arabic under the SIL Open Font License and
    /// registers the directory it extracts them to through <see cref="Add"/>; a host with its own
    /// fonts on disk registers that directory the same way. <c>TETITULZ_PDF_FONTDIR</c> keeps its
    /// meaning — it replaces the operating system's list — and a registered directory still comes
    /// first, because both are the caller's choice.</para>
    /// <para>Process-global, as the search path is; thread-safe. Adding a directory clears the
    /// locator's caches, so fonts found earlier are looked up again.</para>
    /// </remarks>
    public static class FontSearchPath
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Registered = new List<string>();

        /// <summary>
        /// Adds <paramref name="directory"/> to the front of the search path. A directory already
        /// registered moves to the front; one that does not exist is still registered — it is
        /// dropped at search time, as the operating system's absent directories are — so a host may
        /// register before it extracts.
        /// </summary>
        public static void Add(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A font directory is required.", nameof(directory));
            string full = Path.GetFullPath(directory);
            lock (Gate)
            {
                Registered.RemoveAll(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase));
                Registered.Insert(0, full);
            }
            FontFileLocator.ResetCache();
        }

        /// <summary>Removes a directory added by <see cref="Add"/>; false when it was not registered.</summary>
        public static bool Remove(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            string full = Path.GetFullPath(directory);
            bool removed;
            lock (Gate) removed = Registered.RemoveAll(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) FontFileLocator.ResetCache();
            return removed;
        }

        /// <summary>The registered directories, first searched first.</summary>
        public static IReadOnlyList<string> Directories
        {
            get { lock (Gate) return Registered.ToArray(); }
        }
    }
}
