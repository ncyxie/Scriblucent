using NAudio.Wave;

namespace Scriblucent.Services;

/// <summary>
/// Plays one audio clip at a time (WAV/MP3) via NAudio, instead of WPF's built-in MediaPlayer -
/// WPF's media APIs are wired to the legacy Windows Media Player desktop component, which many
/// modern Windows installs don't have (surfaces as "Windows Media Player version 10 or later is
/// required" even though the file itself is fine). NAudio talks to Windows' audio output directly
/// and needs no such dependency. Supports play/pause/restart - deliberately no seek/scrub, since
/// flashcard clips are short (a word, a phrase), not long enough to need a timeline.
/// </summary>
public sealed class AudioClipPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private string? _currentPath;
    private float _volume = 1f;

    public event EventHandler<Exception>? PlaybackFailed;

    /// <summary>Fires whenever play/pause/stop state changes, so a caller tracking "which card's
    /// audio is this" can update its own UI (e.g. swap a Play icon for a Pause icon).</summary>
    public event EventHandler? PlaybackStateChanged;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>0.0-1.0. A single remembered level applied to whatever plays next (and to the
    /// current clip immediately, if one is loaded) - not a per-clip setting.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_reader is not null)
                _reader.Volume = _volume;
        }
    }

    /// <summary>Playing a different file than whatever's currently loaded starts it fresh from the
    /// beginning; playing the same file toggles pause/resume in place.</summary>
    public void TogglePlayPause(string path)
    {
        if (_output is null || _reader is null || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Play(path);
            return;
        }

        if (_output.PlaybackState == PlaybackState.Playing)
            _output.Pause();
        else
            _output.Play();

        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops and releases whatever's currently loaded, if anything - callers should call
    /// this whenever "which card is current" changes, so a clip doesn't keep playing in the
    /// background after navigating away from the card it belongs to.</summary>
    public void Stop()
    {
        if (_output is null && _reader is null)
            return;

        DisposeCurrent();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeks back to the start and plays - if a different (or no) file is currently
    /// loaded, this is the same as a fresh Play, which already starts at the beginning.</summary>
    public void Restart(string path)
    {
        if (_output is null || _reader is null || !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Play(path);
            return;
        }

        _reader.CurrentTime = TimeSpan.Zero;
        _output.Play();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Play(string path)
    {
        DisposeCurrent();

        try
        {
            _reader = new AudioFileReader(path) { Volume = _volume };
            _output = new WaveOutEvent();
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_reader);
            _output.Play();
            _currentPath = path;
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DisposeCurrent();
            PlaybackFailed?.Invoke(this, ex);
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        var exception = e.Exception;
        DisposeCurrent();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        if (exception is not null)
            PlaybackFailed?.Invoke(this, exception);
    }

    /// <summary>Unsubscribes before disposing so a Play()-triggered replacement of the current
    /// clip doesn't re-enter OnPlaybackStopped via the outgoing WaveOutEvent's own Stop/Dispose.</summary>
    private void DisposeCurrent()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }

        _reader?.Dispose();
        _reader = null;
        _currentPath = null;
    }

    public void Dispose() => DisposeCurrent();
}
