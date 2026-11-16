// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// No library code under <c>src/</c> calls native code directly: no <c>DllImport</c>, no
/// <c>LibraryImport</c>, no <c>extern</c> method, no <c>NativeLibrary</c> load and no unmanaged
/// function pointer. Native code runs in-process only inside third-party packages.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> CLAUDE.md said "zero P/Invokes" with one exception,
/// <c>OpenJpeg2Interop</c> in <c>Base/Images</c> — an optional <c>libopenjp2</c> fallback for JPEG 2000.
/// That one exception allocated 512 bytes for an <c>opj_dparameters_t</c> of about 8 KB, which
/// <c>opj_set_default_decoder_parameters</c> zeroes in full, so every native decode overran the
/// heap; no test saw it because the path ran only on hosts with the library installed. It was
/// removed rather than fixed, because the managed plugin already covered the case. A native codec
/// comes back as a plugin that isolates it, never as a P/Invoke inside the library.</para>
///
/// <para><b>A guard whose value is an absence must prove it looked.</b> The tree has no matches, so
/// every pattern is first run against a control it must match, and the scan is checked to reach
/// the file the removed P/Invoke was called from.</para>
/// </remarks>
public class NoLibraryPInvokeTests
{
    private static readonly (string What, Regex Pattern, string Control)[] NativeCallShapes =
    {
        ("DllImport", new Regex(@"\bDllImport(?:Attribute)?\s*\("),
            "[DllImport(\"libx\")] private static extern int F();"),
        ("LibraryImport", new Regex(@"\bLibraryImport(?:Attribute)?\s*\("),
            "[LibraryImport(\"libx\")] private static partial int F();"),
        ("an extern method", new Regex(@"\bextern\b(?!\s+alias\b)"),
            "internal static unsafe extern int F(byte* p);"),
        ("NativeLibrary", new Regex(@"\bNativeLibrary\s*\."),
            "IntPtr h = NativeLibrary.Load(\"libx\");"),
        ("an unmanaged function pointer", new Regex(@"delegate\s*\*\s*unmanaged\b"),
            "var f = (delegate* unmanaged[Cdecl]<int>)p;"),
    };

    [RepoSourcesFact]
    public void NoLibrarySourceCallsNativeCodeDirectly()
    {
        foreach (var shape in NativeCallShapes)
            Assert.True(shape.Pattern.IsMatch(shape.Control),
                $"the {shape.What} pattern no longer matches its own control, so a clean scan " +
                "below would mean nothing");

        string src = RepoSources.SrcRoot!;
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceScanner.IsGenerated(file)) continue;
            string rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            scanned.Add(rel);

            string text = TestSourceScanner.StripComments(File.ReadAllText(file));
            foreach (var shape in NativeCallShapes)
                foreach (Match m in shape.Pattern.Matches(text))
                    offenders.Add($"{rel}:{TestSourceScanner.LineOf(text, m.Index)} {shape.What}");
        }

        Assert.Contains("tetitulz.pdf/Tetitulz.Pdf.Base/Images/JpxImageDecoder.cs", scanned);

        Assert.True(offenders.Count == 0,
            "Library code calls native code directly. Put a native dependency in a plugin that " +
            "isolates it (CLAUDE.md, Platforms):" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }
}
