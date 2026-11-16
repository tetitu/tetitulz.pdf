// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
    {
        public int ReferenceObjectNumber { get; }
        public int ReferenceGenerationNumber { get; }

        public PdfReference(int objectNumber, int generationNumber)
        {
            ReferenceObjectNumber = objectNumber;
            ReferenceGenerationNumber = generationNumber;
        }

        public bool Equals(PdfReference other)
        {
            if (other is null) return false;
            return ReferenceObjectNumber == other.ReferenceObjectNumber
                && ReferenceGenerationNumber == other.ReferenceGenerationNumber;
        }

        public override bool Equals(object obj) => Equals(obj as PdfReference);
        public override int GetHashCode() => (ReferenceObjectNumber * 397) ^ ReferenceGenerationNumber;
        public override string ToString() => $"{ReferenceObjectNumber} {ReferenceGenerationNumber} R";
    }
}
