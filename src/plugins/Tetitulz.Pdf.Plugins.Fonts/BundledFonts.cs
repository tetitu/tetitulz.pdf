// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND OFL-1.1) also carries the Liberation and Noto fonts,
// which keep their own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Plugins.Fonts
{
    /// <summary>
    /// The fonts this package carries — Liberation Sans, Serif and Mono in Regular, Bold, Italic and
    /// Bold Italic (the metric-compatible stand-ins for Helvetica, Times and Courier that
    /// <c>Tetitulz.Pdf.Core</c> maps the standard 14 to first), and Noto Sans Hebrew and Noto Sans
    /// Arabic in Regular and Bold — written to a directory on first use and registered with
    /// <see cref="FontSearchPath"/>, so a host with no fonts of its own renders predictably.
    /// </summary>
    /// <remarks>
    /// <para><b>Mechanism in Core, payload here.</b> Nothing in Core depends on this assembly; it adds
    /// a directory through the one public door, <see cref="FontSearchPath.Add"/>, which puts it ahead
    /// of the operating system's directories and <c>TETITULZ_PDF_FONTDIR</c>'s — so the bundled
    /// Liberation Sans is the one found when the host also has one, and two hosts render a page the
    /// same whatever they have installed.</para>
    /// <para><b>Where the files go.</b> <see cref="Register()"/> writes them under the account's
    /// own application-data folder, in a folder named by this assembly's version, writing each file
    /// unless it already holds the bundled bytes; <see cref="Register(string)"/> takes the host's
    /// own directory instead, for a service that controls where it writes. <see cref="Extract"/>
    /// writes without registering, for a host that wants the files alone. All three are idempotent
    /// and safe to call from several threads.</para>
    /// <para><b>The directory is searched first, so it is one only this account writes.</b> A font
    /// found there is the one rendered and embedded — a bundled name replaced, or any other family
    /// added. The temporary directory is every account's on Linux (<c>/tmp</c>) and every
    /// service's on Windows (<c>C:\Windows\Temp</c>), so <see cref="Register()"/> does not keep a
    /// well-known folder there: an account with no data folder it can write gets a directory of
    /// this process's own, newly made and so empty of anyone else's files, removed when the process
    /// ends. A host that passes its own directory to <see cref="Register(string)"/> answers for who
    /// can write it.</para>
    /// <para><b>Licence.</b> Every font is redistributed unmodified under the SIL Open Font License
    /// 1.1; the licence texts, with each family's copyright and Reserved Font Names, are packed beside
    /// this package and written beside the fonts. The OFL permits bundling with software and forbids
    /// selling the fonts by themselves.</para>
    /// </remarks>
    public static class BundledFonts
    {
        private const string ResourcePrefix = "Tetitulz.Pdf.Plugins.Fonts.Fonts.";
        private static readonly object Gate = new object();
        private static string? _directory;

        /// <summary>The directory the fonts were last written to and registered from, or null before <see cref="Register()"/>.</summary>
        public static string? Directory
        {
            get { lock (Gate) return _directory; }
        }

        /// <summary>The file names this package carries, sorted — sixteen TrueType files.</summary>
        public static IReadOnlyList<string> FileNames
        {
            get
            {
                var names = new List<string>();
                foreach (string resource in typeof(BundledFonts).Assembly.GetManifestResourceNames())
                    if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                        names.Add(FileNameOf(resource));
                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        /// <summary>
        /// Writes the fonts under the account's own application-data folder (a folder named by this
        /// assembly's version) and registers that directory first in the search path; for an account
        /// with no such folder it can write, a directory of this process's own under the temporary
        /// directory. Returns the directory.
        /// </summary>
        public static string Register()
            => RegisterUnder(OwnDataFolder());

        /// <summary>
        /// <see cref="Register()"/> with the account's data folder given: the versioned folder under
        /// <paramref name="dataFolder"/> when it can be written, else — a null folder, or one that
        /// refuses the write — this process's own temporary directory.
        /// </summary>
        internal static string RegisterUnder(string? dataFolder)
        {
            if (!string.IsNullOrEmpty(dataFolder))
            {
                try { return Register(VersionedFolder(dataFolder!)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return Register(ProcessTemporaryDirectory());
        }

        private static string? _processTemporary;

        /// <summary>
        /// A directory under the temporary directory that this process made and no one else had a
        /// chance to fill: its name is not known before it exists, and on Unix under .NET 7 and later
        /// only this account can enter it. Made once a process and removed, with what it holds, when
        /// the process ends.
        /// </summary>
        private static string ProcessTemporaryDirectory()
        {
            lock (Gate)
            {
                if (_processTemporary != null) return _processTemporary;
#if NET
                string made = System.IO.Directory.CreateTempSubdirectory("tetitulz-pdf-fonts-").FullName;
#else
                string made = Path.Combine(Path.GetTempPath(), "tetitulz-pdf-fonts-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(made);
#endif
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { System.IO.Directory.Delete(made, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                };
                return _processTemporary = made;
            }
        }

        /// <summary>The account's application-data folder — <c>%LOCALAPPDATA%</c>, <c>~/.local/share</c> — or null when the account has none.</summary>
        private static string? OwnDataFolder()
        {
            string folder = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrEmpty(folder) ? null : folder;
        }

        private static string VersionedFolder(string root)
        {
            string version = typeof(BundledFonts).Assembly.GetName().Version?.ToString() ?? "0";
            return Path.Combine(root, "tetitulz-pdf-fonts", version);
        }

        /// <summary>Writes the fonts into <paramref name="directory"/> and registers it first in the search path. Returns the directory.</summary>
        public static string Register(string directory)
        {
            string full = Extract(directory);
            FontSearchPath.Add(full);
            lock (Gate) _directory = full;
            return full;
        }

        /// <summary>Removes the directory <see cref="Register()"/> registered from the search path; the files stay.</summary>
        public static void Unregister()
        {
            string? dir;
            lock (Gate) { dir = _directory; _directory = null; }
            if (dir != null) FontSearchPath.Remove(dir);
        }

        /// <summary>
        /// Writes the fonts and their licence texts into <paramref name="directory"/>, creating it,
        /// writing each file only when it is absent or of a different length, and returns the full
        /// path. Registers nothing.
        /// </summary>
        public static string Extract(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("A directory is required.", nameof(directory));
            string full = Path.GetFullPath(directory);
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(full);
                var asm = typeof(BundledFonts).Assembly;
                foreach (string resource in asm.GetManifestResourceNames())
                {
                    if (resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                        WriteIfDifferent(asm, resource, Path.Combine(full, FileNameOf(resource)));
                    else if (resource.StartsWith(LicencePrefix, StringComparison.Ordinal))
                        WriteIfDifferent(asm, resource, Path.Combine(full, resource.Substring(LicencePrefix.Length)));
                }
            }
            return full;
        }

        private const string LicencePrefix = "Tetitulz.Pdf.Plugins.Fonts.Licenses.";

        /// <summary>The licence files written beside the fonts, one per family.</summary>
        public static IReadOnlyList<string> LicenceFileNames
        {
            get
            {
                var names = new List<string>();
                foreach (string resource in typeof(BundledFonts).Assembly.GetManifestResourceNames())
                    if (resource.StartsWith(LicencePrefix, StringComparison.Ordinal))
                        names.Add(resource.Substring(LicencePrefix.Length));
                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        /// <summary>
        /// The folder the parameterless <see cref="Register()"/> writes to when it can: under the
        /// account's application-data folder, named by this assembly's version, so an upgrade writes
        /// afresh. For an account with no data folder this is the same name under the temporary
        /// directory, which <see cref="Register()"/> does not use — <see cref="Directory"/> says
        /// where the fonts went.
        /// </summary>
        public static string DefaultDirectory()
            => VersionedFolder(OwnDataFolder() ?? Path.GetTempPath());

        private static void WriteIfDifferent(Assembly asm, string resource, string path)
        {
            using Stream? stream = asm.GetManifestResourceStream(resource);
            if (stream == null) return;
            // A file already there is kept only when it is these bytes, not merely this length: in a
            // directory another account can write to, a planted font of the same length would be
            // registered first in the search path, and rendered and embedded as the bundled one.
            // This checks the bundled names when Register runs; it does not look at other files in
            // the directory, nor guard it afterwards — which is why Register() chooses a directory
            // only this account writes.
            if (File.Exists(path))
            {
                try { if (SameBytes(stream, path)) return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                stream.Position = 0;
            }
            // Written whole to a sibling and moved into place, so a reader on another thread or in
            // another process never opens a half-written font.
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = File.Create(temp)) stream.CopyTo(file);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (IOException)
            {
                // Another writer won the race; its file is the same bytes.
                try { File.Delete(temp); } catch (IOException) { }
            }
        }

        /// <summary>Whether the file at <paramref name="path"/> holds exactly <paramref name="resource"/>'s bytes; the resource is read from its start.</summary>
        private static bool SameBytes(Stream resource, string path)
        {
            using var file = File.OpenRead(path);
            if (file.Length != resource.Length) return false;
            var a = new byte[64 * 1024];
            var b = new byte[a.Length];
            while (true)
            {
                int n = Core.IO.StreamBytes.Fill(resource, a, 0, a.Length);
                int m = Core.IO.StreamBytes.Fill(file, b, 0, n == 0 ? b.Length : n);
                if (n != m) return false;
                if (n == 0) return true;
                for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            }
        }

        /// <summary>
        /// The resource name is the path with separators turned to dots:
        /// <c>…Fonts.Liberation.LiberationSans-Regular.ttf</c>. The file name is the last two segments.
        /// </summary>
        private static string FileNameOf(string resource)
        {
            string tail = resource.Substring(ResourcePrefix.Length);          // Liberation.LiberationSans-Regular.ttf
            int firstDot = tail.IndexOf('.');
            return firstDot < 0 ? tail : tail.Substring(firstDot + 1);          // LiberationSans-Regular.ttf
        }
    }
}
