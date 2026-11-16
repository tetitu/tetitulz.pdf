// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO.Compression;
using System.Text;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

/// <summary>
/// A written PDF's bytes as Latin-1 text with every <c>/FlateDecode</c> stream inflated in place —
/// for a test that looks for bytes the conversion carried through (a font program's clear text), now
/// that the document writer deflates what it writes. The inflating is .NET's own zlib, not the
/// library's, so the check does not lean on the code it checks.
/// </summary>
internal static class PdfStreamText
{
    public static string Decoded(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        var result = new StringBuilder(text.Length * 2);
        int at = 0;
        while (true)
        {
            int keyword = text.IndexOf("stream", at, StringComparison.Ordinal);
            while (keyword >= 0 && (keyword >= 3 && text.Substring(keyword - 3, 3) == "end"))
                keyword = text.IndexOf("stream", keyword + 6, StringComparison.Ordinal);
            if (keyword < 0) break;
            int start = keyword + 6;
            if (start < text.Length && text[start] == '\r') start++;
            if (start < text.Length && text[start] == '\n') start++;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            int dictionary = text.LastIndexOf("<<", keyword, StringComparison.Ordinal);
            bool deflated = dictionary >= at && text.Substring(dictionary, keyword - dictionary).Contains("/FlateDecode", StringComparison.Ordinal);
            result.Append(text, at, start - at);
            result.Append(deflated ? Inflate(pdf, start, end - start) : text.Substring(start, end - start));
            at = end;
        }
        result.Append(text, at, text.Length - at);
        return result.ToString();
    }

    private static string Inflate(byte[] pdf, int start, int length)
    {
        try
        {
            using var input = new ZLibStream(new MemoryStream(pdf, start, length), CompressionMode.Decompress);
            using var output = new MemoryStream();
            input.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return Encoding.Latin1.GetString(pdf, start, length);
        }
    }
}
