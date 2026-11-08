# Stock Windows PowerShell fixture repair

Checkpoint `0.60-d9ps51`; PROD-QA-03, PROD-QA-04, PROD-QA-08. The shared compatibility-test launcher now sets **only the child process's** PSModulePath to the Modules folder next to the exact Windows PowerShell 5.1 executable it starts. Parent/user environment variables and installed modules are unchanged. All existing script assertions remain.

The existing checkpoint test failed before the repair because Get-FileHash could not be resolved. A new portable regression supplies a synthetic PS7-only Utility manifest requiring PowerShell 7 and an isolated user profile; before the repair it fails with CouldNotAutoloadMatchingModule. After the repair it verifies Windows PowerShell version 5.1, the expected SHA-256 of a temporary fixture, the stock module search directory and absence of the incompatible directory.

Evidence in `artifacts/sessions-tests-0.60-d9ps51/`:

- `ps51-checkpoint-before.trx`: 1 expected failure in the unchanged checkpoint test.
- `ps51-module-before.trx`: 1 expected failure in the new incompatible-module regression.
- `ps51-final.trx`: **5 passed, 0 failed, 0 skipped**, including the existing Luau runner and checkpoint assertions.
- `ps51-before.trx`: discarded test-development attempt with an incorrect assumption about the Utility module's ModuleBase. It is not counted as defect evidence; that assertion was corrected before the regression above was captured.
- `luau-provisioning.json`: local Luau artifact verification and copies into D9 and Completion test-artifact directories.

The existing Luau 0.745 archive matches the prior verification record at `working-copy/artifacts/sessions-owned-bridge-verification-0.57.json`: SHA-256 `5AFE2FD5DB34A045A2E4261EC0EA3144DA6346FF38808726D5AC6E06115081C6`. Each executable was also compared with its archive entry. `luau.exe`: `EE2463CD5F1E9D980FD2600AF9FD58EAAB5E69ABF1C9F318D91D8BDE877D992E`; `luau-compile.exe`: `C6221974A0D8C9567445188095A16FB734C64481B09381BD7A6DAE537F2758EB`. The previously recorded source is the [official 0.745 release](https://github.com/luau-lang/luau/releases/tag/0.745); this cycle reused verified local artifacts rather than downloading replacements.

These are synthetic local script checks. No real account, Roblox client, UAC prompt, native QoS mutation or paid AI test ran. Hosted CI and the full integrated release gate remain separate work.
