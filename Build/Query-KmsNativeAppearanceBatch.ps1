[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateCount(1, 512)][string[]]$CharacterNames,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$SettingsPath = [IO.Path]::Combine($PSScriptRoot, '../WzComparerR2/bin/Release/net8.0-windows/Setting.config'),
    [string]$ReaderAssemblyPath = [IO.Path]::Combine($PSScriptRoot, '../Tests/UnityExportSmoke/bin/Release/net8.0-windows/UnityExportSmoke.dll'),
    [ValidateRange(500, 60000)][int]$DelayMilliseconds = 650
)

# QueryAppearance inside the existing DLL owns settings/key access. This script
# receives only decoded public avatar data, never a key, header or raw response.
# No build, original WZ load, Unity launch or alternate-name lookup is performed.
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
if ([IO.Directory]::Exists($taskRoot) -and @([IO.Directory]::EnumerateFileSystemEntries($taskRoot)).Count -gt 0) {
    throw 'Choose a fresh empty batch cache directory.'
}
$names = @()
$unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($inputName in $CharacterNames) {
    $name = $inputName.Trim()
    if (!$name -or $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or !$unique.Add($name)) { throw 'Character names must be nonempty, unique and valid filenames.' }
    $names += $name
}
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
[Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
$reader = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($ReaderAssemblyPath))
$query = $reader.GetType('KmsAvatarExport', $true).GetMethod('QueryAppearance', [Reflection.BindingFlags]'NonPublic,Public,Static')
$settingsFile = [IO.Path]::GetFullPath($SettingsPath)
$rows = @()
for ($index = 0; $index -lt $names.Count; $index++) {
    $name = $names[$index]
    $stage = 'lookup'
    $row = [ordered]@{ index = $index + 1; name = $name; status = 'lookupFailed'; stage = $stage }
    try {
        $appearance = $query.Invoke($null, @($name, $settingsFile))
        $stage = 'appearance-validation'
        $skinNumber = 0
        if ($null -eq $appearance -or $appearance.UnknownVer -or
            ![int]::TryParse([string]$appearance.Skin, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$skinNumber) -or $skinNumber -lt 0 -or $skinNumber -gt 99) { throw 'Unsupported original appearance/skin.' }
        $cacheName = '{0:D2}-{1}.appearance.json' -f ($index + 1), $name
        $cache = [Newtonsoft.Json.JsonConvert]::SerializeObject($appearance.PSObject.BaseObject, [Newtonsoft.Json.Formatting]::Indented)
        [IO.File]::WriteAllText([IO.Path]::Combine($taskRoot, $cacheName), $cache, [Text.UTF8Encoding]::new($false))
        $row.status = 'available'; $row.stage = 'complete'; $row.version = $appearance.Version; $row.skin = $appearance.Skin; $row.cacheFile = $cacheName
        Write-Output ('KMS_LOOKUP_AVAILABLE index=' + ($index + 1) + ' name=' + $name + ' version=' + $appearance.Version)
    } catch {
        # Do not echo exceptions, inner exceptions, XML, headers or settings data.
        $row.stage = $stage
        Write-Output ('KMS_LOOKUP_FAILED index=' + ($index + 1) + ' name=' + $name + ' stage=' + $stage)
    }
    $rows += $row
    [IO.File]::WriteAllText([IO.Path]::Combine($taskRoot, 'lookup-results.json'), (ConvertTo-Json -InputObject @($rows) -Depth 5), [Text.UTF8Encoding]::new($false))
    if ($index -lt $names.Count - 1) { Start-Sleep -Milliseconds $DelayMilliseconds }
}
Write-Output ('KMS_LOOKUP_BATCH_DONE total=' + $rows.Count + ' available=' + @($rows | Where-Object { $_.status -eq 'available' }).Count + ' failed=' + @($rows | Where-Object { $_.status -ne 'available' }).Count)
