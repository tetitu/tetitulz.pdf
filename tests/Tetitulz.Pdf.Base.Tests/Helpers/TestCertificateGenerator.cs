// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Base.Tests.Helpers;

internal static class TestCertificateGenerator
{
    public static (byte[] pfxBytes, string password) GenerateTestCertificate(
        string commonName = "CN=Test Signer")
    {
        const string password = "test123";
        var random = new SecureRandom();

        // From the per-test pool: distinct within the test, reused across tests (RsaTestKeys).
        var keyPair = Tetitulz.Pdf.TestHelpers.RsaTestKeys.Next();

        var certGen = new X509V3CertificateGenerator();
        var serialNumber = BigInteger.ProbablePrime(120, random);
        certGen.SetSerialNumber(serialNumber);

        var subjectDn = new X509Name(commonName);
        certGen.SetSubjectDN(subjectDn);
        certGen.SetIssuerDN(subjectDn);
        certGen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certGen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        certGen.SetPublicKey(keyPair.Public);

        var signatureFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
            "SHA256WithRSA", keyPair.Private, random);
        var cert = certGen.Generate(signatureFactory);

        var store = new Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test",
            new AsymmetricKeyEntry(keyPair.Private),
            new[] { new X509CertificateEntry(cert) });

        using var pfxStream = new MemoryStream();
        store.Save(pfxStream, password.ToCharArray(), random);

        return (pfxStream.ToArray(), password);
    }
}
