$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('SCI-publish-tests-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $fixture 'artifacts\publish\sci-hmcl'
$newManaged = @('SolidCraftInstaller.exe', 'SolidCraftInstaller-source.zip', 'LICENSE', 'NOTICE',
    'HMCL-ADDITIONAL-TERMS.md', 'THIRD-PARTY-NOTICES.md')
$newPayload = @('SolidCraftInstaller.exe', 'SolidCraftInstaller-source.zip')
$legacy = @('SCI_HMCL.exe', 'SCI_HMCL-source.zip', 'SCI.exe', 'SolidCraft Installer.exe',
    'SolidCraft Installer-source.zip', 'SolidCraft.Installer.exe')
$oldPresent = @($legacy + @('LICENSE', 'NOTICE', 'HMCL-ADDITIONAL-TERMS.md', 'THIRD-PARTY-NOTICES.md'))
$script:checks = 0
$global:SCI_PublishTest = @{ Publishes = 0; AddUserData = $false; FailInstall = $false }
function Check([bool] $condition, [string] $message) {
    if (-not $condition) { throw "CHECK FAILED: $message" }
    $script:checks++
}

function dotnet {
    $target = $args[[Array]::IndexOf($args, '-o') + 1]
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $target 'SCI.exe'), 'new executable fixture')
    foreach ($name in @('LICENSE', 'NOTICE', 'HMCL-ADDITIONAL-TERMS.md', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $fixture $name) -Destination (Join-Path $target $name)
    }
    if ($global:SCI_PublishTest.AddUserData) {
        New-Item -ItemType Directory -Path (Join-Path $output '.minecraft') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $output '.minecraft\keep.txt'), 'game data fixture')
    }
    $global:SCI_PublishTest.Publishes++
    $global:LASTEXITCODE = 0
}

function Move-Item {
    [CmdletBinding()]
    param([string] $LiteralPath, [string] $Destination, [switch] $Force)
    if ($global:SCI_PublishTest.FailInstall -and $Destination -eq (Join-Path $output 'SolidCraftInstaller-source.zip')) {
        throw 'Injected install move failure'
    }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}

try {
    foreach ($directory in @('scripts', 'tests', 'src\McDownload', 'artifacts\publish\sci-hmcl')) {
        New-Item -ItemType Directory -Path (Join-Path $fixture $directory) -Force | Out-Null
    }
    $publish = Join-Path $fixture 'scripts\Publish.ps1'
    Copy-Item -LiteralPath (Join-Path $repository 'scripts\Publish.ps1') -Destination $publish
    foreach ($name in @('README.md', 'SCI.slnx', 'LICENSE', 'NOTICE', 'HMCL-ADDITIONAL-TERMS.md', 'THIRD-PARTY-NOTICES.md')) {
        [IO.File]::WriteAllText((Join-Path $fixture $name), 'fixture ' + $name)
    }
    foreach ($name in $oldPresent) { [IO.File]::WriteAllText((Join-Path $output $name), 'old fixture ' + $name) }
    $before = @{}
    foreach ($name in $oldPresent) { $before[$name] = (Get-FileHash -LiteralPath (Join-Path $output $name)).Hash }

    $credential = Join-Path $fixture 'src\McDownload\curseforge-key.dpapi'
    [IO.File]::WriteAllText($credential, 'ciphertext fixture only')
    $failed = $false
    try { & $publish | Out-Null } catch { $failed = $_.Exception.Message -match 'Unexpected source archive input' }
    Check ($failed -and $global:SCI_PublishTest.Publishes -eq 0) 'Encrypted credential in source tree must stop packaging before publish.'
    Check ((@($oldPresent | Where-Object { (Get-FileHash -LiteralPath (Join-Path $output $_)).Hash -ne $before[$_] }).Count -eq 0)) 'Credential rejection must preserve the old bundle.'
    Remove-Item -LiteralPath $credential

    $locked = Join-Path $fixture 'src\McDownload\fixture.cs'
    [IO.File]::WriteAllText($locked, 'source fixture')
    $lock = [IO.File]::Open($locked, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $failed = $false
    $failureDetail = ''
    try { & $publish | Out-Null }
    catch { $failed = $_.InvocationInfo.Line -match 'CreateEntryFromFile'; $failureDetail = $_.ToString() + ' ' + $_.InvocationInfo.Line }
    finally { $lock.Dispose() }
    Check ($failed -and $global:SCI_PublishTest.Publishes -eq 1) ('Locked source must fail at source archiving after the mocked publish completes. ' + $failureDetail)
    Check ((@($oldPresent | Where-Object { (Get-FileHash -LiteralPath (Join-Path $output $_)).Hash -ne $before[$_] }).Count -eq 0) -and
        (@($newPayload | Where-Object { Test-Path -LiteralPath (Join-Path $output $_) }).Count -eq 0)) 'Failed source archiving must preserve the old package and install no new artifacts.'
    Check (@(Get-ChildItem -LiteralPath (Split-Path -Parent $output) -Force | Where-Object { $_.Name -like '.sci-hmcl-*' }).Count -eq 0) 'A failed build must clean its isolated staging directory.'

    $global:SCI_PublishTest.FailInstall = $true
    $failed = $false
    try { & $publish | Out-Null } catch { $failed = $_.Exception.Message -match 'Injected install move failure' }
    finally { $global:SCI_PublishTest.FailInstall = $false }
    Check $failed 'The install failure injection must reach the file replacement loop.'
    Check ((@($oldPresent | Where-Object { (Get-FileHash -LiteralPath (Join-Path $output $_)).Hash -ne $before[$_] }).Count -eq 0) -and
        (@($newPayload | Where-Object { Test-Path -LiteralPath (Join-Path $output $_) }).Count -eq 0)) 'An install move failure must roll back every package artifact.'

    Remove-Item -LiteralPath (Join-Path $output 'SCI_HMCL.exe')
    $before.Remove('SCI_HMCL.exe')
    $global:SCI_PublishTest.FailInstall = $true
    $failed = $false
    try { & $publish | Out-Null } catch { $failed = $_.Exception.Message -match 'Injected install move failure' }
    finally { $global:SCI_PublishTest.FailInstall = $false }
    Check ($failed -and -not (Test-Path -LiteralPath (Join-Path $output 'SolidCraftInstaller.exe')) -and
        -not (Test-Path -LiteralPath (Join-Path $output 'SCI_HMCL.exe'))) 'Rollback must remove newly installed files and not recreate a missing old file.'
    Check ((@($before.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $output $_)).Hash -ne $before[$_] }).Count -eq 0) -and
        (@($newPayload | Where-Object { Test-Path -LiteralPath (Join-Path $output $_) }).Count -eq 0)) 'Rollback must restore the other files in an incomplete old package.'
    [IO.File]::WriteAllText((Join-Path $output 'SCI_HMCL.exe'), 'old fixture SCI_HMCL.exe')
    $before['SCI_HMCL.exe'] = (Get-FileHash -LiteralPath (Join-Path $output 'SCI_HMCL.exe')).Hash

    $global:SCI_PublishTest.AddUserData = $true
    $failed = $false
    try { & $publish | Out-Null } catch { $failed = $true }
    finally { $global:SCI_PublishTest.AddUserData = $false }
    Check ($failed -and [IO.File]::ReadAllText((Join-Path $output '.minecraft\keep.txt')) -eq 'game data fixture') 'User data appearing during publish must survive and stop installation.'
    Check ((@($before.Keys | Where-Object { (Get-FileHash -LiteralPath (Join-Path $output $_)).Hash -ne $before[$_] }).Count -eq 0) -and
        (@($newPayload | Where-Object { Test-Path -LiteralPath (Join-Path $output $_) }).Count -eq 0)) 'User data appearing during publish must leave every old package file unchanged.'
    Remove-Item -LiteralPath (Join-Path $output '.minecraft\keep.txt')
    Remove-Item -LiteralPath (Join-Path $output '.minecraft')

    & $publish | Out-Null
    Check ([IO.File]::ReadAllText((Join-Path $output 'SolidCraftInstaller.exe')) -eq 'new executable fixture') 'Successful publication must install the renamed executable.'
    Check (@($legacy | Where-Object { Test-Path -LiteralPath (Join-Path $output $_) }).Count -eq 0) 'Successful publication must remove all legacy executable and archive names.'
    Check (@((Get-ChildItem -LiteralPath $output -File).Name | Where-Object { $_ -notin $newManaged }).Count -eq 0) 'Successful publication must leave only the six new managed package files.'
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'SolidCraftInstaller-source.zip'))
    try {
        Check ($null -ne $archive.GetEntry('scripts/Publish.ps1') -and $null -ne $archive.GetEntry('src/McDownload/fixture.cs')) 'Successful publication must include matching source and build materials.'
        foreach ($name in @('scripts/Publish.ps1', 'src/McDownload/fixture.cs')) {
            $reader = [IO.StreamReader]::new($archive.GetEntry($name).Open())
            try { Check ($reader.ReadToEnd() -eq [IO.File]::ReadAllText((Join-Path $fixture $name))) 'Archived source must match the actual build input.' }
            finally { $reader.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    Check (@(Get-ChildItem -LiteralPath (Split-Path -Parent $output) -Force | Where-Object { $_.Name -like '.sci-hmcl-*' }).Count -eq 0) 'Successful publication must clean staging and rollback directories.'

    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($output.ToUpperInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    $mutex = [Threading.Mutex]::new($false, ('Local\SCI-Publish-' + $key))
    $owned = $mutex.WaitOne(0)
    try {
        $message = ''
        try { $message = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publish 2>&1) -join "`n" }
        catch { $message = $_.ToString() }
        Check ($message -match 'Another publish is already running') 'Concurrent publication must be rejected before touching package files.'
    }
    finally { if ($owned) { $mutex.ReleaseMutex() }; $mutex.Dispose() }

    $game = Join-Path $output '.minecraft'
    New-Item -ItemType Directory -Path $game | Out-Null
    $failed = $false
    try { & $publish | Out-Null } catch { $failed = $true }
    Check ($failed -and (Test-Path -LiteralPath $game)) 'A publish directory containing game data must be left untouched.'
    Write-Output "PASS $script:checks isolated packaging checks; mocked dotnet, no real package or game data touched."
}
finally {
    Remove-Variable -Name SCI_PublishTest -Scope Global
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
