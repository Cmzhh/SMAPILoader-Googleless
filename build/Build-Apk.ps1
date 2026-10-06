param(
    [Parameter(Mandatory)]
    [ValidateSet('Inspect', 'Restore', 'Patch', 'Publish', 'Verify')]
    [string] $Stage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repoRoot
$project = Join-Path $repoRoot 'SMAPIGameLoader/SMAPIGameLoader.csproj'
$evidence = Join-Path $repoRoot 'artifacts/build-evidence'
$apkDirectory = Join-Path $repoRoot 'artifacts/apk'
$selectionFile = Join-Path $evidence 'runtime-selection.json'
$runtimeVersion = '9.0.17'
$runtimeId = 'android-arm64'
$buildProperties = @('-p:Configuration=Release', "-p:RuntimeIdentifier=$runtimeId", "-p:RuntimeFrameworkVersion=$runtimeVersion")
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}

function Resolve-Mono([string] $OutputName) {
    $output = & dotnet msbuild $project @buildProperties -nologo -verbosity:quiet `
        '-t:ProcessFrameworkReferences;ResolveFrameworkReferences;ResolveRuntimePackAssets' `
        -getProperty:NETCoreSdkVersion,NetCoreRoot,NetCoreTargetingPackRoot,RuntimeFrameworkVersion,RuntimeIdentifier,AndroidSdkDirectory `
        -getItem:ResolvedRuntimePack,RuntimePackAsset,RuntimeFramework
    if ($LASTEXITCODE -ne 0) { throw "Runtime resolution failed ($LASTEXITCODE)." }
    $raw = $output -join "`n"
    $raw | Set-Content -LiteralPath (Join-Path $evidence $OutputName) -Encoding utf8
    $jsonStart = $raw.IndexOf('{')
    if ($jsonStart -lt 0) { throw "MSBuild did not return runtime resolution JSON: $raw" }
    $resolved = $raw.Substring($jsonStart) | ConvertFrom-Json
    $packs = @($resolved.Items.ResolvedRuntimePack | Where-Object {
        $_.NuGetPackageId -eq 'Microsoft.NETCore.App.Runtime.Mono.android-arm64' -and
        $_.RuntimeIdentifier -eq $runtimeId
    })
    if ($packs.Count -ne 1) { throw "Expected one ARM64 Mono runtime pack, found $($packs.Count). See $OutputName." }
    $pack = $packs[0]
    if ($pack.NuGetPackageVersion -ne $runtimeVersion) { throw "Unsupported Mono version: $($pack.NuGetPackageVersion)" }
    $library = [IO.Path]::GetFullPath((Join-Path $pack.PackageDirectory "runtimes/$runtimeId/native/libmonosgen-2.0.so"))
    $assets = @($resolved.Items.RuntimePackAsset | Where-Object {
        [IO.Path]::GetFullPath($_.Identity) -eq $library
    })
    if ($assets.Count -ne 1 -or !(Test-Path -LiteralPath $library -PathType Leaf)) {
        throw "Resolved Mono library is missing from the runtime assets: $library"
    }
    Write-Host "Selected SDK: $($resolved.Properties.NETCoreSdkVersion)"
    Write-Host "Selected Mono: $($pack.NuGetPackageId) $($pack.NuGetPackageVersion) ($($pack.RuntimeIdentifier))"
    Write-Host "Selected library: $library"
    return [pscustomobject]@{
        SdkVersion = $resolved.Properties.NETCoreSdkVersion
        DotnetRoot = $resolved.Properties.NetCoreRoot
        AndroidSdkDirectory = $resolved.Properties.AndroidSdkDirectory
        PackageId = $pack.NuGetPackageId
        RuntimeVersion = $pack.NuGetPackageVersion
        RuntimeIdentifier = $pack.RuntimeIdentifier
        Library = $library
        Sha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

Start-Transcript -LiteralPath (Join-Path $evidence "stage-$Stage.txt") | Out-Null
try {
switch ($Stage) {
    'Inspect' {
        Get-Command dotnet | Select-Object Source | Format-List
        & where.exe dotnet
        Invoke-Dotnet @('--info')
        Invoke-Dotnet @('--list-sdks')
        Invoke-Dotnet @('workload', 'list')
        Write-Host "DOTNET_ROOT=$env:DOTNET_ROOT"
        Write-Host "DOTNET_INSTALL_DIR=$env:DOTNET_INSTALL_DIR"
        $roots = @($env:DOTNET_ROOT, $env:DOTNET_INSTALL_DIR, (Split-Path (Get-Command dotnet).Source -Parent)) |
            Where-Object { $_ } | Select-Object -Unique
        $locations = @($roots | ForEach-Object { Join-Path $_ 'packs/Microsoft.NETCore.App.Runtime.Mono.android-arm64' })
        $nugetLocation = & dotnet nuget locals global-packages --list
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve NuGet global-packages directory.' }
        $nugetRoot = ($nugetLocation -join '').Substring('global-packages: '.Length).Trim()
        $locations += Join-Path $nugetRoot 'microsoft.netcore.app.runtime.mono.android-arm64'
        foreach ($location in ($locations | Select-Object -Unique)) {
            Write-Host "Inspecting bounded pack directory: $location"
            if (Test-Path -LiteralPath $location -PathType Container) {
                Get-ChildItem -LiteralPath $location -Recurse -File -Filter libmonosgen-2.0.so |
                    Select-Object FullName, Length | Format-List
            } else { Write-Host 'Directory does not exist.' }
        }
    }
    'Restore' {
        Invoke-Dotnet (@('restore', $project) + $buildProperties)
        $selection = Resolve-Mono 'runtime-restore-resolution.json'
        $selection | ConvertTo-Json | Set-Content -LiteralPath $selectionFile -Encoding utf8
    }
    'Patch' {
        $selection = Get-Content -LiteralPath $selectionFile -Raw | ConvertFrom-Json
        Invoke-Dotnet @('run', '--project', 'LibPatcher', '--configuration', 'Release', '--', 'silent', 'patch-file', $selection.Library)
        $patchedHash = (Get-FileHash -LiteralPath $selection.Library -Algorithm SHA256).Hash.ToLowerInvariant()
        $selection | Add-Member -NotePropertyName PatchedSha256 -NotePropertyValue $patchedHash
        $selection | ConvertTo-Json | Set-Content -LiteralPath $selectionFile -Encoding utf8
        Write-Host "Patched runtime SHA-256: $patchedHash"
    }
    'Publish' {
        $selection = Get-Content -LiteralPath $selectionFile -Raw | ConvertFrom-Json
        if ((Get-FileHash -LiteralPath $selection.Library -Algorithm SHA256).Hash.ToLowerInvariant() -ne $selection.PatchedSha256) {
            throw 'Selected runtime changed after patching.'
        }
        Invoke-Dotnet (@('publish', $project, '--no-restore', '--output', $apkDirectory) + $buildProperties)
        $published = Resolve-Mono 'runtime-publish-resolution.json'
        if ($published.Library -ne $selection.Library -or $published.Sha256 -ne $selection.PatchedSha256) {
            throw 'Publish resolved a different runtime from the patched library.'
        }
    }
    'Verify' {
        $selection = Get-Content -LiteralPath $selectionFile -Raw | ConvertFrom-Json
        $apks = @(Get-ChildItem -LiteralPath $apkDirectory -File -Filter '*-Signed.apk')
        if ($apks.Count -ne 1 -or $apks[0].Length -eq 0) { throw 'Expected exactly one nonempty signed APK.' }
        $apk = $apks[0]
        $sdkRoots = @($selection.AndroidSdkDirectory, $env:ANDROID_HOME, $env:ANDROID_SDK_ROOT) |
            Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) } | Select-Object -Unique
        $tools = @($sdkRoots | ForEach-Object {
            $buildTools = Join-Path $_ 'build-tools'
            if (Test-Path -LiteralPath $buildTools) {
                Get-ChildItem -LiteralPath $buildTools -Directory | Where-Object {
                    $_.Name -match '^\d+\.\d+\.\d+$' -and
                    (Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat')) -and
                    (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe'))
                }
            }
        } | Sort-Object { [version]$_.Name } -Descending)
        if ($tools.Count -eq 0) { throw 'Android apksigner/aapt2 are unavailable; signature verification is required.' }
        $toolDirectory = $tools[0].FullName
        Write-Host "Android verification tools: $toolDirectory"
        & (Join-Path $toolDirectory 'apksigner.bat') verify --verbose --print-certs $apk.FullName 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'apksigner.txt')
        if ($LASTEXITCODE -ne 0) { throw "APK signature verification failed ($LASTEXITCODE)." }
        $badging = & (Join-Path $toolDirectory 'aapt2.exe') dump badging $apk.FullName
        if ($LASTEXITCODE -ne 0) { throw "APK manifest inspection failed ($LASTEXITCODE)." }
        $badging | Tee-Object -FilePath (Join-Path $evidence 'apk-badging.txt')
        $abi = @($badging | Where-Object { $_ -match '^native-code:' }) -join ''
        if ($abi -ne "native-code: 'arm64-v8a'") { throw "Unexpected APK architectures: $abi" }
        $zip = [IO.Compression.ZipFile]::OpenRead($apk.FullName)
        try {
            $entry = $zip.GetEntry('lib/arm64-v8a/libmonosgen-2.0.so')
            if ($null -eq $entry -or $entry.Length -eq 0) { throw 'APK contains no ARM64 Mono library.' }
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $packagedHash = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
                finally { $sha.Dispose() }
            } finally { $stream.Dispose() }
            if ($packagedHash -ne $selection.PatchedSha256) { throw 'Packaged Mono differs from the patched runtime.' }
        } finally { $zip.Dispose() }
        $verification = [pscustomobject]@{
            Commit = $env:GITHUB_SHA
            RunId = $env:GITHUB_RUN_ID
            Apk = $apk.Name
            Bytes = $apk.Length
            Sha256 = (Get-FileHash -LiteralPath $apk.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            SignatureVerified = $true
            NativeCode = $abi
            PackagedMonoSha256 = $packagedHash
            Runtime = $selection
        }
        $verification | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $evidence 'apk-verification.json')
    }
}
} finally {
    Stop-Transcript | Out-Null
}
