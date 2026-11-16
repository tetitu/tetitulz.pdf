// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Document
{
    public struct PdfVersion : IComparable<PdfVersion>, IEquatable<PdfVersion>
    {
        public int Major { get; }
        public int Minor { get; }

        public PdfVersion(int major, int minor)
        {
            Major = major;
            Minor = minor;
        }

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
