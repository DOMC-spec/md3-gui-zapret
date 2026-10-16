# Flowseal 1.10.3 — verified read-only reference

The official immutable release https://github.com/Flowseal/zapret-discord-youtube/releases/tag/1.10.3 resolves to commit `865da4f4c3659523bf79bc6edf0446e7d7969614` (GitHub tag API). Downloaded release ZIP SHA-256 matches GitHub asset metadata: `244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058`. Exact file byte hashes are in [embedded manifest](../../src/Zapret.Core/Compatibility/flowseal-1.10.3.json); all 24 match the adjacent local installation. This proves provenance and source identity, not execution compatibility. No script has been executed.

The exact 22 strategies, service.bat and test tool are retained in [read-only fixtures](../../tests/fixtures/flowseal-1.10.3/); executable binaries are omitted. Release ZIP omits license; `LICENSE.txt` was downloaded from the immutable commit, MIT (bol-van and Flowseal); include it with redistributed fixture scripts. Pin fixture bytes with `.gitattributes -text` to avoid Git newline conversions breaking byte hashes. Do not commit executable/driver binaries as test fixtures: inspection tests may create inert presence-only placeholders and must identify them as such.

## Source action map (service.bat menu lines 68–111)

| Menu | Source label | Meaning / effects |
| --- | --- | --- |
| 1 | service_install | Interactive strategy selection and native service registration, TCP timestamp preparation |
| 2 | service_remove | Broad removal, kills processes by name and stops shared driver; GUI needs restricted ownership-safe adaptation |
| 3 | service_status | Interactive service/process status, pause; GUI uses native read-only service observation |
| 4 | game_switch | Interactive Game Filter mode/port settings |
| 5 | ipset_switch | Mutates ipset-all.txt and .backup |
| 6 | check_updates_switch | Presence flag utils/check_updates.enabled |
| 7 | replace_active_fakes | Copies selected fake payload into bin/ACTIVE_DISCORD_UDP.bin or ACTIVE_GAME_UDP.bin |
| 8 | ipset_update | Downloads and replaces IPSet data |
| 9 | hosts_update | Downloads candidate system hosts entries and requests manual intervention |
| 10 | service_check_updates | Network request / browser / interactive output |
| 11 | service_diagnostics | Broad side effects including TCP settings and possible removal of conflicting services; never use as read-only inspection |
| 12 | run_tests | utils/test zapret.ps1, interactive tests and processes |

Top-level external commands: `status_zapret` observes service BUT also calls `tcp_enable` (lines 5–8), so not safe inspection; `check_updates` uses NO_UPDATE_CHECK and check_updates.enabled; `load_game_filter`; `load_user_lists` creates absent optional user lists. Internal labels are not external CLI operations; do not invoke them with service.bat arguments or automate numeric menu input.

All general*.bat scripts call the preparation actions, change directories and detach winws using `start`. Source presence alone cannot enable GUI service install/manual execution. Later parity, ownership and recovery tasks implement these mutation capabilities. An untouched control script can be reported as source-verified while execution capability remains unavailable with a reason.

## Settings formats (service.bat exact source)

`utils/game_filter.enabled` is optional UTF-8/ASCII key=value lines `mode=disabled|all|tcp|udp`, `tcp=<range>`, `udp=<range>`. Default port ranges are `1024-65535`; disabled protocol output is `12`. Backward bare lines `all`, `tcp`, `udp` are recognized. Absent, empty or unrecognized mode produces disabled (NOT enabled). Keys/modes are case-insensitive. Space is removed from range input; comma-separated integers or ascending `start-end`, integers 1..65535 without leading zeros; invalid candidates fall back to default independently. Last recognized occurrence applies per BAT loops. Do not rely on old PresetParser.ReadGameFilter (empty flag incorrectly enabled); task01 can describe formats without port-semantic reimplementation.

All `lists/*.txt` and optional user files are data, not controlling code; never compare contents against immutable hashes. Optional `list-general-user.txt`, `list-exclude-user.txt`, `ipset-exclude-user.txt` are created by load_user_lists on execution. `ipset-all.txt` / `.backup` store IPSet settings and must not be hash-locked. `utils/check_updates.enabled` is a presence flag. `utils/targets.txt` is input data for tests. `bin/ACTIVE_DISCORD_UDP.bin` and `ACTIVE_GAME_UDP.bin` are writable fake selections, not immutable code. Other shipped DLL/exe/sys/payloads must be present for execution where used; consider immutable code identity separately from data configuration.

## Paths / readiness

Spaces are supported for read-only inspection and folder selection. Upstream diagnoses Cyrillic paths (service.bat 524–530) and OneDrive (534–544); inspecting them is still safe, but execution support cannot be promised without validation. CMD metacharacters, newlines, `%`, `!` and special paths must be rejected for execution before constructing shell text; do not reject safe inspection. Missing service.bat, required bin components, shipped strategy dependencies and inaccessible/missing root get actionable diagnostics. Missing/changed a single strategy must not mislabel every other strategy as changed. Optional tests tool affects tests capability only.

## Implemented readiness boundary (task 01)

GUI calls `IFlowsealAdapter.DescribeAsync(root)` and `ReadStateAsync(root)`. The production adapter uses SHA-256 inspection and native service observation; it never invokes `status_zapret`, `load_user_lists`, BAT, diagnostics, or tests during inspection. The GUI starts with the EXE directory, accepts a native folder selection, preserves dirty lists, and clears the loaded editor when switching roots. Service observation failure is shown as Unknown without hiding the distribution result. Its observed service name is not yet proof of managed-installation ownership (task 02).

The descriptor verifies service.bat and each strategy separately, and checks presence of shipped runtime payloads and static list dependencies. It does not hash user settings or lists. Tool identity affects the Tests capability only. Runtime component presence is not proof of trusted binary identity or execution compatibility. A changed strategy remains visible and does not mark the other original strategies as changed.

Read-state capability remains available with unknown or missing source files. Existing guarded list editing is available for a source-verified service.bat and a lists folder, using HostlistStore's backup and external-edit protection. Script, service, manual-execution, TCP, update and diagnostics actions are unavailable until their parity/ownership/recovery work is implemented and verified. Successful hash checks alone never enable these actions. Cyrillic/non-ASCII, OneDrive and CMD-special-character paths remain readable and carry execution caveats; spaces are accepted for inspection.

Validation is read-only: the core contract uses exact-byte official fixtures and disposable copies, and WPF smoke replaces OS service observation at the GUI boundary. The `--inspect <folder>` core option and `--distribution=<folder>` smoke option read a complete local distribution without running its files. Full Windows execution compatibility remains task 12.