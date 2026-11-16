// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;

// Diagnostic harness for V1-V4 encryption analysis.  Pass the PDF path as
// args[0]; args[1] (optional) is the password to test.  We deliberately
// avoid hard-coded paths so the tool runs anywhere.
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DiagDecrypt <input.pdf> [password]");
    return 1;
}
var inputPath = args[0];
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"File not found: {inputPath}");
    return 2;
}
Console.WriteLine($"File size: {new FileInfo(inputPath).Length} bytes");

var ms = new MemoryStream(File.ReadAllBytes(inputPath));
var doc = PdfDocument.Open(ms);

Console.WriteLine($"IsEncrypted: {doc.IsEncrypted}");
var enc = doc.EncryptionDictionary;

int v = enc.GetInteger(PdfName.V) ?? 0;
int r = enc.GetInteger(PdfName.R) ?? 0;
int p = enc.GetInteger(PdfName.P) ?? 0;
int keyLenBits = enc.GetInteger(PdfName.Length) ?? 40;
int keyLenBytes = keyLenBits / 8;

Console.WriteLine($"V={v} R={r} P={p} KeyLen={keyLenBits}bits ({keyLenBytes}bytes)");

var oStr = enc[PdfName.O] as PdfString;
var uStr = enc[PdfName.U] as PdfString;
byte[] oValue = oStr.RawBytes;
byte[] uValue = uStr.RawBytes;
Console.WriteLine($"O ({oValue.Length}): {BitConverter.ToString(oValue)}");
Console.WriteLine($"U ({uValue.Length}): {BitConverter.ToString(uValue)}");

// Get file ID
var idArray = doc.Trailer.GetArray(PdfName.ID);
byte[] fileId;
if (idArray != null && idArray.Count >= 1 && idArray[0] is PdfString firstId)
    fileId = firstId.RawBytes;
else
    fileId = new byte[16];
Console.WriteLine($"FileID ({fileId.Length}): {BitConverter.ToString(fileId)}");

// Standard padding bytes
byte[] PaddingBytes = {
    0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
    0x64, 0x00, 0x4B, 0x49, 0x43, 0x4B, 0x00, 0x42,
    0x4D, 0x41, 0x4A, 0x45, 0x44, 0x41, 0x00, 0x52,
    0x49, 0x43, 0x48, 0x41, 0x52, 0x44, 0x00, 0x41
};

string password = args.Length >= 2 ? args[1] : "";
Console.WriteLine($"\nPassword: '{password}'");

// Step 1: Pad password
byte[] pwdBytes = Encoding.GetEncoding("iso-8859-1").GetBytes(password);
byte[] padded = new byte[32];
int copyLen = Math.Min(pwdBytes.Length, 32);
Buffer.BlockCopy(pwdBytes, 0, padded, 0, copyLen);
if (copyLen < 32)
    Buffer.BlockCopy(PaddingBytes, 0, padded, copyLen, 32 - copyLen);
Console.WriteLine($"Padded password: {BitConverter.ToString(padded)}");

// Step 2: Compute encryption key (Algorithm 2)
using (var md5 = MD5.Create())
{
    md5.TransformBlock(padded, 0, padded.Length, null, 0);
    md5.TransformBlock(oValue, 0, oValue.Length, null, 0);
    byte[] pBytes = BitConverter.GetBytes(p);
    if (!BitConverter.IsLittleEndian) Array.Reverse(pBytes);
    Console.WriteLine($"P bytes (LE): {BitConverter.ToString(pBytes)}");
    md5.TransformBlock(pBytes, 0, 4, null, 0);
    md5.TransformFinalBlock(fileId, 0, fileId.Length);
    byte[] hash = md5.Hash;
    Console.WriteLine($"Initial MD5: {BitConverter.ToString(hash)}");

    if (r >= 3)
    {
        for (int i = 0; i < 50; i++)
            hash = md5.ComputeHash(hash, 0, keyLenBytes);
        Console.WriteLine($"After 50 rounds: {BitConverter.ToString(hash)}");
    }

    byte[] encKey = new byte[keyLenBytes];
    Buffer.BlockCopy(hash, 0, encKey, 0, keyLenBytes);
    Console.WriteLine($"Encryption key: {BitConverter.ToString(encKey)}");

    // Step 3: Compute U value (Algorithm 5 for R>=3)
    byte[] uHash;
    using (var md5u = MD5.Create())
    {
        md5u.TransformBlock(PaddingBytes, 0, PaddingBytes.Length, null, 0);
        md5u.TransformFinalBlock(fileId, 0, fileId.Length);
        uHash = md5u.Hash;
    }
    Console.WriteLine($"U hash (before RC4): {BitConverter.ToString(uHash)}");

    // RC4 encrypt 20 rounds
    byte[] encrypted = Rc4(encKey, uHash);
    for (int i = 1; i <= 19; i++)
    {
        byte[] tmpKey = new byte[encKey.Length];
        for (int j = 0; j < encKey.Length; j++)
            tmpKey[j] = (byte)(encKey[j] ^ (byte)i);
        encrypted = Rc4(tmpKey, encrypted);
    }
    byte[] computedU = new byte[32];
    Buffer.BlockCopy(encrypted, 0, computedU, 0, 16);
    Console.WriteLine($"Computed U: {BitConverter.ToString(computedU)}");
    Console.WriteLine($"Stored U:   {BitConverter.ToString(uValue)}");
    Console.WriteLine($"First 16 match: {BytesMatch(computedU, uValue, 16)}");
}

// Now try decrypt
ms.Position = 0;
try
{
    var outMs = new MemoryStream();
    Tetitulz.Pdf.Base.Encryption.PdfDecryptor.Decrypt(ms, outMs, password);
    Console.WriteLine($"\nSUCCESS! Output: {outMs.Length} bytes");
    string outPath = Path.ChangeExtension(inputPath, ".decrypted.pdf");
    File.WriteAllBytes(outPath, outMs.ToArray());
    Console.WriteLine($"Wrote {outPath}");
}
catch (Exception ex)
{
    Console.WriteLine($"\nDECRYPT ERROR: {ex.Message}");
}

doc.Dispose();
return 0;

static byte[] Rc4(byte[] key, byte[] data)
{
    // Simple RC4
    byte[] s = new byte[256];
    for (int i = 0; i < 256; i++) s[i] = (byte)i;
    int j2 = 0;
    for (int i = 0; i < 256; i++)
    {
        j2 = (j2 + s[i] + key[i % key.Length]) & 255;
        (s[i], s[j2]) = (s[j2], s[i]);
    }
    byte[] output = new byte[data.Length];
    int x = 0, y = 0;
    for (int k = 0; k < data.Length; k++)
    {
        x = (x + 1) & 255;
        y = (y + s[x]) & 255;
        (s[x], s[y]) = (s[y], s[x]);
        output[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 255]);
    }
    return output;
}

static bool BytesMatch(byte[] a, byte[] b, int len)
{
    for (int i = 0; i < len; i++)
        if (a[i] != b[i]) return false;
    return true;
}
