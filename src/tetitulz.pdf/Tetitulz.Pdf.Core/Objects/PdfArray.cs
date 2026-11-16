// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfArray : PdfObject, IList<PdfObject>
    {
        private readonly List<PdfObject> _items = new List<PdfObject>();

        public PdfArray() { }

        public PdfArray(IEnumerable<PdfObject> items)
        {
            _items.AddRange(items);
        }

        /// <summary>
        /// An array of <paramref name="values"/> as numbers — a function's <c>/Domain</c> and
        /// <c>/Range</c>, a colour, a matrix. The content-stream builder, the named-spot-colour
        /// writer and the halftone spot functions each had a private copy.
        /// </summary>
        internal static PdfArray OfNumbers(params double[] values)
        {
            var array = new PdfArray();
            foreach (double v in values) array.Add(new PdfNumber(v));
            return array;
        }

        public PdfObject this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public void Add(PdfObject item) => _items.Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(PdfObject item) => _items.Contains(item);
        public void CopyTo(PdfObject[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public int IndexOf(PdfObject item) => _items.IndexOf(item);
        public void Insert(int index, PdfObject item) => _items.Insert(index, item);
        public bool Remove(PdfObject item) => _items.Remove(item);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        public IEnumerator<PdfObject> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int GetInteger(int index)
        {
            if (index < 0 || index >= _items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (_items[index] is PdfNumber num) return num.IntValue;
            throw new PdfException($"Element at index {index} is not a number.");
        }

        public long GetLong(int index)
        {
            if (index < 0 || index >= _items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (_items[index] is PdfNumber num) return num.LongValue;
            throw new PdfException($"Element at index {index} is not a number.");
        }

        public double GetDouble(int index)
        {
            if (index < 0 || index >= _items.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (_items[index] is PdfNumber num) return num.DoubleValue;
            throw new PdfException($"Element at index {index} is not a number.");
        }

        public PdfName GetName(int index)
        {
            if (index < 0 || index >= _items.Count) return null;
            return _items[index] as PdfName;
        }

        public PdfDictionary GetDictionary(int index)
        {
            if (index < 0 || index >= _items.Count) return null;
            return _items[index] as PdfDictionary;
        }

        public PdfReference GetReference(int index)
        {
            if (index < 0 || index >= _items.Count) return null;
            return _items[index] as PdfReference;
        }

        public PdfString GetString(int index)
        {
            if (index < 0 || index >= _items.Count) return null;
            return _items[index] as PdfString;
        }

        public override string ToString() => $"[Array: {Count} items]";
    }
}
