"""Reference decoder for the Colonization VICEROY.EXE overlay container.

This is the executable form of the format analysis. It exists so the C# loader has
something to be checked against: both must produce the same overlay directory and the
same thunk resolutions for the same file.

Format, all little endian.

The MZ load module ends at `pages * 512 - (512 - extra_bytes)`. Rounded up to the next
paragraph, that is where the overlay container starts. The container is a chain of
overlay images, each one:

    +0x00  u16         total size of this image in paragraphs, header included
    +0x02  u16         header size in paragraphs; code starts at header + this * 16
    +0x04  u32         700 in every image in this build
    +0x08  u32         relocation count
    +0x0C  u32         zero
    +0x10  (u16 offset, u16 segment)[count]  relocations, then zero padding up to the code

The next image starts at header + (field at +0x00) * 16, and the chain ends at EOF.

An overlay's code is several code segments laid end to end, each on a paragraph. A
relocation's segment is a paragraph from the start of the overlay's code, so the word to
fix up is at `code + segment * 16 + offset`.

Overlay code is reached from the resident image through thunks. The segment in a thunk's
far call is a link time segment relative to the load module, so a thunk site is at
`load_module + seg * 16 + off` -- not at the runtime address, which is what makes these
targets look like string data when resolved against a running process. Thunks come in
three shapes:

    overlay, 14 bytes:  call far manager:0DAB; jmp far 0000:entry; u16 overlay; u16 segment
    overlay, 12 bytes:  call far manager:0DAB; jmp far 0000:entry; u16 overlay
    resident, 10 bytes: call far manager:0D91; jmp far segment:offset

The short overlay form only ever appears directly in front of another thunk, and targets
the overlay's first segment. An overlay target is at `code + segment * 16 + entry`.
"""

import struct
from dataclasses import dataclass, field

OVERLAY_MANAGER = 0x110D
"""Segment of both overlay manager entry points, in link time (relative) form."""

MANAGER_OVERLAY = 0x0DAB
"""Manager entry used by thunks whose target lives in an overlay."""

MANAGER_RESIDENT = 0x0D91
"""Manager entry used by thunks whose target is already resident."""

HEADER_CONSTANT = 700
"""The u32 at +0x04 of every overlay header in this build."""


@dataclass
class Overlay:
    """One overlay image in the container."""

    index: int
    """1 based, matching the overlay number stored in a thunk."""

    header_offset: int
    code_offset: int

    code_length: int
    """Readable code bytes. The last overlay is padded to a paragraph, so this is clamped to
    the end of the file and can be smaller than the header's nominal size."""

    nominal_end: int
    """Where the header says this image ends, and where the next one begins."""

    total_paragraphs: int
    header_paragraphs: int
    constant: int
    reloc_offsets: list = field(default_factory=list)

    @property
    def end_offset(self):
        return self.code_offset + self.code_length

    def contains(self, file_offset):
        return self.code_offset <= file_offset < self.end_offset


@dataclass
class Thunk:
    """A far call trampoline in the resident image."""

    site: int
    """File offset of the thunk itself."""

    kind: str
    """'overlay' or 'resident'."""

    target_offset: int
    """Entry offset within the target's segment."""

    length: int
    """14 or 12 for an overlay thunk, 10 for a resident one."""

    overlay: int = 0
    """Overlay number, 0 for a resident thunk."""

    target_segment: int = 0
    """Link time segment of a resident target, or for an overlay thunk the paragraph its
    segment starts at within the overlay's code."""


def _u16(data, offset):
    return struct.unpack_from("<H", data, offset)[0]


def _u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def load_module_offset(data):
    """File offset of the MZ load module, which link time segments are relative to."""
    return _u16(data, 0x08) * 16


def resident_end(data):
    """End of the MZ load module.

    `pages * 512` alone overshoots whenever extra_bytes is set, which is the trap that
    makes the container look like it starts at a slightly wrong place.
    """
    pages = _u16(data, 0x04)
    extra = _u16(data, 0x02)
    end = pages * 512
    if extra:
        end -= 512 - extra
    return end


def container_offset(data):
    """Start of the overlay container: the resident end, rounded up to a paragraph."""
    return (resident_end(data) + 15) & ~15


def parse_overlays(data):
    """Walk the overlay chain. Raises ValueError if the chain does not close at EOF."""
    overlays = []
    offset = container_offset(data)
    index = 1

    while offset < len(data):
        if offset + 16 > len(data):
            raise ValueError(f"overlay {index} header runs past EOF at 0x{offset:X}")

        total = _u16(data, offset)
        header_paragraphs = _u16(data, offset + 2)
        constant = _u32(data, offset + 4)
        count = _u32(data, offset + 8)

        if total == 0 or header_paragraphs == 0 or header_paragraphs >= total:
            raise ValueError(f"overlay {index} has an implausible header at 0x{offset:X}")

        code = offset + header_paragraphs * 16
        end = offset + total * 16

        if offset + 16 + count * 4 > code:
            raise ValueError(f"overlay {index} relocation table overruns its code")

        overlays.append(
            Overlay(
                index=index,
                header_offset=offset,
                code_offset=code,
                code_length=min(end, len(data)) - code,
                nominal_end=end,
                total_paragraphs=total,
                header_paragraphs=header_paragraphs,
                constant=constant,
                reloc_offsets=[
                    _u16(data, offset + 18 + 4 * i) * 16 + _u16(data, offset + 16 + 4 * i)
                    for i in range(count)
                ],
            )
        )

        offset = end
        index += 1

    # The last image is padded to a paragraph, so it may overhang the final bytes.
    last = overlays[-1]
    overhang = last.nominal_end - len(data)
    if not 0 <= overhang < 16:
        raise ValueError(f"overlay chain ends 0x{overhang:X} from EOF, expected under 0x10")

    return overlays


def thunk_site(data, segment, offset):
    """File offset of the thunk a far call `segment:offset` refers to."""
    return load_module_offset(data) + segment * 16 + offset


def _is_thunk_start(data, site):
    """A far call into the overlay manager followed by a far jump, which every thunk is."""
    return (
        site + 10 <= len(data)
        and data[site] == 0x9A
        and data[site + 5] == 0xEA
        and _u16(data, site + 3) == OVERLAY_MANAGER
    )


def read_thunk(data, site, overlay_count):
    """Decode a thunk at `site`, or None if there isn't one there.

    `overlay_count` tells an overlay thunk from a resident thunk into link segment 0, and
    rejects the handful of byte sequences in data that happen to match the shape.
    """
    if site + 12 > len(data) or not _is_thunk_start(data, site):
        return None

    target_offset = _u16(data, site + 6)
    target_segment = _u16(data, site + 8)
    number = _u16(data, site + 10)

    if target_segment == 0 and 1 <= number <= overlay_count:
        # the segment word is left out when another thunk follows straight away
        if site + 14 > len(data) or _is_thunk_start(data, site + 12):
            return Thunk(site=site, kind="overlay", target_offset=target_offset,
                         length=12, overlay=number)
        return Thunk(site=site, kind="overlay", target_offset=target_offset, length=14,
                     overlay=number, target_segment=_u16(data, site + 12))

    return Thunk(
        site=site,
        kind="resident",
        target_offset=target_offset,
        length=10,
        target_segment=target_segment,
    )


def enumerate_thunks(data, overlay_count):
    """Every thunk in the resident image, scanned in order."""
    thunks = []
    offset = load_module_offset(data)
    end = resident_end(data)

    while offset < end - 12:
        thunk = read_thunk(data, offset, overlay_count)
        if thunk is None:
            offset += 1
        else:
            thunks.append(thunk)
            offset += thunk.length

    return thunks


def resolve(overlays, thunk):
    """File offset an overlay thunk points at, or None if it can't be resolved."""
    if thunk.kind != "overlay":
        return None
    overlay = overlays[thunk.overlay - 1]
    target = overlay.code_offset + thunk.target_segment * 16 + thunk.target_offset
    return target if overlay.contains(target) else None


def locate(overlays, file_offset):
    """(overlay number, offset within its code) for a file offset, or (None, None)."""
    for overlay in overlays:
        if overlay.contains(file_offset):
            return overlay.index, file_offset - overlay.code_offset
    return None, None


def directory(overlays):
    """The overlay directory, in the shape the C# loader is expected to emit."""
    return [
        {
            "index": o.index,
            "header": o.header_offset,
            "code": o.code_offset,
            "length": o.code_length,
            "relocations": len(o.reloc_offsets),
        }
        for o in overlays
    ]


def _main(argv):
    import json
    import sys

    path = None
    dump = False

    for arg in argv[1:]:
        if arg == "--dump":
            dump = True
        else:
            path = arg

    if path is None:
        print("usage: col1_overlay.py [--dump] <VICEROY.EXE>", file=sys.stderr)
        return 2

    with open(path, "rb") as handle:
        data = handle.read()

    overlays = parse_overlays(data)

    if dump:
        print(json.dumps(directory(overlays), indent=2))
        return 0

    thunks = enumerate_thunks(data, len(overlays))
    overlay_thunks = sum(1 for t in thunks if t.kind == "overlay")

    print(f"load module      0x{load_module_offset(data):06X}")
    print(f"resident ends    0x{resident_end(data):06X}")
    print(f"container starts 0x{container_offset(data):06X}")
    print(f"overlays         {len(overlays)}")
    print(f"thunks           {len(thunks)} ({overlay_thunks} overlay, "
          f"{len(thunks) - overlay_thunks} resident)")
    print()
    print(f"{'ov':>3} {'header':>8} {'code':>8} {'length':>7} {'relocs':>7}")
    for o in overlays:
        print(f"{o.index:3d} 0x{o.header_offset:06X} 0x{o.code_offset:06X} "
              f"0x{o.code_length:05X} {len(o.reloc_offsets):7d}")
    return 0


if __name__ == "__main__":
    import sys

    sys.exit(_main(sys.argv))
