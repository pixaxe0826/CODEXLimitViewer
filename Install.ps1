param(
    [switch]$StartWithWindows,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$executablePath = Join-Path $PSScriptRoot 'dist\CodexQuotaOverlay.exe'
$desktopDirectory = [Environment]::GetFolderPath('Desktop')
$startupDirectory = [Environment]::GetFolderPath('Startup')
$desktopShortcut = Join-Path $desktopDirectory 'Codex 주간 한도.lnk'
$startupShortcut = Join-Path $startupDirectory 'Codex 주간 한도.lnk'

if ($Remove) {
    foreach ($path in @($desktopShortcut, $startupShortcut)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    Write-Host '바로가기를 제거했습니다.'
    exit 0
}

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    & (Join-Path $PSScriptRoot 'Build-Exe.ps1')
}
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "실행 파일을 찾을 수 없습니다: $executablePath"
}

function New-OverlayShortcut {
    param([string]$Path)

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $executablePath
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = Split-Path $executablePath -Parent
    $shortcut.Description = 'Codex 남은 주간 한도 오버레이'
    $shortcut.IconLocation = $executablePath + ',0'
    $shortcut.Save()
}

New-OverlayShortcut -Path $desktopShortcut
if ($StartWithWindows) {
    New-OverlayShortcut -Path $startupShortcut
}

Write-Host "바탕 화면 바로가기를 만들었습니다: $desktopShortcut"
if ($StartWithWindows) {
    Write-Host 'Windows 로그인 시 자동 실행되도록 등록했습니다.'
}
