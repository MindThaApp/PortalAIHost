namespace Portal.AiHost.Client;

/// <summary>Keeps the app's access token between runs. Implementations must protect it at rest.</summary>
public interface ITokenStore
{
    /// <summary>Returns the stored token, or null if none (or it cannot be read).</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Stores the token, replacing this app's earlier one.</summary>
    Task SaveAsync(string token, CancellationToken cancellationToken);

    /// <summary>Deletes this app's token only. Must not touch anything else.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>A token store that lives only in memory. For tests and short-lived tools.</summary>
public sealed class MemoryTokenStore : ITokenStore
{
    private string? _token;

    /// <summary>Creates the store, optionally holding a token.</summary>
    public MemoryTokenStore(string? token = null) => _token = token;

    /// <inheritdoc />
    public Task<string?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_token);

    /// <inheritdoc />
    public Task SaveAsync(string token, CancellationToken cancellationToken)
    {
        _token = token;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        _token = null;
        return Task.CompletedTask;
    }
}
