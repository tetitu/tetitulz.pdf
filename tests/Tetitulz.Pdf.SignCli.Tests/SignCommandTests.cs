// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.SignCli;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.SignCli.Tests;

/// <summary>
/// The signing tool's command line, run in-process: what it refuses and why, and a signature made
/// and then verified end to end — with and without a trust anchor.
/// </summary>
public sealed class SignCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "signcli-tests-" + Guid.NewGuid().ToString("N"));

    public SignCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static (int Code, string Out, string Err) Run(params string[] args) => RunWithInput("", args);

    private static (int Code, string Out, string Err) RunWithInput(string stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = new SignCommand("tetitulz-sign10", o, e, new StringReader(stdin)).Run(args);
        return (code, o.ToString(), e.ToString());
    }

    private string File(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    // ── refusals, each for its reason ───────────────────────────────────────

    [Fact]
    public void No_arguments_print_the_usage_and_are_a_usage_error()
    {
        var (code, output, _) = Run();
        Assert.Equal(SignCommand.ExitBadArgs, code);
        Assert.Contains("verify --input", output, StringComparison.Ordinal);
        Assert.Equal(SignCommand.ExitOk, Run("--help").Code);
    }

    [Fact]
    public void A_password_given_two_ways_is_refused()
    {
        var (code, _, err) = Run("--input", "a.pdf", "--output", "b.pdf", "--pfx", "c.pfx", "--password", "x", "--password-stdin");
        Assert.Equal(SignCommand.ExitBadArgs, code);
        Assert.Contains("one way", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_arguments_are_named()
    {
        var (code, _, err) = Run("--input", "a.pdf", "--output", "b.pdf", "--password", "x");
        Assert.Equal(SignCommand.ExitBadArgs, code);
        Assert.Contains("--pfx", err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_after_an_option_is_not_its_value()
    {
        Assert.Null(SignCommand.GetArg(new[] { "--reason", "--location", "x" }, "--reason", "-r"));
        Assert.Equal("x", SignCommand.GetArg(new[] { "--reason", "--location", "x" }, "--location", "-l"));
        Assert.Null(SignCommand.GetArg(new[] { "--field" }, "--field", "-f"));
    }

    [Theory]
    [InlineData("1,2,3,4", true)]
    [InlineData("1.5, 2 ,3,4", true)]
    [InlineData("1,2,0,4", false)]          // no width
    [InlineData("1,2,3,-4", false)]         // a negative height
    [InlineData("1,2,3", false)]
    [InlineData("1,2,NaN,4", false)]
    [InlineData("1,2,Infinity,4", false)]
    [InlineData("a,2,3,4", false)]
    public void A_rectangle_is_four_finite_numbers_with_a_positive_size(string text, bool ok)
        => Assert.Equal(ok, SignCommand.TryParseRect(text, out _));

    [Fact]
    public void The_pdf_and_the_certificate_must_exist()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        Assert.Equal(SignCommand.ExitIoFailure, Run("-i", Path.Combine(_dir, "none.pdf"), "-o", "x.pdf", "-p", "c.pfx", "-w", "x").Code);
        Assert.Equal(SignCommand.ExitIoFailure, Run("-i", pdf, "-o", "x.pdf", "-p", Path.Combine(_dir, "none.pfx"), "-w", "x").Code);
    }

    [Fact]
    public void Ltv_without_a_timestamp_authority_is_refused()
    {
        var (pdf, pfx) = (File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf()), File("c.pfx", Pfx(out _, out _, "pw")));
        var (code, _, err) = Run("-i", pdf, "-o", Path.Combine(_dir, "out.pdf"), "-p", pfx, "-w", "pw", "--ltv");
        Assert.Equal(SignCommand.ExitBadArgs, code);
        Assert.Contains("--ltv requires --tsa", err, StringComparison.Ordinal);
        Assert.False(System.IO.File.Exists(Path.Combine(_dir, "out.pdf")));
    }

    [Fact]
    public void Placing_a_signature_needs_its_picture()
    {
        var (pdf, pfx) = (File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf()), File("c.pfx", Pfx(out _, out _, "pw")));
        var (code, _, err) = Run("-i", pdf, "-o", Path.Combine(_dir, "out.pdf"), "-p", pfx, "-w", "pw", "--page", "2");
        Assert.Equal(SignCommand.ExitBadArgs, code);
        Assert.Contains("require --image", err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_certificate_password_signs_nothing_and_leaves_no_output()
    {
        var (pdf, pfx) = (File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf()), File("c.pfx", Pfx(out _, out _, "right")));
        string output = Path.Combine(_dir, "out.pdf");
        var (code, _, _) = Run("-i", pdf, "-o", output, "-p", pfx, "-w", "wrong");
        Assert.NotEqual(SignCommand.ExitOk, code);
        Assert.False(System.IO.File.Exists(output));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ── sign, then verify ───────────────────────────────────────────────────

    [Fact]
    public void A_signature_made_by_the_tool_verifies_and_is_trusted_under_its_root()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        string pfx = File("c.pfx", Pfx(out X509Certificate root, out _, "pw"));
        string signed = Path.Combine(_dir, "signed.pdf");

        var sign = RunWithInput("pw\n", "-i", pdf, "-o", signed, "-p", pfx, "--password-stdin", "--reason", "Approved");
        Assert.Equal(SignCommand.ExitOk, sign.Code);
        Assert.Contains("signed:", sign.Out, StringComparison.Ordinal);

        // Without anchors: valid, and said to be unchecked for trust.
        var plain = Run("verify", "--input", signed);
        Assert.Equal(SignCommand.ExitOk, plain.Code);
        Assert.Contains("VALID (trust not checked", plain.Out, StringComparison.Ordinal);

        // Under its own root: trusted.
        string rootFile = File("root.cer", root.GetEncoded());
        var trusted = Run("verify", "--input", signed, "--trust", rootFile);
        Assert.Equal(SignCommand.ExitOk, trusted.Code);
        Assert.Contains("VALID, TRUSTED", trusted.Out, StringComparison.Ordinal);

        // Under another root: valid, not trusted, and the exit code says so.
        Pfx(out X509Certificate otherRoot, out _, "x");
        string otherFile = File("other.cer", otherRoot.GetEncoded());
        var untrusted = Run("verify", "--input", signed, "--trust", otherFile);
        Assert.Equal(SignCommand.ExitNotVerified, untrusted.Code);
        Assert.Contains("VALID, NOT TRUSTED", untrusted.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signature_a_verifier_will_refuse_is_written_with_a_warning()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        string expired = File("expired.pfx", Pfx(out _, out _, "pw", expired: true));
        string valid = File("valid.pfx", Pfx(out _, out _, "pw"));

        var refused = Run("-i", pdf, "-o", Path.Combine(_dir, "a.pdf"), "-p", expired, "-w", "pw");
        Assert.Equal(SignCommand.ExitOk, refused.Code);
        Assert.True(System.IO.File.Exists(Path.Combine(_dir, "a.pdf")));
        Assert.Contains("warning: a verifier will refuse this signature", refused.Err, StringComparison.Ordinal);

        // The control: a certificate within its validity period signs without the warning.
        var clean = Run("-i", pdf, "-o", Path.Combine(_dir, "b.pdf"), "-p", valid, "-w", "pw");
        Assert.Equal(SignCommand.ExitOk, clean.Code);
        Assert.DoesNotContain("warning", clean.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timestamp_authority_that_does_not_answer_fails_the_signing()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        string pfx = File("c.pfx", Pfx(out _, out _, "pw"));
        string output = Path.Combine(_dir, "stamped.pdf");
        // Port 9 (discard) on the loopback answers nothing a timestamp client accepts.
        var result = Run("-i", pdf, "-o", output, "-p", pfx, "-w", "pw", "--tsa", "http://127.0.0.1:9/");
        Assert.Equal(SignCommand.ExitSignError, result.Code);
        Assert.False(System.IO.File.Exists(output));
    }

    [Fact]
    public void A_changed_signed_document_does_not_verify()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        string pfx = File("c.pfx", Pfx(out _, out _, "pw"));
        string signed = Path.Combine(_dir, "signed.pdf");
        Assert.Equal(SignCommand.ExitOk, Run("-i", pdf, "-o", signed, "-p", pfx, "-w", "pw").Code);

        // One byte of the signed page content changed: "Hello" becomes "Jello".
        byte[] bytes = System.IO.File.ReadAllBytes(signed);
        int at = IndexOf(bytes, "Hello"u8.ToArray());
        Assert.True(at > 0);
        bytes[at] = (byte)'J';
        string tampered = File("tampered.pdf", bytes);

        var result = Run("verify", tampered);
        Assert.Equal(SignCommand.ExitNotVerified, result.Code);
        Assert.Contains("INVALID", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsigned_document_is_said_to_be_unsigned()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        var result = Run("verify", pdf);
        Assert.Equal(SignCommand.ExitUnsigned, result.Code);
        Assert.Contains("not signed", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trust_file_with_no_certificate_is_refused()
    {
        string pdf = File("in.pdf", MinimalPdfBuilder.CreateMinimalPdf());
        string junk = File("junk.cer", new byte[] { 1, 2, 3 });
        var result = Run("verify", pdf, "--trust", junk);
        Assert.Equal(SignCommand.ExitBadArgs, result.Code);
        Assert.Contains("junk.cer", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trust_folder_reads_every_certificate_file_in_it()
    {
        Pfx(out X509Certificate a, out _, "x");
        Pfx(out X509Certificate b, out _, "x");
        string folder = Path.Combine(_dir, "roots");
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllBytes(Path.Combine(folder, "a.cer"), a.GetEncoded());
        System.IO.File.WriteAllText(Path.Combine(folder, "b.pem"),
            "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(b.GetEncoded(), Base64FormattingOptions.InsertLineBreaks) + "\n-----END CERTIFICATE-----\n");
        System.IO.File.WriteAllText(Path.Combine(folder, "readme.txt"), "not a certificate");

        Assert.True(TrustAnchors.TryLoad(folder, out var anchors, out string? why), why);
        Assert.Equal(2, anchors.Count);
        Assert.Contains(anchors, c => c.Equals(a));
        Assert.Contains(anchors, c => c.Equals(b));
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <summary>A PKCS#12 file holding a signing certificate and its key, issued by a root of its own.</summary>
    private static byte[] Pfx(out X509Certificate root, out X509Certificate leaf, string password, bool expired = false)
    {
        AsymmetricCipherKeyPair rootKeys = RsaTestKeys.Next(), leafKeys = RsaTestKeys.Next();
        var rootName = new X509Name("CN=SignCli Test Root " + Guid.NewGuid().ToString("N")[..8]);
        root = Certificate(rootName, rootName, rootKeys.Public, rootKeys.Private, ca: true);
        leaf = Certificate(rootName, new X509Name("CN=SignCli Test Signer"), leafKeys.Public, rootKeys.Private, ca: false, expired);

        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("signer", new AsymmetricKeyEntry(leafKeys.Private),
            new[] { new X509CertificateEntry(leaf), new X509CertificateEntry(root) });
        using var ms = new MemoryStream();
        store.Save(ms, password.ToCharArray(), new SecureRandom());
        return ms.ToArray();
    }

    private static X509Certificate Certificate(X509Name issuer, X509Name subject, AsymmetricKeyParameter publicKey,
        AsymmetricKeyParameter signingKey, bool ca, bool expired = false)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(64, new SecureRandom()));
        gen.SetIssuerDN(issuer);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(expired ? DateTime.UtcNow.AddYears(-2) : DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(expired ? DateTime.UtcNow.AddYears(-1) : DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(publicKey);
        if (ca)
        {
            gen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign));
        }
        else
        {
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.NonRepudiation));
        }
        return gen.Generate(new Asn1SignatureFactory("SHA256WITHRSA", signingKey));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
