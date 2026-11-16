// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics;
using System.IO;

namespace Tetitulz.Pdf.DocxToPdf.Engines
{
    internal static class LibreOfficeConverter
    {
        private static readonly int ProcessTimeoutMs = 120_000;

        public static void ConvertFile(string inputPath, string outputPath, string libreOfficePath)
        {
            string fullInputPath = Path.GetFullPath(inputPath);

            if (!File.Exists(fullInputPath))
                throw new DocxToPdfException($"Input file not found: {fullInputPath}");

            // A directory of this conversion's own: the output, and LibreOffice's profile.
            string tempDir = CreatePrivateDirectory();

            try
            {
                string profileDir = Path.Combine(tempDir, "profile");
                WriteProfile(profileDir);
                // Before the program is looked for, so a document it must not see is refused the same
                // way with LibreOffice or without it.
                string cleaned = ExternalEngineInput.CopyInto(tempDir, fullInputPath);
                string exePath = ResolveExecutablePath(libreOfficePath);

                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    AddConversionArguments(process.StartInfo, tempDir, cleaned, profileDir);

                    process.Start();

                    // Read both pipes asynchronously: a synchronous ReadToEnd on one
                    // stream then the other can deadlock if the child fills the second
                    // pipe's buffer while we're blocked on the first.
                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(ProcessTimeoutMs))
                    {
                        KillWithChildren(process);
                        throw new DocxToPdfException(
                            $"LibreOffice process timed out after {ProcessTimeoutMs / 1000} seconds.");
                    }

                    string stdout = stdoutTask.GetAwaiter().GetResult();
                    string stderr = stderrTask.GetAwaiter().GetResult();

                    if (process.ExitCode != 0)
                    {
                        throw new DocxToPdfException(
                            $"LibreOffice exited with code {process.ExitCode}. " +
                            $"stderr: {stderr}. stdout: {stdout}");
                    }
                }

                // LibreOffice outputs the PDF with the same base name as input
                string baseName = Path.GetFileNameWithoutExtension(cleaned) + ".pdf";
                string resultPath = Path.Combine(tempDir, baseName);

                if (!File.Exists(resultPath))
                {
                    // Try to find any PDF in the temp directory
                    var pdfFiles = Directory.GetFiles(tempDir, "*.pdf");
                    if (pdfFiles.Length > 0)
                    {
                        resultPath = pdfFiles[0];
                    }
                    else
                    {
                        throw new DocxToPdfException(
                            "LibreOffice did not produce a PDF output file.");
                    }
                }

                // Copy result to the desired output path
                string fullOutputPath = Path.GetFullPath(outputPath);
                string outputDir = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                File.Copy(resultPath, fullOutputPath, overwrite: true);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        public static void ConvertStream(Stream input, Stream output, string libreOfficePath)
        {
            // The document and its PDF are written inside a directory of their own rather than
            // beside every other account's files in the temporary directory.
            string holder = CreatePrivateDirectory();
            string tempInput = Path.Combine(holder, "input.docx");
            string tempOutput = Path.Combine(holder, "output.pdf");

            try
            {
                using (var fs = new FileStream(tempInput, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(fs);
                }

                ConvertFile(tempInput, tempOutput, libreOfficePath);

                using (var fs = new FileStream(tempOutput, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.CopyTo(output);
                }
            }
            catch (Exception ex) when (!(ex is DocxToPdfException))
            {
                throw new DocxToPdfException("LibreOffice conversion failed.", ex);
            }
            finally
            {
                try { Directory.Delete(holder, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// A new directory under the temporary directory for one conversion's files. On .NET 7 and
        /// later it is the runtime's own temporary subdirectory, which on Unix only this account can
        /// enter; on the legacy leg it takes the permissions the process's umask gives it.
        /// </summary>
        internal static string CreatePrivateDirectory()
        {
#if NET
            return Directory.CreateTempSubdirectory("tetitu_lo_").FullName;
#else
            string path = Path.Combine(Path.GetTempPath(), "tetitu_lo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
#endif
        }

        /// <summary>Ends the process and, where the runtime can, the processes it started: <c>soffice</c> is a launcher for <c>soffice.bin</c>.</summary>
        private static void KillWithChildren(Process process)
        {
            try
            {
#if NET
                process.Kill(entireProcessTree: true);
#else
                process.Kill();
#endif
            }
            catch { }
        }

        /// <summary>
        /// The configuration a conversion's own LibreOffice profile starts from, in LibreOffice's
        /// <c>registrymodifications.xcu</c> form: macros not run, links from a document outside the
        /// trusted locations not loaded, and a text document's links not updated when it opens.
        /// </summary>
        /// <remarks>
        /// These are requests made of LibreOffice in its own setting names
        /// (<c>org.openoffice.Office.Common/Security/Scripting</c>: <c>MacroSecurityLevel</c>,
        /// <c>DisableMacrosExecution</c>, <c>BlockUntrustedRefererLinks</c>;
        /// <c>org.openoffice.Office.Writer/Content/Update</c>: <c>Link</c>). What LibreOffice then
        /// fetches or runs is LibreOffice's: this library's address guard and file containment do
        /// not reach inside another program, so a host converting untrusted documents with this
        /// engine confines the process itself.
        /// </remarks>
        internal const string ProfileSettings =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" " +
            "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
            "<item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\">" +
            "<prop oor:name=\"MacroSecurityLevel\" oor:op=\"fuse\"><value>3</value></prop></item>\n" +
            "<item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\">" +
            "<prop oor:name=\"DisableMacrosExecution\" oor:op=\"fuse\"><value>true</value></prop></item>\n" +
            "<item oor:path=\"/org.openoffice.Office.Common/Security/Scripting\">" +
            "<prop oor:name=\"BlockUntrustedRefererLinks\" oor:op=\"fuse\"><value>true</value></prop></item>\n" +
            "<item oor:path=\"/org.openoffice.Office.Writer/Content/Update\">" +
            "<prop oor:name=\"Link\" oor:op=\"fuse\"><value>0</value></prop></item>\n" +
            "</oor:items>\n";

        /// <summary>Writes <see cref="ProfileSettings"/> where a profile at <paramref name="profileDir"/> reads it.</summary>
        internal static void WriteProfile(string profileDir)
        {
            string user = Path.Combine(profileDir, "user");
            Directory.CreateDirectory(user);
            File.WriteAllText(Path.Combine(user, "registrymodifications.xcu"), ProfileSettings);
        }

        internal static string ResolveExecutablePath(string userOverride)
        {
            if (!string.IsNullOrEmpty(userOverride))
            {
                if (File.Exists(userOverride))
                    return userOverride;
                throw new DocxToPdfException($"LibreOffice executable not found at: {userOverride}");
            }

            // Search PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            string exeName = IsWindows() ? "soffice.exe" : "soffice";

            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                string candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }

            // Check well-known install locations
            string[] candidates;
            if (IsWindows())
            {
                candidates = new[]
                {
                    @"C:\Program Files\LibreOffice\program\soffice.exe",
                    @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
                };
            }
            else if (IsMacOS())
            {
                candidates = new[]
                {
                    "/Applications/LibreOffice.app/Contents/MacOS/soffice",
                    "/usr/local/bin/soffice"
                };
            }
            else
            {
                // Linux
                candidates = new[]
                {
                    "/usr/bin/soffice",
                    "/usr/lib/libreoffice/program/soffice",
                    "/snap/bin/libreoffice",
                    "/usr/bin/libreoffice"
                };
            }

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                    return path;
            }

            throw new DocxToPdfException(
                "LibreOffice not found. Install LibreOffice or set the LibreOfficePath option. " +
                "Searched PATH and common install locations.");
        }

        /// <summary>
        /// Supplies the <c>soffice</c> command line — the one part of this converter that cannot be
        /// written the same way on both target frameworks.
        /// </summary>
        /// <remarks>
        /// <para><c>ArgumentList</c> passes each argument separately, so a path containing spaces or
        /// quotes cannot break out of the quoting and inject further arguments. It arrived in
        /// .NET Core 2.1 and exists on neither netstandard2.0 nor .NET Framework, where the only
        /// channel is the single <c>Arguments</c> string.</para>
        /// <para><b>net10.0 keeps <c>ArgumentList</c>.</b> Where the framework provides the
        /// guarantee, taking it away in favour of our own code would be a downgrade for every modern
        /// caller — so the two legs differ here on purpose, and only here.</para>
        /// <para>The legacy leg builds the same command line through
        /// <see cref="ProcessArgumentEscaper"/>, a faithful transcription of the algorithm .NET
        /// itself uses for <c>ArgumentList</c>. That is security-sensitive code on a
        /// caller-supplied path, so it is not trusted on the strength of having been written
        /// carefully: <c>ProcessArgumentEscaperTests</c> round-trips its output through the real
        /// <c>CommandLineToArgvW</c> — the very parser <c>soffice.exe</c> will use — and asserts the
        /// arguments come back unchanged.</para>
        /// </remarks>
        private static void AddConversionArguments(ProcessStartInfo startInfo, string tempDir,
                                                   string fullInputPath, string profileDir)
        {
            string[] arguments = ConversionArguments(tempDir, fullInputPath, profileDir);

#if NETSTANDARD2_0
            startInfo.Arguments = ProcessArgumentEscaper.Join(arguments);
#else
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
#endif
        }

        /// <summary>
        /// The <c>soffice</c> arguments for one conversion: a profile of the conversion's own
        /// (<c>-env:UserInstallation</c>), so the settings of <see cref="ProfileSettings"/> apply
        /// whatever the account's profile says and two conversions do not share one; no document
        /// recovery, no start-up document; then the conversion.
        /// </summary>
        internal static string[] ConversionArguments(string tempDir, string fullInputPath, string profileDir)
            => new[]
            {
                "-env:UserInstallation=" + new Uri(profileDir).AbsoluteUri,
                "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
                "--convert-to", "pdf", "--outdir", tempDir, fullInputPath,
            };

        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                || Environment.OSVersion.Platform == PlatformID.Win32S
                || Environment.OSVersion.Platform == PlatformID.Win32Windows;
        }

        private static bool IsMacOS()
        {
            // On .NET Core / .NET 5+, macOS reports PlatformID.Unix.
            // Check for macOS-specific path as fallback.
            if (Environment.OSVersion.Platform == PlatformID.MacOSX)
                return true;
            if (Environment.OSVersion.Platform == PlatformID.Unix
                && Directory.Exists("/Applications")
                && Directory.Exists("/System"))
                return true;
            return false;
        }

    }
}
