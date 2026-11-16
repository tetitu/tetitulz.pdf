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
/// No pattern in library code under <c>src/</c> is built with <c>RegexOptions.Compiled</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Every pattern over a field a file controls carries a match
/// timeout, and a timeout reads as "not found". A compiled pattern's first match pays its JIT
/// inside that window, which a loaded host exceeds (a whole-solution run's test hosts sharing the
/// machine), and a finder then answers "not found" for what it finds a moment later alone. A field
/// under a length cap is a few kilobytes, which the interpreter scans in microseconds, so compiling
/// buys nothing the cap has not already bought and costs a wrong answer on a loaded host.</para>
/// <para><b>A guard whose value is an absence must prove it looked</b>: the pattern is first run
/// against a control it must match, and the scan is checked to reach the file the finding was in.</para>
/// </remarks>
public class NoCompiledRegexTests
{
    private static readonly Regex CompiledOption = new Regex(@"\bRegexOptions\s*\.\s*Compiled\b");

    [RepoSourcesFact]
    public void NoLibraryPatternIsCompiled()
    {
        Assert.True(CompiledOption.IsMatch("new Regex(\"x\", RegexOptions.Compiled | RegexOptions.IgnoreCase, t)"),
            "the pattern no longer matches its own control, so a clean scan below would mean nothing");

        string src = RepoSources.SrcRoot!;
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (TestSourceScanner.IsGenerated(file)) continue;
            string rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            scanned.Add(rel);

            string text = TestSourceScanner.StripComments(File.ReadAllText(file));
            foreach (Match m in CompiledOption.Matches(text))
                offenders.Add($"{rel}:{TestSourceScanner.LineOf(text, m.Index)}");
        }

        Assert.Contains("tetitulz.pdf/Tetitulz.Pdf.Base/Operations/Forms/InvoiceValueParser.cs", scanned);

        Assert.True(offenders.Count == 0,
            "Library code builds a pattern with RegexOptions.Compiled. A pattern over a bounded field " +
            "is interpreted, under a one-second match timeout (CLAUDE.md, XML and regular expressions):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
