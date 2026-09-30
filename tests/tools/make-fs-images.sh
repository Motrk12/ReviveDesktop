#!/bin/sh
# Builds real FAT16 / FAT32 / exFAT / NTFS test images containing deleted files, using the Linux
# file-system drivers (independent of Revive's own parsers).
#
# Run from Windows with:   wsl -d docker-desktop -- sh /mnt/host/g/Recover/tests/tools/make-fs-images.sh
# Needs a root Alpine shell (the docker-desktop WSL distro works). Tools are installed into a throwaway
# root under /tmp; only the images in tests/TestData/generated are written outside it.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$(cd "$HERE/../TestData" && mkdir -p generated && cd generated && pwd)
MEDIA=$(cd "$HERE/../TestData/media" && pwd)
R=/tmp/revive-root

if [ ! -e "$R/usr/sbin/mkntfs" ] && [ ! -e "$R/sbin/mkntfs" ] && [ ! -e "$R/usr/bin/mkntfs" ]; then
  mkdir -p "$R/etc/apk/keys"
  cp /etc/apk/keys/* "$R/etc/apk/keys/"
  cp /etc/apk/repositories "$R/etc/apk/repositories"
  apk --root "$R" --initdb --no-cache --quiet add busybox busybox-binsh musl ntfs-3g ntfs-3g-progs exfatprogs fuse-exfat dosfstools
  chroot "$R" /bin/busybox --install -s
fi

mkdir -p "$R/dev" "$R/proc" "$R/work" "$R/media" "$R/mnt/img"
cleanup() {
  umount "$R/mnt/img" 2>/dev/null || true
  for m in media work proc dev; do umount "$R/$m" 2>/dev/null || true; done
}
trap cleanup EXIT
mount --bind /dev "$R/dev"
mount -t proc proc "$R/proc"
mount --bind "$OUT" "$R/work"
mount --bind "$MEDIA" "$R/media"

in_root() { chroot "$R" /bin/sh -c "$1"; }

# Same layout on every image. Deleted: two camera files, a small note and the whole Music folder.
POPULATE='
  set -e
  M=/mnt/img
  mkdir -p $M/DCIM/100CANON $M/Music $M/Documents
  cp /media/photo.jpg $M/DCIM/100CANON/IMG_0001.JPG
  cp /media/wallpaper.jpg $M/DCIM/100CANON/IMG_0002.JPG
  cp /media/clip.mp4 "$M/DCIM/100CANON/Holiday video with a long name.mp4"
  cp /media/song.mp3 $M/Music/song.mp3
  cp /media/music.ogg $M/Music/music.ogg
  cp /media/image.png $M/Documents/keep.png
  printf "Short note that fits inside the file record.\n" > $M/Documents/note.txt
  sync
  rm $M/DCIM/100CANON/IMG_0001.JPG "$M/DCIM/100CANON/Holiday video with a long name.mp4" $M/Documents/note.txt
  rm -r $M/Music
  sync
'

make_fat() { # file size-mb fat-bits
  rm -f "$OUT/$1"
  in_root "dd if=/dev/zero of=/work/$1 bs=1M count=$2 2>/dev/null && mkfs.vfat -F $3 -S 512 -s 1 -n TESTFAT /work/$1 >/dev/null"
  mount -o loop "$OUT/$1" "$R/mnt/img"
  in_root "$POPULATE"
  umount "$R/mnt/img"
}

make_fat fat32.img 64 32
make_fat fat16.img 24 16

# The FUSE drivers want a block device when run as root, so go through a loop device.
make_fuse() { # file mkfs-command mount-command
  rm -f "$OUT/$1"
  in_root "dd if=/dev/zero of=/work/$1 bs=1M count=64 2>/dev/null && $2 /work/$1 >/dev/null"
  LOOP=$(losetup -f --show "$OUT/$1")
  in_root "$3 $LOOP /mnt/img"
  in_root "$POPULATE"
  umount "$R/mnt/img"
  losetup -d "$LOOP"
}

make_fuse exfat.img "mkfs.exfat -L TESTEXFAT" "mount.exfat-fuse"
make_fuse ntfs.img "mkntfs -F -Q -L TESTNTFS" "ntfs-3g"

ls -la "$OUT"
