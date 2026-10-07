[CmdletBinding()]
param(
    [string[]]$NativeBundleDirectory = @(),
    [string]$StoredAnimationSetPath,
    [string]$StoredCharacterName,
    [string[]]$StoredActions = @('walk2'),
    [switch]$DeathOnly,
    [string]$OutputDirectory,
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll'),
    [switch]$InspectOnly
)

# Existing decoded appearance/export metadata only. No network, API settings,
# Unity Editor launch, scene write, application build or published-file changes.
$ErrorActionPreference = 'Stop'
$readerPath = [IO.Path]::GetFullPath($ReaderAssemblyPath)
$readerRoot = [IO.Path]::GetDirectoryName($readerPath)
$repoRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
$reader = [Reflection.Assembly]::LoadFrom($readerPath)
$common = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.Common.dll'))
$main = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.dll'))
$plugin = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.PluginBase.dll'))
$avatarAssembly = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.Avatar.dll'))
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
$null = $reader.GetType('Smoke', $true).GetMethod('SetDllDirectory', $flags).Invoke($null, @([IO.Path]::Combine($repoRoot, 'References/x64')))
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$data = $null
$avatar = $null
$writer = $null
$results = @()
$avatarType = $main.GetType('WzComparerR2.AvatarCommon.AvatarCanvas', $true)
$partType = $main.GetType('WzComparerR2.AvatarCommon.AvatarPart', $true)
$prismType = $main.GetType('WzComparerR2.AvatarCommon.PrismDataCollection+PrismDataType', $true)
$exporterType = $avatarAssembly.GetType('WzComparerR2.Avatar.Export.UnityAvatarExporter', $true)
$writerType = $main.GetType('WzComparerR2.UnityExport.UnityExportWriter', $true)
$manifestType = $writerType.GetProperty('Manifest').PropertyType
$nativeExporter = $main.GetType('WzComparerR2.UnityExport.UnityEntityExporter', $true)
$resolve = $nativeExporter.GetMethod('ResolveUol')
$readTrack = $nativeExporter.GetMethod('ReadTrack')
$findMethod = $plugin.GetType('WzComparerR2.PluginBase.PluginManager', $true).GetMethods() | Where-Object { $_.Name -eq 'FindWz' -and $_.GetParameters().Count -eq 2 -and $_.GetParameters()[0].ParameterType -eq [string] } | Select-Object -First 1
$find = $findMethod.CreateDelegate($resolve.GetParameters()[1].ParameterType)
$freezeFace = $reader.GetType('KmsAvatarExport', $true).GetMethod('FreezeFace', $flags)

function Add-Meta($List, [string]$Key, [string]$Value) {
    $entry = [Activator]::CreateInstance($List.GetType().GetGenericArguments()[0])
    $entry.key = $Key; $entry.value = $Value; $List.Add($entry)
}

function Read-Manifest([string]$Directory) {
    return [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText([IO.Path]::Combine($Directory, 'wz-unity.json')), $manifestType)
}

function Original-Png([string]$Directory, $Asset) {
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($Directory, $Asset.file))
    if (![IO.File]::Exists($path)) { $path = [IO.Path]::Combine($Directory, 'Textures', $Asset.id + '.png') }
    if (![IO.File]::Exists($path)) { throw 'Original PNG is unavailable.' }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path))).ToLowerInvariant() -cne $Asset.id) { throw 'Original PNG checksum mismatch.' }
    return $path
}

function Restore-Avatar($Entity) {
    $values = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Entity.metadata) { if (!$values.TryAdd($entry.key, $entry.value)) { throw 'Duplicate original outfit metadata.' } }
    $restored = [Activator]::CreateInstance($avatarType)
    foreach ($source in $Entity.metadata | Where-Object { $_.key -match '^parts/\d+/source$' }) {
        $slot = [int]$source.key.Split('/')[1]
        if ($slot -lt 0 -or $slot -ge $restored.Parts.Length) { throw 'Invalid original part slot.' }
        $node = $data.Find($source.value)
        if (!$node) { throw 'Original equipped item is missing from Base.wz.' }
        $part = [Activator]::CreateInstance($partType, @($node))
        $prefix = 'parts/' + $slot + '/'
        if ([string]$part.ID -cne $values[$prefix + 'itemId'] -or $part.Node.FullPathToFile -cne $source.value) { throw 'Original equipped part identity mismatch.' }
        $part.Visible = [bool]::Parse($values[$prefix + 'visible'])
        $part.EffectVisible = [bool]::Parse($values[$prefix + 'effectVisible'])
        $part.MixColor = [int]$values[$prefix + 'mixColor']; $part.MixOpacity = [int]$values[$prefix + 'mixOpacity']
        $part.CustomOriginMap.Clear()
        foreach ($entry in $Entity.metadata | Where-Object { $_.key.StartsWith($prefix + 'customOriginMap/', [StringComparison]::Ordinal) }) {
            $part.CustomOriginMap.Add($entry.key.Substring(($prefix + 'customOriginMap/').Length), $entry.value)
        }
        foreach ($kind in [Enum]::GetValues($prismType)) {
            $key = $prefix + 'prism/' + $kind + '/'
            $part.PrismData.Set($kind, [int]$values[$key + 'type'], [int]$values[$key + 'hue'], [int]$values[$key + 'saturation'], [int]$values[$key + 'brightness'], [bool]::Parse($values[$key + 'convertPureBlack']))
        }
        $restored.Parts[$slot] = $part
    }
    foreach ($mapping in @(@('HairCover','hairCover'), @('HideBody','hideBody'), @('ShowWeaponEffect','showWeaponEffect'), @('ShowWeaponJumpEffect','showWeaponJumpEffect'), @('ShowHairShade','showHairShade'), @('ApplyBRM','applyBodyRelativeMove'))) {
        $restored.($mapping[0]) = [bool]::Parse($values[$mapping[1]])
    }
    foreach ($mapping in @(@('EarType','earType'), @('WeaponType','weaponType'), @('WeaponIndex','weaponIndex'))) { $restored.($mapping[0]) = [int]$values[$mapping[1]] }
    $restored.CapType = $values['capType']; $restored.GroupChair = $values['groupChair']
    $restored.ActionName = $Entity.defaultAction; $restored.EmotionName = $values['emotion']
    foreach ($entry in $Entity.metadata | Where-Object { $_.key.StartsWith('customOrigin/', [StringComparison]::Ordinal) }) {
        $point = $entry.value.Split(',')
        if ($point.Length -ne 2) { throw 'Malformed original custom origin.' }
        $restored.CustomOrigin.Add($entry.key.Substring('customOrigin/'.Length), [Drawing.Point]::new([int]$point[0], [int]$point[1]))
    }
    if (!$restored.Body -or !$restored.Head -or !$restored.LoadZ($data.Find('Base/zmap.img')) -or !$restored.LoadActions()) { throw 'Original body/action catalog is unavailable.' }
    $null = $restored.LoadEmotions(); $restored.LoadAllEffects()
    return $restored
}

function Assert-OldPreserved($Before, $After, [int]$Count, [string[]]$ChangedActions = @()) {
    if ($Before.id -cne $After.id -or $Before.defaultAction -cne $After.defaultAction -or $Before.displayName -cne $After.displayName) { throw 'Original character identity/default action changed.' }
    foreach ($property in @('equipment','metadata')) {
        if ([Newtonsoft.Json.JsonConvert]::SerializeObject($Before.$property) -cne [Newtonsoft.Json.JsonConvert]::SerializeObject($After.$property)) { throw 'Original outfit/face metadata changed.' }
    }
    for ($index = 0; $index -lt $Count; $index++) {
        if ($Before.clips[$index].name -cin $ChangedActions) { continue }
        if ([Newtonsoft.Json.JsonConvert]::SerializeObject($Before.clips[$index]) -cne [Newtonsoft.Json.JsonConvert]::SerializeObject($After.clips[$index])) { throw 'Existing source animation changed.' }
    }
}

function Read-StoredOutfit([string]$Path, [string]$Name) {
    # Read only the exported plain scalar metadata header, never Unity scene,
    # prefab references or runtime state. Refuse formats that need YAML guesses.
    $text = [IO.File]::ReadAllText([IO.Path]::GetFullPath($Path))
    $id = [Text.RegularExpressions.Regex]::Match($text, '(?m)^  sourceId: (avatar-[a-f0-9]{24})\r?$')
    $source = [Text.RegularExpressions.Regex]::Match($text, '(?m)^  sourcePath: (Character\\[^\r\n]+)\r?$')
    $default = [Text.RegularExpressions.Regex]::Match($text, '(?m)^  defaultAction: ([a-zA-Z0-9_]+)\r?$')
    $header = [Text.RegularExpressions.Regex]::Match($text, '(?ms)^  metadata:\r?\n(?<entries>.*?)^  actions:')
    if (!$id.Success -or !$source.Success -or !$default.Success -or !$header.Success -or [string]::IsNullOrWhiteSpace($Name)) { throw 'Stored native outfit metadata header is unavailable.' }
    if ($Name -in @('.','..') -or $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid stored character output directory name.' }
    $entityType = $manifestType.GetField('entities').FieldType.GetGenericArguments()[0]
    $entity = [Activator]::CreateInstance($entityType)
    $entity.id = $id.Groups[1].Value; $entity.sourcePath = $source.Groups[1].Value; $entity.defaultAction = $default.Groups[1].Value
    $entity.kind = 'avatar'; $entity.displayName = $Name
    $entries = $header.Groups['entries'].Value
    $pattern = '(?m)^  - key: ([^\r\n]+)\r?\n    value: ([^\r\n]*)\r?\n'
    foreach ($match in [Text.RegularExpressions.Regex]::Matches($entries, $pattern)) {
        $key = $match.Groups[1].Value; $value = $match.Groups[2].Value
        if ($value.StartsWith("'", [StringComparison]::Ordinal) -and $value.EndsWith("'", [StringComparison]::Ordinal)) { $value = $value.Substring(1, $value.Length - 2).Replace("''", "'") }
        elseif ($value.StartsWith('"', [StringComparison]::Ordinal) -or $value -match '^\s*[\[\{\|>]') { throw 'Only exported plain/single-quoted scalar metadata is supported.' }
        Add-Meta $entity.metadata $key $value
    }
    if (![string]::IsNullOrWhiteSpace([Text.RegularExpressions.Regex]::Replace($entries, $pattern, '')) -or $entity.metadata.Count -eq 0) { throw 'Stored metadata contains unsupported or incomplete entries.' }
    return $entity
}

try {
    $data = [Activator]::CreateInstance($reader.GetType('DataSource', $true), @([IO.Path]::GetFullPath($BaseWzPath)))
    if ($InspectOnly) {
    foreach ($path in @('Character/00002042.img', 'Character/00012042.img', 'Character/Face/00028017.img', 'Character/Ring/01116126.img')) {
        $node = $data.Find($path)
        if (!$node) { Write-Output ('MISSING ' + $path); continue }
        foreach ($action in @('walk1', 'walk2', 'move', 'walk', 'jump', 'dead', 'die')) {
            $actionNode = $node.Nodes[$action]
            if (!$actionNode) { Write-Output ('ACTION_MISSING ' + $path + '/' + $action); continue }
            [pscustomobject]@{ path = $path + '/' + $action; frames = @($actionNode.Nodes | Where-Object { $_.Text -match '^\d+$' } | ForEach-Object {
                [pscustomobject]@{ index = $_.Text; valueType = $(if ($_.Value) { $_.Value.GetType().Name } else { 'node' }); fields = @($_.Nodes | ForEach-Object {
                    [pscustomobject]@{ name = $_.Text; value = $(if ($null -eq $_.Value) { '' } elseif ($_.Value -is [string] -or $_.Value.GetType().IsPrimitive) { $_.Value } else { $_.Value.GetType().Name }) }
                }) }
            }) } | ConvertTo-Json -Depth 8 -Compress
        }
    }
    $source = $resolve.Invoke($null, @($data.Find('Character/00002042.img/dead/0/body'), $find))
    $source = [WzComparerR2.Common.Wz_NodeExtension2]::GetLinkedSourceNode($source, $find, $null)
    $bitmap = $source.Value.ExtractPng()
    $previewFolder = [IO.Path]::Combine($repoRoot, '.tmp/travel-motions-validation')
    $null = [IO.Directory]::CreateDirectory($previewFolder)
    try { $bitmap.Save([IO.Path]::Combine($previewFolder, 'original-dead-body.png'), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
    Write-Output ('DEAD_CANVAS ' + $source.FullPathToFile)
    return
    }
    if (($NativeBundleDirectory.Count -eq 0 -and [string]::IsNullOrWhiteSpace($StoredAnimationSetPath)) -or [string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'Specify existing native bundle directories or stored native metadata and a fresh output directory.' }
    $outputRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
    if ([IO.Directory]::Exists($outputRoot)) { throw 'Use a fresh output directory; original bundles are never modified.' }
    $null = [IO.Directory]::CreateDirectory($outputRoot)
    foreach ($inputDirectory in $NativeBundleDirectory) {
        $input = [IO.Path]::GetFullPath($inputDirectory)
        $original = Read-Manifest $input
        if ($original.entities.Count -ne 1 -or $original.kind -ne 'avatar' -or !$original.entities[0].hasEquipmentMetadata) { throw 'One native avatar with original outfit metadata is required.' }
        $before = $original.entities[0]
        if ([string]::IsNullOrWhiteSpace($before.displayName) -or $before.displayName -in @('.','..') -or $before.displayName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid character directory name.' }
        if ([string]::IsNullOrWhiteSpace($original.id) -or $original.id -in @('.','..') -or $original.id.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid original bundle identity.' }
        $characterRoot = [IO.Path]::Combine($outputRoot, $before.displayName)
        $output = [IO.Path]::Combine($characterRoot, $original.id)
        $entity = [Newtonsoft.Json.JsonConvert]::DeserializeObject([Newtonsoft.Json.JsonConvert]::SerializeObject($before), $before.GetType())
        $oldCount = $entity.clips.Count
        $writer = [Activator]::CreateInstance($writerType, @($output, $original.id, $original.kind, $original.sourcePath, [Threading.CancellationToken]::None))
        $writer.Manifest.pixelsPerUnit = $original.pixelsPerUnit
        foreach ($asset in $original.assets) { if ($writer.AddPngFile((Original-Png $input $asset)) -cne $asset.id) { throw 'Existing PNG changed.' } }
        foreach ($warning in $original.warnings) { $writer.Warn($warning.sourcePath, $warning.reason) }
        $added = @(); $missing = @(); $appearanceMatched = $false
        $isRing = @($entity.metadata | Where-Object { $_.key -eq 'rendering/mode' -and $_.value -eq 'illusion-ring' }).Count -eq 1
        if ($DeathOnly -and ($isRing -or $entity.displayName -cne '깽쿤')) { throw 'Death-only correction requires the original Kkaengkun native bundle.' }
        if ($isRing) {
            $rings = @($entity.equipment | Where-Object { $_.illusionRingClassificationKnown -and $_.isIllusionRing -and !$_.isSkill })
            if ($rings.Count -ne 1) { throw 'One verified original illusion ring is required.' }
            $ring = $rings[0]; $ringNode = $data.Find($ring.sourcePath)
            if (!$ringNode -or $ringNode.FullPathToFile -cne $entity.sourcePath -or !$ringNode.FindNodeByPath('info\illusionGrade')) { throw 'Original illusion ring provenance mismatch.' }
            foreach ($action in @('walk1','walk2','move','walk','jump')) {
                $actionNode = $resolve.Invoke($null, @($ringNode.Nodes[$action], $find))
                if (!$actionNode) { $missing += $action; continue }
                if (@($entity.clips | Where-Object name -eq $action).Count -gt 0) { continue }
                $repeat = $resolve.Invoke($null, @($actionNode.Nodes['repeat'], $find))
                $loop = !$repeat -or [int]$repeat.Value -ne 0
                $track = $readTrack.Invoke($null, @($actionNode, ('illusion-ring/' + $ring.slotIndex), $writer, $find, $loop))
                if ($track.frames.Count -eq 0) { throw 'Original ring motion is empty.' }
                $track.kind = 'body'; $track.slot = $ring.slot; $track.itemId = $ring.itemId
                Add-Meta $track.metadata 'sourceAction' $action; Add-Meta $track.metadata 'source' $actionNode.FullPathToFile
                for ($index = 0; $index -lt $track.frames.Count; $index++) {
                    $frame = $track.frames[$index]
                    $frameNode = $resolve.Invoke($null, @($data.Find($frame.sourcePath), $find))
                    if (!$frameNode.Nodes['delay'] -and $actionNode.Nodes['delay']) { $delay = [int]$actionNode.Nodes['delay'].Value; $frame.delayMs = $(if ($delay -eq 0) { 120 } else { [Math]::Abs([double]$delay) }) }
                    Add-Meta $track.metadata ('frame/' + $index + '/source') $frame.sourcePath
                }
                $clip = [Activator]::CreateInstance($entity.clips.GetType().GetGenericArguments()[0])
                $clip.name = $action; $clip.loop = $loop; $clip.durationMs = [double](($track.frames | Measure-Object delayMs -Sum).Sum)
                $clip.tracks.Add($track); $entity.clips.Add($clip); $added += $action
            }
            $appearanceMatched = $true # Original equipped ring ID/source and untouched full outfit verified.
        } else {
            $avatar = Restore-Avatar $entity
            $primary = [Activator]::CreateInstance($exporterType, @($avatar, $null))
            if ($primary.AppearanceId -cne $original.id) { throw 'Restored original outfit identity mismatch.' }
            $appearanceMatched = $true
            $actions = if ($DeathOnly) { @() } else { @('walk1','walk2','jump') }
            $actions = @($actions | Where-Object { if ($avatar.GetActionFrames($_).Length -eq 0) { $missing += $_; $false } else { $true } })
            $baseEmotion = $avatar.EmotionName
            $fixed = @($entity.metadata | Where-Object key -eq 'face/fixedFrame')
            foreach ($emotion in @($baseEmotion, 'blink') | Select-Object -Unique) {
                $avatar.EmotionName = $emotion
                $exportActions = if ($emotion -eq $baseEmotion) { $actions } else { @($actions | Where-Object { $_ -ne 'dead' }) }
                if ($exportActions.Count -eq 0) { continue }
                $folder = [IO.Path]::Combine($characterRoot, '.source-variants', $emotion)
                $variantExporter = [Activator]::CreateInstance($exporterType, @($avatar, $null))
                $variant = $variantExporter.Export($folder, [string[]]$exportActions, [Threading.CancellationToken]::None, $null)
                if ($emotion -eq $baseEmotion -and $fixed.Count -eq 1) { foreach ($clip in $variant.entities[0].clips) { $null = $freezeFace.Invoke($null, @($clip, [int]$fixed[0].value)) } }
                foreach ($asset in $variant.assets) { if ($writer.AddPngFile((Original-Png $folder $asset)) -cne $asset.id) { throw 'New motion PNG changed.' } }
                foreach ($warning in $variant.warnings) { $writer.Warn($warning.sourcePath, $warning.reason) }
                foreach ($clip in $variant.entities[0].clips) {
                    if ($emotion -ne $baseEmotion) { $clip.name += '_blink' }
                    if (@($entity.clips | Where-Object name -eq $clip.name).Count -gt 0) { throw 'New motion would overwrite an existing action.' }
                    $entity.clips.Add($clip); $added += $clip.name
                }
            }
            if ($entity.displayName -ceq '깽쿤') {
                # dead/0/body is the ghost BODY, with an authored neck anchor.
                # Its face=1 requests the equipped front head, default face and
                # hair/cap attachments. Use the native bone renderer, including
                # original mix/prism options, rather than dropping that head.
                $avatar.EmotionName = $baseEmotion
                $deadFolder = [IO.Path]::Combine($characterRoot, '.source-variants', 'dead-with-equipped-head')
                $deadVariant = [Activator]::CreateInstance($exporterType, @($avatar, $null)).Export($deadFolder, [string[]]@('dead'), [Threading.CancellationToken]::None, $null)
                foreach ($asset in $deadVariant.assets) { if ($writer.AddPngFile((Original-Png $deadFolder $asset)) -cne $asset.id) { throw 'Original dead attachment PNG changed.' } }
                $dead = $deadVariant.entities[0].clips[0]
                if ($fixed.Count -eq 1) { $null = $freezeFace.Invoke($null, @($dead, [int]$fixed[0].value)) }
                $oldDead = @($entity.clips | Where-Object name -CEQ 'dead')
                if ($oldDead.Count -gt 1 -or (!$DeathOnly -and $oldDead.Count -gt 0)) { throw 'Existing dead action requires an explicit death-only correction.' }
                if ($DeathOnly -and $oldDead.Count -ne 1) { throw 'Death-only correction requires exactly one existing dead action.' }
                if ($oldDead.Count -eq 1) { $entity.clips[$entity.clips.IndexOf($oldDead[0])] = $dead } else { $entity.clips.Add($dead) }
                $added += 'dead'
                $writePreview = $reader.GetType('KmsAvatarExport', $true).GetMethod('WritePreview', $flags)
                $previewRows = [Activator]::CreateInstance($writePreview.GetParameters()[6].ParameterType)
                $previewFolder = [IO.Path]::Combine($characterRoot, 'previews')
                $null = [IO.Directory]::CreateDirectory($previewFolder)
                $null = $writePreview.Invoke($null, @($avatar.PSObject.BaseObject, 'dead', $baseEmotion, 0, 'dead-with-equipped-head.png', $previewFolder, $previewRows))
                [IO.File]::WriteAllText([IO.Path]::Combine($previewFolder, 'dead-original-frame.json'), [Newtonsoft.Json.JsonConvert]::SerializeObject($previewRows, [Newtonsoft.Json.Formatting]::Indented), [Text.UTF8Encoding]::new($false))
            }
            $avatar.ClearSkinCache(); $avatar = $null
        }
        Assert-OldPreserved $before $entity $oldCount $(if ($DeathOnly) { [string[]]@('dead') } else { [string[]]@() })
        if ($entity.displayName -ceq '깽쿤') {
            $dead = @($entity.clips | Where-Object name -eq 'dead')
            $body = @($dead[0].tracks | Where-Object id -CEQ 'part/0/body/0')
            $head = @($dead[0].tracks | Where-Object id -CEQ 'part/1/head/0')
            $hair = @($dead[0].tracks | Where-Object { $_.id.StartsWith('part/3/hair', [StringComparison]::Ordinal) })
            $face = @($dead[0].tracks | Where-Object id -CEQ 'face/2/face/0')
            if ($dead.Count -ne 1 -or $dead[0].loop -or $body.Count -ne 1 -or $head.Count -ne 1 -or $hair.Count -eq 0 -or $face.Count -ne 1 -or $body[0].frames.Count -ne 1 -or !$body[0].frames[0].sourcePath.EndsWith('/dead/0/body'.Replace('/','\'), [StringComparison]::Ordinal)) { throw 'Original non-looping ghost with equipped head/hair/face is missing.' }
        }
        $writer.Manifest.entities.Add($entity); $null = $writer.Commit()
        $map = [IO.Path]::Combine($input, 'face-variant-map.json')
        if ([IO.File]::Exists($map)) { [IO.File]::Copy($map, [IO.Path]::Combine($output, 'face-variant-map.json'), $false) }
        foreach ($asset in $writer.Manifest.assets) { $null = Original-Png $output $asset }
        $result = [ordered]@{ name = $entity.displayName; id = $entity.id; input = $input; output = $output; renderingMode = $(if ($isRing) { 'illusion-ring' } else { 'equipment-layers' }); added = @($added); replaced = $(if ($DeathOnly) { @('dead') } else { @() }); missingNativeActions = @($missing); originalAppearanceMatched = $appearanceMatched; existingClipsUnchanged = !$DeathOnly; existingOtherClipsUnchanged = $true; outfitMetadataUnchanged = $true; originalAssetsUnchanged = $true; existingClipCount = $oldCount; clipCount = $entity.clips.Count; assetCount = $writer.Manifest.assets.Count }
        [IO.File]::WriteAllText([IO.Path]::Combine($output, 'travel-motion-provenance.json'), (ConvertTo-Json -InputObject $result -Depth 7), [Text.UTF8Encoding]::new($false))
        $results += $result
        [IO.File]::WriteAllText([IO.Path]::Combine($outputRoot, 'travel-motion-results.json'), (ConvertTo-Json -InputObject @($results) -Depth 7), [Text.UTF8Encoding]::new($false))
        $writer.Dispose(); $writer = $null
        Write-Output ('TRAVEL_MOTIONS_EXPORTED name=' + $entity.displayName + ' added=' + [string]::Join(',', $added) + ' clips=' + $entity.clips.Count + ' output=' + $output)
    }
    if (![string]::IsNullOrWhiteSpace($StoredAnimationSetPath)) {
        $stored = Read-StoredOutfit $StoredAnimationSetPath $StoredCharacterName
        $avatar = Restore-Avatar $stored
        $primary = [Activator]::CreateInstance($exporterType, @($avatar, $null))
        if ($primary.AppearanceId -cne $stored.id) { throw 'Stored native outfit identity mismatch.' }
        $actions = @($StoredActions | Select-Object -Unique)
        if ($actions.Count -eq 0 -or @($actions | Where-Object { $_ -notin @('walk1','walk2','jump') -or $avatar.GetActionFrames($_).Length -eq 0 }).Count -gt 0) { throw 'Stored outfit requested native motion is unavailable.' }
        $output = [IO.Path]::Combine($outputRoot, $stored.displayName, $stored.id)
        $exported = $primary.Export($output, [string[]]$actions, [Threading.CancellationToken]::None, $null)
        $entity = $exported.entities[0]; $entity.displayName = $stored.displayName
        $jsonPath = [IO.Path]::Combine($output, 'wz-unity.json')
        [IO.File]::WriteAllText($jsonPath, [Newtonsoft.Json.JsonConvert]::SerializeObject($exported, [Newtonsoft.Json.Formatting]::Indented), [Text.UTF8Encoding]::new($false))
        foreach ($asset in $exported.assets) { $null = Original-Png $output $asset }
        $result = [ordered]@{ name = $stored.displayName; id = $stored.id; input = [IO.Path]::GetFullPath($StoredAnimationSetPath); output = $output; added = @($actions); originalAppearanceMatched = $true; existingAnimationSetEdited = $false; importMode = 'append only requested actions to existing custom-location AnimationSet; never replace or rebind the actor'; clipCount = $entity.clips.Count; assetCount = $exported.assets.Count; emotion = $avatar.EmotionName }
        [IO.File]::WriteAllText([IO.Path]::Combine($output, 'travel-motion-provenance.json'), (ConvertTo-Json -InputObject $result -Depth 7), [Text.UTF8Encoding]::new($false))
        $results += $result
        [IO.File]::WriteAllText([IO.Path]::Combine($outputRoot, 'travel-motion-results.json'), (ConvertTo-Json -InputObject @($results) -Depth 7), [Text.UTF8Encoding]::new($false))
        Write-Output ('STORED_NATIVE_MOTION_EXPORTED name=' + $entity.displayName + ' added=' + [string]::Join(',', $actions) + ' output=' + $output)
        $avatar.ClearSkinCache(); $avatar = $null
    }
} finally {
    if ($writer) { $writer.Dispose() }
    if ($avatar) { $avatar.ClearSkinCache() }
    if ($data) { $data.Dispose() }
}
