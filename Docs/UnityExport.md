# Unity 프리팹·씬 추출

## 실행과 빌드

PowerShell에서 저장소의 `Build/Build-UnityExport.ps1`을 실행하면 .NET 8 Release 본체와 Avatar, MapRender, DB2, LuaConsole, Network 플러그인을 같은 출력 폴더에 빌드합니다.

실행 파일: `WzComparerR2/bin/Release/net8.0-windows/WzComparerR2.exe`

이 빌드는 .NET 8 Desktop Runtime이 필요합니다. 런타임을 포함한 Windows x64 게시물은 `.tmp/portable/WzComparerR2-win-x64`에 별도로 생성할 수 있습니다. 본체를 `dotnet publish WzComparerR2/WzComparerR2.csproj -c Release -f net8.0-windows -r win-x64 --self-contained true -o .tmp/portable/WzComparerR2-win-x64`로 게시한 뒤 Release 출력의 플러그인과 `Lib` 네이티브 의존성도 함께 포함합니다. 실행 파일만 옮기지 말고 게시물 폴더 전체를 사용합니다.

MapleT는 `E:/nexon/MapleT/Data/Base/Base.wz`를 엽니다. 최신 upstream의 KMST1205/1206 로더를 사용하며 Base 폴더와 Packs의 MS/MN 데이터를 함께 읽습니다. 개별 플러그인 프로젝트를 빌드할 때도 본체의 해당 구성/프레임워크 Plugin 폴더로 복사됩니다.

## 추출

- **캐릭터:** Avatar 창에서 코디를 구성한 다음 **캐릭터 Unity 추출**을 누릅니다. 선택한 상위 폴더 아래 외형 해시로 구분된 폴더를 만듭니다. 현재 장비·표정·가림·프리즘 결과와 부위별 동작을 추출합니다.
- **맵:** MapRender 창의 옵션에 있는 Unity 추출 버튼 또는 **Ctrl+E**를 사용합니다. 선택한 폴더 아래 `Maps/맵ID`에 저장합니다.
- **몬스터·NPC·리액터:** WZ 트리에서 해당 IMG나 하위 노드를 우클릭해 **몬스터·NPC·리액터 Unity 추출**을 선택합니다. 개체별 하위 폴더를 생성합니다.

각 폴더에는 `wz-unity.json`, `png/`, `export-report.txt`가 있습니다. 다른 프로그램을 실행할 필요가 없으며 AEP는 생성하지 않습니다.

캐릭터의 `entity.hasEquipmentMetadata=true`와 `entity.equipment`에는 비어 있지 않은 전체 장착 슬롯(최대 29개)이 포함됩니다. 렌더에 보이지 않는 반지·이미지가 없는 아이템도 유지합니다. 각 항목은 슬롯 번호·이름, 아이템 ID, 표시·이펙트 상태, 원본 WZ 경로, 읽을 수 있는 원본 `info` 값, StringLinker에서 찾은 아이템 이름과 이름의 출처를 담습니다. 찾지 못한 이름·ID를 추측하지 않습니다. 이전 추출의 `hasEquipmentMetadata=false`는 장착 정보가 없다는 의미이며 장비가 없다는 판정에 사용하지 않습니다. 스키마 버전은 1을 유지하므로 이전 추출도 계속 가져올 수 있습니다.

일루전링은 원본 반지의 `info/illusionGrade`가 정수 0 이상일 때 `isIllusionRing=true`, `illusionRingClassificationKnown=true`로 기록합니다. 원본 `info`를 정상적으로 읽었지만 값이 없으면 일반 반지로 확인하며, 읽기 실패·잘못된 값·해결되지 않는 링크는 판별 미확인으로 남깁니다. 외형·이름·`prone` 동작 유무로 추정하지 않습니다. Unity는 이 목록을 `WzAnimationSet.equipment`에 직렬화하여 재임포트·씬 재열기에도 보존합니다. MapleLive의 새 컨트롤러는 이 정보를 이용해 일루전링 잠수를 자동으로 엎드리기로 선택합니다.

확인된 일루전링을 장착한 캐릭터는 반지 원본의 완성 변신 PNG로 자동 추출합니다. 반지 슬롯의 UI 가시성 설정으로 변신을 끄지 않으며, 그 설정은 장착 메타데이터에 그대로 남깁니다. 여러 일루전링이 있으면 원본 우선순위를 추측하지 않고 중단합니다. 변신 이미지의 원점·투명도·프레임 지연을 보존하고, 사람이 입은 내부 장비를 다시 합성하거나 인간 얼굴을 덧붙이지 않습니다. `rendering/mode=illusion-ring`과 아이템 ID·슬롯·원본 경로가 적용 근거를 기록하며 전체 장착 목록과 기존 부위 정보도 유지합니다. 원본 `stand2`가 없으면 `stand1`의 동일 원본 이미지와 시간을 사용하고 `rendering/sourceActionAliases=stand2=stand1`을 명시합니다.

추출은 별도 임시 폴더에서 완성한 뒤 기존 결과를 교체합니다. 취소·실패하면 기존 결과를 유지합니다. 같은 ID의 추출 결과만 갱신하며, 추출기가 만들지 않은 파일도 보존합니다. 경고가 있으면 완료 메시지와 보고서에서 누락 원본 경로·사유를 확인할 수 있습니다.

## KMS 캐릭터 이름으로 CLI 추출

`Tests/UnityExportSmoke`의 `kms-avatar` 명령은 KMS 캐릭터 이름을 조회하고, 반환된 외형 코드를 설치된 원본 `Base.wz`의 부위·프리즘·혼합색·장비로 렌더합니다. 넥슨이 제공하는 캐릭터 미리보기 이미지를 잘라 쓰지 않습니다. `Setting.config` 파일 경로를 인수로 받으며, 키 값은 파일의 `WcR2/nexonOpenAPIKey`에서 읽습니다. 키 값과 설정 내용은 CLI 출력에 포함하지 않으며 조회 실패도 고정된 오류 메시지로 처리합니다.

이 명령은 GUI 실행 파일과 별개의 콘솔 도구입니다. 아래 예시는 최신 소스를 반영한 `Tests/UnityExportSmoke` Release 출력 DLL이 있을 때 실행합니다. 기존 DLL에는 이후 소스 수정이 포함되지 않으므로, 수정된 CLI를 사용하려면 다음 빌드 때 이 프로젝트를 다시 빌드해야 합니다. .NET 8 런타임이 있는 경우에는 `--roll-forward Major`를 생략할 수 있습니다.

저장소 루트의 PowerShell에서 실행하며, 출력 폴더는 실행마다 새 경로를 지정합니다. `previews`와 같은 외형의 결과가 서로 덮어써지지 않도록 이전 출력 폴더를 재사용하지 않습니다.

```powershell
$kmsOutputRoot = Join-Path '.tmp/kms-avatar-export' ([Guid]::NewGuid().ToString('N'))
dotnet --roll-forward Major .\Tests\UnityExportSmoke\bin\Release\net8.0-windows\UnityExportSmoke.dll kms-avatar `
  'D:\MapleData\Base\Base.wz' '캐릭터 이름' `
  '.\WzComparerR2\bin\Release\net8.0-windows\Setting.config' $kmsOutputRoot
```

결과는 출력 폴더 아래 외형 해시 폴더에 저장됩니다. 기본 표정과 원본 `blink`의 `stand1`·`stand2`·`sit`·`prone` 동작, 전체 장착 메타데이터를 하나의 `wz-unity.json`에 포함합니다. `face-variant-map.json`은 몸 포즈별 얼굴 이미지·좌표·원본 경로를 기록하고, `previews`의 PNG와 `preview-origins.json`은 원본 렌더 비교용입니다. `.kms-variants-*` 폴더는 합치기 전 추출의 근거 자료이며, Unity에는 최종 `wz-unity.json`이 있는 외형 해시 폴더만 가져옵니다. 지원하지 않는 외형 코드나 필요한 원본이 없으면 임의 이미지로 대체하지 않고 중단합니다.

일루전링 외형은 `stand1`·`stand2`·`sit`·`prone`의 4개 동작을 원본 반지에서 추출합니다. 별도 얼굴이 없는 완성 변신 이미지이므로 가상의 `_blink` 동작을 만들지 않으며 `face-variant-map.json`은 비어 있습니다. 프리뷰는 변신 원본 프레임 그대로이며 `preview-origins.json`의 `sourcePath`가 원본을 가리킵니다.

### 기존 DLL로 여러 캐릭터 추출

PowerShell 7의 `Build/Query-KmsNativeAppearanceBatch.ps1`과 `Build/Export-KmsNativeAvatarBatch.ps1`은 이미 있는 `UnityExportSmoke` 출력 DLL을 재사용합니다. 첫 단계는 요청한 정확한 이름만 KMS에서 조회해 `lookup-results.json`과 캐릭터별 decoded appearance JSON을 저장합니다. 키 읽기는 기존 DLL 내부에서 처리하며, API 응답·설정 내용·예외 원문을 로그에 쓰지 않습니다. 조회 사이에는 기본 650ms를 기다립니다. `lookupFailed`는 조회 실패 표시이며 캐릭터가 존재하지 않는다는 확정 판정이 아닙니다.

두 번째 단계는 저장된 외형 정보로 원본 WZ를 한 번 열고 순서대로 추출합니다. API 요청이나 키 읽기, 프로젝트 빌드를 수행하지 않습니다. 기존 DLL에 없는 원피스 가시성 수정·피부 ID 0~99 검사·고정 표정 출처 인덱스 보정을 적용합니다. 이미 새 DLL에서 보정된 고정 표정 메타데이터는 그대로 보존합니다.

```powershell
$batchCache = Join-Path '.tmp/kms-native-batch' ([Guid]::NewGuid().ToString('N'))
./Build/Query-KmsNativeAppearanceBatch.ps1 -CharacterNames @('정확한이름1', '정확한이름2') `
  -OutputDirectory $batchCache -SettingsPath './WzComparerR2/bin/Release/net8.0-windows/Setting.config'
./Build/Export-KmsNativeAvatarBatch.ps1 -AppearanceCacheDirectory $batchCache `
  -BaseWzPath 'D:/Nexon/Maple/Data/Base/Base.wz'
```

다른 DLL 출력 위치는 두 도구의 `ReaderAssemblyPath`로 지정합니다. 조회 캐시는 매번 새 빈 폴더를 사용하고, 추출 도구도 이미 있는 캐릭터 출력 폴더를 덮어쓰지 않습니다. 각 캐릭터에 별도 폴더를 만들어 같은 외형이나 `previews`의 출처가 섞이지 않게 합니다. `export-results.json`의 성공 항목 `output`이 Unity에 가져올 최종 번들 경로입니다. 일반 외형의 8개 기본·blink 동작 또는 새 DLL의 일루전링 외형 4개 원본 동작, 장착 슬롯/ID, PNG 해시와 원본 출처·프리뷰 파일을 확인합니다.

일루전링 렌더 소스가 포함되지 않은 이전 DLL에서 사람이 출력되면 `Build/Export-KmsNativeIllusionAvatar.ps1`을 사용합니다. 이미 추출한 장착 목록에서 정확한 일루전링을 확인해 원본 WZ를 읽으며 추가 KMS 조회나 빌드 없이 별도 변신 번들을 생성합니다. 원래 사람 외형 번들은 근거 자료로 보존합니다.

```powershell
$illusionRoot = Join-Path '.tmp/kms-native-illusion' ([Guid]::NewGuid().ToString('N'))
./Build/Export-KmsNativeIllusionAvatar.ps1 -NativeBundleDirectory @('기존장착번들경로1', '기존장착번들경로2') `
  -OutputDirectory $illusionRoot -BaseWzPath 'D:/Nexon/Maple/Data/Base/Base.wz'
```

`illusion-export-results.json`의 `output`이 가져올 변신 번들입니다. `illusion-ring-geometry.json`은 원본·실제 캔버스 경로, PNG/RGBA 해시, 원점·시간·투명 영역 경계와 원본 픽셀 기준 머리/발 위치를 기록합니다. 머리 위치는 불투명 영역의 위쪽보다 4픽셀 위, 발 위치는 불투명 영역의 아래쪽 중앙입니다. PNG는 크기 변경·합성 없이 원본으로 보존하며 모든 RGBA 픽셀을 비교합니다.

얼굴 표정을 같은 캔버스에 정렬할 때는 `face-variant-map.json`의 실제 이미지 왼쪽 위 `(x-originX, y-originY)` 차이를 사용합니다. `originX/Y`만 비교하면 포즈에 적용된 원래 위치 차이를 놓칠 수 있습니다. 표정이 기본 얼굴 경계를 벗어나면 기본 얼굴과 표정을 함께 투명한 합집합 캔버스로 늘리고 앵커·원점을 맞춰 원본 픽셀 위치를 보존합니다. 원본 얼굴 픽셀을 자르지 않습니다.

## MapleLive 의자 원본 추출

`Build/Export-MapleLiveChairAssets.ps1`은 기존 `UnityExportSmoke` 출력 DLL을 읽어 의자 이름·아이템 ID를 찾고 원본 UOL/outlink PNG를 추출합니다. 빌드나 API 조회·설정 파일 접근은 하지 않습니다. PowerShell 7과 기존 DLL이 필요하며, 다른 환경에서는 `ReaderAssemblyPath`, `BaseWzPath`, `SpriteMetaTemplate`, 출력 경로를 지정합니다.

```powershell
./Build/Export-MapleLiveChairAssets.ps1 -ItemName '하늘색 나무 의자' -ItemId 3010001 `
  -SpritePrefix SkyBlueWoodChair -OutputDirectory 'D:/Github/MapleLive/Assets/LiveChat/Chairs/SkyBlueWoodChair'
./Build/Export-MapleLiveChairAssets.ps1 -ItemName 'RISE 감상 의자 : 독서' -ItemId 3018487 `
  -SpritePrefix RiseReadingChair -OutputDirectory 'D:/Github/MapleLive/Assets/LiveChat/Chairs/RiseReadingChair'
```

이름이 같은 아이템이 여럿이면 ID를 지정합니다. 원본 PNG·Unity Sprite 메타와 JSON에 원점·앞뒤 레이어·프레임 시간·`bodyRelMove`·장비 숨김 설정·해시를 보존하며 원본 AvatarCanvas 합성 프리뷰는 Assets 밖에 저장합니다. 현재 지원하는 기본 좌표 방식 밖의 특수 의자는 위치를 추정하지 않고 중단합니다. MapleLive의 의자 프로필은 이 원본 자료에 맞춰 별도로 연결합니다.

## Unity로 가져오기

재사용 가능한 UPM 패키지는 `UnityPackages/com.nam3856.wz-importer`에 있습니다. MapleLike에는 이 로컬 패키지가 연결되어 있습니다. 다른 프로젝트에서는 Package Manager의 **Install package from disk**로 이 폴더의 `package.json`을 선택합니다.

WzComparerR2의 추출 단계에서는 JSON과 PNG만 생성합니다. Unity 프로젝트의 `Assets` 안에 추출해도 `.unity` 씬이 자동 생성되지는 않으며, 아래 메뉴에서 가져오기를 실행해야 합니다. 맵은 `Maps` 상위 폴더가 아닌 `Maps/200090010`처럼 `wz-unity.json`이 있는 개별 맵 폴더를 선택합니다.

1. Unity에서 **Tools > WZ Importer > Import Export Folder...**를 엽니다.
2. `wz-unity.json`이 들어 있는 추출 폴더를 선택합니다.
3. 결과는 `Assets/MapleImported/<ID와 해시>/`에 생성됩니다. `Prefabs`에는 애니메이션 프리팹, `Animations`에는 재생 데이터, `Scenes`에는 맵 씬이 있습니다.
4. 맵 씬을 열고 Play Mode로 재생합니다. 개체 프리팹은 원하는 씬에 배치할 수 있습니다.

같은 추출 폴더를 다시 가져오면 기존 GUID를 유지합니다. 임포터의 소유권 기록에 있는 생성 파일만 갱신하며, 사용자가 만든 파일과 이름이 겹치면 중단합니다. 편집한 생성 씬은 먼저 저장해야 재임포트할 수 있습니다. 오래 유지할 게임 로직과 수동 배치는 별도의 씬 또는 prefab variant에 두는 것이 좋습니다.

## 애니메이션 사용

`WzComparerR2.Unity.WzSpriteAnimator`는 원본 밀리초 지연으로 SpriteRenderer를 재생합니다. `WzAnimationSet`에는 동작 이름과 부위별 프레임이 있습니다. 몸·표정·장비 효과의 재생 주기는 독립적이며, 효과의 부착 위치는 몸의 현재 포즈를 따릅니다.

```csharp
using WzComparerR2.Unity;

var animation = character.GetComponent<WzSpriteAnimator>();
animation.Play("walk1"); // 몬스터의 동작 이름은 move, stand, die1 등 원본 이름
animation.Speed = 1f;
animation.FlipX = true;
animation.UseAuthoredLoop();
animation.Completed += action => UnityEngine.Debug.Log(action + " completed");
// animation.Stop();
```

캐릭터 외형을 하나의 이미지로 합치지 않습니다. 슬롯 안에 여러 렌더 조각이 존재할 수 있으며 팔·옷·무기의 앞뒤 교차를 유지합니다. 이번 버전은 추출 당시 코디를 재현합니다. 다른 장비 조합은 WzComparerR2에서 구성해 다시 추출합니다.

## 맵과 좌표

- 기본 단위는 100px = 1 Unity unit이며, WZ의 아래쪽 Y는 Unity에서 반전됩니다. 외부에 있는 이미지 원점도 보존합니다.
- 텍스처는 Point, mipmap 없음, 무압축이며 URP 2D Unlit 재질을 사용합니다. 프로젝트 전체 색 공간이나 렌더 파이프라인 설정은 바꾸지 않습니다.
- 원본 레이어 컨테이너 순서, 프레임별 Z, 배경 반복·시차·스크롤, 오브젝트 주기 운동을 유지합니다.
- 원본 발판을 EdgeCollider2D로 만들고 일반 발판에 PlatformEffector2D를 사용합니다. 사다리·포탈에는 트리거와 원본 메타데이터를 제공합니다.
- NPC·몬스터·리액터는 원본 위치·방향으로 배치하고 스폰·이동 범위·발판 정보를 보존합니다. 게임 중의 이동·전투 AI와 포탈 전환 코드는 포함하지 않습니다.

## 제한과 보고서

Spine는 원본 애니메이션 한 주기를 PNG 시퀀스로 베이크합니다. 혼합 블렌드는 평탄화 경고가 있을 수 있습니다. 전용 셰이더, 조명, BGM, 게임 이벤트·스크립트, 의자·라이딩의 별도 포즈는 지원 범위를 보고서에 표시합니다. 지원하지 않는 맵 시각 요소는 원본 배치 메타데이터를 유지합니다.

MapleLike의 기존 Linear 색 공간은 유지합니다. 반투명 이펙트가 다른 부위와 겹칠 때 Unity의 선형 색상 합성 결과는 WzComparerR2의 합성과 RGB가 다를 수 있습니다. 원본 PNG·알파·위치·타이밍을 보존하며, 이 차이를 없애기 위해 프로젝트 전체 색 공간을 바꾸지는 않습니다.

## 검증 도구

`Tests/UnityExportSmoke`는 .NET 8 콘솔 검증 도구입니다. 먼저 프로젝트를 Release로 빌드한 뒤 출력 DLL에 다음 명령을 전달합니다.

```text
synthetic [출력폴더]
data <Base.wz>
avatar <Base.wz> <출력폴더>
kms-avatar <Base.wz> <캐릭터이름> <Setting.config> [새출력폴더]
export <Base.wz> <출력폴더> [맵ID...]
inspect <Base.wz> <WZ경로...>
mapcheck <Base.wz> <출력폴더>
gui <Base.wz> <출력폴더>
mapgui <Base.wz> <출력폴더>
reactor <Base.wz> <출력폴더>
```

`synthetic`은 PNG 중복 제거·타이밍/원점·잘못된 참조·취소 보존을 검사합니다. `data`는 혼합 WZ/MS 로드와 실제 IMG·한글 이름을 검사합니다. `avatar`는 원본 AvatarCanvas 렌더와 추출 PNG 조합을 여러 동작·시점에서 픽셀 단위로 비교합니다. 실제 데이터 산출물은 `.tmp/unity-export-validation`에 보관하며 소스 관리에서 제외합니다.

`mapcheck`는 실제 Spine 맵을 이용해 GPU 베이크 도중 취소, 이전 추출 결과 보존, 원본 미리보기와 GPU 상태 복원, 반복 추출의 일관성을 검사합니다. Unity에서는 **Tools > WZ Importer > Validate Importer**로 임포트·재임포트·프레임 재생·씬 재열기·취소 및 오류 복원을 검증합니다. 보고서는 프로젝트의 `Library/WzImporterValidation/latest.json`에 저장됩니다.

`gui`는 실제 본체의 파일 열기와 한글 이름 초기화·아바타 미리보기를, `mapgui`는 Bellona 미리보기와 추출 메뉴를 검사하고 화면을 저장합니다. `reactor`는 실제 상태별 프레임을 확인합니다. Unity의 `WzRealValidation.Run(추출상위폴더)`는 임포트된 실제 데이터의 씬·참조·원본 좌표·프레임 재생을 검사하며 결과는 `Library/WzImporterValidation/real-validation.json`에 기록합니다.

2026-10-07에는 본체와 5개 플러그인의 Release 빌드가 오류 없이 완료됐습니다. 합성 검증은 전체 29슬롯, 숨겨진 이미지 없는 반지, 등급 0·잘못된 값·원본 링크·구버전 JSON과 기존 추출기 회귀를 통과했습니다. `equipment <Base.wz>`는 실제 원본의 일반 반지 1112000, 이미지 없는 일루전링 1114500, 일루전링 1114501, 등급 0 일루전링 1116125의 판별을 확인했습니다. 실제 아바타 검증은 4개 동작·PNG 63개·원본 비교 12시점 픽셀 일치·장착 정보 보존·취소 복구를 통과했습니다. 테스트는 설치된 .NET 10에서 명시적 `--roll-forward Major`로 실행했으며 프로젝트의 .NET 8 런타임 정책은 유지했습니다. MapleLive Unity 6000.3.16f1 임포터 검증에서도 저장된 장착 정보와 재임포트·씬 재열기를 확인했습니다.

같은 날 .NET Core·Desktop 8.0.27을 포함한 Windows x64 게시물도 생성했습니다. 네이티브 의존성·플러그인 파일 25개의 복사 해시와 6개 의존성 manifest의 관리 라이브러리 258개를 확인했으며 누락이 없습니다. 포터블 실행 파일은 실행하지 않았고 별도 런타임을 설치하지 않았습니다.
