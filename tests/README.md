# Regression tests

Run from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-failure-recovery-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-lifecycle-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-public-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-standby-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-wake-recovery-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-regression-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-combined-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-phase-e-control-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-zone-layout-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-masked-capture-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-masked-desktop-engine-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-capture-encoding-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-output-gamma-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\run-vivid-color-tests.ps1
```

The public runner covers fresh public defaults, paused first launch, first-device identity selection, reply timing and output diagnostics. The other runners exercise real engine/filter/detector integration, combined transport boundaries, receiver retirement, release retries and ten/eighteen-zone mapping. They use generated capture, injected time and fake/in-memory transports; these suites do not capture the real desktop or control a strip. Settings fixtures are isolated. Raw evidence is written under review-output or temporary test directories, excluded from Git.

The standby runner exercises notification parsing and callback lifetime, automatic off/wake transitions, fresh-image recovery, still pictures, missing captures, manual pause, overlapping blockers, bounded retries, transport identity checks and retained power baselines. Registration, capture and network boundaries are injected; it does not turn off a real monitor or send to a strip.

The wake-recovery runner compiles a selected test entrypoint using the real managed engine and transport with generated images and in-memory sockets. Its 34 cases cover silent command loss, bounded attempts and deadlines, cached-image recovery, renewed blockers, cancellation and endpoint authority. Delivery and firmware behavior are models, not measurements of H6609 hardware. A new output directory retains source hashes, compiler arguments/output, executable hash, test output and traces; `-OutputDirectory` can select a short path if Windows path limits affect evidence writing. It never runs the ordinary application entrypoint.

The lifecycle runner injects worker/cleanup failures and checks queued/bounded power diagnostics and expected-exit policy. Recovery tests cover bounded direct relaunch, deliberate exit suppression and paused recovery. These do not establish long-duration stability or recovery from a force kill or uncatchable native crash.

Additional focused source tests and runners cover network parsing/authorization, crop boundaries, dark-background weighting, fixture copying and logical zone selection. Native encoding tests use generated D3D11/WARP textures; they require the native build toolchain. SilentStartTests.cs is a separate GUI test and is not part of the public no-device runner.

Masked-desktop regressions check that Windows' already-masked pixels remain usable, static cached images stay active, and a pointer-only mask notice preserves a healthy cache without creating a fresh image. Real errors, invalid generations and encoding changes still need fresh pixels. The engine integration uses the real sampler, crop detector and color processor with injected native results, clock and transport; it checks more than thirty seconds of static capture plus screen-off, lock, sleep, disconnected display, manual pause and shutdown. Native tests render generated colored textures with a black protected rectangle; they do not play or capture protected video.

The application also provides --self-test (generated managed/native checks; local report), --capture-test (actual desktop capture), and --diagnose (capture plus read-only LAN discovery). These are optional diagnostics; normal use needs none of them.

Passing deterministic tests is not a physical acceptance result. Mounting, colors, firmware, HDR/game capture, cursor behavior and long-duration stability must be checked on the recipient's PC and strip. No CI or scheduled maintenance is configured.

Output-gamma tests check neutral compatibility, encoded-channel curves and endpoints, concurrent lookup publication, isolated settings migration/serialization, and gamma before engine smoothing and physical zone mapping. The curve calibrates LED appearance; these tests do not establish a particular strip's transfer function or a match to a real display.

Vivid-color tests check optional color preference in mixed areas, unchanged legacy output when off, preserved uniform colors and dark-content coverage, the maximum-channel ceiling, settings compatibility, and the order of preference, gamma, smoothing and zone mapping. They use generated pixels and fake transport; they do not validate physical color matching.
