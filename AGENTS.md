# AGENTS.md

UgoHTTP is a lightweight HTTP/WebSocket client for Unity 6000.3+ (IL2CPP and Mono; platforms in `docs/contract.md` *Platforms*) built on a pinned, minimal libcurl behind a thin fixed C ABI. Policy (retries, host failover, deadlines, caching) belongs to the caller, never to the library.

## Rails

Every change is judged against these, in this order:

1. **Light.** Smallest binary, fewest dependencies, least code that meets `docs/contract.md`. Nothing is added "for later".
2. **Direct.** Network code reads as what it does: C# call → `ugo_*` → curl. No interfaces with one implementation, no managers, factories, pipelines, middleware or transport abstractions. A layer exists only if it has two real implementations today (e.g. `platform/` for POSIX and Windows).
3. **Minimal allocations.** Nothing allocated per chunk, per event or per progress update, native or managed. See `docs/architecture.md` *Allocations*.
4. **Multithreaded, no Unity loop.** Network IO, TLS and file writes never run on the Unity main thread. The public API may be called from any thread; events are drained on whichever thread the caller picks. Nothing depends on `Update`, the PlayerLoop or the main thread: the core assembly does not reference `UnityEngine` and works in plain .NET and in the Editor outside Play Mode with nothing pumping. See `docs/architecture.md` *Threading*.

Status: pre-1.0, releases in `package/CHANGELOG.md`. In: HTTP and WebSocket (text and binary), native and C# (awaitable + callback forms), iOS finish-in-background; every platform builds and verifies; Windows x64 and ARM64 also checked by hand in a Unity Editor and player on real Windows (the gate runs them under Wine); IL2CPP players on Android arm64 and the iOS simulator in the gate, WebSocket included in the Unity tests; `scripts/package` assembles the `upm` tree; a 4 h WebSocket soak passed on an iPhone (`tests/ios/run --soak`). Not yet: a CI release job.

Stack: C11 shim (plus one Objective-C file for the iOS background task), CMake + Ninja, mbedTLS 4.1, zlib 1.4, Xcode (Apple), NDK r29, `zig cc` (Linux, Windows x64 and ARM64 via the mingw ABI), Wine in docker for Windows runtime checks; Python fault bench (`uv`); Unity Test Framework in batchmode.

Supported build hosts, tools pinned in `scripts/versions.env`: macOS on Apple silicon (every target; `package` and releases only here) and Windows x64 from Git Bash (Linux, Android and Windows targets; tools from sha256-pinned archives, `WINDOWS_HOST_*`). On Windows the gate runs its own step subset: no `package`, host `native-*`, `android-player` or `ios-player`.

## Commands

| Command | Does |
|---|---|
| `./scripts/bootstrap` | Only setup step, idempotent: Brewfile (Windows: pinned tool archives), NDK, bench venv, container images (test container, Wine runners x64 and arm64), `dotnet restore`; checks every version against `scripts/versions.env`. JSON line, exit 0 = ready |
| `./scripts/build <target>\|all` | Dependencies + shim (`libugo`) into `out/<target>/` and stages shipped plugins into `package/Plugins/`. Slices: `macos-{arm64,x86_64}`, `ios-arm64`, `ios-simulator-{arm64,x86_64}`, `linux-{arm64,x86_64}`, `android-{arm64,armv7,x86_64}`, `windows-{x64,arm64}` (`ugo.dll`); fat targets `macos-universal` (`ugo.bundle`) and `ios-xcframework` build their slices |
| `./scripts/verify [target] [--online]` | Versions, protocols, exports (iOS: no symbol but `ugo_*` visible, links next to a second curl; Windows: PE exports and the pinned import set, probes run under Wine), 16 KB alignment, sizes; `--online` adds TLS checks. JSON, exit code |
| `./scripts/package <dir>` | The `upm` branch tree from `out/`: tracked `package/` files under `package/` (installed with `?path=package`) plus every shipped plugin and its `.meta`; fails on a missing artifact |
| `./scripts/release <X.Y.Z>` | After a full green gate on the clean release commit on `master`, assembles and commits `upm`, creates annotated `vX.Y.Z`, and prints the two push commands; never pushes |
| `scripts/bench` | Fault bench; one JSON `ready` line with ports and test CA |
| `tests/android/run`, `tests/ios/run` | IL2CPP player runs on the own AVD / simulator `ugohttp_player`; one JSON result line. Called by the gate's `android-player` / `ios-player` steps, which build the player first |
| `./scripts/gate [step ...]` | The single done-check. Steps: `versions`, `header`, `abi-layout` (C vs C#), `verify`, `package` (`upm` tree reproducible), `bench`, `container`, `container-tsan`, `native-{asan,tsan}`, `container-native-{asan,tsan}`, `dotnet`, `unity-editmode`, `android-player`, `ios-player` (players built from `tests/player`). One JSON line per step + summary; exit 0 pass, 1 fail, 2 could not run. `--list` names the steps. Never builds or installs |
| `tests/bench/selftest`, `tests/container/selftest`, `tests/container/tsan` | Bench scenarios; privileged network faults in a Linux container; concurrent-TLS TSan run |

## Map

| Path | What |
|---|---|
| `docs/contract.md` | What the library must do: base-spec client behaviour plus the optional features it names. The spec; change it only with the owner |
| `docs/abi.md` | C ABI v2 (`native/include/ugo.h`): lifecycles, threading per function, ownership |
| `docs/architecture.md` | Layers, threading, TLS options, build, verified curl facts, prior art |
| `docs/testing.md` | Test levels, fault scenarios, invariants, CI matrix, benchmark |
| `docs/decisions.md` | Settled decisions, current state only; a change edits its entry in place |
| `docs/debt.md` | Known gaps and unverified behaviour |
| `docs/patches.md` | Patches to pinned dependencies: when one is allowed, each patch with its drop condition and test, the re-pin procedure |
| `README.md` | For package users: install, requirements, short examples. No facts that live in `docs/` |

## Rules

- **Done means `./scripts/gate` is green, not that you say so.** Report its summary line and exit code; "should work" is not a result.
- Do the general behaviour or fail loudly with a classified error. No made-up success, no special-casing a value seen in one test or one game.
- Third-party code is pinned by version and sha256 and built from source. Never copy code from GPL projects (e.g. MajdataPlay); read them for ideas only.
- Nothing internal goes into the repo: production data, consumer names, internal hosts or paths. The repo is meant to become public.
- Notes, investigation reports and logs go into your task reply, not into new files. Gaps go to `docs/debt.md`, settled questions to `docs/decisions.md`. Add no other docs without being asked.
- Comments: English, only the non-obvious why. Default is none.
- All repo text is English.

## Parallel checkouts

- Build output stays inside the checkout; never write to a fixed path outside it. The one exception is the read-only content-addressed cache `~/.cache/ugohttp/`: entries are published atomically once and never modified.
- Test servers bind to port 0 and publish the chosen port; never hardcode one.
- Emulators and simulators are shared: one player run at a time per device.
- Run timing-sensitive steps (`ios-player`, `android-player`, `unity-editmode` stress tests) and the release gate on a quiet host: overload shows up as late iOS expiry warnings, emulator connect timeouts and throughput failures, not library defects.

## Read before

- A gate/verify `docker image missing` or `Wine image missing` error → run `./scripts/bootstrap` first: Docker Desktop Resource Saver can make a just-woken daemon report every image missing, and a fresh worktree lacks the `out/wine` markers.
- Writing native code → `docs/architecture.md` *Portability*: the OS only through `platform/`, no curl or mbedTLS types in the C ABI.
- Touching TLS, trust or the CA bundle → `docs/architecture.md` *TLS and trust*.
