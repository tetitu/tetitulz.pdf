# HTJ2K fixture

`simple_enc_rev53_tall_narrow.j2c` is from OpenJPH's conformance codestreams,
<https://github.com/aous72/jp2k_test_codestreams> (commit `1ccd349d12`, `openjph/`), © 2022 Aous Naman,
under the BSD 2-Clause licence in `LICENSE` beside it. It is unmodified.

A 16 × 256 RGB image, reversible (5-3), HT code-blocks only: the one HTJ2K codestream in the
repository, so the HT cleanup pass is checked where the corpora are absent (CI). `JpxHtFixtureTests`
asserts its decode against OpenJPEG 2.5.4's (through Pillow, 2026-10-03): SHA-256
`d2d8c21e6f5dae52faa7fd1dfbf82005341aa9bc179c11d7ef73634e0685fc69` over the samples interleaved R, G, B
in raster order — a lossless decode, so two correct decoders agree byte for byte.
