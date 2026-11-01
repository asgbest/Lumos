# Випуск нової версії Lumos для автооновлення (як tools\Publish-App.ps1 у School_projects):
# 1) піднімає <Version> у Lumos.App.csproj (якщо не -NoBump);
# 2) перевіряє, що в CHANGELOG.md є розділ «## X.Y.Z — дата» цієї версії (пишемо його ДО публікації);
# 3) збирає один exe у publish\Lumos;
# 4) викладає його релізом «Lumos-vX.Y.Z» у репозиторій asgbest/Lumos (з описом із CHANGELOG);
# 5) комітить код (git add -A), ставить тег Lumos-vX.Y.Z і пушить (-NoGit — без цього кроку).
#   & .\tools\Publish-Lumos.ps1 [-NoBump] [-NoGit]
param(
    [switch]$NoBump,
    [switch]$NoGit
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Lumos.App\Lumos.App.csproj'
$changelog = Join-Path $root 'CHANGELOG.md'
$out = Join-Path $root 'publish\Lumos'
$repo = 'asgbest/Lumos' # той самий, що AppUpdate.Repository у програмі

# Токени — лише на ПК (у .gitignore):
#   tools\releases-token.txt   — право створювати релізи (Contents: Read and write) у $repo;
#   Lumos.App\update-token.txt — вбудовується в exe, лише читання (Contents: Read-only): без нього exe не оновлюватиметься.
# Ключ Gemini — Lumos.Core\gemini-key.txt: без нього «✨ AI» у зібраному exe попросить ввести свій ключ.
$tokenFile = Join-Path $PSScriptRoot 'releases-token.txt'
$clientToken = Join-Path $root 'Lumos.App\update-token.txt'
if (-not (Test-Path $tokenFile)) { throw "Немає tools\releases-token.txt — токена GitHub з правом створювати релізи в $repo." }
if (-not (Test-Path $clientToken)) { throw 'Немає Lumos.App\update-token.txt — без нього зібраний Lumos.exe не зможе оновлюватись.' }
if (-not (Test-Path (Join-Path $root 'Lumos.Core\gemini-key.txt'))) { Write-Warning 'Немає Lumos.Core\gemini-key.txt — exe буде без вбудованого ключа Gemini.' }

$git = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $git) { $git = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe' }

$token = (Get-Content $tokenFile -Raw).Trim()
$headers = @{ Authorization = "Bearer $token"; 'User-Agent' = 'Lumos-Publisher'; 'X-GitHub-Api-Version' = '2022-11-28' }

$xml = [IO.File]::ReadAllText($project, [Text.Encoding]::UTF8)
if ($xml -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw 'У Lumos.App.csproj немає <Version>X.Y.Z</Version>' }
$version = if ($NoBump) { "$($Matches[1]).$($Matches[2]).$($Matches[3])" } else { "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)" }

# Опис версії з CHANGELOG — без нього не випускаємо.
$lines = [IO.File]::ReadAllLines($changelog, [Text.Encoding]::UTF8)
$start = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l -match "^##\s+$([regex]::Escape($version))\b" })
if ($start -lt 0) { throw "У CHANGELOG.md немає розділу «## $version — дата». Допишіть, що змінилось, і запустіть знову." }
$notes = New-Object System.Collections.Generic.List[string]
for ($i = $start + 1; $i -lt $lines.Length -and $lines[$i] -notmatch '^##\s'; $i++) { $notes.Add($lines[$i]) }
$body = ($notes -join "`n").Trim()

if (-not $NoBump) {
    $xml = $xml -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$version</Version>"
    [IO.File]::WriteAllText($project, $xml, (New-Object Text.UTF8Encoding $true))
}
Write-Host "Lumos $version"

# Visual Studio відновлює пакети без win-x64 — тоді publish падає з NETSDK1047. Спершу — restore під win-x64.
dotnet restore $project -r win-x64 -v q -nologo
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'Збирання не вдалось' }
$exe = Join-Path $out 'Lumos.exe'
if (-not (Test-Path $exe)) { throw "Після збирання немає $exe" }

$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers `
    -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes((@{ tag_name = "Lumos-v$version"; name = "Lumos $version"; body = $body } | ConvertTo-Json)))
# Свіжозібраний exe якийсь час тримає антивірус («being used by another process») — вантажимо копію.
$upload = Join-Path $env:TEMP "Lumos-$version.exe"
Copy-Item $exe $upload -Force
try {
    Invoke-RestMethod -Method Post -Uri "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=Lumos.exe" `
        -Headers $headers -ContentType 'application/octet-stream' -InFile $upload -TimeoutSec 1800 | Out-Null
}
finally { Remove-Item $upload -ErrorAction SilentlyContinue }
Write-Host "Опубліковано Lumos $version — програма оновиться під час наступного запуску."

if ($NoGit) { return }
# Код цієї версії — комітом з тегом. Секрети (токени, ключ Gemini) — у .gitignore.
& $git -C $root fetch -q origin
$behind = (& $git -C $root rev-list --count 'HEAD..@{u}').Trim()
if ($behind -ne '0') { Write-Warning "У origin є нові коміти ($behind) — git пропущено, злийте їх і закомітьте вручну."; return }
& $git -C $root add -A
# Нічого не змінилось (-NoBump без нових правок) — лише тег на поточний коміт.
if (& $git -C $root status --porcelain) {
    # Опис — файлом: лапки й «|» з історії версій Windows PowerShell ламає в аргументах git.
    $messageFile = Join-Path $env:TEMP 'publish-lumos-commit.txt'
    [IO.File]::WriteAllText($messageFile, "Lumos $version`n`n$body`n", (New-Object System.Text.UTF8Encoding($false)))
    & $git -C $root commit -q -F $messageFile
    if ($LASTEXITCODE -ne 0) { throw 'git commit не вдався' }
}
& $git -C $root tag "Lumos-v$version"
& $git -C $root push -q origin HEAD --tags
if ($LASTEXITCODE -ne 0) { throw 'git push не вдався' }
Write-Host "Код закомічено й запушено з тегом Lumos-v$version."
