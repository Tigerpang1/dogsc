"""Pure regression test for ffprobe frame JSON containing H.264 side data."""
import importlib.util
from pathlib import Path


analyzer_path = Path(__file__).with_name("analyze-ffplay-sync-v2.py")
spec = importlib.util.spec_from_file_location("sync_analyzer", analyzer_path)
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)

sample = b'{"frames":[{"best_effort_timestamp_time":"0.000000","side_data_list":[{"side_data_type":"H.26[45] User Data Unregistered SEI message"}]},{"best_effort_timestamp_time":"0.033333","side_data_list":[{"side_data_type":"H.26[45] User Data Unregistered SEI message"}]}]}'
actual = analyzer.parse_video_timestamps_json(sample).tolist()
expected = [0.0, 0.033333]
if actual != expected:
    raise AssertionError(f"H.264 side data polluted frame PTS parsing: {actual}")

for invalid in (b'{"streams":[]}', b'{"frames":[{"side_data_list":[]}]}', b'{"frames":[{"best_effort_timestamp_time":"N/A"}]}'):
    try:
        analyzer.parse_video_timestamps_json(invalid)
    except ValueError:
        continue
    raise AssertionError(f"Invalid ffprobe frame JSON was accepted: {invalid!r}")

print("ffprobe JSON frame parser tests passed: H.264 side data is ignored; missing/invalid PTS is rejected.")
