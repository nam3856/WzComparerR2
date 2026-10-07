[CmdletBinding()]
param(
    [string]$OutputDirectory = [IO.Path]::Combine($PSScriptRoot, '../.tmp/main-ship-wheel-20261007'),
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll')
)

# Native WZ inspection and unchanged PNG extraction only. No API, settings,
# application build, Unity launch, scene edits or runtime changes.
$ErrorActionPreference = 'Stop'
$readerPath = [IO.Path]::GetFullPath($ReaderAssemblyPath)
$readerRoot = [IO.Path]::GetDirectoryName($readerPath)
$reader = [Reflection.Assembly]::LoadFrom($readerPath)
$main = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.dll'))
$common = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.Common.dll'))
$plugin = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.PluginBase.dll'))
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$null = $reader.GetType('Smoke', $true).GetMethod('SetDllDirectory', $flags).Invoke($null, @([IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../References/x64'))))
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$resolve = $main.GetType('WzComparerR2.UnityExport.UnityEntityExporter', $true).GetMethod('ResolveUol')
$findMethod = $plugin.GetType('WzComparerR2.PluginBase.PluginManager', $true).GetMethods() | Where-Object { $_.Name -eq 'FindWz' -and $_.GetParameters().Count -eq 2 -and $_.GetParameters()[0].ParameterType -eq [string] } | Select-Object -First 1
$find = $findMethod.CreateDelegate($resolve.GetParameters()[1].ParameterType)
$data = $null
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = [IO.Directory]::CreateDirectory($output)
$rows = @()

function Fields($Node) {
    return @($Node.Nodes | ForEach-Object {
        [ordered]@{ name = $_.Text; value = $(if ($null -eq $_.Value) { $null } elseif ($_.Value -is [string] -or $_.Value.GetType().IsPrimitive) { $_.Value } elseif ($_.Value -is [WzComparerR2.WzLib.Wz_Vector]) { [ordered]@{ x = $_.Value.X; y = $_.Value.Y } } else { $_.Value.GetType().Name }) }
    })
}

try {
    $data = [Activator]::CreateInstance($reader.GetType('DataSource', $true), @([IO.Path]::GetFullPath($BaseWzPath)))
    foreach ($number in @(12,13,15)) {
        $path = 'Map/Obj/vehicle.img/ship/ossyria/' + $number
        $node = $data.Find($path)
        if (!$node) { throw 'Original main ship wheel resource is missing.' }
        $row = [ordered]@{ resource = $path; fields = @(Fields $node); frames = @() }
        foreach ($frame in $node.Nodes | Where-Object { $_.Text -match '^\d+$' }) {
            $arguments = [object[]]::new(2)
            $arguments[0] = $frame.PSObject.BaseObject; $arguments[1] = $find.PSObject.BaseObject
            $raw = $resolve.Invoke($null, $arguments)
            $linked = [WzComparerR2.Common.Wz_NodeExtension2]::GetLinkedSourceNode($raw, $find, $null)
            $bitmap = $linked.Value.ExtractPng()
            $file = 'ossyria-' + $number + '-' + $frame.Text + '.png'
            try {
                $bitmap.Save([IO.Path]::Combine($output, $file), [Drawing.Imaging.ImageFormat]::Png)
                $row.frames += [ordered]@{ index = $frame.Text; source = $raw.FullPathToFile; linked = $linked.FullPathToFile; width = $bitmap.Width; height = $bitmap.Height; file = $file; sha256 = (Get-FileHash -LiteralPath ([IO.Path]::Combine($output, $file)) -Algorithm SHA256).Hash.ToLowerInvariant(); fields = @(Fields $raw) }
            } finally { $bitmap.Dispose() }
        }
        $rows += $row
    }
    $map = $data.Find('Map/Map/Map2/200090010.img')
    if (!$map) { throw 'Original main ship map is missing.' }
    $objects = @()
    foreach ($layer in $map.Nodes | Where-Object { $_.Text -match '^\d+$' }) {
        foreach ($obj in $layer.Nodes['obj'].Nodes) {
            if ([string]$obj.Nodes['oS'].Value -ceq 'vehicle' -and [string]$obj.Nodes['l0'].Value -ceq 'ship' -and [string]$obj.Nodes['l1'].Value -ceq 'ossyria') {
                $objects += [ordered]@{ path = $obj.FullPathToFile; layer = $layer.Text; index = $obj.Text; fields = @(Fields $obj) }
            }
        }
    }
    $report = [ordered]@{ resources = @($rows); mapObjects = @($objects) }
    [IO.File]::WriteAllText([IO.Path]::Combine($output, 'main-ship-wheel.json'), (ConvertTo-Json -InputObject $report -Depth 9), [Text.UTF8Encoding]::new($false))
    $propeller = @($rows | Where-Object resource -match '/15$')[0].frames[0]
    $sourcePlacement = @($objects | Where-Object { @($_.fields | Where-Object { $_.name -ceq 'l2' -and $_.value -ceq '15' }).Count -eq 1 })[0]
    $proof = [ordered]@{ sourcePath = $propeller.source; resolvedCanvasPath = $propeller.linked; mapObjectPath = $sourcePlacement.path; file = $propeller.file; sha256 = $propeller.sha256; width = $propeller.width; height = $propeller.height; origin = @($propeller.fields | Where-Object name -CEQ 'origin')[0].value; moveR = @($propeller.fields | Where-Object name -CEQ 'moveR')[0].value; mapPlacement = $sourcePlacement.fields; note = 'Native source has one complete sprite, centred origin and signed moveR; resource 12 is the engine and 13 is exhaust, not this propeller.' }
    [IO.File]::WriteAllText([IO.Path]::Combine($output, 'main-ship-propeller-source.json'), (ConvertTo-Json -InputObject $proof -Depth 7), [Text.UTF8Encoding]::new($false))
    $proof | ConvertTo-Json -Depth 7 -Compress
} finally { if ($data) { $data.Dispose() } }
