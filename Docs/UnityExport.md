# Unity 프리팹·씬 추출

## 실행과 빌드

PowerShell에서 저장소의 `Build/Build-UnityExport.ps1`을 실행하면 .NET 8 Release 본체와 Avatar, MapRender, DB2, LuaConsole, Network 플러그인을 같은 출력 폴더에 빌드합니다.

실행 파일: `WzComparerR2/bin/Release/net8.0-windows/WzComparerR2.exe`

MapleT는 `E:/nexon/MapleT/Data/Base/Base.wz`를 엽니다. 최신 upstream의 KMST1205/1206 로더를 사용하며 Base 폴더와 Packs의 MS/MN 데이터를 함께 읽습니다. 개별 플러그인 프로젝트를 빌드할 때도 본체의 해당 구성/프레임워크 Plugin 폴더로 복사됩니다.

## 추출

- **캐릭터:** Avatar 창에서 코디를 구성한 다음 **캐릭터 Unity 추출**을 누릅니다. 선택한 상위 폴더 아래 외형 해시로 구분된 폴더를 만듭니다. 현재 장비·표정·가림·프리즘 결과와 부위별 동작을 추출합니다.
- **맵:** MapRender 창의 옵션에 있는 Unity 추출 버튼 또는 **Ctrl+E**를 사용합니다. 선택한 폴더 아래 `Maps/맵ID`에 저장합니다.
- **몬스터·NPC·리액터:** WZ 트리에서 해당 IMG나 하위 노드를 우클릭해 **몬스터·NPC·리액터 Unity 추출**을 선택합니다. 개체별 하위 폴더를 생성합니다.

각 폴더에는 `wz-unity.json`, `png/`, `export-report.txt`가 있습니다. 다른 프로그램을 실행할 필요가 없으며 AEP는 생성하지 않습니다.

추출은 별도 임시 폴더에서 완성한 뒤 기존 결과를 교체합니다. 취소·실패하면 기존 결과를 유지합니다. 같은 ID의 추출 결과만 갱신하며, 추출기가 만들지 않은 파일도 보존합니다. 경고가 있으면 완료 메시지와 보고서에서 누락 원본 경로·사유를 확인할 수 있습니다.

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
