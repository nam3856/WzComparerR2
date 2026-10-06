[CmdletBinding()]
param(
    [string]$WzLibPath = (Join-Path $PSScriptRoot '..\WzComparerR2\bin\Release\net462\WzComparerR2.WzLib.dll'),
    [string]$DataRoot = 'D:\Nexon\Maple\Data',
    [string]$OutputDirectory = 'D:\Github\MapleLive\Assets\LiveChat\Textures'
)

# Uses an existing compiled reader only. This helper never builds or starts the WzComparer UI.
$ErrorActionPreference = 'Stop'
$libraryPath = (Resolve-Path -LiteralPath $WzLibPath).Path
$dataPath = (Resolve-Path -LiteralPath $DataRoot).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($libraryPath)
$specifications = @(
    @{ Group = 'Map'; LogicalPath = 'MapHelper.img\minimap\arrowdown'; Name = 'HighlightArrow' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\nw'; Name = 'BubbleNW' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\n'; Name = 'BubbleN' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\ne'; Name = 'BubbleNE' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\w'; Name = 'BubbleW' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\c'; Name = 'BubbleC' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\e'; Name = 'BubbleE' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\sw'; Name = 'BubbleSW' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\s'; Name = 'BubbleS' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\se'; Name = 'BubbleSE' },
    @{ Group = 'UI'; LogicalPath = 'ChatBalloon.img\0\arrow'; Name = 'BubbleTail' }
)
$records = @()
foreach ($group in ($specifications | Group-Object Group)) {
    $structure = [Activator]::CreateInstance($assembly.GetType('WzComparerR2.WzLib.Wz_Structure'))
    try {
        $rootNode = $null
        $structure.LoadWzFolder((Join-Path $dataPath $group.Name), [ref]$rootNode, $false, $null)
        foreach ($spec in $group.Group) {
            $logicalNode = $rootNode.FindNodeByPath($spec.LogicalPath, $true)
            if ($null -eq $logicalNode -or $logicalNode.Value -isnot [WzComparerR2.WzLib.Wz_Png]) {
                throw "Original PNG node unavailable: $($group.Name)/$($spec.LogicalPath)"
            }
            $resolvedNode = $logicalNode
            $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            for ($links = 0; $links -lt 16; $links++) {
                if (!$visited.Add($resolvedNode.FullPathToFile)) { throw 'Cyclic canvas link' }
                $outlink = $resolvedNode.FindNodeByPath('_outlink')
                $inlink = $resolvedNode.FindNodeByPath('_inlink')
                if ($outlink) {
                    $linkPath = ([string]$outlink.Value).Replace('/', '\')
                    $separator = $linkPath.IndexOf('\')
                    if ($separator -lt 0 -or $linkPath.Substring(0, $separator) -ne $group.Name) {
                        throw "Unexpected cross-group canvas link: $linkPath"
                    }
                    $resolvedNode = $rootNode.FindNodeByPath($linkPath.Substring($separator + 1), $true)
                } elseif ($inlink) {
                    $resolvedNode = $resolvedNode.Value.WzImage.Node.FindNodeByPath(([string]$inlink.Value).Replace('/', '\'), $true)
                } else { break }
                if ($null -eq $resolvedNode -or $resolvedNode.Value -isnot [WzComparerR2.WzLib.Wz_Png]) {
                    throw 'Canvas link does not resolve to a PNG'
                }
                if ($links -eq 15) { throw 'Canvas link depth exceeded' }
            }
            $destination = Join-Path $outputPath ($spec.Name + '.png')
            $bitmap = $resolvedNode.Value.ExtractPng()
            $pixelWidth = $bitmap.Width
            $pixelHeight = $bitmap.Height
            try { $bitmap.Save($destination, [Drawing.Imaging.ImageFormat]::Png) }
            finally { $bitmap.Dispose() }
            $originNode = $logicalNode.FindNodeByPath('origin')
            $sourcePath = $resolvedNode.Value.WzFile.FileStream.Name
            $sourceInfo = Get-Item -LiteralPath $sourcePath
            $records += [ordered]@{
                name = $spec.Name; file = $spec.Name + '.png'
                logicalPath = $logicalNode.FullPathToFile.Replace('\', '/')
                resolvedPath = $resolvedNode.FullPathToFile.Replace('\', '/')
                sourceWz = $sourcePath; sourceSize = $sourceInfo.Length
                sourceModifiedUtc = $sourceInfo.LastWriteTimeUtc.ToString('O')
                width = $pixelWidth; height = $pixelHeight
                origin = @{ x = $(if ($originNode) { $originNode.Value.X } else { 0 }); y = $(if ($originNode) { $originNode.Value.Y } else { 0 }) }
                originalOriginPresent = $null -ne $originNode
                pngSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    } finally { $structure.Clear() }
}
$manifest = [ordered]@{
    schemaVersion = 1; sourceDataRoot = $dataPath; readerAssembly = $libraryPath
    pixelsPerUnit = 100; textColorArgb = -16777216
    notes = 'Original WZ pixels decoded without resizing or recoloring. Logical origin is retained separately from linked physical canvas.'
    assets = $records
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'wz-chat-assets.json') -Encoding UTF8
Write-Output "Extracted $($records.Count) original PNG assets and provenance to $outputPath"
