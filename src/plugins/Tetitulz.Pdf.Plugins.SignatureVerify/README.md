# Tetitulz.Pdf.Plugins.SignatureVerify

Registers Tetitulz.Pdf signature verification for **string-keyed, run-time discovery**.

`Tetitulz.Pdf.Signing` verifies signatures directly. This package is the adapter for hosts that
resolve capabilities at run time rather than referencing the verifier themselves.

```csharp
var plugin = new SignatureVerifyPlugin();
plugin.Initialize();
PdfPluginHost.Instance.Register(plugin);

IReadOnlyList<SignatureVerificationResult> results = plugin.VerifyAll(stream);
```

## Verification policy still has to be yours

`VerifyAll(Stream)` verifies with the default policy, and the default policy has **no trust
anchors** - so every chain status stays `Unknown`, `SelfSigned` or `UntrustedRoot`, and nothing can
ever be `Trusted`. That is an integrity check, not a trust decision.

When the verdict matters, call `Tetitulz.Pdf.Signing` directly with a
`SignatureVerificationOptions` carrying your anchors, and read `IsFullyTrusted` rather than
`IsValid`. Putting a green tick next to `IsValid` is the most common way to ship a PDF-signature
bug: it says the bytes match the CMS and nothing about who signed them.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
