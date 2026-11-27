// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A host font file is one program for an export, whatever code points each run asks of it, so the
/// exporter (which embeds a font once per program instance) embeds it once.
/// </summary>
/// <remarks>
/// The resolver's per-export cache is keyed by code-point set, so a document's paragraphs resolve
/// again and again; a face the process-wide file cache cannot hold (larger than
/// <see cref="BuilderFontResolver.MaxCachedFontBytes"/>, or any face once it is full) is read again
/// at each, and every read is a new instance. Host-independent: the one candidate is named through
/// <c>HostFontFilesForTest</c>.
/// </remarks>
public sealed class HostFontOneProgramTests : IDisposable
{
    private readonly List<string> _written = new List<string>();

    public void Dispose()
    {
        foreach (string path in _written)
            try { File.Delete(path); } catch (IOException) { /* a leftover temp file is not a failure */ }
    }

    [Fact]
    public void AFaceTheFileCacheCannotHold_IsOneProgramForEveryCodePointSet()
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-oneprogram-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, MinimalTrueType.BuildWithTables("אבג",
            new Dictionary<string, byte[]> { ["zpad"] = new byte[BuilderFontResolver.MaxCachedFontBytes + 1] }));
        _written.Add(path);

        var resolver = new BuilderFontResolver(new PdfExportOptions { AllowSystemFontSubstitution = true })
        {
            HostFontFilesForTest = new[] { path },
        };

        // Potency: every read of this file is a new program.
        Assert.NotSame(resolver.LoadFontFileForTest(path), resolver.LoadFontFileForTest(path));

        var first = resolver.Resolve(null, false, false, new[] { 0x05D0 });
        Assert.NotNull(first);
        Assert.Same(first, resolver.Resolve(null, false, false, new[] { 0x05D1 }));
        Assert.Same(first, resolver.Resolve(null, false, false, new[] { 0x05D0, 0x05D2 }));
        // A program the export holds still answers only for what it covers.
        Assert.Null(resolver.Resolve(null, false, false, new[] { 0x05D0, 0x05E9 }));

        // The control: another export reads the file for itself.
        var other = new BuilderFontResolver(new PdfExportOptions { AllowSystemFontSubstitution = true })
        {
            HostFontFilesForTest = new[] { path },
        };
        Assert.NotSame(first, other.Resolve(null, false, false, new[] { 0x05D0 }));
    }
}
