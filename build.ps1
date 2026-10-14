$ErrorActionPreference = 'Stop'

$command = Get-Command dotnet -ErrorAction SilentlyContinue
if ($command) {
    $dotnet = $command.Source
} else {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw '.NET SDKが見つかりません。先に.NET 10 SDKをインストールしてください。'
}

$project = Join-Path $PSScriptRoot 'src\AppRemote\AppRemote.csproj'
$output = Join-Path $PSScriptRoot 'dist\AppRemote'
& $dotnet publish $project -c Release -r win-x64 --self-contained false -o $output
if ($LASTEXITCODE -ne 0) {
    throw "ビルドに失敗しました (exit code: $LASTEXITCODE)。AppRemoteが起動中なら終了して再実行してください。"
}

Write-Host "完了: $(Join-Path $output 'AppRemote.exe')"
