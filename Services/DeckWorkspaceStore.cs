using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace AnkiDeck.Services;

/// <summary>Keeps uploaded decks on the server, keyed by the identifier stored in the user's session.</summary>
public sealed class DeckWorkspaceStore : IDisposable
{
    public const string SessionKey = "ankideck.workspace";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    private readonly ConcurrentDictionary<string, DeckWorkspace> _workspaces = new(StringComparer.Ordinal);

    public DeckWorkspace Add(DeckWorkspace workspace)
    {
        RemoveExpired();

        workspace.Id = Guid.NewGuid().ToString("N");
        workspace.LastAccessUtc = DateTimeOffset.UtcNow;
        _workspaces[workspace.Id] = workspace;
        return workspace;
    }

    public bool TryGet(string? id, [NotNullWhen(true)] out DeckWorkspace? workspace)
    {
        if (!string.IsNullOrEmpty(id) && _workspaces.TryGetValue(id, out workspace))
        {
            workspace.LastAccessUtc = DateTimeOffset.UtcNow;
            return true;
        }

        workspace = null;
        return false;
    }

    public void Remove(string? id)
    {
        if (!string.IsNullOrEmpty(id) && _workspaces.TryRemove(id, out var workspace))
        {
            workspace.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var id in _workspaces.Keys)
        {
            Remove(id);
        }
    }

    private void RemoveExpired()
    {
        var threshold = DateTimeOffset.UtcNow - Lifetime;
        foreach (var pair in _workspaces)
        {
            if (pair.Value.LastAccessUtc < threshold && _workspaces.TryRemove(pair.Key, out var expired))
            {
                expired.Dispose();
            }
        }
    }
}
