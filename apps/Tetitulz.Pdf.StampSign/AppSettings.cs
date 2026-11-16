// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>Which corner of the page the signature is measured from.</summary>
    public enum SignatureAnchor { BottomRight, BottomLeft, TopRight, TopLeft }

    /// <summary>Which page the signature goes on.</summary>
    public enum SignaturePageChoice { LastPage, FirstPage, PageShown }

    /// <summary>
    /// What the user chose once and expects again next time: the two images (copied into the app's own
    /// folder, so moving the originals does not lose them), the signature's fixed size and place, the
    /// certificate, and the window.
    /// </summary>
    public sealed class AppSettings
    {
        public string StampImageFile { get; set; }
        public string SignatureImageFile { get; set; }

        public double SignatureWidthMm { get; set; } = 50;
        public double SignatureHeightMm { get; set; } = 20;

        /// <summary>The signature's height follows its picture's proportions; off, the two are set apart.</summary>
        public bool SignatureKeepAspect { get; set; } = true;
        public SignatureAnchor Anchor { get; set; } = SignatureAnchor.BottomRight;
        public double OffsetXMm { get; set; } = 15;
        public double OffsetYMm { get; set; } = 15;
        public SignaturePageChoice Page { get; set; } = SignaturePageChoice.LastPage;

        /// <summary>The default width of a new stamp; its height follows the image.</summary>
        public double StampWidthMm { get; set; } = 40;

        public string CertificateThumbprint { get; set; }
        public string Reason { get; set; } = "";
        public string Location { get; set; } = "";

        /// <summary>An RFC 3161 timestamp authority's URL; empty signs without a timestamp.</summary>
        public string TimestampUrl { get; set; } = "";

        public string LastFolder { get; set; }
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = 1200;
        public int WindowHeight { get; set; } = 850;
        public bool WindowMaximized { get; set; }

        // ── Storage ─────────────────────────────────────────────────────────

        /// <summary>
        /// %APPDATA%\Tetitulz\StampSign — the settings file and the copied images; or the folder the
        /// TETITULZ_STAMPSIGN_HOME variable names (a portable copy, or a trial run that must not touch
        /// the real settings — Windows' own folder ignores an APPDATA variable).
        /// </summary>
        public static string Folder
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("TETITULZ_STAMPSIGN_HOME");
                string dir = !string.IsNullOrWhiteSpace(home)
                    ? Path.GetFullPath(home)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tetitulz", "StampSign");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FilePath => Path.Combine(Folder, "settings.xml");

        /// <summary>The saved settings, or defaults when there are none or they cannot be read.</summary>
        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                var serializer = new XmlSerializer(typeof(AppSettings));
                using (var reader = XmlReader.Create(FilePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    return (AppSettings)serializer.Deserialize(reader) ?? new AppSettings();
            }
            catch (Exception)
            {
                return new AppSettings();   // a damaged file must not stop the app from starting
            }
        }

        public void Save()
        {
            string temp = FilePath + ".tmp";
            var serializer = new XmlSerializer(typeof(AppSettings));
            using (var writer = File.Create(temp)) serializer.Serialize(writer, this);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(temp, FilePath);
        }

        /// <summary>Copies an image into the app's folder under <paramref name="name"/> and returns the copy's path.</summary>
        public static string StoreImage(string sourcePath, string name)
        {
            string target = Path.Combine(Folder, name);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, target, overwrite: true);
            return target;
        }

        public bool HasStampImage => !string.IsNullOrEmpty(StampImageFile) && File.Exists(StampImageFile);
        public bool HasSignatureImage => !string.IsNullOrEmpty(SignatureImageFile) && File.Exists(SignatureImageFile);
    }
}
