// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The image XObjects one document has written, so a page that registers the same image draws the object an
    /// earlier page wrote instead of writing it again: one share per document build, handed to every page's
    /// <see cref="PdfContentStreamBuilder.BuildObjects(ref int, bool, IReadOnlyDictionary{string, int}, ImageObjectShare)"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The same image is the same arrays.</b> The key is the caller's data and raw soft-mask arrays by
    /// identity, and the image dictionary's entries by value — never a hash of the content: a producer that decoded
    /// once and hands the same bytes to each drawing gets one object, and a content-equal copy from another decode
    /// stays its own. A copy per page would write a DOCX naming one picture on two hundred pages (a logo in a header,
    /// or a hostile file's three kilobytes) two hundred times.</para>
    /// <para>The size rule is still asked of every registration (<see cref="AuthoredImageSize"/>): sharing is a
    /// writing decision, made after each <c>AddImage</c> has been judged.</para>
    /// </remarks>
    internal sealed class ImageObjectShare
    {
        private readonly Dictionary<Key, int> _written = new Dictionary<Key, int>();

        /// <summary>The registrations drawn through an object an earlier registration wrote — the share's evidence.</summary>
        internal int Reused { get; private set; }

        /// <summary>The object an identical image was written as, if one was.</summary>
        internal bool TryGet(Key key, out int objectNumber)
        {
            if (!_written.TryGetValue(key, out objectNumber)) return false;
            Reused++;
            return true;
        }

        /// <summary>Records the object <paramref name="key"/>'s image was written as.</summary>
        internal void Add(Key key, int objectNumber) => _written[key] = objectNumber;

        /// <summary>What makes two registrations one image: the arrays by identity, the dictionary by value.</summary>
        internal readonly struct Key : IEquatable<Key>
        {
            private readonly byte[] _data;
            private readonly byte[] _mask;
            private readonly bool _encodeData;
            private readonly int _width, _height, _bitsPerComponent, _smaskInData;
            private readonly string _filter, _colorSpace;
            private readonly double[] _decode;

            internal Key(byte[] data, byte[] mask, bool encodeData, int width, int height, string filter,
                string colorSpace, int bitsPerComponent, int smaskInData, double[] decode)
            {
                _data = data; _mask = mask; _encodeData = encodeData;
                _width = width; _height = height; _bitsPerComponent = bitsPerComponent; _smaskInData = smaskInData;
                _filter = filter; _colorSpace = colorSpace; _decode = decode;
            }

            public bool Equals(Key other)
                => ReferenceEquals(_data, other._data) && ReferenceEquals(_mask, other._mask)
                   && _encodeData == other._encodeData && _width == other._width && _height == other._height
                   && _bitsPerComponent == other._bitsPerComponent && _smaskInData == other._smaskInData
                   && string.Equals(_filter, other._filter, StringComparison.Ordinal)
                   && string.Equals(_colorSpace, other._colorSpace, StringComparison.Ordinal)
                   && SameDecode(_decode, other._decode);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = RuntimeHelpers.GetHashCode(_data);
                    h = h * 31 + (_mask == null ? 0 : RuntimeHelpers.GetHashCode(_mask));
                    h = h * 31 + _width;
                    h = h * 31 + _height;
                    return h;
                }
            }

            private static bool SameDecode(double[] a, double[] b)
            {
                if (ReferenceEquals(a, b)) return true;
                int na = a?.Length ?? 0, nb = b?.Length ?? 0;
                if (na != nb) return false;
                for (int i = 0; i < na; i++) if (!a[i].Equals(b[i])) return false;
                return true;
            }
        }
    }
}
