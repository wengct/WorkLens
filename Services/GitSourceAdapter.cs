using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkLens.Domain;

namespace WorkLens.Services;

public sealed class GitSourceAdapter(ProcessRunner processRunner, ActivitySourceType sourceType) : IActivitySourceAdapter
{
    private const int MaxCommitsPerRepository = 200;

    public string SourceType => sourceType.ToString();

    public SourceCapabilities Capabilities { get; } = new(
        SupportsHistory: true,
        SupportsWorkingTree: true,
        SupportsRewrites: true);

    public async Task<SourceValidationResult> ValidateAsync(
        ActivitySource source,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeGit(source.SettingsJson);
        if (settings.RepositoryPaths.Count == 0)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "至少需要設定一個 repository 路徑。");
        }

        var authorEmails = SourceSettingsSerializer.NormalizeAuthorEmails(settings.AuthorEmails);
        if (authorEmails.Count == 0)
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Git 來源至少需要設定一組自己的作者 email。");
        }

        if (authorEmails.Any(email => !SourceSettingsSerializer.IsValidAuthorEmail(email)))
        {
            return SourceValidationResult.Invalid(
                SourceHealthStatus.Error,
                "Git 來源包含格式無效的作者 email。");
        }

        if (sourceType == ActivitySourceType.MacOsGit && OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "macOS Git 來源只能在 macOS 上執行。");
        }

        if (sourceType == ActivitySourceType.WindowsGit && OperatingSystem.IsMacOS())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "Windows Git 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslGit && !OperatingSystem.IsWindows())
        {
            return SourceValidationResult.Invalid(SourceHealthStatus.Unavailable, "WSL Git 來源只能在 Windows 上執行。");
        }

        if (sourceType == ActivitySourceType.WslGit)
        {
            if (string.IsNullOrWhiteSpace(settings.Distro))
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Error,
                    "WSL Git 來源需要指定 distro。");
            }

            var wsl = await processRunner.RunAsync(
                new ProcessRequest("wsl.exe", ["--status"]),
                timeout: TimeSpan.FromSeconds(10),
                cancellationToken: cancellationToken);
            if (!wsl.Succeeded)
            {
                return SourceValidationResult.Invalid(
                    SourceHealthStatus.Unavailable,
                    "找不到可用的 WSL。",
                    FirstLine(wsl.StandardError));
            }
        }

        var git = await RunGitAsync(source, settings.RepositoryPaths[0], ["--version"], cancellationToken);
        if (!git.Succeeded)
        {
            return SourceValidationResult.Invalid(
                sourceType == ActivitySourceType.WslGit ? SourceHealthStatus.Unavailable : SourceHealthStatus.Error,
                "Git 無法執行。",
                FirstLine(git.StandardError));
        }

        var validPaths = 0;
        var details = new List<string>();
        foreach (var path in settings.RepositoryPaths)
        {
            var root = await RunGitAsync(source, path, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (root.Succeeded)
            {
                validPaths++;
                details.Add($"可讀取：{path}");
            }
            else
            {
                details.Add($"無法讀取：{path}（{FirstLine(root.StandardError)}）");
            }
        }

        return validPaths == settings.RepositoryPaths.Count
            ? SourceValidationResult.Valid($"Git 來源設定有效，共 {validPaths} 個 repository。", details.ToArray())
            : SourceValidationResult.Invalid(
                validPaths == 0 ? SourceHealthStatus.Error : SourceHealthStatus.Ready,
                $"{validPaths}/{settings.RepositoryPaths.Count} 個 repository 可讀取。",
                details.ToArray());
    }

    public async Task<CollectionBatch> CollectAsync(
        CollectionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeGit(request.Source.SettingsJson);
        var authorEmails = SourceSettingsSerializer.NormalizeAuthorEmails(settings.AuthorEmails)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batch = new CollectionBatch();
        if (authorEmails.Count == 0 || authorEmails.Any(email => !SourceSettingsSerializer.IsValidAuthorEmail(email)))
        {
            batch.Warnings.Add("Git 來源尚未設定有效的作者 email，為避免納入他人 commit，本次不會收集。");
            batch.CheckpointJson = request.Source.CheckpointJson;
            return batch;
        }

        var checkpoint = DeserializeCheckpoint(request.Source.CheckpointJson);
        var successful = 0;
        var authorCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredPath in settings.RepositoryPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var rootResult = await RunGitAsync(
                    request.Source,
                    configuredPath,
                    ["rev-parse", "--show-toplevel"],
                    cancellationToken);
                if (!rootResult.Succeeded)
                {
                    batch.Warnings.Add($"{configuredPath}：{FirstLine(rootResult.StandardError)}");
                    continue;
                }

                var root = rootResult.StandardOutput.Trim();
                var repositoryKey = RepositoryKeyNormalizer.Normalize(sourceType, settings.Distro, root);
                var branch = await GetTextAsync(request.Source, root, ["branch", "--show-current"], cancellationToken);
                var head = await GetTextAsync(request.Source, root, ["rev-parse", "HEAD"], cancellationToken);
                var rebaseActive = await IsRebaseActiveAsync(request.Source, root, cancellationToken);
                var repoCheckpoint = checkpoint.GetOrCreate(repositoryKey);
                var since = request.UpdateCheckpoint && repoCheckpoint.LastScanAt is not null
                    ? repoCheckpoint.LastScanAt.Value.AddMinutes(-2)
                    : request.Since;

                var currentHashes = await GetLinesAsync(
                    request.Source,
                    root,
                    ["rev-list", "HEAD", "--max-count=5000"],
                    cancellationToken);
                batch.CurrentCommitsByRepository[repositoryKey] = currentHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);

                var logResult = await RunGitAsync(
                    request.Source,
                    root,
                    ["log", "--all", $"--since={since.ToUniversalTime():O}", ..UntilArgument(request.Until), "--date=iso-strict",
                     "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%cI%x1f%s", "--max-count=200"],
                    cancellationToken);
                if (logResult.Succeeded)
                {
                    foreach (var commit in ParseCommitLog(logResult.StandardOutput)
                                 .Where(commit => authorEmails.Contains(commit.Email))
                                 .Take(MaxCommitsPerRepository))
                    {
                        var patchId = await GetPatchIdAsync(request.Source, root, commit.Hash, cancellationToken);
                        var commitMessage = await GetCommitMessageAsync(request.Source, root, commit.Hash, cancellationToken);
                        batch.Evidence.Add(new SourceEvidence
                        {
                            SourceId = request.Source.Id,
                            ProjectId = request.Source.ProjectId,
                            RepositoryKey = repositoryKey,
                            RepositoryPath = configuredPath,
                            Environment = sourceType.ToString(),
                            Kind = EvidenceKind.Commit,
                            ExternalKey = $"commit:{repositoryKey}:{commit.Hash}",
                            Title = commit.Subject,
                            CommitMessage = commitMessage,
                            OccurredAt = commit.CommittedAt,
                            CommitHash = commit.Hash,
                            ParentHashes = commit.Parents,
                            PatchId = patchId,
                            Branch = branch,
                            MetadataJson = JsonSerializer.Serialize(new { author = commit.Author, email = commit.Email }),
                            ReachabilityStatus = rebaseActive
                                ? CommitReachabilityStatus.Provisional
                                : currentHashes.Contains(commit.Hash, StringComparer.OrdinalIgnoreCase)
                                    ? CommitReachabilityStatus.Current
                                    : CommitReachabilityStatus.Unknown
                        });
                    }
                }
                else
                {
                    batch.Warnings.Add($"{configuredPath}：讀取 commit 失敗：{FirstLine(logResult.StandardError)}");
                }

                await CollectReflogAsync(
                    request,
                    root,
                    repositoryKey,
                    branch,
                    since,
                    authorEmails,
                    authorCache,
                    batch,
                    cancellationToken);

                if (request.UpdateCheckpoint)
                {
                    repoCheckpoint.LastScanAt = DateTimeOffset.UtcNow;
                    repoCheckpoint.LastHead = head;
                    repoCheckpoint.LastBranch = branch;
                    repoCheckpoint.RepositoryPath = configuredPath;
                }
                successful++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                batch.Warnings.Add($"{configuredPath}：{exception.Message}");
            }
        }

        batch.SuccessfulRepositories = successful;
        batch.CheckpointJson = JsonSerializer.Serialize(checkpoint);
        return batch;
    }

    private async Task CollectReflogAsync(
        CollectionRequest request,
        string root,
        string repositoryKey,
        string? branch,
        DateTimeOffset since,
        ISet<string> authorEmails,
        IDictionary<string, string?> authorCache,
        CollectionBatch batch,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            request.Source,
            root,
            ["reflog", "show", "--all", $"--since={since.ToUniversalTime():O}", ..UntilArgument(request.Until), "--date=iso-strict",
             "--format=%H%x1f%gD%x1f%gs%x1f%gI"],
            cancellationToken);
        if (!result.Succeeded)
        {
            return;
        }

        foreach (var item in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = item.TrimEnd('\r').Split('\u001f');
            if (fields.Length < 4 || !DateTimeOffset.TryParse(fields[3], out var occurredAt))
            {
                continue;
            }

            var hash = fields[0];
            if (!await HasConfiguredAuthorAsync(request.Source, root, hash, authorEmails, authorCache, cancellationToken))
            {
                continue;
            }
            var selector = fields[1];
            var subject = fields[2];
            var kind = GetReflogKind(subject);
            if (kind is null)
            {
                continue;
            }

            var externalKey = "reflog:" + HashKey($"{repositoryKey}|{hash}|{selector}|{occurredAt:O}|{subject}");
            batch.Evidence.Add(new SourceEvidence
            {
                SourceId = request.Source.Id,
                ProjectId = request.Source.ProjectId,
                RepositoryKey = repositoryKey,
                RepositoryPath = root,
                Environment = sourceType.ToString(),
                Kind = kind.Value,
                ExternalKey = externalKey,
                Title = subject,
                OccurredAt = occurredAt,
                CommitHash = hash,
                Branch = branch,
                MetadataJson = JsonSerializer.Serialize(new { reflogSelector = selector }),
                ReachabilityStatus = CommitReachabilityStatus.Unknown
            });
        }
    }

    private async Task<string?> GetPatchIdAsync(
        ActivitySource source,
        string root,
        string hash,
        CancellationToken cancellationToken)
    {
        var patch = await RunGitAsync(
            source,
            root,
            ["show", "--format=", "--no-ext-diff", hash],
            cancellationToken);
        if (!patch.Succeeded || string.IsNullOrWhiteSpace(patch.StandardOutput))
        {
            return null;
        }

        var patchId = await processRunner.RunAsync(
            BuildPatchIdRequest(source),
            patch.StandardOutput,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        return patchId.Succeeded
            ? patchId.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
    }

    private async Task<string> GetCommitMessageAsync(
        ActivitySource source,
        string root,
        string hash,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(source, root, ["show", "-s", "--format=%B", "--no-ext-diff", hash], cancellationToken);
        return result.Succeeded ? result.StandardOutput.Trim() : string.Empty;
    }

    private async Task<bool> HasConfiguredAuthorAsync(
        ActivitySource source,
        string root,
        string hash,
        ISet<string> authorEmails,
        IDictionary<string, string?> authorCache,
        CancellationToken cancellationToken)
    {
        if (!authorCache.TryGetValue(hash, out var email))
        {
            var result = await RunGitAsync(source, root, ["show", "-s", "--format=%ae", "--no-ext-diff", hash], cancellationToken);
            email = result.Succeeded ? result.StandardOutput.Trim() : null;
            authorCache[hash] = email;
        }

        return !string.IsNullOrWhiteSpace(email) && authorEmails.Contains(email);
    }

    private ProcessRequest BuildPatchIdRequest(ActivitySource source)
    {
        if (sourceType == ActivitySourceType.WslGit)
        {
            var settings = SourceSettingsSerializer.DeserializeGit(source.SettingsJson);
            return new ProcessRequest(
                "wsl.exe",
                ["-d", settings.Distro ?? string.Empty, "--", "git", "patch-id", "--stable"]);
        }

        return new ProcessRequest("git", ["patch-id", "--stable"]);
    }

    private async Task<ProcessResult> RunGitAsync(
        ActivitySource source,
        string path,
        IReadOnlyList<string> gitArguments,
        CancellationToken cancellationToken)
    {
        var settings = SourceSettingsSerializer.DeserializeGit(source.SettingsJson);
        if (sourceType == ActivitySourceType.WslGit)
        {
            var arguments = new List<string>
            {
                "-d",
                settings.Distro ?? string.Empty,
                "--",
                "git",
                "-C",
                ToWslPath(path)
            };
            arguments.AddRange(gitArguments);
            return await processRunner.RunAsync(
                new ProcessRequest("wsl.exe", arguments),
                timeout: TimeSpan.FromSeconds(45),
                cancellationToken: cancellationToken);
        }

        var nativeArguments = new List<string> { "-C", path };
        nativeArguments.AddRange(gitArguments);
        return await processRunner.RunAsync(
            new ProcessRequest("git", nativeArguments, path),
            timeout: TimeSpan.FromSeconds(45),
            cancellationToken: cancellationToken);
    }

    private async Task<string?> GetTextAsync(
        ActivitySource source,
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(source, root, arguments, cancellationToken);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<List<string>> GetLinesAsync(
        ActivitySource source,
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(source, root, arguments, cancellationToken);
        return result.Succeeded
            ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList()
            : [];
    }

    private async Task<bool> IsRebaseActiveAsync(
        ActivitySource source,
        string root,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(source, root, ["rev-parse", "--verify", "REBASE_HEAD"], cancellationToken);
        return result.Succeeded;
    }

    private static IEnumerable<CommitRecord> ParseCommitLog(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\u001f');
            if (fields.Length < 6 || !DateTimeOffset.TryParse(fields[4], out var committedAt))
            {
                continue;
            }

            yield return new CommitRecord(
                fields[0],
                fields[1],
                fields[2],
                fields[3],
                committedAt,
                fields[5]);
        }
    }

    private static EvidenceKind? GetReflogKind(string subject)
    {
        if (subject.Contains("rebase (start)", StringComparison.OrdinalIgnoreCase)) return EvidenceKind.RebaseStarted;
        if (subject.Contains("rebase (pick)", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("rebase (squash)", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("rebase (fixup)", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("rebase (drop)", StringComparison.OrdinalIgnoreCase)) return EvidenceKind.RebaseStep;
        if (subject.Contains("rebase (finish)", StringComparison.OrdinalIgnoreCase)) return EvidenceKind.RebaseFinished;
        if (subject.Contains("rebase (abort)", StringComparison.OrdinalIgnoreCase)) return EvidenceKind.RebaseAborted;
        if (subject.Contains("amend", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("reset", StringComparison.OrdinalIgnoreCase)) return EvidenceKind.RewriteDetected;
        if (subject.Contains("checkout", StringComparison.OrdinalIgnoreCase)) return EvidenceKind.BranchMoved;
        return EvidenceKind.ReflogActivity;
    }

    private static string ToWslPath(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && value[2] == '/')
        {
            return $"/mnt/{char.ToLowerInvariant(value[0])}/{value[3..]}";
        }

        return value;
    }

    private static GitCheckpointState DeserializeCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GitCheckpointState();
        }

        try
        {
            return JsonSerializer.Deserialize<GitCheckpointState>(json) ?? new GitCheckpointState();
        }
        catch (JsonException)
        {
            return new GitCheckpointState();
        }
    }

    private static string HashKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FirstLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "未知錯誤"
            : Regex.Split(value.Trim(), "\\r?\\n").FirstOrDefault() ?? "未知錯誤";

    private static string[] UntilArgument(DateTimeOffset? until) =>
        until is null ? [] : [$"--until={until.Value.ToUniversalTime():O}"];

    private sealed record CommitRecord(
        string Hash,
        string Parents,
        string Author,
        string Email,
        DateTimeOffset CommittedAt,
        string Subject);

    private sealed class GitCheckpointState
    {
        public Dictionary<string, RepositoryCheckpoint> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public RepositoryCheckpoint GetOrCreate(string key)
        {
            if (!Repositories.TryGetValue(key, out var checkpoint))
            {
                checkpoint = new RepositoryCheckpoint();
                Repositories[key] = checkpoint;
            }

            return checkpoint;
        }
    }

    private sealed class RepositoryCheckpoint
    {
        public DateTimeOffset? LastScanAt { get; set; }
        public string? LastHead { get; set; }
        public string? LastBranch { get; set; }
        public string? RepositoryPath { get; set; }
    }
}

public static class RepositoryKeyNormalizer
{
    public static string Normalize(ActivitySourceType type, string? distro, string path)
    {
        var value = path.Trim().Replace('\\', '/');
        if (value.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) && value.Length > 6)
        {
            var drive = char.ToUpperInvariant(value[5]);
            value = $"{drive}:/{value[7..]}";
        }

        value = value.TrimEnd('/').ToLowerInvariant();
        return value.Contains(":/")
            ? $"physical:{value}"
            : $"{type}:{distro?.Trim().ToLowerInvariant()}:{value}";
    }
}

public interface IActivitySourceAdapter
{
    string SourceType { get; }
    SourceCapabilities Capabilities { get; }
    Task<SourceValidationResult> ValidateAsync(ActivitySource source, CancellationToken cancellationToken);
    Task<CollectionBatch> CollectAsync(CollectionRequest request, CancellationToken cancellationToken);
}
