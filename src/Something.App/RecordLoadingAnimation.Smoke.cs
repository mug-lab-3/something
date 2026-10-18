namespace Something.App;

public sealed partial class RecordLoadingAnimation
{
    internal void PauseForPreview(double seconds)
    {
        if (!IsPlaying)
            return;
        activeCycle?.Pause();
        activeCycle?.Seek(TimeSpan.FromSeconds(seconds));
    }

    internal void ResumeAfterPreview()
    {
        if (IsPlaying)
            activeCycle?.Resume();
    }
}
