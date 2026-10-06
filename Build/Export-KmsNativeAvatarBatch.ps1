[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppearanceCacheDirectory,
    [string]$BaseWzPath = 'D:/Nexon/Maple/Data/Base/Base.wz',
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll')
)

# Reuses existing compiled reader/exporter binaries and decoded appearance cache.
# Does not build, query APIs, read API keys or write to Unity Assets. Each input
# receives its own output root so previews and same-outfit identities cannot mix.
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($AppearanceCacheDirectory))
$readerPath = [IO.Path]::GetFullPath($ReaderAssemblyPath)
$readerRoot = [IO.Path]::GetDirectoryName($readerPath)
$repoRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$reader = [Reflection.Assembly]::LoadFrom($readerPath)
$common = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($readerRoot, 'WzComparerR2.Common.dll'))
$flags = [Reflection.BindingFlags]'NonPublic,Public,Static'
$exporter = $reader.GetType('KmsAvatarExport', $true)
$null = $reader.GetType('Smoke', $true).GetMethod('SetDllDirectory', $flags).Invoke($null, @([IO.Path]::Combine($repoRoot, 'References/x64')))
$create = $exporter.GetMethod('CreateAvatar', $flags)
$export = $exporter.GetMethod('ExportVariants', $flags)
$writeFaceMap = $exporter.GetMethod('WriteFaceVariantMap', $flags)
$manifestType = $writeFaceMap.GetParameters()[0].ParameterType
$appearanceType = $common.GetType('WzComparerR2.OpenAPI.UnpackedAvatarData', $true)
$lookupRows = @(Get-Content -LiteralPath ([IO.Path]::Combine($taskRoot, 'lookup-results.json')) -Raw | ConvertFrom-Json)
$results = @()
$data = $null
$avatar = $null

function Add-Metadata($List, [string]$Key, [string]$Value) {
    $entry = [Activator]::CreateInstance($List.GetType().GetGenericArguments()[0])
    $entry.key = $Key; $entry.value = $Value
    $List.Add($entry)
}

function Repair-FixedFaceProvenance($Manifest) {
    $entity = $Manifest.entities[0]
    $fixed = @($entity.metadata | Where-Object { $_.key -eq 'face/fixedFrame' })
    if ($fixed.Count -eq 0) { return }
    $sourceFrame = [int]$fixed[0].value
    foreach ($clip in $entity.clips) {
        if ($clip.name.EndsWith('_blink', [StringComparison]::Ordinal)) { continue }
        foreach ($track in $clip.tracks) {
            if ($track.kind -ne 'face') { continue }
            $alreadyRemapped = @($track.metadata | Where-Object { $_.key -eq 'sourceFixedFrameIndex' })
            if ($alreadyRemapped.Count -gt 0) {
                if ($alreadyRemapped.Count -ne 1 -or [int]$alreadyRemapped[0].value -ne $sourceFrame) { throw 'Fixed face provenance marker mismatch.' }
                continue
            }
            $corrected = [Activator]::CreateInstance($track.metadata.GetType())
            foreach ($entry in $track.metadata) {
                if (!$entry.key.StartsWith('pose/', [StringComparison]::Ordinal)) {
                    if ($entry.key -ne 'sourceFixedFrameIndex') { $corrected.Add($entry) }
                    continue
                }
                $parts = $entry.key.Split('/')
                if ($parts.Length -lt 4 -or $parts[2] -ne $sourceFrame.ToString([Globalization.CultureInfo]::InvariantCulture)) { continue }
                $parts[2] = '0'
                Add-Metadata $corrected ([string]::Join('/', $parts)) $entry.value
            }
            Add-Metadata $corrected 'sourceFixedFrameIndex' $sourceFrame.ToString([Globalization.CultureInfo]::InvariantCulture)
            $track.metadata = $corrected
        }
    }
}

function Assert-Export($Manifest, [string]$Output, $Avatar, [string]$Name) {
    if ($Manifest.entities.Count -ne 1 -or $Manifest.entities[0].displayName -ne $Name) { throw 'Character identity mismatch.' }
    $entity = $Manifest.entities[0]
    $ringAppearance = @($entity.metadata | Where-Object { $_.key -eq 'rendering/mode' -and $_.value -eq 'illusion-ring' }).Count -eq 1
    $expectedActions = if ($ringAppearance) { @('stand1','stand2','sit','prone') } else { @('stand1','stand2','sit','prone','stand1_blink','stand2_blink','sit_blink','prone_blink') }
    if ($entity.clips.Count -ne $expectedActions.Count) { throw 'Default/blink action inventory mismatch.' }
    foreach ($action in $expectedActions) {
        if (@($entity.clips | Where-Object { $_.name -eq $action }).Count -ne 1) { throw 'Original action missing.' }
    }
    if (!$entity.hasEquipmentMetadata) { throw 'Equipment metadata missing.' }
    $originalParts = @($Avatar.Parts | Where-Object { $null -ne $_ -and $null -ne $_.ID })
    if ($entity.equipment.Count -ne $originalParts.Count) { throw 'Equipped part inventory mismatch.' }
    foreach ($item in $entity.equipment) {
        if ($item.slotIndex -lt 0 -or $item.slotIndex -ge 29 -or $item.itemId -ne $Avatar.Parts[$item.slotIndex].ID) { throw 'Original equipment slot/ID mismatch.' }
    }
    foreach ($asset in $Manifest.assets) {
        $file = [IO.Path]::Combine($Output, $asset.file)
        $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($file))).ToLowerInvariant()
        if ($sha -ne $asset.sha256 -or $sha -ne $asset.id) { throw 'PNG hash mismatch.' }
    }
    $faces = @(Get-Content -LiteralPath ([IO.Path]::Combine($Output, 'face-variant-map.json')) -Raw | ConvertFrom-Json)
    if ($ringAppearance -and ($faces.Count -gt 0 -or @($entity.clips.tracks | Where-Object kind -eq 'face').Count -gt 0)) { throw 'Complete ring sprites must not contain invented human face layers.' }
    if (!$ringAppearance -and ($faces.Count -eq 0 -or @($faces | Where-Object { !$_.sourcePath }).Count -gt 0)) { throw 'Original face provenance missing.' }
    foreach ($face in $faces) {
        if (![IO.File]::Exists([IO.Path]::Combine($Output, $face.file))) { throw 'Face PNG missing.' }
    }
}

try {
    $dataType = $reader.GetType('DataSource', $true)
    $data = [Activator]::CreateInstance($dataType, @([IO.Path]::GetFullPath($BaseWzPath)))
    $find = $dataType.GetMethod('Find').CreateDelegate($create.GetParameters()[1].ParameterType, $data)
    $strings = [Activator]::CreateInstance($common.GetType('WzComparerR2.Common.StringLinker', $true))
    $null = $strings.Update($data.Find('String'), $null, $null, $null, $null)
    foreach ($row in $lookupRows) {
        $stage = 'appearance-cache'
        $result = [ordered]@{ index = $row.index; name = $row.name; status = 'exportFailed'; stage = $stage }
        if ($row.status -ne 'available') { $result.status = 'lookupFailed'; $results += $result; continue }
        try {
            $cachePath = [IO.Path]::GetFullPath([IO.Path]::Combine($taskRoot, $row.cacheFile))
            if (![IO.Path]::GetDirectoryName($cachePath).Equals($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Appearance cache must remain inside the batch root.' }
            $cacheText = [IO.File]::ReadAllText($cachePath)
            $saved = $cacheText | ConvertFrom-Json
            $appearance = [Newtonsoft.Json.JsonConvert]::DeserializeObject($cacheText, $appearanceType)
            $appearance.SetProperties()
            foreach ($property in @('Skin','Face','Hair','Coat','Pants','Weapon','CashWeapon','Ring1','Ring2','Ring3','Ring4','EmotionFaceAcc','MixHairRatio','MixHairColor','MixFaceRatio','MixFaceColor')) {
                if ([string]$appearance.$property -cne [string]$saved.$property) { throw 'Decoded appearance roundtrip mismatch.' }
            }
            $skinNumber = 0
            if ($appearance.UnknownVer -or ![int]::TryParse([string]$appearance.Skin, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$skinNumber) -or $skinNumber -lt 0 -or $skinNumber -gt 99) { throw 'Unsupported original appearance/skin.' }
            $stage = 'original-parts'
            $avatar = $create.Invoke($null, @($appearance.PSObject.BaseObject, $find.PSObject.BaseObject))
            # Current source visibility correction applied without recompilation.
            if ($null -ne $avatar.Longcoat) {
                $avatar.Longcoat.Visible = $true
                if ($null -ne $avatar.Coat) { $avatar.Coat.Visible = $false }
                if ($null -ne $avatar.Pants) { $avatar.Pants.Visible = $false }
            }
            $stage = 'original-actions'
            if (!$avatar.LoadZ($data.Find('Base/zmap.img')) -or !$avatar.LoadActions()) { throw 'Original actions unavailable.' }
            $null = $avatar.LoadEmotions(); $avatar.LoadAllEffects()
            if ([string]::IsNullOrWhiteSpace($row.name) -or $row.name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Character name is not a valid directory name.' }
            $outputRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($taskRoot, ('{0:D2}-{1}' -f [int]$row.index, $row.name)))
            if (![IO.Path]::GetDirectoryName($outputRoot).Equals($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Character output must remain inside the batch root.' }
            if ([IO.Directory]::Exists($outputRoot)) { throw 'Use a fresh per-character output root.' }
            $stage = 'export-variants'
            $output = $export.Invoke($null, @($avatar.PSObject.BaseObject, $strings.PSObject.BaseObject, [string]$row.name, $appearance.PSObject.BaseObject, $outputRoot))
            $manifestPath = [IO.Path]::Combine($output, 'wz-unity.json')
            $manifest = [Newtonsoft.Json.JsonConvert]::DeserializeObject([IO.File]::ReadAllText($manifestPath), $manifestType)
            $stage = 'fixed-face-provenance'
            Repair-FixedFaceProvenance $manifest
            [IO.File]::WriteAllText($manifestPath, [Newtonsoft.Json.JsonConvert]::SerializeObject($manifest, [Newtonsoft.Json.Formatting]::Indented), [Text.UTF8Encoding]::new($false))
            $null = $writeFaceMap.Invoke($null, @($manifest.PSObject.BaseObject, [string]$output))
            $stage = 'validation'
            Assert-Export $manifest $output $avatar $row.name
            $previewFolder = [IO.Path]::Combine($outputRoot, 'previews')
            $previewRecords = @(Get-Content -LiteralPath ([IO.Path]::Combine($previewFolder, 'preview-origins.json')) -Raw | ConvertFrom-Json)
            $ringAppearance = @($manifest.entities[0].metadata | Where-Object { $_.key -eq 'rendering/mode' -and $_.value -eq 'illusion-ring' }).Count -eq 1
            foreach ($previewRecord in $previewRecords) {
                if (![IO.File]::Exists([IO.Path]::Combine($previewFolder, $previewRecord.file)) -or (!$ringAppearance -and !$previewRecord.faceSource) -or ($ringAppearance -and !$previewRecord.sourcePath)) { throw 'Original preview/provenance missing.' }
            }
            $entity = $manifest.entities[0]
            $result.status = 'exported'; $result.stage = 'complete'; $result.output = [string]$output
            $result.previews = $previewFolder; $result.clips = $entity.clips.Count; $result.assets = $manifest.assets.Count
            $result.equipment = $entity.equipment.Count; $result.warnings = $manifest.warnings.Count
            $result.renderingMode = if ($ringAppearance) { 'illusion-ring' } else { 'equipment-layers' }
            $result.fixedFaceFrame = @($entity.metadata | Where-Object { $_.key -eq 'face/fixedFrame' } | ForEach-Object { $_.value })
            $result.illusionRings = @($entity.equipment | Where-Object { $_.illusionRingClassificationKnown -and $_.isIllusionRing } | ForEach-Object { $_.itemId })
            Write-Output ('KMS_NATIVE_EXPORTED index=' + $row.index + ' name=' + $row.name + ' clips=' + $result.clips + ' assets=' + $result.assets + ' equipment=' + $result.equipment + ' warnings=' + $result.warnings + ' output=' + $output)
        } catch {
            # These diagnostics contain only a fixed stage; no raw exceptions.
            $result.stage = $stage
            Write-Output ('KMS_NATIVE_EXPORT_FAILED index=' + $row.index + ' name=' + $row.name + ' stage=' + $stage)
        } finally {
            if ($null -ne $avatar) { $avatar.ClearSkinCache(); $avatar = $null }
        }
        $results += $result
        [IO.File]::WriteAllText([IO.Path]::Combine($taskRoot, 'export-results.json'), (ConvertTo-Json -InputObject @($results) -Depth 6), [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText([IO.Path]::Combine($taskRoot, 'export-results.json'), (ConvertTo-Json -InputObject @($results) -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Output ('KMS_NATIVE_BATCH_DONE total=' + $results.Count + ' exported=' + @($results | Where-Object { $_.status -eq 'exported' }).Count + ' failed=' + @($results | Where-Object { $_.status -ne 'exported' }).Count)
} finally {
    if ($null -ne $avatar) { $avatar.ClearSkinCache() }
    if ($null -ne $data) { $data.Dispose() }
}
