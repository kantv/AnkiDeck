namespace AnkiDeck.Services;

public enum BulkAudioStatus
{
    Running,
    Paused,
    Cancelling,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>Serializable progress snapshot for the bulk audio refresh dialog.</summary>
public sealed record BulkAudioSnapshot(
    bool Active,
    string Status,
    int TotalNotes,
    int CompletedNotes,
    int TotalSegments,
    int CompletedSegments,
    string CurrentText,
    string? Error);

/// <summary>Thread-safe state of a background "refresh all audio" operation.</summary>
public sealed class BulkAudioJob
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _totalNotes;
    private readonly int _totalSegments;

    private BulkAudioStatus _status = BulkAudioStatus.Running;
    private bool _paused;
    private int _completedNotes;
    private int _completedSegments;
    private string _currentText = string.Empty;
    private string? _error;

    public BulkAudioJob(int totalNotes, int totalSegments)
    {
        _totalNotes = totalNotes;
        _totalSegments = totalSegments;
    }

    public CancellationToken Token => _cancellation.Token;

    public void SetCurrentText(string text)
    {
        lock (_gate)
        {
            _currentText = text;
        }
    }

    public void SegmentCompleted()
    {
        lock (_gate)
        {
            _completedSegments++;
        }
    }

    public void NoteCompleted()
    {
        lock (_gate)
        {
            _completedNotes++;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_status != BulkAudioStatus.Running)
            {
                return;
            }

            _paused = true;
            _status = BulkAudioStatus.Paused;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_status != BulkAudioStatus.Paused)
            {
                return;
            }

            _paused = false;
            _status = BulkAudioStatus.Running;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_status is BulkAudioStatus.Completed or BulkAudioStatus.Cancelled or BulkAudioStatus.Failed)
            {
                return;
            }

            _status = BulkAudioStatus.Cancelling;
        }

        _cancellation.Cancel();
    }

    public void Complete()
    {
        lock (_gate)
        {
            _status = BulkAudioStatus.Completed;
            _currentText = string.Empty;
        }
    }

    public void MarkCancelled()
    {
        lock (_gate)
        {
            _status = BulkAudioStatus.Cancelled;
            _currentText = string.Empty;
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _error = error;
            _status = BulkAudioStatus.Failed;
            _currentText = string.Empty;
        }
    }

    public async Task WaitIfPausedAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(_cancellation.Token);
                }

                if (!_paused)
                {
                    return;
                }
            }

            await Task.Delay(200, _cancellation.Token);
        }
    }

    public BulkAudioSnapshot Snapshot()
    {
        lock (_gate)
        {
            var active = _status is BulkAudioStatus.Running or BulkAudioStatus.Paused or BulkAudioStatus.Cancelling;
            return new BulkAudioSnapshot(
                active,
                _status.ToString().ToLowerInvariant(),
                _totalNotes,
                _completedNotes,
                _totalSegments,
                _completedSegments,
                _currentText,
                _error);
        }
    }
}
