// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A real, self-contained JPEG 2000 (JP2 container) test image: the PDF Association's 100x67 RGB
/// "cat" used in <c>UnknownFilter-objstm.pdf</c>, embedded as base64 so no test reads a file.
///
/// <para>Its JP2 header carries a <c>colr</c> box with METH 1 / EnumCS 16 (sRGB). One copy for
/// every test project that needs it.</para>
/// </summary>
public static class CatJp2
{
    // 1623-byte JP2 codestream (jp2 brand, 3-component 8-bit RGB, 100x67).
    private const string Base64 =
        "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";

    public const int Width = 100;
    public const int Height = 67;
    public const int Components = 3;

    public static byte[] Bytes => Convert.FromBase64String(Base64);
}
