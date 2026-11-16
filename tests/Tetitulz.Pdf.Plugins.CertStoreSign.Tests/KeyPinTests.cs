// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Tetitulz.Pdf.Plugins.CertStoreSign.Tests;

/// <summary>Skips visibly off Windows: a key's PIN is handed to its provider through Windows' key storage.</summary>
public sealed class WindowsKeyFactAttribute : FactAttribute
{
    public WindowsKeyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "A key's PIN is handed to its provider through Windows' key storage, which is not here.";
    }
}

/// <summary>The same, for a theory.</summary>
public sealed class WindowsKeyTheoryAttribute : TheoryAttribute
{
    public WindowsKeyTheoryAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "A key's PIN is handed to its provider through Windows' key storage, which is not here.";
    }
}

/// <summary>
/// <see cref="CertStoreExternalSigner.KeyPin"/>: the PIN handed to the key's provider, so that the
/// provider does not ask for it.
/// </summary>
/// <remarks>
/// <para><b>Tested on a key that asks for a password, not on a smart card.</b> Windows' own software
/// provider keeps a key under a password when the key is made with one, and takes that password
/// through the same property a smart card's provider takes its PIN through. So the three answers a
/// token gives are all here: no PIN and no window allowed — refused; a wrong PIN — refused before
/// anything is signed; the right one — a signature.</para>
///
/// <para><b>No window can appear.</b> The key is made and deleted through the provider's own
/// functions with their silent flag, which turns a need for a window into a failure; every use
/// through the signer either hands the PIN over or sets <see cref="CertStoreExternalSigner.Silent"/>.
/// </para>
/// </remarks>
public class KeyPinTests
{
    private const string Pin = "4711";
    private const int SilentContext = unchecked((int)0x80090022);       // NTE_SILENT_CONTEXT
    private const int IncorrectPassword = unchecked((int)0x80090033);   // NTE_INCORRECT_PASSWORD
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("the PDF byte range");

    [WindowsKeyTheory]
    [InlineData("RSA")]
    [InlineData("ECDSA_P256")]
    public void WithThePin_TheKeySigns_AndTheSignatureVerifiesOverTheContent(string algorithm)
    {
        using var key = new ProtectedKey(algorithm, Pin);
        using var signer = new CertStoreExternalSigner(key.Certificate) { Silent = true, KeyPin = Pin };

        byte[] signature = signer.SignData(Content, "SHA-256");

        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(signature);
        cms.CheckSignature(verifySignatureOnly: true);
        Assert.Equal(key.Certificate.Thumbprint, Assert.Single(cms.SignerInfos.Cast<SignerInfo>()).Certificate!.Thumbprint);
    }

    /// <summary>
    /// The control for the test above, and what tells a host to ask for the PIN: the same key,
    /// with no PIN and no window allowed, does not sign.
    /// </summary>
    [WindowsKeyFact]
    public void WithoutThePin_ASilentSignerIsRefused_ForWantOfAWindowToAskIn()
    {
        using var key = new ProtectedKey("RSA", Pin);
        using var signer = new CertStoreExternalSigner(key.Certificate) { Silent = true };

        CryptographicException refused = Assert.ThrowsAny<CryptographicException>(() => signer.SignData(Content, "SHA-256"));

        Assert.Equal(SilentContext, refused.HResult);
    }

    [WindowsKeyFact]
    public void AWrongPin_IsRefusedAsOne_AndTheRightOneStillSigns()
    {
        using var key = new ProtectedKey("RSA", Pin);
        using var signer = new CertStoreExternalSigner(key.Certificate) { Silent = true, KeyPin = "0000" };

        CryptographicException refused = Assert.ThrowsAny<CryptographicException>(() => signer.SignData(Content, "SHA-256"));
        Assert.Equal(IncorrectPassword, refused.HResult);

        signer.KeyPin = Pin;
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(signer.SignData(Content, "SHA-256"));
        cms.CheckSignature(verifySignatureOnly: true);
    }

    /// <summary>
    /// A key that takes no PIN refuses one handed to it — the provider's answer, passed on: a host
    /// hands a PIN over only after the key has asked for one. The control: with none, it signs.
    /// </summary>
    [WindowsKeyFact]
    public void AKeyThatTakesNoPin_RefusesOneHandedToIt_AndSignsWithNone()
    {
        using X509Certificate2 plain = UnprotectedCertificate();
        using var signer = new CertStoreExternalSigner(plain) { Silent = true, KeyPin = Pin };

        Assert.ThrowsAny<CryptographicException>(() => signer.SignData(Content, "SHA-256"));

        signer.KeyPin = null;
        var cms = new SignedCms(new ContentInfo(Content), detached: true);
        cms.Decode(signer.SignData(Content, "SHA-256"));
        cms.CheckSignature(verifySignatureOnly: true);
    }

    /// <summary>
    /// A key of an older provider type takes its PIN as the key's password when the key is opened:
    /// the same provider, container and key, in the same store, and never a new key.
    /// </summary>
    [WindowsKeyTheory]
    [InlineData(CspProviderFlags.NoFlags, false)]
    [InlineData(CspProviderFlags.UseMachineKeyStore, true)]
    public void AnOlderProvidersKey_IsReopenedWithThePinAsItsPassword(CspProviderFlags store, bool machine)
    {
#pragma warning disable CA1416 // Windows only: the attribute skips the test elsewhere.
        var info = new CspKeyContainerInfo(new CspParameters(24, "A Token's Provider", "container-7") { KeyNumber = 2, Flags = store });

        CspParameters reopened = CertStoreExternalSigner.CspParametersWithPin(info, Pin);

        Assert.Equal(24, reopened.ProviderType);
        Assert.Equal("A Token's Provider", reopened.ProviderName);
        Assert.Equal("container-7", reopened.KeyContainerName);
        Assert.Equal(2, reopened.KeyNumber);
        Assert.True(reopened.Flags.HasFlag(CspProviderFlags.UseExistingKey));
        Assert.Equal(machine, reopened.Flags.HasFlag(CspProviderFlags.UseMachineKeyStore));
        Assert.Equal(Pin, Plain(reopened.KeyPassword!));
        Assert.True(reopened.KeyPassword!.IsReadOnly());
#pragma warning restore CA1416
    }

    private static string Plain(SecureString secret)
    {
        IntPtr text = Marshal.SecureStringToBSTR(secret);
        try { return Marshal.PtrToStringBSTR(text); }
        finally { Marshal.ZeroFreeBSTR(text); }
    }

    private static X509Certificate2 UnprotectedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=No PIN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// A key in Windows' software provider kept under a password, with a certificate over it, for
    /// one test: made silently, deleted when the test ends.
    /// </summary>
    private sealed class ProtectedKey : IDisposable
    {
        private const string Provider = "Microsoft Software Key Storage Provider";
        private const int Silent = 0x40;                                 // NCRYPT_SILENT_FLAG
        private readonly string _name = "tetitulz-keypin-test-" + Guid.NewGuid().ToString("N");
        private readonly string _pin;

        public X509Certificate2 Certificate { get; }

        public ProtectedKey(string algorithm, string pin)
        {
            _pin = pin;
            Create(algorithm);
            try
            {
#pragma warning disable CA1416 // Windows only: the attributes skip the tests elsewhere.
                using CngKey key = CngKey.Open(_name, new CngProvider(Provider));
                // The PIN before the key's first use, so that making the certificate asks for nothing.
                key.SetProperty(new CngProperty("SmartCardPin", PinBytes(pin), CngPropertyOptions.None));
                DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow.AddDays(1);
                if (algorithm == "RSA")
                {
                    using var rsa = new RSACng(key);
                    Certificate = new CertificateRequest("CN=KeyPin Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(from, to);
                }
                else
                {
                    using var ecdsa = new ECDsaCng(key);
                    Certificate = new CertificateRequest("CN=KeyPin Test", ecdsa, HashAlgorithmName.SHA256).CreateSelfSigned(from, to);
                }
#pragma warning restore CA1416
            }
            catch
            {
                Delete();
                throw;
            }
        }

        public void Dispose()
        {
            Certificate.Dispose();
            Delete();
        }

        private static byte[] PinBytes(string pin) => Encoding.Unicode.GetBytes(pin + "\0");

        private static void Check(int result, string step)
        {
            if (result != 0) throw new InvalidOperationException($"The test's protected key could not be made: {step} answered 0x{result:X8}.");
        }

        private void Create(string algorithm)
        {
            Check(NCryptOpenStorageProvider(out IntPtr provider, Provider, 0), "opening the provider");
            try
            {
                Check(NCryptCreatePersistedKey(provider, out IntPtr key, algorithm, _name, 0, 0), "creating the key");
                try
                {
                    if (algorithm == "RSA") Check(NCryptSetProperty(key, "Length", BitConverter.GetBytes(2048), 4, 0), "setting the length");
                    // NCRYPT_UI_POLICY: version 1, NCRYPT_UI_FORCE_HIGH_PROTECTION_FLAG, three null strings.
                    byte[] policy = new byte[8 + 3 * IntPtr.Size];
                    BitConverter.GetBytes(1).CopyTo(policy, 0);
                    BitConverter.GetBytes(2).CopyTo(policy, 4);
                    Check(NCryptSetProperty(key, "UI Policy", policy, policy.Length, 0), "asking for a password-protected key");
                    byte[] pin = PinBytes(_pin);
                    Check(NCryptSetProperty(key, "SmartCardPin", pin, pin.Length, Silent), "setting the password");
                    Check(NCryptFinalizeKey(key, Silent), "finishing the key");
                }
                finally
                {
                    NCryptFreeObject(key);
                }
            }
            finally
            {
                NCryptFreeObject(provider);
            }
        }

        private void Delete()
        {
            if (NCryptOpenStorageProvider(out IntPtr provider, Provider, 0) != 0) return;
            try
            {
                if (NCryptOpenKey(provider, out IntPtr key, _name, 0, 0) != 0) return;
                byte[] pin = PinBytes(_pin);
                NCryptSetProperty(key, "SmartCardPin", pin, pin.Length, Silent);
                // A deleted key's handle is the provider's to free; one that stayed is ours.
                if (NCryptDeleteKey(key, Silent) != 0) NCryptFreeObject(key);
            }
            finally
            {
                NCryptFreeObject(provider);
            }
        }

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptOpenStorageProvider(out IntPtr provider, string name, int flags);

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptCreatePersistedKey(IntPtr provider, out IntPtr key, string algorithm, string name, int keySpec, int flags);

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptSetProperty(IntPtr handle, string property, byte[] input, int length, int flags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptFinalizeKey(IntPtr key, int flags);

        [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int NCryptOpenKey(IntPtr provider, out IntPtr key, string name, int keySpec, int flags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptDeleteKey(IntPtr key, int flags);

        [DllImport("ncrypt.dll")]
        private static extern int NCryptFreeObject(IntPtr handle);
    }
}
