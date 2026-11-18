// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Document
{
    public struct PdfVersion : IComparable<PdfVersion>, IEquatable<PdfVersion>
    {
        /// <summary>PDF 1.0.</summary>
        public static readonly PdfVersion Pdf10 = new PdfVersion(1, 0);
        /// <summary>PDF 1.1.</summary>
        public static readonly PdfVersion Pdf11 = new PdfVersion(1, 1);
        /// <summary>PDF 1.2.</summary>
        public static readonly PdfVersion Pdf12 = new PdfVersion(1, 2);
        /// <summary>PDF 1.3.</summary>
        public static readonly PdfVersion Pdf13 = new PdfVersion(1, 3);
        /// <summary>PDF 1.4 — the highest version PDF/A-1 admits.</summary>
        public static readonly PdfVersion Pdf14 = new PdfVersion(1, 4);
        /// <summary>PDF 1.5.</summary>
        public static readonly PdfVersion Pdf15 = new PdfVersion(1, 5);
        /// <summary>PDF 1.6.</summary>
        public static readonly PdfVersion Pdf16 = new PdfVersion(1, 6);
        /// <summary>PDF 1.7 (ISO 32000-1) — the highest version PDF/A-2 and -3 admit.</summary>
        public static readonly PdfVersion Pdf17 = new PdfVersion(1, 7);
        /// <summary>PDF 2.0 (ISO 32000-2).</summary>
        public static readonly PdfVersion Pdf20 = new PdfVersion(2, 0);

        public int Major { get; }
        public int Minor { get; }

        public PdfVersion(int major, int minor)
        {
            Major = major;
            Minor = minor;
        }

        /// <summary>
        /// Whether this is a published version of the format — 1.0 to 1.7, or 2.0 — and so one a
        /// writer may put in a header. A reader still parses any <c>major.minor</c> a file carries.
        /// </summary>
        public bool IsPublished => (Major == 1 && Minor >= 0 && Minor <= 7) || (Major == 2 && Minor == 0);

        /// <summary>The later of two versions.</summary>
        public static PdfVersion Max(PdfVersion a, PdfVersion b) => a >= b ? a : b;

        public int CompareTo(PdfVersion other)
        {
            int c = Major.CompareTo(other.Major);
            return c != 0 ? c : Minor.CompareTo(other.Minor);
        }

        public bool Equals(PdfVersion other) => Major == other.Major && Minor == other.Minor;
        public override bool Equals(object obj) => obj is PdfVersion v && Equals(v);
        public override int GetHashCode() => (Major << 16) | Minor;
        public override string ToString() => $"{Major}.{Minor}";

        /// <summary>
        /// Parses a "major.minor" version string with no leading slash — the shape of the
        /// catalog's optional <c>/Version</c> override (§7.5.2), e.g. the name <c>2.0</c>.
        /// Returns <see langword="false"/> for a malformed string rather than throwing: a
        /// producer's malformed override must not abort parsing the rest of the document.
        /// </summary>
        public static bool TryParse(string text, out PdfVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(text)) return false;

            int dot = text.IndexOf('.');
            if (dot <= 0 || dot == text.Length - 1) return false;

            if (!int.TryParse(text.Substring(0, dot), out int major)) return false;
            if (!int.TryParse(text.Substring(dot + 1), out int minor)) return false;
            if (major < 0 || minor < 0) return false;

            version = new PdfVersion(major, minor);
            return true;
        }

        public static bool operator ==(PdfVersion left, PdfVersion right) => left.Equals(right);
        public static bool operator !=(PdfVersion left, PdfVersion right) => !left.Equals(right);
        public static bool operator <(PdfVersion left, PdfVersion right) => left.CompareTo(right) < 0;
        public static bool operator >(PdfVersion left, PdfVersion right) => left.CompareTo(right) > 0;
        public static bool operator <=(PdfVersion left, PdfVersion right) => left.CompareTo(right) <= 0;
        public static bool operator >=(PdfVersion left, PdfVersion right) => left.CompareTo(right) >= 0;
    }
}
