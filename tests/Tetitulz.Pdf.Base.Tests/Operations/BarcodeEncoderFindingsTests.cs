// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Barcode-encoder findings from the assembly scan: the QR generator cache's shared mutable
/// state, and DataMatrix faulting on a payload it cannot encode.
/// </summary>
public class BarcodeEncoderFindingsTests
{
    // ==================================================================
    //  QrCodeEncoder — no shared mutable state behind a public static API
    // ==================================================================

    [Fact]
    public void QrCodeEncoder_HasNoMutableStaticCollection()
    {
        // The generator polynomials used to live in a lazily filled Dictionary<int,int[]>,
        // mutated from BuildGenerator, which is reached from the *public static*
        // PdfBarcodeWriter.GeneratePdf with nothing serialising the write. Concurrent inserts
        // during a resize corrupt the bucket chain: either a torn read (a wrong generator, so a
        // silently unscannable symbol) or an endless loop inside TryGetValue.
        //
        // Asserting on the shape rather than on a race is deliberate: a race reproduces only
        // sometimes, and "the test passed" would mean nothing. This is the guard's own evidence.
        Type encoder = typeof(PdfBarcodeWriter).Assembly
            .GetType("Tetitulz.Pdf.Base.Operations.Barcodes.QrCodeEncoder", throwOnError: true);

        var mutableCollections = encoder
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => IsMutableCollection(f.FieldType))
            .Select(f => $"{f.FieldType.Name} {f.Name}")
            .ToList();

        Assert.True(mutableCollections.Count == 0,
            "QrCodeEncoder must hold no mutable static collection; found: " +
            string.Join(", ", mutableCollections));
    }

    private static bool IsMutableCollection(Type t) =>
        t.IsGenericType &&
        (t.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
         t.GetGenericTypeDefinition() == typeof(List<>) ||
         t.GetGenericTypeDefinition() == typeof(HashSet<>));

    [Fact]
    public void QrCodeEncoder_ConcurrentEncodesMatchTheSerialResult()
    {
        // Payload lengths chosen to select different QR versions, hence different
        // error-correction codeword counts, hence different generator polynomials — the case
        // that made two threads insert different keys concurrently.
        string[] payloads =
        {
            "v1",
            new string('a', 25),
            new string('b', 50),
            new string('c', 100),
            new string('d', 180),
        };

        var expected = payloads
            .Select(p => PdfBarcodeWriter.GenerateContentStream(p, BarcodeType.QrCode, 0f, 0f))
            .ToArray();

        // Each payload is encoded from many threads at once; every result must be the value the
        // serial run produced, byte for byte.
        var results = new string[payloads.Length * 8];
        Parallel.For(0, results.Length, i =>
            results[i] = PdfBarcodeWriter.GenerateContentStream(
                payloads[i % payloads.Length], BarcodeType.QrCode, 0f, 0f));

        for (int i = 0; i < results.Length; i++)
            Assert.Equal(expected[i % payloads.Length], results[i]);
    }

    // ==================================================================
    //  DataMatrixEncoder — a payload it cannot encode is refused, not faulted on
    // ==================================================================

    [Theory]
    [InlineData("\u05E9\u05DC\u05D5\u05DD")]   // Hebrew
    [InlineData("\u4F60\u597D")]               // CJK
    [InlineData("\u0416")]                     // Cyrillic
    [InlineData("A\uD83D\uDE00B")]             // emoji (surrogate pair)
    public void DataMatrix_PayloadOutsideLatin1_ThrowsArgumentExceptionNamingTheCharacter(string payload)
    {
        // EncodeAscii's upper-shift branch computed `c - 128 + 1` with no range check, so U+05D0
        // produced codeword 1361, which then indexed the 256-entry GfLog table: an
        // IndexOutOfRangeException out of Reed-Solomon rather than the ArgumentException this
        // encoder uses for its other refusals — and reached by exactly the payloads a caller
        // passes through from user input.
        var ex = Assert.Throws<ArgumentException>(() =>
            PdfBarcodeWriter.GenerateContentStream(payload, BarcodeType.DataMatrix, 0f, 0f));

        // The message must identify the offending character, as Code 128's refusal does.
        char offending = payload.First(c => c > 0xFF);
        Assert.Contains($"U+{(int)offending:X4}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataMatrix_Latin1PayloadStillEncodes()
    {
        // The refusal must be exactly at U+00FF: the upper-shift path for 0x80-0xFF is legal
        // ASCII encodation and must keep working.
        string cs = PdfBarcodeWriter.GenerateContentStream(
            "Caf\u00E9 \u00FF", BarcodeType.DataMatrix, 0f, 0f);

        Assert.Contains("% DataMatrix (", cs, StringComparison.Ordinal);
        Assert.Contains(" re", cs, StringComparison.Ordinal);
    }
}
