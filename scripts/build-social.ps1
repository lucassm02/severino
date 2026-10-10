# Renders the social images from assets/branding/social/banner.html with headless Edge.
#   ./scripts/build-social.ps1
# Writes site/assets/og-banner.png, site/assets/apple-touch-icon.png and, next to the page,
# github-social-preview.png and post-quadrado.png. Needs the internet for the fonts.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$page = Join-Path $root 'assets/branding/social/banner.html'
$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge não encontrado.' }

$shots = @(
    @{ Variant = 'og';       Width = 1200; Height = 630;  Out = 'site/assets/og-banner.png' },
    @{ Variant = 'icone';    Width = 180;  Height = 180;  Out = 'site/assets/apple-touch-icon.png' },
    @{ Variant = 'github';   Width = 1280; Height = 640;  Out = 'assets/branding/social/github-social-preview.png' },
    @{ Variant = 'quadrado'; Width = 1080; Height = 1080; Out = 'assets/branding/social/post-quadrado.png' }
)

$userData = Join-Path ([IO.Path]::GetTempPath()) "severino-social-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($shot in $shots) {
        $out = Join-Path $root $shot.Out
        Remove-Item $out -ErrorAction SilentlyContinue
        $url = ([Uri]$page).AbsoluteUri + "?v=$($shot.Variant)"
        & $edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
            "--user-data-dir=$userData" --virtual-time-budget=8000 `
            "--window-size=$($shot.Width),$($shot.Height)" "--screenshot=$out" $url 2>$null | Out-Null
        if (-not (Test-Path $out)) { throw "Edge não gerou $($shot.Out)." }
        Write-Host "$($shot.Out) ($($shot.Width)x$($shot.Height))"
    }
}
finally {
    Remove-Item $userData -Recurse -Force -ErrorAction SilentlyContinue
}
