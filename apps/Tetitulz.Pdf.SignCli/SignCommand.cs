// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.SignCli
{
    /// <summary>
    /// The command line: <c>sign</c> (the default) signs a PDF with a PFX certificate, <c>verify</c>
    /// checks the signatures a PDF carries. One source for both builds — .NET Framework 4.8
    /// (<c>tetitulz-sign</c>) and .NET 10 (<c>tetitulz-sign10</c>) — reading and writing through the
    /// writers it is handed, so it runs without a console.
    /// </summary>
    internal sealed class SignCommand
    {
        public const int ExitOk = 0;
        public const int ExitBadArgs = 2;
        public const int ExitIoFailure = 3;
        public const int ExitSignError = 4;
        public const int ExitSanitizeError = 5;
        public const int ExitNotVerified = 6;
        public const int ExitUnsigned = 7;

        /// <summary>
        /// Bytes reserved in <c>/Contents</c> when <c>--tsa</c> or <c>--ltv</c> is given without
        /// <c>--pades</c>: the figure <c>ApplyPadesBaseline</c> sets, which the library keeps internal.
        /// </summary>
        private const int LtvSignatureReserveBytes = 32768;

        private static readonly string[] KnownOptions =
        {
            "--help", "-h",
            "--input", "-i", "--output", "-o", "--pfx", "-p", "--password", "-w",
            "--password-env", "--password-stdin",
            "--reason", "-r", "--location", "-l", "--field", "-f", "--tsa", "-t",
            "--ltv", "--pades", "--sanitize", "--image", "--page", "--rect",
            "--trust", "--pdf-password-env",
        };

        /// <summary>The visible signature's rectangle when <c>--image</c> is given without <c>--rect</c>.</summary>
        private static readonly float[] DefaultRect = { 50, 50, 200, 80 };

        private readonly string _toolName;
        private readonly TextWriter _out;
        private readonly TextWriter _err;
        private readonly TextReader _in;

        public SignCommand(string toolName, TextWriter output, TextWriter error, TextReader input)
        {
            _toolName = toolName;
            _out = output;
            _err = error;
            _in = input;
        }

        /// <summary>Runs one command line and returns the exit code.</summary>
        public int Run(string[] args)
        {
            if (args == null || args.Length == 0 || HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintUsage();
                return args == null || args.Length == 0 ? ExitBadArgs : ExitOk;
            }

            if (args[0] == "verify") return Verify(args.Skip(1).ToArray());
            if (args[0] == "sign") return Sign(args.Skip(1).ToArray());
            return Sign(args);
        }

        // ── sign ────────────────────────────────────────────────────────────

        private int Sign(string[] args)
        {
            string? input    = GetArg(args, "--input",    "-i");
            string? output   = GetArg(args, "--output",   "-o");
            string? pfx      = GetArg(args, "--pfx",      "-p");
            string? reason   = GetArg(args, "--reason",   "-r");
            string? location = GetArg(args, "--location", "-l");
            string  field    = GetArg(args, "--field",    "-f") ?? "Signature1";
            string? tsaUrl   = GetArg(args, "--tsa",      "-t");
            string? padesArg = GetArg(args, "--pades",    null);
            bool    embedLtv = HasFlag(args, "--ltv");
            bool    sanitize = HasFlag(args, "--sanitize");
            string? image    = GetArg(args, "--image",    null);
            string? pageArg  = GetArg(args, "--page",     null);
            string? rectArg  = GetArg(args, "--rect",     null);

            if (!TryReadSecret(args, "--password", "-w", "--password-env", "--password-stdin", "the password", out string? password))
                return ExitBadArgs;

            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(pfx) || password == null)
            {
                _err.WriteLine("error: --input, --output, --pfx and a password (--password, --password-env or --password-stdin) are required.");
                PrintUsage();
                return ExitBadArgs;
            }
            if (!File.Exists(input))
            {
                _err.WriteLine($"error: input PDF not found: {input}");
                return ExitIoFailure;
            }
            if (!File.Exists(pfx))
            {
                _err.WriteLine($"error: PFX file not found: {pfx}");
                return ExitIoFailure;
            }

            int page = 1;
            float[] rect = DefaultRect;
            if (image == null)
            {
                if (pageArg != null || rectArg != null)
                {
                    _err.WriteLine("error: --page and --rect place the visible signature and require --image.");
                    return ExitBadArgs;
                }
            }
            else
            {
                if (!File.Exists(image))
                {
                    _err.WriteLine($"error: image file not found: {image}");
                    return ExitIoFailure;
                }
                if (pageArg != null && (!int.TryParse(pageArg, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
                {
                    _err.WriteLine($"error: --page '{pageArg}' is not a page number (1 or more).");
                    return ExitBadArgs;
                }
                if (rectArg != null && !TryParseRect(rectArg, out rect))
                {
                    _err.WriteLine($"error: --rect '{rectArg}' is not x,y,width,height in points (finite numbers, width and height above 0).");
                    return ExitBadArgs;
                }
            }

            // PdfSigner embeds /DSS only when both EmbedLtv and a TSA URL are set, so --ltv alone
            // would report success with no revocation data: refuse it.
            if (embedLtv && string.IsNullOrEmpty(tsaUrl))
            {
                _err.WriteLine("error: --ltv requires --tsa.");
                return ExitBadArgs;
            }

            // ApplyPadesBaseline overwrites --tsa/--ltv rather than merging them (B-B drops the TSA URL
            // and EmbedLtv, B-T drops EmbedLtv), so a contradictory combination is refused rather than
            // silently narrowed. B-LTA needs a second document-timestamp pass this tool does not make.
            if (!string.IsNullOrEmpty(padesArg))
            {
                string level = padesArg!.Trim().ToUpperInvariant();
                if (level is "B-B" or "B_B" && (!string.IsNullOrEmpty(tsaUrl) || embedLtv))
                {
                    _err.WriteLine("error: --pades B-B cannot be combined with --tsa or --ltv (B-B is the baseline without a timestamp or revocation data).");
                    return ExitBadArgs;
                }
                if (level is "B-T" or "B_T" && embedLtv)
                {
                    _err.WriteLine("error: --pades B-T cannot be combined with --ltv (B-T carries a timestamp but no revocation data).");
                    return ExitBadArgs;
                }
                if (level is "B-LTA" or "B_LTA")
                {
                    _err.WriteLine("error: --pades B-LTA is not supported: it requires a second document-timestamp pass (PdfDocumentTimestamper.AddTimestamp) that this tool does not perform. Use --pades B-LT.");
                    return ExitBadArgs;
                }
            }

            var parameters = new SignatureParameters
            {
                CertificatePath       = pfx,
                CertificatePassword   = password,
                Reason                = reason,
                Location              = location,
                SignatureFieldName    = field,
                TimestampAuthorityUrl = tsaUrl,
                EmbedLtv              = embedLtv,
            };

            if (!string.IsNullOrEmpty(padesArg))
            {
                if (!TryParsePadesBaseline(padesArg!, out PadesBaseline baseline))
                {
                    _err.WriteLine($"error: --pades '{padesArg}' is not one of B-B, B-T, B-LT, B-LTA.");
                    return ExitBadArgs;
                }
                try { parameters.ApplyPadesBaseline(baseline, tsaUrl); }
                catch (ArgumentException ex)
                {
                    _err.WriteLine($"error: {ex.Message}");
                    return ExitBadArgs;
                }
            }
            else if (embedLtv || !string.IsNullOrEmpty(tsaUrl))
            {
                // The size PadesBaseline reserves, so the placeholder holds the TSA token and OCSP
                // responses on a hand-rolled configuration too.
                parameters.EstimatedSignatureSize = LtvSignatureReserveBytes;
            }

            // The signature is written to a file beside the output and moved over it only once signing
            // has succeeded: a refusal (a wrong password, a page out of range) then leaves no empty
            // output behind and an existing file at the output path as it was.
            string fullOutput = Path.GetFullPath(output);
            string pending = fullOutput + ".signing-" + Guid.NewGuid().ToString("N") + ".tmp";
            Stream? toSign = null;
            string? signerWarning;
            try
            {
                if (!sanitize)
                    toSign = File.OpenRead(input);
                else if ((toSign = SanitizedCopy(input!)) == null)
                    return ExitSanitizeError;

                Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? ".");
                using (var outStream = File.Create(pending))
                {
                    if (image == null)
                        new PdfSigner().Sign(toSign, outStream, parameters);
                    else
                        VisualSignatureBuilder.Sign(toSign, outStream, File.ReadAllBytes(image), parameters,
                                                    page, rect[0], rect[1], rect[2], rect[3]);
                    // Per thread, and this is the thread that signed.
                    signerWarning = PdfSigner.LastSignerWarning;
                }
                // The input may be the output path: it is closed before the signed file replaces it.
                toSign.Dispose();
                // .NET Framework's File.Move cannot overwrite; File.Replace swaps an existing file.
                if (File.Exists(fullOutput)) File.Replace(pending, fullOutput, null);
                else File.Move(pending, fullOutput);
            }
            catch (PdfException ex)
            {
                // PdfSigningException, and the visible signature's refusals (an unreadable image, a page
                // out of range, encrypted input).
                _err.WriteLine($"sign failed: {ex.Message}");
                return ExitSignError;
            }
            catch (IOException ex)
            {
                _err.WriteLine($"io error: {ex.Message}");
                return ExitIoFailure;
            }
            catch (UnauthorizedAccessException ex)
            {
                _err.WriteLine($"access denied: {ex.Message}");
                return ExitIoFailure;
            }
            finally
            {
                toSign?.Dispose();
                if (File.Exists(pending)) File.Delete(pending);
            }

            _out.WriteLine($"signed: {fullOutput}");
            // The signature is written either way; what a verifier will refuse it for is said now.
            if (signerWarning != null) _err.WriteLine($"warning: a verifier will refuse this signature: {signerWarning}");
            return ExitOk;
        }

        /// <summary>
        /// The input sanitized with <see cref="SanitizeOptions.Default"/>, held in memory so the output
        /// file is created only once the input has been cleaned; null, with the reason written, when the
        /// sanitizer refuses the input.
        /// </summary>
        private MemoryStream? SanitizedCopy(string input)
        {
            var cleaned = new MemoryStream();
            try
            {
                using (var inStream = File.OpenRead(input))
                    PdfSanitizer.Sanitize(inStream, cleaned, SanitizeOptions.Default);
            }
            catch (Exception ex) when (ex is PdfException || ex is InvalidOperationException)
            {
                // PdfSanitizer refuses encrypted input (PdfEncryptedDocumentException) and a signed
                // document (InvalidOperationException), whose signature its rewrite would break.
                cleaned.Dispose();
                _err.WriteLine($"sanitize failed: {ex.Message}");
                return null;
            }
            if (PdfSanitizer.LastXfaWarning is string xfaWarning)
                _err.WriteLine($"sanitize warning: {xfaWarning}");
            if (PdfSanitizer.LastObjectWarning is string objectWarning)
                _err.WriteLine($"sanitize warning: {objectWarning}");
            cleaned.Position = 0;
            return cleaned;
        }

        // ── verify ──────────────────────────────────────────────────────────

        private int Verify(string[] args)
        {
            string? input = GetArg(args, "--input", "-i") ?? FirstPositional(args);
            string? trust = GetArg(args, "--trust", null);
            string? passwordEnv = GetArg(args, "--pdf-password-env", null);

            if (string.IsNullOrEmpty(input))
            {
                _err.WriteLine("error: verify needs the PDF: verify --input <pdf>.");
                return ExitBadArgs;
            }
            if (!File.Exists(input))
            {
                _err.WriteLine($"error: input PDF not found: {input}");
                return ExitIoFailure;
            }
            string? pdfPassword = null;
            if (passwordEnv != null && (pdfPassword = Environment.GetEnvironmentVariable(passwordEnv)) == null)
            {
                _err.WriteLine($"error: the environment variable {passwordEnv} is not set.");
                return ExitBadArgs;
            }

            var options = new SignatureVerificationOptions();
            if (trust != null)
            {
                if (!TrustAnchors.TryLoad(trust, out var anchors, out string? why))
                {
                    _err.WriteLine($"error: --trust: {why}");
                    return ExitBadArgs;
                }
                options.TrustAnchors = anchors;
            }

            IReadOnlyList<SignatureVerificationResult> results;
            try
            {
                using (var stream = File.OpenRead(input))
                {
                    bool encrypted;
                    using (var probe = PdfDocument.Open(stream)) encrypted = probe.IsEncrypted;
                    stream.Position = 0;
                    if (encrypted)
                    {
                        // The signatures cover the file's own bytes, read under its password.
                        Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();
                        results = Tetitulz.Pdf.Encryption.PdfEncryptionOperations.VerifySignatures(stream, pdfPassword ?? "", options);
                    }
                    else
                    {
                        results = PdfSigningOperations.VerifySignatures(stream, options);
                    }
                }
            }
            catch (PdfException ex)
            {
                _err.WriteLine($"verify failed: {ex.Message}");
                return ExitNotVerified;
            }
            catch (IOException ex)
            {
                _err.WriteLine($"io error: {ex.Message}");
                return ExitIoFailure;
            }

            if (results.Count == 0)
            {
                _out.WriteLine("not signed");
                return ExitUnsigned;
            }

            bool allGood = true;
            foreach (SignatureVerificationResult r in results)
            {
                bool good = r.IsValid && (trust == null || r.IsFullyTrusted);
                allGood &= good;
                string verdict = !r.IsValid ? "INVALID" : r.IsFullyTrusted ? "VALID, TRUSTED" : trust == null ? "VALID (trust not checked: no --trust)" : "VALID, NOT TRUSTED";
                _out.WriteLine($"{r.FieldName ?? "(no field)"}: {verdict}");
                _out.WriteLine($"  signer:    {r.SignerCommonName ?? r.SignerSubject ?? "(unknown)"}");
                if (r.ClaimedSigningTime is DateTime claimed)
                    _out.WriteLine($"  signed:    {claimed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC (claimed)");
                if (r.TimestampTime is DateTime stamped)
                    _out.WriteLine($"  timestamp: {stamped.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC{(r.HasTrustedTimestamp ? "" : " (not trusted)")}");
                _out.WriteLine($"  covers:    {(r.CoversEntireDocument ? "the whole document" : "an earlier revision; changes follow it")}");
                _out.WriteLine($"  chain:     {r.ChainStatus}; revocation: {r.RevocationStatus}");
                foreach (string e in r.ValidationErrors) _out.WriteLine($"  error:     {e}");
                foreach (string w in r.Warnings) _out.WriteLine($"  warning:   {w}");
            }
            return allGood ? ExitOk : ExitNotVerified;
        }

        // ── arguments ───────────────────────────────────────────────────────

        /// <summary>
        /// A secret given one way: on the command line, from an environment variable, or as the first
        /// line of standard input. False, with the reason written, when it is given twice or the
        /// variable is unset; null when not given at all.
        /// </summary>
        private bool TryReadSecret(string[] args, string longName, string shortName, string envName, string stdinName, string what, out string? secret)
        {
            secret = GetArg(args, longName, shortName);
            string? env = GetArg(args, envName, null);
            bool stdin = HasFlag(args, stdinName);
            if ((secret == null ? 0 : 1) + (env == null ? 0 : 1) + (stdin ? 1 : 0) > 1)
            {
                _err.WriteLine($"error: give {what} one way: {longName}, {envName} or {stdinName}.");
                return false;
            }
            if (env != null)
            {
                secret = Environment.GetEnvironmentVariable(env);
                if (secret == null)
                {
                    _err.WriteLine($"error: the environment variable {env} is not set.");
                    return false;
                }
            }
            else if (stdin)
            {
                secret = _in.ReadLine() ?? "";
            }
            return true;
        }

        internal static bool TryParseRect(string value, out float[] rect)
        {
            rect = DefaultRect;
            string[] parts = value.Split(',');
            if (parts.Length != 4) return false;
            var parsed = new float[4];
            for (int i = 0; i < 4; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) ||
                    float.IsNaN(parsed[i]) || float.IsInfinity(parsed[i]))
                    return false;
            }
            if (parsed[2] <= 0 || parsed[3] <= 0) return false;
            rect = parsed;
            return true;
        }

        internal static bool TryParsePadesBaseline(string value, out PadesBaseline baseline)
        {
            switch (value.Trim().ToUpperInvariant().Replace('-', '_'))
            {
                case "B_B":   baseline = PadesBaseline.B_B;   return true;
                case "B_T":   baseline = PadesBaseline.B_T;   return true;
                case "B_LT":  baseline = PadesBaseline.B_LT;  return true;
                case "B_LTA": baseline = PadesBaseline.B_LTA; return true;
                default:      baseline = PadesBaseline.None;  return false;
            }
        }

        /// <summary>
        /// The value after an option; null when it is missing or is itself an option, so the
        /// required-argument check and a default both still apply.
        /// </summary>
        internal static string? GetArg(string[] args, string longName, string? shortName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == longName || (shortName != null && args[i] == shortName))
                {
                    if (i + 1 >= args.Length) return null;
                    string next = args[i + 1];
                    return Array.IndexOf(KnownOptions, next) >= 0 ? null : next;
                }
            }
            return null;
        }

        private static string? FirstPositional(string[] args)
            => args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal) ? args[0] : null;

        private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

        private void PrintUsage()
        {
            _out.WriteLine(
$"""
{_toolName} — sign a PDF with a PFX certificate, or check the signatures it carries.

Sign:
  {_toolName} [sign] --input <pdf> --output <pdf> --pfx <pfx>
        (--password <pwd> | --password-env <NAME> | --password-stdin)
        [--reason <text>] [--location <text>] [--field <name>]
        [--tsa <url>] [--ltv] [--pades B-B|B-T|B-LT]
        [--sanitize] [--image <png|jpg> [--page <n>] [--rect x,y,w,h]]

  -i, --input     Source PDF file.
  -o, --output    Destination PDF file (will be overwritten).
  -p, --pfx       Path to .pfx / .p12 certificate file.
  -w, --password  Certificate password (use "" for empty). A command line is
                  visible to other processes and kept in shell history; prefer:
      --password-env <NAME>   read the password from that environment variable.
      --password-stdin        read the password as the first line of standard input.
  -r, --reason    /Reason in the signature dictionary.
  -l, --location  /Location in the signature dictionary.
  -f, --field     Signature form-field name (default: Signature1).
  -t, --tsa       RFC 3161 TSA URL (e.g. http://timestamp.digicert.com).
      --ltv       Embed /DSS with OCSP responses (requires --tsa).
      --pades     Apply PAdES baseline (B-B, B-T, or B-LT). B-T and above require --tsa.
      --sanitize  Before signing, strip JavaScript, launch, submit-form and
                  remote actions, the open action, embedded files, XFA,
                  RichMedia and 3D annotations, and non-web URI links.
                  Refused for an encrypted or already-signed input.
      --image     Visible signature: a PNG (transparency kept) or JPEG stretched
                  over --rect. The field is named by the tool; --field is ignored.
      --page      1-based page for the visible signature (default: 1).
      --rect      x,y,width,height in points from the page's lower-left corner
                  (default: 50,50,200,80).
  A signature a verifier would refuse (an expired certificate, a weak digest) is
  still written, with a warning on standard error.

Verify:
  {_toolName} verify --input <pdf> [--trust <file-or-folder>] [--pdf-password-env <NAME>]

      --trust             Trusted root certificates: a .cer/.crt/.pem file, or a folder
                          of them. Without it the chain's trust is not checked.
      --pdf-password-env  The password of an encrypted PDF, from that variable.

Exit codes:
  0  success                 2  bad arguments
  3  io failure              4  signing failure
  5  sanitize failure        6  a signature does not verify (or is not trusted)
  7  the document is not signed
""");
        }
    }
}
