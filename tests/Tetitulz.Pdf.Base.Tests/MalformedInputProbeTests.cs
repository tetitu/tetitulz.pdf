// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Malformed input reaches the public entry points as a <b>typed refusal or a graceful degradation</b>
/// — never a hang, a crash, or an exception type that means "we did not think about this".
/// </summary>
/// <remarks>
/// <para>
/// The library already has 86 files of hostile-input coverage aimed at individual parsers. What was
/// missing was the end-to-end question a caller actually asks: <i>if I hand this thing a corrupt
/// file, what happens?</i> These eight shapes were measured on 2026-08-12 and all eight behaved;
/// the value is that they keep behaving.
/// </para>
/// <para>
/// <b>What counts as a pass.</b> Either a documented failure — <see cref="PdfException"/> and its
/// subclasses, or <see cref="ArgumentOutOfRangeException"/> for "page 0 of a 0-page document" — or
/// success. What fails is <see cref="NullReferenceException"/>, <see cref="IndexOutOfRangeException"/>,
/// <see cref="InvalidCastException"/> and friends: those are unhandled shapes, not decisions. A
/// stack overflow or an infinite loop cannot be caught at all, so this test's real guard against
/// them is that it terminates.
/// </para>
/// </remarks>
public class MalformedInputProbeTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "empty", Array.Empty<byte>() };
        yield return new object[] { "header only", Encoding.ASCII.GetBytes("%PDF-1.7\n") };
        yield return new object[] { "garbage", Encoding.ASCII.GetBytes(new string('©', 4096)) };
        yield return new object[] { "no xref", Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n") };
        yield return new object[] { "startxref past EOF", Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< >>\nendobj\ntrailer\n<< /Root 1 0 R >>\nstartxref\n999999\n%%EOF\n") };
        // A /Pages node that is its own kid: the page-tree cycle guard is the only thing between
        // this and unbounded recursion.
        yield return new object[] { "self-referential pages", Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [2 0 R] /Count 1 >>\nendobj\n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n") };
        yield return new object[] { "negative /Length", Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n" +
            "4 0 obj\n<< /Length -5 >>\nstream\nabc\nendstream\nendobj\n" +
            "trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n") };
        // 1e400 overflows a double to infinity; the page box must not propagate that into a
        // bitmap dimension.
        yield return new object[] { "MediaBox overflows to infinity", Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1e400 1e400] >>\nendobj\n" +
            "trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n") };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ExtractText_OnMalformedInput_RefusesOrDegrades(string name, byte[] pdf)
        => AssertHandled(name, () =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.ExtractText(ms);
        });

    [Theory]
    [MemberData(nameof(Cases))]
    public void RenderPageToPng_OnMalformedInput_RefusesOrDegrades(string name, byte[] pdf)
        => AssertHandled(name, () =>
        {
            using var ms = new MemoryStream(pdf);
            PdfOperations.RenderPageToPng(ms, 0, 72);
        });

    private static void AssertHandled(string name, Action act)
    {
        try
        {
            act();
        }
        catch (PdfException)
        {
            // A documented refusal, with a reason. The intended outcome.
        }
        catch (ArgumentOutOfRangeException)
        {
            // "page 0 of a 0-page document" - the honest answer once the file yielded no pages.
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"'{name}' produced {ex.GetType().Name}, which is an unhandled shape rather than a " +
                $"decision. Malformed input must fail as PdfException (or ArgumentOutOfRangeException " +
                $"for an empty page list): {ex.Message}");
        }
    }
}
