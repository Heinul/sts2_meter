"""
Godot 4.x PCK v2 파일 빌더.
BetterSpire2.pck 포맷을 정확히 따름.

PCK v2 헤더 구조 (100 bytes):
  [0]   "GDPC"           4B  magic
  [4]   pack_version=2   4B
  [8]   ver_major         4B
  [12]  ver_minor         4B
  [16]  ver_patch         4B
  [20]  pack_flags        4B
  [24]  file_base         8B  (uint64, 데이터 시작 오프셋)
  [32]  reserved[16]     64B  (16 x uint32, 전부 0)
  [96]  file_count        4B
  --- total: 100 bytes ---

파일 엔트리 구조:
  path_len              4B
  path_data             가변 (4바이트 정렬)
  offset                8B  (uint64, file_base 기준 상대)
  size                  8B  (uint64)
  md5                  16B
  file_flags            4B
"""
import struct
import hashlib
import os
import sys
import json


def build_pck(manifest_path: str, output_path: str):
    with open(manifest_path, 'rb') as f:
        manifest_data = f.read()

    # JSON 유효성 검증
    json.loads(manifest_data)

    # 리소스 경로
    res_path = "res://mod_manifest.json"
    path_bytes = res_path.encode('utf-8')
    path_len = len(path_bytes)  # 23
    pad = (4 - (path_len % 4)) % 4
    path_padded = path_bytes + b'\x00' * pad  # 24 bytes

    # 크기 계산
    HEADER_SIZE = 100  # magic(4) + ver(16) + flags(4) + file_base(8) + reserved(64) + count(4)
    ENTRY_SIZE = 4 + len(path_padded) + 8 + 8 + 16 + 4  # 64 bytes
    file_base = HEADER_SIZE + ENTRY_SIZE  # 164 (데이터 시작 위치)

    md5 = hashlib.md5(manifest_data).digest()

    pck = bytearray()

    # ===== Header (100 bytes) =====
    pck += b'GDPC'                         # [0]   magic
    pck += struct.pack('<I', 2)            # [4]   pack_version = 2
    pck += struct.pack('<I', 4)            # [8]   ver_major
    pck += struct.pack('<I', 5)            # [12]  ver_minor
    pck += struct.pack('<I', 1)            # [16]  ver_patch
    pck += struct.pack('<I', 0)            # [20]  pack_flags = 0
    pck += struct.pack('<Q', file_base)    # [24]  file_base (uint64)
    pck += b'\x00' * 64                   # [32]  reserved (16 x uint32)
    pck += struct.pack('<I', 1)            # [96]  file_count = 1

    assert len(pck) == HEADER_SIZE, f"Header size mismatch: {len(pck)} != {HEADER_SIZE}"

    # ===== File Entry (64 bytes) =====
    # Godot는 path_len만큼 정확히 읽으므로, 패딩을 포함한 길이를 저장해야 함
    pck += struct.pack('<I', len(path_padded))        # path length (패딩 포함!)
    pck += path_padded                                # path (4-byte aligned)
    pck += struct.pack('<Q', 0)                       # offset (relative to file_base)
    pck += struct.pack('<Q', len(manifest_data))      # size
    pck += md5                                         # md5 hash
    pck += struct.pack('<I', 0)                       # file_flags = 0

    assert len(pck) == HEADER_SIZE + ENTRY_SIZE, f"Entry end mismatch: {len(pck)}"
    assert len(pck) == file_base, f"Data start mismatch: {len(pck)} != {file_base}"

    # ===== File Data =====
    pck += manifest_data

    # 출력
    with open(output_path, 'wb') as f:
        f.write(pck)

    print(f"  format:   PCK v2 (Godot 4.5.1)")
    print(f"  header:   {HEADER_SIZE} bytes")
    print(f"  entry:    {ENTRY_SIZE} bytes (1 file)")
    print(f"  file_base: {file_base}")
    print(f"  manifest: {len(manifest_data)} bytes")
    print(f"  total:    {len(pck)} bytes")
    print(f"  output:   {output_path}")
    return True


if __name__ == '__main__':
    project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    manifest_path = os.path.join(project_dir, 'mod_manifest.json')

    # 기본: 게임 mods 폴더에 직접 출력
    default_output = r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\DamageMeterMod.pck"
    output_path = sys.argv[1] if len(sys.argv) > 1 else default_output

    print("[build_pck] Building PCK v2 file...")
    if build_pck(manifest_path, output_path):
        print("[build_pck] Done!")
    else:
        print("[build_pck] Failed!")
        sys.exit(1)
