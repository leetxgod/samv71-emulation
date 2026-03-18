#!/usr/bin/env bash
set -euo pipefail

# Creates an SD image with 4 FAT32 partitions so mmcblka1..mmcblka4 are present.
#
# Layout:
#   - Partition 1..4: FAT32 (LBA), 1 MiB-aligned
#
# Usage:
#   ./create_sd_card_image.sh [output_image] [size_mb] [partitions] [files_dir] [target_partition]
#
# Defaults:
#   output_image: sd_card.img
#   size_mb: 512
#   partitions: 4
#   files_dir: (none)
#   target_partition: 2 (used when files_dir is set)

IMG="${1:-sd_card.img}"
SIZE_MB="${2:-512}"
PARTITIONS="${3:-4}"
FILES_DIR="${4:-}"
TARGET_PARTITION="${5:-2}"

if ! [[ "$SIZE_MB" =~ ^[0-9]+$ ]]; then
  echo "size_mb must be a positive integer"
  exit 1
fi

if ! [[ "$PARTITIONS" =~ ^[0-9]+$ ]]; then
  echo "partitions must be a positive integer"
  exit 1
fi

if ! [[ "$TARGET_PARTITION" =~ ^[0-9]+$ ]]; then
  echo "target_partition must be a positive integer"
  exit 1
fi

if (( PARTITIONS < 1 || PARTITIONS > 4 )); then
  echo "partitions must be in range 1..4 (MBR limit)"
  exit 1
fi

if (( TARGET_PARTITION < 1 || TARGET_PARTITION > PARTITIONS )); then
  echo "target_partition must be in range 1..${PARTITIONS}"
  exit 1
fi

if (( SIZE_MB < 128 )); then
  echo "size_mb must be >= 128"
  exit 1
fi

if [[ -n "$FILES_DIR" ]] && [[ ! -d "$FILES_DIR" ]]; then
  echo "files_dir does not exist or is not a directory: $FILES_DIR"
  exit 1
fi

if [[ -n "$FILES_DIR" ]] && ! command -v mcopy >/dev/null 2>&1; then
  echo "mcopy is required to inject files into FAT partitions (install mtools)"
  exit 1
fi

# Keep each partition at >=64MiB so FAT32 stays within standard cluster limits.
min_part_mb=64
if (( SIZE_MB / PARTITIONS < min_part_mb )); then
  echo "size_mb is too small: need at least $((PARTITIONS * min_part_mb)) MiB for ${PARTITIONS} FAT32 partitions"
  exit 1
fi

SECTOR_SIZE=512
TOTAL_SECTORS=$((SIZE_MB * 1024 * 1024 / SECTOR_SIZE))
ALIGN_SECTORS=2048 # 1 MiB

if (( TOTAL_SECTORS <= ALIGN_SECTORS * 2 )); then
  echo "image is too small"
  exit 1
fi

declare -a STARTS=()
declare -a SIZES=()
declare -a TMPS=()

next_start=$ALIGN_SECTORS
remaining=$((TOTAL_SECTORS - next_start))
for ((i = 1; i <= PARTITIONS; i++)); do
  slots_left=$((PARTITIONS - i + 1))
  if (( i < PARTITIONS )); then
    part_size=$(((remaining / slots_left / ALIGN_SECTORS) * ALIGN_SECTORS))
    if (( part_size < ALIGN_SECTORS )); then
      part_size=$ALIGN_SECTORS
    fi
  else
    part_size=$remaining
  fi

  STARTS+=("$next_start")
  SIZES+=("$part_size")

  next_start=$((next_start + part_size))
  remaining=$((TOTAL_SECTORS - next_start))
done

cleanup() {
  for f in "${TMPS[@]}"; do
    rm -f "$f"
  done
}
trap cleanup EXIT

rm -f "$IMG"
truncate -s $((TOTAL_SECTORS * SECTOR_SIZE)) "$IMG"

{
  echo "label: dos"
  echo "unit: sectors"
  echo
  for ((i = 0; i < PARTITIONS; i++)); do
    boot=""
    if (( i == 0 )); then
      boot="*"
    fi
    # 0x0C = W95 FAT32 (LBA)
    echo "${STARTS[$i]} ${SIZES[$i]} 0x0C ${boot}"
  done
} | sfdisk "$IMG" >/dev/null

for ((i = 0; i < PARTITIONS; i++)); do
  tmp="$(mktemp "/tmp/sd_p$((i + 1)).XXXXXX.img")"
  TMPS+=("$tmp")
  truncate -s $((SIZES[$i] * SECTOR_SIZE)) "$tmp"
  # We intentionally force FAT32 even on smaller partitions for compatibility
  # with mount tests that expect FAT32 labels/boot sectors.
  # Use partition start as BPB hidden-sector value for better compatibility.
  mkfs.fat -F 32 -h "${STARTS[$i]}" "$tmp" >/dev/null
  dd if="$tmp" of="$IMG" bs=$SECTOR_SIZE seek="${STARTS[$i]}" conv=notrunc status=none
done

if [[ -n "$FILES_DIR" ]]; then
  part_index=$((TARGET_PARTITION - 1))
  part_offset_bytes=$((STARTS[$part_index] * SECTOR_SIZE))
  image_spec="${IMG}@@${part_offset_bytes}"

  shopt -s nullglob dotglob
  entries=("$FILES_DIR"/*)
  shopt -u nullglob dotglob

  if (( ${#entries[@]} == 0 )); then
    echo "files_dir is empty, no files injected"
  else
    mcopy -i "$image_spec" -s "${entries[@]}" ::
    echo "Injected ${#entries[@]} item(s) from '$FILES_DIR' into partition ${TARGET_PARTITION}"
  fi
fi

echo "Created $IMG (${SIZE_MB} MiB, ${PARTITIONS} FAT32 partitions)"
fdisk -l "$IMG"
