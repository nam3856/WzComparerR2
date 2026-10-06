# Unity 추출 검증 기록 — 2026-09-23

기준은 `seotbeo/seotbeo_main`의 `71bc8a9`입니다. `agent/restore-unity-export`에서 작업했고 이전 작업 브랜치와 stash는 유지했습니다. WzLib의 임시 포맷 감지나 체크섬 우회는 추가하지 않았습니다. 커밋·푸시는 하지 않았습니다.

## WzComparerR2

- .NET 8 Release 본체와 Avatar, MapRender, DB2, LuaConsole, Network 빌드 성공. 실제 실행 파일에서 플러그인 5개 로딩과 정상 응답 확인.
- 실제 `MainForm.openWz`로 MapleT `Base.wz` 및 MS 팩 12개 로드. 트리 17개 항목과 StringLinker 장비 이름 39,496개 초기화. `1000000`의 이름 `파란색 털모자` 확인.
- Base·String·Character·Map의 IMG/PNG 추출 및 별도 Mob·Skill MS 파일 확인.
- 실제 Avatar 창의 코디 미리보기·한글 이름·캐릭터 Unity 추출 버튼과 WZ 트리의 개체 추출 메뉴 확인.
- 실제 MapRender에서 Bellona 24프레임 렌더 및 Unity 추출 탭·버튼 표시 확인.
- 캐릭터 `stand1`, `walk1`, `alert`, `swingO1`의 0/137/489ms, 총 12시점에서 원본 AvatarCanvas와 추출 PNG 조합이 픽셀 단위로 일치.
- 맵 GPU 베이크 도중 취소, 기존 결과 보존, 원본 미리보기·GPU 상태 복원, 반복 추출의 바이트 일치 검사 통과.
- 잘못된 링크·프레임 및 경로 입력 거부, 취소·실패·정상 재추출 시 사용자 파일 보존 검사 통과.

## MapleLike / Unity 6000.3.16f1

기존 URP 2D 설정에 로컬 UPM 패키지를 연결했습니다. 생성 위치는 `Assets/MapleImported`입니다.

| 맵 | PNG | 프리팹 | 레이어 | 발판 | 원본 개체 배치 | 경고 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Bellona 993218092 | 105 | 6 | 6 | 2 | 0 | 3 |
| 헤네시스 100000000 | 1030 | 173 | 1626 | 353 | NPC 34 | 7 |
| 일반 필드 100010000 | 227 | 79 | 483 | 175 | 몬스터 20, NPC 6 | 1 |

- 아바타: PNG 63개·프리팹 1개. 몬스터 `0100100`, NPC `1012000`, 리액터 `0100000`도 별도 임포트. 리액터 상태 클립 9개 확인.
- 실제 데이터 검사: 235개 프레임 시점, 1,109개 렌더 조각, 맵 3개 통과. 필드 몬스터 `1210102`의 이동·기본·점프·피격·사망도 확인.
- 저장된 씬을 다시 열어 누락 스크립트·참조, 프리팹 연결, 발판 연결 정보, 사다리·포탈, 배치 좌표·방향·스폰 정보를 검사.
- 0/5,000/17,000ms와 카메라 위치 변화에서 레이어 순서·시차·반복·스크롤·주기 운동 검사 및 URP 렌더 캡처.
- 합성 회귀검증 9개 항목 통과: GUID·사용자 파일 유지, 같은 PNG 재사용, 변경된 이미지·임포트 설정 복구, 독립 트랙·가림·반전, 사망 완료 이벤트, 운동 경계, 취소·오류 복원, 원래 편집 씬 보존.
- Play Mode는 헤네시스 58프레임/3.040초, 일반 필드 119프레임/3.009초, Bellona 481프레임/3.001초를 관찰했습니다. 세 씬 모두 애니메이션 변화와 런타임 오류 0개를 확인했습니다.
- 검증 후 기존 SampleScene만 열린 편집 상태로 복원했으며 미저장 변경·컴파일 오류·현재 콘솔 오류는 없습니다. 맵별 결과는 아래 보고서에 저장됩니다.

## 확인된 제한

- Spine 혼합 블렌드는 PNG로 평탄화되어 원본 혼합과 차이가 있을 수 있습니다. 전용 효과·BGM·게임 이벤트 및 이미지가 없는 포탈은 추출 보고서에 원본 경로와 사유를 기록합니다.
- NPC 원본의 불연속 프레임 번호는 존재하는 프레임을 순서대로 유지하고 경고를 기록합니다. 실제로 존재하지만 해석되지 않는 프레임은 실패 처리합니다.
- Unity 캐릭터 캡처 12개는 원본과 경계·알파가 일치했습니다. 반투명 이펙트의 RGB 합성은 기존 Linear 색 공간 및 렌더 타깃의 premultiplied RGB 때문에 차이가 있으므로 RGB 픽셀 일치를 주장하지 않습니다.
- 이 검증은 위의 실제 데이터와 합성 회귀검증에 한정됩니다. 모든 WZ 버전·코디·맵의 전용 효과를 검증한 것은 아닙니다.

## 결과 파일

- 저장소 `.tmp/unity-export-validation/real`: 실제 추출 데이터와 원본 캐릭터 비교 이미지.
- 저장소 `.tmp/unity-export-validation/gui`, `map-gui`: 실제 프로그램 화면 캡처.
- MapleLike `Library/WzImporterValidation/latest.json`: 임포터 회귀검증.
- MapleLike `Library/WzImporterValidation/real-validation.json`: 실제 데이터·렌더 비교.
- MapleLike `Library/WzImporterValidation/play-validation-<맵ID>.json`: 맵별 Play Mode 관찰.
- 각 추출 폴더 `export-report.txt`, 각 임포트 폴더 `import-result.json`: 결과와 경고.

사용법과 빌드 명령은 [UnityExport.md](UnityExport.md)에 있습니다.

## 후속 수정: 200090010 구름 스크롤

최신 upstream의 배경 계산이 속도 기준 거리(기본 100px)를 반복 간격으로도 사용해, 800~2,000px 간격으로 배치된 구름이 0.4~0.6초마다 100px씩 튀었습니다. 이전 검증은 이 스크롤 경계의 연속성을 확인하지 못했습니다.

속도 기준과 반복 간격을 분리하고 실제 반복 간격에서만 좌표를 감싸도록 네이티브 미리보기와 Unity 재생기를 수정했습니다. 반복하지 않는 축은 좌표를 감싸지 않으며, 네이티브 스크롤 곱셈은 double로 계산해 장시간 재생 시 정수 오버플로를 피합니다. W/Wx/Wy 및 Spine Flow에 따른 속도는 유지합니다.

실제 미리보기에서 구름 8개와 반복 경계·장시간 재생·Spine Flow 조건 검증을 통과했습니다. Unity는 합성 회귀검증 10항목과 실제 맵의 경계/카메라 전환 128쌍을 통과했습니다. 또한 추출·폴더 선택 뒤에는 게임 시계를 재개하여 대기한 시간만큼 미리보기가 갑자기 진행되지 않게 했으며, 300ms 대기 후 재개 간격이 0.5555ms임을 확인했습니다.

수정 DLL은 기존 Release 실행 경로에 반영했습니다. 이미 실행 중인 프로그램은 이전 DLL을 사용하므로 WzComparerR2 전체를 다시 실행해야 적용됩니다. 검증 로그는 `.tmp/map-motion-regression.log`, Unity 보고서는 `Library/WzImporterValidation/scroll-validation.json`입니다.
