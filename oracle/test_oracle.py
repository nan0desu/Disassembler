"""Ground truth for the VICEROY.EXE overlay container.

The facts in REPO_SITES and THUNK_TARGETS were established by someone else, by runtime
measurement under QEMU with hardware watchpoints, and published in
https://github.com/fcmircea/col1-traderoute-boycott-fix (docs/ANALYSIS.md). They were not
derived from this decoder, which is what makes them worth testing against: a decoder that
reproduces them statically is being checked, not confirmed by its own assumptions.

Run:
    python3 -m unittest discover -s oracle

The binary is not redistributable. Point COL1_VICEROY_EXE at your own copy, or leave it
unset to use GameSRC/COLONIZE/VICEROY.EXE. Tests skip when it is absent.
"""

import hashlib
import json
import os
import pathlib
import struct
import unittest

import col1_overlay as col1

PRISTINE_MD5 = "0f5d5b0063721fbc6aca314e5a43ddaf"
PRISTINE_SIZE = 494910

OVERLAY_COUNT = 31

# Offsets published in docs/ANALYSIS.md, with the bytes each one must start with.
# 'overlay' is this decoder's prediction and is what the test is really exercising;
# the offset and the bytes are the part that came from outside.
REPO_SITES = [
    (0x2A6A6, "c8 0e 00 00", 2, "unload routine entry, enter 0xe,0"),
    (0x2A874, "01 80 9a 00", 2, "deposit store, add [bx+si+0x9A], ax"),
    (0x41080, "c8 42 00 00", 8, "route stop function"),
    (0x411D8, "c7 46 e2 00", 8, "unload loop start, i = 0"),
    (0x411E8, "9a 94 05 1f 19", 8, "unload(unit, good, 0) to a colony"),
    (0x41204, "9a 1c 02 1f 1a", 8, "unload_list_get(i)"),
    (0x41210, "9a d8 0c 1f 19", 8, "skip_test(good)"),
    (0x4121A, "75 da", 8, "traderoute-boycott patch site"),
    (0x41222, "9a 2c 0c 1f 18", 8, "find_hold(unit, good)"),
    (0x41240, "9a 02 0d 1f 19", 8, "unload to Europe"),
    (0x41354, "9a f8 07 1f 19", 8, "colony load call"),
    (0x41384, "74 03", 8, "traderoute-topup patch site"),
    (0x6E3D0, "c8 38 00 00", 23, "route processor"),
    (0x6E43B, "23 16 54 1f", 23, "and dx, [0x1f54], the documented dead end"),
    (0x745AB, "85 06 54 1f", 26, "UI loop greying out boycotted goods"),
]

# Far calls in ANALYSIS.md, and where this decoder says each one lands.
THUNK_TARGETS = [
    (0x191F, 0x0594, 2, 0x4DA6, 0x2A6A6, "unload"),
    (0x191F, 0x07F8, 2, 0x4B62, 0x2A462, "colony load"),
    (0x191F, 0x0CD8, 4, 0x05E8, 0x30B38, "skip_test"),
    (0x1A1F, 0x021C, 18, 0x057A, 0x603DA, "unload_list_get"),
]

# skip_test, in full. 'and ax,[bx+0x20]' is the boycott bitmap at +0x20 of the nation
# record, which is where ANALYSIS.md independently places it from the save file.
SKIP_TEST = "55 8b ec b8 01 00 8a 4e 06 d3 e0 8b 1e fc 84 23 47 20 c9 cb"


def _find_binary():
    override = os.environ.get("COL1_VICEROY_EXE")
    if override:
        return pathlib.Path(override)
    root = pathlib.Path(__file__).resolve().parent.parent
    return root / "GameSRC" / "COLONIZE" / "VICEROY.EXE"


def _load():
    path = _find_binary()
    if not path.is_file():
        raise unittest.SkipTest(f"VICEROY.EXE not found at {path}")
    return path.read_bytes()


def _hex(data, offset, length):
    return " ".join(f"{b:02x}" for b in data[offset : offset + length])


class OverlayContainerTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = _load()
        cls.overlays = col1.parse_overlays(cls.data)

    def test_binary_is_the_pristine_v30_build(self):
        self.assertEqual(len(self.data), PRISTINE_SIZE)
        self.assertEqual(hashlib.md5(self.data).hexdigest(), PRISTINE_MD5)

    def test_resident_end_accounts_for_extra_bytes(self):
        # pages * 512 alone gives 0x20800, which is 0x19B too far and lands in padding.
        self.assertEqual(col1.resident_end(self.data), 0x20665)
        self.assertEqual(col1.container_offset(self.data), 0x20670)
        self.assertEqual(col1.load_module_offset(self.data), 0x2400)

    def test_chain_closes_at_eof(self):
        self.assertEqual(len(self.overlays), OVERLAY_COUNT)
        self.assertEqual(self.overlays[0].header_offset, 0x20670)
        self.assertEqual(self.overlays[0].code_offset, 0x20EE0)

        for previous, overlay in zip(self.overlays, self.overlays[1:]):
            self.assertEqual(previous.nominal_end, overlay.header_offset)

        overhang = self.overlays[-1].nominal_end - len(self.data)
        self.assertGreaterEqual(overhang, 0)
        self.assertLess(overhang, 16)

    def test_every_header_carries_the_same_constant(self):
        for overlay in self.overlays:
            self.assertEqual(overlay.constant, col1.HEADER_CONSTANT, f"overlay {overlay.index}")

    def test_overlay_thunks_resolve_within_their_overlay(self):
        thunks = col1.enumerate_thunks(self.data, len(self.overlays))
        overlay_thunks = [t for t in thunks if t.kind == "overlay"]

        self.assertGreater(len(overlay_thunks), 600)
        for thunk in overlay_thunks:
            self.assertIsNotNone(
                col1.resolve(self.overlays, thunk),
                f"thunk at 0x{thunk.site:X} -> overlay {thunk.overlay} +0x{thunk.target_offset:X}",
            )

    def test_repo_offsets_land_where_the_decoder_says(self):
        for offset, expected, overlay_number, label in REPO_SITES:
            with self.subTest(label):
                length = len(expected.split())
                self.assertEqual(_hex(self.data, offset, length), expected)

                found, _ = col1.locate(self.overlays, offset)
                if offset < col1.resident_end(self.data):
                    self.assertIsNone(found, "resident site should not be in an overlay")
                else:
                    self.assertEqual(found, overlay_number)

    def test_documented_far_calls_resolve_to_documented_bodies(self):
        for segment, offset, number, entry, target, label in THUNK_TARGETS:
            with self.subTest(label):
                site = col1.thunk_site(self.data, segment, offset)
                thunk = col1.read_thunk(self.data, site, len(self.overlays))

                self.assertIsNotNone(thunk, f"no thunk at 0x{site:X}")
                self.assertEqual(thunk.kind, "overlay")
                self.assertEqual(thunk.overlay, number)
                self.assertEqual(thunk.target_offset, entry)
                self.assertEqual(col1.resolve(self.overlays, thunk), target)

    def test_skip_test_is_the_boycott_check(self):
        site = col1.thunk_site(self.data, 0x191F, 0x0CD8)
        thunk = col1.read_thunk(self.data, site, len(self.overlays))
        body = col1.resolve(self.overlays, thunk)

        self.assertEqual(_hex(self.data, body, 20), SKIP_TEST)
        # the next function starts immediately after, confirming the 20 byte boundary
        self.assertEqual(_hex(self.data, body + 20, 4), "c8 04 00 00")

    def test_patch_sites_are_unmodified(self):
        # guards against testing a patched copy, which would move real bugs out of view
        self.assertEqual(_hex(self.data, 0x4121A, 2), "75 da")
        self.assertEqual(_hex(self.data, 0x41384, 2), "74 03")


class DirectoryDumpTest(unittest.TestCase):
    """Checks a directory produced by another implementation against this one.

    Set COL1_DIRECTORY_JSON to a file written by the C# loader in the same shape as
    `python3 oracle/col1_overlay.py --dump`. Skipped when unset, so this is inert until
    there is a second implementation to compare.
    """

    def test_matches_reference(self):
        path = os.environ.get("COL1_DIRECTORY_JSON")
        if not path:
            raise unittest.SkipTest("COL1_DIRECTORY_JSON not set")

        data = _load()
        reference = [
            {
                "index": o.index,
                "header": o.header_offset,
                "code": o.code_offset,
                "length": o.code_length,
                "relocations": len(o.reloc_offsets),
            }
            for o in col1.parse_overlays(data)
        ]
        actual = json.loads(pathlib.Path(path).read_text())
        self.assertEqual(actual, reference)


if __name__ == "__main__":
    unittest.main()
