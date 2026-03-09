#!/usr/bin/env python3
"""
Damage Meter Mod - 패치 타겟 검증 스크립트.

sts2.dll의 바이너리에서 패치에 필요한 클래스/메서드가
존재하는지 빠르게 확인합니다.

사용법:
  python verify_patch_targets.py
  python verify_patch_targets.py "D:/Steam/.../Slay the Spire 2"
"""

import sys
import os

# 기본 게임 경로
DEFAULT_GAME_PATH = r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"

# 검증할 패치 타겟 목록
REQUIRED_TARGETS = [
    # (설명, 바이너리에서 찾을 문자열)
    ("Hook.AfterDamageGiven", b"MegaCrit.Sts2.Core.Hooks.Hook+<AfterDamageGiven>"),
    ("Hook.BeforeCombatStart", b"MegaCrit.Sts2.Core.Hooks.Hook+<BeforeCombatStart>"),
    ("Hook.AfterCombatEnd", b"MegaCrit.Sts2.Core.Hooks.Hook+<AfterCombatEnd>"),
    ("Hook.AfterTurnEnd", b"MegaCrit.Sts2.Core.Hooks.Hook+<AfterTurnEnd>"),
    ("CombatManager", b"MegaCrit.Sts2.Core.Combat.CombatManager"),
    ("PlayerId property", b"get_PlayerId"),
    ("ModManager", b"MegaCrit.Sts2.Core.Modding.ModManager"),
]

OPTIONAL_TARGETS = [
    ("Hook.BeforeAttack", b"MegaCrit.Sts2.Core.Hooks.Hook+<BeforeAttack>"),
    ("Hook.AfterAttack", b"MegaCrit.Sts2.Core.Hooks.Hook+<AfterAttack>"),
    ("Hook.AfterDamageReceived", b"MegaCrit.Sts2.Core.Hooks.Hook+<AfterDamageReceived>"),
    ("Hook.AfterPlayerTurnStart", b"MegaCrit.Sts2.Core.Hooks.Hook+<AfterPlayerTurnStart>"),
    ("AfterModifyingDamageAmount", b"AfterModifyingDamageAmount"),
    ("DealDamageToAllEnemies", b"DealDamageToAllEnemies"),
    ("Characters namespace", b"MegaCrit.Sts2.Core.Models.Characters"),
    ("LocalPlayerId", b"LocalPlayerId"),
    ("steamPlayerId", b"steamPlayerId"),
]


def main():
    game_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_GAME_PATH
    dll_path = os.path.join(game_path, "data_sts2_windows_x86_64", "sts2.dll")

    if not os.path.exists(dll_path):
        print(f"ERROR: sts2.dll not found at {dll_path}")
        print("Usage: python verify_patch_targets.py [game_path]")
        sys.exit(1)

    # 게임 버전 확인
    release_info = os.path.join(game_path, "release_info.json")
    if os.path.exists(release_info):
        with open(release_info, "r") as f:
            print(f"Game info: {f.read().strip()}")
    print()

    # DLL 로드
    with open(dll_path, "rb") as f:
        data = f.read()

    print(f"Loaded sts2.dll ({len(data):,} bytes)")
    print("=" * 60)

    # 필수 타겟 검증
    print("\n[REQUIRED TARGETS]")
    all_ok = True
    for name, pattern in REQUIRED_TARGETS:
        found = pattern in data
        status = "OK" if found else "MISSING"
        icon = "+" if found else "!"
        print(f"  [{icon}] {status:8s} {name}")
        if not found:
            all_ok = False

    # 선택적 타겟 검증
    print("\n[OPTIONAL TARGETS]")
    for name, pattern in OPTIONAL_TARGETS:
        found = pattern in data
        status = "OK" if found else "N/A"
        icon = "+" if found else "-"
        print(f"  [{icon}] {status:8s} {name}")

    # 전체 Hook 메서드 목록 추출
    import re
    hook_pattern = rb"MegaCrit\.Sts2\.Core\.Hooks\.Hook\+<([A-Za-z_]+)>d__(\d+)"
    hooks = set(re.findall(hook_pattern, data))
    print(f"\n[HOOK SYSTEM] Total hooks found: {len(hooks)}")

    # 결과
    print("\n" + "=" * 60)
    if all_ok:
        print("RESULT: All required patch targets found. Mod should work.")
    else:
        print("RESULT: Some required targets are MISSING.")
        print("        The game may have been updated.")
        print("        Use ILSpy to decompile sts2.dll and update CombatPatches.cs.")

    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
