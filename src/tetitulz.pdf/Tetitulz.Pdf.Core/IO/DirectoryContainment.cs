// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// Whether a path lies inside a directory — the question every reader of a local file a document
    /// names has to ask before it opens the file: an HTML page's <c>src</c> and <c>href</c>, in the
    /// managed engine and in the browser engine's request guard alike.
    /// </summary>
    /// <remarks>
    /// <para><b>The trailing separator is the point.</b> <c>candidate.StartsWith(directory)</c> is
    /// the classic containment bug: <c>C:\base-evil\x</c> starts with <c>C:\base</c> and is not
    /// inside it. Both paths are made absolute and normalised first, so <c>base\..\other</c> is
    /// judged where it leads.</para>
    /// <para><b>Case follows the platform's file system</b>: ignored on Windows, significant
    /// elsewhere. Ignoring it everywhere would admit, on Linux, a sibling directory whose name
    /// differs only in case — a different directory there.</para>
    /// <para><b>It is a judgement about names.</b> A link inside the directory that leads out of it
    /// is followed by whoever opens the file; a directory that untrusted parties can write links
    /// into is not one to name as a base.</para>
    /// </remarks>
    public static class DirectoryContainment
    {
        /// <summary>
        /// True when <paramref name="candidate"/> is a path strictly inside
        /// <paramref name="directory"/> — in it or in its subtree, not the directory itself. Either
        /// path empty, or one that cannot be made absolute, is not inside anything.
        /// </summary>
        public static bool IsInside(string directory, string candidate)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(candidate)) return false;

            string root, full;
            try
            {
                root = Path.GetFullPath(directory);
                full = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException
                                       || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return false;
            }
            if (root.Length == 0) return false;

            char last = root[root.Length - 1];
            string prefix = last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
                ? root
                : root + Path.DirectorySeparatorChar;

            StringComparison comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return full.Length > prefix.Length && full.StartsWith(prefix, comparison);
        }
    }
}
