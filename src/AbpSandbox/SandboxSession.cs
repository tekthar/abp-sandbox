using Microsoft.Extensions.Logging;

namespace AbpSandbox;

/// <summary>
/// A kept container (docs/adr/0001, decision 2). Its calls take turns; each use moves its expiry to its idle time from
/// now, never past its lifetime. A command past its time closes it: a command can't be stopped alone, and what it
/// left running isn't something the next call should find.
/// </summary>
internal sealed class SandboxSession(
    Sandbox sandbox,
    ISandboxContainer container,
    string owner,
    string key,
    string image,
    string shape,
    TimeSpan commandTime,
    TimeSpan idleTime,
    DateTimeOffset openedAt) : ISandboxSession
{
    private readonly SemaphoreSlim turn = new(1, 1);
    private DateTimeOffset usedAt = openedAt;
    private int closed;

    public string Id => container.Id;

    public string Owner => owner;

    public string Key => key;

    public string Image => image;

    /// <summary>The image and preparation the session was opened with: opening with another closes this one.</summary>
    public string Shape => shape;

    public DateTimeOffset OpenedAt { get; } = openedAt;

    public DateTimeOffset ExpiresAt
    {
        get
        {
            DateTimeOffset idle = usedAt + idleTime;
            DateTimeOffset end = OpenedAt + sandbox.Policy.Options.SessionLifetime;
            return idle < end ? idle : end;
        }
    }

    public bool IsClosed => Volatile.Read(ref closed) == 1;

    public bool Expired(DateTimeOffset now) => now >= ExpiresAt;

    public void Touch() => usedAt = sandbox.Clock.GetUtcNow();

    public async Task<SandboxExecResult> ExecAsync(IReadOnlyList<string> command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        SandboxPolicy.Command(command);
        TimeSpan time = timeout ?? commandTime;
        if (time <= TimeSpan.Zero || time > sandbox.Policy.Options.MaxTime)
        {
            throw new SandboxRefusedException($"A sandbox command's time is at most {sandbox.Policy.Options.MaxTime.TotalSeconds:0} seconds.");
        }

        return await InTurnAsync(async () =>
        {
            SandboxExecResult result = await container.ExecAsync(command, time, sandbox.Policy.Options.MaxConsoleBytes, cancellationToken);
            if (result.TimedOut)
            {
                sandbox.Logger.LogInformation("Sandbox session {Session} closed: a command ran past its time.", Id);
                await CloseInTurnAsync();
            }

            return result;
        }, cancellationToken);
    }

    public Task PutFilesAsync(IReadOnlyList<SandboxFile> files, CancellationToken cancellationToken = default)
    {
        sandbox.Policy.Input(files);
        return InTurnAsync(async () =>
        {
            await container.PutFilesAsync(files, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<SandboxFiles> GetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        sandbox.Policy.Outputs(paths);
        return InTurnAsync(() => container.GetFilesAsync(paths, sandbox.Policy.Options.MaxOutputBytes, cancellationToken), cancellationToken);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (IsClosed)
        {
            return;
        }

        await turn.WaitAsync(cancellationToken);
        try
        {
            await CloseInTurnAsync();
        }
        finally
        {
            turn.Release();
        }
    }

    private async Task CloseInTurnAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 1)
        {
            return;
        }

        sandbox.Forget(this);
        await sandbox.RemoveQuietlyAsync(container);
    }

    private async Task<T> InTurnAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            if (IsClosed || Expired(sandbox.Clock.GetUtcNow()))
            {
                throw new SandboxRefusedException("The sandbox session is closed; open it again.");
            }

            T result = await work();
            Touch();
            return result;
        }
        finally
        {
            turn.Release();
        }
    }
}
