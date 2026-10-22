# Installs laraue-boards-agent (and its short name, lba) on Windows and runs `lba setup`; when it is already set up,
# updates it and restarts the background task instead, without questions. In PowerShell:
#   irm https://raw.githubusercontent.com/Laraue/boards-agent/main/install.ps1 | iex
# Optional environment variables:
#   BOARDS_AGENT_VERSION      a release tag, e.g. v0.2.0 (default: the newest release)
#   BOARDS_AGENT_RELEASE_URL  where the release files are downloaded from (default: the GitHub release)
#   BOARDS_AGENT_NO_SETUP=1   install only, don't run setup
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = 'Laraue/boards-agent'
$tag = $env:BOARDS_AGENT_VERSION
if (-not $tag) {
    # The newest release, pre-releases included (GitHub's "latest" leaves them out).
    $tag = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases?per_page=1")[0].tag_name
}

# Only an x64 build is published; Windows on ARM runs it under emulation.
$asset = "laraue-boards-agent-$tag-win-x64.zip"
$base = if ($env:BOARDS_AGENT_RELEASE_URL) { $env:BOARDS_AGENT_RELEASE_URL } else { "https://github.com/$repo/releases/download/$tag" }
$dir = Join-Path $env:LOCALAPPDATA 'Programs\boards-agent'
$exe = Join-Path $dir 'laraue-boards-agent.exe'

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("boards-agent-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading laraue-boards-agent $tag (win-x64)..."
    Invoke-WebRequest "$base/$asset" -OutFile (Join-Path $tmp $asset) -UseBasicParsing
    Invoke-WebRequest "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS') -UseBasicParsing

    $expected = Get-Content (Join-Path $tmp 'SHA256SUMS') |
        ForEach-Object { $parts = $_ -split '\s+'; if ($parts[1] -eq $asset) { $parts[0] } } |
        Select-Object -First 1
    $actual = (Get-FileHash (Join-Path $tmp $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) {
        throw "The checksum of $asset doesn't match SHA256SUMS."
    }

    Expand-Archive (Join-Path $tmp $asset) -DestinationPath $tmp
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # A running background task holds the binary open: stop it while it's replaced (setup starts it again).
    & schtasks.exe /End /TN boards-agent *> $null
    Start-Sleep -Seconds 1
    Copy-Item (Join-Path $tmp 'laraue-boards-agent\laraue-boards-agent.exe') $exe -Force
    # lba: the short name. boards-agent: the name before the rename - its old binary becomes a shim to the new one.
    Set-Content -Path (Join-Path $dir 'lba.cmd') -Value '@"%~dp0laraue-boards-agent.exe" %*' -Encoding ASCII
    $oldExe = Join-Path $dir 'boards-agent.exe'
    if (Test-Path $oldExe) {
        Remove-Item $oldExe -Force
        Set-Content -Path (Join-Path $dir 'boards-agent.cmd') -Value '@"%~dp0laraue-boards-agent.exe" %*' -Encoding ASCII
    }
    Write-Host "Installed $exe (also as lba)."
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# Put the folder on the user's PATH for new terminals, and this one.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($userPath -split ';') -contains $dir)) {
    $newPath = if ($userPath) { $userPath.TrimEnd(';') + ';' + $dir } else { $dir }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Write-Host "Added $dir to your PATH (new terminals)."
}
if (-not (($env:Path -split ';') -contains $dir)) {
    $env:Path = $env:Path.TrimEnd(';') + ';' + $dir
}

if ($env:BOARDS_AGENT_NO_SETUP -ne '1') {
    # Already set up: an update only restarts the background task (stopped above for the copy) - no questions.
    $config = Join-Path $env:APPDATA 'boards-agent\config.json'
    if (Test-Path $config) {
        & $exe service restart
        Write-Host "Updated to $(& $exe --version). Run ``lba setup`` to change its settings."
    }
    else {
        & $exe setup
    }
}
