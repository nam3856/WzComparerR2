param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projects = @('WzComparerR2', 'WzComparerR2.Avatar', 'WzComparerR2.MapRender', 'WzComparerR2.DB2', 'WzComparerR2.LuaConsole', 'WzComparerR2.Network')
foreach ($project in $projects) {
    & dotnet build (Join-Path $repoRoot "$project\$project.csproj") -c Release -f net8.0-windows --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project ($LASTEXITCODE)" }
}
$executable = Join-Path $repoRoot 'WzComparerR2\bin\Release\net8.0-windows\WzComparerR2.exe'
Write-Output $executable
if ($Launch) { Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) }
