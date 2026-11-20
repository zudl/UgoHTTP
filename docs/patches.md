# Dependency patches

Every change UgoHTTP makes to the source of a pinned dependency is listed here.

## Policy

- A patch only when a defect cannot wait for upstream. The tarball stays as pinned; the patch is `native/deps/patches/<dep>-<version>-<topic>.patch`, paths relative to the tarball root.
- `PATCH_COMMAND` applies every `patches/<dep>-*.patch` in name order with `patch -p1 --forward --batch --fuzz=0` (`native/deps/CMakeLists.txt`): a hunk that does not apply exactly fails the build. Patch text is part of the dependency signature (`native/deps/driver.py`, the container sanitizer deps in `native/tests/run`), so editing a patch rebuilds its dependency; the content-addressed tarball cache is untouched. `patch` is among the test container's locked packages.
- Each patch carries a git-format-patch header: the upstream licence lines, the targeted version and files, the defect with a repro, the fix, the test that catches it, and when to drop it.
- Each patch has an entry below and a sentence in its dependency's section of `package/Third Party Notices.md`.

## curl-8.23.0-unfinished-content-encoding.patch

| | |
|---|---|
| Dependency | curl 8.23.0: `lib/content_encoding.c`, `lib/content_encoding.h`, `lib/transfer.c` |
| Fixes | A gzip or deflate body that ends before its end marker while the HTTP framing is complete is reported as success with the decoded prefix; patched, it fails with `CURLE_BAD_CONTENT_ENCODING` (decisions.md *curl patch: unfinished content coding*) |
| Upstream status | Not reported |
| Drop at re-pin | When the new curl fails the unfinished stream itself |
| Caught by | Native test `content_encoding_truncated`; .NET `GzipIsDecodedUnlessTheCallerSetsAcceptEncoding` |

`Curl_xfer_write_done`, on a transfer that is not premature, returns `CURLE_BAD_CONTENT_ENCODING` for any gzip/deflate writer that took body bytes and did not finish. "Took body bytes" is a sticky flag set on the first non-empty body write, not `z_stream.total_in`: that is an `unsigned long`, 32-bit on Windows x64 and armeabi-v7a, and wraps to 0 after 4 GiB. The check sits there and not on `CLIENTWRITE_EOS` or in flush: an HTTP/1.1 body with `Content-Length` and a chunked body end without EOS reaching the decoders, and flush also runs on unpause. A writer with no input passes: an empty body with `Content-Encoding: gzip` and the ignored body of a followed redirect. An earlier error is kept (`multi_done` keeps the first fatal result).

## curl-8.23.0-wakeup-nodelay.patch

| | |
|---|---|
| Dependency | curl 8.23.0: `lib/socketpair.c` |
| Fixes | Where curl has no eventfd, pipe or socketpair, `curl_multi_wakeup` writes one byte over a loopback TCP pair; with Nagle on, a wakeup that follows another waits for the delayed ACK of the first, so `ugo_abort`, a submit or a shutdown reaches the owner about 100 ms late. Patched, `TCP_NODELAY` is set on the end `Curl_wakeup_signal` writes to |
| Upstream status | Not reported |
| Drop at re-pin | When the new curl disables Nagle on its wakeup pair |
| Caught by | No test in this repository: Linux and Android wake with eventfd, Apple with a pipe, and Windows loopback acknowledges at once. A port that uses the TCP pair checks it on its platform (seen on Nintendo Switch: an abort took 95 ms, about 1 ms patched) |

Compiled only where `TCP_NODELAY` is a macro: a platform that declares it as an enumerator has to define it, or curl also leaves Nagle on for every transfer (`CURLOPT_TCP_NODELAY` is guarded the same way in `lib/cf-socket.c`).

## mbedtls-4.1.1-aesce-gcm-4block.patch

| | |
|---|---|
| Dependency | mbedTLS 4.1.1 / TF-PSA-Crypto 1.1.1: `tf-psa-crypto/drivers/builtin/src/aesce.c`, `aesce.h`, `gcm.c` |
| Fixes | On AArch64 with the crypto extensions, GCM runs one AES block and one GHASH reduction per 16 bytes, so a phone core stays near 1 GiB/s on TLS records; patched, every 64 whole bytes go through a four-block AESCE kernel, about four times faster (decisions.md *mbedTLS configuration*) |
| Upstream status | Upstream [PR #702](https://github.com/Mbed-TLS/TF-PSA-Crypto/pull/702) open; not contributed |
| Drop at re-pin | When the new TF-PSA-Crypto has its own multi-block AESCE GCM path (PR #702 or a successor) |
| Caught by | Native test `aes_gcm_known_answers` |

Compiled only for AArch64 with `MBEDTLS_AESCE_C`, `MBEDTLS_BLOCK_CIPHER_C` and no PSA block-cipher driver; Windows ARM64 (AESCE off), armv7 and x86 keep the stock code, as do AAD, partial blocks and the tag. H^2..H^4 are stored in rows of `ctx->H` the AESCE multiply does not read, an assumption a re-pin can break without touching a patched hunk (debt.md *Patched mbedTLS: four-block AESCE GCM*).

## Not a patch: client_config.h

`native/deps/client_config.h` is the mbedTLS and TF-PSA-Crypto user configuration (decisions.md *mbedTLS configuration*), passed to every dependency and to the shim through `native/deps/client_config.cmake`; it changes no upstream source. The `#error` at the top of `native/src/ugo.c` fails the build when the shim does not see it, which would leave the shim with a different layout of mbedTLS structures than the library.

## Re-pin procedure

For each patch of the dependency being bumped:

1. Build: `./scripts/build <target>` applies the patch with `--fuzz=0`.
2. If it fails to apply, check whether upstream fixed the defect (the *Drop at re-pin* row). Fixed: delete the patch, its section here and its sentence in `package/Third Party Notices.md`. Not fixed: rebase it on the new source.
3. If it applies, still check the drop condition: a patch that applies cleanly may be redundant.
4. Rename a kept patch to the new version, update its *Dependency* row in the table above, and run the test in *Caught by* (`./scripts/gate`).
