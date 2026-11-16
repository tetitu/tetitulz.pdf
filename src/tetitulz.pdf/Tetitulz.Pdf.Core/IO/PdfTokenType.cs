// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.IO
{
    public enum PdfTokenType
    {
        Number,
        LiteralString,
        HexString,
        Name,
        Boolean,
        Null,
        Keyword,
        Comment,
        ArrayStart,
        ArrayEnd,
        DictionaryStart,
        DictionaryEnd,
        StreamData,
        Eof
    }
}
