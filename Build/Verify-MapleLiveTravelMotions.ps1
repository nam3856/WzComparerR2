[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$ResultFile,
    [string]$VerificationFile,
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll')
)

# Read-only acceptance checks against existing bundles and original WZ canvases.
# Uses existing reader binaries; no API, Unity Editor, application build or import.
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
$checks = 0; $pngs = 0; $rgbaFrames = 0; $rows = @()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

function Assert-Source([bool]$Condition, [string]$Code) {
    if (!$Condition) { throw $Code }
    $script:checks++
}

function Digest($Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 100 -Compress
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
}

function Resolve-Uol($Node) {
    if (!$Node) { return $null }
    $arguments = [object[]]::new(2)
    $arguments[0] = $Node.PSObject.BaseObject
    $arguments[1] = $find.PSObject.BaseObject
    return $resolve.Invoke($null, $arguments)
}

function Resolve-Canvas($Node) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    while ($true) {
        Assert-Source ($null -ne $Node -and $seen.Add($Node.FullPathToFile)) 'missing-or-cyclic-source-canvas'
        $Node = Resolve-Uol $Node
        $next = [WzComparerR2.Common.Wz_NodeExtension2]::GetLinkedSourceNode($Node, $find, $null)
        if ([object]::ReferenceEquals($Node, $next)) { break }
        $Node = $next
    }
    Assert-Source ($Node.Value -is [WzComparerR2.WzLib.Wz_Png]) 'source-is-not-png'
    return $Node
}

function Verify-RawCanvas($Frame, $Asset, [string]$Directory, $ActionNode) {
    $frameNode = Resolve-Uol ($data.Find($Frame.sourcePath))
    Assert-Source ($null -ne $frameNode) 'source-frame-unavailable'
    $source = Resolve-Canvas $frameNode
    $original = $source.Value.ExtractPng()
    $exported = [Drawing.Bitmap]::new([IO.Path]::Combine($Directory, $Asset.file))
    try {
        Assert-Source ($original.Width -eq $exported.Width -and $original.Height -eq $exported.Height) 'original-canvas-dimensions-changed'
        for ($y = 0; $y -lt $original.Height; $y++) {
            for ($x = 0; $x -lt $original.Width; $x++) {
                if ($original.GetPixel($x, $y).ToArgb() -ne $exported.GetPixel($x, $y).ToArgb()) { throw 'original-rgba-changed' }
            }
        }
        $script:checks++; $script:rgbaFrames++
        $origin = $frameNode.Nodes['origin'].Value
        Assert-Source ($null -ne $origin -and $Frame.originX -eq $origin.X -and $Frame.originY -eq $origin.Y) 'original-origin-changed'
        $delay = if ($frameNode.Nodes['delay']) { [int]$frameNode.Nodes['delay'].Value } elseif ($ActionNode -and $ActionNode.Nodes['delay']) { [int]$ActionNode.Nodes['delay'].Value } else { 120 }
        $delay = if ($delay -eq 0) { 120 } else { [Math]::Abs($delay) }
        Assert-Source ($Frame.delayMs -eq $delay) 'original-source-clock-changed'
    } finally { $original.Dispose(); $exported.Dispose() }
}

try {
    $data = [Activator]::CreateInstance($reader.GetType('DataSource', $true), @([IO.Path]::GetFullPath($BaseWzPath)))
    foreach ($file in $ResultFile) {
        foreach ($result in @(Get-Content -LiteralPath $file -Raw | ConvertFrom-Json)) {
            Assert-Source ($names.Add($result.name)) 'duplicate-character-results'
            $manifest = Get-Content -LiteralPath ([IO.Path]::Combine($result.output, 'wz-unity.json')) -Raw | ConvertFrom-Json
            Assert-Source ($manifest.entities.Count -eq 1 -and $manifest.id -ceq $result.id -and $manifest.entities[0].id -ceq $result.id) 'identity-changed'
            $entity = $manifest.entities[0]
            $assets = @{}
            foreach ($asset in $manifest.assets) {
                $path = [IO.Path]::Combine($result.output, $asset.file)
                Assert-Source ([IO.File]::Exists($path)) 'png-missing'
                Assert-Source ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $asset.id -and $asset.id -ceq $asset.sha256) 'png-checksum-changed'
                Assert-Source (!$assets.ContainsKey($asset.id)) 'duplicate-png-id'
                $assets.Add($asset.id, $asset); $pngs++
            }
            if ([IO.Directory]::Exists($result.input)) {
                $original = Get-Content -LiteralPath ([IO.Path]::Combine($result.input, 'wz-unity.json')) -Raw | ConvertFrom-Json
                $before = $original.entities[0]
                Assert-Source ($before.id -ceq $entity.id -and $before.defaultAction -ceq $entity.defaultAction -and $before.displayName -ceq $entity.displayName) 'existing-actor-identity-or-default-changed'
                Assert-Source ((Digest $before.metadata) -ceq (Digest $entity.metadata)) 'existing-outfit-and-face-metadata-changed'
                Assert-Source ((Digest $before.equipment) -ceq (Digest $entity.equipment)) 'existing-equipment-inventory-changed'
                for ($index = 0; $index -lt $before.clips.Count; $index++) { Assert-Source ((Digest $before.clips[$index]) -ceq (Digest $entity.clips[$index])) 'existing-animation-changed' }
                foreach ($asset in $original.assets) { Assert-Source ($assets.ContainsKey($asset.id)) 'existing-png-removed' }
                $oldMap = [IO.Path]::Combine($result.input, 'face-variant-map.json')
                if ([IO.File]::Exists($oldMap)) { Assert-Source ((Get-FileHash -LiteralPath $oldMap).Hash -ceq (Get-FileHash -LiteralPath ([IO.Path]::Combine($result.output, 'face-variant-map.json'))).Hash) 'existing-face-profile-map-changed' }
            }
            foreach ($action in $result.added) {
                $clips = @($entity.clips | Where-Object name -CEQ $action)
                Assert-Source ($clips.Count -eq 1) 'requested-native-action-missing-or-duplicated'
                $clip = $clips[0]
                $sourceAction = $action -replace '_blink$', ''
                foreach ($track in $clip.tracks) {
                    foreach ($frame in $track.frames) {
                        if (!$frame.visible -or !$frame.assetId) { continue }
                        Assert-Source ($assets.ContainsKey($frame.assetId) -and $null -ne $data.Find($frame.sourcePath)) 'visible-frame-without-original-png-or-source'
                    }
                    foreach ($pose in $track.poses) { if ($pose.visible) { Assert-Source ($assets.ContainsKey($pose.assetId)) 'pose-asset-missing' } }
                }
                if ($action -ceq 'dead') {
                    Assert-Source ($result.name -ceq '깽쿤' -and !$clip.loop -and $clip.tracks.Count -eq 1 -and $clip.tracks[0].frames.Count -eq 1) 'ghost-action-has-human-attachments-or-invalid-clock'
                    $frame = $clip.tracks[0].frames[0]
                    Assert-Source ($frame.sourcePath -ceq 'Character\00002042.img\dead\0\body') 'wrong-original-ghost-body'
                    Verify-RawCanvas $frame $assets[$frame.assetId] $result.output $null
                } elseif ($result.renderingMode -ceq 'illusion-ring') {
                    Assert-Source ($clip.tracks.Count -eq 1 -and $clip.tracks[0].kind -ceq 'body') 'illusion-action-replaced-with-human-layers'
                    $path = 'Character/Ring/01116126.img/' + $sourceAction
                    $originalAction = Resolve-Uol ($data.Find($path))
                    $nativeFrames = @($originalAction.Nodes | Where-Object { $_.Text -match '^\d+$' })
                    Assert-Source ($null -ne $originalAction -and $nativeFrames.Count -eq $clip.tracks[0].frames.Count) 'original-illusion-action-or-frame-count-mismatch'
                    foreach ($frame in $clip.tracks[0].frames) { Verify-RawCanvas $frame $assets[$frame.assetId] $result.output $originalAction }
                } else {
                    $nativeAction = Resolve-Uol ($data.Find($entity.sourcePath + '/' + $sourceAction))
                    $nativeFrames = @($nativeAction.Nodes | Where-Object { $_.Text -match '^\d+$' } | Sort-Object { [int]$_.Text })
                    $clock = @($clip.tracks | Where-Object { $_.id -ceq 'body' -and $_.kind -ceq 'clock' })
                    Assert-Source ($null -ne $nativeAction -and $clock.Count -eq 1 -and $nativeFrames.Count -eq $clock[0].frames.Count) 'original-body-clock-frame-count-mismatch'
                    for ($index = 0; $index -lt $nativeFrames.Count; $index++) {
                        $frameNode = Resolve-Uol $nativeFrames[$index]
                        $delay = if ($frameNode.Nodes['delay']) { [Math]::Abs([int]$frameNode.Nodes['delay'].Value) } else { 120 }
                        if ($delay -eq 0) { $delay = 120 }
                        Assert-Source ($clock[0].frames[$index].delayMs -eq $delay) 'original-body-clock-delay-changed'
                    }
                }
            }
            $rows += [ordered]@{ name = $result.name; id = $result.id; output = $result.output; motions = @($result.added); existingMetadataSha256 = Digest $entity.metadata; pngCount = $manifest.assets.Count; passed = $true }
        }
    }
    $report = [ordered]@{ characterCount = $rows.Count; checks = $checks; pngChecks = $pngs; originalRgbaFrames = $rgbaFrames; results = @($rows) }
    if (![string]::IsNullOrWhiteSpace($VerificationFile)) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($VerificationFile), (ConvertTo-Json -InputObject $report -Depth 7), [Text.UTF8Encoding]::new($false)) }
    Write-Output ('TRAVEL_MOTIONS_VERIFIED characters=' + $rows.Count + ' checks=' + $checks + ' pngs=' + $pngs + ' originalRgbaFrames=' + $rgbaFrames)
} finally { if ($data) { $data.Dispose() } }
