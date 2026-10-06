[CmdletBinding()]
param(
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll'),
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$ItemName = '',
    [int]$ItemId = 0,
    [string]$SpritePrefix = '',
    [string]$OutputDirectory = '',
    [string]$PreviewDirectory = '',
    [string]$SpriteMetaTemplate = 'D:/Github/MapleLive/Assets/LiveChat/Textures/HighlightArrow.png.meta'
)

# Reads existing compiled WZ/exporter assemblies. No build, API request, settings
# access, Unity Editor launch, resizing, recoloring or synthesized chair pixels.
# Preview composites are separate original AvatarCanvas renders outside Assets.
$ErrorActionPreference = 'Stop'
$readerPath = [IO.Path]::GetFullPath($ReaderAssemblyPath)
$readerRoot = [IO.Path]::GetDirectoryName($readerPath)
$repoRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
$readerAssembly = [Reflection.Assembly]::LoadFrom($readerPath)
$commonAssembly = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.Common.dll'))
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static'
$null = $readerAssembly.GetType('Smoke', $true).GetMethod('SetDllDirectory', $flags).Invoke($null, @([IO.Path]::Combine($repoRoot, 'References/x64')))
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)

function Get-StableGuid([string]$Identity) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Identity))
    return [Convert]::ToHexString($hash).ToLowerInvariant().Substring(0, 32)
}

function Get-ProvenancePath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function Write-FolderMeta([string]$Path) {
    if ([IO.File]::Exists($Path + '.meta')) { return }
    $guid = Get-StableGuid ('MapleLive/LiveChat/ChairFolder/' + [IO.Path]::GetFileName($Path))
    [IO.File]::WriteAllText($Path + '.meta', "fileFormatVersion: 2`nguid: $guid`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n", [Text.UTF8Encoding]::new($false))
}

function Resolve-Canvas($Node, $Data) {
    $resolved = $Node
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($step = 0; $step -lt 24; $step++) {
        if ($null -eq $resolved -or !$visited.Add($resolved.FullPathToFile)) { throw 'Missing or cyclic original chair canvas link.' }
        if ($resolved.Value -is [WzComparerR2.WzLib.Wz_Uol]) { $resolved = $resolved.Value.HandleUol($resolved); continue }
        $outlink = $resolved.FindNodeByPath('_outlink')
        $inlink = $resolved.FindNodeByPath('_inlink')
        if ($outlink) { $resolved = $Data.Find(([string]$outlink.Value).Replace('/', '\')); continue }
        if ($inlink) { $resolved = $resolved.Value.WzImage.Node.FindNodeByPath(([string]$inlink.Value).Replace('/', '\'), $true); continue }
        if ($resolved.Value -isnot [WzComparerR2.WzLib.Wz_Png]) { throw 'Original chair canvas is not a PNG.' }
        return $resolved
    }
    throw 'Original chair canvas link limit exceeded.'
}

function Get-OriginalProperties($Node, [string]$Prefix = '') {
    # Flatten scalar/vector metadata; never serialize icons, raw canvas bytes or
    # arbitrary reader objects. This includes every original customChair flag.
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

function Write-SpriteMeta($Record) {
    $metaPath = [IO.Path]::Combine($outputPath, $Record.file + '.meta')
    $guid = Get-StableGuid ('MapleLive/LiveChat/Chair/' + $Record.logicalPath)
    if ([IO.File]::Exists($metaPath)) {
        $existingGuid = [regex]::Match([IO.File]::ReadAllText($metaPath), '(?m)^guid: ([0-9a-f]{32})').Groups[1].Value
        if ($existingGuid) { $guid = $existingGuid }
    }
    $spriteId = Get-StableGuid ('MapleLive/LiveChat/ChairSprite/' + $Record.logicalPath)
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $pivotX = ([double]$Record.origin.x / $Record.width).ToString('R', $culture)
    $pivotY = (1.0 - [double]$Record.origin.y / $Record.height).ToString('R', $culture)
    $maximumSize = 128
    while ($maximumSize -lt [Math]::Max($Record.width, $Record.height)) { $maximumSize *= 2 }
    $template = [IO.File]::ReadAllText([IO.Path]::GetFullPath($SpriteMetaTemplate))
    $template = [regex]::Replace($template, '(?m)^guid: [0-9a-f]{32}', 'guid: ' + $guid)
    $template = $template.Replace('HighlightArrow_0', $Record.name).Replace('-1955448553306655712', '21300000')
    $template = [regex]::Replace($template, 'spriteID: [0-9a-f]{32}', 'spriteID: ' + $spriteId)
    $template = [regex]::Replace($template, '(?m)(\s+)width: 11\s*$', '$1width: ' + $Record.width)
    $template = [regex]::Replace($template, '(?m)(\s+)height: 7\s*$', '$1height: ' + $Record.height)
    $template = [regex]::Replace($template, '(?m)(\s+)maxTextureSize: 128\s*$', '$1maxTextureSize: ' + $maximumSize)
    $template = $template.Replace('spritePivot: {x: 0, y: 1}', "spritePivot: {x: $pivotX, y: $pivotY}")
    $template = $template.Replace('pivot: {x: 0, y: 1}', "pivot: {x: $pivotX, y: $pivotY}")
    $template = [regex]::Replace($template, '(?m)^  userData: .*$', '  userData: "' + $Record.logicalPath + '"')
    [IO.File]::WriteAllText($metaPath, $template, [Text.UTF8Encoding]::new($false))
    return $guid
}

$data = $null
$avatar = $null
try {
    $data = [Activator]::CreateInstance($readerAssembly.GetType('DataSource', $true), @([IO.Path]::GetFullPath($BaseWzPath)))
    $strings = [Activator]::CreateInstance($commonAssembly.GetType('WzComparerR2.Common.StringLinker', $true))
    $null = $strings.Update($data.Find('String'), $null, $null, $null, $null)
    if ($ItemId -le 0 -and !$ItemName) { $ItemName = '릴렉스 체어' }
    $chairMatches = @($strings.StringItem.GetEnumerator() | Where-Object {
        ($ItemId -le 0 -or [int]$_.Key -eq $ItemId) -and
        (!$ItemName -or ($_.Value.Name -replace '\s', '') -eq ($ItemName -replace '\s', ''))
    })
    if ($chairMatches.Count -ne 1) { throw 'Original String/Ins.img must identify exactly one chair. Specify ItemId when the name has duplicate entries.' }
    $chairId = [int]$chairMatches[0].Key
    $chairName = [string]$chairMatches[0].Value.Name
    if (!$SpritePrefix) {
        $SpritePrefix = switch ($chairId) { 3010000 { 'RelaxChair' }; 3010001 { 'SkyBlueWoodChair' }; 3010532 { 'SkyBlueWoodChair' }; 3018487 { 'RiseReadingChair' }; default { 'Chair' + $chairId } }
    }
    if ($SpritePrefix -notmatch '^[A-Za-z][A-Za-z0-9]*$') { throw 'SpritePrefix must contain ASCII letters and digits only.' }
    if (!$OutputDirectory) { $OutputDirectory = 'D:/Github/MapleLive/Assets/LiveChat/Chairs/' + $SpritePrefix }
    if (!$PreviewDirectory) { $PreviewDirectory = [IO.Path]::Combine($repoRoot, '.tmp/chair-assets-validation/' + $SpritePrefix) }
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory)
    $previewPath = [IO.Path]::GetFullPath($PreviewDirectory)
    $slug = ([regex]::Replace($SpritePrefix, '([a-z0-9])([A-Z])', '$1-$2')).ToLowerInvariant()
    $itemText = $chairId.ToString('D8', [Globalization.CultureInfo]::InvariantCulture)
    $install = $data.Find('Item/Install')
    $chair = $null
    foreach ($image in $install.Nodes) {
        if ($itemText.StartsWith($image.Text.Replace('.img', ''), [StringComparison]::Ordinal)) {
            $candidate = $data.Find('Item/Install/' + $image.Text + '/' + $itemText)
            if ($candidate) { if ($chair) { throw 'Original chair ID resolves to multiple images.' }; $chair = $candidate }
        }
    }
    if (!$chair) { throw 'Original chair item node is unavailable.' }
    [IO.Directory]::CreateDirectory($outputPath) | Out-Null
    [IO.Directory]::CreateDirectory($previewPath) | Out-Null
    Write-FolderMeta ([IO.Path]::GetDirectoryName($outputPath))
    Write-FolderMeta $outputPath
    $info = $chair.FindNodeByPath('info')
    if (!$info) { throw 'Original chair info is unavailable.' }
    $bodyRelMove = $info.FindNodeByPath('bodyRelMove')
    if ($bodyRelMove -and $bodyRelMove.Value -isnot [WzComparerR2.WzLib.Wz_Vector]) { throw 'Original bodyRelMove is not a vector.' }
    $sitAction = $info.FindNodeByPath('sitAction')
    $sitEmotion = $info.FindNodeByPath('sitEmotion')
    $bodyPixels = @{ x = $(if ($bodyRelMove) { [int]$bodyRelMove.Value.X } else { 0 }); y = $(if ($bodyRelMove) { [int]$bodyRelMove.Value.Y } else { 0 }) }
    $seatOffset = @{ x = $bodyPixels.x / 100.0; y = -$bodyPixels.y / 100.0 }
    $action = $(if ($sitAction) { [string]$sitAction.Value } else { 'sit' })
    $fixedFrame = $info.FindNodeByPath('fixFrameIdx')
    if (!$fixedFrame) { $fixedFrame = $info.FindNodeByPath('fixActionFrame') }
    $bodyFrameIndex = $(if ($fixedFrame -and [int]$fixedFrame.Value -ge 0) { [int]$fixedFrame.Value } else { 0 })
    $itemEffects = $data.Find('Effect/ItemEff.img/' + $chairId)
    if ($itemEffects) { throw 'Additional original ItemEff layers require explicit effect export before import.' }
    $sources = @()
    foreach ($branch in @('effect', 'effect2')) {
        $node = $chair.FindNodeByPath($branch)
        if ($node) {
            $originalZ = $node.FindNodeByPath('z')
            $sourceZ = $(if ($originalZ) { [int]$originalZ.Value } else { -2 })
            $originalPosition = $node.FindNodeByPath('pos')
            $sourcePosition = $(if ($originalPosition) { [int]$originalPosition.Value } else { 0 })
            if ($sourcePosition -notin @(0, 3) -or ($bodyRelMove -and $sourcePosition -ne 3)) {
                throw 'This original chair uses a non-root anchor or special bodyRelMove relation. Review its positioning before creating a shared chair profile.'
            }
            $sources += @{ node = $node; branch = $branch; rawZ = $sourceZ; layer = $(if ($sourceZ -lt 0) { 'back' } else { 'front' }) }
        }
    }
    if ($sources.Count -eq 0) { throw 'Original chair has no effect/effect2 canvases.' }
    $records = @()
    $tracks = @()
    foreach ($source in $sources) {
        $zNode = $source.node.FindNodeByPath('z')
        $posNode = $source.node.FindNodeByPath('pos')
        $z = $source.rawZ
        $layer = $source.layer
        $trackRecords = @()
        foreach ($frame in @($source.node.Nodes | Where-Object { $_.Text -match '^\d+$' } | Sort-Object { [int]$_.Text })) {
            $resolved = Resolve-Canvas $frame $data
            $bitmap = $resolved.Value.ExtractPng()
            $name = $SpritePrefix + $(if ($layer -eq 'back') { 'Back' } else { 'Front' })
            if (@($sources | Where-Object { $_.layer -eq $layer }).Count -gt 1) { $name += '_' + $source.branch }
            if ([int]$frame.Text -ne 0) { $name += '_' + $frame.Text }
            $file = $name + '.png'
            $originNode = $frame.FindNodeByPath('origin')
            $origin = @{ x = $(if ($originNode) { [int]$originNode.Value.X } else { 0 }); y = $(if ($originNode) { [int]$originNode.Value.Y } else { 0 }) }
            $delayNode = $frame.FindNodeByPath('delay')
            $frameZ = $frame.FindNodeByPath('z')
            $pngPath = [IO.Path]::Combine($outputPath, $file)
            $width = $bitmap.Width; $height = $bitmap.Height
            try { $bitmap.Save($pngPath, [Drawing.Imaging.ImageFormat]::Png) }
            finally { $bitmap.Dispose() }
            $physicalFile = $resolved.Value.WzFile.FileStream.Name
            $physicalInfo = [IO.FileInfo]::new($physicalFile)
            $record = [ordered]@{
                name = $name; file = $file; layer = $layer; branch = $source.branch; frameIndex = [int]$frame.Text
                logicalPath = $frame.FullPathToFile.Replace('\', '/'); resolvedPath = $resolved.FullPathToFile.Replace('\', '/')
                width = $width; height = $height; origin = $origin; originalOriginPresent = $null -ne $originNode
                rawZ = $z; originalZPresent = $null -ne $zNode; rawPosition = $(if ($posNode) { $posNode.Value } else { 0 }); originalPositionPresent = $null -ne $posNode
                frameRawZ = $(if ($frameZ) { $frameZ.Value } else { $null }); originalFrameZPresent = $null -ne $frameZ
                originalDelayPresent = $null -ne $delayNode; delayMs = $(if ($delayNode) { [int]$delayNode.Value } else { 120 })
                originalProperties = @(Get-OriginalProperties $frame)
                sourceWz = Get-ProvenancePath $physicalFile; sourceSize = $physicalInfo.Length; sourceModifiedUtc = $physicalInfo.LastWriteTimeUtc.ToString('O')
                pngSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($pngPath))).ToLowerInvariant()
            }
            $record.unityGuid = Write-SpriteMeta $record
            $records += $record; $trackRecords += $record
        }
        if ($trackRecords.Count -eq 0) { throw 'Original chair track has no numbered frames.' }
        $trackDuration = 0
        foreach ($trackRecord in $trackRecords) { $trackDuration += [int]$trackRecord.delayMs }
        $tracks += [ordered]@{
            branch = $source.branch; layer = $layer; rawZ = $z; rawPosition = $(if ($posNode) { $posNode.Value } else { 0 })
            frameCount = $trackRecords.Count; isStatic = $trackRecords.Count -eq 1; loop = $trackRecords.Count -gt 1
            totalDurationMs = $trackDuration
            frames = @($trackRecords | ForEach-Object { [ordered]@{ frameIndex = $_.frameIndex; file = $_.file; unityGuid = $_.unityGuid; delayMs = $_.delayMs; origin = $_.origin } })
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1; itemId = $chairId; name = $chairName
        stringSourcePath = 'String/' + $chairMatches[0].Value.FullPath.Replace('\', '/')
        sourcePath = $chair.FullPathToFile.Replace('\', '/'); sourceBaseWz = Get-ProvenancePath $BaseWzPath
        readerAssembly = Get-ProvenancePath $readerPath; pixelsPerUnit = 100; coordinateSystem = 'WZ pixels: positive Y down; Unity: positive Y up'
        sitAction = $action; originalSitActionPresent = $null -ne $sitAction
        sitEmotion = $(if ($sitEmotion) { $sitEmotion.Value } else { 0 }); originalSitEmotionPresent = $null -ne $sitEmotion
        originalFixedFramePresent = $null -ne $fixedFrame; fixedBodyFrameIndex = $bodyFrameIndex
        originalBodyRelMovePresent = $null -ne $bodyRelMove; bodyRelMovePixels = $bodyPixels
        seatOffsetWorld = $seatOffset; chairOffsetWorld = @{ x = 0; y = 0 }; groundOffsetWorld = @{ x = 0; y = 0 }
        positioningEvidence = $(if ($bodyRelMove) { 'AvatarCanvas pos=3 applies -bodyRelMove to chair skins while the avatar root remains fixed. Translating the complete result by +bodyRelMove fixes the chair floor at zero and moves the avatar by bodyRelMove. Unity inverts Y.' } else { 'No original bodyRelMove. AvatarCanvas default sit action and root-anchored chair effect apply no relative body translation.' })
        isStatic = @($tracks | Where-Object { !$_.isStatic }).Count -eq 0
        hasFrontLayer = @($records | Where-Object { $_.layer -eq 'front' }).Count -gt 0; additionalItemEffectPresent = $false
        originalInfoProperties = @(Get-OriginalProperties $info)
        tracks = $tracks; assets = $records
    }
    $jsonName = 'wz-' + $slug + '.json'
    [IO.File]::WriteAllText([IO.Path]::Combine($outputPath, $jsonName), ($manifest | ConvertTo-Json -Depth 15), [Text.UTF8Encoding]::new($false))
    $jsonGuid = Get-StableGuid ('MapleLive/LiveChat/' + $SpritePrefix + '/SourceMetadata')
    [IO.File]::WriteAllText([IO.Path]::Combine($outputPath, $jsonName + '.meta'), "fileFormatVersion: 2`nguid: $jsonGuid`nTextScriptImporter:`n  externalObjects: {}`n  userData:`n  assetBundleName:`n  assetBundleVariant:`n", [Text.UTF8Encoding]::new($false))

    # Validate every original chair animation frame with the existing renderer.
    # Standard fixture parts have no cape/weapon/equipment effects to be hidden.
    $mainAssembly = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.dll'))
    $avatar = [Activator]::CreateInstance($mainAssembly.GetType('WzComparerR2.AvatarCommon.AvatarCanvas', $true))
    foreach ($path in @('Character/00002000.img', 'Character/00012000.img', 'Character/Face/00020000.img', 'Character/Hair/00030000.img', 'Character/Coat/01040036.img', 'Character/Pants/01060026.img')) {
        $null = $avatar.AddPart($data.Find($path))
    }
    $null = $avatar.LoadZ($data.Find('Base/zmap.img')); $null = $avatar.LoadActions(); $null = $avatar.LoadEmotions()
    $emptyIcon = [Activator]::CreateInstance($commonAssembly.GetType('WzComparerR2.BitmapOrigin', $true))
    $null = $avatar.AddChairPart($chair, $emptyIcon, $chairId, $(if ($bodyRelMove) { $bodyRelMove.Value } else { $null }), $false)
    $avatar.LoadAllEffects()
    $avatar.ActionName = $action; $avatar.EmotionName = 'default'
    if ($sitEmotion -and [int]$sitEmotion.Value -ne 0) { throw 'Nondefault original sitEmotion requires explicit preview emotion mapping.' }
    $maximumFrames = 0
    foreach ($track in $tracks) { $maximumFrames = [Math]::Max($maximumFrames, [int]$track.frameCount) }
    for ($frameIndex = 0; $frameIndex -lt $maximumFrames; $frameIndex++) {
        $effects = [int[]]::new([WzComparerR2.AvatarCommon.AvatarCanvas]::LayerSlotLength)
        [Array]::Fill($effects, -1)
        foreach ($track in $tracks) {
            $layerSlot = $(if ($track.branch -eq 'effect') { [WzComparerR2.AvatarCommon.AvatarCanvas]::IndexChairLayer1 } else { [WzComparerR2.AvatarCommon.AvatarCanvas]::IndexChairLayer2 })
            $effects[$layerSlot] = [int]$track.frames[$frameIndex % $track.frameCount].frameIndex
        }
        $primitives = $avatar.CreateFramePrimitives($avatar.CreateFrame($bodyFrameIndex, 0, $null, $effects))
        try {
            $bounds = [Drawing.Rectangle]::Empty
            foreach ($primitive in $primitives) {
                $rectangle = [Drawing.Rectangle]::new($primitive.Position, $primitive.Bitmap.Size)
                $bounds = $(if ($bounds.IsEmpty) { $rectangle } else { [Drawing.Rectangle]::Union($bounds, $rectangle) })
            }
            $preview = [Drawing.Bitmap]::new($bounds.Width, $bounds.Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [Drawing.Graphics]::FromImage($preview)
            $previewName = 'original-avatar-' + $slug + $(if ($frameIndex -eq 0) { '' } else { '-' + $frameIndex })
            try {
                foreach ($primitive in $primitives) { $graphics.DrawImageUnscaled($primitive.Bitmap, $primitive.Position.X - $bounds.X, $primitive.Position.Y - $bounds.Y) }
                $preview.Save([IO.Path]::Combine($previewPath, $previewName + '.png'), [Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $preview.Dispose() }
            $previewInfo = [ordered]@{
                itemId = $chairId; action = $action; bodyFrame = $bodyFrameIndex; faceFrame = 0; chairFrame = $frameIndex
                bodyRelMoveApplied = $null -ne $bodyRelMove; bodyRelMovePixels = $bodyPixels; seatOffsetWorld = $seatOffset
                width = $bounds.Width; height = $bounds.Height; origin = @{ x = -$bounds.X; y = -$bounds.Y }
                floorCoordinateTranslationPixels = $bodyPixels
                renderLayers = @($primitives | ForEach-Object { @{ source = $_.SourceKey; slot = $_.PartSlot; x = $_.Position.X; y = $_.Position.Y; floorX = $_.Position.X + $bodyPixels.x; floorY = $_.Position.Y + $bodyPixels.y; rawZ = $_.RawZIndex; resolvedZ = $_.ResolvedZ; drawOrdinal = $_.DrawOrdinal } })
            }
            [IO.File]::WriteAllText([IO.Path]::Combine($previewPath, $previewName + '.json'), ($previewInfo | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        } finally { foreach ($primitive in $primitives) { if ($primitive.OwnsBitmap) { $primitive.Bitmap.Dispose() } } }
    }
    Write-Output ('CHAIR_ORIGINAL_EXPORT item=' + $chairId + ' name=' + $chairName + ' frames=' + $records.Count + ' bodyOffset=(' + $bodyPixels.x + ',' + $bodyPixels.y + ') output=' + $outputPath + ' previews=' + $previewPath)
} finally {
    if ($avatar) { $avatar.ClearSkinCache() }
    if ($data) { $data.Dispose() }
}
