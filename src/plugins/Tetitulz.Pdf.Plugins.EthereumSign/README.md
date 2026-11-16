# Tetitulz.Pdf.Plugins.EthereumSign

Anchors a document's hash on an **Ethereum** chain and verifies it later - a timestamp-and-existence
proof that does not depend on a certificate authority.

Isolates Nethereum from the core package graph.

```csharp
var provider = new BlockchainSignatureProvider();

BlockchainProof proof = await provider.RecordHashAsync(
    documentHash,
    new BlockchainParameters { ChainId = 1, RpcUrl = "https://…", GasLimit = 100_000 });

BlockchainVerificationResult check =
    await provider.VerifyHashAsync(documentHash, proof, parameters);
```

## What this proves, and what it does not

It proves **that this exact hash existed by the time that transaction was mined**. It says nothing
about *who* produced the document: there is no identity, no certificate chain and no revocation.

That is a different claim from a PAdES signature, not a stronger one. If you need "this person
signed this", use `Tetitulz.Pdf.Signing` with a real certificate; if you need "this existed then",
this is the cheaper mechanism. They compose, and neither substitutes for the other.

## What the verification holds the chain to

- **The transaction is in a block.** One the endpoint knows but that has not been mined records
  nothing; it is reported as not found.
- **The hash on the chain is this document's.** An empty hash matches nothing.
- **The sender is the address the proof names.** A proof travels with the document, so anyone can
  anchor a hash from an address of their own and write a proof that says so: by default the check
  shows the document was anchored, not by whom, and `BlockchainVerificationResult.Warning` says
  that. Set `BlockchainParameters.ExpectedSignerAddress` to accept an anchor from one address
  only; `SignerMatches` then holds the sender to it as well as to the proof.

## The RPC URL is yours

`BlockchainParameters.RpcUrl` is caller-configured and is **not** subject to the private-address
guard that applies to document-derived URLs - localhost is the normal setup for a local node. That
is deliberate, and it means the URL you supply is a network-egress decision you are making.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices, including
Nethereum's.
