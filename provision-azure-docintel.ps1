# ============================================================================
# PracticeX - Azure Document Intelligence provisioning (PowerShell)
# ============================================================================
#
# Creates a resource group + Document Intelligence resource in eastus
# (HIPAA-eligible region, S0 tier required for HIPAA workloads). Saves the
# endpoint and key1 to data\azure-docintel-credentials.txt (gitignored).
#
# Idempotent. Re-running just refreshes the credentials file.
#
# Run from project root:
#     .\provision-azure-docintel.ps1
#
# If execution policy blocks it:
#     powershell -ExecutionPolicy Bypass -File .\provision-azure-docintel.ps1
# ============================================================================

# ===== Edit these only if you want different names =====
$RG_NAME       = "rg-practicex-prod"
$LOCATION      = "eastus"
$DOCINTEL_NAME = "practicex-docintel"
# ========================================================

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "=== PracticeX Azure Document Intelligence provisioning ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Resource group:  $RG_NAME"
Write-Host "Location:        $LOCATION"
Write-Host "Resource name:   $DOCINTEL_NAME"
Write-Host ""

# --- Step 0: az cli present? ---
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] Azure CLI not found on PATH." -ForegroundColor Red
    Write-Host "Install from: https://aka.ms/installazurecliwindows"
    Write-Host "Then restart your terminal and re-run this script."
    exit 1
}

# --- Step 1/5: login if needed ---
Write-Host "[1/5] Verifying Azure login..." -ForegroundColor Yellow
$null = & az account show 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Not logged in. Opening browser - sign in as rvadapally@practicex.ai"
    & az login --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] az login failed." -ForegroundColor Red
        exit 1
    }
}

# --- Step 2/5: confirm subscription ---
Write-Host ""
Write-Host "[2/5] Active subscription:" -ForegroundColor Yellow
& az account show --output table
Write-Host ""
Write-Host "If this is the wrong account, press Ctrl+C now and run:"
Write-Host '    az account set --subscription "<subscription-id-or-name>"'
Write-Host ""
Read-Host "Press Enter to continue"

# --- Step 3/5: resource group (idempotent) ---
Write-Host ""
Write-Host "[3/5] Creating resource group $RG_NAME in $LOCATION..." -ForegroundColor Yellow
& az group create --name $RG_NAME --location $LOCATION --output none --only-show-errors
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Failed to create resource group $RG_NAME." -ForegroundColor Red
    exit 1
}
Write-Host "Resource group ready."

# --- Step 4/5: Doc Intel resource (idempotent) ---
Write-Host ""
Write-Host "[4/5] Provisioning Document Intelligence resource $DOCINTEL_NAME..." -ForegroundColor Yellow
$null = & az cognitiveservices account show --name $DOCINTEL_NAME --resource-group $RG_NAME --output none 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Resource does not exist yet. Creating (~30 seconds)..."
    & az cognitiveservices account create `
        --name $DOCINTEL_NAME `
        --resource-group $RG_NAME `
        --kind FormRecognizer `
        --sku S0 `
        --location $LOCATION `
        --custom-domain $DOCINTEL_NAME `
        --yes `
        --output none `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "[ERROR] Doc Intel resource creation failed." -ForegroundColor Red
        Write-Host "Common causes:"
        Write-Host "  - Resource name '$DOCINTEL_NAME' is taken globally."
        Write-Host "    Edit `$DOCINTEL_NAME at the top of this file and re-run."
        Write-Host "  - Subscription is Free Trial. S0 tier requires Pay-As-You-Go."
        Write-Host "    Upgrade at https://portal.azure.com/#blade/Microsoft_Azure_Billing/BillingMenuBlade/Overview"
        Write-Host "  - eastus quota exhausted."
        Write-Host "    Try eastus2 / westus2 / southcentralus (also HIPAA-eligible)."
        exit 1
    }
    Write-Host "Doc Intel resource created."
} else {
    Write-Host "Resource already exists. Fetching keys."
}

# --- Step 5/5: capture endpoint + key1, write to gitignored file ---
Write-Host ""
Write-Host "[5/5] Fetching endpoint + key1..." -ForegroundColor Yellow

$endpoint = & az cognitiveservices account show `
    --name $DOCINTEL_NAME `
    --resource-group $RG_NAME `
    --query "properties.endpoint" `
    --output tsv

$key1 = & az cognitiveservices account keys list `
    --name $DOCINTEL_NAME `
    --resource-group $RG_NAME `
    --query "key1" `
    --output tsv

if (-not $endpoint -or -not $key1) {
    Write-Host "[ERROR] Could not fetch endpoint or key1." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path "data")) {
    New-Item -ItemType Directory -Path "data" | Out-Null
}
$outFile = "data\azure-docintel-credentials.txt"

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$content = @"
PracticeX - Azure Document Intelligence credentials
Generated: $timestamp

Endpoint: $endpoint
Key1:     $key1

Resource group: $RG_NAME
Location:       $LOCATION
Resource:       $DOCINTEL_NAME

To activate locally, run from project root:
  cd src\PracticeX.Api
  dotnet user-secrets set "DocumentIntelligence:Endpoint" "$endpoint"
  dotnet user-secrets set "DocumentIntelligence:ApiKey"   "$key1"
  dotnet user-secrets set "DocumentIntelligence:Enabled"  "true"

Then add the Eagle GI tenant Guid to DocumentIntelligence:AllowedTenantIds.
"@

Set-Content -Path $outFile -Value $content -Encoding UTF8

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host "=== Provisioning complete ===" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Endpoint: $endpoint"
Write-Host "Key1:     $key1"
Write-Host ""
Write-Host "Saved to: $outFile"
Write-Host "(this file is in data\ which is gitignored - never commit)"
Write-Host ""
Write-Host "Next: paste the endpoint + key1 above into chat with Claude"
Write-Host "so the user-secrets get configured and Doc Intel goes live."
Write-Host "============================================================"
Write-Host ""
