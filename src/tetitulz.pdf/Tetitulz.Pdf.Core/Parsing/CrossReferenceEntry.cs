// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Parsing
{
    public sealed class CrossReferenceEntry
    {
        public enum EntryType
        {
            Free = 0,
            Normal = 1,
            Compressed = 2
        }

        public int ObjectNumber { get; set; }
        public EntryType Type { get; set; }

        /// <summary>Byte offset in file for Normal entries.</summary>
        public long Offset { get; set; }

        /// <summary>Generation number for Normal and Free entries.</summary>
        public int GenerationNumber { get; set; }

        /// <summary>Object stream number for Compressed entries.</summary>
        public int ObjectStreamNumber { get; set; }

        /// <summary>Index within the object stream for Compressed entries.</summary>
        public int IndexInObjectStream { get; set; }

        /// <summary>
        /// The stream position of the cross-reference section that supplied this entry, or -1 for
        /// one a scan made. The bytes before it are the entry's own revision and the ones it builds
        /// on; what a later revision appended lies after it.
        /// </summary>
        internal long SectionOffset { get; set; } = -1;

        public override string ToString()
        {
            switch (Type)
            {
                case EntryType.Normal:
                    return $"obj {ObjectNumber} gen {GenerationNumber} offset {Offset}";
                case EntryType.Compressed:
                    return $"obj {ObjectNumber} in stream {ObjectStreamNumber} index {IndexInObjectStream}";
                case EntryType.Free:
                    return $"obj {ObjectNumber} FREE";
                default:
                    return $"obj {ObjectNumber} unknown type";
            }
        }
    }
}
