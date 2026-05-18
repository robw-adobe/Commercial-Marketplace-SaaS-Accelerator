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

```powershell
# Install .NET SDK
wget https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh -version 8.0.303
$ENV:PATH="$HOME/.dotnet:$ENV:PATH"
dotnet --version   # should show 8.0.303

# Install EF tools
dotnet tool install --global dotnet-ef --version 8.0.0
$ENV:PATH="$HOME/.dotnet/tools:$ENV:PATH"

# Clone the fix branch from the fork
git clone https://github.com/robw-adobe/Commercial-Marketplace-SaaS-Accelerator.git `
  -b spec/admin-subscription-fetch-resilience --depth 1
cd ./Commercial-Marketplace-SaaS-Accelerator

# Build
dotnet publish ./src/AdminSite/AdminSite.csproj `
  -v q -c release -o ./Publish/AdminSite/

dotnet publish ./src/MeteredTriggerJob/MeteredTriggerJob.csproj `
  -c release `
  -o ./Publish/AdminSite/app_data/jobs/triggered/MeteredTriggerJob/ `
  --runtime win-x64 --self-contained true

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

---

## Phase 6 — Swap staging to production

Once all Phase 5 checks pass, execute the swap. Each swap takes ~30 seconds
with zero downtime — Azure warms up the new code before flipping traffic.

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
| 3 | Add config to staging | 3 min | None |
| 4 | Build and deploy to staging | 10 min | None |
| 5 | Test staging | 15–30 min | None |
| 6 | Swap to production | 1 min | Zero-downtime |
| 7 | Add config to production | 3 min | ~5 sec app restart |
| 8 | Cleanup | 5 min | None |
