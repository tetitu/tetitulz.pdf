// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.SignCli
{
    /// <summary>The root certificates a verification trusts, read from a file or a folder of files.</summary>
    internal static class TrustAnchors
    {
        private static readonly string[] Extensions = { ".cer", ".crt", ".pem", ".der" };

        /// <summary>A folder holds at most this many certificate files; a larger one is refused, not cut short.</summary>
        internal const int MaxFiles = 1000;

        /// <summary>
        /// Every certificate in <paramref name="path"/> — a DER or PEM file (several PEM blocks may
        /// share one), or a folder whose <c>.cer</c>, <c>.crt</c>, <c>.pem</c> and <c>.der</c> files
        /// are read. False, with <paramref name="why"/>, when the path is missing, a file holds no
        /// certificate, or the folder holds none.
        /// </summary>
        public static bool TryLoad(string path, out IReadOnlyList<X509Certificate> anchors, out string? why)
        {
            var list = new List<X509Certificate>();
            anchors = list;
            why = null;

            var files = new List<string>();
            if (File.Exists(path))
            {
                files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                foreach (string f in Directory.EnumerateFiles(path))
                {
                    if (Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                    if (files.Count == MaxFiles)
                    {
                        why = $"{path} holds more than {MaxFiles} certificate files.";
                        return false;
                    }
                    files.Add(f);
                }
            }
            else
            {
                why = $"{path} is neither a file nor a folder.";
                return false;
            }

            var parser = new X509CertificateParser();
            foreach (string file in files)
            {
                int before = list.Count;
                try
                {
                    // ReadCertificates reads DER and PEM alike, every certificate in the file.
                    foreach (X509Certificate c in parser.ReadCertificates(File.ReadAllBytes(file)))
                        list.Add(c);
                }
                catch (Exception ex) when (ex is IOException || ex is Org.BouncyCastle.Security.Certificates.CertificateException || ex is ArgumentException)
                {
                    why = $"{Path.GetFileName(file)} could not be read as a certificate: {ex.Message}";
                    return false;
                }
                if (list.Count == before)
                {
                    why = $"{Path.GetFileName(file)} holds no certificate.";
                    return false;
                }
            }
            if (list.Count == 0)
            {
                why = $"{path} holds no certificate file (.cer, .crt, .pem, .der).";
                return false;
            }
            return true;
        }
    }
}
