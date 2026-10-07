[CmdletBinding()]
param(
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll'),
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$OutputDirectory = 'D:/Github/MapleLive/Assets/LiveEvents/CrimsonBalrog/SourceExports',
    [switch]$InspectOnly,
    [string]$PreviewDirectory
)

# Use the already compiled WzComparer reader/exporter; never read API settings or start its UI.
$ErrorActionPreference = 'Stop'
$readerPath = [IO.Path]::GetFullPath($ReaderAssemblyPath)
$readerRoot = [IO.Path]::GetDirectoryName($readerPath)
$repoRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
$assembly = [Reflection.Assembly]::LoadFrom($readerPath)
$null = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.Common.dll'))
$main = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.dll'))
$plugin = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.PluginBase.dll'))
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$null = $assembly.GetType('Smoke', $true).GetMethod('SetDllDirectory', $flags).Invoke($null, @([IO.Path]::Combine($repoRoot, 'References/x64')))
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$exporterType = $main.GetType('WzComparerR2.UnityExport.UnityEntityExporter', $true)
$readTrack = $exporterType.GetMethod('ReadTrack')
$resolveUol = $exporterType.GetMethod('ResolveUol')
$findMethod = $plugin.GetType('WzComparerR2.PluginBase.PluginManager', $true).GetMethods() | Where-Object {
    $_.Name -eq 'FindWz' -and $_.GetParameters().Count -eq 2 -and $_.GetParameters()[0].ParameterType -eq [string]
} | Select-Object -First 1
$find = $findMethod.CreateDelegate($readTrack.GetParameters()[3].ParameterType)
$writerType = $main.GetType('WzComparerR2.UnityExport.UnityExportWriter', $true)
$addEntity = $exporterType.GetMethod('AddEntity')
$utf8 = [Text.UTF8Encoding]::new($false)
$data = $null
$writer = $null

function Resolve-Canvas($Node) {
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $links = [Collections.Generic.List[string]]::new()
    $resolved = $Node
    while ($true) {
        if (!$resolved -or !$visited.Add($resolved.FullPathToFile)) { throw 'Missing/cyclic original canvas link.' }
        $links.Add($resolved.FullPathToFile.Replace('\', '/'))
        $next = $resolveUol.Invoke($null, @($resolved, $find))
        if (![object]::ReferenceEquals($resolved, $next)) { $resolved = $next; continue }
        $next = [WzComparerR2.Common.Wz_NodeExtension2]::GetLinkedSourceNode($resolved, $find, $null)
        if ([object]::ReferenceEquals($resolved, $next)) { break }
        $resolved = $next
    }
    if ($resolved.Value -isnot [WzComparerR2.WzLib.Wz_Png]) { throw 'Resolved original canvas is not a PNG.' }
    return [pscustomobject]@{ node = $resolved; links = @($links) }
}

function Add-Meta($List, [string]$Key, [string]$Value) {
    $entry = [Activator]::CreateInstance($List.GetType().GetGenericArguments()[0])
    $entry.key = $Key; $entry.value = $Value; $List.Add($entry)
}

function Get-OriginalProperties($Node, [string]$Prefix = '') {
    foreach ($child in $Node.Nodes) {
        $path = $(if ($Prefix) { $Prefix + '/' + $child.Text } else { $child.Text })
        $value = $child.Value
        if ($value -is [WzComparerR2.WzLib.Wz_Png]) { continue }
        if ($value -is [WzComparerR2.WzLib.Wz_Vector]) {
            [ordered]@{ path = $path; type = 'vector'; value = @{ x = [int]$value.X; y = [int]$value.Y } }
        } elseif ($value -is [WzComparerR2.WzLib.Wz_Uol]) {
            [ordered]@{ path = $path; type = 'uol'; value = [string]$value.Uol }
        } elseif ($null -ne $value -and ($value -is [string] -or $value.GetType().IsPrimitive -or $value -is [decimal])) {
            [ordered]@{ path = $path; type = $value.GetType().Name; value = $value }
        }
        if ($child.Nodes.Count -gt 0) { Get-OriginalProperties $child $path }
    }
}

function Add-OriginalClip($Entity, [string]$Name, $ActionNode, [bool]$Loop) {
    $track = $readTrack.Invoke($null, @($ActionNode, 'body', $writer, $find, $Loop))
    if ($track.frames.Count -eq 0) { throw 'Required original animation contains no frames.' }
    Add-Meta $track.metadata 'source' $ActionNode.FullPathToFile
    $clip = [Activator]::CreateInstance($Entity.clips.GetType().GetGenericArguments()[0])
    $clip.name = $Name; $clip.loop = $Loop
    $clip.durationMs = [double](($track.frames | Measure-Object delayMs -Sum).Sum)
    $clip.tracks.Add($track); $Entity.clips.Add($clip)
}

function Get-PixelHash([byte[]]$Pixels) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Pixels)).ToLowerInvariant()
}

function Test-OriginalFrames($Entity) {
    foreach ($clip in $Entity.clips) {
        for ($trackIndex = 0; $trackIndex -lt $clip.tracks.Count; $trackIndex++) {
            $track = $clip.tracks[$trackIndex]
            for ($frameIndex = 0; $frameIndex -lt $track.frames.Count; $frameIndex++) {
                $frame = $track.frames[$frameIndex]
                $logical = $data.Find($frame.sourcePath)
                if (!$logical) { throw 'Original animation frame is unavailable.' }
                $authoredPath = $Entity.sourcePath + '/' + $(if ($Entity.kind -eq 'obj') { '' } else { $clip.name + '/' }) + $frameIndex
                if ($trackIndex -gt 0 -or !$data.Find($authoredPath)) { $authoredPath = $frame.sourcePath }
                $authored = $data.Find($authoredPath)
                $resolution = Resolve-Canvas $authored
                $resolved = $resolution.node
                $asset = $writer.Manifest.assets | Where-Object id -eq $frame.assetId | Select-Object -First 1
                if (!$asset) { throw 'Original animation references an unexported canvas.' }
                $originalBitmap = $resolved.Value.ExtractPng()
                $exportedPath = [IO.Path]::Combine($writer.StagingDirectory, $asset.file)
                $exportedBitmap = [Drawing.Bitmap]::new($exportedPath)
                try {
                    $originalPixels = [MapleLiveCrimsonBalrogPixels]::Read($originalBitmap)
                    $originalHash = Get-PixelHash $originalPixels
                    if ($originalBitmap.Width -ne $exportedBitmap.Width -or $originalBitmap.Height -ne $exportedBitmap.Height -or
                        $originalHash -ne (Get-PixelHash ([MapleLiveCrimsonBalrogPixels]::Read($exportedBitmap)))) { throw 'Original RGBA changed during export.' }
                    if ((Get-FileHash -LiteralPath $exportedPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw 'Exported PNG checksum mismatch.' }
                    $expectedOrigin = $logical.Nodes['origin'].Value
                    if ($frame.originX -ne $expectedOrigin.X -or $frame.originY -ne $expectedOrigin.Y) { throw 'Original WZ origin changed during export.' }
                    $originalDelay = $logical.Nodes['delay'].Value
                    $expectedDelay = $(if ($null -eq $originalDelay -or $originalDelay -eq 0) { 120.0 } else { [Math]::Abs([double]$originalDelay) })
                    if ($frame.delayMs -ne $expectedDelay) { throw 'Original WZ frame duration changed during export.' }
                    $alpha = [MapleLiveCrimsonBalrogPixels]::AlphaBounds($originalPixels, $originalBitmap.Width, $originalBitmap.Height)
                    $sourceFile = [IO.FileInfo]::new($resolved.Value.WzFile.FileStream.Name)
                    [ordered]@{
                        action = $clip.name; track = $track.id; frameIndex = $frameIndex
                        authoredFramePath = $authoredPath.Replace('\', '/'); effectiveFramePath = $frame.sourcePath.Replace('\', '/')
                        resolvedCanvasPath = $resolved.FullPathToFile.Replace('\', '/'); links = $resolution.links
                        sourceWzFile = $sourceFile.FullName.Replace('\', '/'); sourceWzLength = $sourceFile.Length; sourceWzLastWriteUtc = $sourceFile.LastWriteTimeUtc.ToString('O')
                        file = $asset.file; sha256 = $asset.sha256; rgbaSha256 = $originalHash
                        width = $asset.width; height = $asset.height; originX = $frame.originX; originY = $frame.originY
                        delayMs = $frame.delayMs; a0 = $frame.a0; a1 = $frame.a1; blend = $frame.blend
                        loop = $track.loop; startMs = $track.startMs
                        alphaBounds = @{ x = $alpha[0]; y = $alpha[1]; width = $alpha[2]; height = $alpha[3] }
                        properties = @(Get-OriginalProperties $authored)
                    }
                } finally { $originalBitmap.Dispose(); $exportedBitmap.Dispose() }
            }
        }
    }
}

function Write-UnityMeta([string]$Path, [bool]$Folder = $false) {
    if ([IO.File]::Exists($Path + '.meta')) { return }
    # Stable identities include the complete source export relative path. Existing
    # meta files are preserved when re-exporting a matching managed bundle.
    $relative = [IO.Path]::GetRelativePath($outputRoot, $Path).Replace('\', '/')
    $guid = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('MapleLive/CrimsonBalrog/SourceExports/' + $relative))).ToLowerInvariant().Substring(0, 32)
    $body = "fileFormatVersion: 2`nguid: $guid`n" + $(if ($Folder) { "folderAsset: yes`n" } else { '' })
    if (!$Folder -and [IO.Path]::GetExtension($Path) -eq '.png') {
        $body += "TextureImporter:`n  externalObjects: {}`n  serializedVersion: 13`n  mipmaps:`n    enableMipMap: 0`n  isReadable: 0`n  textureType: 0`n  alphaIsTransparency: 1`n  textureSettings:`n    serializedVersion: 2`n    filterMode: 0`n    aniso: 1`n    mipBias: 0`n    wrapU: 1`n    wrapV: 1`n    wrapW: 1`n  textureCompression: 0`n  userData: original-wz-source-export`n  assetBundleName:`n  assetBundleVariant:`n"
    } else {
        $body += "DefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n"
    }
    [IO.File]::WriteAllText($Path + '.meta', $body, $utf8)
}

try {
    $data = [Activator]::CreateInstance($assembly.GetType('DataSource', $true), @([IO.Path]::GetFullPath($BaseWzPath)))
    if ($InspectOnly) {
        $mobStrings = $data.Find('String/Mob.img')
        foreach ($entry in $mobStrings.Nodes) {
            $nameNode = $entry.FindNodeByPath('name')
            if ($nameNode -and $entry.Text -in @('8150000', '9300288', '9300400')) {
                [pscustomobject]@{ kind = 'mob-name'; id = $entry.Text; name = [string]$nameNode.Value } | ConvertTo-Json -Compress
            }
        }
        foreach ($path in @('Mob/8150000.img', 'Map/Obj/vehicle.img/ship', 'Map/Obj/vehicle.img/ship/ossyria/97', 'Map/Obj/vehicle.img/ship/ossyria/98', 'Map/Obj/vehicle.img/ship/ossyria/99', 'Effect/Tomb.img/fall', 'Effect/Tomb.img/land')) {
            $node = $data.Find($path)
            if (!$node) { Write-Output ('MISSING ' + $path); continue }
            Write-Output ('NODE ' + $path)
            foreach ($child in $node.Nodes) {
                if ($path -eq 'Map/Back' -or $path -eq 'Map/Obj') {
                    if ($child.Text -notmatch 'ship|plane|airport|balrog|crimson|air') { continue }
                } elseif ($path -in @('Effect', 'Character', 'UI') -and $child.Text -notmatch 'tomb|grave|death|die|dead') { continue }
                $resolved = $data.Find($path + '/' + $child.Text)
                [pscustomobject]@{ kind = 'child'; path = $path + '/' + $child.Text; type = $(if ($child.Value) { $child.Value.GetType().Name } else { 'node' });
                    children = @($resolved.Nodes | ForEach-Object { [pscustomobject]@{ name = $_.Text; type = $(if ($_.Value) { $_.Value.GetType().Name } else { 'node' }); count = $_.Nodes.Count } }) } | ConvertTo-Json -Depth 5 -Compress
            }
        }
        if ($PreviewDirectory) {
            $previewRoot = [IO.Path]::GetFullPath($PreviewDirectory)
            $null = [IO.Directory]::CreateDirectory($previewRoot)
            foreach ($entry in @(
                @{ name = 'ship97'; path = 'Map/Obj/vehicle.img/ship/ossyria/97/0' },
                @{ name = 'ship98'; path = 'Map/Obj/vehicle.img/ship/ossyria/98/0' },
                @{ name = 'ship99'; path = 'Map/Obj/vehicle.img/ship/ossyria/99/0' },
                @{ name = 'balrog-fly0'; path = 'Mob/8150000.img/fly/0' },
                @{ name = 'tomb-fall0'; path = 'Effect/Tomb.img/fall/0' },
                @{ name = 'tomb-land0'; path = 'Effect/Tomb.img/land/0' }
            )) {
                $logical = $resolveUol.Invoke($null, @($data.Find($entry.path), $find))
                $source = $logical
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                while ($source -and $seen.Add($source.FullPathToFile)) {
                    $next = [WzComparerR2.Common.Wz_NodeExtension2]::GetLinkedSourceNode($source, $find, $null)
                    $next = $resolveUol.Invoke($null, @($next, $find))
                    if (!$next -or [object]::ReferenceEquals($source, $next)) { break }
                    $source = $next
                }
                if (!$source -or $source.Value -isnot [WzComparerR2.WzLib.Wz_Png]) { continue }
                $bitmap = $source.Value.ExtractPng()
                try { $bitmap.Save([IO.Path]::Combine($previewRoot, $entry.name + '.png'), [Drawing.Imaging.ImageFormat]::Png) }
                finally { $bitmap.Dispose() }
                [pscustomobject]@{ kind = 'preview'; path = $entry.path; resolved = $source.FullPathToFile; originX = $logical.Nodes['origin'].Value.X; originY = $logical.Nodes['origin'].Value.Y; delay = $logical.Nodes['delay'].Value;
                    image = [IO.Path]::Combine($previewRoot, $entry.name + '.png') } | ConvertTo-Json -Compress
            }
        }
    } else {
        $outputRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
        if ($outputRoot -eq [IO.Path]::GetPathRoot($outputRoot).TrimEnd('\', '/')) { throw 'Choose a dedicated export subdirectory.' }
        Add-Type -TypeDefinition @'
public static class MapleLiveCrimsonBalrogPixels {
    public static byte[] Read(System.Drawing.Bitmap image) {
        byte[] pixels = new byte[image.Width * image.Height * 4];
        int offset = 0;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) {
            System.Drawing.Color color = image.GetPixel(x, y);
            pixels[offset++] = color.R; pixels[offset++] = color.G;
            pixels[offset++] = color.B; pixels[offset++] = color.A;
        }
        return pixels;
    }
    public static int[] AlphaBounds(byte[] pixels, int width, int height) {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
            if (pixels[(y * width + x) * 4 + 3] == 0) continue;
            if (x < left) left = x; if (x > right) right = x;
            if (y < top) top = y; if (y > bottom) bottom = y;
        }
        return right < left ? new int[] { 0, 0, 0, 0 } : new int[] { left, top, right - left + 1, bottom - top + 1 };
    }
}
'@ -ReferencedAssemblies @([Drawing.Bitmap].Assembly.Location, [Drawing.Color].Assembly.Location)
        $specifications = @(
            @{ folder = 'Mob'; id = 'crimson-balrog-8150000'; entityId = 'mob-8150000'; kind = 'mob'; path = 'Mob/8150000.img'; name = '크림슨 발록'; defaultAction = 'fly'; actions = @('fly', 'stand', 'attack1', 'attack2', 'hit1', 'die1'); counts = @(4, 4, 8, 5, 1, 10) },
            @{ folder = 'Ship'; id = 'crimson-balrog-ship'; entityId = 'balrog-ship'; kind = 'obj'; path = 'Map/Obj/vehicle.img/ship/ossyria/97'; name = '크림슨 발록의 배'; defaultAction = 'idle'; actions = @('idle'); counts = @(1) },
            @{ folder = 'Tomb'; id = 'maple-character-tomb'; entityId = 'character-tomb'; kind = 'effect'; path = 'Effect/Tomb.img'; name = '기본 캐릭터 묘비'; defaultAction = 'fall'; actions = @('fall', 'land'); counts = @(20, 1) }
        )
        $results = @()
        foreach ($specification in $specifications) {
            $node = $data.Find($specification.path)
            if (!$node) { throw 'Required original WZ asset is missing.' }
            $destination = [IO.Path]::GetFullPath([IO.Path]::Combine($outputRoot, $specification.folder))
            if (![IO.Path]::GetDirectoryName($destination).Equals($outputRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Export destination escaped its dedicated root.' }
            $writer = [Activator]::CreateInstance($writerType, @($destination, $specification.id, $specification.kind, $node.FullPathToFile, [Threading.CancellationToken]::None))
            if ($specification.kind -eq 'mob') {
                $entity = $addEntity.Invoke($null, @($node, 'mob', $writer, $find, $specification.entityId, $null))
            } else {
                $entity = [Activator]::CreateInstance($writer.Manifest.entities.GetType().GetGenericArguments()[0])
                $entity.id = $specification.entityId; $entity.kind = $specification.kind; $entity.sourcePath = $node.FullPathToFile
                if ($specification.kind -eq 'obj') {
                    Add-OriginalClip $entity 'idle' $node $true
                } else {
                    foreach ($action in $specification.actions) { Add-OriginalClip $entity $action $node.Nodes[$action] $false }
                }
                $writer.Manifest.entities.Add($entity)
            }
            if (!$entity) { throw 'Required original WZ entity has no animation.' }
            $entity.displayName = $specification.name; $entity.defaultAction = $specification.defaultAction
            Add-Meta $entity.metadata 'original/source' $node.FullPathToFile
            Add-Meta $entity.metadata 'original/pixels' 'WZ canvas RGBA unchanged; all UOL/inlink/outlink resolved'
            Add-Meta $entity.metadata 'original/motion' $(if ($specification.kind -eq 'obj') { 'Static original ship canvas; arrival/departure translation and opacity are runtime effects.' } else { 'Original frame sequence, durations, origins and authored alpha.' })
            for ($index = 0; $index -lt $specification.actions.Count; $index++) {
                $clips = @($entity.clips | Where-Object name -eq $specification.actions[$index])
                if ($clips.Count -ne 1 -or $clips[0].tracks[0].frames.Count -ne $specification.counts[$index]) { throw 'Original WZ animation frame inventory differs from the verified source.' }
            }
            if ($entity.clips.Count -ne $specification.actions.Count) { throw 'Original WZ export has unexpected or omitted actions.' }
            $frames = @(Test-OriginalFrames $entity)
            $provenance = [ordered]@{
                schemaVersion = 1; id = $specification.id; displayName = $specification.name
                originalBaseWz = [IO.Path]::GetFullPath($BaseWzPath).Replace('\', '/')
                sourcePath = $node.FullPathToFile.Replace('\', '/'); exporter = 'WzComparerR2 UnityEntityExporter/ReadTrack'
                generatedUtc = [DateTime]::UtcNow.ToString('O'); pixelsPerUnit = $writer.Manifest.pixelsPerUnit
                originalRgbaVerified = $true; originalOriginsVerified = $true; originalDurationsVerified = $true
                sourceInfo = @(Get-OriginalProperties $node.Nodes['info'])
                clips = @($entity.clips | ForEach-Object { [ordered]@{ name = $_.name; loop = $_.loop; durationMs = $_.durationMs; tracks = $_.tracks.Count; frames = $_.tracks[0].frames.Count } })
                frames = $frames
            }
            $null = $writer.Commit()
            # Provenance is a helper-owned sidecar, not a writer-managed PNG.
            # Write after Commit so a repeat export preserves user files without
            # colliding with the prior provenance sidecar in the staging folder.
            [IO.File]::WriteAllText([IO.Path]::Combine($destination, 'original-wz-provenance.json'), ($provenance | ConvertTo-Json -Depth 16), $utf8)
            $result = [ordered]@{ id = $specification.id; entityId = $entity.id; output = $destination.Replace('\', '/'); sourcePath = $provenance.sourcePath; clips = $provenance.clips; assets = $writer.Manifest.assets.Count; warnings = $writer.Manifest.warnings.Count; originalRgbaVerified = $true }
            $results += $result
            $writer.Dispose(); $writer = $null
            Write-Output ('ORIGINAL_WZ_EXPORTED ' + ($result | ConvertTo-Json -Depth 5 -Compress))
        }
        [IO.File]::WriteAllText([IO.Path]::Combine($outputRoot, 'crimson-balrog-export-results.json'), (ConvertTo-Json -InputObject @($results) -Depth 8), $utf8)
        $assetsRoot = [IO.Path]::GetFullPath('D:/Github/MapleLive/Assets')
        if ($outputRoot.StartsWith($assetsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            $folder = $outputRoot
            while (!$folder.Equals($assetsRoot, [StringComparison]::OrdinalIgnoreCase)) { Write-UnityMeta $folder $true; $folder = [IO.Path]::GetDirectoryName($folder) }
            foreach ($folderInfo in Get-ChildItem -LiteralPath $outputRoot -Directory -Recurse) { Write-UnityMeta $folderInfo.FullName $true }
            foreach ($fileInfo in Get-ChildItem -LiteralPath $outputRoot -File -Recurse | Where-Object Extension -ne '.meta') { Write-UnityMeta $fileInfo.FullName }
        }
    }
} finally {
    if ($writer) { $writer.Dispose() }
    if ($data) { $data.Dispose() }
}
