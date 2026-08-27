#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'Noted.csproj'
$assemblyPath = Join-Path $repoRoot 'obj\Debug\net9.0-windows\Noted.dll'

& dotnet msbuild $projectPath `
    '-target:BuildOnlySettings,PrepareForBuild,ResolveReferences,PrepareResources,Compile' `
    '-property:Configuration=Debug' `
    '-verbosity:minimal'
if ($LASTEXITCODE -ne 0) {
    throw "Noted failed to compile. Exit code: $LASTEXITCODE"
}
if (-not [System.IO.File]::Exists($assemblyPath)) {
    throw "The compiled Noted assembly was not found at '$assemblyPath'."
}
if ([System.IO.File]::GetLastWriteTimeUtc($assemblyPath) -lt
    [System.IO.File]::GetLastWriteTimeUtc((Join-Path $repoRoot 'Services\FileWriter.cs'))) {
    throw "The compiled Noted assembly at '$assemblyPath' is older than FileWriter.cs."
}

$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
$writerType = $assembly.GetType('Noted.Services.FileWriter', $true)
$bindingFlags = [System.Reflection.BindingFlags]::NonPublic -bor
    [System.Reflection.BindingFlags]::Static
$writeMethod = $writerType.GetMethod(
    'WriteAllText',
    $bindingFlags,
    $null,
    [Type[]]@([string], [string], [string]),
    $null)
if ($null -eq $writeMethod) {
    throw 'Noted.Services.FileWriter.WriteAllText(string, string, string) was not found.'
}

$script:failureCount = 0
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())

function Get-RootException {
    param([Parameter(Mandatory)][Exception]$Exception)

    $current = $Exception
    while ($null -ne $current.InnerException -and
           ($current -is [System.Reflection.TargetInvocationException] -or
            $current.GetType().FullName -eq 'System.Management.Automation.MethodInvocationException')) {
        $current = $current.InnerException
    }

    return $current
}

function Invoke-FileWriter {
    param(
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$Content,
        [AllowNull()][object]$BackupPath
    )

    try {
        $result = $writeMethod.Invoke(
            $null,
            [object[]]@($DestinationPath, $Content, $BackupPath))
        return [pscustomobject]@{
            Result = $result
            Exception = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Result = $null
            Exception = Get-RootException -Exception $_.Exception
        }
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        [AllowNull()]$Expected,
        [AllowNull()]$Actual,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not [object]::Equals($Expected, $Actual)) {
        throw "$Message Expected '$Expected', actual '$Actual'."
    }
}

function Assert-NoWriterTempFiles {
    param([Parameter(Mandatory)][string]$DestinationPath)

    $directory = [System.IO.Path]::GetDirectoryName($DestinationPath)
    $fileName = [System.IO.Path]::GetFileName($DestinationPath)
    $temporaryFiles = @(
        [System.IO.Directory]::EnumerateFiles($directory, ".$fileName.*.tmp"))
    Assert-Equal 0 $temporaryFiles.Count 'A FileWriter temporary file was left behind.'
}

function Assert-NoPendingBackupFiles {
    param([Parameter(Mandatory)][string]$BackupPath)

    $directory = [System.IO.Path]::GetDirectoryName($BackupPath)
    $fileName = [System.IO.Path]::GetFileName($BackupPath)
    $pendingFiles = @(
        [System.IO.Directory]::EnumerateFiles($directory, "$fileName.pending-*"))
    Assert-Equal 0 $pendingFiles.Count 'A pending backup file was left behind.'
}

function Invoke-InTemporaryDirectory {
    param([Parameter(Mandatory)][scriptblock]$Action)

    $directory = [System.IO.Path]::GetFullPath(
        (Join-Path $tempRoot ("Noted.FileWriterHarness.{0}" -f [Guid]::NewGuid().ToString('N'))))
    if (-not $directory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The harness temporary directory resolved outside '$tempRoot'."
    }

    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    try {
        & $Action $directory
    }
    finally {
        if ([System.IO.Directory]::Exists($directory)) {
            [System.IO.Directory]::Delete($directory, $true)
        }
    }
}

function Invoke-TestCase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Test
    )

    try {
        Invoke-InTemporaryDirectory $Test
        Write-Host "PASS: $Name"
    }
    catch {
        $script:failureCount++
        Write-Host "FAIL: $Name" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)" -ForegroundColor Red
    }
}

Invoke-TestCase 'creates an initial destination file' {
    param($directory)
    $destination = Join-Path $directory 'initial.txt'

    $invocation = Invoke-FileWriter $destination 'initial content' $null

    Assert-True ($null -eq $invocation.Exception) `
        "The initial write failed: $($invocation.Exception)"
    Assert-True $invocation.Result.FileUpdated `
        'The initial write did not report destination success.'
    Assert-Equal 'initial content' ([System.IO.File]::ReadAllText($destination)) `
        'The initial destination content was incorrect.'
    Assert-NoWriterTempFiles $destination
}

Invoke-TestCase 'replaces a destination file without a backup' {
    param($directory)
    $destination = Join-Path $directory 'replace.txt'
    [System.IO.File]::WriteAllText($destination, 'old content')

    $invocation = Invoke-FileWriter $destination 'new content' $null

    Assert-True ($null -eq $invocation.Exception) `
        "The replacement failed: $($invocation.Exception)"
    Assert-True $invocation.Result.FileUpdated `
        'The replacement did not report destination success.'
    Assert-Equal 'new content' ([System.IO.File]::ReadAllText($destination)) `
        'The destination content was not replaced.'
    Assert-NoWriterTempFiles $destination
}

Invoke-TestCase 'replaces an existing backup with the previous destination file' {
    param($directory)
    $destination = Join-Path $directory 'backed-up.txt'
    $backup = "$destination.bak"
    [System.IO.File]::WriteAllText($destination, 'previous file')
    [System.IO.File]::WriteAllText($backup, 'older backup')

    $invocation = Invoke-FileWriter $destination 'new file' $backup

    Assert-True ($null -eq $invocation.Exception) `
        "The backed-up replacement failed: $($invocation.Exception)"
    Assert-True $invocation.Result.FileUpdated `
        'The backed-up replacement did not report destination success.'
    Assert-True $invocation.Result.BackupUpdated `
        'The backed-up replacement did not report backup success.'
    Assert-Equal 'new file' ([System.IO.File]::ReadAllText($destination)) `
        'The new destination content was incorrect.'
    Assert-Equal 'previous file' ([System.IO.File]::ReadAllText($backup)) `
        'The backup did not preserve the previous destination content.'
    Assert-NoWriterTempFiles $destination
    Assert-NoPendingBackupFiles $backup
}

Invoke-TestCase 'creates a backup when none exists' {
    param($directory)
    $destination = Join-Path $directory 'new-backup.txt'
    $backup = "$destination.bak"
    [System.IO.File]::WriteAllText($destination, 'previous file')

    $invocation = Invoke-FileWriter $destination 'new file' $backup

    Assert-True ($null -eq $invocation.Exception) `
        "The replacement failed: $($invocation.Exception)"
    Assert-True $invocation.Result.BackupUpdated `
        'The replacement did not report backup success.'
    Assert-Equal 'new file' ([System.IO.File]::ReadAllText($destination)) `
        'The new destination content was incorrect.'
    Assert-Equal 'previous file' ([System.IO.File]::ReadAllText($backup)) `
        'The new backup did not preserve the previous destination content.'
    Assert-NoWriterTempFiles $destination
    Assert-NoPendingBackupFiles $backup
}

Invoke-TestCase 'rejects a backup outside the destination directory' {
    param($directory)
    $destination = Join-Path $directory 'validated.txt'
    $otherDirectory = Join-Path $directory 'other'
    [System.IO.Directory]::CreateDirectory($otherDirectory) | Out-Null
    $backup = Join-Path $otherDirectory 'validated.txt.bak'
    [System.IO.File]::WriteAllText($destination, 'unchanged file')

    $invocation = Invoke-FileWriter $destination 'rejected content' $backup

    Assert-True ($invocation.Exception -is [ArgumentException]) `
        "The invalid backup path did not produce ArgumentException: $($invocation.Exception)"
    Assert-Equal 'unchanged file' ([System.IO.File]::ReadAllText($destination)) `
        'Validation failure changed the destination content.'
    Assert-NoWriterTempFiles $destination
}

Invoke-TestCase 'preserves destination and backup when replacement is locked' {
    param($directory)
    $destination = Join-Path $directory 'locked.txt'
    $backup = "$destination.bak"
    [System.IO.File]::WriteAllText($destination, 'locked file')
    [System.IO.File]::WriteAllText($backup, 'known-good backup')
    $lock = [System.IO.File]::Open(
        $destination,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $invocation = Invoke-FileWriter $destination 'uncommitted content' $backup

        Assert-True ($invocation.Exception -is [System.IO.IOException]) `
            "The locked replacement did not produce IOException: $($invocation.Exception)"
        Assert-Equal 'locked file' ([System.IO.File]::ReadAllText($destination)) `
            'A failed replacement changed the destination content.'
        Assert-Equal 'known-good backup' ([System.IO.File]::ReadAllText($backup)) `
            'A failed replacement changed or deleted the existing backup.'
        Assert-NoWriterTempFiles $destination
        Assert-NoPendingBackupFiles $backup
    }
    finally {
        $lock.Dispose()
    }
}

Invoke-TestCase 'preserves a pending backup when backup update is locked' {
    param($directory)
    $destination = Join-Path $directory 'locked-backup.txt'
    $backup = "$destination.bak"
    [System.IO.File]::WriteAllText($destination, 'previous file')
    [System.IO.File]::WriteAllText($backup, 'known-good backup')
    $lock = [System.IO.File]::Open(
        $backup,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $invocation = Invoke-FileWriter $destination 'new file' $backup

        Assert-True ($null -eq $invocation.Exception) `
            "The destination save failed: $($invocation.Exception)"
        Assert-True $invocation.Result.FileUpdated `
            'The save did not report destination success.'
        Assert-True (-not $invocation.Result.BackupUpdated) `
            'The save incorrectly reported backup success.'
        Assert-True (-not [string]::IsNullOrWhiteSpace($invocation.Result.Warning)) `
            'The backup result did not include a warning.'
        Assert-True ([System.IO.File]::Exists($invocation.Result.PreservedBackupPath)) `
            'The immediately previous file was not preserved.'
        Assert-Equal 'new file' ([System.IO.File]::ReadAllText($destination)) `
            'The destination save did not commit the new content.'
        Assert-Equal 'known-good backup' ([System.IO.File]::ReadAllText($backup)) `
            'The failed backup update changed the existing backup.'
        Assert-Equal 'previous file' `
            ([System.IO.File]::ReadAllText($invocation.Result.PreservedBackupPath)) `
            'The pending backup did not contain the previous destination content.'
        Assert-NoWriterTempFiles $destination
    }
    finally {
        $lock.Dispose()
    }
}

Invoke-TestCase 'cleans the temporary file when initial move fails' {
    param($directory)
    $destination = Join-Path $directory 'destination-conflict.txt'
    [System.IO.Directory]::CreateDirectory($destination) | Out-Null

    $invocation = Invoke-FileWriter $destination 'uncommitted content' $null

    Assert-True ($invocation.Exception -is [System.IO.IOException]) `
        "The destination conflict did not produce IOException: $($invocation.Exception)"
    Assert-True ([System.IO.Directory]::Exists($destination)) `
        'The destination conflict directory was changed.'
    Assert-NoWriterTempFiles $destination
}

if ($script:failureCount -ne 0) {
    throw "$script:failureCount FileWriter harness test(s) failed."
}

Write-Host 'All FileWriter harness tests passed.'
