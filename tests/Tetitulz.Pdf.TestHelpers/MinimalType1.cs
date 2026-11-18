// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Builds a genuine Type 1 font program for a PDF <c>/FontFile</c> stream: a clear-text header, then
/// an <c>eexec</c>-encrypted private section whose CharStrings are themselves encrypted — split where
/// <c>/Length1</c> and <c>/Length2</c> say, so a reader's split and <c>Type1Parser</c> are both under
/// test whenever it is used.
/// </summary>
/// <remarks>
/// Both layers use the same additive cipher (Type 1 spec, chapter 7) with different seeds — 55665 for
/// <c>eexec</c> and 4330 for a charstring — and each prepends <c>lenIV</c> bytes of padding that the
/// decryptor discards. Shared here because Core, Base and Accessibility tests all need a real program.
/// </remarks>
public static class MinimalType1
{
    /// <summary>A Type 1 charstring number (same encoding as Type 2 for -1131..1131).</summary>
    public static byte[] Num(int v)
    {
        if (v >= -107 && v <= 107) return new[] { (byte)(v + 139) };
        if (v >= 108 && v <= 1131) { int a = v - 108; return new[] { (byte)((a >> 8) + 247), (byte)a }; }
        if (v >= -1131 && v <= -108) { int a = -v - 108; return new[] { (byte)((a >> 8) + 251), (byte)a }; }
        return new[] { (byte)255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    /// <summary>A charstring that only sets its advance: <c>0 width hsbw endchar</c>.</summary>
    public static byte[] AdvanceOnly(int width)
    {
        var bytes = new List<byte> { 139 };
        bytes.AddRange(Num(width));
        bytes.Add(13);   // hsbw
        bytes.Add(14);   // endchar
        return bytes.ToArray();
    }

    /// <summary>
    /// The program. <paramref name="glyphs"/> are written to <c>/CharStrings</c> in order;
    /// <paramref name="encoding"/> fills the built-in <c>/Encoding</c> (everything else
    /// <c>.notdef</c>); a null <paramref name="fontMatrix"/> omits <c>/FontMatrix</c>;
    /// <paramref name="extraCharStringsText"/> is written verbatim inside the CharStrings dictionary
    /// after the glyphs, for a malformed entry. <paramref name="lenIV"/> is written to the Private
    /// dictionary; at -1 the charstrings are written unencrypted and unpadded, as the value means.
    /// <paramref name="subrsText"/> replaces the empty <c>/Subrs</c> line, for a hostile one.
    /// </summary>
    public static (byte[] Program, int Length1, int Length2) Build(
        IReadOnlyList<(string Name, byte[] Charstring)> glyphs,
        IReadOnlyDictionary<int, string>? encoding = null,
        string fontName = "ProbeFont",
        string fontMatrix = "[0.001 0 0 0.001 0 0]",
        string fontBBox = "{0 0 1000 1000}",
        string? extraCharStringsText = null,
        int lenIV = 4,
        string? subrsText = null)
    {
        var clear = new StringBuilder();
        clear.Append($"%!PS-AdobeFont-1.0: {fontName} 001.001\n");
        clear.Append($"/FontName /{fontName} def\n");
        clear.Append("/PaintType 0 def\n/FontType 1 def\n");
        if (fontMatrix != null) clear.Append($"/FontMatrix {fontMatrix} readonly def\n");
        clear.Append("/Encoding 256 array\n");
        clear.Append("0 1 255 {1 index exch /.notdef put} for\n");
        if (encoding != null)
            foreach (KeyValuePair<int, string> entry in encoding) clear.Append($"dup {entry.Key} /{entry.Value} put\n");
        clear.Append("readonly def\n");
        clear.Append($"/FontBBox {fontBBox} readonly def\n");
        clear.Append("currentdict end\ncurrentfile eexec\n");

        var priv = new MemoryStream();
        void P(string s) { byte[] b = Encoding.ASCII.GetBytes(s); priv.Write(b, 0, b.Length); }
        P("dup /Private 8 dict dup begin\n");
        P("/RD {string currentfile exch readstring pop} executeonly def\n");
        P("/ND {noaccess def} executeonly def\n");
        P("/NP {noaccess put} executeonly def\n");
        P($"/lenIV {lenIV} def\n");
        P(subrsText ?? "/Subrs 0 array ND\n");
        P($"2 index /CharStrings {glyphs.Count} dict dup begin\n");
        foreach ((string name, byte[] charstring) in glyphs)
        {
            byte[] encrypted = lenIV < 0 ? charstring : Encrypt(Pad(charstring), 4330);
            P($"/{name} {encrypted.Length} RD ");
            priv.Write(encrypted, 0, encrypted.Length);
            P(" ND\n");
        }
        if (extraCharStringsText != null) P(extraCharStringsText);
        P("end\nend\nreadonly put\nnoaccess put\n");
        P("dup /FontName get exch definefont pop\n");
        P("mark currentfile closefile\n");

        byte[] clearBytes = Encoding.ASCII.GetBytes(clear.ToString());
        byte[] encryptedPrivate = Encrypt(Pad(priv.ToArray()), 55665);

        var all = new MemoryStream();
        all.Write(clearBytes, 0, clearBytes.Length);
        all.Write(encryptedPrivate, 0, encryptedPrivate.Length);
        return (all.ToArray(), clearBytes.Length, encryptedPrivate.Length);
    }

    /// <summary>
    /// <paramref name="charstring"/> as a font holds it under the default <c>lenIV</c> of 4: four
    /// pad bytes prepended and the whole encrypted with key 4330 — what a Type 0 CIDFont's binary data
    /// and <c>/GlyphDirectory</c> carry.
    /// </summary>
    public static byte[] EncryptCharstring(byte[] charstring) => Encrypt(Pad(charstring), 4330);

    /// <summary>Prepends the four <c>lenIV</c> bytes the decryptor discards.</summary>
    private static byte[] Pad(byte[] plain)
    {
        var padded = new byte[plain.Length + 4];
        // Any bytes will do; the first must not make the result look like clear-text hex to a
        // reader sniffing the section, so a non-hex byte is conventional.
        padded[0] = 0x5A; padded[1] = 0x5A; padded[2] = 0x5A; padded[3] = 0x5A;
        Buffer.BlockCopy(plain, 0, padded, 4, plain.Length);
        return padded;
    }

    /// <summary>The Type 1 additive cipher (spec chapter 7), used for both eexec and charstrings.</summary>
    private static byte[] Encrypt(byte[] plain, ushort seed)
    {
        var outBytes = new byte[plain.Length];
        ushort r = seed;
        for (int i = 0; i < plain.Length; i++)
        {
            byte c = (byte)(plain[i] ^ (r >> 8));
            r = (ushort)(((c + r) * 52845 + 22719) & 0xFFFF);
            outBytes[i] = c;
        }
        return outBytes;
    }
}
