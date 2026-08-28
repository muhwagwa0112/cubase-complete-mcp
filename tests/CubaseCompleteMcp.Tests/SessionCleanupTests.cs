using CubaseCompleteMcp.Core;

namespace CubaseCompleteMcp.Tests;

public sealed class SessionCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cbmcp-cleanup-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PreviewAndCommit_DeleteOnlyOldSession()
    {
        var old = WriteSession('a', _now.AddDays(-40), "old");
        var recent = WriteSession('b', _now.AddDays(-2), "recent");
        var service = Service();
        var proposal = service.Preview(30);
        var candidate = Assert.Single(proposal.Candidates);
        Assert.Equal(Path.GetFileName(old), candidate.SessionId);
        Assert.True(Directory.Exists(old));

        var result = service.Commit(30, proposal.ConfirmationId);
        Assert.Equal(1, result.DeletedSessions);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.Throws<InvalidOperationException>(() => service.Commit(30, proposal.ConfirmationId));
        Assert.Throws<ArgumentException>(() => service.Commit(30, "not-a-confirmation"));
    }

    [Fact]
    public void ChangedCandidateAndExpiredProposal_AreRejectedWithoutDeletion()
    {
        var session = WriteSession('c', _now.AddDays(-40), "before");
        var service = Service();
        var changed = service.Preview(30);
        File.AppendAllText(Path.Combine(session, "document.json"), "after");
        Assert.Throws<InvalidOperationException>(() => service.Commit(30, changed.ConfirmationId));
        Assert.True(Directory.Exists(session));

        SetSessionTime(session, _now.AddDays(-40));
        var expired = service.Preview(30);
        _now = _now.AddMinutes(6);
        Assert.Throws<InvalidOperationException>(() => service.Commit(30, expired.ConfirmationId));
        Assert.True(Directory.Exists(session));
    }

    [Fact]
    public void ActiveBroker_BlocksPreviewAndCommit()
    {
        var service = new SessionCleanupService(_root, () => true, () => _now);
        Assert.Throws<InvalidOperationException>(() => service.Preview(30));
    }

    private SessionCleanupService Service() => new(_root, () => false, () => _now);

    private string WriteSession(char value, DateTimeOffset timestamp, string content)
    {
        var directory = Path.Combine(_root, "compositions", new string(value, 24));
        Directory.CreateDirectory(Path.Combine(directory, "renders"));
        File.WriteAllText(Path.Combine(directory, "document.json"), content);
        File.WriteAllText(Path.Combine(directory, "renders", "render.mid"), content);
        SetSessionTime(directory, timestamp);
        return directory;
    }

    private static void SetSessionTime(string directory, DateTimeOffset timestamp)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, timestamp.UtcDateTime);
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            Directory.SetLastWriteTimeUtc(child, timestamp.UtcDateTime);
        Directory.SetLastWriteTimeUtc(directory, timestamp.UtcDateTime);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
