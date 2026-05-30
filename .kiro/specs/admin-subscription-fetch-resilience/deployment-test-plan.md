# Deployment & Test Plan — admin-subscription-fetch-resilience

This document describes how to deploy the fix to Azure using a staging slot,
test it without impacting live traffic, swap it to production, and roll back
if needed.

**Prerequisites:** Azure Cloud Shell (PowerShell mode), access to the
`Adobe-Marketplace` resource group, and contributor rights on the
`robw-adobe/Commercial-Marketplace-SaaS-Accelerator` fork.

---

## Phase 0 — Confirm current state

```powershell
# Confirm subscription
az account show --query "{subscription:name, id:id}" -o table

# Confirm both web apps are running
az webapp show --resource-group Adobe-Marketplace --name Adobe-Marketplace-admin `
  --query "{name:name, state:state}" -o table

az webapp show --resource-group Adobe-Marketplace --name Adobe-Marketplace-portal `
  --query "{name:name, state:state}" -o table

# Confirm App Service Plan tier (need Standard for slots)
az appservice plan show `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-asp `
  --query "{name:name, tier:sku.tier, size:sku.name}" -o table
```

Expected tier: `Basic / B1`. If already `Standard`, skip Phase 1.

---

## Phase 1 — Scale up to Standard S1

Deployment slots require Standard tier or above. B1 (Basic) does not support them.
Cost difference is approximately $15/month; can be scaled back down after cleanup.

```powershell
az appservice plan update `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-asp `
  --sku S1

# Verify
az appservice plan show `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-asp `
  --query "{tier:sku.tier, size:sku.name}" -o table
# Expected: Standard / S1
```

---

## Phase 2 — Create staging slots

Creates a live copy of each web app at a separate URL. Inherits production
configuration automatically so no manual reconfiguration is needed.

```powershell
az webapp deployment slot create `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --slot staging `
  --configuration-source Adobe-Marketplace-admin

az webapp deployment slot create `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --slot staging `
  --configuration-source Adobe-Marketplace-portal

# Verify
az webapp deployment slot list `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --query "[].{name:name, state:state}" -o table
```

Staging URLs (production unchanged):

- Admin staging: `https://Adobe-Marketplace-admin-staging.azurewebsites.net`
- Portal staging: `https://Adobe-Marketplace-portal-staging.azurewebsites.net`

---

## Phase 2a — Slot prerequisites (Key Vault access + redirect URIs)

A new staging slot does **not** fully inherit two things from production, and
both cause hard failures (HTTP 500, then a sign-in error) if skipped. Do this
before deploying.

### 2a.1 — Grant the slot's managed identity access to Key Vault

The `DefaultConnection` connection string is a Key Vault reference
(`@Microsoft.KeyVault(VaultName=Adobe-Marketplace-kv;SecretName=DefaultConnection)`).
Key Vault references resolve using the app's **managed identity**, and **each
slot has its own separate identity**. The slot copies the reference *value* but
not vault access, so the reference fails to resolve and the literal
`@Microsoft.KeyVault(...)` string is passed to SqlClient, producing:

```text
ArgumentException: Keyword not supported: '@microsoft.keyvault(vaultname'.
```

Every page 500s because the first DB query throws. Fix: grant each slot's
identity `get`/`list` secret access. Run one line at a time and substitute the
printed principal ID into the `set-policy` command.

Admin slot:

```powershell
az webapp identity assign --resource-group Adobe-Marketplace --name Adobe-Marketplace-admin --slot staging --query principalId -o tsv
```

```powershell
az keyvault set-policy --name Adobe-Marketplace-kv --object-id <admin-staging-principalId> --secret-permissions get list
```

```powershell
az webapp restart --resource-group Adobe-Marketplace --name Adobe-Marketplace-admin --slot staging
```

Portal slot:

```powershell
az webapp identity assign --resource-group Adobe-Marketplace --name Adobe-Marketplace-portal --slot staging --query principalId -o tsv
```

```powershell
az keyvault set-policy --name Adobe-Marketplace-kv --object-id <portal-staging-principalId> --secret-permissions get list
```

```powershell
az webapp restart --resource-group Adobe-Marketplace --name Adobe-Marketplace-portal --slot staging
```

> This vault uses the access-policy model (`enableRbacAuthorization = false`),
> so `set-policy` is correct. If a future vault returns `true` for
> `az keyvault show --name Adobe-Marketplace-kv --query "properties.enableRbacAuthorization" -o tsv`,
> grant the `Key Vault Secrets User` role with `az role assignment create`
> instead.

### 2a.2 — Add the staging redirect URIs to the app registration

Both sites authenticate through one Entra app registration
(`9e7221af-fa46-42d0-b2c7-235ca0a5c06f`, the `MTClientId`). It only lists the
production hostnames, so signing in on a `*-staging` host fails with:

```text
AADSTS50011: The redirect URI '.../Home/Index' specified in the request does
not match the redirect URIs configured for the application.
```

`az ad app update --web-redirect-uris` **overwrites** the entire list, so the
command below repeats all 8 production URIs and appends the 8 staging ones
(4 each for admin and portal, matching the existing base / `/` / `/Home/Index`
/ `/Home/Index/` pattern). Run as a single line:

```powershell
az ad app update --id 9e7221af-fa46-42d0-b2c7-235ca0a5c06f --web-redirect-uris "https://Adobe-Marketplace-admin.azurewebsites.net/Home/Index/" "https://Adobe-Marketplace-admin.azurewebsites.net/Home/Index" "https://Adobe-Marketplace-admin.azurewebsites.net/" "https://Adobe-Marketplace-admin.azurewebsites.net" "https://Adobe-Marketplace-portal.azurewebsites.net/Home/Index/" "https://Adobe-Marketplace-portal.azurewebsites.net/Home/Index" "https://Adobe-Marketplace-portal.azurewebsites.net/" "https://Adobe-Marketplace-portal.azurewebsites.net" "https://Adobe-Marketplace-admin-staging.azurewebsites.net/Home/Index/" "https://Adobe-Marketplace-admin-staging.azurewebsites.net/Home/Index" "https://Adobe-Marketplace-admin-staging.azurewebsites.net/" "https://Adobe-Marketplace-admin-staging.azurewebsites.net" "https://Adobe-Marketplace-portal-staging.azurewebsites.net/Home/Index/" "https://Adobe-Marketplace-portal-staging.azurewebsites.net/Home/Index" "https://Adobe-Marketplace-portal-staging.azurewebsites.net/" "https://Adobe-Marketplace-portal-staging.azurewebsites.net"
```

Verify all 16 are present (no app restart needed — Entra picks up the change
within a minute):

```powershell
az ad app show --id 9e7221af-fa46-42d0-b2c7-235ca0a5c06f --query "web.redirectUris" -o json
```

> Editing this registration requires Application Administrator rights (or
> ownership of the app). If you do not have them, the URI additions must be
> done by someone who does. First confirm the current list before overwriting:
> if production has more or different URIs than the 8 shown here, capture them
> and include them in the command so none are dropped.

---

## Phase 3 — Add resilience config to staging slots

This PR introduces a `MarketplaceResilience` configuration section.
Add it to staging only — production is untouched at this point.

```powershell
# Admin portal staging
az webapp config appsettings set `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --slot staging `
  --settings `
    "MarketplaceResilience__MaxConcurrentPlanFetches=20" `
    "MarketplaceResilience__BackgroundSyncIntervalSeconds=300" `
    "MarketplaceResilience__BackgroundSyncEnabled=true" `
    "MarketplaceResilience__PageSize=100" `
    "MarketplaceResilience__DatabaseQueryTimeoutSeconds=30" `
    "MarketplaceResilience__MaxRetries=3" `
    "MarketplaceResilience__BaseDelaySeconds=1" `
    "MarketplaceResilience__ConsecutiveFailureThreshold=5" `
    "MarketplaceResilience__CooldownSeconds=60"

# Customer portal staging
az webapp config appsettings set `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --slot staging `
  --settings `
    "MarketplaceResilience__MaxConcurrentPlanFetches=20" `
    "MarketplaceResilience__BackgroundSyncIntervalSeconds=300" `
    "MarketplaceResilience__BackgroundSyncEnabled=true" `
    "MarketplaceResilience__PageSize=100" `
    "MarketplaceResilience__DatabaseQueryTimeoutSeconds=30" `
    "MarketplaceResilience__MaxRetries=3" `
    "MarketplaceResilience__BaseDelaySeconds=1" `
    "MarketplaceResilience__ConsecutiveFailureThreshold=5" `
    "MarketplaceResilience__CooldownSeconds=60"
```

---

## Phase 4 — Build and deploy to staging slots

Run in Azure Cloud Shell from the home directory.

### 4a. Install the .NET 8 SDK

**Read these notes before running the commands — they cover the gotchas that
will otherwise cost you time.**

- **Run commands one at a time.** Pasting a multi-line block into Cloud Shell
  merges lines and silently drops commands, especially around inline `#`
  comments. The commands below are deliberately split into single lines with
  no trailing comments. Paste and run each individually.
- **Cloud Shell is Linux even in PowerShell mode.** Use the bash installer with
  the OS and architecture forced (`--os linux --architecture x64`). The
  PowerShell installer (`dotnet-install.ps1`) fails on Linux with
  `Architecture '' not supported`.
- **Install into a dedicated directory (`$HOME/dotnet8`), not `$HOME/.dotnet`.**
  `$HOME/.dotnet` is the .NET CLI home and already contains the system 9.x
  cache files; installing there causes collisions (a stray Windows `dotnet.exe`
  from a failed install, no usable Linux host) that are hard to diagnose.
- **PowerShell caches native-command lookups per session.** After you have run
  bare `dotnet` once (it resolves to the system 9.x host at `/usr/bin/dotnet`),
  appending to `$env:PATH` will not change resolution. Pin the binary with
  `Set-Alias` instead. This is the step that actually makes `dotnet` resolve to
  8.0.303.
- **None of this persists across Cloud Shell sessions.** `$HOME` is on a
  mounted file share so the installed SDK files survive, but `PATH`, the alias,
  and `DOTNET_ROOT` do not. If your session drops, re-run the four export/alias
  lines (4b) before continuing — you do not need to reinstall.
- **The MeteredTriggerJob publish needs `-p:PublishReadyToRun=false` in Cloud
  Shell.** Its project enables ReadyToRun (AOT via crossgen2), which is
  OOM-killed in the memory-constrained Cloud Shell container (exit 137 /
  `NETSDK1096`). The flag is already baked into the build command in 4d.
- The canonical installer URL is `https://dot.net/v1/dotnet-install.sh`. The
  older `dotnet.microsoft.com/.../scripts/v1/...` path no longer resolves.

```powershell
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
```

```powershell
bash dotnet-install.sh --version 8.0.303 --install-dir "$HOME/dotnet8" --os linux --architecture x64
```

### 4b. Point the shell at the new SDK

Run each line separately. The `Set-Alias` line is what defeats PowerShell's
cached lookup of the system `dotnet`.

```powershell
Set-Alias dotnet "$HOME/dotnet8/dotnet" -Scope Global
```

```powershell
$ENV:DOTNET_ROOT="$HOME/dotnet8"
```

```powershell
$ENV:PATH="$HOME/dotnet8:$HOME/dotnet8/tools:$ENV:PATH"
```

```powershell
dotnet --version
```

`dotnet --version` must print `8.0.303`. If it still shows `9.0.313`, confirm
the binary exists with `Test-Path "$HOME/dotnet8/dotnet"` and that the alias is
set with `Get-Alias dotnet`.

### 4c. Install EF tools (optional)

This PR has no schema changes, so the EF migration step is not required (see
the migrations note at the end of this phase). Skip to 4d unless you need
`dotnet-ef` for another reason.

```powershell
dotnet tool install --global dotnet-ef --version 8.0.0
```

```powershell
$ENV:PATH="$HOME/.dotnet/tools:$ENV:PATH"
```

### 4d. Clone, build, and deploy

```powershell
# Clone the fix branch from the fork
git clone https://github.com/robw-adobe/Commercial-Marketplace-SaaS-Accelerator.git `
  -b spec/admin-subscription-fetch-resilience --depth 1
cd ./Commercial-Marketplace-SaaS-Accelerator

# Build
dotnet publish ./src/AdminSite/AdminSite.csproj `
  -v q -c release -o ./Publish/AdminSite/

# MeteredTriggerJob: -p:PublishReadyToRun=false is REQUIRED in Cloud Shell.
# The project sets PublishReadyToRun=true, which runs crossgen2 (AOT). crossgen2
# exceeds the Cloud Shell container memory limit and is OOM-killed (exit code
# 137 / NETSDK1096). R2R is only a startup optimization; disabling it produces
# a functionally identical WebJob. If it is still OOM-killed, also append
# -p:PublishSingleFile=false (Azure runs the resulting .exe either way).
dotnet publish ./src/MeteredTriggerJob/MeteredTriggerJob.csproj `
  -c release `
  -o ./Publish/AdminSite/app_data/jobs/triggered/MeteredTriggerJob/ `
  --runtime win-x64 --self-contained true `
  -p:PublishReadyToRun=false

dotnet publish ./src/CustomerSite/CustomerSite.csproj `
  -v q -c release -o ./Publish/CustomerSite/

# Zip
Compress-Archive -Path ./Publish/AdminSite/* -DestinationPath ./Publish/AdminSite.zip -Force
Compress-Archive -Path ./Publish/CustomerSite/* -DestinationPath ./Publish/CustomerSite.zip -Force

# Deploy to staging slots — production is NOT touched
az webapp deploy `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --slot staging `
  --src-path "./Publish/AdminSite.zip" `
  --type zip

az webapp deploy `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --slot staging `
  --src-path "./Publish/CustomerSite.zip" `
  --type zip

# Clean up
Remove-Item -Path ./Publish -Recurse -Force
```

**Note on database migrations:** This PR contains no schema changes.
The EF migration script step from `Upgrade.ps1` is not required.
The staging slot uses the same production database via the inherited
connection string; the new code is fully backward-compatible.

---

## Phase 5 — Test staging

Open: `https://Adobe-Marketplace-admin-staging.azurewebsites.net`

Work through the checklist in order. Do not proceed to Phase 6 until all
items pass.

### 5a. Basic load

- [ ] Login page appears; AAD authentication completes successfully
- [ ] Subscriptions page loads without error
- [ ] If the DB has subscriptions: table renders with pagination bar and
      rows-per-page selector (25 / 50 / 100 / 200)
- [ ] If the DB is empty: enriched empty-state panel appears with text
      referencing the background sync interval

### 5b. Pagination

- [ ] "Next" and "Previous" navigate between pages correctly
- [ ] Clicking "50" in the rows-per-page selector reloads with 50 rows
- [ ] Clicking "200" reloads with up to 200 rows

### 5c. Fetch All Subscriptions

- [ ] Click "Fetch All Subscriptions" and confirm the dialog
- [ ] Response is 200 OK (no error dialog appears in the browser)
- [ ] Refresh the page — subscriptions are present or count has updated

### 5d. Background sync logs (~5 min after startup)

Azure portal: **Adobe-Marketplace-admin (staging) → Monitoring → Log stream**

Look for these structured JSON entries:

```json
{"event":"background_sync_started","intervalSeconds":300}
{"event":"background_sync_tick_completed","startUtc":"...","durationMs":...,"total":N,"succeeded":N,"failed":0}
```

- [ ] `background_sync_started` entry is present
- [ ] At least one `background_sync_tick_completed` entry appears within 5 minutes

### 5e. Per-subscription operations (preservation check)

- [ ] Open a subscription detail page — loads without error
- [ ] Activate or Deactivate a test subscription — completes successfully
- [ ] Change plan on a test subscription — completes successfully
- [ ] Change quantity on a test subscription — completes successfully

### 5f. Customer portal staging

Open: `https://Adobe-Marketplace-portal-staging.azurewebsites.net`

- [ ] Landing page loads
- [ ] Subscription activation flow works

**If any check fails:** stop here, do not swap. Check the log stream,
fix the issue, re-deploy to the staging slot, and retest.

**Debugging a 500 on staging:** the IIS error page hides the real exception.
To see the actual stack trace, temporarily switch the slot to the developer
exception page, restart, and reload:

```powershell
az webapp config appsettings set --resource-group Adobe-Marketplace --name Adobe-Marketplace-admin --slot staging --settings "ASPNETCORE_ENVIRONMENT=Development" "ASPNETCORE_DETAILEDERRORS=true"
```

```powershell
az webapp restart --resource-group Adobe-Marketplace --name Adobe-Marketplace-admin --slot staging
```

This is safe on staging (no production traffic), but **must be reverted before
the Phase 6 swap** — see Phase 6 step 1. The two most common staging-only
failures (Key Vault reference and redirect URI) are both addressed in Phase 2a.

---

## Phase 6 — Swap staging to production

### 6.1 — Revert any diagnostic settings first

If you set `ASPNETCORE_ENVIRONMENT=Development` while debugging Phase 5, remove
it now. Swapping it into production would expose detailed error pages to live
users. Skip this entirely if you never set it.

```powershell
az webapp config appsettings delete --resource-group Adobe-Marketplace --name Adobe-Marketplace-admin --slot staging --setting-names ASPNETCORE_ENVIRONMENT ASPNETCORE_DETAILEDERRORS
```

Deleting an app setting restarts the slot automatically — no separate restart
command is needed. Wait ~30 seconds, reload the staging URL once to confirm it
still loads cleanly, then swap.

### 6.2 — Swap

Once all Phase 5 checks pass, execute the swap. The swap itself is fast
(~30 seconds) and zero-downtime: Azure applies the target config to the
already-warm staging instances and only flips routing once they respond, so
users never hit a cold start.

```powershell
az webapp deployment slot swap `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --slot staging `
  --target-slot production

az webapp deployment slot swap `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --slot staging `
  --target-slot production
```

After swap:

- `Adobe-Marketplace-admin.azurewebsites.net` → fix is now live
- `Adobe-Marketplace-admin-staging.azurewebsites.net` → old 8.2.1 code
  (available for instant rollback)

Immediately repeat the Phase 5 checklist on the production URL to confirm
live traffic is working correctly.

---

## Phase 7 — Add config to production

The `MarketplaceResilience` settings added to the staging slot in Phase 3
need to be added to production as well. Without them the compiled defaults
in the binary apply (which are correct), but explicit settings are preferred
for operator visibility.

```powershell
az webapp config appsettings set `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --settings `
    "MarketplaceResilience__MaxConcurrentPlanFetches=20" `
    "MarketplaceResilience__BackgroundSyncIntervalSeconds=300" `
    "MarketplaceResilience__BackgroundSyncEnabled=true" `
    "MarketplaceResilience__PageSize=100" `
    "MarketplaceResilience__DatabaseQueryTimeoutSeconds=30" `
    "MarketplaceResilience__MaxRetries=3" `
    "MarketplaceResilience__BaseDelaySeconds=1" `
    "MarketplaceResilience__ConsecutiveFailureThreshold=5" `
    "MarketplaceResilience__CooldownSeconds=60"

az webapp config appsettings set `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --settings `
    "MarketplaceResilience__MaxConcurrentPlanFetches=20" `
    "MarketplaceResilience__BackgroundSyncIntervalSeconds=300" `
    "MarketplaceResilience__BackgroundSyncEnabled=true" `
    "MarketplaceResilience__PageSize=100" `
    "MarketplaceResilience__DatabaseQueryTimeoutSeconds=30" `
    "MarketplaceResilience__MaxRetries=3" `
    "MarketplaceResilience__BaseDelaySeconds=1" `
    "MarketplaceResilience__ConsecutiveFailureThreshold=5" `
    "MarketplaceResilience__CooldownSeconds=60"
```

This triggers a brief app restart (~5 seconds). Adding these settings makes
the configuration explicit and allows tuning without code changes.

---

## Rollback procedure

If a problem is found after the Phase 6 swap, swap back immediately.
The old 8.2.1 code is still running in the staging slot.

```powershell
az webapp deployment slot swap `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --slot staging `
  --target-slot production

az webapp deployment slot swap `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --slot staging `
  --target-slot production
```

30 seconds. No rebuild required. No database changes to undo.

---

## Phase 8 — Cleanup (after confirmed stable in production)

Wait at least 24 hours of clean production operation before cleanup.

```powershell
# Delete staging slots
az webapp deployment slot delete `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-admin `
  --slot staging

az webapp deployment slot delete `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-portal `
  --slot staging

# Optionally scale back to B1 if slots are no longer needed
az appservice plan update `
  --resource-group Adobe-Marketplace `
  --name Adobe-Marketplace-asp `
  --sku B1
```

---

## Timeline summary

| Phase | What | Estimated time | Production impact |
|---|---|---|---|
| 0 | Pre-flight checks | 5 min | None |
| 1 | Scale to S1 | 2 min | None |
| 2 | Create staging slots | 3 min | None |
| 2a | Slot prerequisites (Key Vault + redirect URIs) | 5 min | None |
| 3 | Add config to staging | 3 min | None |
| 4 | Build and deploy to staging | 10 min | None |
| 5 | Test staging | 15–30 min | None |
| 6 | Swap to production | 1 min | Zero-downtime |
| 7 | Add config to production | 3 min | ~5 sec app restart |
| 8 | Cleanup | 5 min | None |
