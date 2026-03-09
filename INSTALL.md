# Damage Meter Mod - 설치 및 빌드 가이드

## 사전 요구사항

| 항목 | 버전 | 비고 |
|------|------|------|
| .NET SDK | 9.0+ | `dotnet --version`으로 확인 |
| Slay the Spire 2 | v0.98.x | Steam 설치 |
| (선택) ILSpy | 최신 | sts2.dll 디컴파일용 |

## 빌드 방법

### 1. .NET 9 SDK 설치
```bash
# winget 사용 (Windows)
winget install Microsoft.DotNet.SDK.9

# 또는 https://dotnet.microsoft.com/download/dotnet/9.0 에서 수동 설치
```

### 2. 프로젝트 빌드
```bash
cd DamageMeterMod

# 게임 경로가 기본값과 다른 경우 환경변수 설정
# set STS2_GAME_PATH=D:\Steam\steamapps\common\Slay the Spire 2

# Debug 빌드
dotnet build

# Release 빌드 (자동으로 mods 폴더에 복사)
dotnet build -c Release
```

### 3. 수동 설치
Release 빌드를 사용하지 않는 경우:

```
Slay the Spire 2/
└── mods/
    └── DamageMeter/
        ├── DamageMeterMod.dll    ← bin/Release/net9.0/ 에서 복사
        └── mod_manifest.json     ← 프로젝트 루트에서 복사
```

## 게임에서 활성화

1. Slay the Spire 2 실행
2. 메인 메뉴 → **Mods** 클릭
3. "Damage Meter" 체크박스 활성화
4. 게임 재시작 (모드 컴파일 적용)
5. 화면 우하단에 "Running Modded (1)" 표시 확인

## 사용법

| 키 | 기능 |
|----|------|
| **F7** | 데미지 미터 표시/숨기기 토글 |
| **F8** | 디버그 로그 ON/OFF |
| **드래그** | 헤더 영역을 드래그하여 위치 이동 |
| **−** 버튼 | 패널 최소화/복원 |
| **×** 버튼 | 패널 숨기기 (F7로 복원) |

## 표시 정보

- **Player Name**: 캐릭터 이름
- **Damage**: 현재 전투에서의 총 데미지
- **%**: 전체 파티 데미지 중 비율
- **This turn**: 현재 턴 데미지
- **DPT**: 턴당 평균 데미지 (Damage Per Turn)
- **Max**: 한 번에 가한 최대 데미지
- **Turn / Total**: 현재 턴 수 및 파티 총 데미지

## 디컴파일 가이드 (패치 타겟 확인용)

게임이 업데이트되면 내부 클래스 구조가 변경될 수 있습니다.
아래 절차로 실제 메서드 이름을 확인하세요.

### ILSpy로 sts2.dll 디컴파일

1. ILSpy 설치: https://github.com/icsharpcode/ILSpy/releases
2. `data_sts2_windows_x86_64/sts2.dll` 열기
3. 탐색할 네임스페이스:
   - `MegaCrit.Sts2.Core.Combat.CombatManager` - 전투 관리
   - `MegaCrit.Sts2.Core.Hooks.Hook` - 78개 이벤트 Hook
   - `MegaCrit.Sts2.Core.Modding.ModManager` - 모드 로딩
   - `MegaCrit.Sts2.Core.Models.Characters` - 캐릭터 모델

### 핵심 확인 사항

패치가 작동하려면 아래 메서드가 존재해야 합니다:

```
Hook.AfterDamageGiven      (d__23)  → 데미지 추적
Hook.BeforeCombatStart     (d__18)  → 전투 초기화
Hook.AfterCombatEnd        (d__19)  → 전투 종료
Hook.AfterTurnEnd          (d__77)  → 턴 카운트
```

메서드 시그니처가 변경된 경우 `Patches/CombatPatches.cs`를 수정합니다.

## 트러블슈팅

### "Could not locate AfterDamageGiven hook" 에러
→ 게임 업데이트로 메서드명이 변경됨. ILSpy로 확인 후 패치 타겟 수정.

### UI가 표시되지 않음
→ F7 키 확인. Godot 콘솔(F12)에서 "[DamageMeter]" 로그 확인.

### 데미지가 기록되지 않음
→ F8으로 디버그 모드 활성화. 콘솔에서 Harmony 패치 성공 여부 확인.

### 모드가 로드되지 않음
→ mods/ 폴더 경로 확인. mod_manifest.json이 DLL과 같은 폴더에 있는지 확인.

## 프로젝트 구조

```
DamageMeterMod/
├── mod_manifest.json         # 모드 메타데이터
├── DamageMeterMod.csproj     # .NET 프로젝트 설정
├── DamageMeterMod.sln        # Visual Studio 솔루션
├── ModEntry.cs               # 엔트리포인트 (Harmony 초기화 + UI 생성)
├── Core/
│   ├── PlayerDamageRecord.cs  # 플레이어별 데미지 데이터 구조체
│   └── DamageTracker.cs       # 데미지 추적 싱글턴 매니저
├── Patches/
│   └── CombatPatches.cs       # Harmony 패치 (전투 이벤트 후킹)
├── UI/
│   └── DamageMeterOverlay.cs  # CanvasLayer 기반 오버레이 UI
├── Assets/                    # (선택) 아이콘, 폰트 등
└── INSTALL.md                 # 이 파일
```
