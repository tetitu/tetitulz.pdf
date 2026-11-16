// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Loads the trust anchors <see cref="Models.SignatureVerificationOptions.TrustAnchors"/> needs,
    /// from a PEM or DER file, a directory of them, or bytes the caller already has.
    /// </summary>
    /// <remarks>
    /// <para><b>Without anchors nothing can be <c>Trusted</c></b> —
    /// <c>SignatureVerificationResult.IsFullyTrusted</c> is unreachable and
    /// <c>IsValid</c> reports integrity only. Yet the property takes
    /// <c>Org.BouncyCastle.X509.X509Certificate</c> and the assembly's own certificate parsing is
    /// <c>internal</c>, so without this a caller would reach for BouncyCastle's API themselves to
    /// use this library's headline verification feature.</para>
    ///
    /// <para><b>Not "an Israeli CA loader".</b> Nothing in
    /// <c>CertificateChainValidator</c> is CA-specific, and this library deliberately ships no
    /// certificate bundle — whose anchors to trust is a policy decision belonging to the
    /// deployment, and shipping a list would be this code making it. What a caller needs is a way
    /// to load the bundle <i>they</i> chose; that is generic, so this is.</para>
    ///
    /// <para><b>One method reads PEM and DER alike.</b> BouncyCastle's
    /// <see cref="X509CertificateParser"/> sniffs the encoding, so asking the caller which one they
    /// have would be this API inventing a distinction the parser does not make — and getting it
    /// wrong is a failure the caller cannot diagnose from the outside.</para>
    ///
    /// <para><b>No OS certificate store here, deliberately.</b> A caller who wants the machine's
    /// roots can read them and pass the bytes to <see cref="FromDer"/> in two lines, which keeps
    /// platform-store handling out of an assembly that multi-targets <c>netstandard2.0</c> — the
    /// same separation <c>Tetitulz.Pdf.Plugins.CertStoreSign</c> exists to preserve on the signing
    /// side.</para>
    /// </remarks>
    public static class TrustAnchorLoader
    {
        /// <summary>
        /// Certificate files read from one directory. A trust bundle is tens of files; a directory
        /// of thousands is a misconfiguration, and parsing every one of them is not free.
        /// </summary>
        private const int MaxFilesPerDirectory = 512;

        /// <summary>
        /// Ceiling on one certificate file. A PEM bundle of a full root program is a few hundred KB;
        /// anything past this is not a certificate file and should not be inflated into memory.
        /// </summary>
        private const int MaxFileBytes = 8 * 1024 * 1024;

        [ThreadStatic] private static string? _lastError;

        /// <summary>
        /// Why the most recent load <b>on this thread</b> returned fewer anchors than the caller
        /// expected, or null when nothing was skipped. Cleared at the start of every call.
        /// </summary>
        /// <remarks>
        /// A load that finds nothing usable returns an <b>empty list rather than throwing</b>,
        /// because the common cause is a path that exists and holds the wrong thing — and an empty
        /// anchor list then silently turns every signature into "valid but not trusted", which is
        /// indistinguishable from a correctly-loaded set that simply does not cover this signer.
        /// This says which happened.
        /// </remarks>
        public static string? LastError => _lastError;

        /// <summary>Reads every certificate in <paramref name="pemOrDer"/>.</summary>
        /// <param name="pemOrDer">A PEM or DER stream. Not disposed.</param>
        public static IReadOnlyList<X509Certificate> Load(Stream pemOrDer)
        {
            _lastError = null;
            if (pemOrDer == null) throw new ArgumentNullException(nameof(pemOrDer));

            return Parse(pemOrDer, "the stream");
        }

        /// <summary>Reads every certificate in one PEM or DER file.</summary>
        /// <param name="path">Path to the file.</param>
        public static IReadOnlyList<X509Certificate> LoadFile(string path)
        {
            _lastError = null;
            if (path == null) throw new ArgumentNullException(nameof(path));

            return ReadOne(path);
        }

        /// <summary>
        /// Reads every certificate in every file of a directory — the shape a distribution's
        /// <c>ca-certificates</c> directory has.
        /// </summary>
        /// <param name="path">Directory to read. Not searched recursively.</param>
        /// <param name="searchPattern">Which files to consider; the default takes them all.</param>
        /// <remarks>
        /// <para><b>A file that will not parse is skipped, not fatal.</b> Such a directory routinely
        /// holds a README, a hash symlink or an index; refusing the whole bundle because one entry
        /// is not a certificate would make the common configuration unusable. Every skip is counted
        /// in <see cref="LastError"/>, so "I loaded 3 of 140" is visible rather than inferred.</para>
        /// </remarks>
        public static IReadOnlyList<X509Certificate> LoadDirectory(string path, string searchPattern = "*")
        {
            _lastError = null;
            if (path == null) throw new ArgumentNullException(nameof(path));

            string[] files;
            try { files = Directory.GetFiles(path, searchPattern ?? "*"); }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                _lastError = "the directory could not be listed: " + ex.Message;
                return Array.Empty<X509Certificate>();
            }

            Array.Sort(files, StringComparer.Ordinal);   // deterministic across filesystems

            var all = new List<X509Certificate>();
            int skipped = 0;
            int considered = Math.Min(files.Length, MaxFilesPerDirectory);

            for (int i = 0; i < considered; i++)
            {
                var one = ReadOne(files[i]);
                if (one.Count == 0) skipped++;
                all.AddRange(one);
            }

            var notes = new List<string>();
            if (skipped > 0)
                notes.Add(skipped + " of " + considered + " files held no readable certificate");
            if (files.Length > considered)
                notes.Add("only the first " + considered + " of " + files.Length +
                          " files were read (MaxFilesPerDirectory)");
            if (all.Count == 0 && considered == 0)
                notes.Add("the directory matched no files");

            _lastError = notes.Count == 0 ? null : string.Join("; ", notes.ToArray());
            return all;
        }

        /// <summary>
        /// Wraps DER bytes the caller already holds — for instance
        /// <c>X509Certificate2.RawData</c> read from an OS certificate store.
        /// </summary>
        /// <param name="derCertificates">One DER-encoded certificate per element.</param>
        /// <remarks>
        /// This is the seam for the platform store. Two lines in the caller
        /// (<c>new X509Store(StoreName.Root, StoreLocation.LocalMachine)</c>, then
        /// <c>FromDer(store.Certificates.Select(c =&gt; c.RawData))</c>) keep the store API in the
        /// host, where its platform behaviour is the host's to reason about.
        /// </remarks>
        public static IReadOnlyList<X509Certificate> FromDer(IEnumerable<byte[]> derCertificates)
        {
            _lastError = null;
            if (derCertificates == null) throw new ArgumentNullException(nameof(derCertificates));

            var parser = new X509CertificateParser();
            var all = new List<X509Certificate>();
            int skipped = 0;

            foreach (byte[] der in derCertificates)
            {
                if (der == null || der.Length == 0) { skipped++; continue; }
                try
                {
                    var cert = parser.ReadCertificate(der);
                    if (cert != null) all.Add(cert); else skipped++;
                }
                catch (Exception) { skipped++; }
            }

            if (skipped > 0) _lastError = skipped + " entries were not readable certificates";
            return all;
        }

        // ------------------------------------------------------------------

        private static IReadOnlyList<X509Certificate> ReadOne(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    _lastError = "the file does not exist: " + path;
                    return Array.Empty<X509Certificate>();
                }

                if (info.Length > MaxFileBytes)
                {
                    _lastError = "the file is " + info.Length +
                                 " bytes, past the " + MaxFileBytes + "-byte ceiling: " + path;
                    return Array.Empty<X509Certificate>();
                }

                using (var fs = File.OpenRead(path))
                    return Parse(fs, path);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                _lastError = "the file could not be read: " + ex.Message;
                return Array.Empty<X509Certificate>();
            }
        }

        private static IReadOnlyList<X509Certificate> Parse(Stream source, string what)
        {
            var result = new List<X509Certificate>();
            try
            {
                // ReadCertificates, not ReadCertificate: a PEM trust bundle is many certificates in
                // one file, and reading only the first would silently trust a fraction of the set
                // the caller supplied.
                var parsed = new X509CertificateParser().ReadCertificates(source);
                if (parsed != null)
                    foreach (X509Certificate c in parsed)
                        if (c != null) result.Add(c);
            }
            catch (Exception ex)
            {
                _lastError = "no certificate could be parsed from " + what + ": " + ex.Message;
                return Array.Empty<X509Certificate>();
            }

            if (result.Count == 0 && _lastError == null)
                _lastError = "no certificate was found in " + what;

            return result;
        }

        // The two parse sites above catch broadly on purpose, matching CertificateChainValidator:
        // BouncyCastle's parser reports a malformed encoding through several unrelated exception
        // types and documents none of them, so enumerating a set here would silently let the next
        // one escape a method whose contract is to return an empty list. Filesystem failures below
        // ARE enumerable, so those stay typed.
        private static bool IsIoFailure(Exception ex)
            => ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException;
    }
}
