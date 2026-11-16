// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Tetitulz.Pdf.DocxToPdf.Engines;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// Skips <b>visibly</b> off Windows rather than passing.
/// </summary>
/// <remarks>
/// The round trip below calls <c>CommandLineToArgvW</c>, which exists only on Windows. An early
/// <c>return</c> would report Passed for a check that never ran — banned test pattern 2 — so this
/// reports Skipped instead, modelled on <c>CorpusFact</c> / <c>SystemFontFact</c>.
/// </remarks>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "CommandLineToArgvW is a Windows API, so the command-line round trip cannot run here.";
    }
}

/// <summary>
/// Pins <see cref="ProcessArgumentEscaper"/> against <b>the operating system's own parser</b>.
/// </summary>
/// <remarks>
/// <para>This is the test the escaper exists for. Its job is to produce a command line that
/// <c>CommandLineToArgvW</c> — the function <c>soffice.exe</c>'s CRT uses to split its arguments —
/// parses back into exactly the strings we passed. Asserting that against a <i>hand-written
/// expected string</i> would only check the escaper against the same reading of the rules that
/// wrote it. So the assertion is a genuine round trip through the real API.</para>
/// <para>Why it matters: the legacy leg of <c>LibreOfficeConverter</c> has no
/// <c>ProcessStartInfo.ArgumentList</c> and must build the command line itself, and the last
/// argument is a <b>caller-supplied file path</b>. An escaping slip there is argument injection.</para>
/// </remarks>
public class ProcessArgumentEscaperTests
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Splits <paramref name="commandLine"/> exactly as a C runtime would.
    /// </summary>
    /// <remarks>
    /// A dummy program name is prepended and its result dropped: <c>argv[0]</c> is parsed by
    /// <i>different</i> rules (backslashes are never escapes there), so measuring our escaping in
    /// that slot would test the wrong thing — and would pass for an escaper that is wrong everywhere
    /// else.
    /// </remarks>
    private static string[] OsParse(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW("app.exe " + commandLine, out int count);
        Assert.NotEqual(IntPtr.Zero, argv);

        try
        {
            var result = new List<string>(count);
            for (int i = 1; i < count; i++)   // skip argv[0]
            {
                IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(p));
            }
            return result.ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static void RoundTrips(params string[] arguments)
    {
        string commandLine = ProcessArgumentEscaper.Join(arguments);
        Assert.Equal(arguments, OsParse(commandLine));
    }

    // ------------------------------------------------------------------
    //  The round trip, over the shapes that break naive escapers
    // ------------------------------------------------------------------

    [WindowsOnlyFact]
    public void Ordinary_arguments_round_trip()
    {
        RoundTrips("--headless", "--convert-to", "pdf", "--outdir", @"C:\temp\out", @"C:\in\doc.docx");
    }

    [WindowsOnlyFact]
    public void Paths_with_spaces_round_trip()
    {
        RoundTrips("--outdir", @"C:\Program Files\Some Dir", @"C:\Users\A B\my document.docx");
    }

    [WindowsOnlyFact]
    public void Embedded_quotes_round_trip()
    {
        RoundTrips("a\"b", "\"quoted\"", "say \"hi\" now");
    }

    /// <summary>
    /// A trailing backslash is the everyday case that swallows the closing quote when the
    /// doubling rule is missed — and <c>--outdir C:\temp\</c> is exactly how this converter is
    /// called.
    /// </summary>
    [WindowsOnlyFact]
    public void Trailing_backslashes_round_trip()
    {
        RoundTrips(@"C:\dir with space\", @"C:\a\\", @"ends\\\", "plain");
    }

    [WindowsOnlyFact]
    public void Backslashes_before_a_quote_round_trip()
    {
        RoundTrips(@"a\""b", @"a\\""b", @"a\\\""b");
    }

    /// <summary>An empty argument must survive as an empty argument, not vanish.</summary>
    /// <remarks>
    /// If it disappeared, every argument after it would shift one position left — so LibreOffice
    /// would receive the input path where it expected <c>--outdir</c>'s value.
    /// </remarks>
    [WindowsOnlyFact]
    public void Empty_argument_survives_and_does_not_shift_the_rest()
    {
        RoundTrips("--outdir", string.Empty, "after");
    }

    [WindowsOnlyFact]
    public void Shell_metacharacters_are_literal_and_do_not_split()
    {
        // No shell is involved (UseShellExecute is false), so these must arrive verbatim.
        RoundTrips("a&b", "c|d", "e^f", "g;h", "i>j");
    }

    [WindowsOnlyFact]
    public void Unicode_paths_round_trip()
    {
        RoundTrips(@"C:\תיקייה עם רווח\קובץ.docx", @"C:\日本語\文書 2.docx");
    }

    /// <summary>
    /// The injection attempt this escaping exists to stop: a file name that tries to close the
    /// quoting and append its own switch.
    /// </summary>
    [WindowsOnlyFact]
    public void An_argument_that_tries_to_inject_a_switch_stays_one_argument()
    {
        const string Hostile = @"C:\tmp\a"" --outdir C:\evil ""x.docx";

        string[] parsed = OsParse(ProcessArgumentEscaper.Join(new[] { "--outdir", @"C:\out", Hostile }));

        Assert.Equal(3, parsed.Length);
        Assert.Equal(Hostile, parsed[2]);
        Assert.DoesNotContain(@"C:\evil", parsed[0], StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\evil", parsed[1], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Acceptance control
    // ------------------------------------------------------------------

    /// <summary>
    /// Without this, every test above could pass with an escaper that quotes everything
    /// unconditionally — correct, but hiding whether the "no special characters" fast path works.
    /// </summary>
    [Fact]
    public void An_argument_needing_no_quoting_is_emitted_verbatim()
    {
        Assert.Equal("--headless", ProcessArgumentEscaper.Join(new[] { "--headless" }));
        Assert.Equal("a b-c d", ProcessArgumentEscaper.Join(new[] { "a", "b-c", "d" }));
    }

    /// <summary>An empty argument is quoted, because nothing else keeps it in the list.</summary>
    [Fact]
    public void An_empty_argument_is_written_as_an_empty_quoted_string()
    {
        Assert.Equal("\"\"", ProcessArgumentEscaper.Join(new[] { string.Empty }));
    }
}
