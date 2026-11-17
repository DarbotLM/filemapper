param(
    [Parameter(Mandatory)] [string] $Dotnet,
    [Parameter(Mandatory)] [string] $CliDll,
    [Parameter(Mandatory)] [string] $CaptureRoot,
    [Parameter(Mandatory)] [string] $CaptureCatalog,
    [Parameter(Mandatory)] [string] $InventoryDirectory,
    [Parameter(Mandatory)] [string] $HeadReceipt,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [long] $MinimumFreeBytes = 10GB,
    [long] $MaximumIndexBytes = 2GB,
    [long] $MaximumEditionBytes = 8GB,
    [string] $StopRequestPath,
    [switch] $PlanOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Write-NewJsonAtomic([string]$Path, [object]$Value, [int]$Depth = 10) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.part'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth $Depth), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $Path)
}
function Write-OrVerifyJson([string]$Path, [object]$Value, [int]$Depth = 10) {
    # Requests are evidence too: a resume must not rewrite their byte representation.
    if (Test-Path -LiteralPath $Path) {
        $prior = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($Path))
        $expected = [System.Text.Json.Nodes.JsonNode]::Parse(($Value | ConvertTo-Json -Depth $Depth))
        # The native request contract parses these two fields as DateTimeOffset.
        # Compare their instants without rewriting either retained representation.
        foreach ($section in @('evidence', 'headEvidence')) {
            if ($null -eq $prior[$section] -or $null -eq $expected[$section]) { continue }
            foreach ($document in @($prior, $expected)) {
                $timestamp = $document[$section]['observedAt']
                if ($null -ne $timestamp) {
                    $instant = [DateTimeOffset]::Parse($timestamp.GetValue[string](), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
                    $document[$section]['observedAt'] = [System.Text.Json.Nodes.JsonValue]::Create($instant.ToUniversalTime().ToString('O'))
                }
            }
        }
        if (-not [System.Text.Json.Nodes.JsonNode]::DeepEquals($prior, $expected)) { throw "Retained request differs from selected inputs: $Path" }
    } else { Write-NewJsonAtomic $Path $Value $Depth }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if (-not $StopRequestPath) { $StopRequestPath = Join-Path $OutputDirectory 'stop.request' }
$batchLock = $null
try {
if (-not $PlanOnly) {
    $batchLock = [IO.File]::Open((Join-Path $OutputDirectory 'batch.writer.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
$catalog = Get-Content -LiteralPath $CaptureCatalog -Raw | ConvertFrom-Json
$inventory = Get-Content -LiteralPath (Join-Path $InventoryDirectory 'inventory.json') -Raw | ConvertFrom-Json
if (@($inventory.owners | Where-Object { -not $_.paginationComplete -or $_.error }).Count -ne 0) { throw 'Inventory pagination is incomplete.' }
$heads = Get-Content -LiteralPath $HeadReceipt -Raw | ConvertFrom-Json
$inventorySha = (Get-FileHash -LiteralPath (Join-Path $InventoryDirectory 'inventory.json') -Algorithm SHA256).Hash.ToLowerInvariant()
$headSha = (Get-FileHash -LiteralPath $HeadReceipt -Algorithm SHA256).Hash.ToLowerInvariant()
if ($heads.schema -cne 'filemapper.public-word-library-heads/1' -or $heads.authenticated -ne $false -or $heads.inventorySha256 -cne $inventorySha) { throw 'HEAD receipt is not bound to pinned unauthenticated inventory.' }
$headById = @{}
foreach ($head in $heads.repositories) {
    $key = [string]$head.repositoryId
    if ($headById.ContainsKey($key)) { throw "Duplicate HEAD repository ID $key." }
    $headById[$key] = $head
}
$public = @{}
foreach ($page in Get-ChildItem -LiteralPath $InventoryDirectory -Filter '*-page-*.json') {
    $hash = (Get-FileHash -LiteralPath $page.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($repo in @(Get-Content -LiteralPath $page.FullName -Raw | ConvertFrom-Json)) {
        if ($repo.private -ne $false -or $repo.visibility -ne 'public') { throw 'Inventory page contains non-public entry.' }
        $key = [string]$repo.id
        if ($public.ContainsKey($key)) { throw "Duplicate provider ID $key." }
        $public[$key] = @{ repo = $repo; page = $page.FullName; hash = $hash }
    }
}
$work = @()
foreach ($capture in $catalog.items) {
    $manifestPath = Join-Path $CaptureRoot "captures/$($capture.captureId).json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $key = [string]$manifest.providerStableId
    if ($public.ContainsKey($key) -and $public[$key].repo.html_url -ceq $manifest.canonicalUri -and $headById.ContainsKey($key) -and $headById[$key].state -ceq 'observed' -and $headById[$key].url -ceq $manifest.canonicalUri -and $headById[$key].commit -ceq $manifest.commit) {
        $work += [pscustomobject]@{ capture = $capture; manifest = $manifest; proof = $public[$key]; head = $headById[$key] }
    }
}
$selectedIds = @{}; foreach ($item in $work) { $selectedIds[[string]$item.manifest.providerStableId] = $true }
$missing = @($public.Keys | Where-Object { -not $selectedIds.ContainsKey($_) } | Sort-Object | ForEach-Object { [ordered]@{ providerStableId=$_; fullName=$public[$_].repo.full_name; reason='no-retained-capture-matching-pinned-public-head' } })
$plan = [ordered]@{ editionSchema='filemapper.public-word-library/4'; headReceiptSha256=$headSha; pageSha256=@($public.Values | ForEach-Object { $_.hash } | Sort-Object -Unique); publicEnumerated=$public.Count; publicWithCapture=$selectedIds.Count; publicWithoutCapture=$missing; schema='filemapper.public-word-batch/1'; inventorySha256=(Get-FileHash (Join-Path $InventoryDirectory 'inventory.json') -Algorithm SHA256).Hash.ToLowerInvariant(); catalogSha256=(Get-FileHash $CaptureCatalog -Algorithm SHA256).Hash.ToLowerInvariant(); selected=@($work | ForEach-Object { $_.capture.captureId }); selectedCount=$work.Count; scope='All retained captures matching pinned public inventory; unmatched inventory repositories and historical missing entries remain unprocessed.' }
$planPath = Join-Path $OutputDirectory 'plan.json'
$planJson = $plan | ConvertTo-Json -Depth 10
if (Test-Path -LiteralPath $planPath) {
    $prior = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    if ($prior.headReceiptSha256 -cne $plan.headReceiptSha256 -or $prior.editionSchema -cne $plan.editionSchema -or ($prior.pageSha256 -join ',') -cne ($plan.pageSha256 -join ',') -or $prior.inventorySha256 -cne $plan.inventorySha256 -or $prior.catalogSha256 -cne $plan.catalogSha256) { throw 'Pinned input differs from existing plan.' }
} else { Write-NewJsonAtomic $planPath $plan 10 }
if ($PlanOnly) { Write-Output $planJson; return }
$failures = 0; $completed = 0
foreach ($item in $work) {
    if (Test-Path -LiteralPath $StopRequestPath) {
        Write-Output (ConvertTo-Json -Compress @{phase='stopped';reason='stop-request-file';boundary='between-captures';selected=$work.Count;completed=$completed;failed=$failures})
        return
    }
    $id = $item.capture.captureId; $directory = Join-Path $OutputDirectory $id
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $done = Join-Path $directory 'complete.json'; $edition = Join-Path $directory 'edition.db'
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($OutputDirectory)))
    if ($drive.AvailableFreeSpace -lt $MinimumFreeBytes) { throw "Insufficient free space before $id; safe to resume." }
    Write-Output (ConvertTo-Json -Compress @{phase='starting';captureId=$id;repository=$item.capture.displayName;completed=$completed;failed=$failures})
    try {
    $extractFile = Join-Path $directory 'extraction.json'
    if (Test-Path -LiteralPath $extractFile) {
        try { $null = Get-Content -LiteralPath $extractFile -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch { Move-Item -LiteralPath $extractFile -Destination ($extractFile + '.invalid-' + [Guid]::NewGuid().ToString('N')) }
    }
    if (-not (Test-Path -LiteralPath $extractFile)) {
        $raw = & $Dotnet $CliDll harvest extract $id --workspace $CaptureRoot --defer-index
        if ($LASTEXITCODE -ne 0) { throw "Extraction failed: $id" }
        $extraction = $raw | ConvertFrom-Json
        Write-NewJsonAtomic $extractFile $extraction 30
    } else { $extraction = Get-Content -LiteralPath $extractFile -Raw | ConvertFrom-Json }
    if ($extraction.manifest.captureId -cne $id -or $extraction.extractionId -cnotmatch '^[a-f0-9]{64}$') { throw "Extraction receipt does not match capture: $id" }
    $manifestFile = Join-Path $CaptureRoot "extractions/$($extraction.extractionId).json"
    if ((Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $extraction.extractionId) { throw "Extraction manifest hash differs: $id" }
    $index = Join-Path $directory 'index.db'; $compactInput = Join-Path $directory 'compact-input.json'
    Write-OrVerifyJson $compactInput @{inputs=@(@{captureId=$id; extractionId=$extraction.extractionId});options=@{minimumFreeBytes=$MinimumFreeBytes;maximumDatabaseBytes=$MaximumIndexBytes}} 8
    if (-not (Test-Path -LiteralPath $index)) {
        & $Dotnet $CliDll harvest compact --input $compactInput --destination $index --workspace $CaptureRoot | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Compact index failed: $id" }
    }
    $owner = $item.proof.repo.owner.login
    $headStdoutPath = Join-Path (Split-Path -Parent $HeadReceipt) $item.head.stdout
    $headEvidence = @{receiptSha256=$headSha; stdoutSha256=(Get-FileHash -LiteralPath $headStdoutPath -Algorithm SHA256).Hash.ToLowerInvariant(); observedAt=$item.head.finishedAt}
    $request = @{headEvidence=$headEvidence; headReceiptPath=$HeadReceipt; headStdoutPath=$headStdoutPath; captureRoot=$CaptureRoot; indexPath=$index; compactIndex=$true; destination=$edition; evidenceJsonPath=$item.proof.page; extractionId=$extraction.extractionId; options=@{minimumFreeBytes=$MinimumFreeBytes;maximumEditionBytes=$MaximumEditionBytes}; evidence=@{captureId=$id;providerStableId=[string]$item.manifest.providerStableId;repositoryUrl=$item.manifest.canonicalUri;visibility='public';observedAt=$inventory.observedAt;evidenceUri="https://api.github.com/users/$owner/repos";evidenceSha256=$item.proof.hash}}
    $requestPath = Join-Path $directory 'request.json'; Write-OrVerifyJson $requestPath $request 10
    $indexRaw = & $Dotnet $CliDll word-library verify-index $requestPath
    if ($LASTEXITCODE -ne 0) { throw "Compact index validation failed: $id" }
    $indexReceipt = $indexRaw | ConvertFrom-Json; $indexReceiptPath = Join-Path $directory 'index-receipt.json'
    if (Test-Path -LiteralPath $indexReceiptPath) {
        try { $null = Get-Content -LiteralPath $indexReceiptPath -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch { Move-Item -LiteralPath $indexReceiptPath -Destination ($indexReceiptPath + '.invalid-' + [Guid]::NewGuid().ToString('N')) }
    }
    if (Test-Path -LiteralPath $indexReceiptPath) {
        $priorIndex = Get-Content -LiteralPath $indexReceiptPath -Raw | ConvertFrom-Json
        if ($priorIndex.sha256 -cne $indexReceipt.sha256 -or $priorIndex.captureId -cne $id -or $priorIndex.extractionId -cne $extraction.extractionId) { throw "Compact index differs from its prior receipt: $id" }
    } else { Write-NewJsonAtomic $indexReceiptPath $indexReceipt 5 }
    if (Test-Path -LiteralPath $edition) {
        $raw = & $Dotnet $CliDll word-library verify $requestPath
        if ($LASTEXITCODE -ne 0) { throw "Existing edition input validation failed: $id" }
    } else {
        if (Test-Path -LiteralPath $done) { throw "Receipt exists but edition missing: $id" }
        $raw = & $Dotnet $CliDll word-library publish $requestPath
        if ($LASTEXITCODE -ne 0) { throw "Edition failed: $id" }
    }
    $receipt = @{sha256=(Get-FileHash -LiteralPath $edition -Algorithm SHA256).Hash.ToLowerInvariant(); result=($raw | ConvertFrom-Json)}
    if (Test-Path -LiteralPath $done) {
        $prior = Get-Content -LiteralPath $done -Raw | ConvertFrom-Json
        if ($prior.sha256 -cne $receipt.sha256 -or $prior.result.editionId -cne $receipt.result.editionId) { throw "Edition differs from prior receipt: $id" }
    } else {
        $temporary = $done + '.' + [Guid]::NewGuid().ToString('N') + '.part'
        [IO.File]::WriteAllText($temporary, ($receipt | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $done)
    }
    $completed++
    Write-Output (ConvertTo-Json -Compress @{phase='complete';captureId=$id;completed=$completed;failed=$failures;words=$receipt.result.wordOccurrences})
    } catch {
        $failures++
        $failure = @{captureId=$id;at=[DateTimeOffset]::UtcNow;error=$_.Exception.Message}
        $failurePath = Join-Path $directory ("failure-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmssfff') + ".json")
        [IO.File]::WriteAllText($failurePath, ($failure | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        Write-Output (ConvertTo-Json -Compress @{phase='failed';captureId=$id;completed=$completed;failed=$failures;error=$_.Exception.Message})
    }
}
Write-Output (ConvertTo-Json -Compress @{phase='finished';selected=$work.Count;completed=$completed;failed=$failures;missing=$missing.Count})
if ($failures -gt 0) { exit 1 }
} finally { if ($null -ne $batchLock) { $batchLock.Dispose() } }
