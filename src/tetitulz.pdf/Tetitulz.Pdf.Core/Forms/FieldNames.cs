// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// Joins a field's partial names into its fully qualified name (ISO 32000-2 §12.7.4.2): "a
    /// period-separated concatenation of the partial names of the field and all its ancestors".
    /// </summary>
    /// <remarks>
    /// <para><b>An empty partial name adds no segment.</b> <see cref="PdfFormField.FullyQualifiedName"/>
    /// skips one, and <see cref="AcroFieldClassification"/> reads a node whose
    /// <c>/T</c> is empty as a widget, which contributes no name. So the name every form reader
    /// lists for a field is the one without it.</para>
    /// <para><b>One join</b>, for the field model, <c>PdfActionRunner</c>'s field search, the FDF
    /// reader, the XFDF reader and <c>PdfActionDispatcher</c>'s field walk. A join that keeps an
    /// empty segment would list a field the form reader calls <c>a.b</c> as <c>a..b</c> or
    /// <c>a.</c>.</para>
    /// <para>The signing assembly's two walks joined by hand the longest, because their test for
    /// "this node is a named field" was <c>/T</c> present rather than non-empty, and changing the
    /// join alone would have let the signer adopt a widget. They join here now, and take a name only
    /// from a non-empty <c>/T</c>, which is <see cref="AcroFieldClassification"/>'s reading.</para>
    /// </remarks>
    internal static class FieldNames
    {
        /// <summary>
        /// <paramref name="parent"/>'s fully qualified name followed by <paramref name="partial"/>,
        /// skipping whichever is empty. <c>null</c> only when both are null or empty and
        /// <paramref name="parent"/> is null. A name past <see cref="PdfLimits.MaxQualifiedFieldNameLength"/>
        /// is answered as its stand-in (<see cref="StandIn"/>), never built.
        /// </summary>
        internal static string Qualify(string parent, string partial)
        {
            if (string.IsNullOrEmpty(partial)) return parent;
            bool hasParent = !string.IsNullOrEmpty(parent);
            long length = hasParent ? (long)parent.Length + 1 + partial.Length : partial.Length;
            if (length <= PdfLimits.MaxQualifiedFieldNameLength)
                return hasParent ? parent + "." + partial : partial;
            return StandIn(hasParent ? parent : null, partial);
        }

        /// <summary>The characters of a long name a stand-in keeps before its digest.</summary>
        internal const int StandInHeadLength = 64;

        /// <summary>
        /// A name too long to answer, as its first <see cref="StandInHeadLength"/> characters (a
        /// surrogate pair never cut), a <c>#</c> and 64 hexadecimal digits: the SHA-256 of the parent's
        /// name (itself at most the ceiling, since it was answered by this join), a zero, and the
        /// SHA-256 of the partial name — so names that differ anywhere differ here, and a partial name
        /// many fields share is hashed once (<see cref="PartialDigest"/>).
        /// </summary>
        private static string StandIn(string parent, string partial)
        {
            var head = new StringBuilder(StandInHeadLength + 65);
            if (parent != null)
            {
                head.Append(parent, 0, Math.Min(parent.Length, StandInHeadLength));
                if (head.Length < StandInHeadLength) head.Append('.');
            }
            if (head.Length < StandInHeadLength)
                head.Append(partial, 0, Math.Min(partial.Length, StandInHeadLength - head.Length));
            if (head.Length > 0 && char.IsHighSurrogate(head[head.Length - 1])) head.Length--;

            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] parentBytes = parent == null ? Array.Empty<byte>() : Encoding.Unicode.GetBytes(parent);
                byte[] partialDigest = PartialDigest(partial);
                var input = new byte[parentBytes.Length + 1 + partialDigest.Length];
                Buffer.BlockCopy(parentBytes, 0, input, 0, parentBytes.Length);
                Buffer.BlockCopy(partialDigest, 0, input, parentBytes.Length + 1, partialDigest.Length);
                digest = sha.ComputeHash(input);
            }

            head.Append('#');
            foreach (byte b in digest) head.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return head.ToString();
        }

        /// <summary>
        /// The SHA-256 of a partial name's UTF-16 code units, kept for the string's life: the fields
        /// under one parent all read its one decoded name (<see cref="Objects.PdfString.GetText"/>
        /// answers the same string each time), so a name of a megabyte is hashed once, not once a field.
        /// </summary>
        private static byte[] PartialDigest(string partial)
            => PartialDigests.GetValue(partial, p =>
            {
                PartialDigestsComputed++;
                using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.Unicode.GetBytes(p));
            });

        /// <summary>How many partial names this thread has hashed: the memo's evidence, since a stand-in is the same either way.</summary>
        [ThreadStatic] internal static long PartialDigestsComputed;

        private static readonly ConditionalWeakTable<string, byte[]> PartialDigests = new ConditionalWeakTable<string, byte[]>();

        /// <summary>
        /// The last segment of a period-separated qualified name — a field's fully qualified name,
        /// or an XFA SOM path: the text after the last period, the whole name when there is none.
        /// The XFA field-path resolver and the XFA importer alike.
        /// </summary>
        internal static string Leaf(string qualified)
        {
            int dot = qualified.LastIndexOf('.');
            return dot < 0 ? qualified : qualified.Substring(dot + 1);
        }
    }
}
