# bOps V1.3 PREVIEW-02

Operate this preview under supervision. Verify AI diagnoses against the underlying evidence.
`Completed` records technical completion. Automatic remediation is prohibited. Diagnostic reliability remains an open gate for stable V1.3 under RC-BLOCKER-04.

## Start the preview

1. Verify the ZIP against the SHA-256 supplied by the release operator. Extract it to a fresh installation directory. Use a dedicated, isolated Windows or Linux host with trusted local users and processes. Keep the API on loopback and disconnect reverse proxies and tunnels.
2. Protect the installation directory and its contents from writes or replacement by the bOps process account, including `preview-read-only.yaml`. Use a separate writable data directory. For an upgrade, stop the previous instance and retain the data directory and its existing keys and model settings. Verify that no third-party plugins are enabled and that all configured credentials are limited to viewer and operator roles. The launcher sets those roles for the packaged single credential. Linux requires ICU and an executable `bOps.Api`.
3. Provision `BOPS_API_KEY` and `BOPS_VAULT_MASTER_KEY` in the launcher environment using the operator's secret handling. Run the packaged launcher in the foreground with PowerShell 7.4 or later. Pass the existing data directory as an absolute path.

   ```powershell
   ./Start-BopsPreview.ps1 -DataDirectory $data
   ```

4. Wait for `Preview startup verified` before opening the printed loopback URL or providing access to operators. The launcher verifies the exact approved policy bytes before creating a process. It checks that the host reports `Loaded` for the expected absolute path before the listener appears, then rechecks the hash. An absent, unreadable or different policy, an unexpected load state, or a missing startup message stops the launch. The approved policy grants only Read automatically and forbids Low, Medium, High and Critical. Keep the host isolated through each restart and use the launcher every time.

## Package the approved source

Use a clean checkout of the approved candidate, with LF preserved for the policy file. Use the .NET SDK selected by `global.json`, PowerShell 7.4+ and Node.js 22.23.4. On each matching OS set `$rid` to `win-x64` or `linux-x64` and run the commands below. The script replaces the publish directory.

```powershell
$publish = "artifacts/preview-$rid"
$zip = "artifacts/bops-v1.3-preview-02-$rid.zip"
./scripts/Publish-Bops.ps1 -Runtime $rid -Output $publish
Copy-Item samples/deployment/preview-read-only.yaml,samples/deployment/Start-BopsPreview.ps1 $publish
Copy-Item docs/preview-02.md (Join-Path $publish 'PREVIEW.md')
./scripts/New-ReproducibleZip.ps1 -SourceDirectory $publish -DestinationFile $zip
Get-FileHash -Algorithm SHA256 $zip
```

Record the candidate SHA, toolchain versions and ZIP hashes. Verify repeatability per RID and run the published-artifact smoke and the launcher checks before handing the packages to the release operator.