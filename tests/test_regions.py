import numpy as np
import pytest

from claude_live_conversation_assistant.regions import ChangeDetector, Region, downsample_gray, drag_to_region


def test_region_parse():
    assert Region.parse("10,20,300,200") == Region(10, 20, 300, 200)
    with pytest.raises(ValueError):
        Region.parse("10,20,300")
    with pytest.raises(ValueError):
        Region.parse("10,20,0,200")


def test_drag_maps_canvas_to_physical_pixels_in_any_direction():
    r = drag_to_region(origin=(100, 50), scale=(2.0, 2.0), p0=(30, 40), p1=(10, 10))
    assert r == Region(left=120, top=70, width=40, height=60)


def test_drag_origin_can_be_negative_for_left_hand_monitors():
    r = drag_to_region((-1920, 0), (1.0, 1.0), (100, 100), (400, 300))
    assert r == Region(-1820, 100, 300, 200)


def test_tiny_drag_is_rejected():
    assert drag_to_region((0, 0), (1, 1), (5, 5), (10, 9)) is None


def test_downsample_keeps_thin_dark_text_visible():
    bgra = np.full((300, 800, 4), 255, dtype=np.uint8)
    bgra[100:102, 100:500, :3] = 0  # 2px black line, like underlined text
    small = downsample_gray(bgra)
    assert small.shape[1] <= 160 and small.min() < 200


def frame(seed_text=0, size=(60, 160)):
    """A 'screen' with some dark blocks; seed_text moves/adds blocks."""
    f = np.full(size, 240, dtype=np.float32)
    for i in range(seed_text):
        f[10 + 8 * i : 14 + 8 * i, 10:100] = 20
    return f


def run(det, frames):
    """frames: list of (t, frame). Returns times at which the detector fired."""
    return [t for t, f in frames if det.update(f, now=t)]


def test_no_change_never_fires():
    det = ChangeDetector()
    assert run(det, [(t * 0.5, frame(2)) for t in range(40)]) == []


def test_fires_once_after_change_settles():
    det = ChangeDetector(settle_s=1.0)
    seq = [(0.0, frame(1)), (0.5, frame(1)), (1.0, frame(2)), (1.5, frame(2)), (2.0, frame(2)), (2.5, frame(2)), (3.0, frame(2))]
    fired = run(det, seq)
    assert fired == [2.0]  # the change appeared at 1.0 and the content stayed still for 1s after it


def test_does_not_fire_while_still_changing():
    det = ChangeDetector(settle_s=1.0, max_wait_s=100)
    seq = [(0.0, frame(1))] + [(0.5 * i, frame(1 + i)) for i in range(1, 6)]
    assert run(det, seq) == []
    # once typing stops, it fires
    assert run(det, [(3.0 + 0.5 * i, frame(6)) for i in range(5)]) != []


def test_blinking_cursor_is_ignored():
    det = ChangeDetector()
    base = frame(2)
    blink = base.copy()
    blink[40:48, 105:107] = 20  # a 2x8 caret
    seq = [(0.5 * i, blink if i % 2 else base) for i in range(40)]
    assert run(det, seq) == []


def test_returning_to_baseline_cancels_pending_change():
    det = ChangeDetector(settle_s=1.0)
    seq = [(0.0, frame(1)), (0.5, frame(3)), (1.0, frame(1)), (1.5, frame(1)), (3.0, frame(1))]
    assert run(det, seq) == []


def moving(i):
    """A block that jumps to a new place every sample (like video), never repeating soon."""
    f = np.full((60, 160), 240, dtype=np.float32)
    row = 2 + (i * 7) % 45
    f[row : row + 6, 20:140] = 20
    return f


def test_continuous_motion_fires_at_max_wait_then_rate_limits():
    det = ChangeDetector(settle_s=1.0, max_wait_s=4.0, min_interval_s=3.0)
    fired = run(det, [(0.5 * i, moving(i)) for i in range(40)])
    assert 2 <= len(fired) <= 6
    assert all(b - a >= 3.0 for a, b in zip(fired, fired[1:]))


def test_pending_is_true_only_between_change_and_report():
    det = ChangeDetector(settle_s=1.0)
    det.update(frame(1), now=0.0)
    assert not det.pending
    det.update(frame(3), now=0.5)
    assert det.pending
    det.update(frame(3), now=1.0)
    assert det.pending
    assert det.update(frame(3), now=1.5) is True
    assert not det.pending
