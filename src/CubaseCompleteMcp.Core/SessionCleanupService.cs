using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CubaseCompleteMcp.Core;

public sealed record SessionCleanupFile(string RelativePath, long Bytes, DateTimeOffset LastWriteAt, string Sha256);
public sealed record SessionCleanupCandidate(string SessionId, string Path, DateTimeOffset LastWriteAt,
    int FileCount, long Bytes, SessionCleanupFile[] Files);
public sealed record SessionCleanupProposal(int SchemaVersion, string ConfirmationId, int OlderThanDays,
    DateTimeOffset Cutoff, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string CandidateHash,
    SessionCleanupCandidate[] Candidates);
public sealed record SessionCleanupResult(string ConfirmationId, int DeletedSessions, int DeletedFiles, long DeletedBytes);

public sealed class SessionCleanupService
{
    private const string BrokerMutexName = @"Local\CubaseCompleteMcp.Broker.v1";
    private readonly string _compositionRoot;
    private readonly string _proposalRoot;
    private readonly Func<bool> _brokerActive;
    private readonly Func<DateTimeOffset> _clock;

    public SessionCleanupService(string localStateRoot, Func<bool>? brokerActive = null,
        Func<DateTimeOffset>? clock = null)
    {
        var stateRoot = Path.GetFullPath(localStateRoot);
        _compositionRoot = Path.Combine(stateRoot, "compositions");
        _proposalRoot = Path.Combine(stateRoot, "cleanup-proposals");
        _brokerActive = brokerActive ?? IsBrokerActive;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SessionCleanupProposal Preview(int olderThanDays)
    {
        ValidateDays(olderThanDays);
        EnsureBrokerInactive();
        Directory.CreateDirectory(_compositionRoot);
        Directory.CreateDirectory(_proposalRoot);
        PruneExpiredProposals();
        var now = _clock();
        var cutoff = now.AddDays(-olderThanDays);
        var candidates = ScanCandidates(cutoff);
        var confirmationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var proposal = new SessionCleanupProposal(1, confirmationId, olderThanDays, cutoff, now,
            now.AddMinutes(5), HashCandidates(candidates), candidates);
        WriteAtomic(ProposalPath(confirmationId), JsonSerializer.SerializeToUtf8Bytes(proposal, PipeProtocol.JsonOptions));
        return proposal;
    }

    public SessionCleanupResult Commit(int olderThanDays, string confirmationId)
    {
        ValidateDays(olderThanDays);
        EnsureBrokerInactive();
        ValidateConfirmationId(confirmationId);
        var proposalPath = ProposalPath(confirmationId);
        if (!File.Exists(proposalPath))
            throw new InvalidOperationException("The cleanup confirmation is unknown, expired, or already used. Create a new preview.");
        var proposal = JsonSerializer.Deserialize<SessionCleanupProposal>(File.ReadAllBytes(proposalPath), PipeProtocol.JsonOptions)
            ?? throw new InvalidDataException("The cleanup proposal is invalid.");
        if (proposal.SchemaVersion != 1 || !proposal.ConfirmationId.Equals(confirmationId, StringComparison.Ordinal)
            || proposal.OlderThanDays != olderThanDays)
            throw new InvalidOperationException("The cleanup confirmation does not match the requested conditions.");
        if (proposal.ExpiresAt < _clock())
        {
            File.Delete(proposalPath);
            throw new InvalidOperationException("The cleanup confirmation expired. Create a new preview.");
        }

        var current = ScanCandidates(proposal.Cutoff);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(proposal.CandidateHash), Encoding.ASCII.GetBytes(HashCandidates(current))))
            throw new InvalidOperationException("The cleanup candidate set changed after preview. No files were deleted; create a new preview.");

        foreach (var candidate in current) ValidateCandidatePath(candidate.Path);
        var consumed = proposalPath + ".consumed";
        File.Move(proposalPath, consumed);
        var deletedSessions = 0;
        var deletedFiles = 0;
        long deletedBytes = 0;
        try
        {
            foreach (var candidate in current)
            {
                Directory.Delete(candidate.Path, true);
                deletedSessions++;
                deletedFiles += candidate.FileCount;
                deletedBytes += candidate.Bytes;
            }
        }
        finally
        {
            if (File.Exists(consumed)) File.Delete(consumed);
        }
        return new SessionCleanupResult(confirmationId, deletedSessions, deletedFiles, deletedBytes);
    }

    private SessionCleanupCandidate[] ScanCandidates(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(_compositionRoot)) return [];
        var output = new List<SessionCleanupCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(_compositionRoot, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 24 || !name.All(Uri.IsHexDigit)) continue;
            ValidateCandidatePath(directory);
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(SnapshotFile).ToArray();
            var lastWrite = files.Select(file => file.LastWriteAt)
                .Append(new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero)).Max();
            if (lastWrite >= cutoff) continue;
            output.Add(new SessionCleanupCandidate(name, Path.GetFullPath(directory), lastWrite,
                files.Length, files.Sum(file => file.Bytes), files));
        }
        return output.ToArray();
    }

    private SessionCleanupFile SnapshotFile(string path)
    {
        EnsureNoReparsePoint(path);
        var info = new FileInfo(path);
        var relative = Path.GetRelativePath(_compositionRoot, path).Replace('\\', '/');
        return new SessionCleanupFile(relative, info.Length, info.LastWriteTimeUtc,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    }

    private void ValidateCandidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_compositionRoot, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            || relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidOperationException("A cleanup candidate escaped the composition session root.");
        EnsureNoReparsePoint(_compositionRoot);
        EnsureNoReparsePoint(full);
        foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
            EnsureNoReparsePoint(entry);
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Cleanup does not traverse reparse points: {path}");
    }

    private static string HashCandidates(IEnumerable<SessionCleanupCandidate> candidates)
    {
        var builder = new StringBuilder();
        foreach (var candidate in candidates)
        {
            builder.Append(candidate.SessionId).Append('|').Append(candidate.LastWriteAt.UtcTicks).Append('|')
                .Append(candidate.FileCount).Append('|').Append(candidate.Bytes).Append('\n');
            foreach (var file in candidate.Files)
                builder.Append(file.RelativePath).Append('|').Append(file.Bytes).Append('|')
                    .Append(file.LastWriteAt.UtcTicks).Append('|').Append(file.Sha256).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private void PruneExpiredProposals()
    {
        foreach (var path in Directory.EnumerateFiles(_proposalRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var proposal = JsonSerializer.Deserialize<SessionCleanupProposal>(File.ReadAllBytes(path), PipeProtocol.JsonOptions);
                if (proposal is null || proposal.ExpiresAt < _clock()) File.Delete(path);
            }
            catch (Exception error) when (error is IOException or JsonException) { File.Delete(path); }
        }
    }

    private string ProposalPath(string id) => Path.Combine(_proposalRoot, id + ".json");
    private void EnsureBrokerInactive()
    {
        if (_brokerActive())
            throw new InvalidOperationException("Stop Cubase Complete MCP clients and wait for the broker to exit before cleaning sessions.");
    }
    private static void ValidateDays(int value)
    {
        if (value is < 1 or > 36500) throw new ArgumentOutOfRangeException(nameof(value), "older-than-days must be between 1 and 36500.");
    }
    private static void ValidateConfirmationId(string value)
    {
        if (value.Length != 32 || !value.All(Uri.IsHexDigit)) throw new ArgumentException("confirmation-id must be a 32-character hexadecimal value.");
    }
    private static bool IsBrokerActive()
    {
        if (Mutex.TryOpenExisting(BrokerMutexName, out var mutex)) { mutex.Dispose(); return true; }
        var processes = Process.GetProcessesByName("CubaseCompleteMcp.Broker");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
