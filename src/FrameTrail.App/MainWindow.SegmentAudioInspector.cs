using System.Windows;

namespace FrameTrail.App;

public partial class MainWindow
{
    private Guid? selectedScreenSegmentId;

    private void BuildSegmentAudioInspector(Guid segmentId)
    {
        var project = this.project!;
        var segment = project.Timeline.Segments.FirstOrDefault(x => x.Id == segmentId);
        if (segment is null) return;

        Section("所选片段音量");
        Note("未单独设置的项目继承全片音量；覆盖只作用于当前屏幕片段。隐藏音轨不会改变混音。");
        var overrides = segment.AudioOverrides;
        var systemTrack = project.AudioTracks.FirstOrDefault(x => x.Source == AudioTrackSource.System);
        Toggle("静音系统声音", overrides?.SystemMuted ?? (project.Settings.MainAudioMuted || systemTrack?.Muted == true),
            value => SetSegmentAudioOverride(segmentId, x => x.SystemMuted = value));
        Number("系统声音", Math.Clamp(overrides?.SystemVolume ?? project.Settings.MainAudioVolume, 0, 1) * 100, 0, 100,
            value => SetSegmentAudioOverride(segmentId, x => x.SystemVolume = value / 100), "%", "0");

        var microphone = project.AudioTracks.FirstOrDefault(x => x.Source == AudioTrackSource.Microphone);
        if (microphone is not null)
        {
            Toggle("静音麦克风", overrides?.MicrophoneMuted ?? microphone.Muted,
                value => SetSegmentAudioOverride(segmentId, x => x.MicrophoneMuted = value));
            Number("麦克风", Math.Clamp(overrides?.MicrophoneVolume ?? microphone.Volume, 0, 1) * 100, 0, 100,
                value => SetSegmentAudioOverride(segmentId, x => x.MicrophoneVolume = value / 100), "%", "0");
        }
        else
        {
            Note("此项目没有标记为麦克风来源的音轨；导入的普通音轨不自动归类为麦克风。");
        }

        if (overrides is not null)
            ActionButton("恢复全片音量", () => ApplyEdit(p =>
            {
                var target = p.Timeline.Segments.FirstOrDefault(x => x.Id == segmentId);
                if (target is not null) target.AudioOverrides = null;
            }));
    }

    private void SetSegmentAudioOverride(Guid segmentId, Action<PrimarySegmentAudioOverrides> update)
    {
        ApplyEdit(p =>
        {
            var segment = p.Timeline.Segments.FirstOrDefault(x => x.Id == segmentId);
            if (segment is null) return;
            var overrides = segment.AudioOverrides ??= new PrimarySegmentAudioOverrides();
            update(overrides);
            if (overrides.IsEmpty) segment.AudioOverrides = null;
        });
    }
}
