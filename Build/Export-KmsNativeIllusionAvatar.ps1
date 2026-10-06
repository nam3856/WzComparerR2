[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$NativeBundleDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll')
)

# Uses only existing binaries and previously exported public outfit metadata.
# No build, API/settings access or Unity Assets writes. Every PNG is decoded from
# the equipped original ring canvas, with its original origin and transparency.
$ErrorActionPreference = 'Stop'
$readerPath = [IO.Path]::GetFullPath($ReaderAssemblyPath)
$readerRoot = [IO.Path]::GetDirectoryName($readerPath)
$outputRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
if ([IO.Directory]::Exists($outputRoot)) { throw 'Use a fresh illusion export directory.' }
$reader = [Reflection.Assembly]::LoadFrom($readerPath)
$main = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.dll'))
$plugin = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.PluginBase.dll'))
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$null = $reader.GetType('Smoke', $true).GetMethod('SetDllDirectory', $flags).Invoke($null, @([IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '../References/x64'))))
$readTrack = $main.GetType('WzComparerR2.UnityExport.UnityEntityExporter', $true).GetMethod('ReadTrack')
$resolveUol = $main.GetType('WzComparerR2.UnityExport.UnityEntityExporter', $true).GetMethod('ResolveUol')
$writerType = $main.GetType('WzComparerR2.UnityExport.UnityExportWriter', $true)
$manifestType = $writerType.GetProperty('Manifest').PropertyType
$findMethod = $plugin.GetType('WzComparerR2.PluginBase.PluginManager', $true).GetMethods() | Where-Object {
    $_.Name -eq 'FindWz' -and $_.GetParameters().Count -eq 2 -and $_.GetParameters()[0].ParameterType -eq [string]
} | Select-Object -First 1
$find = $findMethod.CreateDelegate($readTrack.GetParameters()[3].ParameterType)
$data = $null
$writer = $null
$results = @()

function Add-Meta($List, [string]$Key, [string]$Value) {
    $entry = [Activator]::CreateInstance($List.GetType().GetGenericArguments()[0])
    $entry.key = $Key; $entry.value = $Value; $List.Add($entry)
}

function Get-PixelHash($Bitmap) {
    $bytes = [byte[]]::new($Bitmap.Width * $Bitmap.Height * 4)
    $offset = 0
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            $color = $Bitmap.GetPixel($x, $y)
            $bytes[$offset++] = $color.R; $bytes[$offset++] = $color.G
            $bytes[$offset++] = $color.B; $bytes[$offset++] = $color.A
        }
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-AlphaBounds($Bitmap) {
    $left = $Bitmap.Width; $top = $Bitmap.Height; $right = -1; $bottom = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -eq 0) { continue }
            $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y)
            $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y)
        }
    }
    if ($right -lt $left) { throw 'Original ring frame is fully transparent.' }
    return [ordered]@{ x = $left; y = $top; width = $right - $left + 1; height = $bottom - $top + 1 }
}

try {
    $data = [Activator]::CreateInstance($reader.GetType('DataSource', $true), @([IO.Path]::GetFullPath($BaseWzPath)))
    foreach ($inputDirectory in $NativeBundleDirectory) {
        $input = [IO.Path]::GetFullPath($inputDirectory)
        $original = [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText([IO.Path]::Combine($input, 'wz-unity.json')), $manifestType)
        if ($original.entities.Count -ne 1 -or !$original.entities[0].hasEquipmentMetadata) { throw 'Original outfit inventory is required.' }
        $entity = $original.entities[0]
        $rings = @($entity.equipment | Where-Object { !$_.isSkill -and $_.illusionRingClassificationKnown -and $_.isIllusionRing })
        if ($rings.Count -ne 1) { throw 'Exactly one equipped verified illusion ring is required; no priority is guessed.' }
        $ring = $rings[0]
        $id = 0
        if (![int]::TryParse($ring.itemId, [ref]$id) -or [Math]::Floor($id / 10000.0) -ne 111 -or $ring.slotIndex -lt 25 -or $ring.slotIndex -gt 28) { throw 'Original ring identity/slot mismatch.' }
        $node = $data.Find($ring.sourcePath)
        $expectedSource = 'Character\Ring\' + $id.ToString('D8', [Globalization.CultureInfo]::InvariantCulture) + '.img'
        if (!$node -or !$node.FullPathToFile.Equals($expectedSource, [StringComparison]::OrdinalIgnoreCase)) { throw 'Original ring source path does not match its equipped item ID.' }
        $grade = $resolveUol.Invoke($null, @($node.FindNodeByPath('info\illusionGrade'), $find))
        $gradeNumber = 0
        if (!$grade -or $null -eq $grade.Value -or ![int]::TryParse([string]$grade.Value, [ref]$gradeNumber) -or $gradeNumber -lt 0) { throw 'Original illusion marker is unavailable.' }
        if ([string]::IsNullOrWhiteSpace($entity.displayName) -or $entity.displayName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid character output directory name.' }
        if ([string]::IsNullOrWhiteSpace($entity.id) -or $entity.id -in @('.', '..') -or $entity.id.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid original entity directory identity.' }
        $characterRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($outputRoot, $entity.displayName))
        if (![IO.Path]::GetDirectoryName($characterRoot).Equals($outputRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Character output must remain inside the destination.' }
        $output = [IO.Path]::GetFullPath([IO.Path]::Combine($outputRoot, $entity.displayName, $entity.id + '-illusion-' + $ring.itemId))
        if (![IO.Path]::GetDirectoryName($output).Equals($characterRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Entity output must remain inside its character folder.' }
        $entity.id += '-illusion-' + $ring.itemId
        $entity.sourcePath = $node.FullPathToFile
        $entity.defaultAction = 'stand1'
        $entity.clips.Clear()
        Add-Meta $entity.metadata 'rendering/mode' 'illusion-ring'
        Add-Meta $entity.metadata 'rendering/illusionRingItemId' $ring.itemId
        Add-Meta $entity.metadata 'rendering/illusionRingSlot' ([string]$ring.slotIndex)
        Add-Meta $entity.metadata 'rendering/illusionRingSource' $node.FullPathToFile
        Add-Meta $entity.metadata 'rendering/sourceActionAliases' $(if (!$node.Nodes['stand2']) { 'stand2=stand1' } else { '' })
        Add-Meta $entity.metadata 'rendering/underlyingAvatarBundle' $input.Replace('\', '/')
        Add-Meta $entity.metadata 'rendering/faceVariants' 'none; the original ring supplies a complete body sprite'
        $writer = [Activator]::CreateInstance($writerType, @($output, $entity.id, 'avatar', $entity.sourcePath, [Threading.CancellationToken]::None))
        $geometry = @()
        foreach ($action in @('stand1', 'stand2', 'sit', 'prone')) {
            $sourceAction = if ($action -eq 'stand2' -and !$node.Nodes['stand2']) { 'stand1' } else { $action }
            $actionNode = $resolveUol.Invoke($null, @($node.Nodes[$sourceAction], $find))
            if (!$actionNode) { throw 'Required original illusion action is unavailable.' }
            $repeat = $resolveUol.Invoke($null, @($actionNode.Nodes['repeat'], $find))
            $repeatValue = 0
            if ($repeat -and ($null -eq $repeat.Value -or ![int]::TryParse([string]$repeat.Value, [ref]$repeatValue))) { throw 'Malformed original repeat value.' }
            $loop = !$repeat -or $repeatValue -ne 0
            $track = $readTrack.Invoke($null, @($actionNode, ('illusion-ring/' + $ring.slotIndex), $writer, $find, $loop))
            if ($track.frames.Count -eq 0) { throw 'Original illusion action contains no frames.' }
            $track.kind = 'body'; $track.slot = $ring.slot; $track.itemId = $ring.itemId
            Add-Meta $track.metadata 'sourceAction' $sourceAction
            Add-Meta $track.metadata 'source' $actionNode.FullPathToFile
            for ($index = 0; $index -lt $track.frames.Count; $index++) {
                $frame = $track.frames[$index]
                $source = $resolveUol.Invoke($null, @($data.Find($frame.sourcePath), $find))
                if (!$source) { throw 'Original frame provenance unavailable.' }
                # Ring actions author their clock on the parent. Frame delay wins
                # when present; a missing clock retains the native 120ms default.
                if (!$source.Nodes['delay'] -and $actionNode.Nodes['delay']) {
                    $parentDelay = $resolveUol.Invoke($null, @($actionNode.Nodes['delay'], $find))
                    $delay = 0
                    if (!$parentDelay -or $null -eq $parentDelay.Value -or ![int]::TryParse([string]$parentDelay.Value, [ref]$delay)) { throw 'Malformed original action delay.' }
                    if ($delay -eq 0) { $delay = 120; $writer.Warn($actionNode.FullPathToFile, '0ms illusion action delay normalized to 120ms.') }
                    $frame.delayMs = [Math]::Abs([double]$delay)
                }
                Add-Meta $track.metadata ('frame/' + $index + '/source') $frame.sourcePath
                $resolved = $source
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                while ($true) {
                    if (!$resolved -or !$seen.Add($resolved.FullPathToFile)) { throw 'Missing/cyclic original ring canvas.' }
                    if ($resolved.Value -is [WzComparerR2.WzLib.Wz_Uol]) { $resolved = $resolved.Value.HandleUol($resolved); continue }
                    $next = [WzComparerR2.Common.Wz_NodeExtension2]::GetLinkedSourceNode($resolved, $find, $null)
                    if ([object]::ReferenceEquals($resolved, $next)) { break }
                    $resolved = $next
                }
                $asset = $writer.Manifest.assets | Where-Object id -eq $frame.assetId | Select-Object -First 1
                $originalBitmap = $resolved.Value.ExtractPng()
                $exportedBitmap = [Drawing.Bitmap]::new([IO.Path]::Combine($writer.StagingDirectory, $asset.file))
                try {
                    if ($originalBitmap.Width -ne $exportedBitmap.Width -or $originalBitmap.Height -ne $exportedBitmap.Height -or (Get-PixelHash $originalBitmap) -ne (Get-PixelHash $exportedBitmap)) { throw 'Original RGBA changed during ring export.' }
                    $bounds = Get-AlphaBounds $originalBitmap
                    $center = $bounds.x + $bounds.width / 2.0 - $frame.originX
                    $geometry += [ordered]@{
                        action = $action; sourceAction = $sourceAction; frameIndex = $index
                        sourcePath = $frame.sourcePath; resolvedSourcePath = $resolved.FullPathToFile
                        file = $asset.file; sha256 = $asset.sha256; rgbaSha256 = Get-PixelHash $originalBitmap
                        width = $asset.width; height = $asset.height; originX = $frame.originX; originY = $frame.originY
                        delayMs = $frame.delayMs; alphaBounds = $bounds
                        headAnchor = @{ x = $center / 100.0; y = -($bounds.y - 4 - $frame.originY) / 100.0 }
                        feetOffset = @{ x = $center / 100.0; y = -($bounds.y + $bounds.height - $frame.originY) / 100.0 }
                    }
                } finally { $originalBitmap.Dispose(); $exportedBitmap.Dispose() }
            }
            $clipType = $entity.clips.GetType().GetGenericArguments()[0]
            $clip = [Activator]::CreateInstance($clipType)
            $clip.name = $action; $clip.loop = $loop
            $clip.durationMs = [double](($track.frames | Measure-Object delayMs -Sum).Sum)
            $clip.tracks.Add($track); $entity.clips.Add($clip)
        }
        $writer.Manifest.entities.Add($entity)
        $null = $writer.Commit()
        $preview = [IO.Path]::Combine([IO.Path]::GetDirectoryName($output), 'previews')
        $null = [IO.Directory]::CreateDirectory($preview)
        foreach ($frame in $geometry | Where-Object { $_.action -ne 'stand2' }) {
            [IO.File]::Copy([IO.Path]::Combine($output, $frame.file), [IO.Path]::Combine($preview, $frame.action + '-' + $frame.frameIndex + '.png'), $false)
        }
        [IO.File]::WriteAllText([IO.Path]::Combine($output, 'illusion-ring-geometry.json'), (ConvertTo-Json -InputObject @($geometry) -Depth 8), [Text.UTF8Encoding]::new($false))
        $result = [ordered]@{ name = $entity.displayName; output = $output; previews = $preview; ringId = $ring.itemId; ringSlot = $ring.slotIndex; clips = $entity.clips.Count; assets = $writer.Manifest.assets.Count; equipment = $entity.equipment.Count; warnings = $writer.Manifest.warnings.Count; originalRgbaVerified = $true; geometryFile = [IO.Path]::Combine($output, 'illusion-ring-geometry.json') }
        $results += $result
        $writer.Dispose(); $writer = $null
        Write-Output ('NATIVE_ILLUSION_EXPORTED name=' + $entity.displayName + ' ring=' + $ring.itemId + ' assets=' + $result.assets + ' equipment=' + $result.equipment + ' output=' + $output)
    }
    [IO.File]::WriteAllText([IO.Path]::Combine($outputRoot, 'illusion-export-results.json'), (ConvertTo-Json -InputObject @($results) -Depth 6), [Text.UTF8Encoding]::new($false))
} finally {
    if ($writer) { $writer.Dispose() }
    if ($data) { $data.Dispose() }
}
