$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $root 'artifacts\publish'
$output = Join-Path $parent 'sci-hmcl'
$staging = Join-Path $parent ('.sci-hmcl-staging-' + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ('.sci-hmcl-previous-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $root 'src\McDownload\McDownload.csproj'
$newManagedFiles = @('SolidCraftInstaller.exe', 'SolidCraftInstaller-source.zip', 'LICENSE', 'NOTICE',
    'HMCL-ADDITIONAL-TERMS.md', 'THIRD-PARTY-NOTICES.md')
$legacyFiles = @('SCI_HMCL.exe', 'SCI_HMCL-source.zip', 'SCI.exe', 'SolidCraft Installer.exe',
    'SolidCraft Installer-source.zip', 'SolidCraft.Installer.exe')
$managedFiles = @($newManagedFiles + $legacyFiles)

function Assert-PublishDirectory {
    $running = @(Get-Process -Name SCI, SCI_HMCL, 'SolidCraft Installer', SolidCraftInstaller,
        'SolidCraft.Installer' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($running.Count) { throw 'Close SCI in the publish directory before replacing the package.' }
    if (Test-Path -LiteralPath $output) {
        if ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'The publish directory must not be a reparse point.'
        }
        $existing = @(Get-ChildItem -LiteralPath $output -Force)
        if (@($existing | Where-Object { $_.PSIsContainer -or $_.Name -notin $managedFiles -or
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) {
            throw 'The publish directory contains user files or unexpected entries. They will not be replaced.'
        }
    }
}

$sha = [Security.Cryptography.SHA256]::Create()
try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToUpperInvariant()))).Replace('-', '') }
finally { $sha.Dispose() }
$mutex = [Threading.Mutex]::new($false, ('Local\SCI-Publish-' + $key))
$acquired = $false
$keepPrevious = $false
try {
    try { $acquired = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Another publish is already running for this directory.' }
    Assert-PublishDirectory
    $files = @(foreach ($directory in @('src', 'tests', 'scripts')) {
        Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    })
    foreach ($name in @('README.md', 'SCI.slnx', 'LICENSE', 'NOTICE', 'HMCL-ADDITIONAL-TERMS.md',
            'THIRD-PARTY-NOTICES.md', '.gitignore', 'global.json', 'NuGet.Config', 'Directory.Build.props',
            'Directory.Build.targets', '.editorconfig')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { $files += Get-Item -LiteralPath $path }
    }
    foreach ($file in $files) {
        if ($file.Name -match '^\.env($|\.)|\.(pfx|p12|key|dpapi)$' -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected source archive input: $($file.FullName)"
        }
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    Move-Item -LiteralPath (Join-Path $staging 'SCI.exe') -Destination (Join-Path $staging 'SolidCraftInstaller.exe')
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $staging 'SolidCraftInstaller-source.zip'), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in ($files | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
    foreach ($name in $newManagedFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $name) -PathType Leaf)) {
            throw "Incomplete publish bundle: $name"
        }
    }
    if (@(Get-ChildItem -LiteralPath $staging -Force | Where-Object { $_.PSIsContainer -or $_.Name -notin $newManagedFiles }).Count) {
        throw 'Unexpected entries in the staged publish bundle.'
    }
    Assert-PublishDirectory
    New-Item -ItemType Directory -Path $previous | Out-Null
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    foreach ($name in $managedFiles) {
        $path = Join-Path $output $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $previous $name) }
    }
    $changed = [Collections.Generic.List[string]]::new()
    try {
        foreach ($name in $newManagedFiles) {
            $changed.Add($name)
            Move-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $output $name) -Force
        }
        foreach ($name in $legacyFiles) {
            $legacyPath = Join-Path $output $name
            if (Test-Path -LiteralPath $legacyPath) {
                $changed.Add($name)
                Remove-Item -LiteralPath $legacyPath -Force
            }
        }
    }
    catch {
        try {
            foreach ($name in $changed) {
                $saved = Join-Path $previous $name
                $destination = Join-Path $output $name
                if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $destination -Force }
                elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Force }
            }
        }
        catch { $keepPrevious = $true; throw "Package rollback failed. Original files are retained at $previous. $_" }
        throw
    }
}
finally {
    try {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        if (-not $keepPrevious -and (Test-Path -LiteralPath $previous)) { Remove-Item -LiteralPath $previous -Recurse -Force }
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

Get-Item -LiteralPath (Join-Path $output 'SolidCraftInstaller.exe'), (Join-Path $output 'SolidCraftInstaller-source.zip') |
    Select-Object Name, Length
